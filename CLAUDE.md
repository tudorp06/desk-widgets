# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & test

```
build.bat                              # builds bin\DeskWidgets.exe (C# 5, .NET Framework 4, no NuGet)
bin\DeskWidgets.exe                    # run (adds itself to Start menu on first launch)
bin\DeskWidgets.exe --background       # launch without opening the library window (Start with Windows mode)
node tools\build-widget-pages.mjs      # builds the gallery (docs\w) from widgets\, and checks every widget
```

There is no solution/csproj file. `build.bat` calls the .NET Framework csc.exe that ships with
Windows and compiles `/recurse:src\*.cs` into one exe. If the exe is locked (app is running), the
build fails on the output file, not on a compile error — kill the process first.

For the website, serve `docs/` locally (e.g. `python -m http.server -d docs`) and check both
light and dark themes.

## Releases

Cutting one, so the release is complete the moment it goes public:

```
tools\set-version.ps1 1.8.8                               # then commit and merge to main
git fetch origin && git push origin origin/main:refs/tags/v1.8.8
gh release create v1.8.8 --draft --verify-tag --title v1.8.8 --notes "..."
gh workflow run release.yml -f tag=v1.8.8                 # builds the assets, attaches them to the draft
gh release edit v1.8.8 --draft=false --latest             # once that run is green
```

The tag is pushed and the workflow started by hand because GitHub does neither for a draft: a
draft has no tag until it is published, and it fires no release event, so nothing builds on its
own. Publishing does fire the workflow, which rebuilds from the same tag and replaces the three
assets with equivalent ones; that second run is expected. (Publishing a release in one go, with
no draft, also works, but the download links point at a release with no files for the few
minutes the build takes.)

The workflow refuses a tag that does not match the version in the files, and a package whose
identity or version is wrong, so a hand-built or test package cannot reach a release.

Every GitHub release carries all four of these, built from the same commit:

