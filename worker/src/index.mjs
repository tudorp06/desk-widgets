// The upload Worker: lets someone publish a widget from a form on the website instead of by
// hand-made pull request. It signs them in with GitHub to learn who they are, then opens the
// pull request for them, as the upload app, from a branch named upload/<them>/<widget>.
//
// It only ever proposes. Merging is still the review, main is still protected, and the same
// checks run on its pull requests as on anyone's (.github/workflows/widgets.yml). It stores
// nothing: no database, no log of who uploaded what beyond the pull request itself.
//
//   GET  /login      to GitHub's sign-in page
//   GET  /callback   back from GitHub; on to the upload page with a session token
//   GET  /me         the signed-in person's folder, widgets and open uploads
//   POST /submit     check the upload, commit it, open or update the pull request
//
// Settings are in wrangler.toml; the three secrets are named in worker/README.md.

import { sign, verify, base64 } from "./session.mjs";
import { github, installationToken, whoSignedIn, GitHubError } from "./github.mjs";
import { planUpload } from "./plan.mjs";
import { parseJson, PART, MAX_SOURCE, MAX_PREVIEW, WIDGET_FILES } from "../../tools/widget-rules.mjs";

const SESSION_SECONDS = 3600, STATE_SECONDS = 600;
const MAX_OPEN_UPLOADS = 3;
const MAX_BODY = MAX_SOURCE + MAX_PREVIEW + 65536;

// The pages allowed to use this Worker: the upload page, and a local copy while developing.
const pages = env => String(env.UPLOAD_PAGES || "").split(",").map(s => s.trim()).filter(Boolean);

function cors(request, env) {
  const origin = request.headers.get("origin");
  const allowed = origin && pages(env).some(p => new URL(p).origin === origin);
  return allowed ? {
    "access-control-allow-origin": origin,
    "access-control-allow-headers": "authorization",
    "access-control-allow-methods": "GET, POST",
    "access-control-max-age": "86400",
    "vary": "origin",
  } : {};
}

const answer = (data, status, headers) => new Response(JSON.stringify(data), {
  status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store", ...headers },
});
const redirect = (to, headers) => new Response(null, { status: 302, headers: { "location": to, "cache-control": "no-store", ...headers } });

async function session(request, env) {
  const m = /^Bearer (.+)$/.exec(request.headers.get("authorization") || "");
  const s = m ? await verify(m[1], env.SESSION_SECRET) : null;
  return s && s.login && s.id ? s : null;
}

// ---- signing in ----

async function login(url, env) {
  const all = pages(env), want = url.searchParams.get("return");
  const back = all.includes(want) ? want : all[0];
  // The same value goes to GitHub and into a cookie; the callback requires both, so a link
  // someone else made cannot finish a sign-in in this browser.
  const state = await sign({ back, nonce: crypto.randomUUID(), exp: Date.now() / 1000 + STATE_SECONDS }, env.SESSION_SECRET);
  const to = new URL("https://github.com/login/oauth/authorize");
  to.searchParams.set("client_id", env.GITHUB_CLIENT_ID);
  to.searchParams.set("redirect_uri", url.origin + "/callback");
  to.searchParams.set("state", state);
  return redirect(to.href, { "set-cookie": `dw_state=${state}; Max-Age=${STATE_SECONDS}; Path=/callback; HttpOnly; Secure; SameSite=Lax` });
}

async function callback(request, url, env) {
  const clear = { "set-cookie": "dw_state=; Max-Age=0; Path=/callback; HttpOnly; Secure; SameSite=Lax" };
  const state = url.searchParams.get("state") || "";
  const cookie = /(?:^|;\s*)dw_state=([^;]+)/.exec(request.headers.get("cookie") || "");
  const known = cookie && cookie[1] === state ? await verify(state, env.SESSION_SECRET) : null;
  const back = known && pages(env).includes(known.back) ? known.back : pages(env)[0];
  const code = url.searchParams.get("code");
  if (!known || !code) return redirect(back + "#error=signin", clear);   // also: the person pressed Cancel
  const who = await whoSignedIn(env, code, url.origin + "/callback");
  if (!who) return redirect(back + "#error=signin", clear);
  const token = await sign({ login: who.login, id: who.id, exp: Date.now() / 1000 + SESSION_SECONDS }, env.SESSION_SECRET);
  // In the fragment, which browsers do not send to any server or put in a Referer.
  return redirect(back + "#session=" + token, clear);
}

