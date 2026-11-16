// Decides what an upload changes, without touching the network: given who signed in, what the
// form holds and what is on main, either the problems to show the person, or the files to
// commit and the pull request to open. Everything it allows, the pull request check allows
// (tools/check-widget-pr.mjs); both use the rules in tools/widget-rules.mjs.

import { widgetProblems, authorProblems, CATEGORIES } from "../../tools/widget-rules.mjs";

const enc = new TextEncoder();

// The name git gives a file's contents, to tell whether an upload differs from main without
// downloading what is there.
export async function blobSha(bytes) {
  const head = enc.encode(`blob ${bytes.length}\0`);
  const all = new Uint8Array(head.length + bytes.length);
  all.set(head); all.set(bytes, head.length);
  const hash = new Uint8Array(await crypto.subtle.digest("SHA-1", all));
  return [...hash].map(b => b.toString(16).padStart(2, "0")).join("");
}

// A rule's sentence, in the words of the form rather than of the repository's folders.
export const inFormWords = p => p.replace(/^meta\.json: /, "").replace(/^the widget folder must/, "the short name must");

// Text a stranger typed, made safe to put in a pull request: no mentions, no markup.
const plain = s => String(s).replace(/[`@<>\[\]\r\n]/g, " ").trim();

// who     { login, id }: the GitHub account that signed in
// folder  their folder in widgets/
// author  that folder's author.json on main, or null for a first upload
// live    the widget on main: { meta, codeSha, previewSha }, or null when it is new
// form    { slug, name, category, description, api_version, bio, code, preview };
//         code and preview are bytes, or null to keep what is on main
export async function planUpload({ who, folder, author, live, form }) {
  const slug = String(form.slug || "").trim();
  const code = form.code && form.code.length ? form.code : null;
  const preview = form.preview && form.preview.length ? form.preview : null;
  const problems = [];

  const codeChanged = code ? !live || live.codeSha !== await blobSha(code) : false;
  if (!code && !live) problems.push("choose the widget's source file");
  if (!preview && !(live && live.previewSha)) problems.push("add a preview picture: it is what people see in the gallery");

  const meta = {
    name: String(form.name || "").trim(),
    category: CATEGORIES.includes(form.category) ? form.category : String(form.category || ""),
    description: String(form.description || "").trim(),
    api_version: Number(form.api_version),
    // 1 for a new widget, and up by one exactly when the source changes.
    version: live ? live.meta.version + (codeChanged ? 1 : 0) : 1,
  };
  // The source that stays on main already passed these rules; a stand-in keeps them quiet about it.
  const checked = code || (live ? enc.encode("[DeskWidget]") : null);
  for (const p of widgetProblems({ author: folder, slug, meta, code: checked, preview }))
    if (!(p.startsWith("widget.cs is missing") && problems.length)) problems.push(inFormWords(p));

  const bio = form.bio == null ? (author ? author.bio : "") : String(form.bio).trim();
  const authorNow = { github: author ? author.github : who.login, bio };
  for (const p of authorProblems(authorNow)) problems.push(p.replace(/^author\.json: /, ""));
  if (problems.length) return { problems };

  const json = o => enc.encode(JSON.stringify(o, null, 2) + "\n");
  const dir = `widgets/${folder}/${slug}`;
  const files = [];
  if (codeChanged) files.push({ path: `${dir}/widget.cs`, bytes: code });
  if (preview && !(live && live.previewSha === await blobSha(preview))) files.push({ path: `${dir}/preview.png`, bytes: preview });
  const same = live && ["name", "category", "description", "api_version", "version"].every(k => live.meta[k] === meta[k]);
  if (!same) files.push({ path: `${dir}/meta.json`, bytes: json(meta) });
  if (!files.length && author && author.bio === bio) return { problems: ["nothing here differs from what is already in the gallery"] };
  if (!author || author.bio !== bio) files.push({ path: `widgets/${folder}/author.json`, bytes: json(authorNow) });

  const login = who.login.toLowerCase();
  const name = plain(meta.name);
  const what = !live ? "a new widget" : codeChanged ? `version ${meta.version} of a widget already in the gallery` : "a change to a widget's description or picture, with the same source";
  return {
    problems: [],
    files,
    version: meta.version,
    branch: `upload/${login}/${slug}`,
    title: !live ? `Add ${name}` : codeChanged ? `Update ${name} to version ${meta.version}` : `Update the details of ${name}`,
    message: `${!live ? "Add" : "Update"} ${dir}`,
    committer: { name: who.login, email: `${who.id}+${who.login}@users.noreply.github.com` },
    body: [
      `@${who.login} sent this through the upload page: ${what}.`,
      "",
      `- Widget: \`${name}\` (\`u.${folder}.${slug}\`), category \`${plain(meta.category)}\`, API version ${meta.api_version}`,
      `- Description: \`${plain(meta.description) || "none"}\``,
      "",
      "The upload page checked who signed in, and the branch name records it. Review comments here reach them.",
    ].join("\n"),
  };
}
