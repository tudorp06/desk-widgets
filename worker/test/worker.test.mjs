// Run: npm test   (in worker/; needs nothing installed)
//
// The Worker is driven through its fetch handler against a pretend GitHub (dev/pretend-github.mjs),
// so every route is exercised without a network, an account or a deployed Worker.

import { test, beforeEach } from "node:test";
import assert from "node:assert/strict";
import { generateKeyPairSync, createVerify } from "node:crypto";
import worker from "../src/index.mjs";
import { sign, verify, fromBase64 } from "../src/session.mjs";
import { appJwt } from "../src/github.mjs";
import { hub, reset, put, GALLERY_URL } from "../dev/pretend-github.mjs";
import { planUpload, blobSha } from "../src/plan.mjs";
import { pullRequestOwner, widgetProblems } from "../../tools/widget-rules.mjs";

const enc = new TextEncoder(), dec = new TextDecoder();
const keys = generateKeyPairSync("rsa", { modulusLength: 2048 });
const PAGE = "https://example.github.io/site/upload.html";
const env = {
  REPO: "owner/repo", BASE_BRANCH: "main", GALLERY_URL,
  UPLOAD_PAGES: PAGE + ",http://localhost:8000/upload.html",
  GITHUB_APP_ID: "123", GITHUB_CLIENT_ID: "Iv1.abc", GITHUB_CLIENT_SECRET: "shh",
  GITHUB_PRIVATE_KEY: keys.privateKey.export({ type: "pkcs1", format: "pem" }),
  SESSION_SECRET: "a long random secret for tests",
};

const CODE = enc.encode("using DeskWidgets;\r\n[DeskWidget(\"Hello\")]\r\npublic class Hello {}\r\n");
const PNG = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3]);

beforeEach(reset);

// ---- helpers ----

const call = (path, init) => worker.fetch(new Request("https://up.example.workers.dev" + path, init), env);
const sessionFor = (login = "Ada", id = 42) => sign({ login, id, exp: Date.now() / 1000 + 600 }, env.SESSION_SECRET);
async function upload(fields, token) {
  const form = new FormData();
  for (const [k, v] of Object.entries(fields)) {
    if (v === undefined) continue;
    if (v instanceof Uint8Array) form.set(k, new Blob([v]), k); else form.set(k, String(v));
  }
  const res = await call("/submit", { method: "POST", body: form, headers: { authorization: "Bearer " + (token || await sessionFor()), origin: "https://example.github.io" } });
  return { status: res.status, data: await res.json(), res };
}
const hello = { slug: "hello", name: "Hello", category: "utility", description: "Says hello", api_version: 1, bio: "I make widgets", code: CODE, preview: PNG };
// The files of the one commit an upload made, path -> bytes.
function committed() {
  const commit = [...hub.commits.values()].pop();
  return new Map(hub.trees.get(commit.tree).tree.map(e => [e.path, hub.blobs.get(e.sha)]));
}
function publish(folder, slug, meta, github = "Ada") {
  put(`widgets/${folder}/author.json`, JSON.stringify({ github, bio: "I make widgets" }));
  put(`widgets/${folder}/${slug}/widget.cs`, CODE);
  put(`widgets/${folder}/${slug}/preview.png`, PNG);
  put(`widgets/${folder}/${slug}/meta.json`, JSON.stringify(meta));
}
const META = { name: "Hello", category: "utility", description: "Says hello", api_version: 1, version: 1 };

// ---- tokens and keys ----

test("a session token round-trips, and fails when altered, expired or signed by another key", async () => {
  const t = await sign({ login: "Ada", id: 1, exp: 2000 }, "k");
  assert.deepEqual(await verify(t, "k", 1000), { login: "Ada", id: 1, exp: 2000 });
  assert.equal(await verify(t, "k", 3000), null);
  assert.equal(await verify(t, "other", 1000), null);
  const forged = await sign({ login: "Eve", id: 1, exp: 2000 }, "k");
  assert.equal(await verify(forged.split(".")[0] + "." + t.split(".")[1], "k", 1000), null);
  for (const junk of ["", "a", "a.b.c", ".", null]) assert.equal(await verify(junk, "k", 1000), null);
});