// ---- what is on main ----

// A file's text at a commit, or null when it is not there.
async function textAt(token, env, sha, path) {
  const r = await github(token, "GET", `/repos/${env.REPO}/contents/${path}?ref=${sha}`, { raw: true, allow: [404] });
  return r.status === 200 ? r.data : null;
}

async function gallery(env) {
  try {
    const res = await fetch(env.GALLERY_URL, { headers: { "user-agent": "desk-widgets-upload" } });
    const data = res.ok ? await res.json() : null;
    return data && Array.isArray(data.widgets) && Array.isArray(data.authors) ? data : { widgets: [], authors: [] };
  } catch (e) { return { widgets: [], authors: [] }; }
}

// The folder in widgets/ that belongs to this account. Usually it is their username, but an
// author brought over from the old gallery may have another (widgets/saif is @whoissaaif), and
// those are found through the published gallery. { folder, author } with author null for
// someone new, or { taken } when the folder with their name is someone else's.
async function folderOf(token, env, sha, login, published) {
  const lower = login.toLowerCase();
  const read = async folder => parseJson(await textAt(token, env, sha, `widgets/${folder}/author.json`) || "");
  const mine = a => a && String(a.github || "").toLowerCase() === lower;
  const own = await read(lower);
  if (mine(own)) return { folder: lower, author: own };
  for (const a of published.authors) {
    if (!mine(a) || !PART.test(String(a.username))) continue;
    const there = await read(a.username);
    if (mine(there)) return { folder: a.username, author: there };
  }
  return own ? { taken: true } : { folder: lower, author: null };
}

const openUploads = async (token, env, login) =>
  (await github(token, "GET", `/repos/${env.REPO}/pulls?state=open&per_page=100`)).data
    .filter(p => p.head.repo && p.head.repo.full_name === env.REPO && p.head.ref.startsWith(`upload/${login.toLowerCase()}/`));

const TAKEN = "A folder with your GitHub username already exists in the gallery and belongs to another account. Open an issue and a maintainer will sort it out.";

async function me(request, env) {
  const who = await session(request, env);
  if (!who) return answer({ error: "Sign in again." }, 401);
  const token = await installationToken(env);
  const head = (await github(token, "GET", `/repos/${env.REPO}/commits/${env.BASE_BRANCH}`)).data;
  const published = await gallery(env);
  const mine = await folderOf(token, env, head.sha, who.login, published);
  if (mine.taken) return answer({ login: who.login, error: TAKEN }, 409);
  const open = await openUploads(token, env, who.login);
  return answer({
    login: who.login,
    folder: mine.folder,
    bio: mine.author ? mine.author.bio : "",
    widgets: published.widgets.filter(w => w.author === mine.folder).map(w => ({
      slug: w.id.split(".")[2], name: w.name, category: w.category, description: w.description,
      api_version: w.api_version, version: w.version,
    })),
    open: open.map(p => ({ slug: p.head.ref.split("/")[2], url: p.html_url })),
  }, 200);
}

// ---- uploading ----

