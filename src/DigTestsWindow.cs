#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. The dig-test lab: one clearly-labelled section per test, each carrying its
/// <b>rules, commands, buttons and settings</b> together in one place.
///
/// <para><b>Why everything lives here rather than being spread around.</b> Sansflaire's instruction was
/// that each test be a very obvious section only the dev build can reach. A test whose rules are in
/// a doc, whose commands are in the command switch and whose ranges are in a header is four places
/// to forget one of — and the one people forget is the gate. Everything about a test being inside a
/// single <c>#if DEV_BUILD</c> file makes the boundary something you can see rather than something
/// you have to audit.</para>
///
/// <para>Raw ImGui rather than PanacheUI, and that is permitted: the match-the-main-window rule
/// covers <i>player-facing</i> surfaces, and the Challenge Creator is the standing precedent for a
/// dev-only window being exempt. No player will ever see this.</para>
///
/// <para><b>The tuning sliders write straight through to <see cref="DigTuning"/> and save on
/// release.</b> Editing a range mid-run is deliberately allowed — that is the entire point of the
/// lab. The one guarded relationship is the Clue Trail's radar radius, which is clamped to stay
/// larger than its dig radius; below that the ring could only appear after the dig was already
/// possible, which makes "solid means dig" a lie.</para>
/// </summary>
internal sealed class DigTestsWindow
{
    public bool IsVisible;

    private readonly DigTests _tests;

