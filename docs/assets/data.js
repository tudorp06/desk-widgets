// The gallery's data layer. Callers ask for widgets, authors, previews and source and never
// learn where they came from.
//
// It all comes from files on this site, built from the widgets/ folder of the repository by
// tools/build-widget-pages.mjs: w/gallery.json lists every widget and author, and each widget
// has w/<id>/preview.png and w/<id>/source.cs. There is no database and nothing to sign in to.
// To see the gallery on a dev host, run that script once before serving docs/.
import { isValidId, isAuthor } from "./site.js";

const TIMEOUT = 10000;      // every call fails rather than hanging the loading state

const file = path => new URL("../w/" + path, import.meta.url);

// gallery.json, asked for once per page however many callers there are. A failed read is not
// kept, so a retry asks again.
let loaded = null;
function gallery() {
  if (!loaded) {
    loaded = fetch(file("gallery.json"), { signal: AbortSignal.timeout(TIMEOUT) })
      .then((response) => {
        if (!response.ok) throw new Error(response.status + " " + response.statusText);
        return response.json();
      })
      .then((g) => ({
        widgets: (Array.isArray(g.widgets) ? g.widgets : []).filter((w) => w && isValidId(w.id) && isAuthor(w.author)),
        authors: (Array.isArray(g.authors) ? g.authors : []).filter((a) => a && isAuthor(a.username)),
      }));
    loaded.catch(() => { loaded = null; });
  }
  return loaded;
}

// Every widget, newest first: id, version, name, category, description, api_version, sha256,
// reviewed_at, author, preview (whether it has a picture).
export async function getWidgets() {
  return (await gallery()).widgets.slice();
}

// One widget by id, or null.
export async function getWidget(id) {
  if (!isValidId(id)) return null;
  return (await gallery()).widgets.find((w) => w.id === id) || null;
}

// Every author: username, github, bio, avatar (a file name in w/avatars/, or null).
export async function getProfiles() {
  return (await gallery()).authors.slice();
}

// A single author by username, or null.
export async function getProfile(username) {
  if (!isAuthor(username)) return null;
  return (await gallery()).authors.find((a) => a.username === username) || null;
}

// id -> an <img> src (or null for no preview), for widgets as getWidgets answers them. The
// version in the address makes a new version's picture a new URL, past any cache.
export async function getPreviews(widgets) {
  return new Map(widgets.map((w) => [w.id, w.preview
    ? file(encodeURIComponent(w.id) + "/preview.png?v=" + encodeURIComponent(w.version)).href : null]));
}

// The widget's C# source: the bytes its sha256 in the gallery was taken from.
export async function getSource(w) {
  const response = await fetch(file(encodeURIComponent(w.id) + "/source.cs?v=" + encodeURIComponent(w.version)),
    { signal: AbortSignal.timeout(TIMEOUT) });
  if (!response.ok) throw new Error(response.status + " " + response.statusText);
  return response.text();
}
