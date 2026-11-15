// Builds the published gallery from widgets/, the folder every community widget lives in:
//
//   widgets/<author>/author.json            { github, bio }
//   widgets/<author>/<slug>/widget.cs       the source people install
//   widgets/<author>/<slug>/preview.png     optional
//   widgets/<author>/<slug>/meta.json       { name, category, description, api_version, version }
//
// A widget is in the gallery because it is on main, and it is on main because a pull request
// for it was merged: merging is the review. The id is u.<author>.<slug>.
//
// Everything is written under docs/w/, which is generated and not committed:
//
//   docs/w/gallery.json          every widget (with the SHA-256 of its source) and every author
//   docs/w/previews.json         id -> { version, preview }, what docs/assets/data.js reads
//   docs/w/<id>/source.cs        the bytes that SHA-256 was taken from
//   docs/w/<id>/preview.png
//   docs/w/<id>/index.html       the widget's own shareable page
//
// Why a page per widget: docs/widget.html is a single file that reads ?id= in the browser, so
// every widget link unfurls with the same title and the same picture. Crawlers do not run the
// page's JavaScript, so the only fix is a real file per widget with its own <title>,
// description and og:image. Each one is docs/widget.html with four mechanical edits, so a change
// to widget.html reaches every generated page the next time this runs:
//   1. <base href="../../"> so the shared nav, footer and profile links still resolve
//   2. the real title and the social meta tags
//   3. the widget id baked in, instead of reading the query string
//   4. the one fragment link fixed, because <base> would send it to the site root
//
// Run: node tools/build-widget-pages.mjs      (.github/workflows/pages.yml runs it on every publish)
//
// Before serving docs/ locally, run it once, or the gallery pages have nothing to show.

import { readFile, writeFile, mkdir, rm, readdir, copyFile } from "fs/promises";
import { existsSync } from "fs";
import { createHash } from "crypto";
import { execFileSync } from "child_process";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const docs = join(root, "docs");
const source = join(root, "widgets");
const outRoot = join(docs, "w");

const { REPO_URL } = await import("file://" + join(docs, "config.js").replace(/\\/g, "/"));

// github.com/<owner>/<repo> -> <owner>.github.io/<repo>, the Pages URL og: tags must be absolute for.
const repo = REPO_URL.replace(/\/+$/, "").split("/");
const SITE = `https://${repo[repo.length - 2]}.github.io/${repo[repo.length - 1]}`;

// The same limits the app and the pull request checks hold a widget to. A widget that breaks one
// stops the build: publishing a gallery with a hole in it would look like the widget was removed.
const PART = /^[a-z0-9-]+$/;
const CATEGORIES = ["clocks", "productivity", "weather", "system", "media", "developer", "sports",
  "finance", "news", "utility", "social", "health", "travel", "games"];
const MAX_SOURCE = 524288, MAX_PREVIEW = 105000;
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

