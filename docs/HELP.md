<!--
    HELP.md — the player-facing help, PARSED AT RUNTIME by HelpLibrary.

    THE FORMAT IS LOAD-BEARING. Read this before editing:

      # Category            starts a group in the Help window's left-hand list
      ## Section title       one searchable, jump-to-able entry
      <!-- keywords: a, b -->   hidden search terms for the section above

    Rules:
      • A `## ` heading starts a new section. Everything until the next heading is its body.
      • The keywords comment must come DIRECTLY under its `## ` heading. It is invisible both
        here (it is an HTML comment) and in the plugin, and exists so a player who describes a
        thing awkwardly still finds it — "filled in", "solid", "shape" all reaching the stars.
      • Write keywords for the words a player would GUESS, not the words we chose. If a section
        can only be found by already knowing its title, it has no keywords worth having.
      • Body text is shown as plain paragraphs. Blank lines separate paragraphs. Do not rely on
        tables, images, or nested lists — the renderer draws wrapped text and simple bullets.
      • A line starting with "- " is drawn as a bullet.

    This file ships beside the DLL and is read at startup. It is a player-facing document, so
    per CLAUDE.md §6 it must be updated in the SAME commit as any change a player can see.
-->

# Getting started

## What this plugin does
<!-- keywords: what is this, purpose, about, overview, intro, introduction, point, why, explain, summary, begin, new, start here -->

FFXIV Miscellaneous Challenges tracks self-imposed challenges — small goals you set yourself that
the game itself does not track. Walk a route, perform an emote in a particular spot, ride a
specific mount somewhere, wear a full outfit in a given zone, or race between two points against
a clock.

Challenges **complete themselves**. There is no button to mark one done: the plugin watches for
the conditions and notices when you meet them.

## Opening the window
<!-- keywords: open, command, slash, chat command, launch, show, window, hide, close, toggle, cant find, missing, disappeared, lost, offscreen, off screen, center, recover -->

Type `/tchallenges` or the shorter `/tchal` in chat to open and close the window.

Other commands:

- `/tchallenges center` — brings the window back to the middle of the screen. Use this if you have
  dragged it off-screen, or if it was left on a monitor you no longer have.
- `/tchallenges reset` — asks before wiping your progress.

## Escape gives control back
<!-- keywords: escape, esc, key, stuck, frozen, cant type, cannot type, keyboard, unresponsive, trapped, locked, release, cancel, stop, get out -->

Pressing **Escape** always stops whatever the plugin is doing with your keyboard or mouse. It
hands the keyboard straight back to the game and closes the plugin's menus, dropdowns and panels.

Escape never cancels anything you would mind losing. A race you are running, a search you have
typed and your place in the list are all left alone, and the game still receives the keypress and
still closes its own windows.

# Reading the list

## The challenge row
<!-- keywords: row, entry, line, item, list, layout, what am i looking at, columns, number, name, title, description, reading -->

Each row is one challenge. From left to right it shows a tick box, the challenge number and name,
a description underneath, and a set of controls and markers on the right.

The number is just its position in the list you are looking at. It changes when you sort or filter
and means nothing permanent — the challenge itself is tracked by a hidden identifier that never
changes, so renaming or reordering can never lose your progress.

## Expanding a row to read all of it
<!-- keywords: expand, longer, cut off, truncated, ellipsis, dots, more, full text, click, taller, grow, read, cropped, shortened -->

Long names and descriptions are trimmed with "…" to keep rows a uniform height. **Click a row** to
expand it and see the whole thing wrapped over as many lines as it needs. Click it again, or click
anywhere that is not a row, to collapse it.

Clicking a row only ever expands it. It can never mark a challenge done.

## The tick box
<!-- keywords: tick, check, checkbox, box, square, done, complete, finished, mark, cross, empty, filled, green, status -->

The box at the left of each row shows whether the challenge is finished. A ticked box means done;
an empty box means not yet.

It is a readout, not a control — clicking it does nothing, because completion is always earned
rather than declared.

## Difficulty stars
<!-- keywords: star, stars, difficulty, hard, easy, rating, rated, unrated, five, 5, filled, filled in, solid, full, empty, hollow, outline, shape, gold, yellow, level, tough, simple, how hard, score -->

Up to **five stars** on the right of a row show how hard the challenge is meant to be. A solid gold
star counts; a hollow outlined star does not. Five solid stars is the hardest rating.

Some challenges show **no stars at all**. That means nobody has rated it yet — it does not mean the
challenge is easy.

## Hints
<!-- keywords: hint, clue, help me, stuck, where, how, question mark, ?, tip, spoiler, reveal, find it, cant find, show me -->