test("the app's JWT is signed with the key GitHub hands out (PKCS#1)", async () => {
  const jwt = await appJwt("123", env.GITHUB_PRIVATE_KEY, 1000000);
  const [head, body, sig] = jwt.split(".");
  assert.ok(createVerify("RSA-SHA256").update(head + "." + body).verify(keys.publicKey, Buffer.from(fromBase64(sig))));
  assert.deepEqual(JSON.parse(dec.decode(fromBase64(body))), { iat: 999940, exp: 1000540, iss: "123" });
  // The same key as PKCS#8, and with its line breaks flattened to \n the way some dashboards store it.
  await appJwt("123", keys.privateKey.export({ type: "pkcs8", format: "pem" }));
  await appJwt("123", env.GITHUB_PRIVATE_KEY.replace(/\n/g, "\\n"));
});

test("blobSha is git's name for the bytes", async () => {
  assert.equal(await blobSha(enc.encode("hello\n")), "ce013625030ba8dba906f756967f9e9ca394464a");
});

// ---- the rules both sides share ----

test("the rules read plain byte arrays, not only Node buffers", () => {
  const meta = { ...META };
  assert.deepEqual(widgetProblems({ author: "ada", slug: "hello", meta, code: CODE, preview: PNG }), []);
  assert.match(widgetProblems({ author: "ada", slug: "hello", meta, code: CODE, preview: new Uint8Array([1, 2, 3]) })[0], /not a PNG/);
});

test("a pull request is the uploader's only when the bot opened it from a branch in this repository", () => {
  const bot = "desk-upload[bot]", pr = { author: bot, headRef: "upload/ada/hello", headRepo: "o/r", baseRepo: "o/r", bot };
  assert.equal(pullRequestOwner(pr), "ada");
  assert.equal(pullRequestOwner({ ...pr, author: "Mallory" }), "mallory");          // an ordinary pull request
  assert.equal(pullRequestOwner({ ...pr, headRepo: "mallory/r" }), "");              // the bot, but from a fork
  assert.equal(pullRequestOwner({ ...pr, headRef: "upload/Ada/hello" }), "");        // not a branch the Worker makes
  assert.equal(pullRequestOwner({ ...pr, headRef: "upload/ada/hello/x" }), "");
  assert.equal(pullRequestOwner({ ...pr, bot: "" }), bot);                           // no app registered yet
});

// ---- signing in ----

test("signing in goes to GitHub and comes back to the upload page with a session", async () => {
  const start = await call("/login?return=" + encodeURIComponent("http://localhost:8000/upload.html"));
  assert.equal(start.status, 302);
  const to = new URL(start.headers.get("location"));
  assert.equal(to.origin + to.pathname, "https://github.com/login/oauth/authorize");
  assert.equal(to.searchParams.get("client_id"), "Iv1.abc");
  assert.equal(to.searchParams.get("scope"), null);
  const state = to.searchParams.get("state"), cookie = start.headers.get("set-cookie").split(";")[0];
  assert.equal(cookie, "dw_state=" + state);

  const back = await call(`/callback?code=good&state=${state}`, { headers: { cookie } });
  const landed = back.headers.get("location");
  assert.ok(landed.startsWith("http://localhost:8000/upload.html#session="));
  const s = await verify(landed.split("#session=")[1], env.SESSION_SECRET);
  assert.equal(s.login, "Ada"); assert.equal(s.id, 42);

  // Without the cookie the same link does nothing: it was not this browser that started it.
  const stolen = await call(`/callback?code=good&state=${state}`);
  assert.ok(stolen.headers.get("location").endsWith("#error=signin"));
  const refused = await call(`/callback?code=bad&state=${state}`, { headers: { cookie } });
  assert.ok(refused.headers.get("location").endsWith("#error=signin"));
});

test("sign-in never returns to a page that is not on the list", async () => {
  const start = await call("/login?return=" + encodeURIComponent("https://evil.example/upload.html"));
  const state = new URL(start.headers.get("location")).searchParams.get("state");
  const back = await call(`/callback?code=good&state=${state}`, { headers: { cookie: "dw_state=" + state } });
  assert.ok(back.headers.get("location").startsWith(PAGE + "#session="));
});

test("only the upload pages may read answers, and nothing works without a session", async () => {
  const ok = await call("/me", { headers: { origin: "https://example.github.io" } });
  assert.equal(ok.status, 401);
  assert.equal(ok.headers.get("access-control-allow-origin"), "https://example.github.io");
  const other = await call("/me", { headers: { origin: "https://evil.example" } });
  assert.equal(other.headers.get("access-control-allow-origin"), null);
  assert.equal((await upload(hello, "not.a-session")).status, 401);
  assert.equal(hub.calls.length, 0);
});

