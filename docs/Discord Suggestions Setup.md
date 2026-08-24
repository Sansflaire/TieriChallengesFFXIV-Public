# Discord Suggestions — Setup

How the in-plugin **Suggest** button reaches your Discord channel, how it is configured, and the
one security decision you need to make.

---

## 1. The security problem, stated plainly

The endpoint the plugin posts to is **compiled into the DLL**. Anyone who installs the plugin can
extract it in seconds:

```powershell
# this is all an attacker has to do
Select-String -Path TieriChallengesFFXIV.dll -Pattern 'https://discord\.com/api/webhooks/\S+'
```

Once someone has your raw webhook URL they can post to that channel directly, forever, at any
rate they like, with no involvement from the plugin at all. **Your only remedy is deleting the
webhook — which silently breaks the Suggest button for every copy already installed**, until you
ship a new build and every user updates.

There are two ways to configure this. Both work today; they differ in what happens on a bad day.

| | Raw webhook (current) | Worker proxy (recommended) |
|---|---|---|
| Setup time | 2 minutes | ~15 minutes |
| Works today | ✅ | ✅ |
| Secret in the shipped DLL | the real webhook | a URL you can revoke |
| If abused | delete webhook → button dies for all installed users until they update | block/rate-limit at the Worker; users unaffected |
| Rotate the webhook | requires a new plugin release | change one Worker secret, no release |
| Server-side rate limiting | none possible | yes |

**Current state:** configured with the raw webhook, because that is what was supplied. It works.
Switching to the proxy later is a one-line change to `src/Secrets.props` and a rebuild — no code
changes.

---

## 2. Where the endpoint lives

It is **not** a literal in source, so it never enters git history.

```
src/Secrets.props        <-- gitignored. Holds <SuggestionEndpoint>.
        │
        │  imported by the csproj, baked in as an AssemblyMetadata attribute at build time
        ▼
TieriChallengesFFXIV.dll <-- SuggestionService reads it back from assembly metadata
```

- `src/Secrets.props` is listed in `.gitignore`. **Never commit it.**
- A fresh clone has no `Secrets.props`. The build still succeeds; the endpoint resolves to empty,
  `SuggestionService.IsConfigured` is false, and the Suggest button simply does not appear.
  Shipping a dead button would be worse than shipping none.

To change the endpoint: edit `src/Secrets.props`, rebuild. That is the whole procedure.

---

## 3. Option A — raw Discord webhook (what is configured now)

1. In Discord: **Server Settings → Integrations → Webhooks → New Webhook**.
2. Pick the channel, name it, **Copy Webhook URL**.
3. Put it in `src/Secrets.props` as `<SuggestionEndpoint>`.
4. Rebuild.

If the channel starts getting spam, your only option is deleting that webhook and creating a new
one — then shipping a new plugin build. Plan for Option B before that happens.

---

## 4. Option B — Cloudflare Worker proxy (recommended)

The plugin talks to the Worker; the Worker holds the real webhook as a secret and forwards. The
shipped DLL never contains the webhook.

**Steps**

1. Sign in at <https://dash.cloudflare.com> → **Workers & Pages** → **Create Worker**. The free
   tier is far beyond what this needs (100k requests/day).
2. Name it something unremarkable, e.g. `challenges-suggestions`. Deploy the placeholder.
3. **Edit code** and paste the Worker below.
4. **Settings → Variables and Secrets → Add secret**
   Name `DISCORD_WEBHOOK`, value = your real webhook URL. Secrets are not visible after saving
   and are not in the Worker source.
5. Deploy. Copy the Worker URL (`https://challenges-suggestions.<you>.workers.dev`).
6. Put **that** URL in `src/Secrets.props`. Rebuild.
7. **Delete and recreate the Discord webhook** afterwards, since the old one has been in a chat
   log and in a local file. Update only the Worker secret — no plugin release needed.

**Worker source**

```js
export default {
  async fetch(request, env, ctx) {
    if (request.method !== 'POST') {
      return new Response('Method not allowed', { status: 405 });
    }

    // Crude per-IP rate limit. Requires a KV namespace bound as RATE (optional but recommended:
    // Settings -> Bindings -> KV namespace -> variable name RATE).
    const ip = request.headers.get('CF-Connecting-IP') || 'unknown';
    if (env.RATE) {
      const key = `rl:${ip}`;
      const hits = parseInt((await env.RATE.get(key)) || '0', 10);
      if (hits >= 10) {
        return new Response('Rate limited', { status: 429 });
      }
      // 1 hour window
      await env.RATE.put(key, String(hits + 1), { expirationTtl: 3600 });
    }

    let body;
    try {
      body = await request.json();
    } catch {
      return new Response('Bad JSON', { status: 400 });
    }

    const content = typeof body.content === 'string' ? body.content.slice(0, 1800) : '';
    if (content.trim().length < 10) {
      return new Response('Too short', { status: 400 });
    }

    const forward = await fetch(env.DISCORD_WEBHOOK, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username: 'Challenges Suggestions',
        content,
        allowed_mentions: { parse: [] },   // never ping anyone
      }),
    });

    return new Response(forward.ok ? 'ok' : 'upstream error',
                        { status: forward.ok ? 200 : 502 });
  },
};
```

The plugin already sends exactly the shape this expects (`{ username, content, allowed_mentions }`),
so no plugin change is needed to switch — only the URL in `Secrets.props`.

---

## 5. What actually gets sent

Nothing is transmitted until the player presses **Send**. The payload is:

| Field | Included | Notes |
|-------|----------|-------|
| The message they typed | always | trimmed, capped at 1500 chars |
| Plugin version + build flavour | always | e.g. `v0.62.2.0 beta · 62% to 1.0`, `public build` |
| Contact handle | only if they type one | optional field, capped at 100 chars |
| Character name | **only if they tick the box** | off by default |

No character name, world, zone, position, or account identifier is sent unless the player
explicitly opts in. There is no telemetry and no background sending.

All user text is escaped for Discord markdown and mention syntax, and `allowed_mentions` is
empty, so a suggestion cannot format your channel, fake a heading, or smuggle an `@everyone`.

---

## 6. Client-side limits

In `SuggestionService`:

| Limit | Value |
|-------|-------|
| Minimum message length | 10 characters |
| Maximum message length | 1500 characters |
| Cooldown between sends | 60 seconds |
| Maximum sends per game session | 5 |
| Request timeout | 15 seconds |

**These are courtesy limits, not security.** They stop an honest user double-clicking or venting
repeatedly; they do nothing against anyone who has extracted the URL. Real limits have to live
server-side, which is the argument for Option B.

A failed send refunds the attempt, so a network blip does not cost the player one of their five.

---

## 7. Testing it

The dev build has the button too, so you can test end-to-end without publishing:

1. `/tchallenges` → **Suggest**.
2. Type at least 10 characters, press **Send**.
3. Watch the channel. The message is tagged with the build flavour, so a test from your dev
   build is distinguishable from a real player report.

If it fails, `/xllog` shows the status code — `[Suggestion] endpoint returned NNN`.