The **?** button on a row reveals a hint, replacing the description with it. Press it again to put
the description back.

Not every challenge has one. Where none was written the button is drawn dim and does nothing,
rather than pretending there is a hint to give.

## The CUSTOM badge
<!-- keywords: custom, badge, official, unofficial, mine, homemade, made up, not official, tag, label, source, trust, where from -->

A challenge marked **CUSTOM** is not part of the published catalogue — it exists only on your
install. Everything without the badge came from the official challenge list and is the same for
everybody.

## Hidden challenges — ??? rows
<!-- keywords: ???, question marks, hidden, masked, unknown, spoiler, spoilers, blank, blanked, cant see, censored, secret, locked, unexplored, why hidden -->

A row showing **??? Challenge** belongs to a zone you have not been to yet. The name, description,
hint and difficulty are all hidden so the plugin never spoils somewhere you have not reached.

Visit the zone and it reveals itself. Finishing a challenge also reveals it permanently, since
finishing it means you were there.

Residential districts are never hidden this way.

## The zone marker and map pins
<!-- keywords: pin, map, marker, flag, waypoint, location, where is it, find, yellow icon, gold icon, same zone, here, nearby, arrow, coordinates, navigate, direction -->

A small marker appears on a row when you are **standing in that challenge's zone right now**. It is
the quickest way to see what you can work on where you are.

On some challenges the marker is also a **button**. Click it and the plugin drops the game's map
flag on the challenge's location and opens the map there — it points at the next thing you actually
have to do, so on a quest it marks the step you are on rather than the start.

Most challenges do not offer this, because finding the spot is part of the challenge. Only ones
where the doing matters more than the hunting have it switched on.

# Kinds of challenge

## Standard challenges
<!-- keywords: normal, standard, plain, ordinary, gold, yellow, single, simple, one, basic, regular -->

The ordinary kind: one thing to do, in one place. Do it and it completes. These are shown in the
plugin's usual gold.

## Quests — the blue ones
<!-- keywords: quest, quests, blue, chain, chains, chained, series, steps, step, multi part, multipart, sequence, stages, part 1, next step, story, linked -->

A **quest** is a series of steps. The row shows only the step you are on, and it changes to the next
one as you finish each. Only completing the final step completes the quest.

Quests move around the zone list with you: if the step you are on is in Ul'dah, the quest sits under
Ul'dah, even if it began somewhere else.

Later steps are deliberately not shown in advance. The **QUEST** button on the row opens the full
sheet, where finished steps and the one you are on are listed and the rest read as `???`.

Your progress through a quest is saved. Take as long as you like.

## Adventures — the green ones
<!-- keywords: adventure, adventures, green, objectives, checklist, list, several, multiple, many, tasks, todo, collection, group, set, all of them -->

An **adventure** is one challenge with several objectives. Some can be done in any order, some in a
set order, and the **STEPS** button on the row opens the full list with everything you have already
done marked off.

Adventure progress is saved between sessions, so you can work through one over days.

A few adventures are marked as needing to be done in a single login session. Where that applies the
objectives sheet says so plainly.

## Races — the timed ones
<!-- keywords: race, races, timer, timed, time, clock, speed, speedrun, run, fast, seconds, stopwatch, countdown, best time, record, personal best, start line, finish line -->

A **race** is a timed run between two points.

Stand in the start area and a panel appears in the bottom-right corner asking if you want to begin.
Press **Start!** and the clock runs. Reach the finish area before the time limit and the race is
complete.

While running you get a live clock in the corner, which turns red for the last five seconds.

# Running a race

## Starting, restarting and abandoning a race
<!-- keywords: start, begin, restart, reset race, retry, again, abandon, quit, give up, cancel run, stop race, redo, another go, second attempt -->

There are two ways to start: the corner panel when you are standing at the line, or the **START!**
button on the challenge's own row.

- **Re-entering the start area restarts the clock.** A bad run costs you nothing but the walk back.
- **Abandon** ends the run with no penalty.
- Leaving the zone ends the run.
- Some races have a boundary you must stay inside; leaving it ends the run.
- Running out of time ends the run.

Ending a run never costs you a completion you already had.

## Best times
<!-- keywords: best, best time, record, personal best, pb, fastest, beat, improve, score, time, faster, again, replay, leaderboard -->

Your fastest finish is saved and shown on the challenge's row.

A finished race **stays runnable** so you can go back and beat your own time. Beating it announces
itself with a gold **PERSONAL BEST** panel, floating text and a chat message telling you the time
you beat.

