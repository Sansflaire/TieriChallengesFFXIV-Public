# FFXIV Miscellaneous Challenges

A Dalamud plugin that tracks self-imposed challenges in FFXIV — combat, exploration, gathering,
crafting and social goals.

Challenges **complete themselves** when their conditions are met. Walk a route, perform an emote
at a particular spot facing a particular way, ride a specific mount somewhere, summon a particular
minion, carry an item, play a certain job, wait for the right Eorzean hour, or wear a full Glamour
Dresser outfit in a given zone — the plugin notices and celebrates it.

Challenges come in four shapes: ordinary one-off goals, **quests** that run over several steps,
**adventures** with a list of objectives to work through at your own pace, and **races** timed
between two points with your best time recorded.

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
| `/tchallenges center` | Bring a lost or off-screen window back |
| `/tchallenges sync` | Fetch new official challenges now |
| `/tchallenges reset` | Ask before clearing progress |

Pressing **Escape** always hands the keyboard back to the game and closes the plugin's menus and
panels. It never cancels a race you are running or a search you have typed.

---

## What it does

- **Automatic completion.** There is no "mark done" button. A challenge completes when you
  actually satisfy it, and records the date.
- **Your progress is safe.** Completion is stored twice — a working copy and a permanent record.
  Reset only clears the working copy, so it is always recoverable, with the original dates.
- **Cheap to run.** Challenges are only evaluated in the zone they belong to, only while
  incomplete, and only a few times a second. In a zone with nothing to track it costs a single
  comparison per tick.
- **Searchable built-in help.** **Help → Help & documentation** explains every part of the plugin
  and can be searched in plain language — describe a thing loosely and it will still be found.
- **Find what you care about.** Search both lists by name, description or hint; filter by whether
  something is done, by challenge type, or by a difficulty ceiling; sort three ways.
- **Yours to set up.** Sound volume, mute and per-sound switches; independent toggles for the
  completion banner, corner popups and floating text, with a duration and an option to hold them
  during combat; a full colour palette editor; three text sizes; and a background image.
- **No spoilers.** Challenges in zones you have not reached stay hidden until you get there.
- **Suggestions welcome.** There is a Suggest button in the window that sends feedback straight
  to the developer. Nothing about you is sent unless you choose to include it.

## Requirements

Nothing extra — the plugin bundles everything it needs.

---

## Privacy

The plugin downloads two public files from GitHub — its challenge catalogue and a small
moderation list — shortly after you log in, and again whenever you press **Sync now**. Those are
ordinary downloads: nothing about you or your characters is sent with them.

The only time the plugin *transmits* anything is when **you** press Send in the suggestion or
bug-report box. That sends your message and the plugin version; a bug report also attaches the
plugin's recent log so the fault can be traced; and your character name is included only if you
tick the box.

There is no telemetry, and nothing is sent in the background.

## Issues and suggestions

Use the in-plugin **Report a bug** button — it attaches the plugin's recent log so the problem can
be traced — or the **Suggest** button for ideas. You can also open an issue on this repository.

---

*This repository holds the plugin's source, its `pluginmaster.json` and the signed release zips.
Challenge data is published separately to
[TieriChallengesFFXIV-Sync](https://github.com/Sansflaire/TieriChallengesFFXIV-Sync) and picked up
by the plugin when it syncs.*

**Author:** Sansflaire
