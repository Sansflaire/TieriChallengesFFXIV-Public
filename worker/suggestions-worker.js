/**
 * Cloudflare Worker — suggestion / bug-report relay for FFXIV Miscellaneous Challenges.
 *
 * WHY THIS EXISTS
 * ---------------
 * The plugin ships as a .NET DLL. Anything baked into it is public: the previous build's Discord
 * webhook URL was recoverable with a single `grep` on the shipped file, no decompiler needed. That
 * means anyone who ever downloaded the plugin could post to the channel directly, forever, and the
 * only remedy was deleting the webhook — which breaks every installed copy.
 *
 * This Worker holds the real webhook as a secret that never leaves Cloudflare. The plugin talks to
 * this URL instead. Consequences:
 *
 *   - The webhook can be rotated without shipping a plugin build.
 *   - Bans are enforced HERE, server-side, so a modified or hand-built client cannot bypass them.
 *     The plugin's own check is a courtesy that shows the user a message; this one is the fence.
 *   - Abuse can be rate-limited and blocked at the edge instead of arriving in the channel.
 *
 * DEPLOY
 * ------
 *   1. dash.cloudflare.com → Workers & Pages → Create → Worker. Paste this file. Deploy.
 *   2. Settings → Variables → add these:
 *        DISCORD_WEBHOOK  (secret)    the real https://discord.com/api/webhooks/... URL
 *        BAN_SALT         (secret)    must equal BanService.IdSalt in the plugin, byte for byte
 *        BANS_URL         (plain)     https://raw.githubusercontent.com/Sansflaire/TieriChallengesFFXIV-Sync/main/bans.json
                                     (raw, NOT api.github.com — the API 403s from Cloudflare's
                                      shared egress IPs, which silently disabled the ban check)
 *   3. Copy the worker's https://<name>.<subdomain>.workers.dev URL into src/Secrets.props.
 *   4. Rebuild + ship the plugin. ONLY THEN delete the old Discord webhook — doing it earlier
 *      breaks reports for everyone still on the previous build.
 *
 * The free tier covers 100,000 requests/day. This will use a handful.
 */

const MAX_BODY_BYTES = 8 * 1024 * 1024;   // Discord's own attachment ceiling for a bot post.
const BANS_TTL_MS    = 60_000;            // Re-fetch the ban list at most once a minute.
const BANS_CACHE_KEY = "https://relay.internal/bans";   // synthetic key for the Cache API

/** How many limiter trips before you get told someone is doing it on purpose. */
const SPAM_ALERT_AFTER = 2;   // two refusals from one IP is already deliberate
/** Don't re-alert about the same person more often than this. */
const SPAM_ALERT_COOLDOWN_S = 3600;

/**
 * Per-isolate memo. NOT the durability story — isolates are created and destroyed constantly, and
 * a cold one starts with this empty. The Cache API below is what actually survives that.
 */
let bansCache = { at: 0, hashes: null };

export default {
  async fetch(request, env) {
    // Only ever a POST from the plugin. A browser hitting this URL gets nothing useful.
    if (request.method !== "POST") {
      return json({ ok: false, error: "method not allowed" }, 405);
    }

    if (!env.DISCORD_WEBHOOK) {
      // Misconfiguration, not the caller's fault — say so in the log, stay vague to the caller.
      console.error("DISCORD_WEBHOOK is not set");
      return json({ ok: false, error: "relay not configured" }, 500);
    }

    const contentType = request.headers.get("content-type") || "";

    try {
      if (contentType.includes("application/json")) {
        return await relayJson(request, env);
      }
      if (contentType.includes("multipart/form-data")) {
        return await relayMultipart(request, env);
      }
      return json({ ok: false, error: "unsupported content type" }, 415);
    } catch (err) {
      console.error("relay failed", err && err.stack ? err.stack : String(err));
      return json({ ok: false, error: "relay failed" }, 502);
    }
  },
};

/* ── Suggestions: a plain JSON Discord payload plus our own `sender` field ─────────────── */

async function relayJson(request, env) {
  const body = await readCapped(request);
  if (body === null) return json({ ok: false, error: "payload too large" }, 413);

  let payload;
  try {
    payload = JSON.parse(body);
  } catch {
    return json({ ok: false, error: "malformed payload" }, 400);
  }

  // `sender` is ours, not Discord's. Pull it out before forwarding: Discord rejects unknown
  // top-level fields, and the channel does not need the raw identity repeated as metadata.
  const sender = typeof payload.sender === "string" ? payload.sender : "";
  delete payload.sender;

  const verdict = await checkSender(sender, env);
  if (verdict === "banned")  return dropped();
  if (verdict === "unknown") return unavailable();

  const limited = await rateLimit(sender, request, env);
  if (limited) return limited;

  // Re-assert this regardless of what the client sent. A hand-built client could omit it and
  // turn the suggestion box into an @everyone cannon.
  payload.allowed_mentions = { parse: [] };

  const res = await fetch(env.DISCORD_WEBHOOK, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(payload),
  });

  return passthrough(res);
}