Best times are **not** erased by resetting your progress — a personal best is a record of something
you did, and resetting is exactly when you would most want the time to beat still on screen.

## Turning the race prompt off
<!-- keywords: prompt, popup, annoying, dont show, disable, turn off, stop asking, corner, panel, ask, offer, re-enable, bring back, back on -->

The corner panel has a **Don't show these** button if you would rather it stopped asking.

Races stay startable from the challenge list either way, and the running clock always shows — only
the offer is hidden. Turn the prompts back on under **Settings → Notifications**.

# Finding things

## Searching
<!-- keywords: search, find, filter text, look up, box, type, typing, query, keyword, name, cant find, locate, where, lookup -->

There is a search box above each list. Type in it and the list narrows to matching challenges.

Search looks at names, descriptions and hints, so you can search for something you half-remember
from a description rather than the exact title.

It waits about a second after you stop typing before searching, so the list does not jump around
mid-word. A "…" appears while it is waiting. Clearing the box shows everything again immediately.

Searching the zone list opens collapsed expansions, so a match is never hidden inside a closed
group.

## Filtering what you see
<!-- keywords: filter, filters, hide, show, only, narrow, sort out, button, dropdown, menu, toggle, options, too many, declutter, subset, categories of challenge -->

The small button beside the difficulty stars opens a filter menu. Switch off anything you do not
want to see: completed or unfinished challenges, standard challenges, quests, adventures, races, or
custom ones.

The button **lights up** whenever something is being hidden, and the line under the category name
tells you how many rows the filter is holding back — so a short list is never a mystery.

Choose **Show everything** to clear it.

## The difficulty filter
<!-- keywords: difficulty filter, stars, hide hard, too hard, easy only, ceiling, maximum, limit, cap, level, rating filter, five star, one star -->

The five stars beside the category name are a filter, not a rating. Click the third star and the
list hides anything rated harder than three.

Unrated challenges are never hidden by it — no rating means "not judged yet", which is not the same
as "easy".

Click the same star again to go back to showing everything.

## Sorting the list
<!-- keywords: sort, order, arrange, alphabetical, a-z, alphabetically, by name, by difficulty, creation, oldest, newest, rearrange, reorder -->

**Settings → Sort** offers three orders: creation order, A→Z by name, and by difficulty.

Sorting by difficulty puts unrated challenges last rather than first, and keeps the order you were
already reading within each star rating.

## Categories and zones
<!-- keywords: category, categories, zone, zones, group, grouping, tab, tabs, left, list, expansion, region, organise, organize, browse, navigate, switch view -->

The left-hand pane can group challenges two ways, chosen with the tabs at its top:

- **Categories** — the groupings the challenge author chose.
- **Zones** — every zone in the game, grouped by expansion, with a count of how many challenges in
  each you have finished.

In zone view you can collapse an expansion to keep the list manageable, and there is an option to
hide zones that have no challenges in them at all.

## Teleporting to a zone
<!-- keywords: teleport, travel, go to, warp, aetheryte, right click, right-click, jump to, fly, visit, attuned -->

**Right-click a zone** in the zone list to teleport there, if you are attuned to it.

# Settings

## Where the settings are
<!-- keywords: settings, options, preferences, config, configure, change, customise, customize, menu, where, setup -->

Open **Settings → Sound, notifications & colours…** in the menu bar. The window has three tabs.

Everything saves the moment you change it — there is no Apply or OK button.

## Sound and volume
<!-- keywords: sound, sounds, audio, volume, loud, quiet, mute, muted, silence, silent, sfx, effects, noise, turn down, turn off, cue, jingle, chime, ding, fanfare -->

**Settings → Sound** has:

- A **volume** slider for the plugin's own sounds. It changes nothing else in the game.
- **Mute all sounds**, which is separate from the volume so unmuting brings back the level you had.
- Each sound listed individually with its own on/off switch and a **Play** button so you can hear
  one before deciding.

Sounds still play when on-screen popups are held back during combat, because a sound cannot get in
the way of anything.

## Notifications and popups
<!-- keywords: notification, notifications, popup, popups, toast, banner, alert, message, fly text, floating text, combat text, on screen, duration, how long, seconds, disable, turn off, annoying, distracting, in combat, duty, raid -->

**Settings → Notifications** controls what appears on screen:

- **Completion banner** — the large popup when a challenge finishes.
- **Progress popups** — the small corner popup when part of a challenge is done.
- **Floating text** — text that rises over your character, like combat text.
- **On screen for** — how long they stay, between 2 and 15 seconds.
- **Hold notifications in combat and duties** — keeps popups off your screen during a fight. Sounds
  still play.

