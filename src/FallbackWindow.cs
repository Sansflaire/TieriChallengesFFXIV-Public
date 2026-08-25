using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// The plain-ImGui rendering of the main window, used when PanacheUI is switched off or is not
/// loadable at all.
///
/// <para><b>This is a deliberate, documented exception to the PanacheUI mandate</b> in
/// devPlugins/CLAUDE.md. The mandate says every window is built from Panache nodes; that rule
/// cannot be honoured by the very code whose job is to keep the plugin usable when Panache is
/// the thing that is missing. Keeping this fallback plain is the point — it must not reference a
/// PanacheUI type anywhere, including in field declarations, or the CLR will fail to load this
/// class for exactly the reason it exists. See <see cref="PanacheAvailability"/>.</para>
///
/// <para>It is intentionally utilitarian rather than pretty. It exists so a user with a broken
/// or missing PanacheUI can still read and use their challenges, not to be a second design.</para>
/// </summary>
internal sealed class FallbackWindow
{
    // Aliased onto DialogTheme's exact values so this renderer cannot visually drift from the
    // popups or the main window — same fix applied to Dialogs.cs's constants on 2026-08-24.
    private static readonly Vector4 ColAccent = DialogTheme.Accent;
    private static readonly Vector4 ColOk     = DialogTheme.StatusOk;
    private static readonly Vector4 ColMuted  = DialogTheme.TextMuted;
    private static readonly Vector4 ColDanger = DialogTheme.Danger;
    private static readonly Vector4 ColHint   = new(0.66f, 0.79f, 0.94f, 1.00f);

    /// <summary>"  ·  best 41.20s" for a race that has been finished, else nothing.</summary>
    private string RaceBestSuffix(ChallengeDef def)
    {
        if (def.Kind != ChallengeKind.RaceTimer) return string.Empty;

        double? best = _store.BestRaceTime(def.Id);
        return best.HasValue ? $"   ·   best {CompletionStore.FormatRaceTime(best.Value)}" : string.Empty;
    }

    /// <summary>
    /// Challenges whose hint is revealed, by GUID. Session-only and per-renderer, for the same
    /// reason as <c>MainWindow._hintShown</c>: a hint is asked for in the moment, and one left
    /// open across sessions would spoil the challenge on every reopen.
    /// </summary>
    private readonly System.Collections.Generic.HashSet<string> _hintShown =
        new(StringComparer.Ordinal);

    private readonly Configuration    _config;
    private readonly CompletionStore  _store;
    private readonly ChallengeTracker _tracker;
    private readonly Dialogs          _dialogs;
    private readonly ChallengeSyncService _sync;
    private readonly Action           _save;
    private readonly Action           _onRestore;

    public FallbackWindow(Configuration config, CompletionStore store, ChallengeTracker tracker,
                          Dialogs dialogs, ChallengeSyncService sync, Action save, Action onRestore)
    {
        _config    = config;
        _store     = store;
        _tracker   = tracker;
        _dialogs   = dialogs;
        _sync      = sync;
        _save      = save;
        _onRestore = onRestore;
    }

    /// <summary>How long a revealed challenge keeps its marker after the button is clicked.</summary>
    private const double FocusHighlightSeconds = 10.0;

    private string?  _focusId;
    private DateTime _focusUntil = DateTime.MinValue;

    /// <summary>
    /// Set by a reveal, cleared the frame the scroll is applied. Without this the list would be
    /// re-centred every frame for the whole highlight window, pinning it against the user trying
    /// to scroll away.
    /// </summary>
    private bool _focusScrollPending;

