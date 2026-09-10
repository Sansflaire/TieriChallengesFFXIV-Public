#if DEV_BUILD
using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. The dig-test lab: one clearly-labelled section per test, each carrying its
/// <b>rules, commands, buttons and settings</b> together in one place.
///
/// <para><b>Why everything lives here rather than being spread around.</b> Trist's instruction was
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
        DrawStatus();

        ImGui.Separator();
        DrawShared();

        ImGui.Separator();
        DrawHunt();

        ImGui.Separator();
        DrawSite();

        ImGui.Separator();
        DrawTrail();
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

    private void DrawShared()
    {
        ImGui.TextColored(Head, "SHARED SETTINGS");
        ImGui.TextColored(Rule,
            "Max rise is how far above or below you a buried spot may sit. It is the whole\n"
          + "\"could you actually walk there\" heuristic — it rejects clifftops and ravine floors.");

        Slider("Max rise (yalms)", ref DigTuning.MaxRise, 2f, 80f);

        ImGui.Spacing();
        ImGui.TextColored(Head, "DIG HUD PLACEMENT");
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

        ImGui.Spacing();
        ImGui.TextColored(Head, "DIG ANIMATION LENGTH");
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

        ImGui.Spacing();
        ImGui.TextColored(Head, "DIG ANIMATION SPEED");
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

        ImGui.Spacing();
        if (ImGui.Button("Test dig now")) Say(Plugin.Props.Dig());
        ImGui.SameLine();
        if (ImGui.Button("Stop dig")) Say(Plugin.Props.Stop());

        ImGui.Spacing();
        if (ImGui.Button("Reset ALL tuning")) { DigTuning.ResetAll(); DigTuning.Save(); }
        ImGui.SameLine();
        ImGui.TextDisabled("saved to dig-tuning.json — delete it for defaults");
    }

    // ── Test 1 ───────────────────────────────────────────────────────────────

    private void DrawHunt()
    {
        ImGui.TextColored(Head, "TEST 1 — SENSE HUNT");

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
        ImGui.TextColored(Head, "TEST 2 — AREA SURVEILLANCE");

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
        ImGui.TextColored(Head, "TEST 3 — CLUE TRAIL");

        ImGui.TextColored(Rule,
            "RULES\n"
          + "  An ordered chain of spots YOU place, each with a clue YOU write. Digging on a stop\n"
          + "  reveals the clue to the next one. Reach the end and the trail pays out.\n"
          + "  A circular radar appears ONLY within radar range of the current stop and pulses\n"
          + "  faster as you close, going SOLID exactly when a dig will land — that is a promise,\n"
          + "  which is why radar range is clamped above the dig radius.\n"
          + "  A trail MAY cross zones: being in the wrong one is travel, not failure, and the HUD\n"
          + "  names the zone to head for instead of showing a clue you cannot act on.\n"
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
