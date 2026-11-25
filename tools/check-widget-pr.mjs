// Checks a pull request that changes widgets/ before anyone reviews it:
//
//   - someone who is not a maintainer changes only their own author folder, and nothing else
//   - every widget it touches follows the rules (tools/widget-rules.mjs)
//   - version is 1 for a new widget, and goes up by one exactly when widget.cs changes
//
// It also copies the source of every widget the pull request adds or changes into a folder,
// for the compile check that follows (tools/compile-widgets.ps1).
//
// The pull request is read as data, straight out of git, and never checked out or run: this
// script and its rules come from main, so a pull request cannot loosen the check it is held to
// (.github/workflows/widgets.yml).
//
// Run: node tools/check-widget-pr.mjs <main> <pull request head> [--extract <folder>]
//   PR_AUTHOR        GitHub username of whoever opened the pull request
//   PR_HEAD_REF      the pull request's branch, and PR_HEAD_REPO / PR_BASE_REPO the repositories
//                    it comes from and goes to: together they recognise a pull request the
//                    upload Worker opened for someone (pullRequestOwner in widget-rules.mjs)
//   PR_ASSOCIATION   their relation to the repository (OWNER, MEMBER, COLLABORATOR, ...)

import { execFileSync } from "child_process";
import { mkdirSync, writeFileSync, appendFileSync } from "fs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";
import { authorProblems, widgetProblems, parseJson, PART, WIDGET_FILES, AUTHOR_FILE, pullRequestOwner } from "./widget-rules.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const args = process.argv.slice(2);
const extractAt = args.indexOf("--extract");
const extractTo = extractAt >= 0 ? args[extractAt + 1] : null;
const [main, head] = args;
if (!main || !head || (extractAt >= 0 && !extractTo)) {
  console.error("usage: node tools/check-widget-pr.mjs <main> <pull request head> [--extract <folder>]");
  process.exit(2);
}

const login = pullRequestOwner({
  author: process.env.PR_AUTHOR, headRef: process.env.PR_HEAD_REF,
  headRepo: process.env.PR_HEAD_REPO, baseRepo: process.env.PR_BASE_REPO,
});
const uploaded = login !== (process.env.PR_AUTHOR || "").toLowerCase();
const maintainer = ["OWNER", "MEMBER", "COLLABORATOR"].includes(process.env.PR_ASSOCIATION || "");

const git = (...a) => execFileSync("git", a, { cwd: root, maxBuffer: 64 * 1024 * 1024 });
// A file's bytes at a commit, or null when it is not there.
function fileAt(commit, path) {
  try { return execFileSync("git", ["cat-file", "blob", `${commit}:${path}`], { cwd: root, maxBuffer: 64 * 1024 * 1024, stdio: ["ignore", "pipe", "ignore"] }); }
  catch (e) { return null; }
}
const jsonAt = (commit, path) => { const b = fileAt(commit, path); return b ? parseJson(b.toString("utf8")) : null; };

// What the pull request changes: everything between where it left main and its head.
const from = git("merge-base", main, head).toString().trim();
const changed = git("diff", "--name-only", "--no-renames", "-z", from, head).toString().split("\0").filter(Boolean);

const problems = [];
const say = (where, what) => problems.push(where ? `${where}: ${what}` : what);

const touchedAuthors = new Set(), touchedWidgets = new Set();   // "author", "author/slug"
const elsewhere = [];
for (const path of changed) {
  const parts = path.split("/");
  if (parts[0] !== "widgets" || parts.length < 3) { elsewhere.push(path); continue; }
  const [, author, second, third] = parts;
  touchedAuthors.add(author);
  if (parts.length === 3 && second === AUTHOR_FILE) continue;
  if (parts.length === 4 && WIDGET_FILES.includes(third)) { touchedWidgets.add(`${author}/${second}`); continue; }
  // A file the pull request takes away is fine whatever it was called: the rule is about what a
  // widget folder is left holding. Without this, a file that should never have been merged can
  // never be removed, because the removal reads as a change to a file with the wrong name.
  if (!fileAt(head, path)) continue;
  say(path, `a widget folder holds only ${WIDGET_FILES.join(", ")}, beside one ${AUTHOR_FILE} per author`);
}

