// Tries the upload page end to end on this PC, with no GitHub App, no Cloudflare account and
// no network: the real Worker code, answering on http://localhost:8787, against the pretend
// GitHub beside this file. Signing in succeeds at once, as "Ada".
//
//   node worker/dev/server.mjs            (then, in docs/config.js: UPLOAD_URL = "http://localhost:8787")
//   node tools/build-widget-pages.mjs
//   python -m http.server -d docs 8000    and open http://localhost:8000/upload.html
//
// Each upload is printed here instead of becoming a pull request. Never deployed.

import { createServer } from "node:http";
import { hub, put, GALLERY_URL } from "./pretend-github.mjs";
import worker from "../src/index.mjs";

const PORT = 8787, PAGE = "http://localhost:8000/upload.html";
const env = {
  REPO: "owner/repo", BASE_BRANCH: "main", GALLERY_URL, UPLOAD_PAGES: PAGE,
  GITHUB_APP_ID: "1", GITHUB_CLIENT_ID: "dev", GITHUB_CLIENT_SECRET: "shh", SESSION_SECRET: "dev only",
  GITHUB_PRIVATE_KEY: (await import("node:crypto")).generateKeyPairSync("rsa", { modulusLength: 2048 }).privateKey.export({ type: "pkcs1", format: "pem" }),
};

// Ada already has one widget in the gallery, so updating can be tried as well as adding.
const meta = { name: "Hello", category: "utility", description: "Says hello", api_version: 1, version: 1 };
put("widgets/ada/author.json", JSON.stringify({ github: "Ada", bio: "I make widgets" }));
put("widgets/ada/hello/meta.json", JSON.stringify(meta));
put("widgets/ada/hello/widget.cs", "[DeskWidget]");
put("widgets/ada/hello/preview.png", new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]));
hub.gallery = { widgets: [{ id: "u.ada.hello", author: "ada", ...meta }], authors: [{ username: "ada", github: "Ada", bio: "I make widgets" }] };

createServer(async (req, res) => {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  const headers = new Headers();
  for (const [k, v] of Object.entries(req.headers)) if (typeof v === "string") headers.set(k, v);
  const request = new Request(`http://localhost:${PORT}${req.url}`, {
    method: req.method, headers, body: chunks.length ? Buffer.concat(chunks) : undefined,
  });
  let out = await worker.fetch(request, env);
  // Where the real Worker sends the browser to GitHub's sign-in page, come straight back signed in.
  const to = out.headers.get("location") || "";
  if (to.startsWith("https://github.com/login/oauth/authorize")) {
    const state = new URL(to).searchParams.get("state");
    out = await worker.fetch(new Request(`http://localhost:${PORT}/callback?code=good&state=${state}`, { headers: { cookie: "dw_state=" + state } }), env);
  }
  if (req.method === "POST" && out.status === 200) {
    const commit = [...hub.commits.values()].pop(), pr = hub.pulls[hub.pulls.length - 1];
    console.log(`\n${pr.title}  (${pr.head.ref})\n${pr.body}\n` +
      hub.trees.get(commit.tree).tree.map(e => `  ${e.path}  ${hub.blobs.get(e.sha).length} bytes`).join("\n"));
  }
  res.writeHead(out.status, Object.fromEntries(out.headers));
  res.end(Buffer.from(await out.arrayBuffer()));
}).listen(PORT, () => console.log(`The upload Worker, against a pretend GitHub, on http://localhost:${PORT}`));
