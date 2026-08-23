# FFXIV Miscellaneous Challenges

A Dalamud plugin that tracks self-imposed challenges in FFXIV — combat, exploration, gathering,
crafting and social goals.

Challenges **complete themselves** when their conditions are met. Walk a route, perform an emote
at a particular spot facing a particular way, ride a specific mount somewhere, or wear a full
Glamour Dresser outfit in a given zone — the plugin notices and celebrates it.

---

## Installing

1. In game, open **`/xlsettings` → Experimental**.
2. Under **Custom Plugin Repositories**, paste this URL and press **+**, then **Save**:

   ```
   https://raw.githubusercontent.com/Sansflaire/TieriChallengesFFXIV-Public/main/pluginmaster.json
   ```

3. Open **`/xlplugins`**, search for **FFXIV Miscellaneous Challenges**, and install.
4. Open it with **`/tchallenges`** (or `/tchal`).

## Commands

| Command | Does |
|---------|------|
| `/tchallenges` | Toggle the window |
| `/tchal` | Same, shorter |
| `/tchallenges status` | Print progress to chat |
| `/tchallenges reset` | Ask before clearing progress |

---

## What it does

- **Automatic completion.** There is no "mark done" button. A challenge completes when you
  actually satisfy it, and records the date.
- **Your progress is safe.** Completion is stored twice — a working copy and a permanent record.
  Reset only clears the working copy, so it is always recoverable, with the original dates.
- **Cheap to run.** Challenges are only evaluated in the zone they belong to, only while
  incomplete, and only a few times a second. In a zone with nothing to track it costs a single
  comparison per tick.
- **Suggestions welcome.** There is a Suggest button in the window that sends feedback straight
  to the developer. Nothing about you is sent unless you choose to include it.

## Requirements

Nothing extra — the plugin bundles everything it needs.

---

## Privacy

The plugin makes no network requests unless **you** press Send in the suggestion box. It sends
your message, the plugin version, and — only if you tick the box — your character name. There is
no telemetry.

## Issues and suggestions

Use the in-plugin **Suggest** button, or open an issue on this repository.

---

*This repository hosts the public release and its plugin manifest. The source lives in a private
repository.*

**Author:** Sansflaire
