// Signed tokens, and the byte helpers the rest of the Worker shares.
//
// The Worker keeps no database and no cookie the website could send (the site and the Worker
// are on different domains, so a browser treats such a cookie as third-party and drops it).
// Instead, signing in ends with a token the upload page holds for an hour and sends back in the
// Authorization header: the payload, a dot, and its HMAC-SHA256 under SESSION_SECRET.

const enc = new TextEncoder(), dec = new TextDecoder();

export function base64(bytes) {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}
export const base64url = bytes => base64(bytes).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
export function fromBase64(text) {
  const s = atob(text.replace(/-/g, "+").replace(/_/g, "/").replace(/\s+/g, ""));
  const bytes = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i);
  return bytes;
}

const hmacKey = secret => crypto.subtle.importKey("raw", enc.encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign", "verify"]);

// payload is a plain object; exp (seconds since 1970) is what makes it expire.
export async function sign(payload, secret) {
  if (!secret) throw new Error("SESSION_SECRET is not set");
  const body = base64url(enc.encode(JSON.stringify(payload)));
  const mac = await crypto.subtle.sign("HMAC", await hmacKey(secret), enc.encode(body));
  return body + "." + base64url(new Uint8Array(mac));
}

// The payload, or null when the token is not ours, was altered, or has expired.
export async function verify(token, secret, now = Date.now() / 1000) {
  if (!secret) throw new Error("SESSION_SECRET is not set");
  const parts = String(token || "").split(".");
  if (parts.length !== 2 || !parts[0] || !parts[1]) return null;
  try {
    const ok = await crypto.subtle.verify("HMAC", await hmacKey(secret), fromBase64(parts[1]), enc.encode(parts[0]));
    if (!ok) return null;
    const payload = JSON.parse(dec.decode(fromBase64(parts[0])));
    return payload && typeof payload.exp === "number" && payload.exp > now ? payload : null;
  } catch (e) { return null; }
}