    /// <summary>
    /// Reveal a challenge: select its category and scroll the list to it. Counterpart to
    /// <see cref="MainWindow.FocusChallenge"/>, and actually the more precise of the two —
    /// ImGui can scroll to a specific item, which PanacheUI has no API for.
    /// </summary>
    public void FocusChallenge(string challengeId, string category)
    {
        if (!string.IsNullOrWhiteSpace(category)
            && !string.Equals(category, _config.SelectedCategory, StringComparison.Ordinal))
        {
            _config.SelectedCategory = category;
            _save();
        }

        // Grouped by zone, the category selection reveals nothing — that pane is keyed by
        // territory, so move the zone selection too and open its expansion.
        if (_config.Grouping == GroupMode.Zones)
        {
            ZoneIndex.Reveal(_config, ZoneIndex.TerritoryOf(_config, challengeId));
            _save();
        }

        _focusId            = challengeId;
        _focusUntil         = DateTime.UtcNow.AddSeconds(FocusHighlightSeconds);
        _focusScrollPending = true;
    }

    private bool IsFocused(string id) =>
        _focusId != null
        && DateTime.UtcNow < _focusUntil
        && string.Equals(id, _focusId, StringComparison.Ordinal);

    /// <summary>Wired by Plugin. Opens the live-state popup, same as the Panache renderer's.</summary>
    public Action? OnOpenStatus;

    /// <summary>
    /// Put the window back in the middle of the screen on the next frame. Mirror of
    /// <c>MainWindow.RequestCenter</c> — see there for why this is a deferred request.
    /// </summary>
    public void RequestCenter() => _centerPending = true;

    private bool _centerPending;

    public void Draw(ref bool isVisible)
    {
        ImGui.SetNextWindowSize(new Vector2(_config.WindowWidth, _config.WindowHeight), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 320), new Vector2(1600, 1800));

        if (_centerPending)
        {
            _centerPending = false;
            var vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.Pos + vp.Size * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        }

        // This is the surface a player actually sees when PanacheUI is off or missing — the
        // standing rule (2026-08-24) that every public-facing surface matches the main window's
        // style applies here too, same as the popups in Dialogs.cs and StatusWindow.
        DialogTheme.Push();

        // Keeps the title bar here on purpose: without Panache there is no custom chrome, so the
        // window would otherwise have no way to be closed or identified.
        if (!ImGui.Begin("FFXIV Miscellaneous Challenges##tc_fallback", ref isVisible))
        {
            ImGui.End();
            DialogTheme.Pop();
            return;
        }

        DrawHeader();
        ImGui.Separator();
        DrawBody();

        ImGui.End();
        DialogTheme.Pop();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(ColAccent, "FFXIV Miscellaneous Challenges");
        ImGui.SameLine();
        ImGui.TextDisabled(PluginVersion.Display);

        // Live game state moved behind the Info button — see StatusWindow. Same change as the
        // Panache renderer, so the two stay the same shape.
        ImGui.SameLine();
        if (ImGui.SmallButton("Info##tc_fb_info")) OnOpenStatus?.Invoke();

        var (done, total) = ChallengeCatalog.OverallProgress(_config, _store);
        float frac = ChallengeCatalog.Percent(done, total);

        ImGui.Spacing();
        ImGui.TextUnformatted($"ALL CHALLENGES   {done} of {total}  ·  {frac * 100f:0}%");
        ImGui.ProgressBar(frac, new Vector2(-1, 6), string.Empty);

        ImGui.Spacing();
        DrawPanacheToggle();

        ImGui.SameLine();
        if (_sync.IsRunning) ImGui.BeginDisabled();
        if (ImGui.Button(_sync.IsRunning ? "Syncing…" : "Sync", new Vector2(90, 24)))
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var r = await _sync.SyncAsync();
                Plugin.ChatGui.Print("[Challenges] " + r.Message);
                _tracker.Invalidate();
            });
        }
        if (_sync.IsRunning) ImGui.EndDisabled();

        if (SuggestionService.IsConfigured)
        {
            ImGui.SameLine();
            if (ImGui.Button("Suggest", new Vector2(90, 24))) _dialogs.RequestSuggestion();
            ImGui.SameLine();
            if (ImGui.Button("Report Bug", new Vector2(110, 24))) _dialogs.RequestBugReport();
        }

        int recoverable = _store.PermanentCount - _store.CurrentCount;
        if (recoverable > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button($"Restore {recoverable}", new Vector2(110, 24))) _onRestore();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset", new Vector2(80, 24))) _dialogs.RequestReset();