// ---- whose folder is it ----
if (!maintainer) {
  if (!login) say("", "the pull request's author is unknown");
  if (elsewhere.length)
    say("", `a widget pull request changes only your own folder in widgets/, but this one also changes ${elsewhere.slice(0, 5).join(", ")}${elsewhere.length > 5 ? " and more" : ""}`);
  for (const author of touchedAuthors) {
    const current = jsonAt(main, `widgets/${author}/${AUTHOR_FILE}`);
    if (current) {
      const owner = String(current.github || "").toLowerCase();
      if (!owner) say(`widgets/${author}`, "this author has not been linked to a GitHub account yet; ask a maintainer to link it");
      else if (owner !== login) say(`widgets/${author}`, `this folder belongs to @${current.github}`);
    } else if (author !== login) {
      say(`widgets/${author}`, `a new author folder is named after your GitHub username: widgets/${login}`);
    }
  }
}

// ---- the author files it leaves behind ----
for (const author of touchedAuthors) {
  if (!PART.test(author)) { say(`widgets/${author}`, "the author folder must be lowercase letters, digits and hyphens"); continue; }
  const at = `widgets/${author}/${AUTHOR_FILE}`;
  const bytes = fileAt(head, at);
  if (!bytes) {
    // Removing the file is fine only when the whole folder goes with it.
    const left = git("ls-tree", "-r", "--name-only", head, "--", `widgets/${author}/`).toString().trim();
    if (left) say(`widgets/${author}`, `${AUTHOR_FILE} is missing`);
    continue;
  }
  const now = parseJson(bytes.toString("utf8"));
  for (const p of authorProblems(now)) say(`widgets/${author}`, p);
  if (!now || maintainer) continue;
  const before = jsonAt(main, at);
  const wanted = before ? String(before.github || "").toLowerCase() : login;
  if (String(now.github || "").toLowerCase() !== wanted) say(at, "github must stay your own GitHub username");
}

// ---- the widgets it adds or changes ----
const toCompile = [];
for (const key of [...touchedWidgets].sort()) {
  const [author, slug] = key.split("/"), dir = `widgets/${key}`;
  const code = fileAt(head, `${dir}/widget.cs`), metaBytes = fileAt(head, `${dir}/meta.json`), preview = fileAt(head, `${dir}/preview.png`);
  if (!code && !metaBytes && !preview) continue;   // the widget is being removed
  const meta = metaBytes ? parseJson(metaBytes.toString("utf8")) : null;
  const wrong = widgetProblems({ author, slug, meta, code, preview });
  for (const p of wrong) say(dir, p);
  if (code) toCompile.push({ id: `u.${author}.${slug}`, code });
  if (!meta || !code || !Number.isInteger(meta.version)) continue;

  const liveCode = fileAt(main, `${dir}/widget.cs`), live = jsonAt(main, `${dir}/meta.json`);
  if (!liveCode || !live) {
    if (meta.version !== 1) say(dir, "a new widget starts at version 1");
  } else if (!liveCode.equals(code)) {
    if (meta.version !== live.version + 1) say(dir, `widget.cs changed, so version goes from ${live.version} to ${live.version + 1}`);
  } else if (meta.version !== live.version) {
    say(dir, `widget.cs did not change, so version stays ${live.version}`);
  }
}

if (extractTo) {
  mkdirSync(extractTo, { recursive: true });
  for (const w of toCompile) writeFileSync(join(extractTo, `${w.id}.cs`), w.code);
}

const summary = problems.length
  ? `### This pull request needs changes before it can be reviewed\n\n${problems.map(p => `- ${p}`).join("\n")}\n`
  : `### Widget checks passed\n\n${toCompile.length} widget(s) added or changed${maintainer ? " by a maintainer" : ` by @${login}${uploaded ? " through the upload page" : ""}, all in their own folder`}.\n`;
console.log(summary);
if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
process.exit(problems.length ? 1 : 0);
