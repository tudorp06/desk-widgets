// Shared helpers for every page. Everything an author wrote reaches the page through
// textContent (el()), never innerHTML, and ids are re-validated here even though the gallery
// build already checks them.
import { RELEASES_URL, DOWNLOAD_URL, SETUP_URL, REPO_URL, STORE_URL, ANALYTICS_CODE, UPLOAD_URL } from "../config.js";

// Where "publish" leads: the upload page while there is a Worker behind it, the guide otherwise.
export const PUBLISH_URL = UPLOAD_URL ? "upload.html" : "develop.html#publish";

export { RELEASES_URL, DOWNLOAD_URL, SETUP_URL, REPO_URL, STORE_URL };

// The same categories as the app (WidgetRegistry.Categories) and tools/widget-rules.mjs.
export const CATEGORIES = [
  ["clocks", "Clocks"], ["productivity", "Productivity"], ["weather", "Weather"],
  ["system", "System"], ["media", "Media"], ["developer", "Developer"], ["sports", "Sports"],
  ["finance", "Finance"], ["news", "News"], ["utility", "Utility"], ["social", "Social"],
  ["health", "Health"], ["travel", "Travel"], ["games", "Games"],
];
export const categoryName = id => {
  const known = CATEGORIES.find(c => c[0] === id);
  if (known) return known[1];
  return id ? String(id).charAt(0).toUpperCase() + String(id).slice(1) : "";
};

// One line icon per category, for cards with no preview. 24-unit paths, drawn at any size.
const CATEGORY_PATHS = {
  clocks: ["M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z", "M12 7v5l3.5 2"],
  productivity: ["M5 3.5h14v17H5z", "M9 9h6M9 13h6M9 17h3"],
  weather: ["M7.5 4.5a3 3 0 1 0 0 6 3 3 0 0 0 0-6z", "M17.5 19H10a4.5 4.5 0 0 1 0-9 5 5 0 0 1 9.4 1.6A3.7 3.7 0 0 1 17.5 19z"],
  system: ["M5 5h14v14H5z", "M9.5 9.5h5v5h-5z", "M9 2.5V5M15 2.5V5M9 19v2.5M15 19v2.5M2.5 9H5M2.5 15H5M19 9h2.5M19 15h2.5"],
  media: ["M9.5 17.5V5.5l10-2v12", "M7 15a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z", "M17 13a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z"],
  developer: ["M9 8l-4 4 4 4M15 8l4 4-4 4"],
  sports: ["M5 3v18", "M5 5c4.5-2.2 9 2.2 14 0v8.5c-5 2.2-9.5-2.2-14 0z"],
  games: ["M8 10v4M6 12h4", "M3.5 8.5h17v7h-17z", "M15.5 11h.01M18 13.5h.01"],
  finance: ["M4 19h16", "M7 15l4-4 3 3 5-6"],
  widget: ["M4 4.5h7v7H4zM13 4.5h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z"],
};

export function categoryIcon(category, size = 32) {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("width", String(size));
  svg.setAttribute("height", String(size));
  svg.setAttribute("class", "cat-ico");
  svg.setAttribute("aria-hidden", "true");
  for (const d of CATEGORY_PATHS[category] || CATEGORY_PATHS.widget) {
    const path = document.createElementNS(ns, "path");
    path.setAttribute("d", d);
    svg.appendChild(path);
  }
  return svg;
}

export const ID_PATTERN = /^u\.[a-z0-9-]+\.[a-z0-9-]+$/;
export const isValidId = id => typeof id === "string" && id.length <= 64 && ID_PATTERN.test(id);

// An author's folder in widgets/: their GitHub username in lower case, or the name they had in
// the old gallery. Checked before it becomes part of an address.
export const isAuthor = name => typeof name === "string" && /^[a-z0-9-]{1,39}$/.test(name);