#if DEV_BUILD
        // The preview toggle has to exist in BOTH renderers. It previously lived only in the
        // Panache window, so switching PanacheUI off made it unreachable — which is exactly the
        // state a developer checking the fallback ends up in.
        if (!_config.PublicPreview)
        {
            ImGui.SameLine();
            if (ImGui.Button("Preview public", new Vector2(130, 24)))
            {
                _config.PublicPreview = true;
                _save();
                Plugin.ChatGui.Print("[Challenges] Public preview ON — /tchallenges preview to exit.");
            }
        }
#endif
    }

    /// <summary>
    /// The renderer switch. Greyed out with an explanation when the library could not be loaded,
    /// because a toggle that silently does nothing is worse than a disabled one.
    /// </summary>
    private void DrawPanacheToggle()
    {
        bool available = PanacheAvailability.IsAvailable;

        if (!available) ImGui.BeginDisabled();

        if (ImGui.Button(_config.UsePanacheUI ? "PanacheUI: ON" : "PanacheUI: OFF", new Vector2(140, 24)))
        {
            _config.UsePanacheUI = !_config.UsePanacheUI;
            _save();
        }

        if (!available) ImGui.EndDisabled();

        if (!available)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColDanger, "No PanacheUI loaded");
            if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(PanacheAvailability.FailureReason))
                ImGui.SetTooltip($"PanacheUI could not be loaded: {PanacheAvailability.FailureReason}\n"
                               + "Check that PanacheUI.dll, SkiaSharp.dll and libSkiaSharp.dll sit\n"
                               + "next to TieriChallengesFFXIV.dll.");
        }
    }

    private void DrawBody()
    {
        // Same rule as the Panache renderer: empty categories are an authoring affordance, not a
        // player-facing one.
#if DEV_BUILD
        bool showEmpty = !_config.PublicPreview;
#else
        const bool showEmpty = false;
#endif
        var categories = ChallengeCatalog.Categories(_config, includeEmpty: showEmpty);
        if (categories.Count == 0)
        {
            // Same rule as the Panache renderer: an empty catalogue is normal before the first
            // sync, so say what to do rather than implying a fault.
            ImGui.TextColored(ColAccent, "No challenges yet.");
            bool neverSynced = _config.LastSyncUtc == DateTime.MinValue;
            ImGui.TextWrapped(neverSynced
                ? "Press Sync above to download the challenge list."
                : $"Synced {CompletionStore.FormatDate(_config.LastSyncUtc)} — no challenges are "
                + "published yet. New ones are added over time; press Sync again later.");

            ImGui.Spacing();
            if (ImGui.Button("Sync now", new Vector2(120, 26)))
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    var r = await _sync.SyncAsync();
                    Plugin.ChatGui.Print("[Challenges] " + r.Message);
                    _tracker.Invalidate();
                });
            }
            return;
        }

        string selected = ResolveSelection(categories);
        bool   zones    = _config.Grouping == GroupMode.Zones;

        float masterW = MathF.Max(140f, ImGui.GetContentRegionAvail().X / 3f);

        // What the detail pane is showing. Resolved once, so the two panes cannot disagree.
        string title = zones
            ? (_config.SelectedTerritory >= 0 ? ZoneIndex.DisplayName(_config, (uint)_config.SelectedTerritory) : string.Empty)
            : selected;

        var list = zones
            ? (_config.SelectedTerritory >= 0
                ? ZoneIndex.InZone(_config, (uint)_config.SelectedTerritory)
                : new System.Collections.Generic.List<ChallengeDef>())
            : ChallengeCatalog.InCategory(_config, selected);

        if (ImGui.BeginChild("##tc_fb_master", new Vector2(masterW, 0), true))
        {
            // Same segmented control as the Panache renderer, in the plainest form ImGui has.
            if (ImGui.RadioButton("Category##tc_fb_group", !zones) && zones)
            {
                _config.Grouping = GroupMode.Categories;
                _save();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Zone##tc_fb_group", zones) && !zones)
            {
                _config.Grouping = GroupMode.Zones;
                // Land on the zone you are standing in, matching MainWindow.SelectCurrentZone.
                // Kept in step deliberately: the two renderers differing on where a tab lands is
                // the kind of drift that makes the fallback feel like a lesser plugin.
                SelectCurrentZone();
                _save();
            }
            ImGui.Separator();

            if (zones) DrawZoneMaster();
            else       DrawCategoryMaster(categories, selected);
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("##tc_fb_detail", new Vector2(0, 0), true))
        {
            // Deliberately not an early return: EndChild is called unconditionally below, and a
            // return from inside here would leave it paired with a second call.
            if (string.IsNullOrEmpty(title))
                ImGui.TextDisabled(zones ? "Pick a zone on the left." : "Pick a category on the left.");

            int dd = 0;
            foreach (var d in list) if (_store.IsComplete(d.Id)) dd++;
            int dt = list.Count;

            // Same difficulty ceiling the Panache renderer applies, and the same rule: unrated
            // challenges are never filtered out, and the done/total counts stay whole-category so
            // the progress line does not lurch when the filter moves. Turning PanacheUI off must
            // not silently change which challenges exist.
            var shown = new List<ChallengeDef>(list.Count);
            foreach (var d in list)
                if (!d.HasDifficulty || d.Difficulty <= _config.MaxDifficulty) shown.Add(d);
            int hiddenByFilter = list.Count - shown.Count;

            if (!string.IsNullOrEmpty(title))
            {
                ImGui.TextColored(ColAccent, title);

                // The control, in the plainest form ImGui has. Text pips rather than icons for
                // the same reason the difficulty meter uses them here — this path exists for when
                // the icon renderer is unavailable.
                ImGui.SameLine();
                for (int i = 1; i <= 5; i++)
                {
                    bool lit = i <= _config.MaxDifficulty;
                    if (ImGui.SmallButton($"{(lit ? '●' : '○')}##tc_fb_df{i}"))
                    {
                        _config.MaxDifficulty = _config.MaxDifficulty == i ? 5 : i;
                        _save();
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(i == 5
                            ? "Show every difficulty."
                            : $"Show difficulty {i} and below; hide anything harder.");
                    if (i < 5) ImGui.SameLine();
                }

                string line = $"{dd} of {dt} done  ·  {ChallengeCatalog.Percent(dd, dt) * 100f:0}% "
                            + $"of this {(zones ? "zone" : "category")}";
                if (hiddenByFilter > 0) line += $"  ·  {hiddenByFilter} hidden by filter";
                ImGui.TextUnformatted(line);
                ImGui.Separator();
            }

            list = shown;

            // Numbered against THIS list, exactly as MainWindow does it: whatever the pane is
            // showing, rows read 1, 2, 3 from the top. Keep the two in step — a player switching
            // renderers must not see the same challenge under two different numbers.
            int rowNumber = 0;

            foreach (var def in list)
            {
                rowNumber++;
                bool done    = _store.IsComplete(def.Id);
                bool focused = IsFocused(def.Id);

                // Same rule as MainWindow.ChallengeRow: completing it means you were there, so it
                // stops being a spoiler; dev builds bypass this outside public-preview, where the
                // whole point is to show exactly what a player sees.
                bool devBypass = false;
#if DEV_BUILD
                devBypass = !_config.PublicPreview;
#endif
                bool spoilered = !done && !devBypass
                               && AttunementService.IsZoneSpoilered(_config, ZoneIndex.TerritoryOf(_config, def.Id));

                string shownTitle = spoilered ? "??? Challenge"
                    : string.IsNullOrWhiteSpace(def.Title) ? "(unnamed challenge)" : def.Title;

                ImGui.TextColored(focused    ? ColAccent
                                 : done      ? ColOk
                                 : spoilered ? ColMuted
                                             : new Vector4(0.92f, 0.92f, 0.92f, 1f),
                                  $"{(focused ? "▸ " : string.Empty)}"
                                + $"{(done ? "[x]" : "[ ]")}  #{rowNumber}  {shownTitle}");

                // Applied once per reveal, not every frame — otherwise the list would fight the
                // user for the scrollbar for the whole highlight window.
                if (focused && _focusScrollPending)
                {
                    ImGui.SetScrollHereY(0.5f);
                    _focusScrollPending = false;
                }

                // Live step progress for multi-area challenges. This renderer previously showed
                // none, so switching PanacheUI off silently lost the "2/4" readout. Suppressed
                // when spoilered — a step count still confirms "something is here".
                if (!done && !spoilered)
                {
                    var src = ChallengeCatalog.FindCustom(_config, def.Id);
                    if (src != null && src.ShowProgress
                        && _tracker.TryGetProgress(src, out int step, out int total) && total > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(step > 0 ? ColAccent : ColMuted, $"({step}/{total})");
                    }
                }

                // Difficulty meter. This renderer previously showed none at all, so turning
                // PanacheUI off silently dropped the rating the same way it once dropped the step
                // count above. Text pips rather than icons because this path exists precisely for
                // when the icon renderer is unavailable — see ChallengeDef.DifficultyMeter for why
                // they are circles and not stars.
                //
                // Hidden when spoilered, matching MainWindow.ChallengeRow: how hard something is
                // is a strong hint about what it involves.
                if (def.HasDifficulty && !spoilered)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColAccent, def.DifficultyMeter());
                }

                if (!def.IsOfficial)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColMuted, "[CUSTOM]");
                }

                if (done)
                {
                    var when = _store.CompletedAt(def.Id);
                    if (when.HasValue)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ColOk, $"— Complete on {CompletionStore.FormatDate(when.Value)} !");
                    }
                }

                // Hint control. Present on every challenge; live only where a hint was actually
                // written, so it never promises something it cannot show. Withheld entirely when
                // spoilered — a hint's whole job is helping find something, which is exactly what
                // the mask is hiding.
                ImGui.SameLine();
                bool hintOpen = !spoilered && def.HasHint && _hintShown.Contains(def.Id);

                if (spoilered)
                {
                    ImGui.TextDisabled("[???]");
                }
                else if (!def.HasHint)
                {
                    ImGui.TextDisabled("[no hint]");
                }
                else if (ImGui.SmallButton($"{(hintOpen ? "Hide hint" : "Hint")}##hint_{def.Id}"))
                {
                    if (!_hintShown.Remove(def.Id)) _hintShown.Add(def.Id);
                    hintOpen = !hintOpen;
                }

                // Race controls, in parity with the Panache row. This is the only way to start a
                // race once the corner prompt is suppressed, so it cannot be Panache-only.
                if (!spoilered && def.Kind == ChallengeKind.RaceTimer)
                {
                    if (_tracker.IsRaceRunning(def.Id))
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Abandon##race_{def.Id}")) _tracker.AbandonRace();
                    }
                    else if (_tracker.IsRaceArmed(def.Id))
                    {
                        ImGui.SameLine();
                        if (ImGui.SmallButton($"Start!##race_{def.Id}")
                            && !_tracker.TryStartRace(def.Id))
                        {
                            Plugin.ChatGui.PrintError(
                                "[Challenges] Stand in the start area to begin the run.");
                        }
                    }
                }

                // The hint replaces the description rather than joining it — asking for a hint
                // should not leave you reading both lines to find the new one.
                if (spoilered)
                    ImGui.TextColored(ColMuted, "      Explore this zone to reveal this challenge.");
                else if (hintOpen)
                    ImGui.TextColored(ColHint, $"      Hint: {def.Hint}");
                else if (def.Kind == ChallengeKind.RaceTimer && _tracker.IsRaceRunning(def.Id))
                    ImGui.TextColored(ColOk,
                        $"      Running — {CompletionStore.FormatRaceTime(_tracker.RunningElapsedSeconds)}");
                else if (def.Kind == ChallengeKind.RaceTimer && _tracker.IsRaceArmed(def.Id))
                    ImGui.TextColored(ColAccent, "      Ready to start timed challenge?");
                else if (!string.IsNullOrWhiteSpace(def.Detail))
                    ImGui.TextDisabled($"      {def.Detail}{RaceBestSuffix(def)}");