| Asset | Built by |
|---|---|
| `DeskWidgets.exe` | `build.bat` |
| `DeskWidgets-Setup.exe` | Inno Setup on `packaging\installer.iss` (outputs to `bin\`) |
| `DeskWidgets.msix` | `packaging\build-msix.ps1` |
| Source code (zip) | attached by GitHub automatically |

`DeskWidgets.exe` and `DeskWidgets-Setup.exe` must keep those exact names: the website's download
page links `releases/latest/download/<name>` for both (`docs/config.js`), and renaming either
asset breaks it.

**One version everywhere.** The tag is three parts with a `v` in front, e.g. `v1.8.6`, matching
every release before it. The MSIX version is that number plus a trailing `.0` (`1.8.6.0`): the
Store requires four parts with the last one `0`, and each uploaded package must be higher than the
last. The same version is declared in three files and they must agree — they have silently
drifted before:

- `packaging\AppxManifest.xml` — the tag's number plus `.0` (`1.8.6.0`)
- `packaging\installer.iss` — `MyAppVersion`
- `src\AppUpdater.cs` — `AppVersion`

The Store submission stays manual on purpose: download the msix from the release and upload it in
Partner Center. Before uploading any package there, check the identity inside it — every build is
about the same size, so a test build and a real one are indistinguishable by name or size (the
release workflow runs this check too):

```
python -c "import zipfile,re; m=zipfile.ZipFile('bin/DeskWidgets.msix').read('AppxManifest.xml').decode('utf-8'); print(re.search(r'<Identity[^>]*>',m).group(0))"
```

Pre-commit hook: `git config core.hooksPath .githooks` (runs `tools\check-hidden.ps1` to reject
characters that make code read differently from how it compiles).

## Language constraints

**C# 5 only.** The compiler is the one built into Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).
No string interpolation (`$"..."`), null-conditional (`?.`), `nameof`, expression-bodied members
(`=>`), or anything from C# 6+. No NuGet — the exe has zero dependencies.

## Design rules

One design language for everything: `design/apple-design.md`. Read it before changing any UI
(widgets, the library window, dialogs, right-click menus, the website or design mockups) and
follow it. Section 17 maps it onto this app; when a change needs a rule that isn't there, add it
to section 17 in the same change. In code, colors, type and shape come from `src/Theme.cs`
(widgets: the palettes in `CardWidget`), never from hex values scattered in UI code.
`design/DESIGN.md` is retired; don't follow it or add rules to it.

Only the window title bar and scrollbars are drawn by Windows. Everything else, right-click menus
included, is drawn by the app in the Apple style.

## Architecture

### The app (`src/`)

Single-process, single-exe WinForms app. No designer files — all UI is hand-coded GDI+.

- **`App.cs`** — `ApplicationContext` subclass; owns the tray icon, the widget list, wallpaper
  watcher, calendar events, session lock/unlock. The singleton `App.Current` is the backbone.
- **`WidgetWindow.cs`** — public abstract base for every widget. Per-pixel-alpha layered window
  (`UpdateLayeredWindow`) pinned to the desktop z-order via Progman ownership. Handles
  drag-to-move, right-click menus, render loop, DPI scaling. Community plugins derive from this.
- **`CardWidget.cs`** (in `DeskWidgets.Widgets` namespace) — abstract base for Apple-style card
  widgets. Provides Dark/Light/Vibrant palettes, small/medium/large sizes, frosted backdrop,
  font caching, emoji rendering, drawing helpers. Community widgets extend this.
- **`WidgetRegistry.cs`** — static catalog of `WidgetType` entries grouped by `WidgetCategory`.
  The only built-in widget is the analog clock; everything else is a community widget
  installed from the gallery. Community widgets are added/removed at runtime via
  `Register`/`Unregister`.
- **`LibraryWindow.cs`** — the main app window (sidebar + grid of cards). Only exists while open.
  Also defines the painted controls: `SidebarItem`, `WidgetCard`, `ExploreCard`, `PluginRow`, etc.
- **`Plugins.cs`** — community widget lifecycle: compile `.cs` against the running exe, hash-check
  against the reviewed version, crash isolation via a marker file. Installed under
  `%LOCALAPPDATA%\DeskWidgets\plugins\<id>\`.
- **`Community.cs`** — reads the gallery from files on the website (`w/gallery.json`, and each
  widget's `source.cs` and `preview.png`), keeps previews on disk, installs the published version. Handles `deskwidgets://install/<id>/<version>` URLs.
- **`PluginApi.cs`** — the public API surface that community widgets compile against (API v3):
  this file plus `WidgetWindow`, `CardWidget`, `Section`, `PopupMenu`, `InputDialog`, `Http`,
  `CalEvent`, `Ics`, `MediaSession`, `F1`, `F1LiveFeed`. Treat as frozen; changing a public
  signature breaks every installed widget. **Adding** to it bumps `PluginApi.Version`, and the
  widget that uses the addition declares that `api_version` when it is published — otherwise an
  older app passes the check in `Community.Confirm` and the person sees a compiler error instead
  of "needs a newer Desk Widgets".
- **`DesktopBackdrop.cs`** — keeps one 1/12-scale copy of the wallpaper; vibrant widgets sample
  a piece of it. Rebuilds asynchronously off the UI thread when the wallpaper changes.
- **`Theme.cs`** — all colors, fonts and shapes for the app's own UI (library, dialogs, menus).
  Follows Windows light/dark. Widgets have separate palettes in `CardWidget`.
- **`Config.cs`** — INI-format settings; `Section` is the public per-widget settings bag.
- **`PopupMenu.cs`** — app-drawn context menus (Apple-style, not native Win32 menus).

### Rendering model

Widgets are `Form` subclasses with `WS_EX_LAYERED`. Each has a `LayeredSurface` (a single
DIB section). `Draw()` paints into it, then `UpdateLayeredWindow` composits it onto the desktop.
No per-frame allocations. Widgets redraw only when something changes; drawing pauses while the
PC is locked. Slow work (network, disk, image decoding) runs on background threads.

### Community widget compile path

`Plugins.Build()` → `CSharpCodeProvider` (the .NET Framework csc) → DLL bytes → `Assembly.Load`
from bytes (no file lock) → find the `[DeskWidget]` class → compiled delegate for its constructor.
The source's SHA-256 must match the reviewed hash from the gallery.

### Website (`docs/`)

Static site served by GitHub Pages from `main`. `docs/assets/site.js` has the shared JS;
individual pages (explore, widget, profile, develop, etc.) are self-contained HTML. The site
has no accounts to create and no database: `docs/assets/data.js` reads `docs/w/gallery.json`
and the files beside it. `account.html`, `admin.html`, `login.html` and `password.html` are
redirects to the publishing guide (`develop.html#publish`), kept because older copies of the
app and old links still open them. `upload.html` is the upload page (below), and behaves like
those redirects while `UPLOAD_URL` in `docs/config.js` is empty.

The gallery is in the repository, not in a database. Every community widget lives in
`widgets/<author>/<slug>/` (`widget.cs`, `preview.png`, `meta.json`; one `author.json` per
author) and its id is `u.<author>.<slug>`. `tools/build-widget-pages.mjs` builds `docs/w/` from
that folder: `gallery.json` (every widget with the SHA-256 of its source, and every author),
`previews.json`, each author's GitHub picture (`avatars/`, so visitors never contact GitHub),
and per widget its page, `preview.png` and `source.cs`. `docs/w/` is generated
and not committed: `.github/workflows/pages.yml` builds it on every publish, and before serving
`docs/` locally you run the script once. `widgets/**/*.cs` is never line-ending converted
(`.gitattributes`), because the hash is taken from the file's bytes.

A widget is added or updated by pull request, and merging it is the review. The rules for a
widget are in `tools/widget-rules.mjs`, used by both the gallery build and the pull request
check (`.github/workflows/widgets.yml`): a contributor changes only the author folder whose
`author.json` names their GitHub account, `version` goes up by one exactly when `widget.cs`
changes, and the widget compiles against the app built from `main`. That workflow runs on
`pull_request_target` and treats the pull request as data; never add a step to it that checks
out or runs a file from the pull request.

### Upload Worker (`worker/`)

The one server in the project: a Cloudflare Worker that lets someone publish from
`docs/upload.html` instead of making the pull request by hand. It signs them in with GitHub to
learn their username, then, as a GitHub App installed on this repository, commits their files
to `upload/<username>/<widget>` and opens the pull request. It has no database, never acts as
the person, and can only propose: its pull requests get the same checks and the same review.
`worker/README.md` has the setup; `tools/widget-rules.mjs` is shared by the Worker, the upload
page (copied to `docs/w/` by the gallery build) and the pull request check, so it must stay
free of Node-only calls.

```
cd worker && node --test          # the Worker's tests; nothing to install
node worker/dev/server.mjs        # the real Worker code against a pretend GitHub, for trying upload.html locally
```

The pull request check takes the author from the branch name only when the app's bot
(`UPLOAD_BOT` in `tools/widget-rules.mjs`) opened the pull request from a branch in this
repository (`pullRequestOwner`). Do not loosen either half of that test: the bot's name alone
can be imitated by another app, and the branch name alone by anyone with a fork.

The app reads the same files (`src/Community.cs`). It has no account and sends nothing about
what was installed, so there are no likes or install counts. The Worker is the only server, and
the gallery does not depend on it: with it down, publishing by pull request works as before.
Copies of the app older than 1.9 read a Supabase project that is no longer part of this
repository; they lose Explore when it is deleted, and nothing here can change that.

## Performance expectations

~33 MB memory, ~0.3% CPU with seven widgets on-screen. Measure before and after anything that
runs continuously. Widgets that animate should check `IsCovered()` and skip frames when hidden
behind other windows.

## Widget sizing

Apple's widget specs: small 170 x 170 pt, medium 364 x 170 pt, large 364 x 382 pt. 22 pt corner
radius, 16 pt margins. Sizes scale with DPI via `P(pt)` in CardWidget or `Px(logical)` in
WidgetWindow.

## Widgets

The only built-in widget is the analog clock (`src/Widgets/AnalogClock.cs`). All other widgets
(reminders, weather, GitHub, etc.) are community widgets — users install them from the gallery
on the community page. Their source lives in `widgets/` and is not compiled into the exe.

## Settings & data paths

- Settings: `%APPDATA%\DeskWidgets\widgets.ini` (or `widgets.ini` next to the exe for portable mode).
- Plugin source + DLLs: `%LOCALAPPDATA%\DeskWidgets\plugins\<id>\`.
- Preview cache: `%LOCALAPPDATA%\DeskWidgets\previews\`.
- Widget caches (GitHub, weather): `%APPDATA%\DeskWidgets\cache\`.