// el("a", { href: "#", class: "x" }, "text", child, ...): text arguments become text nodes.
export function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === false || v == null) continue;
    if (k.startsWith("on")) node.addEventListener(k.slice(2), v);
    else node.setAttribute(k, v === true ? "" : String(v));
  }
  for (const c of children.flat()) if (c != null && c !== false) node.append(c instanceof Node ? c : String(c));
  return node;
}

// The global nav and the footer, on every page.

// Page views, and the four clicks worth knowing: Download, Microsoft Store, Get, and the
// "not installed" prompt Get shows when nothing opened. Those four answer the questions a launch
// post raises -- did it bring installs, and do visitors arrive without the app -- and nothing
// else is collected. GoatCounter keeps a daily hash instead of a visitor and sets no cookie, so
// there is no consent banner to show. Do Not Track is honoured here because its script does not
// check it. With ANALYTICS_CODE empty, nothing is loaded and nothing is sent.
export const eventKind = href =>
  href === DOWNLOAD_URL || href === SETUP_URL ? "download"
  : href === STORE_URL ? "store"
  : href.indexOf("deskwidgets://install/") === 0 ? "get"
  : null;

// shortcut: a click landing before the async script does is not counted; add a queue if these
// numbers ever read low against the download counts on the GitHub releases.
function count(name) {
  if (window.goatcounter && window.goatcounter.count)
    window.goatcounter.count({ path: name, title: name, event: true });
}

// One delegated listener rather than a handler per link: index.html, explore.html and
// widget.html each build their own download and Get buttons.
function analytics() {
  if (!ANALYTICS_CODE || navigator.doNotTrack === "1") return;
  document.head.append(el("script", { async: true, src: "//gc.zgo.at/count.js",
    "data-goatcounter": "https://" + ANALYTICS_CODE + ".goatcounter.com/count" }));
  addEventListener("click", e => {
    const a = e.target.closest ? e.target.closest("a") : null;
    if (!a) return;
    const kind = eventKind(a.href);
    if (kind) count(kind);
  });
}

export function nav(current) {
  const link = (href, text, key) => el("a", { class: "link", href, "aria-current": current === key ? "page" : null }, text);
  document.body.prepend(el("nav", { class: "nav", "aria-label": "Site" },
    el("div", { class: "inner" },
      el("a", { class: "brand", href: "./" }, "Desk Widgets"),
      link("explore.html", "Explore", "explore"),
      link("develop.html", "Develop", "develop"),
      UPLOAD_URL ? link("upload.html", "Publish", "publish") : null,
      link("download.html", "Download", "download"),
      el("div", { class: "account" }), themeToggle())));
  document.body.append(footer());
  analytics();
  speculate();
}

// Starts fetching a page of this site when a link to it is hovered or pressed, so the click
// finds it already here. Downloads and the app's own links are left alone.
function speculate() {
  if (!HTMLScriptElement.supports || !HTMLScriptElement.supports("speculationrules")) return;
  const rules = el("script", { type: "speculationrules" });
  rules.textContent = JSON.stringify({ prefetch: [{ eagerness: "moderate",
    where: { and: [{ href_matches: "/*" }, { not: { href_matches: "*.exe" } }] } }] });
  document.head.append(rules);
}

// Every page's footer. Only links that go somewhere real; the licence line is the one in LICENSE.
function footer() {
  const column = (title, links) => el("div", {}, el("h2", { class: "f-head" }, title),
    ...links.map(([href, text]) => el("a", { href }, text)));
  return el("footer", { class: "footer" },
    el("div", { class: "inner" },
      el("p", { class: "f-notice" },
        "Desk Widgets is free and open source. Community widgets are reviewed by a person before they appear here."),
      el("div", { class: "f-cols" },
        column("Widgets", [["explore.html", "Explore"], ["./#catalog", "Built-in widgets"], [PUBLISH_URL, "Publish yours"]]),
        column("App", [["download.html", "Download"], [STORE_URL, "Microsoft Store"],
          [RELEASES_URL, "What's new"], ["privacy.html", "Privacy"]]),
        column("Open source", [[REPO_URL, "Source code"], [REPO_URL + "/issues", "Report an issue"],
          ["develop.html", "Create a widget"]])),
      el("p", { class: "f-bottom" },
        "Copyright © 2026 whoissaaif and Desk Widgets contributors. Released under the MIT license.")));
}