## Changing the colours
<!-- keywords: colour, color, colours, colors, theme, recolour, recolor, palette, gold, blue, green, change colour, customise, appearance, look, style, ugly, contrast, readable, hard to read, reset colours -->

**Settings → Colours** lets you recolour the interface: titles, descriptions, hints, the main
accent, the completed colour, warnings, muted text, quests and adventures.

Changes apply as you drag. Every colour has its own **Reset**, and there is a **Reset all colours**
button, so you can never paint yourself into an unreadable corner.

## Text size
<!-- keywords: size, scale, ui scale, bigger, larger, smaller, text size, font, zoom, tiny, huge, cant read, small text, readable, magnify -->

**Settings → UI Scale…** offers three sizes for the main window: compact, larger, and largest.

## Background image
<!-- keywords: background, image, picture, wallpaper, appearance, backdrop, art, opacity, transparent, see through, panel, custom image, photo -->

**Settings → Appearance…** sets a background image for the window. Four are built in, or you can
point it at an image file of your own.

Two sliders control how strongly the image shows and how transparent the panels over it are — the
second is what makes a background usable rather than hidden behind solid panels.

## Switching the renderer
<!-- keywords: renderer, plain, panache, panacheui, ugly, broken ui, fallback, simple, basic, layout broken, missing, wont load, does not load, crash, blank -->

**Settings → Switch to plain renderer** falls back to a plain, utilitarian version of the window.

It exists so the plugin stays usable if its graphical framework is missing or misbehaving. Everything
still works there — challenges, races, searching, settings — it simply looks plain.

# Your progress

## How completion is recorded
<!-- keywords: progress, saved, save, storage, record, data, file, where stored, per character, account, alt, alts, characters, shared, lost, safe, backup -->

Progress is stored per install and shared across all of your characters — finishing something on one
character finishes it everywhere.

Each completion is stored with the date you earned it, which the row shows once it is done.

## Resetting your progress
<!-- keywords: reset, wipe, clear, start over, delete, erase, redo, again, fresh, restart, undo, remove progress, blank slate -->

**Settings → Reset** wipes your completions so you can do everything again. It asks first, and it is
the only thing in the plugin that deletes anything.

It also clears progress on things you have **started but not finished**. A quest chain goes back to
its first step, and any adventure objectives you have ticked off are unticked. That part is not
covered by the permanent record below, because the record only ever holds challenges you finished.

What reset does **not** touch:

- Challenges you or the catalogue authored — the definitions all survive.
- Your race best times.
- The permanent record described below.

## Recovering progress after a reset
<!-- keywords: restore, recover, undo reset, mistake, accident, get back, permanent, ledger, history, oops, deleted, bring back -->

The plugin keeps a **permanent record** of the first time you ever completed each challenge, and a
reset never touches it.

If that record holds anything your current progress is missing, a **Restore** item appears under
**Update** in the menu bar. Restoring puts back each challenge's original completion date, not
today's.

## Getting new challenges
<!-- keywords: sync, update, new challenges, download, refresh, more, empty, none, no challenges, nothing here, catalogue, catalog, official, latest -->

New official challenges arrive by syncing. It happens automatically shortly after you log in, and
**Update → Sync now** does it on demand.

An empty list is normal before the first sync — the plugin ships with no challenges built in.

## Challenges that need a newer plugin
<!-- keywords: newer, update plugin, version, incompatible, withheld, hidden, missing, cant see, too old, outdated, banner, warning -->

If the catalogue contains a challenge this version cannot understand, it is withheld rather than
shown broken, and a banner tells you how many and which version you need.

Updating the plugin brings them in. This is deliberate: a challenge that silently never completes
would be far worse than one that says "update first".

# Help and feedback

## Reporting a bug
<!-- keywords: bug, broken, report, problem, issue, error, crash, wrong, not working, fault, feedback, log, tell you -->

**Help → Report a bug…** sends a report straight to the developer, with the plugin's recent log
attached so the problem can be traced without a back-and-forth.

## Suggesting something
<!-- keywords: suggest, suggestion, idea, feature, request, wish, want, feedback, ask for, improvement -->

**Help → Suggest a feature…** sends an idea to the developer. No log is attached.

## This help page
<!-- keywords: help, documentation, docs, manual, guide, instructions, how to, faq, search help, this page, explain -->

**Help → Help & documentation** opens this page.

Use the search box to find a topic. It searches everything here, including extra terms attached to
each section that do not appear on screen — so describing a thing loosely usually finds it even if
you do not know what it is called.

Click any result to jump to it.