async function submit(request, env) {
  const who = await session(request, env);
  if (!who) return answer({ error: "Sign in again." }, 401);
  if (Number(request.headers.get("content-length") || 0) > MAX_BODY) return answer({ error: "That upload is too large." }, 413);
  let form;
  try { form = await request.formData(); } catch (e) { return answer({ error: "The upload could not be read." }, 400); }
  const bytes = async name => {
    const f = form.get(name);
    if (!f || typeof f === "string" || !f.size) return null;
    if (f.size > MAX_BODY) return new Uint8Array(MAX_BODY);   // too large either way; the rules say so
    return new Uint8Array(await f.arrayBuffer());
  };
  const text = name => { const v = form.get(name); return typeof v === "string" ? v : ""; };
  const slug = text("slug").trim();
  if (!PART.test(slug) || slug.length > 36)
    return answer({ problems: ["the widget's short name must be lowercase letters, digits and hyphens, at most 36 characters"] }, 400);

  const token = await installationToken(env);
  const head = (await github(token, "GET", `/repos/${env.REPO}/commits/${env.BASE_BRANCH}`)).data;
  const mine = await folderOf(token, env, head.sha, who.login, await gallery(env));
  if (mine.taken) return answer({ error: TAKEN }, 409);

  // The widget as it is on main, if it is there.
  const dir = `widgets/${mine.folder}/${slug}`;
  const listing = await github(token, "GET", `/repos/${env.REPO}/contents/${dir}?ref=${head.sha}`, { allow: [404] });
  let live = null;
  if (listing.status === 200 && Array.isArray(listing.data)) {
    const sha = name => { const f = listing.data.find(e => e.name === name && e.type === "file"); return f ? f.sha : null; };
    const meta = parseJson(await textAt(token, env, head.sha, `${dir}/meta.json`) || "");
    if (meta && Number.isInteger(meta.version) && sha(WIDGET_FILES[0])) live = { meta, codeSha: sha("widget.cs"), previewSha: sha("preview.png") };
  }

  const plan = await planUpload({
    who, folder: mine.folder, author: mine.author, live,
    form: {
      slug, name: text("name"), category: text("category"), description: text("description"),
      api_version: text("api_version"), bio: form.has("bio") ? text("bio") : null,
      code: await bytes("code"), preview: await bytes("preview"),
    },
  });
  if (plan.problems.length) return answer({ problems: plan.problems }, 400);

  const open = await openUploads(token, env, who.login);
  const existing = open.find(p => p.head.ref === plan.branch);
  if (!existing && open.length >= MAX_OPEN_UPLOADS)
    return answer({ error: `You have ${open.length} uploads waiting for review, which is the most at one time. Another can be sent once one of them is merged or closed.` }, 429);

  // One commit on top of main holding only the changed files, on the upload's own branch.
  const repo = `/repos/${env.REPO}`;
  const tree = [];
  for (const f of plan.files) {
    const blob = await github(token, "POST", `${repo}/git/blobs`, { body: { content: base64(f.bytes), encoding: "base64" } });
    tree.push({ path: f.path, mode: "100644", type: "blob", sha: blob.data.sha });
  }
  const made = await github(token, "POST", `${repo}/git/trees`, { body: { base_tree: head.commit.tree.sha, tree } });
  const commit = await github(token, "POST", `${repo}/git/commits`, {
    body: { message: plan.message, tree: made.data.sha, parents: [head.sha], author: plan.committer },
  });
  // The branch may be left over from an earlier upload of the same widget; it is replaced, so
  // sending the form again while a pull request is open updates that pull request.
  const ref = await github(token, "PATCH", `${repo}/git/refs/heads/${plan.branch}`, { body: { sha: commit.data.sha, force: true }, allow: [404, 422] });
  if (ref.status !== 200) await github(token, "POST", `${repo}/git/refs`, { body: { ref: `refs/heads/${plan.branch}`, sha: commit.data.sha } });

  if (existing) {
    await github(token, "PATCH", `${repo}/pulls/${existing.number}`, { body: { title: plan.title, body: plan.body } });
    return answer({ url: existing.html_url, updated: true, version: plan.version }, 200);
  }
  const pr = await github(token, "POST", `${repo}/pulls`, { body: { title: plan.title, head: plan.branch, base: env.BASE_BRANCH, body: plan.body } });
  return answer({ url: pr.data.html_url, updated: false, version: plan.version }, 200);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url), route = `${request.method} ${url.pathname}`;
    const headers = cors(request, env);
    try {
      if (request.method === "OPTIONS") return new Response(null, { status: 204, headers });
      if (route === "GET /login") return await login(url, env);
      if (route === "GET /callback") return await callback(request, url, env);
      let res = null;
      if (route === "GET /me") res = await me(request, env);
      if (route === "POST /submit") res = await submit(request, env);
      if (!res) return answer({ error: "Not found." }, 404, headers);
      for (const k in headers) res.headers.set(k, headers[k]);
      return res;
    } catch (e) {
      console.error(e && e.stack || e);
      const message = e instanceof GitHubError
        ? "GitHub did not answer as expected. Nothing was published; try again in a minute."
        : "Something went wrong on our side. Nothing was published.";
      return answer({ error: message }, 502, headers);
    }
  },
};