// A round profile photo, or the first letter of the name on grey. The photo is the author's
// GitHub picture, copied onto this site when the gallery is built (w/avatars/), so showing it
// asks nothing of GitHub. A file that fails to load falls back to the letter.
const AVATAR_FILE = /^[a-z0-9-]{1,39}\.(png|jpg|gif|webp)$/;
export function avatar(profile, size) {
  const style = "width:" + size + "px;height:" + size + "px";
  const letter = () => el("span", { class: "avatar", style: style + ";font-size:" + Math.round(size * 0.45) + "px", "aria-hidden": "true" },
    profile && profile.username ? profile.username[0].toUpperCase() : "");
  if (!profile || typeof profile.avatar !== "string" || !AVATAR_FILE.test(profile.avatar)) return letter();
  const img = el("img", { class: "avatar", style, alt: "", src: new URL("../w/avatars/" + profile.avatar, import.meta.url).href });
  img.addEventListener("error", () => img.replaceWith(letter()));
  return img;
}

// Brave with Shields blocking a site's storage -- and any browser with cookies blocked -- throws a
// SecurityError on localStorage instead of returning null. Every page imports this module, so one
// unguarded read blanks the whole site. All access goes through these two.
function stored(key) {
  try { return localStorage.getItem(key); } catch (e) { return null; }
}
function store(key, value) {
  try { localStorage.setItem(key, value); } catch (e) { /* no storage: the choice won't stick */ }
}

// Apply saved theme before anything renders (called once at import time).
const _savedTheme = stored("dw-theme");
if (_savedTheme) document.documentElement.setAttribute("data-theme", _savedTheme);

