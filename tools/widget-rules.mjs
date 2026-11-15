// What a community widget in widgets/ must be. One set of rules, used twice: on a pull request
// (tools/check-widget-pr.mjs), so the author hears about a problem before anyone reviews, and
// when the gallery is built (tools/build-widget-pages.mjs), so nothing broken is ever published.
// The app holds an installed widget to the same limits (src/Plugins.cs, src/Community.cs).

export const PART = /^[a-z0-9-]+$/;          // an author folder or a widget folder
export const GITHUB_LOGIN = /^[A-Za-z0-9-]{1,39}$/;
export const CATEGORIES = ["clocks", "productivity", "weather", "system", "media", "developer", "sports",
  "finance", "news", "utility", "social", "health", "travel", "games"];
export const MAX_SOURCE = 524288, MAX_PREVIEW = 105000, MAX_BIO = 160;
// The only files a widget folder holds, and the only file beside the widget folders.
export const WIDGET_FILES = ["widget.cs", "meta.json", "preview.png"];
export const AUTHOR_FILE = "author.json";

const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const wholeNumber = n => Number.isInteger(n) && n >= 1;

// Characters that make code look different from what the C# compiler sees ("Trojan Source",
// CVE-2021-42574). Same rule as Plugins.IsHidden (app), hiddenCharacters (docs/assets/site.js)
// and tools/check-hidden.ps1. The pattern is assembled from pieces so this file never holds the
// characters, or an escape a tool could turn into them: u + "2028" becomes the escape for U+2028.
const u = "\\" + "u";
const HIDDEN = new RegExp("\r(?!\n)|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F" + u + "200B-" + u + "200F" + u + "2028" + u + "2029" +
  u + "202A-" + u + "202E" + u + "2060-" + u + "2069" + u + "FEFF]");

// "U+2028 on line 3" for the first hidden character, or null when the text is plain.
export function hiddenCharacter(text) {
  const m = HIDDEN.exec(text);
  if (!m) return null;
  const code = m[0].charCodeAt(0).toString(16).toUpperCase().padStart(4, "0");
  return `U+${code} on line ${text.slice(0, m.index).split("\n").length}`;
}

// JSON.parse that answers null instead of throwing, for text a stranger wrote.
export function parseJson(text) {
  try {
    const value = JSON.parse(text);
    return value && typeof value === "object" && !Array.isArray(value) ? value : null;
  } catch (e) { return null; }
}

// Everything wrong with an author.json, as sentences. Empty means it is fine.
// github may be empty only for an author brought over from the old gallery who has not said
// which GitHub account is theirs; nobody but a maintainer can change that folder until it is set.
export function authorProblems(author) {
  if (!author) return ["author.json is not a JSON object"];
  const problems = [];
  if (typeof author.github !== "string" || (author.github !== "" && !GITHUB_LOGIN.test(author.github)))
    problems.push("author.json: github must be a GitHub username");
  if (typeof author.bio !== "string" || author.bio.length > MAX_BIO)
    problems.push(`author.json: bio must be text of at most ${MAX_BIO} characters`);
  return problems;
}

// Everything wrong with one widget, as sentences. Empty means it is fine.
// meta is the parsed meta.json (or null), code and preview are the files' bytes (or null).
export function widgetProblems({ author, slug, meta, code, preview }) {
  const problems = [];
  if (!PART.test(author)) problems.push("the author folder must be lowercase letters, digits and hyphens");
  if (!PART.test(slug) || slug.length > 36 || `u.${author}.${slug}`.length > 64)
    problems.push("the widget folder must be lowercase letters, digits and hyphens, at most 36 characters");

  if (!meta) problems.push("meta.json is missing or is not a JSON object");
  else {
    if (typeof meta.name !== "string" || meta.name.trim().length < 1 || meta.name.length > 40)
      problems.push("meta.json: name must be 1 to 40 characters");
    if (!CATEGORIES.includes(meta.category))
      problems.push(`meta.json: category must be one of ${CATEGORIES.join(", ")}`);
    if (typeof meta.description !== "string" || meta.description.length > 120)
      problems.push("meta.json: description must be text of at most 120 characters");
    if (!wholeNumber(meta.api_version)) problems.push("meta.json: api_version must be a whole number from 1");
    if (!wholeNumber(meta.version)) problems.push("meta.json: version must be a whole number from 1");
  }

  if (!code || !code.length) problems.push("widget.cs is missing or empty");
  else if (code.length > MAX_SOURCE) problems.push(`widget.cs is larger than ${MAX_SOURCE / 1024} KB`);
  else {
    let text = null;
    try { text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(code); }
    catch (e) { problems.push("widget.cs is not UTF-8 text"); }
    if (text !== null) {
      const hidden = hiddenCharacter(text);
      if (hidden) problems.push(`widget.cs contains a hidden character (${hidden}) that can make code look different from what runs`);
      const marked = (text.match(/\[\s*DeskWidget\b/g) || []).length;
      if (marked !== 1) problems.push("widget.cs must have exactly one class marked [DeskWidget]");
    }
  }

  if (preview && !preview.subarray(0, 8).equals(PNG_SIGNATURE)) problems.push("preview.png is not a PNG");
  else if (preview && preview.length > MAX_PREVIEW) problems.push(`preview.png is larger than ${Math.floor(MAX_PREVIEW / 1000)} KB`);
  return problems;
}
