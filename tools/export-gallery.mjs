// Copies the approved widgets out of the Supabase gallery into widgets/, the folder the gallery
// is published from (tools/build-widget-pages.mjs). This is the move off the database: it is run
// by hand, and again only if something was approved in the database after the last run. It goes
// away together with the database.
//
//   widgets/<author>/author.json            { github, bio }
//   widgets/<author>/<slug>/widget.cs       the reviewed source, byte for byte
//   widgets/<author>/<slug>/preview.png
//   widgets/<author>/<slug>/meta.json       { name, category, description, api_version, version }
//   widgets/migrated.json                   id -> { version, sha256, reviewed_at } as reviewed there
//
// A widget's id stays u.<author>.<slug>, so copies already installed keep finding their updates.
// The source is refused unless its SHA-256 is the one the reviewer approved.
//
// Run: node tools/export-gallery.mjs
//
// Reads with the publishable key, exactly as a visitor's browser does. Previews come from
// docs/w when the site build has already exported that version, so they are not downloaded twice.

import { readFile, writeFile, mkdir } from "fs/promises";
import { existsSync } from "fs";
import { createHash } from "crypto";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const out = join(root, "widgets");
const exportedDir = join(root, "docs", "w");

// The old gallery, and its publishable key: public by design there, every permission was row
// level security. The website no longer knows either.
const SUPABASE_URL = "https://vtgsbgkjleyqlxgjdvqy.supabase.co";
const SUPABASE_ANON_KEY = "sb_publishable_7-z7D1GwtQJumqAPFxlJlA_d3myO4gk";

const ID_PATTERN = /^u\.[a-z0-9-]+\.[a-z0-9-]+$/;
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

async function rest(query) {
  const res = await fetch(`${SUPABASE_URL}/rest/v1/${query}`, { headers: { apikey: SUPABASE_ANON_KEY } });
  if (!res.ok) throw new Error(`gallery read failed: ${res.status} ${res.statusText} (${query})`);
  return res.json();
}

const json = value => JSON.stringify(value, null, 2) + "\n";
const readJson = async (path, otherwise) => {
  try { return JSON.parse(await readFile(path, "utf8")); } catch (e) { return otherwise; }
};

const widgets = await rest("explore?select=id,version,name,category,description,api_version,sha256,reviewed_at,author&order=id");
if (!widgets.length) throw new Error("the gallery returned no widgets");

const exported = await readJson(join(exportedDir, "previews.json"), {});
const migrated = {};
let previewsDownloaded = 0, withoutPreview = [];

for (const w of widgets) {
  if (!ID_PATTERN.test(w.id) || w.id.length > 64) throw new Error(`unusable id: ${w.id}`);
  const [, author, slug] = w.id.split(".");
  if (author !== w.author) throw new Error(`${w.id} is listed under the author "${w.author}"`);

  const rows = await rest(`versions?select=source,sha256&widget_id=eq.${w.id}&version=eq.${w.version}&status=eq.approved`);
  if (rows.length !== 1) throw new Error(`${w.id} v${w.version}: expected one approved version, got ${rows.length}`);
  const source = Buffer.from(rows[0].source, "utf8");
  const hash = createHash("sha256").update(source).digest("hex");
  if (hash !== w.sha256 || hash !== rows[0].sha256) throw new Error(`${w.id} v${w.version}: the source does not match its reviewed SHA-256`);

  let png = null;
  const file = Object.hasOwn(exported, w.id) ? exported[w.id] : null;
  if (file && file.version === w.version) {
    if (file.preview) png = await readFile(join(exportedDir, w.id, "preview.png"));
  } else {
    const [row] = await rest(`explore?select=version,preview&id=eq.${w.id}`);
    if (row && row.version === w.version && typeof row.preview === "string" && /^[A-Za-z0-9+/=]+$/.test(row.preview)) {
      png = Buffer.from(row.preview, "base64");
      previewsDownloaded++;
    }
  }
  if (png && !png.subarray(0, 8).equals(PNG_SIGNATURE)) png = null;
  if (!png) withoutPreview.push(w.id);

  const dir = join(out, author, slug);
  await mkdir(dir, { recursive: true });
  await writeFile(join(dir, "widget.cs"), source);
  if (png) await writeFile(join(dir, "preview.png"), png);
  await writeFile(join(dir, "meta.json"), json({
    name: w.name, category: w.category, description: w.description,
    api_version: w.api_version, version: w.version,
  }));
  migrated[w.id] = { version: w.version, sha256: hash, reviewed_at: w.reviewed_at };
}

// One author.json per author that has a widget. "github" is who may change the folder; it is
// filled in by hand, and a value already there is kept.
const bios = new Map((await rest("authors?select=username,bio")).map(a => [a.username, a.bio || ""]));
const authors = [...new Set(widgets.map(w => w.author))].sort();
for (const author of authors) {
  const path = join(out, author, "author.json");
  const current = await readJson(path, {});
  await writeFile(path, json({ github: current.github || "", bio: bios.get(author) || "" }));
}

await writeFile(join(out, "migrated.json"), json(migrated));

console.log(`${widgets.length} widgets by ${authors.length} authors written to widgets/ (${previewsDownloaded} preview(s) downloaded).`);
if (withoutPreview.length) console.log(`No preview: ${withoutPreview.join(", ")}`);
