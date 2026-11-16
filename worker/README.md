# The upload Worker

A small Cloudflare Worker that lets someone publish a widget from the form on the website
(`docs/upload.html`) instead of making a pull request by hand. It signs the person in with GitHub
to learn their username, then opens the pull request for them from a branch named
`upload/<username>/<widget>`.

It has no database and stores nothing. It can only propose: merging is still the review, and its
pull requests go through the same checks as anyone's (`.github/workflows/widgets.yml`). If it is
ever down, publishing by pull request works exactly as before.

```
npm test                  # in this folder; needs nothing installed
npm install               # once, for the two commands below
npx wrangler dev          # run it locally on http://localhost:8787
npx wrangler deploy       # publish it
```

## Setting it up, once

### 1. Register the GitHub App

GitHub > Settings > Developer settings > GitHub Apps > New GitHub App.

| Field | Value |
|---|---|
| Name | anything free, e.g. `Desk Widgets Upload`. Its pull requests are opened by `<name in lower case with hyphens>[bot]` |
| Homepage URL | the website |
| Callback URL | `https://desk-widgets-upload.<your subdomain>.workers.dev/callback` (and `http://localhost:8787/callback` to test locally) |
| Expire user authorization tokens | on |
| Request user authorization (OAuth) during installation | off |
| Webhook | not active |
| Repository permissions | **Contents: Read and write**, **Pull requests: Read and write**. Nothing else; in particular not Workflows |
| Account permissions | none |
| Where can this app be installed | Only on this account |

Then, on the app's page: generate a **client secret**, generate a **private key** (a `.pem` file
downloads), and **install** the app on the `desk-widgets` repository only.

### 2. Fill in the settings

In `wrangler.toml`, set `GITHUB_APP_ID` and `GITHUB_CLIENT_ID` from the app's page. Neither is a
secret. The three secrets go straight to Cloudflare and are never written to a file here:

```
npx wrangler login
npx wrangler secret put GITHUB_CLIENT_SECRET
npx wrangler secret put GITHUB_PRIVATE_KEY < path\to\the-key.pem
npx wrangler secret put SESSION_SECRET          # any long random text
npx wrangler deploy
```

Delete the `.pem` file afterwards; a new one can be generated at any time.

### 3. Tell the repository about it

- `tools/widget-rules.mjs`: set `UPLOAD_BOT` to the bot's name, e.g. `desk-widgets-upload[bot]`.
  Until then the pull request check does not recognise the Worker's pull requests and refuses
  them.
- `docs/config.js`: set `UPLOAD_URL` to the Worker's address. Until then the upload page sends
  people to the pull request guide.

## What it is trusted with

The app's key can create branches and pull requests in the repository. It cannot push to `main`
(the ruleset requires a pull request), cannot change workflows (that permission is not granted),
and cannot merge. A person's GitHub token is used for one question, who are you, and dropped;
the Worker never acts as them and asks for no access to their account.

The pull request check trusts a pull request as coming from `<username>` only when the app's bot
opened it from an `upload/<username>/...` branch in this repository. Only the app can open a pull
request as its bot, so this cannot be imitated from a fork.

## Limits

- Three uploads waiting for review per GitHub account.
- The same size and content rules as a hand-made pull request (`tools/widget-rules.mjs`).
- Cloudflare's free plan allows 100,000 requests a day; an upload uses about ten.