// ---- uploading ----

test("a first upload opens a pull request with the widget and the author's file", async () => {
  const { status, data } = await upload(hello);
  assert.equal(status, 200, JSON.stringify(data));
  assert.deepEqual(data, { url: "https://github.com/owner/repo/pull/1", updated: false, version: 1 });

  const files = committed();
  assert.deepEqual([...files.keys()].sort(), ["widgets/ada/author.json", "widgets/ada/hello/meta.json", "widgets/ada/hello/preview.png", "widgets/ada/hello/widget.cs"]);
  assert.deepEqual([...files.get("widgets/ada/hello/widget.cs")], [...CODE], "the source is committed byte for byte");
  assert.deepEqual(JSON.parse(dec.decode(files.get("widgets/ada/hello/meta.json"))), META);
  assert.deepEqual(JSON.parse(dec.decode(files.get("widgets/ada/author.json"))), { github: "Ada", bio: "I make widgets" });

  const commit = [...hub.commits.values()][0];
  assert.deepEqual(commit.parents, ["mainsha"]);
  assert.deepEqual(commit.author, { name: "Ada", email: "42+Ada@users.noreply.github.com" });
  assert.equal(hub.trees.get(commit.tree).base_tree, "maintree");
  assert.equal(hub.refs.get("upload/ada/hello"), "commit0");

  const pr = hub.pulls[0];
  assert.equal(pr.title, "Add Hello"); assert.equal(pr.base, "main"); assert.equal(pr.head.ref, "upload/ada/hello");
  assert.match(pr.body, /^@Ada sent this through the upload page: a new widget\./);
  // What the pull request check will conclude from it.
  assert.equal(pullRequestOwner({ author: "bot[bot]", headRef: pr.head.ref, headRepo: "owner/repo", baseRepo: "owner/repo", bot: "bot[bot]" }), "ada");
});

test("problems come back as sentences and nothing is written", async () => {
  const { status, data } = await upload({ ...hello, name: "", category: "nope", code: enc.encode("class X {}"), preview: new Uint8Array([1, 2, 3]) });
  assert.equal(status, 400);
  assert.deepEqual(data.problems, [
    "name must be 1 to 40 characters",
    "category must be one of clocks, productivity, weather, system, media, developer, sports, finance, news, utility, social, health, travel, games",
    "widget.cs must have exactly one class marked [DeskWidget]",
    "preview.png is not a PNG",
  ]);
  assert.equal((await upload({ ...hello, slug: "../x" })).status, 400);
  const bare = await upload({ slug: "hello", name: "Hello", category: "utility", description: "", api_version: 1 });
  assert.deepEqual(bare.data.problems, ["choose the widget's source file", "add a preview picture: it is what people see in the gallery"]);
  assert.equal(hub.commits.size, 0); assert.equal(hub.pulls.length, 0);
});

test("changed source raises the version by one; the picture on main is kept", async () => {
  publish("ada", "hello", META);
  const code2 = enc.encode(dec.decode(CODE) + "// more\r\n");
  const { data } = await upload({ ...hello, code: code2, preview: undefined, bio: undefined });
  assert.equal(data.version, 2);
  const files = committed();
  assert.deepEqual([...files.keys()].sort(), ["widgets/ada/hello/meta.json", "widgets/ada/hello/widget.cs"]);
  assert.equal(JSON.parse(dec.decode(files.get("widgets/ada/hello/meta.json"))).version, 2);
  assert.equal(hub.pulls[0].title, "Update Hello to version 2");
});

test("the same source with a new description keeps the version", async () => {
  publish("ada", "hello", META);
  const { data } = await upload({ ...hello, description: "Says hello, by time of day" });
  assert.equal(data.version, 1);
  assert.deepEqual([...committed().keys()], ["widgets/ada/hello/meta.json"]);
  // And with no source sent at all.
  reset(); publish("ada", "hello", META);
  const again = await upload({ ...hello, code: undefined, preview: undefined, name: "Hello there" });
  assert.equal(again.data.version, 1);
  assert.equal(hub.pulls[0].title, "Update the details of Hello there");
});

