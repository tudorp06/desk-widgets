// Talking to GitHub as the upload app.
//
// The app is installed on the one repository and may write branches and pull requests there,
// nothing else. To act, the Worker signs a short-lived JWT with the app's private key, trades
// it for an installation token narrowed to that repository, and keeps the token in memory
// until shortly before it expires.

import { base64url, fromBase64 } from "./session.mjs";

const enc = new TextEncoder();
const API = "https://api.github.com";
const AGENT = "desk-widgets-upload";

function derLength(n) {
  if (n < 128) return [n];
  const out = [];
  for (; n > 0; n = Math.floor(n / 256)) out.unshift(n % 256);
  return [0x80 | out.length, ...out];
}

// GitHub hands out the key as PKCS#1 ("BEGIN RSA PRIVATE KEY"); Web Crypto only reads PKCS#8.
// The second is the first inside a fixed envelope, so wrap it rather than ask whoever sets the
// Worker up to convert the file.
export function pkcs8FromPem(pem) {
  const text = String(pem || "").replace(/\\n/g, "\n");
  const der = fromBase64(text.replace(/-----[^-]+-----/g, ""));
  if (!/BEGIN RSA PRIVATE KEY/.test(text)) return der;
  const version = [0x02, 0x01, 0x00];
  const rsa = [0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00];
  const key = [0x04, ...derLength(der.length)];
  const length = version.length + rsa.length + key.length + der.length;
  return new Uint8Array([0x30, ...derLength(length), ...version, ...rsa, ...key, ...der]);
}

export async function appJwt(appId, pem, now = Math.floor(Date.now() / 1000)) {
  if (!appId || !pem) throw new Error("GITHUB_APP_ID or GITHUB_PRIVATE_KEY is not set");
  const key = await crypto.subtle.importKey("pkcs8", pkcs8FromPem(pem), { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const part = o => base64url(enc.encode(JSON.stringify(o)));
  // Issued a minute ago, so a clock that runs slightly ahead of GitHub's is not "in the future".
  const body = part({ alg: "RS256", typ: "JWT" }) + "." + part({ iat: now - 60, exp: now + 540, iss: String(appId) });
  const sig = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, enc.encode(body));
  return body + "." + base64url(new Uint8Array(sig));
}

// A failed call to GitHub. The message is for the log; the person is told only that GitHub
// did not answer as expected.
export class GitHubError extends Error {
  constructor(status, what) { super(`GitHub answered ${status} to ${what}`); this.status = status; }
}

// One call. Answers { status, data }; data is parsed JSON, or text when raw is set. A status
// listed in allow comes back to the caller, anything else that is not a success throws.
export async function github(token, method, path, { body, raw = false, allow = [] } = {}) {
  const res = await fetch(API + path, {
    method,
    headers: {
      "authorization": "Bearer " + token,
      "accept": raw ? "application/vnd.github.raw+json" : "application/vnd.github+json",
      "x-github-api-version": "2022-11-28",
      "user-agent": AGENT,
      ...(body ? { "content-type": "application/json" } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok && !allow.includes(res.status)) throw new GitHubError(res.status, `${method} ${path.split("?")[0]}`);
  const data = raw || !res.ok ? await res.text() : await res.json();
  return { status: res.status, data };
}

let cached = null;   // { token, until, repo }
export function forgetToken() { cached = null; }

export async function installationToken(env, now = Date.now()) {
  if (cached && cached.repo === env.REPO && cached.until > now) return cached.token;
  const jwt = await appJwt(env.GITHUB_APP_ID, env.GITHUB_PRIVATE_KEY);
  const found = await github(jwt, "GET", `/repos/${env.REPO}/installation`);
  const made = await github(jwt, "POST", `/app/installations/${found.data.id}/access_tokens`, {
    body: { repositories: [env.REPO.split("/")[1]], permissions: { contents: "write", pull_requests: "write" } },
  });
  cached = { token: made.data.token, repo: env.REPO, until: Date.parse(made.data.expires_at) - 5 * 60 * 1000 };
  return cached.token;
}

// Who a person is, from the code GitHub sent them back with. Their token is used for this one
// question and then dropped: the Worker never acts as them.
export async function whoSignedIn(env, code, redirect) {
  const res = await fetch("https://github.com/login/oauth/access_token", {
    method: "POST",
    headers: { "accept": "application/json", "content-type": "application/json", "user-agent": AGENT },
    body: JSON.stringify({ client_id: env.GITHUB_CLIENT_ID, client_secret: env.GITHUB_CLIENT_SECRET, code, redirect_uri: redirect }),
  });
  const grant = res.ok ? await res.json() : null;
  if (!grant || !grant.access_token) return null;
  const me = await github(grant.access_token, "GET", "/user", { allow: [401, 403] });
  return me.status === 200 && me.data.login && me.data.id ? { login: me.data.login, id: me.data.id } : null;
}
