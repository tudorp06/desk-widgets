# Security

Desk Widgets runs community code on people's PCs, so security reports matter a lot here.

## Reporting a problem

Please report privately through
[GitHub's private vulnerability reporting](https://github.com/whoissaaif/desk-widgets/security/advisories/new),
not in a public issue. Say what's affected, how to reproduce it, and what an attacker could do.
You'll get an answer within a week. Please don't test against other people's accounts or data.

## What's in scope

- Getting code installed or run that the owner didn't approve, or that differs from what was
  reviewed (the pull request › review › `deskwidgets://` › hash check › compile chain)
- Getting past the pull request check (`.github/workflows/widgets.yml`): changing someone else's
  widget, or making the check run code from a pull request
- Script injection on the website (`docs/`)
- Tokens or passwords stored or sent unsafely by the app

A community widget that does something harmful *after* it was approved is a review problem, not a
vulnerability, but report it the same way so the owner can act on it.

## Antivirus false positives

`DeskWidgets.exe` and `DeskWidgets-Setup.exe` are not code-signed, and generic machine-learning
engines flag them. On VirusTotal that currently reads as roughly 16-20 of 70 vendors, under
names like `Gen:Variant.MSILHeracles`, `Trojan:Win32/Wacatac.B!ml` and `Generic.ml` - all
generic buckets, none of them a named family, and most of the hits are one engine seen several
times (Bitdefender's is resold by ALYac, Arcabit, CTX, Emsisoft, eScan, GData and VIPRE).

The app honestly does three things these models score against: it writes a `Run` key when you
ask it to start with Windows, it replaces its own exe when it updates, and it downloads and
compiles community widget source at runtime. Those are the product, not a disguise.

What you can check instead of trusting that:

- Every release asset is built by `.github/workflows/release.yml` on GitHub's runners, from the
  tag, from the source in this repository. Nothing is uploaded from a personal machine.
- The build is reproducible by you: `build.bat` is the whole command.
- The Microsoft Store build is the same code, signed by Microsoft, and is the quieter download
  if your antivirus is unhappy.

If you believe a detection is real rather than generic, report it as above rather than in a
public issue.

## How it's built to be safe

The gallery is the `widgets/` folder: a widget is published by merging a pull request, the app
installs only bytes whose SHA-256 is the one in the published gallery, and it always asks first.
See the comments at the top of `.github/workflows/widgets.yml` and `tools/widget-rules.mjs`.
"Threat model" in `design/community-plan.md` describes the earlier design, with a database.