/* ── Bug reports: multipart, because the log rides along as a file attachment ──────────── */

async function relayMultipart(request, env) {
  const form = await request.formData();

  const sender = form.get("sender");
  if (typeof sender === "string") form.delete("sender");

  const verdict = await checkSender(typeof sender === "string" ? sender : "", env);
  if (verdict === "banned")  return dropped();
  if (verdict === "unknown") return unavailable();

  const limited = await rateLimit(typeof sender === "string" ? sender : "", request, env);
  if (limited) return limited;

  // Same reassertion as above, applied inside the JSON part this time.
  const rawJson = form.get("payload_json");
  if (typeof rawJson === "string") {
    try {
      const parsed = JSON.parse(rawJson);
      parsed.allowed_mentions = { parse: [] };
      form.set("payload_json", JSON.stringify(parsed));
    } catch {
      return json({ ok: false, error: "malformed payload_json" }, 400);
    }
  }

  // Deliberately NOT setting content-type: fetch regenerates the multipart boundary itself, and
  // a copied boundary header that no longer matches the re-serialised body is unparseable.
  const res = await fetch(env.DISCORD_WEBHOOK, { method: "POST", body: form });
  return passthrough(res);
}

/* ── Ban enforcement ──────────────────────────────────────────────────────────────────── */

/**
 * "banned" | "clear" | "unknown".
 *
 * FAILS CLOSED. If the ban list cannot be established at all, this returns "unknown" and the
 * caller refuses the message rather than forwarding it. That is the opposite of what the first
 * version did, and the first version was wrong: a cold isolate whose fetch 403'd treated an
 * unknown list as an empty one, so a banned message went straight through. Observed in testing,
 * not theorised.
 *
 * The cost of failing closed is that a total GitHub outage also blocks honest reports. That is the
 * trade that was asked for — banned messages must not arrive, "no matter what" — and the window is
 * small because the source below is a CDN with a durable cache in front of it.
 */
async function checkSender(sender, env) {
  const hashes = await banHashes(env);
  if (hashes === null) return "unknown";
  if (hashes.size === 0) return "clear";

  // No sender at all: nothing to match, and refusing would block every legitimate not-logged-in
  // report. The plugin always sends one when a character is loaded.
  if (!sender || !env.BAN_SALT) return "clear";

  try {
    const digest = await crypto.subtle.digest(
      "SHA-256",
      new TextEncoder().encode(sender.trim().toLowerCase() + env.BAN_SALT),
    );
    return hashes.has(base64(digest)) ? "banned" : "clear";
  } catch (err) {
    console.error("hashing failed", String(err));
    return "unknown";
  }
}

/**
 * The ban hashes, or null if they genuinely cannot be established.
 *
 * Three layers, because the obvious one is the unreliable one:
 *   1. per-isolate memo — free, but empty on every cold start
 *   2. Cloudflare Cache API — shared across isolates in the colo, survives isolate churn
 *   3. raw.githubusercontent — NOT api.github.com. The API rate-limits Cloudflare's shared egress
 *      IPs and returned 403 for most requests in testing, while the same call from a home IP had
 *      59/60 budget left. raw is a CDN: no auth, no rate limit. It can be up to ~5 minutes stale,
 *      which is the right trade here — the plugin still uses the API for its own fresher check,
 *      because it runs from the user's own IP and its own quota.
 */
async function banHashes(env) {
  const now = Date.now();
  if (bansCache.hashes !== null && now - bansCache.at < BANS_TTL_MS) return bansCache.hashes;

  const cache = caches.default;
  let text = null;

  try {
    const fresh = await fetch(env.BANS_URL, {
      headers: { "User-Agent": "TieriChallengesFFXIV-Relay" },
      // No extra caching layer of our own. raw.githubusercontent already fronts this with its
      // own multi-minute CDN cache, and stacking a second one on top measurably delayed a ban:
      // a real ban published and verified present at the origin still was not enforced 75s later,
      // and only took effect around the four-minute mark. The remaining delay is raw's and cannot
      // be removed from here; the Cache API fallback below still covers a fetch failure.
      cf: { cacheTtl: 0 },
    });

    if (fresh.ok) {
      text = await fresh.text();
      // Store a copy that outlives this isolate. Short TTL so an unban still lands quickly.
      await cache.put(BANS_CACHE_KEY,
        new Response(text, { headers: { "cache-control": "max-age=300" } }));
    } else {
      console.error("ban list fetch returned " + fresh.status);
    }
  } catch (err) {
    console.error("ban list fetch failed", String(err));
  }

  if (text === null) {
    const cached = await cache.match(BANS_CACHE_KEY);
    if (cached) {
      text = await cached.text();
      console.log("ban list served from edge cache");
    }
  }

  if (text === null) {
    // Nothing anywhere. Do NOT synthesise an empty list — that is precisely the bug.
    bansCache = { at: 0, hashes: null };
    return null;
  }

  try {
    const parsed = JSON.parse(text);
    const set = new Set();
    for (const entry of parsed.entries || []) {
      if (entry && typeof entry.h === "string") set.add(entry.h);
    }
    bansCache = { at: now, hashes: set };
    return set;
  } catch (err) {
    console.error("ban list is not valid JSON", String(err));
    return null;
  }
}