#if DEV_BUILD
                // spoilered is unreachable here: it requires devBypass == false, i.e.
                // _config.PublicPreview == true, which is exactly the condition this block
                // already excludes.
                if (!_config.PublicPreview)
                {
                    if (!def.HasDetails)
                        ImGui.TextColored(ColDanger, "      Missing details");
                    else if (!def.HasDetector)
                        ImGui.TextDisabled("      no detector: nothing can complete this");
                }
#endif

                ImGui.Spacing();
            }
        }
        ImGui.EndChild();
    }

    private void DrawCategoryMaster(System.Collections.Generic.List<string> categories, string selected)
    {
        foreach (var cat in categories)
        {
            var (cd, ct) = ChallengeCatalog.CategoryProgress(_config, _store, cat);
            bool isSel = string.Equals(cat, selected, StringComparison.Ordinal);

            if (ImGui.Selectable($"{cat}##tc_fb_cat_{cat}", isSel))
            {
                _config.SelectedCategory = cat;
                _save();
            }

            ImGui.SameLine();
            ImGui.TextColored(cd == ct && ct > 0 ? ColOk : ColMuted, $"{cd}/{ct}");
        }
    }

    /// <summary>
    /// Expansions with their zones beneath, matching the Panache renderer. ImGui supplies a real
    /// tree widget, so this one gets collapsing for free rather than hand-rolling it.
    /// </summary>
    /// <summary>
    /// Select the zone the player is standing in and uncollapse its expansion, so switching to
    /// the Zone tab lands somewhere useful. Mirror of <c>MainWindow.SelectCurrentZone</c> — see
    /// that one for the reasoning; the two must not drift.
    /// </summary>
    private void SelectCurrentZone()
    {
        uint here = (uint)Plugin.ClientState.TerritoryType;
        if (here == 0) return;

        _config.SelectedTerritory = (int)here;

        foreach (var exp in ZoneIndex.Expansions(_config))
        {
            foreach (var zone in exp.Zones)
            {
                if (zone.TerritoryId != here) continue;
                _config.CollapsedExpansions.Remove(exp.Id);
                return;
            }
        }
    }

    private void DrawZoneMaster()
    {
        var expansions = ZoneIndex.Expansions(_config);
        if (expansions.Count == 0)
        {
            ImGui.TextDisabled("The game's zone list could not be read.");
            return;
        }

        var counts = ZoneIndex.Tally(_config, _store);

        bool only = _config.ZonesWithChallengesOnly;
        if (ImGui.Checkbox("Only zones with challenges##tc_fb_zonefilter", ref only))
        {
            _config.ZonesWithChallengesOnly = only;
            _save();
        }

        foreach (var expansion in expansions)
        {
            var (exDone, exTotal) = counts.Of(expansion);
            if (only && exTotal == 0) continue;

            bool collapsed = _config.CollapsedExpansions.Contains(expansion.Id);

            ImGui.SetNextItemOpen(!collapsed, ImGuiCond.Always);
            bool open = ImGui.TreeNodeEx(
                $"{expansion.Name}{(exTotal > 0 ? $"  ({exDone}/{exTotal})" : string.Empty)}"
              + $"##tc_fb_exp_{expansion.Id}",
                ImGuiTreeNodeFlags.SpanAvailWidth);

            // The tree's own open state is not the source of truth — the config is, so the
            // choice survives a relaunch. Write back only when the user actually toggled it.
            if (open == collapsed)
            {
                if (open) _config.CollapsedExpansions.Remove(expansion.Id);
                else      _config.CollapsedExpansions.Add(expansion.Id);
                _save();
            }

            if (!open) continue;

            if (expansion.Zones.Count == 0)
                ZoneSelectable(ZoneIndex.AnyZone, counts.Zone(ZoneIndex.AnyZone));

            foreach (var zone in expansion.Zones)
            {
                var tally = counts.Zone(zone.TerritoryId);
                if (only && tally.Total == 0) continue;
                ZoneSelectable(zone.TerritoryId, tally);
            }

            ImGui.TreePop();
        }
    }

    private void ZoneSelectable(uint territoryId, (int Done, int Total) tally)
    {
        bool isSel     = _config.SelectedTerritory >= 0 && (uint)_config.SelectedTerritory == territoryId;
        bool spoilered = AttunementService.IsZoneSpoilered(_config, territoryId);
        bool empty     = tally.Total == 0 || spoilered;

        // Routed through DisplayName rather than the raw name passed in previously — a masked
        // list entry that leaks its real name via the Selectable label defeats the mask entirely.
        string displayName = ZoneIndex.DisplayName(_config, territoryId);

        if (empty) ImGui.PushStyleColor(ImGuiCol.Text, ColMuted);
        if (ImGui.Selectable($"{displayName}##tc_fb_zone_{territoryId}", isSel))
        {
            _config.SelectedTerritory = (int)territoryId;
            _save();
        }
        if (empty) ImGui.PopStyleColor();

        // Right-click-to-teleport. Native ImGui item detection — no workaround needed here the
        // way the Panache renderer needs one; this is the one thing plain ImGui does that
        // PanacheUI currently cannot.
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            HandleZoneRightClick(territoryId, displayName);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Right-click to teleport to {displayName}");

        if (empty) return;

        ImGui.SameLine();
        ImGui.TextColored(tally.Done == tally.Total ? ColOk : ColMuted, $"{tally.Done}/{tally.Total}");
    }

    /// <summary>Same teleport logic as the Panache renderer's <c>MainWindow.HandleZoneRightClick</c>.</summary>
    private static void HandleZoneRightClick(uint territoryId, string zoneName)
    {
        switch (AttunementService.TryTeleport(territoryId))
        {
            case AttunementService.TeleportOutcome.Dispatched:
                break;
            case AttunementService.TeleportOutcome.NoAetheryteInZone:
                FlyTextService.ShowError("No Aetheryte", $"None exists in {zoneName}");
                break;
            case AttunementService.TeleportOutcome.NotAttuned:
                FlyTextService.ShowError("Not Attuned", $"Visit an aetheryte in {zoneName} first");
                break;
            case AttunementService.TeleportOutcome.Failed:
                FlyTextService.ShowError("Teleport Failed", "Try again");
                break;
        }
    }

    /// <summary>Selection is by category name, never index — same rule as the Panache renderer.</summary>
    private string ResolveSelection(System.Collections.Generic.List<string> categories)
    {
        foreach (var c in categories)
            if (string.Equals(c, _config.SelectedCategory, StringComparison.Ordinal)) return c;
        return categories[0];
    }

    // DescribeCharacter/DescribeZone were duplicated here and in MainWindow. Both copies are gone;
    // StatusWindow holds the one implementation and both renderers open it.
}
