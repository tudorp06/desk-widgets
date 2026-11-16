// A pretend GitHub, in memory: just enough of its API for the Worker to sign someone in, read
// main, commit an upload and open a pull request. Importing this file replaces fetch, so
// nothing here or in the Worker reaches the network. Used by the tests and by server.mjs beside it.
//
// It expects the repository "owner/repo", the client secret "shh", and the sign-in code "good".

import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { fromBase64 } from "../src/session.mjs";
import { forgetToken } from "../src/github.mjs";

export const GALLERY_URL = "https://example.github.io/site/w/gallery.json";
const enc = new TextEncoder(), dec = new TextDecoder();
const gitSha = bytes => createHash("sha1").update(`blob ${bytes.length}\0`).update(bytes).digest("hex");

export let hub;
export function reset() {
  forgetToken();
  hub = {
    files: new Map(),          // what is on main: path -> bytes
    gallery: { widgets: [], authors: [] },
    pulls: [], refs: new Map(), blobs: new Map(), trees: new Map(), commits: new Map(),
    user: { login: "Ada", id: 42 },
    calls: [],
  };
}
export const put = (path, value) => hub.files.set(path, typeof value === "string" ? enc.encode(value) : value);
const reply = (data, status = 200) => new Response(typeof data === "string" ? data : JSON.stringify(data), { status });

globalThis.fetch = async (input, init = {}) => {
  const url = new URL(typeof input === "string" ? input : input.url), method = init.method || "GET";
  const body = init.body ? JSON.parse(init.body) : null;
  const path = url.pathname;
  hub.calls.push(`${method} ${url.host}${path}`);
  if (url.host === "github.com" && path === "/login/oauth/access_token")
    return reply(body.code === "good" && body.client_secret === "shh" ? { access_token: "user-token" } : { error: "bad_verification_code" });
  if (url.href === GALLERY_URL) return reply(hub.gallery);
  assert.equal(url.host, "api.github.com", "unexpected request to " + url.href);
  const auth = init.headers.authorization;
  if (path === "/user") return auth === "Bearer user-token" ? reply(hub.user) : reply({}, 401);
  if (path === "/repos/owner/repo/installation") return reply({ id: 7 });
  if (path === "/app/installations/7/access_tokens") {
    assert.deepEqual(body, { repositories: ["repo"], permissions: { contents: "write", pull_requests: "write" } });
    return reply({ token: "inst-token", expires_at: new Date(Date.now() + 3600e3).toISOString() }, 201);
  }
  assert.equal(auth, "Bearer inst-token");
  const r = "/repos/owner/repo";
  if (path === r + "/commits/main") return reply({ sha: "mainsha", commit: { tree: { sha: "maintree" } } });
  if (path.startsWith(r + "/contents/")) {
    assert.equal(url.searchParams.get("ref"), "mainsha");
    const at = path.slice((r + "/contents/").length);
    if (hub.files.has(at)) return reply(dec.decode(hub.files.get(at)));
    const inside = [...hub.files.keys()].filter(k => k.startsWith(at + "/") && !k.slice(at.length + 1).includes("/"));
    return inside.length ? reply(inside.map(k => ({ name: k.slice(at.length + 1), type: "file", sha: gitSha(hub.files.get(k)) }))) : reply({}, 404);
  }
  if (path === r + "/pulls" && method === "GET") return reply(hub.pulls.filter(p => p.state === "open"));
  if (path === r + "/pulls" && method === "POST") {
    const pr = { number: hub.pulls.length + 1, state: "open", title: body.title, body: body.body, base: body.base,
      html_url: `https://github.com/owner/repo/pull/${hub.pulls.length + 1}`, head: { ref: body.head, repo: { full_name: "owner/repo" } } };
    hub.pulls.push(pr);
    return reply(pr, 201);
  }
  const edit = /\/pulls\/(\d+)$/.exec(path);
  if (edit && method === "PATCH") { Object.assign(hub.pulls[edit[1] - 1], body); return reply(hub.pulls[edit[1] - 1]); }
  if (path === r + "/git/blobs") {
    const bytes = fromBase64(body.content), sha = gitSha(bytes);
    hub.blobs.set(sha, bytes);
    return reply({ sha }, 201);
  }
  if (path === r + "/git/trees") { const sha = "tree" + hub.trees.size; hub.trees.set(sha, body); return reply({ sha }, 201); }
  if (path === r + "/git/commits") { const sha = "commit" + hub.commits.size; hub.commits.set(sha, body); return reply({ sha }, 201); }
  if (path.startsWith(r + "/git/refs/heads/") && method === "PATCH") {
    const name = path.slice((r + "/git/refs/heads/").length);
    if (!hub.refs.has(name)) return reply({ message: "Reference does not exist" }, 422);
    hub.refs.set(name, body.sha);
    return reply({});
  }
  if (path === r + "/git/refs" && method === "POST") { hub.refs.set(body.ref.replace("refs/heads/", ""), body.sha); return reply({}, 201); }
  throw new Error(`the pretend GitHub has no ${method} ${path}`);
};

reset();