const esc = s => String(s ?? "")
  .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
  .replace(/"/g, "&quot;").replace(/'/g, "&#39;");

const folders = async dir => (await readdir(dir, { withFileTypes: true }))
  .filter(e => e.isDirectory()).map(e => e.name).sort();
const readJson = async path => JSON.parse(await readFile(path, "utf8"));
const wholeNumber = n => Number.isInteger(n) && n >= 1;

// When the widget's source last changed on main: the day it was merged, which is its review.
function mergedAt(path) {
  try {
    const date = execFileSync("git", ["log", "-1", "--format=%cI", "--", path], { cwd: root, encoding: "utf8" }).trim();
    return date || null;
  } catch (e) { return null; }
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

// Widgets that came from the old database keep the date they were reviewed there, and must still
// be the bytes that were reviewed there, for as long as they stay on that version.
const migrated = existsSync(join(source, "migrated.json")) ? await readJson(join(source, "migrated.json")) : {};

const widgets = [], authors = [];
for (const author of await folders(source)) {
  if (!PART.test(author)) throw new Error(`widgets/${author}: an author folder is lowercase letters, digits and hyphens`);
  const who = await readJson(join(source, author, "author.json"));
  authors.push({ username: author, github: String(who.github || ""), bio: String(who.bio || "").slice(0, 160) });

  for (const slug of await folders(join(source, author))) {
    const dir = join(source, author, slug), id = `u.${author}.${slug}`, at = `widgets/${author}/${slug}`;
    const fail = why => { throw new Error(`${at}: ${why}`); };
    if (!PART.test(slug) || slug.length > 36 || id.length > 64) fail("the folder name is not a usable widget id");

    const meta = await readJson(join(dir, "meta.json"));
    if (typeof meta.name !== "string" || meta.name.length < 1 || meta.name.length > 40) fail("name must be 1 to 40 characters");
    if (!CATEGORIES.includes(meta.category)) fail(`category must be one of: ${CATEGORIES.join(", ")}`);
    if (typeof meta.description !== "string" || meta.description.length > 120) fail("description must be at most 120 characters");
    if (!wholeNumber(meta.api_version) || !wholeNumber(meta.version)) fail("api_version and version must be whole numbers from 1");

    const code = await readFile(join(dir, "widget.cs"));
    if (!code.length || code.length > MAX_SOURCE) fail(`widget.cs must be 1 to ${MAX_SOURCE} bytes`);
    const sha256 = createHash("sha256").update(code).digest("hex");

    const was = Object.hasOwn(migrated, id) && migrated[id].version === meta.version ? migrated[id] : null;
    if (was && was.sha256 !== sha256) fail(`widget.cs changed but version is still ${meta.version}`);

    const previewPath = join(dir, "preview.png");
    const preview = existsSync(previewPath) ? await readFile(previewPath) : null;
    if (preview && (preview.length > MAX_PREVIEW || !preview.subarray(0, 8).equals(PNG_SIGNATURE))) fail(`preview.png must be a PNG of at most ${MAX_PREVIEW} bytes`);

    widgets.push({
      id, version: meta.version, name: meta.name, category: meta.category, description: meta.description,
      api_version: meta.api_version, sha256, author, preview: Boolean(preview),
      reviewed_at: was ? was.reviewed_at : mergedAt(`${at}/widget.cs`) || new Date().toISOString(),
      dir,
    });
  }
}
if (!widgets.length) throw new Error("widgets/ holds no widgets; refusing to publish an empty gallery");

// Newest first, the order Explore shows. The id settles a tie so the file is the same every run.
widgets.sort((a, b) => Date.parse(b.reviewed_at) - Date.parse(a.reviewed_at) || a.id.localeCompare(b.id));

// docs/w is only ever this script's output, so it is rebuilt whole: a widget removed from
// widgets/ cannot keep a live page.
const template = await readFile(join(docs, "widget.html"), "utf8");
await rm(outRoot, { recursive: true, force: true });
for (const w of widgets) {
  const dir = join(outRoot, w.id);
  await mkdir(dir, { recursive: true });
  await writeFile(join(dir, "index.html"), pageFor(template, w, w.preview));
  await copyFile(join(w.dir, "widget.cs"), join(dir, "source.cs"));
  if (w.preview) await copyFile(join(w.dir, "preview.png"), join(dir, "preview.png"));
}

const listed = widgets.map(({ dir, ...w }) => w);
await writeFile(join(outRoot, "gallery.json"), JSON.stringify({ widgets: listed, authors }, null, 1) + "\n");
await writeFile(join(outRoot, "previews.json"), JSON.stringify(Object.fromEntries(
  widgets.map(w => [w.id, { version: w.version, preview: w.preview }]).sort(([a], [b]) => a.localeCompare(b))), null, 1) + "\n");

console.log(`${widgets.length} widgets by ${authors.length} authors written to docs/w/.`);