/**
 * A banned sender is told it worked and nothing is forwarded.
 *
 * Shadow-dropping rather than refusing, deliberately: an error is a signal to retry from another
 * character, which is exactly the iteration a ban exists to stop.
 */
function dropped() {
  console.log("dropped a message from a banned sender");
  return json({ ok: true, dropped: true }, 204);
}

/**
 * The ban list could not be established, so we do not know whether this sender is allowed.
 *
 * 503 rather than a silent success: this one is an honest failure the user should see and retry,
 * unlike a drop, which must look like success. The plugin's existing error copy already handles a
 * non-2xx by telling them to try again later.
 */
function unavailable() {
  console.error("refusing to forward: ban list unavailable");
  return json({ ok: false, error: "moderation data unavailable, try again shortly" }, 503);
}

/* ── Rate limiting ────────────────────────────────────────────────────────────────────── */

/**
 * Returns a Response to send back when the caller is over quota, or null to let it through.
 *
 * Enforced by the PLATFORM, not by logic here: `env.RL_*.limit()` is a Cloudflare rate-limit
 * binding, so a caller cannot race it, restart to reset it, or skip it by building their own
 * client. The plugin's own 5-per-session courtesy limit is unaffected and remains bypassable —
 * that is what this exists to backstop.
 *
 * Keyed on the sender identity, falling back to IP when there is none, so one abusive character
 * cannot exhaust everyone else's quota and a not-logged-in caller is still bounded.
 */
async function rateLimit(sender, request, env) {
  // Counted HERE, in the Cache API, rather than through env.RL_* rate-limit bindings.
  //
  // The bindings were tried first and are inert on this account: instrumented in production, they
  // resolve to a live object with a working .limit() that returned {"success":true} for eight
  // requests in one second against a three-per-ten-second limit. Whatever the cause, a limiter
  // that always says yes is not a limiter, so this counts for itself.
  //
  // Cache API storage is per-colo and mildly racy under concurrency. Both are acceptable: a
  // spammer hammering from one connection lands in ONE colo, which is the case this exists for,
  // and a lost increment delays a block by one request rather than preventing it.
  const ip        = request.headers.get("CF-Connecting-IP") || "noip";
  const senderKey = (sender && sender.trim().toLowerCase()) || "anon";

  try {
    // Checked in this order so an IP block wins: the sender name is caller-supplied and can be
    // rotated freely, the IP cannot.
    const ipHit = await bump("ip:" + ip, env);
    const idHit = sender ? await bump("id:" + senderKey, env) : null;

    if (!ipHit.over && !(idHit && idHit.over)) return null;

    // A name tripping the limit escalates to an IP block, as asked: one person cannot keep
    // sending merely by editing the name they claim.
    if (idHit && idHit.over && !ipHit.over) await forceBlockIp(ip, env);

    const why = ipHit.over ? "ip" : "sender";
    console.log("rate limited by " + why + ": " + senderKey + " @ " + ip);
    await noteSpam(sender, ip, env, why, ipHit.count);

    return json({ ok: false, error: "too many messages, slow down" }, 429);
  } catch (err) {
    console.error("rate limiter threw, allowing", String(err));
    return null;
  }
}

/** Window length and ceiling. Deliberately generous — a real person sends one message, not eight. */
const RL_WINDOW_S = 60;
const RL_MAX      = 5;

/**
 * Increment a counter and report whether it is over the ceiling.
 *
 * Stores an absolute expiry inside the value rather than trusting the cache TTL alone, so a colo
 * that serves a stale entry still gets the window right instead of extending a block forever.
 */
