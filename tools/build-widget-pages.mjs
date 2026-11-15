// Generates one shareable page per community widget: docs/w/<id>/index.html plus its preview.png,
// and docs/w/previews.json, the list of which version each preview.png was exported from.
//
// Why these exist: docs/widget.html is a single file that reads ?id= in the browser, so every
// widget link unfurls with the same title and the same picture. A link to the F1 widget posted in
// an F1 Discord shows nothing about F1. Crawlers do not run the page's JavaScript, so the only
// fix is a real file per widget with its own <title>, description and og:image.
//
// The pages are not a second copy of the UI. Each one is docs/widget.html with four mechanical
// edits, so a change to widget.html reaches every generated page the next time this runs:
//   1. <base href="../../"> so the shared nav, footer and profile links still resolve
//   2. the real title and the social meta tags
//   3. the widget id baked in, instead of reading the query string
//   4. the one fragment link fixed, because <base> would send it to the site root
//
// The preview files are also what the website shows (docs/assets/data.js). Previews live in the
// database as base64, and reading them through its API on every visit is what used up the
// project's egress quota; a file on GitHub Pages costs the database nothing and is cached by
// browsers. The site reads previews.json and asks the database only for a version that has no
// file yet. An approved version never changes, so this script reads the small list first and
// downloads a preview only when previews.json does not already hold that version: a run where
// nothing was approved costs a few kilobytes. Keep it that way; it runs on a schedule
// (.github/workflows/pages.yml).
//
// Run: node tools/build-widget-pages.mjs        (add --check to fail instead of writing)
//
// Reads the gallery with the publishable key, exactly as a visitor's browser does.

