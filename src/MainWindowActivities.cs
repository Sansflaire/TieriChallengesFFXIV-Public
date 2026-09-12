using System;
using System.Collections.Generic;

using PanacheUI.Components;
using PanacheUI.Core;

namespace TieriChallengesFFXIV;

/// <summary>
/// The <b>Activity</b> tab — things a player can engage with without limit.
///
/// <para><b>A separate file rather than more of MainWindow.cs, and a partial class rather than a
/// helper object.</b> It needs the window's colour tokens, its layout constants, its
/// <c>Surface()</c> alpha handling and its save callback; handing all of those to a second type
/// would either mean passing six things into a constructor or making them public on the window. The
/// pane is part of the window, so it is written as part of the window and merely kept in its own
/// file.</para>
///
/// <para><b>This ships publicly.</b> The activity is the Wild Trail, the Wild Trail is the roam dig
/// test, and the point of the tab from the day it was asked for was that other people play it — the
/// copy-a-run-for-your-friends button says so on its face. What stayed behind the dev gate is the
/// LAB: the four tests' settings window and the map debug view, which plots the candidate spots and
/// is therefore the answer sheet.</para>
///
/// <para><b>Shipping it makes vnavmesh a real requirement</b>, since nothing else can tell which
/// ground a character can actually reach. That is declared in README.md and docs/HELP.md and refused
/// at runtime with a line the player can act on — see <see cref="ActivityService.Unavailable"/>. A
/// required sibling plugin that is never mentioned anywhere a player reads is indistinguishable from
/// the feature being broken.</para>
/// </summary>
internal sealed partial class MainWindow
{

    /// <summary>The live activity runner. Null until <see cref="Plugin"/> hands it over.</summary>
    public ActivityService? Activities { get; set; }

    /// <summary>Opens the player-facing reference map. Null when PanacheUI could not load.</summary>
    public Action? OnOpenActivityMap { get; set; }

    /// <summary>
    /// Which history row said "Copied!" and until when.
    ///
    /// <para><b>Session state, deliberately not persisted and deliberately not a modal.</b> A copy
    /// is over the instant it happens; the only thing left to do is confirm it landed, and a toast
    /// or a dialog for that would be louder than the action. The row saying so for a second and a
    /// half is the whole feedback.</para>
    /// </summary>
    private int  _copiedRow = -1;
    private long _copiedUntilMs;

    private const long CopiedHoldMs = 1600;

    private bool CopiedNow(int index) =>
        _copiedRow == index && Environment.TickCount64 < _copiedUntilMs;

    // ── master pane ──────────────────────────────────────────────────────────

    /// <summary>The activity the player has selected, falling back to the first one.</summary>
    private ActivityDef? ResolveActivity()
    {
        var all = ActivityCatalog.All;
        if (all.Count == 0) return null;

        foreach (var a in all)
            if (string.Equals(a.Id, _config.SelectedActivity, StringComparison.Ordinal)) return a;

        return all[0];
    }

    private void BuildActivityList(Node scroll, int masterW)
    {
        var selected = ResolveActivity();

        foreach (var a in ActivityCatalog.All)
        {
            if (!_masterSearch.Matches(a.Name)) continue;
            scroll.AppendChild(ActivityRow(masterW, a, selected != null && selected.Id == a.Id));
        }
    }