test("an upload that changes nothing is refused", async () => {
  publish("ada", "hello", META);
  const { status, data } = await upload(hello);
  assert.equal(status, 400);
  assert.deepEqual(data.problems, ["nothing here differs from what is already in the gallery"]);
});

test("sending the form again updates the open pull request instead of opening another", async () => {
  await upload(hello);
  const { data } = await upload({ ...hello, description: "Second try" });
  assert.deepEqual(data, { url: "https://github.com/owner/repo/pull/1", updated: true, version: 1 });
  assert.equal(hub.pulls.length, 1);
  assert.equal(hub.refs.get("upload/ada/hello"), "commit1");
  assert.deepEqual(hub.commits.get("commit1").parents, ["mainsha"], "the new commit replaces the old one, on top of main");
});

test("an author from the old gallery uploads into the folder that is already theirs", async () => {
  publish("old-name", "hello", META, "ADA");
  hub.gallery.authors.push({ username: "old-name", github: "ADA", bio: "" });
  const { data } = await upload({ ...hello, slug: "second" });
  assert.equal(data.version, 1);
  const files = committed();
  assert.ok(files.has("widgets/old-name/second/widget.cs"));
  // github in author.json must not change, or the pull request check refuses it.
  assert.ok(!files.has("widgets/old-name/author.json"));
  assert.equal(hub.pulls[0].head.ref, "upload/ada/second");
});

test("the gallery alone does not decide whose folder it is", async () => {
  // The published gallery says the folder is Ada's; main says it is Bob's. main wins.
  publish("bob", "hello", META, "Bob");
  hub.gallery.authors.push({ username: "bob", github: "Ada", bio: "" });
  await upload(hello);
  assert.ok(committed().has("widgets/ada/hello/widget.cs"));
});

test("a folder with the person's username that belongs to someone else stops the upload", async () => {
  publish("ada", "hello", META, "SomeoneElse");
  const { status, data } = await upload({ ...hello, slug: "mine" });
  assert.equal(status, 409);
  assert.match(data.error, /belongs to another account/);
  assert.equal(hub.commits.size, 0);
});

test("at most three uploads wait for review at a time", async () => {
  for (const slug of ["a", "b", "c"]) assert.equal((await upload({ ...hello, slug })).status, 200);
  const fourth = await upload({ ...hello, slug: "d" });
  assert.equal(fourth.status, 429);
  assert.equal(hub.pulls.length, 3);
  assert.equal((await upload({ ...hello, slug: "b", description: "again" })).status, 200, "one already open can still be updated");
  // Someone else's uploads do not count against them.
  assert.equal((await upload({ ...hello, slug: "d" }, await sessionFor("Grace", 9))).status, 200);
});

test("text typed into the form cannot mention people or add markup in the pull request", async () => {
  await upload({ ...hello, name: "Hi @torvalds `x`", description: "<img src=x> [a](b) @someone" });
  const pr = hub.pulls[0];
  assert.equal(pr.title, "Add Hi  torvalds  x");
  assert.equal((pr.body.match(/@/g) || []).length, 1, "only the uploader is mentioned");
  assert.ok(!/[<\[]/.test(pr.body));
});

test("/me lists the person's folder, widgets and waiting uploads", async () => {
  publish("ada", "hello", META);
  hub.gallery.widgets.push({ id: "u.ada.hello", author: "ada", ...META }, { id: "u.bob.x", author: "bob", ...META });
  await upload({ ...hello, slug: "second" });
  const res = await call("/me", { headers: { authorization: "Bearer " + await sessionFor() } });
  assert.deepEqual(await res.json(), {
    login: "Ada", folder: "ada", bio: "I make widgets",
    widgets: [{ slug: "hello", ...META }],
    open: [{ slug: "second", url: "https://github.com/owner/repo/pull/1" }],
  });
});

test("a failure at GitHub is reported without detail", async () => {
  const real = globalThis.fetch;
  globalThis.fetch = async (input, init) => String(input).includes("/git/trees") ? new Response("{}", { status: 500 }) : real(input, init);
  try {
    const { status, data } = await upload(hello);
    assert.equal(status, 502);
    assert.match(data.error, /^GitHub did not answer as expected/);
    assert.equal(hub.pulls.length, 0);
  } finally { globalThis.fetch = real; }
});
