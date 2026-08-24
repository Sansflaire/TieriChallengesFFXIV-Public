# TEST-TODO

Things that are built and believed correct but have **not been exercised by a human in the game**.

Everything here compiles, and most of it is covered by automated checks — that is exactly why it
needs listing. A green build and a passing unit test say the code does what I wrote; they say
nothing about whether what I wrote is what the game does.

Tick an item only after seeing it with your own eyes.

---

## Priority 1 — the unverified link in the ban chain

- [ ] **Test banning another account; use trial account on secondary PC**

  Everything either side of this is proven: the crypto round-trips (18 automated checks), a real
  entry published to the live sync repo was fetched back, matched and decrypted, and the Cloudflare
  relay dropped 12 of 12 banned messages with zero leaks. The one link never executed is the plugin
  reading `LocalPlayer.Name` and `HomeWorld` in a running game and building `Name@World` from it.

  If that string does not match what the ban form produced, **the ban silently does nothing** — no
  error, no log line, just a banned player who is not banned. It is the highest-value 10 minutes of
  testing available.

  What to check, in order:
  1. Ban the trial character by name + world in the Creator's **Bans** tab. Confirm the ledger shows it.
  2. **Publish ban list.** Wait a minute or two for the CDN.
  3. On the secondary PC, restart the plugin. The window should collapse to the ban notice showing
     **your exact typed reason** — that proves the hash matched AND the reason decrypted.
  4. Confirm the challenge list, tracking and reports are all gone, not merely hidden.
  5. Send a suggestion from the trial account anyway (if reachable) — it must not reach Discord.
  6. **Unban**, publish, restart the plugin, confirm it returns to normal.
  7. Log in on your main. It must be completely unaffected — a ban that catches the wrong character
     is worse than no ban.

---

## Priority 2 — visual, never seen rendered

None of the following has been looked at by anyone. All are cosmetic; none can corrupt data.

- [ ] **Header height.** Now sized to its content (~100 px) instead of a quarter of the window.
      Watch for the progress bar being clipped at the bottom, or the menu bar colliding with the
      title row.
- [ ] **UI Scale steps 2 and 3.** `/tchallenges` → Settings → UI Scale. Check row text is not
      clipped, the master pane is still readable, and the header still fits at step 3 — that is the
      most likely thing to break.
- [ ] **Difficulty meter.** Five pips per rated challenge. Currently circles, not stars — see
      `docs/TEMPORARY-ICONS.md`.
- [ ] **Row numbering.** Every list must read 1, 2, 3 from the top with no gaps, in the Categories
      tab and the Zones tab, and must renumber when the sort changes.
- [ ] **Sorting.** Settings → the three sort options. Difficulty order should put unrated last.
- [ ] **Ban notice.** Hard to see without a ban; Priority 1 covers it.
- [ ] **Toast scaling.** Completion and progress popups should follow UI Scale.

---

## Priority 3 — paths with no runtime evidence at all

- [ ] **Bug report with a log attachment through the relay.** The relay's multipart branch has
      never executed. Suggestions (plain JSON) are proven; the file-attachment path is not, and it
      is the most likely first-deploy surprise. Send one real bug report and confirm the log file
      arrives in Discord as an attachment.
- [ ] **Challenge sync against the new repo.** Sync data moved to
      `TieriChallengesFFXIV-Sync`. Hashes were verified byte-identical after the copy, but no sync
      has actually run against the new URL. Hit **Update → Sync now** and confirm it reports the
      expected challenge count rather than zero.
- [ ] **Ledger mirroring.** Add a throwaway ban, then confirm `backup/bans-private.json` and
      `backup/history/bans-private-<date>.json` appear in the private repo. Delete the throwaway
      afterwards.
- [ ] **Relay refusal path.** Hard to trigger deliberately; it fires when the ban list cannot be
      fetched at all and returns 503 rather than forwarding. Worth knowing it exists if reports
      ever start failing with "try again shortly".

---

## Done

- [x] Ban crypto: identity normalisation, hash determinism and separation, reason round-trip,
      wrong-key rejection, tamper and truncation rejection — 18 automated checks, all passing.
- [x] Live publish round-trip: real entry pushed to the sync repo, fetched back, matched, decrypted.
- [x] Relay ban enforcement: 12 banned attempts across cache expiries, 12 dropped, 0 leaked,
      1 control delivered.
- [x] Shipped DLL contains no Discord webhook — verified inside the release zip, not just the build.
- [x] Old webhook deleted (2026-08-24).