    private Node ActivityRow(int masterW, ActivityDef activity, bool selected)
    {
        var row = new Node().WithId($"act_{activity.Id}").WithStyle(s =>
        {
            s.Flow            = Flow.Horizontal;
            s.WidthMode       = SizeMode.Fill;
            s.HeightMode      = SizeMode.Fixed; s.Height = RowH_Master;
            s.BackgroundColor = selected ? Accent.WithOpacity(0.09f) : PColor.Transparent;
            if (!selected) s.HoverBackgroundColor = PColor.White.WithOpacity(0.03f);
        });

        string id = activity.Id;
        row.OnClick += _ => { _config.SelectedActivity = id; _save(); };

        row.AppendChild(new Node().WithStyle(s =>
        {
            s.WidthMode       = SizeMode.Fixed; s.Width = SelBarW;
            s.HeightMode      = SizeMode.Fill;
            s.BackgroundColor = selected ? Accent : PColor.Transparent;
            s.PointerEvents   = PointerEvents.None;
        }));

        var inner = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Vertical;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fill;
            s.Padding       = new EdgeSize(9, 10, 0, 10);
            s.Gap           = 4;
            s.PointerEvents = PointerEvents.None;
        });

        inner.AppendChild(new Node().WithText(activity.Name).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 12.5f;
            s.Bold         = true;
            s.Color        = selected ? TextHi : Theme.TextMuted;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        // How many times it has been played, across every map. A count rather than a percentage,
        // because there is nothing to be a percentage OF — that is the whole difference between this
        // tab and the other two.
        int runs = 0;
        foreach (var r in ActivityRuns.All())
            if (string.Equals(r.ActivityId, activity.Id, StringComparison.Ordinal)) runs++;

        inner.AppendChild(new Node()
            .WithText(runs == 0 ? "not played yet" : runs == 1 ? "1 run" : $"{runs} runs")
            .WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = 10f;
                s.Color        = Theme.TextSubtle;
                s.TextOverflow = TextOverflow.Ellipsis;
            }));

        row.AppendChild(inner);
        return row;
    }

    // ── detail pane ──────────────────────────────────────────────────────────

    private Node BuildActivityDetail(int detailW)
    {
        var pane = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fixed; s.Width = detailW;
            s.HeightMode = SizeMode.Fill;

            // THE PANE PAINTS ITS OWN GROUND. Without this the text sat directly on the user's
            // background image and was genuinely unreadable — every other pane in this window goes
            // through Surface(), which is also what makes the image show through at the opacity the
            // player chose instead of being hidden or absent.
            s.BackgroundColor = Surface(Theme.Base);
        });

        var activity = ResolveActivity();

        if (activity == null)
        {
            pane.AppendChild(EmptyNote("No activities yet.", "Nothing has been authored."));
            return pane;
        }

        pane.AppendChild(ActivityHeader(activity));

        var scroll = new Node().WithId("activity_scroll").WithStyle(s =>
        {
            s.Flow        = Flow.Vertical;
            s.WidthMode   = SizeMode.Fill;
            s.HeightMode  = SizeMode.Fill;
            s.OverflowY   = OverflowMode.Scroll;
            s.ClipContent = true;
            s.Padding     = new EdgeSize(10, PadPaneX, 12, PadPaneX);
            s.Gap         = 12;
        });

        // THE HARD REQUIREMENT FIRST, because everything below it is moot without one.
        //
        // Stated in the pane rather than by hiding the tab. A missing tab teaches the player nothing;
        // a line naming the plugin they need is something they can act on — and it is re-read every
        // frame, so installing vnavmesh makes the activity playable without a reload.
        if (ActivityService.Unavailable is { } blocked)
        {
            scroll.AppendChild(EmptyNote("This activity needs vnavmesh.", blocked));
            pane.AppendChild(scroll);
            return pane;
        }

        var maps = ActivityCatalog.AuthoredMaps();

        if (maps.Count == 0)
        {
            // Deliberately does NOT name the dig lab. That window is developer-only, so telling a
            // player to open it describes a door that is not in their build — the exact failure the
            // HELP.md rule exists to prevent, one layer in.
            scroll.AppendChild(EmptyNote(
                "No map is ready for this yet.",
                "Wild Trail is hand-authored per map, and only the authored ones can place a trail. "
              + "If you are standing in a map that should work, check the version below — the "
              + "authored maps ship with the plugin, so an older build does not have them."));

            // THE DIAGNOSIS, ON SCREEN. This message has three unrelated causes — a stale plugin,
            // missing content, or genuinely being somewhere unauthored — and printing only the
            // sentence above made them indistinguishable from a screenshot. Two releases went out
            // fixing real bugs that were not the reported one because of it.
            scroll.AppendChild(DiagnosticNote(ActivityCatalog.Diagnose()));

            pane.AppendChild(scroll);
            return pane;
        }

        scroll.AppendChild(ActivitySetupCard(activity, maps));
        scroll.AppendChild(ActivityHistoryCard(activity, maps));

        pane.AppendChild(scroll);
        return pane;
    }

    private Node ActivityHeader(ActivityDef activity)
    {
        var head = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Padding    = new EdgeSize(12, PadPaneX, 10, PadPaneX);
            s.Gap        = 5;
        });

        head.AppendChild(new Node().WithText(activity.Name).WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 16f;
            s.Bold       = true;
            s.Color      = TextHi;
        }));

        head.AppendChild(new Node().WithText(activity.Blurb).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 11f;
            s.Color        = Theme.TextMuted;
            s.TextOverflow = TextOverflow.Wrap;
            s.MaxLines     = 3;
        }));

        head.AppendChild(PUI.SectionDivider(Accent.WithOpacity(0.25f)));
        return head;
    }

    /// <summary>
    /// Map, difficulty, clue count, and the one button that starts it.
    /// </summary>
    private Node ActivitySetupCard(ActivityDef activity, List<ActivityCatalog.AuthoredMap> maps)
    {
        var card = PUI.Card(Accent);
        card.WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Padding    = new EdgeSize(10, 12);
            s.Gap        = 9;
        });

        card.AppendChild(PUI.SectionLabel("SET UP A RUN", Accent));

        // MAP. One row per authored map; the selected one is the player's current territory when it
        // matches, and otherwise the first. There is exactly one today — see
        // ActivityCatalog.AuthoredMaps for why that is a property of the tuning store rather than of
        // this list.
        var map = maps[0];
        foreach (var m in maps)
            if (m.Territory == Plugin.ClientState.TerritoryType) { map = m; break; }

        bool here    = Plugin.ClientState.TerritoryType == map.Territory;
        bool running = Activities?.Running == true;

        card.AppendChild(LabelledLine("Map", map.Name, here ? StatusOk : Theme.TextMuted));

        card.AppendChild(new Node()
            .WithText(here
                ? "You are standing in it."
                : "Travel here to start — a trail is placed around where you stand.")
            .WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = 10f;
                s.Color        = here ? StatusOk.WithOpacity(0.8f) : Theme.TextSubtle;
                s.TextOverflow = TextOverflow.Wrap;
                s.MaxLines     = 2;
            }));

        // DIFFICULTY — whole numbers 0 to 10, which PUI.Slider quantises for us with step 1. The
        // control is disabled while a run is going: changing the difficulty of a trail already in
        // the ground would change nothing about it, so an enabled slider would be a lie.
        card.AppendChild(PUI.SliderRow(
            "act_diff", "Difficulty", _config.ActivityDifficulty, 0f, 10f, Accent,
            running ? null : v =>
            {
                int want = (int)MathF.Round(v);
                if (want == _config.ActivityDifficulty) return;
                _config.ActivityDifficulty = want;
                _save();
            },
            format: "0", labelWidth: 74f, valueWidth: 30f, sliderWidth: 150f, step: 1f));

        card.AppendChild(new Node().WithText(DifficultyBlurb(_config.ActivityDifficulty)).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 10f;
            s.Color        = Theme.TextSubtle;
            s.TextOverflow = TextOverflow.Wrap;
            s.MaxLines     = 2;
        }));

        card.AppendChild(PUI.SliderRow(
            "act_clues", "Clues", _config.ActivityClues,
            ActivityService.MinClues, ActivityService.MaxClues, Accent,
            running ? null : v =>
            {
                int want = (int)MathF.Round(v);
                if (want == _config.ActivityClues) return;
                _config.ActivityClues = want;
                _save();
            },
            format: "0", labelWidth: 74f, valueWidth: 30f, sliderWidth: 150f, step: 1f));

        // ACTIONS.
        var actions = new Node().WithStyle(s =>
        {
            s.Flow       = Flow.Horizontal;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Gap        = 8;
            s.Margin     = new EdgeSize(4, 0, 0, 0);
        });

        if (running)
        {
            actions.AppendChild(Pill("act_stop", "ABANDON", Danger, () =>
            {
                string r = Activities?.Abandon() ?? string.Empty;
                if (r.Length > 0) Plugin.ChatGui.Print("[Challenges] " + r);
            }));
        }
        else
        {
            var captured = map;
            actions.AppendChild(Pill("act_start", here ? "START" : "START (travel first)",
                                     here ? Accent : Theme.TextSubtle, () =>
            {
                string r = Activities?.Start(activity, captured.Territory, captured.Name,
                                             _config.ActivityDifficulty, _config.ActivityClues)
                           ?? "the activity runner is not available.";

                Plugin.ChatGui.Print("[Challenges] " + r);

                // GET OUT OF THE WAY ONCE IT IS RUNNING. The trail is played in the world — the clue
                // is on the HUD and the dial is on screen — so leaving a full-width window sitting
                // over the top of it is pure obstruction. Sansflaire's call: close on start, and the
                // player reopens it whenever they want.
                //
                // ONLY ON SUCCESS, and asked of the service rather than of the returned sentence.
                // Every refusal — wrong zone, no vnavmesh, nowhere walkable — also returns a string,
                // so closing unconditionally would make the window vanish on the exact press that
                // failed, taking the pane that explains why with it.
                if (Activities?.Running == true) IsVisible = false;
            }));
        }

        if (OnOpenActivityMap != null)
            actions.AppendChild(Pill("act_map", "REFERENCE MAP", QuestBlue, () => OnOpenActivityMap()));

        card.AppendChild(actions);

        // The outcome of the last run, which is the one thing a player wants to see without
        // scrolling back through chat.
        string outcome = Activities?.LastOutcome ?? string.Empty;

        if (outcome.Length > 0)
            card.AppendChild(new Node().WithText(outcome).WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = 10.5f;
                s.Bold         = true;
                s.Color        = outcome.StartsWith("Run cancelled", StringComparison.Ordinal)
                              || outcome.StartsWith("Run abandoned", StringComparison.Ordinal)
                                    ? Danger : StatusOk;
                s.TextOverflow = TextOverflow.Wrap;
                s.MaxLines     = 2;
            }));

        card.AppendChild(new Node()
            .WithText("Changing zone, logging out or entering an instance cancels the run and "
                    + "throws the time away.")
            .WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = 9.5f;
                s.Color        = Theme.TextSubtle;
                s.TextOverflow = TextOverflow.Wrap;
                s.MaxLines     = 3;
            }));

        return card;
    }

    /// <summary>
    /// What a difficulty number actually buys, in the player's words.
    ///
    /// <para><b>Derived from the same two thresholds the clue writer bands on</b>, so the sentence
    /// cannot describe a model the generator no longer uses. It states the fact count because that
    /// is the part of difficulty a player can see in the clue itself.</para>
    /// </summary>
    private static string DifficultyBlurb(int difficulty)
    {
        float hard = Math.Clamp(difficulty, 0, 10) / 10f;

        string facts = hard < 0.34f ? "three facts per clue"
                     : hard < 0.67f ? "two facts per clue"
                     :                "one fact per clue";

        string edge = difficulty <= 1 ? " — as plainly as they can be put"
                    : difficulty >= 9 ? " — and put about as vaguely as it can be"
                    :                   string.Empty;

        return $"{facts}{edge}.";
    }

    /// <summary>
    /// A small monospace-ish block of facts under an empty state, styled as secondary information
    /// rather than as an error — it is context for a screenshot, not a scolding.
    /// </summary>
    private Node DiagnosticNote(string text)
    {
        var card = PUI.Card(Theme.TextSubtle);
        card.WithStyle(s =>
        {
            s.Flow          = Flow.Vertical;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.Padding       = new EdgeSize(8, 10);
            s.Margin        = new EdgeSize(4, PadPaneX, 0, PadPaneX);
            s.Gap           = 3;
            s.PointerEvents = PointerEvents.None;
        });

        card.AppendChild(new Node().WithText("WHAT THE PLUGIN SEES").WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 9f;
            s.Bold       = true;
            s.Color      = Theme.TextSubtle;
        }));

        card.AppendChild(new Node().WithText(text).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 10f;
            s.Color        = Theme.TextMuted;
            s.TextOverflow = TextOverflow.Wrap;
            s.MaxLines     = 8;
        }));

        return card;
    }

    private Node LabelledLine(string label, string value, PColor colour)
    {
        var row = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Horizontal;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.Gap           = 8;
            s.PointerEvents = PointerEvents.None;
        });

        row.AppendChild(new Node().WithText(label).WithStyle(s =>
        {
            s.WidthMode  = SizeMode.Fixed; s.Width = 74f;
            s.HeightMode = SizeMode.Fit;
            s.FontSize   = 10.5f;
            s.Color      = Theme.TextMuted;
        }));

        row.AppendChild(new Node().WithText(value).WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 11.5f;
            s.Bold         = true;
            s.Color        = colour;
            s.TextOverflow = TextOverflow.Ellipsis;
        }));

        return row;
    }

    /// <summary>
    /// Past runs, newest first, each one a click away from being on the clipboard.
    /// </summary>
    private Node ActivityHistoryCard(ActivityDef activity, List<ActivityCatalog.AuthoredMap> maps)
    {
        var card = PUI.Card(QuestBlue);
        card.WithStyle(s =>
        {
            s.Flow       = Flow.Vertical;
            s.WidthMode  = SizeMode.Fill;
            s.HeightMode = SizeMode.Fit;
            s.Padding    = new EdgeSize(10, 12);
            s.Gap        = 7;
        });

        card.AppendChild(PUI.SectionLabel("PAST RUNS", QuestBlue));

        // Every map's runs, not only the selected one. A player who has played two maps wants one
        // history, and the map is named on each row anyway.
        var runs = new List<ActivityRun>();
        foreach (var r in ActivityRuns.All())
            if (string.Equals(r.ActivityId, activity.Id, StringComparison.Ordinal)) runs.Add(r);

        if (runs.Count == 0)
        {
            card.AppendChild(new Node()
                .WithText("Nothing finished yet. A run only counts when you dig up the last clue.")
                .WithStyle(s =>
                {
                    s.WidthMode    = SizeMode.Fill;
                    s.HeightMode   = SizeMode.Fit;
                    s.FontSize     = 10.5f;
                    s.Color        = Theme.TextSubtle;
                    s.TextOverflow = TextOverflow.Wrap;
                    s.MaxLines     = 3;
                }));

            return card;
        }

        card.AppendChild(new Node().WithText("Click a run to copy it for a friend.").WithStyle(s =>
        {
            s.WidthMode    = SizeMode.Fill;
            s.HeightMode   = SizeMode.Fit;
            s.FontSize     = 9.5f;
            s.Color        = Theme.TextSubtle;
            s.PointerEvents = PointerEvents.None;
        }));

        // A SCROLLER INSIDE A FIT-HEIGHT CARD NEEDS A FIXED HEIGHT. A Scroll node whose own height
        // is Fit reports the full content height, so it grows to fit everything and never scrolls —
        // the overflow it is meant to handle simply never happens. The height is a number of rows
        // rather than a pixel guess so it stays right if the row height changes.
        var list = new Node().WithId("activity_runs").WithStyle(s =>
        {
            s.Flow        = Flow.Vertical;
            s.WidthMode   = SizeMode.Fill;
            s.HeightMode  = SizeMode.Fixed;
            s.Height      = RunRowH * MathF.Min(runs.Count, VisibleRunRows) + 2f;
            s.OverflowY   = OverflowMode.Scroll;
            s.ClipContent = true;
            s.Gap         = 0;
        });

        // Fastest first is NOT the order. Newest first is: the list is a log of what you have been
        // doing, and the best time has its own line under the header where it cannot be scrolled
        // past. Sorting by time would bury today's run under a personal best from a fortnight ago.
        for (int i = 0; i < runs.Count; i++) list.AppendChild(RunRow(runs[i], i));

        card.AppendChild(list);
        return card;
    }

    private const float RunRowH        = 34f;
    private const int   VisibleRunRows = 7;

    private Node RunRow(ActivityRun run, int index)
    {
        bool copied = CopiedNow(index);

        var row = new Node().WithId($"run_{index}").WithStyle(s =>
        {
            s.Flow                 = Flow.Horizontal;
            s.WidthMode            = SizeMode.Fill;
            s.HeightMode           = SizeMode.Fixed; s.Height = RunRowH;
            s.Padding              = new EdgeSize(0, 6);
            s.AlignItems           = AlignItems.Center;
            s.Gap                  = 8;
            s.BackgroundColor      = copied ? StatusOk.WithOpacity(0.14f) : PColor.Transparent;
            s.HoverBackgroundColor = PColor.White.WithOpacity(0.04f);
            s.BorderRadius         = 3f;
        });

        row.OnClick += _ =>
        {
            try
            {
                Dalamud.Bindings.ImGui.ImGui.SetClipboardText(run.ShareLine());
                _copiedRow     = index;
                _copiedUntilMs = Environment.TickCount64 + CopiedHoldMs;
            }
            catch (Exception ex)
            {
                Diag.Error($"[Activity] clipboard write failed: {ex.Message}");
            }
        };

        var left = new Node().WithStyle(s =>
        {
            s.Flow          = Flow.Vertical;
            s.WidthMode     = SizeMode.Fill;
            s.HeightMode    = SizeMode.Fit;
            s.Gap           = 1;
            s.PointerEvents = PointerEvents.None;
        });

        left.AppendChild(new Node()
            .WithText($"{CompletionStore.FormatRaceTime(run.Seconds)}   ·   "
                    + $"{run.Clues} clue{(run.Clues == 1 ? "" : "s")}   ·   diff {run.Difficulty}/10")
            .WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = 11f;
                s.Bold         = true;
                s.Color        = TextHi;
                s.TextOverflow = TextOverflow.Ellipsis;
            }));

        left.AppendChild(new Node()
            .WithText($"{run.MapName} · {run.WhenUtc.ToLocalTime():d MMM yyyy, HH:mm}")
            .WithStyle(s =>
            {
                s.WidthMode    = SizeMode.Fill;
                s.HeightMode   = SizeMode.Fit;
                s.FontSize     = 9.5f;
                s.Color        = Theme.TextSubtle;
                s.TextOverflow = TextOverflow.Ellipsis;
            }));

        row.AppendChild(left);

        // The confirmation replaces nothing and moves nothing — it appears in space the row already
        // reserves, so a list of runs does not reflow every time one is clicked.
        row.AppendChild(new Node().WithText(copied ? "Copied!" : string.Empty).WithStyle(s =>
        {
            s.WidthMode     = SizeMode.Fixed; s.Width = 54f;
            s.HeightMode    = SizeMode.Fit;
            s.FontSize      = 10.5f;
            s.Bold          = true;
            s.Color         = StatusOk;
            s.TextAlign     = TextAlign.Right;
            s.PointerEvents = PointerEvents.None;
        }));

        return row;
    }

}