import { readFile, writeFile, appendFile, mkdir, rm, readdir } from "fs/promises";
import { existsSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

const here = dirname(fileURLToPath(import.meta.url));
const docs = join(here, "..", "docs");
const outRoot = join(docs, "w");
const manifestPath = join(outRoot, "previews.json");

const { SUPABASE_URL, SUPABASE_ANON_KEY, REPO_URL } = await import(
  "file://" + join(docs, "config.js").replace(/\\/g, "/"));

// github.com/<owner>/<repo> -> <owner>.github.io/<repo>, the Pages URL og: tags must be absolute for.
const repo = REPO_URL.replace(/\/+$/, "").split("/");
const SITE = `https://${repo[repo.length - 2]}.github.io/${repo[repo.length - 1]}`;

// Same rule as isValidId in docs/assets/site.js. Kept here rather than imported because site.js
// touches document at module load. It also means no id can escape docs/w/ as a path.
const ID_PATTERN = /^u\.[a-z0-9-]+\.[a-z0-9-]+$/;

const esc = s => String(s ?? "")
  .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
  .replace(/"/g, "&quot;").replace(/'/g, "&#39;");

async function rest(query) {
  const res = await fetch(`${SUPABASE_URL}/rest/v1/${query}`, { headers: { apikey: SUPABASE_ANON_KEY } });
  if (!res.ok) throw new Error(`gallery read failed: ${res.status} ${res.statusText}`);
  return res.json();
}

// The list, without previews: this is the only request a run makes when nothing is new.
const gallery = () => rest("explore?select=id,name,category,description,version,author");

// id -> { version, png } for these widgets. png is null for a widget with no usable preview.
// The version comes back with the picture, so a widget approved between the two requests is
// recorded as the version its picture really belongs to.
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
async function previewsOf(ids) {
  const out = new Map();
  for (let at = 0; at < ids.length; at += 20) {
    const rows = await rest(`explore?select=id,version,preview&id=in.(${ids.slice(at, at + 20).join(",")})`);
    for (const row of rows) {
      const ok = typeof row.preview === "string" && row.preview.length <= 140000 && /^[A-Za-z0-9+/=]+$/.test(row.preview);
      const png = ok ? Buffer.from(row.preview, "base64") : null;
      out.set(row.id, { version: row.version,
        png: png && png.subarray(0, 8).equals(PNG_SIGNATURE) ? png : null });
    }
  }
  return out;
}

function pageFor(template, w, hasPreview) {
  const title = `${w.name} · Desk Widgets`;
  const desc = w.description && w.description !== w.name
    ? w.description
    : `A community widget for the Desk Widgets desktop app.`;
  const url = `${SITE}/w/${w.id}/`;
  const image = hasPreview ? `${url}preview.png` : `${SITE}/assets/img/calendar-dark.png`;

  const meta = [
    `  <title>${esc(title)}</title>`,
    `  <meta name="description" content="${esc(desc)}">`,
    `  <link rel="canonical" href="${esc(url)}">`,
    `  <meta property="og:type" content="website">`,
    `  <meta property="og:title" content="${esc(w.name)}">`,
    `  <meta property="og:description" content="${esc(desc)}">`,
    `  <meta property="og:image" content="${esc(image)}">`,
    `  <meta property="og:url" content="${esc(url)}">`,
    `  <meta name="twitter:card" content="summary_large_image">`,
  ].join("\n");

  let html = template;
  const edits = [
    // 1. Every link in the shared nav, footer and page body is relative to docs/.
    [`  <meta name="viewport" content="width=device-width, initial-scale=1">`,
     `  <meta name="viewport" content="width=device-width, initial-scale=1">\n  <base href="../../">`],
    // 2. The whole point: this page's own identity, for crawlers that never run the script.
    [`  <title>Widget · Desk Widgets</title>`, meta],
    // 3. No query string to read: this file is only ever this widget.
    [`    const id = new URLSearchParams(location.search).get("id");`,
     `    const id = ${JSON.stringify(w.id)};`],
    // 4. <base> would resolve a bare "#source" against the site root and navigate away.
    [`el("a", { class: "more", href: "#source" }, "View source")`,
     `el("a", { class: "more", href: location.pathname + "#source" }, "View source")`],
  ];
  for (const [from, to] of edits) {
    if (!html.includes(from)) throw new Error(`docs/widget.html no longer contains:\n  ${from}`);
    html = html.replace(from, to);
  }
  return html;
}

const check = process.argv.includes("--check");
const widgets = (await gallery()).filter(w => ID_PATTERN.test(w.id) && w.id.length <= 64);
if (!widgets.length) throw new Error("the gallery returned no usable widgets; refusing to wipe docs/w");

const template = await readFile(join(docs, "widget.html"), "utf8");
let written = 0, removed = 0, stale = [];

// What was exported last time: id -> { version, preview }. preview false means that version has
// no picture, which is remembered so it is not asked for again.
let manifest = {};
try { manifest = JSON.parse(await readFile(manifestPath, "utf8")); } catch (e) { /* first run */ }
const exported = w => {
  const m = Object.hasOwn(manifest, w.id) ? manifest[w.id] : null;
  return Boolean(m && m.version === w.version && (!m.preview || existsSync(join(outRoot, w.id, "preview.png"))));
};

const wanted = widgets.filter(w => !exported(w));
const fetched = check || !wanted.length ? new Map() : await previewsOf(wanted.map(w => w.id));
const next = {};

for (const w of widgets) {
  const dir = join(outRoot, w.id);
  const fresh = fetched.get(w.id);
  if (fresh) next[w.id] = { version: fresh.version, preview: Boolean(fresh.png) };
  else if (exported(w)) next[w.id] = manifest[w.id];
  // else: not exported and nothing came back. Left out, so the next run asks again.
  const hasPreview = Boolean(next[w.id] && next[w.id].preview);

  const html = pageFor(template, w, hasPreview);
  const current = existsSync(join(dir, "index.html")) ? await readFile(join(dir, "index.html"), "utf8") : null;
  if (current === html && !fresh && exported(w)) continue;
  if (check) { stale.push(w.id); continue; }
  await mkdir(dir, { recursive: true });
  if (current !== html) await writeFile(join(dir, "index.html"), html);
  if (fresh && fresh.png) await writeFile(join(dir, "preview.png"), fresh.png);
  else if (fresh) await rm(join(dir, "preview.png"), { force: true });
  written++;
}

// A widget pulled from the gallery must not keep a live page.
const live = new Set(widgets.map(w => w.id));
const existing = existsSync(outRoot)
  ? (await readdir(outRoot, { withFileTypes: true })).filter(e => e.isDirectory()).map(e => e.name) : [];
for (const name of existing) {
  if (live.has(name)) continue;
  if (check) { stale.push(`${name} (removed from the gallery)`); continue; }
  await rm(join(outRoot, name), { recursive: true, force: true });
  console.log(`removed docs/w/${name}`);
  removed++;
}

// Sorted, so the file only changes when its contents do.
const manifestText = JSON.stringify(Object.fromEntries(Object.keys(next).sort().map(id => [id, next[id]])), null, 1) + "\n";
const manifestNow = existsSync(manifestPath) ? await readFile(manifestPath, "utf8") : null;
if (!check && manifestText !== manifestNow) { await writeFile(manifestPath, manifestText); written++; }

if (check) {
  if (stale.length) {
    console.error(`docs/w is out of date with the gallery:\n  ${stale.join("\n  ")}`);
    console.error(`Run: node tools/build-widget-pages.mjs`);
    process.exit(1);
  }
  console.log(`docs/w matches the gallery (${widgets.length} widgets).`);
} else {
  console.log(`${widgets.length} widgets, ${fetched.size} preview(s) downloaded, ${written} file set(s) written to docs/w/.`);
  // For the workflow: a scheduled run with nothing new does not redeploy the site.
  if (process.env.GITHUB_OUTPUT) await appendFile(process.env.GITHUB_OUTPUT, `changed=${written + removed > 0}\n`);
}