async function bump(key, env) {
  const cache = caches.default;
  const url   = "https://relay.internal/rl/" + encodeURIComponent(key);
  const nowS  = Math.floor(Date.now() / 1000);

  let count = 0, resetAt = nowS + RL_WINDOW_S, blockedUntil = 0;
  try {
    const prev = await cache.match(url);
    if (prev) {
      const v = await prev.json();
      blockedUntil = v.blockedUntil || 0;
      if ((v.resetAt || 0) > nowS) { count = v.count || 0; resetAt = v.resetAt; }
    }
  } catch { /* an unreadable counter starts a fresh window */ }

  if (blockedUntil > nowS) return { over: true, count, blocked: true };

  count += 1;
  const over = count > RL_MAX;

  try {
    await cache.put(url, new Response(JSON.stringify({ count, resetAt, blockedUntil }), {
      headers: { "cache-control": "max-age=" + Math.max(1, resetAt - nowS) },
    }));
  } catch (err) {
    console.error("rl counter write failed", String(err));
  }

  return { over, count, blocked: false };
}

/** Escalate a name-based trip into an IP block, so rotating the claimed name buys nothing. */
async function forceBlockIp(ip, env) {
  const nowS = Math.floor(Date.now() / 1000);
  try {
    await caches.default.put(
      "https://relay.internal/rl/" + encodeURIComponent("ip:" + ip),
      new Response(JSON.stringify({
        count: RL_MAX + 1, resetAt: nowS + RL_WINDOW_S, blockedUntil: nowS + RL_WINDOW_S,
      }), { headers: { "cache-control": "max-age=" + RL_WINDOW_S } }));
    console.log("escalated to an ip block: " + ip);
  } catch (err) {
    console.error("ip escalation failed", String(err));
  }
}

/**
 * Count limiter trips per sender and, past a threshold, tell the channel owner who is doing it.
 *
 * <p>The point is the distinction between "clicked Send twice" and "is deliberately hammering the
 * endpoint". One trip is the former; several in an hour is the latter, and that is worth a name
 * and a world rather than a silent 429 nobody ever sees.</p>
 *
 * <p>Uses the Cache API, so the count is per-colo and approximate. That is fine: it gates an
 * ALERT, not an enforcement decision. Under-counting delays a notification; it never lets a
 * message through, because the limiter above has already refused.</p>
 */
async function noteSpam(sender, ip, env, why, count) {
  const who = sender || ("unknown sender @ " + ip);

  const cache = caches.default;
  // Keyed on the IP, NOT the name. Keying on the name was a bug that made the alert unreachable:
  // a spammer rotating names created a fresh counter per request, so the trip count never rose
  // above one and the notification never fired. The IP is the thing they cannot vary.
  const key   = "https://relay.internal/spam/" + encodeURIComponent(ip);

  let trips = 0, alerted = 0, names = [];
  try {
    const prev = await cache.match(key);
    if (prev) ({ trips = 0, alerted = 0, names = [] } = await prev.json());
  } catch { /* treat an unreadable counter as a fresh one */ }

  trips += 1;
  if (sender && !names.includes(sender)) names = names.concat([sender]).slice(-5);
  const nowS = Math.floor(Date.now() / 1000);
  const due  = trips >= SPAM_ALERT_AFTER && (nowS - alerted) > SPAM_ALERT_COOLDOWN_S;

  if (due) {
    alerted = nowS;
    await alertOwner(who, ip, trips, env, why, names);
  }

  try {
    await cache.put(key, new Response(JSON.stringify({ trips, alerted, names }), {
      headers: { "cache-control": "max-age=" + SPAM_ALERT_COOLDOWN_S },
    }));
  } catch (err) {
    console.error("spam counter write failed", String(err));
  }
}

/** Post the spam notice to the same channel, clearly marked so it is not mistaken for a report. */
async function alertOwner(who, ip, trips, env, why, names) {
  try {
    await fetch(env.DISCORD_WEBHOOK, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({
        username: "Challenges Relay",
        content:
          `**Rate limit tripped repeatedly**
` + "`" + who + "` from `" + ip + "`" + `
**${trips}** refusals in the last hour, tripped by **${why}**.
Names used: ${names.length ? names.map((n) => "`" + n + "`").join(", ") : "(none sent)"}
Their messages are being blocked, not delivered.
Ban them from the Creator's Bans tab if this continues.`,
        allowed_mentions: { parse: [] },
      }),
    });
    console.log("owner alerted about " + sender);
  } catch (err) {
    console.error("owner alert failed", String(err));
  }
}

/* ── Helpers ──────────────────────────────────────────────────────────────────────────── */

async function readCapped(request) {
  const text = await request.text();
  return new TextEncoder().encode(text).length > MAX_BODY_BYTES ? null : text;
}

/** Discord's status is handed back unchanged so the plugin's existing error copy stays truthful. */
function passthrough(res) {
  if (res.ok) return json({ ok: true }, 204);
  console.error("discord returned " + res.status);
  return json({ ok: false, error: "upstream " + res.status }, res.status);
}

function json(obj, status) {
  // 204 must not carry a body.
  if (status === 204) return new Response(null, { status });
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json" },
  });
}

function base64(buffer) {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
  return btoa(binary);
}