    private static readonly Vector4 Head    = new(0.95f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Rule    = new(0.68f, 0.68f, 0.76f, 1f);
    private static readonly Vector4 Running = new(0.44f, 0.86f, 0.62f, 1f);
    private static readonly Vector4 Cmd     = new(0.55f, 0.75f, 0.95f, 1f);
    private static readonly Vector4 Warn    = new(0.95f, 0.62f, 0.35f, 1f);
    private static readonly Vector4 Ok      = new(0.44f, 0.86f, 0.62f, 1f);

    /// <summary>
    /// Plays a banner without a trail. Supplied by the plugin rather than reached for here, so this
    /// window keeps knowing nothing about the HUD — same arrangement as the dial's dig click.
    /// </summary>
    public Action<bool>? OnPreviewBanner;

    /// <summary>Opens the map debug view. Same arrangement as the banner preview.</summary>
    public Action? OnOpenMapDebug;

    public DigTestsWindow(DigTests tests) => _tests = tests;

    public void Draw()
    {
        if (!IsVisible) return;

        ImGui.SetNextWindowSize(new Vector2(560, 720), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Dig Tests  (DEV ONLY)###tc_dig_tests", ref IsVisible))
        {
            ImGui.End();
            return;
        }

        try { DrawBody(); }
        catch (Exception ex) { Diag.Error($"[Dig] lab draw failed: {ex.Message}"); }

        ImGui.End();
    }

    private void DrawBody()
    {
        ImGui.TextColored(Head, "DEVELOPER BUILD ONLY");
        ImGui.TextColored(Rule,
            "None of this ships. The services, the HUD, the commands and these settings are all\n"
          + "behind #if DEV_BUILD. A player cannot reach any of it.");

        ImGui.Separator();

        // LIVE stays open and outside the collapsing set. It is the one part that answers "what is
        // happening right now", and a running test whose readout is folded away behind a header is
        // a readout nobody sees — which defeats the point of having it in a lab at all.
        DrawStatus();

        // Everything else collapses, and starts collapsed. This window had grown to five sections
        // and something like forty controls, so the thing being looked for was always past three
        // screens of something else. ImGui remembers each header's state in its own ini, so a
        // section opened once stays open across sessions.
        Section("Placement — where a spot may be buried", DrawMaxRise);
        Section("HUD placement — dial and clue",          DrawHudPlacement);
        Section("CLUE! / DIG! word",                      DrawWord);
        Section("Clue text",                              DrawClueText);
        Section("Trail banners",                          DrawBanners);
        Section("Dig artwork gradient",                   DrawArtwork);
        Section("Light burst",                            DrawLightBurst);
        Section("Dig animation length",                   DrawDigLength);
        Section("Dig animation speed",                    DrawDigSpeed);

        Section("Test 1 — Sense Hunt",        DrawHunt);
        Section("Test 2 — Area Surveillance", DrawSite);
        Section("Test 3 — Clue Trail",        DrawTrail);
        Section("Test 4 — Wild Trail",        DrawRoam);

        // Outside the headers on purpose. These are the two things reached for while fiddling with
        // whichever section happens to be open, and hunting for them inside a collapsed one would
        // be the exact friction the headers are here to remove.
        ImGui.Separator();

        if (ImGui.Button("Test dig now")) Say(Plugin.Props.Dig());
        ImGui.SameLine();
        if (ImGui.Button("Stop dig")) Say(Plugin.Props.Stop());
        ImGui.SameLine();
        if (ImGui.Button("Reset ALL tuning")) { DigTuning.ResetAll(); DigTuning.Save(); }

        ImGui.TextDisabled("saved to dig-tuning.json — delete it for defaults");
    }

    /// <summary>
    /// One collapsible block. Closed by default — <see cref="ImGuiTreeNodeFlags.None"/> rather than
    /// <c>DefaultOpen</c> — because with every section open the window opens on a wall of sliders.
    /// </summary>
    private static void Section(string title, Action body)
    {
        if (!ImGui.CollapsingHeader(title)) return;

        ImGui.Indent();

        // Guarded individually rather than relying on DrawBody's catch: one section throwing must
        // not take the rest of the lab down with it, and an Unindent that never ran would shift
        // every window drawn afterwards.
        try { body(); }
        catch (Exception ex) { Diag.Error($"[Dig] lab section '{title}' failed: {ex.Message}"); }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    /// <summary>
    /// A collapsible block INSIDE a <see cref="Section"/>, so a long test's settings do not all
    /// arrive at once just because the test was opened.
    ///
    /// <para><b>A tree node rather than another CollapsingHeader.</b> A header nested in a header
    /// renders as a second full-width bar and the two levels become impossible to tell apart at a
    /// glance; a tree node indents and carries an arrow, which is what says "this belongs to the
    /// thing above".</para>
    ///
    /// <para><b>The id is scoped by the parent's title, not by this one.</b> Several tests have a
    /// "Rules" or a "Ranges" block, and ImGui keys open/closed state by label — so without the
    /// scope, opening Test 3's Rules would open Test 1's too, and the state would be shared for as
    /// long as both existed.</para>
    /// </summary>
    private static void SubSection(string owner, string title, Action body)
    {
        if (!ImGui.TreeNodeEx($"{title}##{owner}-{title}", ImGuiTreeNodeFlags.SpanAvailWidth))
            return;

        // Same reasoning as Section: guarded individually, because a TreePop that never ran leaves
        // every window drawn afterwards inside this node's indent level.
        try { body(); }
        catch (Exception ex) { Diag.Error($"[Dig] lab block '{owner}/{title}' failed: {ex.Message}"); }

        ImGui.TreePop();
        ImGui.Spacing();
    }

    // ── live status ──────────────────────────────────────────────────────────

    private void DrawStatus()
    {
        var active = _tests.Active;

        ImGui.TextColored(Head, "LIVE");

        if (active == null)
        {
            ImGui.TextDisabled("Nothing running. Only one test may run at a time — starting one stops the other.");
            return;
        }

        ImGui.TextColored(Running, $"{active.Name}  —  {active.Headline}");
        ImGui.TextDisabled(active.Subtitle);
        ImGui.Text($"Elapsed {CompletionStore.FormatRaceTime(active.ElapsedSeconds)}   band {active.Band}");

        // The distance readouts are dev-only for a reason: shown to a player they would replace
        // every one of these tests with walking down a gradient.
        if (active is DigHuntService h)
            ImGui.Text($"distance {h.DebugDistance:0.0}   senses {h.SenseCount}");
        else if (active is DigSiteService s)
            ImGui.Text($"pieces {s.PieceFound}/{s.PieceTotal}   digs {s.Digs}   inside site: {s.InSite}");
        else if (active is DigTrailService t)
            ImGui.Text($"stop {t.StopIndex + 1}/{t.StopTotal}   distance {t.DebugDistance:0.0}   digs {t.Digs}");

        if (ImGui.Button("Stop##live")) Say(_tests.StopAll());
        ImGui.SameLine();
        if (ImGui.Button("Dig##live")) Say(_tests.Dig());
    }

    /// <summary>
    /// Test 4's lab block. Broken into sub-sections because it is by far the longest — rules,
    /// navmesh state, controls, clue categories, the per-category bench, placement and diagnostics
    /// are seven separate concerns, and opening the test used to deliver all seven at once.
    ///
    /// <para>The controls come FIRST and stay outside a sub-section. Start / Stop / Repeat clue are
    /// what the section is opened for; putting them behind their own arrow would mean two clicks to
    /// reach the thing everything else is in aid of.</para>
    /// </summary>
    private void DrawRoam()
    {
        if (ImGui.Button("Start##roam")) Say(_tests.Start(_tests.Roam));
        ImGui.SameLine();
        if (ImGui.Button("Stop##roam")) Say(_tests.Roam.Stop());
        ImGui.SameLine();
        if (ImGui.Button("Repeat clue##roam")) Say(_tests.Roam.Recall());

        // Navmesh state is not in a sub-section either: when it is building or missing, that is the
        // answer to "why did Start do nothing", and it would be behind the one arrow nobody thinks
        // to open while wondering.
        DrawRoamNavmesh();

        SubSection("roam", "Map bounds — set by hand",  DrawManualBox);
        SubSection("roam", "Rules",                     DrawRoamRules);
        SubSection("roam", "Clue categories",           DrawClueCategories);
        SubSection("roam", "Placement — where spots go", DrawRoamPlacement);
        SubSection("roam", "Diagnostics",               DrawRoamDiagnostics);
    }

    /// <summary>
    /// The hand-set walkable box: walk to two opposite corners, press two buttons.
    /// </summary>
    private void DrawManualBox()
    {
        ImGui.TextColored(Rule,
            "THE BOX EVERY DIRECTION IS MEASURED AGAINST.\n"
          + "EASIEST WAY: open Debug map locations and DRAG THE TWO CORNER HANDLES. Walking to a\n"
          + "corner is often impossible — a bounding box's corners are routinely inside a building,\n"
          + "past a wall, or outside the ward, which are exactly the places a box must reach and a\n"
          + "character cannot stand. The buttons below remain for when you CAN stand on a limit,\n"
          + "because they involve no coordinate conversion at all, where dragging does.\n"
          + "Every derived box has been wrong here. The map rectangle is a mostly-empty square. The\n"
          + "navmesh box swallowed terrain nobody can stand on. The landmark spread is the best of\n"
          + "the three and still only covers where the game puts LABELS.\n"
          + "Walking to two opposite corners involves no conversion, no sheet and no assumption\n"
          + "about what a navmesh flag means — it is the one measurement that cannot be wrong about\n"
          + "the thing it measures. Stand at the NORTH-WEST limit of where you can actually walk,\n"
          + "press A; go to the SOUTH-EAST limit, press B.\n"
          + "It is keyed to THIS territory and is ignored everywhere else, so a box set here can\n"
          + "never silently apply in a field zone. It also bounds PLACEMENT, so nothing is buried\n"
          + "outside it.");

        var player = Plugin.ObjectTable.LocalPlayer;
        uint territory = Plugin.ClientState.TerritoryType;

        if (player == null) { ImGui.TextDisabled("No character loaded."); return; }

        ImGui.Text($"You are at world ({player.Position.X:0.0}, {player.Position.Z:0.0})");

        if (ImGui.Button("Set corner A (north-west) here"))
        {
            DigTuning.SetBoxCorner(true, territory, player.Position.X, player.Position.Z);
            DigLandmarks.Invalidate();
            Say("corner A set.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Set corner B (south-east) here"))
        {
            DigTuning.SetBoxCorner(false, territory, player.Position.X, player.Position.Z);
            DigLandmarks.Invalidate();
            Say("corner B set.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear manual box"))
        {
            DigTuning.ClearBox();
            DigLandmarks.Invalidate();
            Say("manual box cleared — falling back to the derived boxes.");
        }

        if (DigTuning.TryManualBox(territory, out float x0, out float z0, out float x1, out float z1))
        {
            ImGui.TextColored(Ok,
                $"ACTIVE for this zone: x {x0:0.0} to {x1:0.0}, z {z0:0.0} to {z1:0.0}"
              + $"   ({x1 - x0:0} x {z1 - z0:0} yalms)");
        }
        else if (DigTuning.BoxSet)
        {
            ImGui.TextColored(Warn,
                $"A box is stored for territory {DigTuning.BoxTerritory}, not this one ({territory}).\n"
              + "It is ignored here. Set both corners in this zone to use one.");
        }
        else
        {
            ImGui.TextDisabled("No manual box — using the derived ones (landmarks, then navmesh, "
                             + "then the map rectangle).");
        }
    }

    private void DrawRoamRules()
    {
        ImGui.TextColored(Rule,
            "RULES\n"
          + "  Test 3's ordered trail, except the stops and the clues are GENERATED for the map\n"
          + "  you are standing on. Dig each to get the clue to the next.\n"
          + "  Every clue is anchored to something the GAME draws on the map — the named markers\n"
          + "  behind every label on your map screen. That is what makes a generated clue usable:\n"
          + "  you can open the map, find the word, and go. A bearing from wherever you happened\n"
          + "  to be standing — which is what the removed Test 3 generator produced — cannot be\n"
          + "  looked up anywhere, which is why that one was deleted rather than tuned.\n"
          + "  Spots obey Test 2's ground rules: within Max rise of you, and no step across the\n"
          + "  dig radius, so none sits on a roof, a ledge or astride a wall.\n"
          + "  Leaving the zone ABANDONS the run, unlike Test 3 — every spot was placed against\n"
          + "  this map's geometry and every clue names this map's landmarks.");
    }

    private void DrawRoamNavmesh()
    {
        if (DigNavmesh.Available && !DigNavmesh.Ready)
        {
            // Front and centre: this is a wait, not a failure, and without a number it is
            // indistinguishable from one.
            ImGui.TextColored(Warn, DigNavmesh.StatusLine());

            float p = DigNavmesh.BuildProgress;
            if (p >= 0f) ImGui.ProgressBar(Math.Clamp(p, 0f, 1f), new Vector2(-1f, 0f));

            ImGui.TextColored(Rule,
                "A run will not start until this finishes. Placement can query a partial mesh\n"
              + "quite happily, but every spot it could place would sit in whichever corner of the\n"
              + "zone was meshed first — a lopsided trail you would have no way of noticing.");
        }

        ImGui.TextColored(Head, "VNAVMESH IS REQUIRED");
        ImGui.TextColored(Rule,
            "Not a preference and no longer a toggle. The old \"Require navmesh\" checkbox had an\n"
          + "off position that fell back to raycast heuristics, and those cannot answer\n"
          + "reachability even in principle: a ray finds the first solid surface under a point, and\n"
          + "the sand outside a housing ward's walls is solid, meshes, and is unreachable. So \"off\"\n"
          + "did not trade accuracy for availability — it produced spots nobody could walk to while\n"
          + "looking like a setting somebody might reasonably pick.\n"
          + "REACHABLE is not the same as ON THE MESH, and that distinction is the whole fix.\n"
          + "vnavmesh flags disconnected islands with FLAG_UNREACHABLE during a flood fill at build\n"
          + "time. Query.Mesh.NearestPoint ignores that flag; NearestPointReachable respects it.\n"
          + "We were asking the first one, getting correct answers, and still placing spots outside\n"
          + "the walls — because those spots genuinely ARE on the navmesh, just on an island you\n"
          + "cannot get to. No amount of tolerance tightening was ever going to fix that.");

        if (!DigNavmesh.Available)
        {
            ImGui.TextColored(Warn,
                "vnavmesh is NOT answering — not installed or not loaded. Test 4 will refuse to\n"
              + "start rather than place spots it cannot vouch for.");
        }
        else if (!DigNavmesh.HasReachableQuery)
        {
            ImGui.TextColored(Warn,
                "vnavmesh is answering but does NOT publish Query.Mesh.NearestPointReachable, so\n"
              + "the gate would only be able to ask \"is it on the mesh\" — which accepts\n"
              + "disconnected islands. Test 4 refuses. Update vnavmesh.");
        }
        else
        {
            ImGui.TextColored(Ok,
                "vnavmesh is answering and is reachability-aware. Every spot is snapped onto\n"
              + "reachable navmesh and then re-grounded, so it sits where a character actually\n"
              + "stands rather than up to a tolerance away from it.");

            ImGui.SliderFloat("Navmesh tolerance", ref DigTuning.RoamNavTolerance, 0.1f, 20f, "%.1f y");
            if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();
        }

        ImGui.TextColored(Rule,
            "WHEN THIS TEST GOES PUBLIC, vnavmesh becomes a PUBLIC requirement. Un-gating it means\n"
          + "saying so in README.md and docs/HELP.md, and refusing at runtime with a line a player\n"
          + "can act on — not silently doing nothing.");
    }

    private void DrawRoamDiagnostics()
    {
        ImGui.TextColored(Head, "GIVE IT AWAY");
        ImGui.TextColored(Rule,
            "Prints where the spot REALLY is and flags it on your map.\n"
          + "IT IS ALSO THE DIAGNOSIS. A clue you cannot follow is wrong in one of two ways that\n"
          + "look identical from in-game: the clue is unhelpful (an area label is a poor anchor —\n"
          + "\"west of a whole subdivision\" is most of a housing ward), or the marker-to-world\n"
          + "conversion is off and the clue describes a place the spot is not.\n"
          + "This separates them. It prints the clue's ANCHOR position and the TRUE bearing from\n"
          + "it to the spot. If that bearing matches the clue's wording, the maths is sound and\n"
          + "the clue is merely hard. If it disagrees, the conversion is the bug and no amount of\n"
          + "clue tuning will help.");

        if (ImGui.Button("Reveal current spot")) Say(_tests.Roam.Reveal());
        ImGui.SameLine();
        if (ImGui.Button("Debug map locations")) OnOpenMapDebug?.Invoke();

        ImGui.TextColored(Rule,
            "Debug map locations draws the real map with every candidate spot on it in red and\n"
          + "the clue landmarks in cyan — the fastest way to see whether anything can land off\n"
          + "the edge, and whether the landmarks sit on their own labels.");

        ImGui.Spacing();
        ImGui.TextColored(Cmd, "/tchal roam start | stop | dig | clue | where");

        ImGui.Spacing();
        ImGui.TextColored(Cmd, "/tchal roam try <category> [easy|medium|hard]");
    }

    private void DrawRoamPlacement()
    {
        ImGui.SliderInt("Spots", ref DigTuning.RoamStops, 1, 20);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.TextColored(Rule,
            "DIFFICULTY is how much a clue WITHHOLDS, not how vague its words are.\n"
          + "  0.00–0.33  EASY   — a landmark, a direction from it, and a sense of distance.\n"
          + "                      \"EAST of Blue Badger Gate, close by.\"\n"
          + "  0.34–0.66  MEDIUM — the landmark and the direction, but no distance: you pick\n"
          + "                      which way to set off and decide when you have overshot.\n"
          + "  0.67–1.00  HARD   — two landmarks and NO bearing, plus a map quadrant. The spot is\n"
          + "                      the intersection of \"near this\" and \"near that\", which is read\n"
          + "                      off the map rather than walked in a straight line.\n"
          + "No setting ever gives coordinates. A coordinate is not a clue, it is the answer.");

        ImGui.SliderFloat("Difficulty", ref DigTuning.RoamDifficulty, 0f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        DrawClueCategories();

        ImGui.TextColored(Rule,
            "THE WHOLE MAP IS ELIGIBLE. Spots are drawn from the map's own world rectangle, so\n"
          + "anywhere the drawn map covers can hold one. The cap below is optional and defaults\n"
          + "to 0 = none; an earlier version sampled a ring around you, which let how far you had\n"
          + "walked before pressing Start decide what the trail could contain.");

        ImGui.SliderFloat("Nearest spot (yalms)", ref DigTuning.RoamMinRange, 0f, 200f, "%.0f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Furthest spot (0 = whole map)", ref DigTuning.RoamMaxRange, 0f, 1000f,
                          DigTuning.RoamMaxRange <= 0f ? "whole map" : "%.0f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.TextColored(Rule,
            "ROOF CLEARANCE is how much open space under a candidate makes it a roof rather than\n"
          + "ground. It replaces the player-relative height gate, which cannot survive whole-map\n"
          + "sampling — across a zone that rejects every hill and basement for being far from\n"
          + "where you stand. Asking \"is there more ground a long way below this\" is local, so it\n"
          + "stays true anywhere. 0 disables it.");

        ImGui.SliderFloat("Roof clearance (yalms)", ref DigTuning.RoamMaxRise, 0f, 40f, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        Slider("Min gap between spots", ref DigTuning.RoamSpacing,  5f, 120f);
        // (clue-category controls are drawn above, next to Difficulty)
        Slider("Dig radius",            ref DigTuning.RoamDig,      1f, 30f);
        Slider("Radar appears within",  ref DigTuning.RoamRadar,    2f, 150f);

        if (DigTuning.RoamRadar <= DigTuning.RoamDig)
        {
            DigTuning.RoamRadar = DigTuning.RoamDig + 1f;
            ImGui.TextColored(Warn, "Radar range clamped above the dig radius — same rule as Test 3.");
        }

        ImGui.Spacing();
        ImGui.TextColored(Head, "WHAT THIS MAP OFFERS");
        ImGui.TextColored(Rule,
            "Dumps the named landmarks the clue writer can draw on here, with the world position\n"
          + "each one converts to and how far it is from you.\n"
          + "IT IS ALSO THE VERIFICATION. The marker-to-world conversion is the algebraic inverse\n"
          + "of two formulas this repo spot-checked but never confirmed in game. Stand ON a\n"
          + "landmark and read zero yalms and it is proven; read fifty and it is not. Until\n"
          + "somebody does that, treat every generated clue as derived rather than confirmed.\n"
          + "Only NAMED markers are listed. The icon-only ones — retainer bells, market boards,\n"
          + "menders — carry an icon id and no text, and turning an id into the words \"a retainer\n"
          + "bell\" needs a lookup table this plugin does not have and will not invent.");

        if (ImGui.Button("Dump map landmarks")) DumpLandmarks();
    }

    /// <summary>Every clue category, in bit order, with the label the bench shows.</summary>
    private static readonly (ClueCategory Cat, string Label)[] ClueCats =
    {
        (ClueCategory.Landmark,  "Landmark — any named map label"),
        (ClueCategory.Aetheryte, "Aetheryte — the one anchor you can teleport to"),
        (ClueCategory.Section,   "Map section — subdivisions, wards, plazas, gates"),
        (ClueCategory.Quadrant,  "Quadrant — map grid, no anchor at all"),
        (ClueCategory.Clock,     "Clock — bearing from the middle of the map"),
        (ClueCategory.Npc,       "NPC — a named friendly the client has loaded"),
        (ClueCategory.Enemy,     "Enemy — a named hostile the client has loaded"),
    };

    /// <summary>Which category the bench will spawn, and at what difficulty.</summary>
    private int _tryCat;
    private int _tryBand = 1;

    private static readonly string[] Bands = { "EASY", "MEDIUM", "HARD" };

    /// <summary>Middle of each band, so the spawned clue is representative rather than borderline.</summary>
    private static readonly float[] BandValue = { 0.15f, 0.50f, 0.85f };

    /// <summary>
    /// The clue-category controls: which categories a real trail may draw on, the anti-repeat
    /// strength, the awkwardness, and a bench that spawns ONE spot from ONE category.
    /// </summary>
    private void DrawClueCategories()
    {
        ImGui.Spacing();
        ImGui.TextColored(Head, "CLUE CATEGORIES");
        ImGui.TextColored(Rule,
            "A trail draws each clue's KIND from these by weight, so it does not say the same sort\n"
          + "of thing five times. Using a category knocks its share down; every clue written\n"
          + "without it lets the share climb back. It is a WEIGHT and not a ban on purpose — on a\n"
          + "map where only two categories can say anything, a ban would deadlock on clue three.\n"
          + "A category that cannot describe a given spot contributes nothing to that draw, which\n"
          + "is different from being suppressed: availability is decided by actually trying to\n"
          + "write the clue, so no predicate can disagree with what the writer would do.");

        int mask = DigTuning.RoamClueCategories;

        foreach (var (cat, label) in ClueCats)
        {
            bool on = (mask & (int)cat) != 0;

            if (ImGui.Checkbox(label + "##cat" + (int)cat, ref on))
            {
                mask = on ? mask | (int)cat : mask & ~(int)cat;

                // Never let the mask reach zero. With every category off the writer has nothing to
                // draw from and falls back to a bare quadrant line for every stop, which looks
                // exactly like the generator being broken.
                if (mask == 0) mask = (int)ClueCategory.Quadrant;

                DigTuning.RoamClueCategories = mask;
                DigTuning.Save();
            }
        }

        ImGui.Spacing();
        ImGui.SliderFloat("Anti-repeat strength", ref DigTuning.RoamRepeatPenalty, 0f, 0.99f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.TextColored(Rule,
            "AWKWARDNESS is how willing a clue is to anchor itself to something that is NOT the\n"
          + "nearest landmark. At 0 it always picks the nearest, which is the old behaviour and is\n"
          + "kept reachable on purpose — it is the ONE MOVE that separates \"these clues are cruel\"\n"
          + "from \"these clues are wrong\". Turn it up and you get true-but-irritating clues like\n"
          + "\"Far EAST of\" somewhere on the west side, where being east of it narrows very little.");

        ImGui.SliderFloat("Awkwardness", ref DigTuning.RoamAwkwardness, 0f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.Spacing();
        ImGui.TextColored(Head, "TRY ONE CATEGORY");
        ImGui.TextColored(Rule,
            "Buries a SINGLE spot within " + (int)DigRoamService.TestSpotRange + "y of you with the\n"
          + "clue pinned to one category and one difficulty, and runs it as a real one-stop trail —\n"
          + "same HUD, same radar, same dig. A clue that reads well as a chat line and is useless\n"
          + "while you are standing in the zone is exactly what this catches, and only the real\n"
          + "thing catches it.\n"
          + "The bench ignores the checkboxes and the weights above, and does not disturb them.\n"
          + "If the category cannot describe the spot it says so and buries nothing — that is a\n"
          + "fact about the category's reach, not a bug. NPC and Enemy only see what the client has\n"
          + "streamed in around YOU, so they are the two that legitimately come up empty.");

        ImGui.SetNextItemWidth(340f);
        string[] labels = new string[ClueCats.Length];
        for (int i = 0; i < ClueCats.Length; i++) labels[i] = ClueCats[i].Label;
        ImGui.Combo("Category##try", ref _tryCat, labels, labels.Length);

        ImGui.SetNextItemWidth(140f);
        ImGui.Combo("Difficulty##try", ref _tryBand, Bands, Bands.Length);

        if (ImGui.Button("Bury one here##try"))
        {
            int c = Math.Clamp(_tryCat,  0, ClueCats.Length - 1);
            int b = Math.Clamp(_tryBand, 0, Bands.Length - 1);

            // Through DigTests.Start so it still stops whatever else was running — the one-test-at-
            // a-time rule is not suspended for a bench spawn, or the HUD would carry two headlines.
            Say(_tests.StartRoamSingle(ClueCats[c].Cat, BandValue[b]));
        }

        ImGui.SameLine();
        if (ImGui.Button("What can this map offer?##try"))
        {
            var p = Plugin.ObjectTable.LocalPlayer;
            Say(p == null ? "no character loaded." : DigClueSources.Census(p.Position));
        }

        ImGui.SameLine();
        if (ImGui.Button("Why no aetherytes?##try"))
        {
            // "This zone has no aetherytes" and "every row was rejected by a filter" produce the
            // identical count of zero, which is how a housing ward full of aethernet shards came
            // back empty and looked like game data rather than our own gate.
            DigClueSources.Invalidate();
            foreach (var line in DigClueSources.AetheryteReport.Split('\n')) Say(line);
        }

        ImGui.SameLine();
        if (ImGui.Button("Probe zone LGB##try")) Say(DigClueSources.ProbeZoneLayers());

        ImGui.TextColored(Rule,
            "Probe zone LGB counts the placement objects in this zone's level geometry files.\n"
          + "It answers whether mob SPAWN POINTS can come from client data, which is a different\n"
          + "question from spawn TABLES — those are server-side and settled (Q11), which is why\n"
          + "monsters.json has mapLocation ??? for all 14,560 entries. Lumina can parse LGB and\n"
          + "LayerEntryType.BattleNPC exists, but whether retail zones actually carry usable\n"
          + "BattleNPC entries is UNVERIFIED. Healthy counts across a few zones means a whole-map\n"
          + "Enemy source is worth building; zeros mean Enemy stays live-only and that is settled.");
    }

    private void DumpLandmarks()
    {
        var marks = DigLandmarks.ForCurrentMap();
        var player = Plugin.ObjectTable.LocalPlayer;

        Say($"map {DigLandmarks.CurrentMapId()} — {marks.Count} named landmark(s).");

        foreach (var m in marks)
        {
            string away = player != null
                ? $"{DigGround.Flat(player.Position, m.World):0.0}y away"
                : "no player";

            Say($"  {m.Name}  —  map ({m.MapX:0.0}, {m.MapY:0.0})  world ({m.World.X:0.0}, {m.World.Z:0.0})  {away}");
        }

        if (player != null)
            Say($"  you are in {DigLandmarks.Quadrant(player.Position)}.");
    }

    private void DrawMaxRise()
    {
        ImGui.TextColored(Rule,
            "Max rise is how far above or below you a buried spot may sit. It is the whole\n"
          + "\"could you actually walk there\" heuristic — it rejects clifftops and ravine floors.");

        Slider("Max rise (yalms)", ref DigTuning.MaxRise, 2f, 80f);
    }

    private void DrawHudPlacement()
    {
        ImGui.TextColored(Rule,
            "Where the dig dial and the clue line sit, in logical pixels before UI scale.\n"
          + "Drop moves the PAIR down together, so it never changes the gap between them.\n"
          + "Clue gap is the space under the dial that the clue's first line starts at.\n"
          + "These are sliders because the game prints its own location banner into that gap and\n"
          + "we cannot read where it lands — it belongs to the game's HUD layout and moves with\n"
          + "the player's own HUD settings. Seating our two elements around it is a look-and-nudge\n"
          + "job, so the numbers live here rather than in the source.");

        ImGui.SliderFloat("HUD drop (px)", ref DigTuning.HudDropPx, -60f, 200f, "%.0f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Clue gap (px)", ref DigTuning.HudClueGapPx, 0f, 200f, "%.0f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        if (ImGui.Button("Reset HUD placement")) { DigTuning.ResetHud(); DigTuning.Save(); }
    }

    private void DrawWord()
    {
        ImGui.TextColored(Rule,
            "The CLUE! / DIG! word that arcs over the dial. It is authored into the TOP of the\n"
          + "same canvas as the dial, so drawing it at the dial's own rect is where the artist\n"
          + "put it — these three are the deliberate nudge off that, and the default Y lift is\n"
          + "the one adjustment it always needed to clear the rim.\n"
          + "Scale grows it about the dial's CENTRE, so the offsets keep meaning the same thing\n"
          + "at every size. It has its own window now, so it can move or grow as far as you like\n"
          + "without being clipped — and that window takes no input, unlike the dial's.");

        ImGui.SliderFloat("Word X", ref DigTuning.LabelOffsetX, -200f, 200f, "%.0f px");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Word Y", ref DigTuning.LabelOffsetY, -200f, 200f, "%.0f px");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Word scale", ref DigTuning.LabelScale, 0.2f, 3f, "%.2fx");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();
    }

    private void DrawBanners()
    {
        ImGui.TextColored(Rule,
            "The TRAIL START! / TRAIL END! artwork's vertical gradient, picked at BOTH ends\n"
          + "rather than derived from one colour and a falloff — which can only ever make the\n"
          + "bottom a darker copy of the top, and rules out the warm-to-deep shifts real title\n"
          + "art uses. The gradient runs across the whole banner, not per letter.");

        // Right at the top of the section, because every setting below is judged by watching one
        // play. Firing it from the real trigger means scrolling to the bottom of this window,
        // starting a trail, and getting back up here before a two-second animation ends.
        if (ImGui.Button("Play TRAIL START!")) OnPreviewBanner?.Invoke(false);
        ImGui.SameLine();
        if (ImGui.Button("Play TRAIL END!")) OnPreviewBanner?.Invoke(true);
        ImGui.SameLine();
        ImGui.TextDisabled("preview — no trail needed");

        ImGui.Spacing();
        ImGui.TextColored(Rule,
            "Placement is in FRACTIONS of the viewport, not pixels, so a banner lands in the same\n"
          + "place on a 1080p monitor as on a 1440p one. Width is a fraction too; the height\n"
          + "follows from the artwork's 2:1 shape and is never set separately.");

        ImGui.SliderFloat("Banner Y", ref DigTuning.BannerY, 0f, 0.90f, "%.3f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Banner X", ref DigTuning.BannerX, -0.5f, 0.5f, "%.3f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Banner width", ref DigTuning.BannerWidth, 0.10f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.Spacing();
        ImGui.ColorEdit3("Banner top", ref DigTuning.BannerTopColor);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.ColorEdit3("Banner bottom", ref DigTuning.BannerBottomColor);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.Spacing();
        ImGui.TextColored(Rule,
            "Motion echoes behind the banner as it slides. Each ghost is the banner drawn where it\n"
          + "genuinely WAS that many seconds ago, not offset by a fixed number of pixels — so the\n"
          + "trail bunches up as it decelerates into place and stretches as it leaves, the way real\n"
          + "motion blur does. They only appear while it is actually moving; during the hold every\n"
          + "ghost would land on the banner and just darken it.\n"
          + "Step is the gap in TIME between ghosts, so a larger one reaches further back.");

        if (ImGui.Checkbox("Echo trail", ref DigTuning.BannerEcho)) DigTuning.Save();

        ImGui.SliderInt("Echoes", ref DigTuning.BannerEchoCount, 1, 16);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Echo step", ref DigTuning.BannerEchoStep, 0.005f, 0.30f, "%.3f s");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Echo falloff", ref DigTuning.BannerEchoFalloff, 0.05f, 0.95f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();
    }

    private void DrawClueText()
    {
        ImGui.TextColored(Rule,
            "How the clue line under the dial is painted.\n"
          + "The OUTLINE's job is to separate the face from the ground, so a dark one is almost\n"
          + "always right: the grounds that defeat light type — snow, frost, pale stone, lit\n"
          + "grass — are exactly the ones a white outline vanishes into. A white outline was\n"
          + "tried and is the reason this section exists.\n"
          + "The SHADOW is what carries legibility against bright ground; the outline stops the\n"
          + "face reading as smudged into it. Keep the outline narrower than the shadow offset,\n"
          + "or the outline swallows the shadow and you are back to plain outlined text.\n"
          + "The GRADIENT matches the artwork but necessarily dims the BOTTOM of the type, which\n"
          + "is the part that has to survive the worst ground. Switch it off if it costs more\n"
          + "than it looks worth.");

        ImGui.ColorEdit3("Face colour", ref DigTuning.ClueFaceColor);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.ColorEdit3("Outline colour", ref DigTuning.ClueOutlineColor);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        if (ImGui.Checkbox("Outline", ref DigTuning.ClueOutline)) DigTuning.Save();
        ImGui.SameLine();
        if (ImGui.Checkbox("Drop shadow", ref DigTuning.ClueShadow)) DigTuning.Save();
        ImGui.SameLine();
        if (ImGui.Checkbox("Vertical gradient", ref DigTuning.ClueGradient)) DigTuning.Save();

        ImGui.SliderFloat("Outline width", ref DigTuning.ClueOutlineWidth, 0f, 8f, "%.1f px");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Shadow offset", ref DigTuning.ClueShadowOffset, 0f, 12f, "%.1f px");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Font size", ref DigTuning.ClueFontSize, 10f, 48f, "%.0f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        if (DigTuning.ClueOutline && DigTuning.ClueShadow
            && DigTuning.ClueOutlineWidth >= DigTuning.ClueShadowOffset)
        {
            ImGui.TextColored(Warn,
                "Outline is as wide as the shadow offset — the shadow is hidden behind it.");
        }

        if (ImGui.Button("Reset clue text")) { DigTuning.ResetClueStyle(); DigTuning.Save(); }
    }

    private void DrawArtwork()
    {
        ImGui.TextColored(Rule,
            "How dark the BOTTOM of the dial, the CLUE!/DIG! word and the trail banners go, as a\n"
          + "multiplier on their colour. 1 is flat; lower is a stronger top-to-bottom fall.\n"
          + "There is deliberately no inner-edge highlight setting. Reproducing the reference\n"
          + "art's white-to-gold band inside the black outline was tried twice — once by drawing\n"
          + "the fill as a smaller copy, once by ringing the shape with displaced copies — and\n"
          + "both mangled the artwork. The effect needs EROSION, which a draw list cannot do:\n"
          + "blending only ever adds coverage. It would have to be baked into the PNG.");

        ImGui.SliderFloat("Artwork gradient", ref DigTuning.IconGradientDrop, 0.2f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.Spacing();
        ImGui.TextColored(Rule,
            "The dark outline is OUTER: the shape goes down in dark first and the artwork covers\n"
          + "it, so the dark survives only where it reaches past the art. The artwork is drawn at\n"
          + "true size and position and is never inset or shrunk.\n"
          + "Two things can still make a WIDE one look like it eats the image. The art's own edge\n"
          + "is antialiased, so its outermost pixels are part-transparent and the dark behind\n"
          + "shows through them as a fringe. And a stamp cannot tell the outside of a shape from a\n"
          + "hole inside it, so transparent detail WITHIN the art fills with dark too, creeping in\n"
          + "from both sides of every interior line. Narrow fixes both — hence the 1.0 default.");

        if (ImGui.Checkbox("Dark outline", ref DigTuning.IconOutline)) DigTuning.Save();

        ImGui.SliderFloat("Outline width##icon", ref DigTuning.IconOutlineWidth, 0f, 6f, "%.1f px");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Outline opacity##icon", ref DigTuning.IconOutlineAlpha, 0f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();
    }

    private void DrawLightBurst()
    {
        ImGui.TextColored(Rule,
            "Rays of light behind the dial once a dig would land. Each ray breathes on its own\n"
          + "clock, so the burst expands and contracts unevenly instead of pumping as one.\n"
          + "COLOUR MATTERS MORE THAN YOU'D THINK: ImGui blends normally rather than additively,\n"
          + "so white light drawn over snow is white on white and simply is not there. A real\n"
          + "light effect would ADD and blow out; this cannot. On pale ground give the rays a\n"
          + "colour with somewhere to go — amber against snow, white against stone or night.\n"
          + "Drawn into the background draw list, so they pass under every plugin window and\n"
          + "never steal a click.");

        if (ImGui.Checkbox("Light burst", ref DigTuning.RaysEnabled)) DigTuning.Save();

        ImGui.ColorEdit3("Ray colour", ref DigTuning.RayColor);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderInt("Rays", ref DigTuning.RayCount, 3, 48);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Reach", ref DigTuning.RayReach, 10f, 400f, "%.0f px");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Ray opacity", ref DigTuning.RayOpacity, 0f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Breathe speed", ref DigTuning.RaySpeed, 0.02f, 3f, "%.2f Hz");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Shortest stretch", ref DigTuning.RayMinLength, 0f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Fade shape", ref DigTuning.RayFalloff, 0.2f, 5f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Edge fade", ref DigTuning.RayEdgeFade, 0f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.TextColored(Rule,
            "Edge fade softens each beam's LEFT and RIGHT sides. 0 is a hard-edged wedge.\n"
          + "It also widens the beams as it rises, and it has to: beams tile the full turn\n"
          + "exactly, so a beam fading to nothing inside its own slot would meet its neighbour\n"
          + "where BOTH are zero — a dark seam at every join. Spreading each beam over its\n"
          + "neighbours puts its faded edge on top of theirs rather than beside it.");

        ImGui.TextColored(Rule,
            "The three VARIANCE sliders are how unlike each other the beams are. Width spreads the\n"
          + "angular slices, speed spreads their breathing rates, reach keeps some of them shorter\n"
          + "than the rest. All three at 0 gives a perfectly regular star; the defaults give\n"
          + "something that never quite repeats.\n"
          + "Beams always fill the whole 360 whatever the width spread — the widths are weights\n"
          + "normalised to a full turn, not sizes with gaps between them.");

        ImGui.SliderFloat("Width variance", ref DigTuning.RayWidthVariance, 0f, 3f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Speed variance", ref DigTuning.RaySpeedVariance, 0f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SliderFloat("Reach variance", ref DigTuning.RayReachVariance, 0f, 1f, "%.2f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        if (ImGui.Button("Reset light burst")) { DigTuning.ResetRays(); DigTuning.Save(); }
    }

    private void DrawDigLength()
    {
        ImGui.TextColored(Rule,
            $"How long the dig plays before it ends itself and the shovel comes off.\n"
          + $"SHIPPED DEFAULT: {PropService.DefaultHoldMilliseconds / 1000f:0.##}s — found by testing, and what\n"
          + "every dig uses unless a saved dig-tuning.json overrides it. 0 = no cap: run until the\n"
          + "game cancels it. The cap and the game's own cancel are not alternatives — whichever\n"
          + "comes first wins, so this never stops you walking out of a dig.\n"
          + "Drives PropService, which SHIPS, so this governs EVERY dig action.");

        ImGui.SliderFloat("Dig length (seconds)", ref DigTuning.DigHoldSeconds, 0f, 15f,
                          DigTuning.DigHoldSeconds <= 0f ? "no cap" : "%.2f s");

        // Applied on release rather than every frame. A per-frame Apply would make this slider the
        // authority over PropService's shipped default for as long as the window is open, which is
        // exactly the drift the default-derived initialiser exists to prevent.
        if (ImGui.IsItemDeactivatedAfterEdit()) { DigTuning.Apply(); DigTuning.Save(); }

        ImGui.SameLine();
        ImGui.TextDisabled(Plugin.Props.HoldMilliseconds <= 0
            ? "no cap"
            : $"= {Plugin.Props.EffectiveHoldMilliseconds} ms at {DigTuning.DigSpeed:0.##}x");

        // The two named lengths content will ask for by name.
        if (ImGui.Button($"Short Dig ({PropService.ShortDigMilliseconds / 1000f:0.##}s)"))
        {
            DigTuning.DigHoldSeconds = PropService.ShortDigMilliseconds / 1000f;
            DigTuning.Apply(); DigTuning.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button($"Long Dig ({PropService.LongDigMilliseconds / 1000f:0.##}s)"))
        {
            DigTuning.DigHoldSeconds = PropService.LongDigMilliseconds / 1000f;
            DigTuning.Apply(); DigTuning.Save();
        }
    }

    private void DrawDigSpeed()
    {
        ImGui.TextColored(Rule,
            "Playback multiplier for the dig. 1 = untouched, 2 = twice as fast.\n"
          + "Driven by ActionTimelineSequencer.SetSlotSpeed on the base slot. The speed in force\n"
          + "before we touch it is READ and restored afterwards, so nothing is guessed on the way\n"
          + "out — but note the one gap: if the plugin unloads mid-dig the slot keeps the\n"
          + "multiplier, because teardown may not call game code. Use Reset if that happens.\n"
          + "Length is a length of ANIMATION at 1x, so speed SHORTENS the dig rather than\n"
          + "cramming more of it into the same clock: a 4s dig at 2x ends after 2s having played\n"
          + "exactly the same dig. The two settings are independent.");

        ImGui.SliderFloat("Speed", ref DigTuning.DigSpeed, PropService.MinSpeed, PropService.MaxSpeed, "%.2fx");
        if (ImGui.IsItemDeactivatedAfterEdit()) { DigTuning.Apply(); DigTuning.Save(); }

        int method = DigTuning.DigSpeedMethod;
        if (ImGui.Combo("Method", ref method,
                        "SetSlotSpeed (function)\0TimelineSpeeds[0] (array)\0OverallSpeed (container)\0All three\0"))
        {
            DigTuning.DigSpeedMethod = method;
            DigTuning.Apply();
            DigTuning.Save();
        }

        ImGui.TextColored(Rule,
            "MEASURED 2026-09-09: SetSlotSpeed and TimelineSpeeds[0] each work ON THEIR OWN.\n"
          + "SetSlotSpeed is the default — the narrowest lever that works, and the game's own\n"
          + "setter, so any bookkeeping it does beyond storing the float still happens.\n"
          + "OverallSpeed is NOT needed and scales everything the character does, not just this\n"
          + "animation. Do not make it or All three the default again.");

        ImGui.TextColored(Rule, "LIVE: " + Plugin.Props.SpeedReadout());
        ImGui.TextColored(Rule,
            "Watch this DURING a dig. A value that never changes means the write is not landing;\n"
          + "one that snaps back to 1 means the game is reverting it — those look identical from\n"
          + "outside, which is why all three are shown. The write is repeated every frame.");

        if (ImGui.Button("Reset speed to 1.0")) Say(Plugin.Props.ResetPlaybackSpeed());
    }

    // ── Test 1 ───────────────────────────────────────────────────────────────

    private void DrawHunt()
    {

        ImGui.TextColored(Rule,
            "RULES\n"
          + "  One spot is buried somewhere around you, between the min and max placement range.\n"
          + "  Sense reports an eight-point compass bearing plus a woolly distance word. The\n"
          + "  bearing is a SNAPSHOT — it does not update as you move, so you must sense again.\n"
          + "  The banner warms RED -> YELLOW -> GREEN as you close in, then reads DIG.\n"
          + "  Digging within the dig radius ends it and reports the time. Digging anywhere else\n"
          + "  just plays the animation and costs you the seconds.");

        Commands("/tchal hunt", "/tchal hunt sense", "/tchal hunt dig", "/tchal hunt stop");

        if (ImGui.Button("Start##hunt")) Say(_tests.Start(_tests.Hunt));
        ImGui.SameLine();
        if (ImGui.Button("Sense##hunt")) Say(_tests.Hunt.Sense());
        ImGui.SameLine();
        if (ImGui.Button("Dig##hunt")) Say(_tests.Hunt.Dig());
        ImGui.SameLine();
        if (ImGui.Button("Stop##hunt")) Say(_tests.Hunt.Stop());

        Slider("Nearby / RED (yalms)", ref DigTuning.HuntNearby, 5f, 200f);
        Slider("Warm / YELLOW",        ref DigTuning.HuntWarm,   3f, 150f);
        Slider("Hot / GREEN",          ref DigTuning.HuntHot,    2f, 100f);
        Slider("DIG radius",           ref DigTuning.HuntDig,    0.5f, 40f);
        Slider("Min placement",        ref DigTuning.HuntMinPlacement, 5f, 400f);
        Slider("Max placement",        ref DigTuning.HuntMaxPlacement, 10f, 800f);

        // The bands are only meaningful in descending order. Rather than refusing an edit
        // mid-drag, say so — the lab is for experimenting, and a warning beats a fight.
        if (!(DigTuning.HuntNearby > DigTuning.HuntWarm
           && DigTuning.HuntWarm   > DigTuning.HuntHot
           && DigTuning.HuntHot    > DigTuning.HuntDig))
            ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.45f, 1f),
                "Bands overlap — nearby > warm > hot > dig, or colours will skip.");

        if (DigTuning.HuntMaxPlacement <= DigTuning.HuntMinPlacement)
            ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.45f, 1f),
                "Max placement must exceed min placement.");

        if (ImGui.Button("Reset##hunt")) { DigTuning.ResetHunt(); DigTuning.Save(); }
    }

    // ── Test 2 ───────────────────────────────────────────────────────────────

    private void DrawSite()
    {

        ImGui.TextColored(Rule,
            "RULES\n"
          + "  A square site is marked around you: low gradient walls at its edges, and the ground\n"
          + "  inside painted as a striped grid that follows the terrain.\n"
          + "  Pieces are buried at SPECIFIC spots, spaced so no one dig turns up two. You must dig\n"
          + "  close enough to a spot to get one; a miss costs nothing but the animation, so just\n"
          + "  try again somewhere else.\n"
          + "  One grid cell is about one dig's worth of ground — the stripes show you how\n"
          + "  precisely you have to place a dig, they are not decoration.\n"
          + "  There is no sense and no warming here: sweeping the area is the thing being tested.\n"
          + "  Collect every piece to assemble the relic, which ends it.\n"
          + "  Recovered pieces are marked on the ground. Unfound ones never are.");

        Commands("/tchal site", "/tchal site dig", "/tchal site stop");

        if (ImGui.Button("Start##site")) Say(_tests.Start(_tests.Site));
        ImGui.SameLine();
        if (ImGui.Button("Dig##site")) Say(_tests.Site.Dig());
        ImGui.SameLine();
        if (ImGui.Button("Stop##site")) Say(_tests.Site.Stop());

        Slider("Site size (yalms square)", ref DigTuning.SiteSize, 15f, 300f);
        Slider("Wall height (visual only)", ref DigTuning.SiteWallHeight, 0.2f, 30f);

        // Re-samples the live site on release rather than waiting for the next Start — the grid IS
        // the thing being tuned, so making you restart a survey to see a change would defeat it.
        ImGui.SliderInt("Grid cells (both axes)", ref DigTuning.SiteGridCells, 2, 40);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            DigTuning.Save();
            _tests.Site.RebuildGrid();
        }

        ImGui.ColorEdit3("Wall / floor colour", ref DigTuning.SiteColor);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SameLine();
        if (ImGui.Button("Reset##sitecol"))
        {
            DigTuning.SiteColor = DigTuning.DefaultSiteColor;
            DigTuning.Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(Head, "DEBUG DRAW");

        if (ImGui.Checkbox("Show floor grid", ref DigTuning.SiteShowGrid)) DigTuning.Save();

        if (ImGui.Checkbox("Show buried piece locations + dig radius", ref DigTuning.SiteRevealPieces))
            DigTuning.Save();

        ImGui.TextColored(Rule,
            "The piece markers are the ANSWER KEY — with them on this is not a test. They exist\n"
          + "because a miss and a mis-tuned dig radius look identical from inside the game: the\n"
          + "ring is drawn at exactly SitePieceRadius, so you can see whether you were short.\n"
          + "Cyan ring = still buried. Green ring = already recovered.");

        ImGui.ColorEdit3("Marker colour", ref DigTuning.SiteDebugColor);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();

        ImGui.SameLine();
        if (ImGui.Button("Reset##dbgcol"))
        {
            DigTuning.SiteDebugColor = DigTuning.DefaultDebugColor;
            DigTuning.Save();
        }
        SliderInt("Pieces to bury",        ref DigTuning.SitePieces, 1, 20);
        Slider("Piece dig radius",         ref DigTuning.SitePieceRadius, 0.5f, 30f);
        Slider("Min spacing between pieces", ref DigTuning.SitePieceSpacing, 1f, 100f);
        Slider("Max ground drop across a piece", ref DigTuning.SitePieceMaxDrop, 0.2f, 15f);
        Slider("Max ground STEP (wall detector)", ref DigTuning.SitePieceMaxStep, 0.05f, 3f);
        ImGui.TextColored(Rule,
            "Two different tests, and the second is the one that matters.\n"
          + "  DROP is the total spread across the circle — catches big terrain.\n"
          + "  STEP is the jump between samples half a yalm apart — catches WALLS.\n"
          + "Total height cannot tell a wall from a hill: a 2y rise over a 4y radius is a walkable\n"
          + "26 degree slope, while a 1y garden wall is a SMALLER rise and unusable. Lower the step\n"
          + "if spots still land on kerbs; raise it if nothing will bury on rocky ground.");

        if (DigTuning.SitePieceSpacing <= DigTuning.SitePieceRadius * 2f)
            ImGui.TextColored(new Vector4(0.95f, 0.45f, 0.45f, 1f),
                "Spacing below twice the dig radius lets one dig turn up two pieces.");

        // The honest failure mode of a cramped site: the count silently drops to what fitted.
        // Better to predict it here than to have it look like a bug in the field.
        float area = DigTuning.SiteSize * DigTuning.SiteSize;
        float need = DigTuning.SitePieces * MathF.Pow(DigTuning.SitePieceSpacing, 2f) * 0.8f;
        if (need > area)
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f),
                "Site is likely too small for this many pieces at this spacing — fewer will be buried.");

        if (ImGui.Button("Reset##site")) { DigTuning.ResetSite(); DigTuning.Save(); }
    }

    // ── Test 3 ───────────────────────────────────────────────────────────────

    private void DrawTrail()
    {

        ImGui.TextColored(Rule,
            "RULES\n"
          + "  An ordered chain of spots YOU place, each with a clue YOU write. Digging on a stop\n"
          + "  reveals the clue to the next one. Reach the end and the trail pays out.\n"
          + "  A circular radar appears ONLY within radar range of the current stop and pulses\n"
          + "  faster as you close, going SOLID exactly when a dig will land — that is a promise,\n"
          + "  which is why radar range is clamped above the dig radius.\n"
          + "  A trail MAY cross zones: being in the wrong one is travel, not failure, and the HUD\n"
          + "  names the zone to head for instead of showing a clue you cannot act on. That is the\n"
          + "  ONLY thing that ever replaces the clue line — there is deliberately no \"dig here\"\n"
          + "  message, because the dial already says it where you are looking.\n"
          + "  Starting throws a TRAIL START! banner that slides in from the left and fades where\n"
          + "  it lands; finishing throws TRAIL END! in the same spot, which then leaves to the\n"
          + "  right. Both are fired by the overlay watching this test's state — nothing in here\n"
          + "  reaches into the HUD to play an animation.");

        ImGui.TextColored(Head, "WHY THIS IS AUTHORED AND NOT GENERATED");
        ImGui.TextColored(Rule,
            "It used to place random spots and generate clues like \"NORTH-EAST, far away\". That\n"
          + "is not a clue, it is a search order — a bearing across a whole zone leaves you\n"
          + "sweeping hundreds of yalms unable to tell progress from luck. A clue works because\n"
          + "someone who knows the place wrote something someone who does not can act on, and no\n"
          + "amount of geometry substitutes for that.");

        Commands("/tchal trail", "/tchal trail add", "/tchal trail dig", "/tchal trail clue", "/tchal trail stop");

        if (ImGui.Button("Start##trail")) Say(_tests.Start(_tests.Trail));
        ImGui.SameLine();
        if (ImGui.Button("Dig##trail")) Say(_tests.Trail.Dig());
        ImGui.SameLine();
        if (ImGui.Button("Clue##trail")) Say(_tests.Trail.Recall());
        ImGui.SameLine();
        if (ImGui.Button("Stop##trail")) Say(_tests.Trail.Stop());

        Slider("Dig radius",           ref DigTuning.TrailDig,   0.5f, 30f);
        Slider("Radar appears within", ref DigTuning.TrailRadar, 2f, 150f);
        Slider("Radar fade in/out (s)", ref DigTuning.RadarFadeSeconds, 0.1f, 8f);
        ImGui.TextColored(Rule,
            "The fade is a RATE, not a clip: opacity climbs while in range and falls while out, so\n"
          + "crossing the boundary twice drifts it up and down instead of restarting a timer.");

        DrawTrailAuthoring();

        // Clamped rather than warned about: "solid means the dig lands" is load-bearing for this
        // test, and it stops being true the instant the radar is tighter than the dig radius.
        if (DigTuning.TrailRadar <= DigTuning.TrailDig)
        {
            DigTuning.TrailRadar = DigTuning.TrailDig + 1f;
            ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f),
                "Radar range clamped above the dig radius — see the rules.");
        }

        if (ImGui.Button("Reset##trail")) { DigTuning.ResetTrail(); DigTuning.Save(); }
    }

    /// <summary>
    /// The trail editor: capture a stop where you stand, write its clue, reorder, delete.
    ///
    /// <para>Raw ImGui text fields, which is permitted here — PanacheUI has no multi-line input and
    /// this whole window is dev-only anyway, the same exemption the Challenge Creator has.</para>
    ///
    /// <para>Every edit saves immediately. A trail is twenty minutes of walking around; losing it to
    /// a forgotten Save button, or to a plugin reload between authoring and testing, would be the
    /// worst possible failure for this panel.</para>
    /// </summary>
    private void DrawTrailAuthoring()
    {
        ImGui.Spacing();
        ImGui.TextColored(Head, $"TRAIL STOPS ({DigTrailStore.Stops.Count})");

        if (ImGui.Button("Add stop HERE")) Say(DigTrailStore.AddHere());
        ImGui.SameLine();
        ImGui.TextDisabled("stand where you want the dig spot, then click");

        var player   = Plugin.ObjectTable.LocalPlayer;
        uint here    = Plugin.ClientState.TerritoryType;

        for (int i = 0; i < DigTrailStore.Stops.Count; i++)
        {
            var stop = DigTrailStore.Stops[i];
            ImGui.PushID(i);

            bool sameZone = stop.Territory == here;
            float dist    = sameZone && player != null
                                ? DigGround.Flat(player.Position, stop.Position)
                                : -1f;

            string where = sameZone
                ? (dist >= 0f ? $"{dist:0} yalms away" : "this zone")
                : "another zone";

            ImGui.TextColored(sameZone ? Running : Rule, $"{i + 1}.  {where}");

            ImGui.SameLine();
            if (ImGui.SmallButton("Move to me") && player != null)
            {
                stop.SetPosition(player.Position);
                stop.Territory = here;
                stop.MapId     = PlayerStateReader.MapIdFor((ushort)here);
                DigTrailStore.Save();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Up"))   DigTrailStore.Move(i, -1);
            ImGui.SameLine();
            if (ImGui.SmallButton("Down")) DigTrailStore.Move(i, +1);
            ImGui.SameLine();
            if (ImGui.SmallButton("Delete")) { DigTrailStore.Remove(i); ImGui.PopID(); break; }

            // The clue LEADING TO this stop — so stop 1's clue is what the player starts with.
            string clue = stop.Clue ?? string.Empty;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText($"##clue{i}", ref clue, 400))
                stop.Clue = clue;
            if (ImGui.IsItemDeactivatedAfterEdit()) DigTrailStore.Save();

            if (string.IsNullOrWhiteSpace(stop.Clue))
                ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.35f, 1f),
                    "    no clue written — the player will be told exactly that");

            ImGui.PopID();
        }

