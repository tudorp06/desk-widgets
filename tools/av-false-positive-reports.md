# Reporting the antivirus false positives

Every release, generic engines flag `DeskWidgets.exe` and `DeskWidgets-Setup.exe`. This is the
list of who to tell and what to say. It will stay necessary until the binaries are code-signed.
Issue #141 is the report that prompted it.

Verify a URL before using it — these move. VirusTotal keeps the canonical directory of vendor
contacts at <https://docs.virustotal.com/docs/false-positive-contacts>; prefer it over the links
below if they disagree.

## Who actually needs telling

A VirusTotal row count is not a vendor count. Most of the names resell someone else's engine, so
one report clears several rows:

| Report to | Clears |
|---|---|
| **Bitdefender** | Bitdefender, ALYac, Arcabit, CTX, Emsisoft, eScan, GData, VIPRE — 8 rows |
| **TrendMicro** | TrendMicro, TrendMicro-HouseCall |
| **Trellix** | Trellix ENS, McAfee Scanner |
| Microsoft | Microsoft (`Trojan:Win32/Wacatac.B!ml`) — also the SmartScreen path |
| Sophos | Sophos (`Mal/Generic-S`) |
| Broadcom/Symantec | Symantec (`ML.Attribute.HighConfidence`, `Trojan.MsilHrcls!ATN`) |
| Palo Alto Networks | Palo Alto (`Generic.ml`) |
| Elastic, DeepInstinct, SecureAge, Lionic, Antiy, Trapmine, Acronis | one row each; do these last |

Start with Bitdefender. It is a third of the score on its own.

## Where

- **Bitdefender** — <https://www.bitdefender.com/submit> (if that form misbehaves, the business
  one reaches the same lab: <https://www.bitdefender.com/business/submit>)
- **Microsoft** — <https://www.microsoft.com/wdsi/filesubmission?persona=SoftwareDeveloper>.
  Choose "Incorrect detection" and give the detection name. Expect slow, sometimes silent, handling.
- **TrendMicro** — <https://www.trendmicro.com/en_us/about/legal/detection-reevaluation.html>.
  Zip the file first; 12 MB limit; password-protect the archive and put the password in the
  details field.
- **Sophos** — submit through Sophos Intelix, or the sample submission form. Allow ~15 days.
- **Broadcom/Symantec** — <https://symsubmit.symantec.com>, the "Clean Software Incorrectly
  Detected" tile. No support case needed.
- **Palo Alto Networks** — report the verdict through the WildFire portal for your region. No
  support case needed.
- **Trellix** — check Trellix's current support site; the old McAfee submission page is gone.
- **Elastic** — raise it on `elastic/protections-artifacts` on GitHub.

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

## After

Re-scan on VirusTotal a week later and note what moved. Every new release needs the round again
until there is a signing certificate — which is the only thing that fixes this properly.
