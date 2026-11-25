# Reporting the antivirus false positives

Every release, generic engines flag `DeskWidgets.exe` and `DeskWidgets-Setup.exe`. This is who
to tell and what to say. It will stay necessary until the binaries are code-signed. Issue #141 is
the report that prompted it.

Verify a URL before using it — these move.

## Where it stands

Measured on v1.10.1, the first build with a real version resource (#142). Both files sit at
**11 of 70**, down from 20/70 (`DeskWidgets.exe`) and 16/64 (`DeskWidgets-Setup.exe`) on v1.10.0.

On *both* files, 8 of the 11 are a single engine — Bitdefender's `Gen:Variant.MSILHeracles.278397`,
resold under ALYac, Arcabit, CTX, Emsisoft, eScan, GData and VIPRE. A VirusTotal row count is
never a vendor count.

The remaining three differ per file and are not worth chasing:

| | not Bitdefender |
|---|---|
| `DeskWidgets.exe` | McAfee `Ti!4FF57891E08E`, SecureAge, Trapmine |
| `DeskWidgets-Setup.exe` | Arctic Wolf, DeepInstinct, Skyhigh (SWG) |

McAfee's is not analysis: `Ti!` is their trust index and the suffix is the first 12 hex digits of
the file's own SHA-256, so it means "never seen this file" and clears as downloads accumulate.
v1.10.0 carried the same shape (`Ti!10A49AAAAB1E`, that build's hash prefix).

## Who to tell

**Bitdefender, and stop.** <https://www.bitdefender.com/submit> — if that form misbehaves, the
business one reaches the same lab: <https://www.bitdefender.com/business/submit>. Detection name
`Gen:Variant.MSILHeracles.278397`. One report, both files, 8 of 11 rows on each.

Do not file with anyone else. Microsoft, TrendMicro, Trellix, Sophos, Broadcom/Symantec, Palo
Alto, Elastic, Antiy and Lionic all flagged v1.10.0 and all read clean on v1.10.1 — Microsoft's
`Trojan:Win32/Wacatac.B!ml` among them, which is the SmartScreen path too. Reporting a file a lab
already passes wastes their time and yours.

If a later release is flagged more widely again, the old contact list is in the history of this
file, and VirusTotal keeps the canonical directory at
<https://docs.virustotal.com/docs/false-positive-contacts>.

## What to send

Attach both files from the release — not a local build, or the hashes won't match what they see.

```
https://github.com/whoissaaif/desk-widgets/releases/latest
```

v1.10.1 hashes, for the message body:

```
4ff57891e08e61620635192c2f443ac96a5f33cf89e46cf13ee9c7596f40877f  DeskWidgets.exe
05e2383321dfbef692587599a8cdf12c4c10279aaf1e587df97508aa36072403  DeskWidgets-Setup.exe
```

Re-run `sha256sum` on the release assets for any later version; do not copy these forward.

Take the hashes **after** the release is published, not from the draft. Publishing fires
`release.yml` a second time, which rebuilds from the same tag and replaces all three assets,
and csc output is not byte-reproducible, so the draft's hashes are not the ones anybody will
download. (Measured on v1.10.1: the draft exe was `4c881c9a...`, the published one
`4ff57891...`.)

## The message

Keep it short, name the detection, and link something they can read. Do not argue — state
what the binary is and what it does that looks bad, because the behaviour is real and they will
see it in the sandbox anyway.

> Desk Widgets is a free, open-source desktop widget app for Windows.
> Source: https://github.com/whoissaaif/desk-widgets
>
> Your engine detects it as <DETECTION NAME>. I believe this is a generic false positive.
>
> File: DeskWidgets-Setup.exe
> SHA-256: <hash>
> Version: <version>, from https://github.com/whoissaaif/desk-widgets/releases/latest
>
> The binaries are built on GitHub's runners from the public tag, by
> .github/workflows/release.yml. Nothing is uploaded from a personal machine, and the build is
> one command (build.bat).
>
> Three behaviours in the app are likely what scored it, and all three are documented features:
> it writes an HKCU Run entry when the user opts into "Start with Windows"; it updates itself by
> downloading the new exe and replacing the running one; and it compiles community widget source
> at runtime, after checking the source against a SHA-256 published in the repository and asking
> the user to confirm. SECURITY.md describes this.
>
> The binaries are not yet code-signed, which I understand is a large part of the score.
>
> Thank you for taking a look.

## After, and for the next release

Re-scan both files about a week later and note what moved. A freshly uploaded file has no
prevalence data and some cloud verdicts firm up over days, so the score on release day is not yet
the real one.

Then do the same on the next release: scan first, and report only to whoever is still flagging.
The list shrank from seven labs to one between v1.10.0 and v1.10.1 without a single report being
filed, so start from what the scan says rather than from this file.

None of this is a fix. A signing certificate is, and until there is one the Bitdefender family
will likely need telling again each release.