// Light / Dark button at the right of the nav. Persists in localStorage so the choice sticks
// across pages; with nothing stored it flips away from whatever the system asks for.
function themeToggle() {
  const current = () => stored("dw-theme") ||
    (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  const button = el("button", { type: "button", class: "theme-button", "aria-label": "Switch light or dark",
    onclick: () => {
      const theme = current() === "dark" ? "light" : "dark";
      document.documentElement.setAttribute("data-theme", theme);
      store("dw-theme", theme);
    } });
  button.innerHTML = '<svg width="17" height="17" viewBox="0 0 16 16" aria-hidden="true" focusable="false">' +
    '<circle cx="8" cy="8" r="7.25" fill="none" stroke="currentColor" stroke-width="1.5"/>' +
    '<path d="M8 .75a7.25 7.25 0 0 0 0 14.5z" fill="currentColor"/></svg>';
  return button;
}

// The Explore card. Pass artNode to supply your own art element (explore.html lazy-loads
// previews into a skeleton). options: author false drops the "by" line (a profile page is
// already one person's).
export function widgetCard(w, artNode, options = {}) {
  // The generated page (tools/build-widget-pages.mjs): same content as widget.html?id=, but it
  // unfurls as itself when shared.
  const href = "w/" + encodeURIComponent(w.id) + "/";
  const by = options.author === false ? [categoryName(w.category)]
    : [el("a", { href: "profile.html?user=" + encodeURIComponent(w.author) }, w.author),
       " · " + categoryName(w.category)];
  return el("article", { class: "widget-card" },
    el("a", { href, "aria-label": w.name }, artNode || artFrom(null, w.name, w.category)),
    el("div", { class: "widget-card-body" },
      el("div", { class: "widget-card-text" },
        el("a", { class: "name", href }, w.name),
        el("div", { class: "by" }, by)),
      getButton(w.id, w.version)));
}

// The person card from Explore's People tab.
export function personCard(profile, count) {
  return el("a", { class: "profile-card", href: "profile.html?user=" + encodeURIComponent(profile.username) },
    avatar(profile, 72),
    el("div", { class: "p-info" },
      el("div", { class: "p-name" }, profile.username),
      profile.bio ? el("div", { class: "p-bio" }, profile.bio) : null,
      el("div", { class: "p-stat" }, count + " widget" + (count !== 1 ? "s" : ""))),
    el("span", { class: "p-arrow" }, "›"));
}

// A widget's preview as an <img>, or a quiet placeholder. src is what getPreviews (data.js)
// answers with: the address of the widget's preview.png, or null.
export function artFrom(src, name, category) {
  return el("div", { class: "art" }, src ? el("img", { src, alt: name + " preview" }) : categoryIcon(category));
}

// Opens Desk Widgets on this PC; it re-checks everything and always asks before installing.
export const installUrl = (id, version) => `deskwidgets://install/${id}/${version}`;

// A protocol link does nothing visible when the app isn't installed, so visitors arriving from a
// shared link click Get and see nothing. If the page still has focus 1.5s later, nothing opened.
export function getButton(id, version, label = "Get", cls = "get") {
  const check = () => {
    let left = document.hidden;
    const away = () => { left = true; };
    addEventListener("blur", away);
    document.addEventListener("visibilitychange", away);
    setTimeout(() => {
      removeEventListener("blur", away);
      document.removeEventListener("visibilitychange", away);
      if (left || document.hidden || !document.hasFocus()) return;
      count("no-app");
      confirmDialog("Desk Widgets isn’t installed",
        "Get opens the app on your PC. Download it, then click Get again to install this widget.",
        "Download", () => { location.href = "download.html"; }, false);
    }, 1500);
  };
  return el("a", { class: cls, href: installUrl(id, version), "aria-label": label + " (opens Desk Widgets)", onclick: check }, label);
}

// Characters that make code look different from what the C# compiler sees ("Trojan Source"):
// line breaks only the compiler honors (lone CR, U+0085, U+2028, U+2029), invisible characters and
// bidi controls. The gallery refuses them (tools/widget-rules.mjs) and so does the app
// (Plugins.IsHidden); the browser wouldn't show one truthfully, so if one ever got here it
// shows as a marker.
const HIDDEN = /\r(?!\n)|[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F\u200B-\u200F\u2028\u2029\u202A-\u202E\u2060-\u2069\uFEFF]/g;
const marker = ch => "⟨U+" + ch.charCodeAt(0).toString(16).toUpperCase().padStart(4, "0") + "⟩";

// The text with every hidden character replaced by a visible marker (for display only).
export const visibleSource = text => text.replace(HIDDEN, marker);

export function notice(text, error = false) {
  return el("p", { class: "notice" + (error ? " error" : ""), role: error ? "alert" : "status" }, text);
}

export function formatDate(iso) {
  return iso ? new Date(iso).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" }) : "";
}

export function confirmDialog(title, message, action, onConfirm, destructive = true) {
  const overlay = el("div", { class: "dialog-overlay" },
    el("div", { class: "dialog" },
      el("h3", {}, title),
      el("p", {}, message),
      el("div", { class: "actions" },
        el("button", { class: "button grey small", type: "button", onclick: () => overlay.classList.remove("open") }, "Cancel"),
        el("button", { class: destructive ? "button destructive small" : "button small", type: "button", onclick: async () => {
          overlay.classList.remove("open");
          await onConfirm();
        } }, action))));
  document.body.append(overlay);
  requestAnimationFrame(() => overlay.classList.add("open"));
  overlay.addEventListener("click", e => { if (e.target === overlay) overlay.classList.remove("open"); });
  return overlay;
}