        if (DigTrailStore.Stops.Count == 0)
            ImGui.TextDisabled("No stops yet. Walk somewhere and press Add stop HERE.");

        ImGui.Spacing();
        if (ImGui.Button("Clear whole trail")) DigTrailStore.Clear();
        ImGui.SameLine();
        ImGui.TextDisabled("saved to dig-trail.json on every edit");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static void Commands(params string[] cmds)
    {
        ImGui.TextDisabled("COMMANDS");
        foreach (var c in cmds)
        {
            ImGui.SameLine();
            ImGui.TextColored(Cmd, c);
        }
        ImGui.Spacing();
    }

    /// <summary>
    /// A slider that persists on release rather than on every frame of the drag — writing the file
    /// sixty times a second while a slider is held would be the one expensive thing in the lab.
    /// </summary>
    private static void Slider(string label, ref float value, float min, float max)
    {
        ImGui.SliderFloat(label, ref value, min, max, "%.1f");
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();
    }

    private static void SliderInt(string label, ref int value, int min, int max)
    {
        ImGui.SliderInt(label, ref value, min, max);
        if (ImGui.IsItemDeactivatedAfterEdit()) DigTuning.Save();
    }

    private static void Say(string message) => Plugin.ChatGui.Print("[Challenges] " + message);
}
#endif
