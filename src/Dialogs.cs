using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// The reset confirmation and the suggestion box. Both are standard ImGui modals, which
/// DESIGN_SYSTEM §10 anti-pattern 8 explicitly permits.
///
/// <para>They live here, outside either renderer, for two reasons: the Panache window and the
/// plain-ImGui fallback must not each carry a copy, and — more importantly — this type contains
/// <b>no PanacheUI types at all</b>, so it still works when the library is missing. See
/// <see cref="PanacheAvailability"/>.</para>
///
/// <para>Everything is drawn at root ImGui scope, outside any window's Begin/End, so a modal
/// opened from a chat command works even with the main window shut.</para>
/// </summary>
internal sealed class Dialogs
{
    private const string ResetPopupId      = "ARE YOU SURE!?##tc_reset_confirm";
    private const string AppearancePopupId = "Window appearance##tc_appearance";
    private const string UiScalePopupId    = "UI scale##tc_uiscale";
    /// <summary>
    /// Everything after <c>###</c> is the ImGui identity, so the visible title can change
    /// between suggestion and bug-report mode while OpenPopup and BeginPopupModal still agree.
    /// </summary>
    private string SuggestPopupId =>
        (_bugMode ? "Report a bug" : "Send a suggestion") + "###tc_suggest";

    // Aliased onto DialogTheme's exact values rather than kept as separately-tuned constants —
    // this file was where the drift started (these predate DialogTheme by several versions).
    private static readonly Vector4 ColOk     = DialogTheme.StatusOk;
    private static readonly Vector4 ColWarn   = new(0.85f, 0.60f, 0.35f, 1f);
    private static readonly Vector4 ColDanger = DialogTheme.Danger;

    private readonly Configuration    _config;
    private readonly CompletionStore  _store;
    private readonly ChallengeTracker _tracker;
    private readonly Action           _save;

    // Reset modal.
    private bool _resetRequested;
    private bool _resetPopupOpen;

    // Suggestion modal. InputText needs `ref string`, hence fields.
    private bool   _suggestRequested;
    private bool   _suggestPopupOpen;
    private string _suggestText    = string.Empty;
    private string _suggestContact = string.Empty;
    private volatile bool _suggestSending;
    private string _suggestStatus = string.Empty;
    private bool   _suggestOk;

    /// <summary>Bug-report mode attaches the plugin log; suggestion mode does not.</summary>
    private bool _bugMode;

    public Dialogs(Configuration config, CompletionStore store, ChallengeTracker tracker, Action save)
    {
        _config  = config;
        _store   = store;
        _tracker = tracker;
        _save    = save;
    }

    // Appearance modal. PanacheUI has no text-input component, so the image path has to be typed
    // into an ImGui popup — the same reason the Challenge Creator is ImGui.
    private bool   _appearanceRequested;
    private bool   _appearancePopupOpen;
    private string _appearancePath = string.Empty;

    // UI scale modal. No draft field: the steps apply live, so there is nothing to stage.
    private bool _uiScaleRequested;
    private bool _uiScalePopupOpen;

    /// <summary>True while a modal owns the mouse, so a renderer can go inert behind it.</summary>
    public bool AnyOpen => _resetPopupOpen || _suggestPopupOpen || _appearancePopupOpen
                        || _uiScalePopupOpen;

    public void RequestReset()      => _resetRequested = true;
    public void RequestSuggestion() { _bugMode = false; _suggestRequested = true; }
    public void RequestBugReport()  { _bugMode = true;  _suggestRequested = true; }
    public void RequestUiScale()    => _uiScaleRequested = true;

    public void RequestAppearance()
    {
        // Seeded from config each time it opens, so a cancelled edit cannot leave stale text
        // sitting in the box for next time.
        _appearancePath      = _config.BackgroundImagePath ?? string.Empty;
        _appearanceRequested = true;
    }

    public void Draw()
    {
        DrawResetConfirm();
        DrawSuggestion();
        DrawAppearance();
        DrawUiScale();
    }

    // ── UI scale ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Three fixed steps rather than a slider — see <see cref="Configuration.UiScale"/> for why a
    /// free-form multiplier is the wrong shape here.
    ///
    /// <para>Applies live, with no Apply button. The window being resized is sitting right behind
    /// this modal, so a staged edit would hide the only thing worth looking at while choosing.
    /// Nothing needs invalidating: the renderer reads the step through a property every frame.</para>
    /// </summary>
    private void DrawUiScale()
    {
        if (_uiScaleRequested)
        {
            _uiScaleRequested = false;
            _uiScalePopupOpen = true;
            ImGui.OpenPopup(UiScalePopupId);
        }

        if (!_uiScalePopupOpen) return;

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.Appearing);

        DialogTheme.Push();

        bool open = true;
        if (ImGui.BeginPopupModal(UiScalePopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("How large the challenge window's text and rows are drawn. "
                            + "The change is immediate — the window behind this one is already "
                            + "showing it.");
            ImGui.Spacing();

            ScaleOption(1, "1  —  Compact",  "The original sizing.");
            ScaleOption(2, "2  —  Larger",   "About 15% bigger text and rows.");
            ScaleOption(3, "3  —  Largest",  "About a third bigger.");

            ImGui.Spacing();
            // Accurate, not aspirational: the completion and progress toasts are Panache surfaces
            // too and could be scaled the same way, but they are not wired to this yet. Saying so
            // beats letting someone wonder whether their toast is broken.
            ImGui.TextDisabled("Affects the main window. Completion and progress notifications "
                             + "keep their own sizing for now.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Close", new Vector2(100, 28)))
            {
                _uiScalePopupOpen = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        DialogTheme.Pop();
        if (!open) _uiScalePopupOpen = false;
    }

    /// <summary>One radio row in the UI scale dialog. Writes and saves only on an actual change.</summary>
    private void ScaleOption(int step, string label, string blurb)
    {
        if (ImGui.RadioButton(label + "##tc_scale" + step, _config.UiScale == step)
            && _config.UiScale != step)
        {
            _config.UiScale = step;
            _save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(blurb);
    }

    // ── Appearance ───────────────────────────────────────────────────────────

    private void DrawAppearance()
    {
        if (_appearanceRequested)
        {
            _appearanceRequested = false;
            _appearancePopupOpen = true;
            ImGui.OpenPopup(AppearancePopupId);
        }

        if (!_appearancePopupOpen) return;

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);

        // Pushed BEFORE BeginPopupModal so it colors the popup's own chrome (background, border,
        // rounding), and popped unconditionally after — the push/pop must bracket the call itself
        // regardless of whether it returns true, or the style stack goes unbalanced.
        DialogTheme.Push();

        bool open = true;
        if (ImGui.BeginPopupModal(AppearancePopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Paint an image behind the window. The panels over it become "
                            + "translucent so it shows through.");
            ImGui.Spacing();

            // Built-ins first — a dropdown of names beats making everyone paste a path for the
            // common case, and "Blank" is the explicit default so nobody has to guess how to
            // turn a background back off.
            ImGui.TextUnformatted("Background");
            ImGui.SetNextItemWidth(-1);

            var builtIns = BackgroundLibrary.BuiltIn();
            string current = BackgroundLibrary.DescribeCurrent(_appearancePath.Trim());

            if (ImGui.BeginCombo("##tc_bgpicker", current))
            {
                if (ImGui.Selectable(BackgroundLibrary.NoneName, current == BackgroundLibrary.NoneName))
                    _appearancePath = string.Empty;

                foreach (var opt in builtIns)
                    if (ImGui.Selectable(opt.Name, string.Equals(_appearancePath.Trim(), opt.Path, StringComparison.OrdinalIgnoreCase)))
                        _appearancePath = opt.Path;

                ImGui.Separator();
                if (ImGui.Selectable("Custom path…", current == "Custom"))
                {
                    // Selecting this just focuses attention on the box below; it does not clear
                    // a path someone may already be about to type into it.
                }

                ImGui.EndCombo();
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Custom image file");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##tc_bgpath", @"C:\path\to\image.png  (png, jpg, bmp)",
                                    ref _appearancePath, 512);

            // Checked here rather than on apply: knowing the path is wrong BEFORE closing the
            // dialog is the difference between a typo and a mystery.
            string typed = _appearancePath.Trim();
            if (typed.Length > 0 && !System.IO.File.Exists(typed))
                ImGui.TextColored(ColDanger, "No file at that path.");
            else if (typed.Length == 0)
                ImGui.TextDisabled("Empty = no background image.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            bool hasImage = !string.IsNullOrWhiteSpace(_config.BackgroundImagePath);
            if (!hasImage) ImGui.BeginDisabled();

            float panel = _config.PanelOpacity;
            if (ImGui.SliderFloat("Panel opacity##tc_panelop", ref panel, 0.10f, 1.00f, "%.2f"))
            {
                _config.PanelOpacity = Math.Clamp(panel, 0.10f, 1.00f);
                _save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How solid the panels over the image are.\nLower shows more of the image.");

            float img = _config.BackgroundImageOpacity;
            if (ImGui.SliderFloat("Image strength##tc_imgop", ref img, 0.05f, 1.00f, "%.2f"))
            {
                _config.BackgroundImageOpacity = Math.Clamp(img, 0.05f, 1.00f);
                _save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Fades the image toward the plain theme background.");

            if (!hasImage)
            {
                ImGui.EndDisabled();
                ImGui.TextDisabled("Set an image first — these only affect a window that has one.");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            bool canApply = typed.Length == 0 || System.IO.File.Exists(typed);
            if (!canApply) ImGui.BeginDisabled();
            if (ImGui.Button("Apply", new Vector2(120, 28)))
            {
                _config.BackgroundImagePath = typed;
                _save();
            }
            if (!canApply) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Remove image", new Vector2(140, 28)))
            {
                _appearancePath             = string.Empty;
                _config.BackgroundImagePath = string.Empty;
                _save();
            }

            ImGui.SameLine();
            if (ImGui.Button("Close", new Vector2(100, 28)))
            {
                _appearancePopupOpen = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        DialogTheme.Pop();
        if (!open) _appearancePopupOpen = false;
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    private void DrawResetConfirm()
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            _resetPopupOpen = true;
            ImGui.OpenPopup(ResetPopupId);
        }

        if (!_resetPopupOpen) return;

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(470, 0), ImGuiCond.Appearing);

        DialogTheme.Push();

        bool open = true;
        if (ImGui.BeginPopupModal(ResetPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var (done, total) = ChallengeCatalog.OverallProgress(_config, _store);

            ImGui.PushStyleColor(ImGuiCol.Text, ColDanger);
            ImGui.TextUnformatted("This WILL delete all of your challenge progress.");
            ImGui.PopStyleColor();

            ImGui.Spacing();
            ImGui.TextWrapped(
                $"Every completion mark will be cleared — all {done} challenge(s) you have "
              + $"marked done, out of {total}, will go back to incomplete.");

            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, ColOk);
            ImGui.TextWrapped(
                $"This is recoverable. A permanent record of all {_store.PermanentCount} completion(s) — "
              + "with their original dates — is kept in a separate file that Reset never touches. "
              + "Use Restore afterwards to put it all back.");
            ImGui.PopStyleColor();

            ImGui.Spacing();
#if DEV_BUILD
            ImGui.TextWrapped(
                "Challenges you authored in the Challenge Creator are NOT deleted — only their "
              + "completion state is cleared.");
#else
            ImGui.TextWrapped("The challenge list itself is untouched — only your completion state is cleared.");
#endif

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Overrides DialogTheme's default accent-gold button for this one destructive action —
            // matching the plugin's own Danger color (#E57B72), not an arbitrary red.
            ImGui.PushStyleColor(ImGuiCol.Button,        ColDanger with { W = 0.55f });
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ColDanger with { W = 0.75f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  ColDanger);
            if (ImGui.Button("Yes, I understand — wipe all progress", new Vector2(300, 30)))
            {
                // Sound first, as everywhere else — a cue must not depend on what happens after
                // it. This is also the one destructive action in the plugin, so it gets an
                // audible acknowledgement that the wipe actually went through.
                Plugin.Sound.Play(SoundService.Cue.ResetConfirmed);

                _store.ResetCurrent();

                // Partial progress goes too. Unlike the permanent ledger and the race best-time
                // file — both of which are RECORDS of things that happened — a half-finished quest
                // chain is exactly what "let me do these again" has to clear, or the player is
                // left unable to start it over. The tracker's in-memory sets are dropped in the
                // same motion, or they would write the old positions straight back out.
                Plugin.Progress.ResetAll();
                _tracker.ClearPartialProgress();

                _config.StateVersion++;
                _tracker.Invalidate();
                _save();
                Plugin.Log.Information("Challenge progress wiped by user confirmation.");
                _resetPopupOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopStyleColor(3);

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(110, 30)))
            {
                _resetPopupOpen = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        DialogTheme.Pop();
        if (!open) _resetPopupOpen = false;
    }

    // ── Suggestion ───────────────────────────────────────────────────────────

    private void DrawSuggestion()
    {
        if (_suggestRequested)
        {
            _suggestRequested = false;
            _suggestPopupOpen = true;
            _suggestStatus    = string.Empty;
            ImGui.OpenPopup(SuggestPopupId);
        }

        if (!_suggestPopupOpen) return;

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);

        DialogTheme.Push();

        bool open = true;
        if (ImGui.BeginPopupModal(SuggestPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (_bugMode)
            {
                ImGui.TextWrapped("Report a bug. The plugin's own log is attached automatically so the "
                                + "developer can see what actually happened.");
                ImGui.Spacing();

                ImGui.TextDisabled($"Attaching {DiagnosticLog.Count} log line(s) + a state summary "
                                 + "(version, renderer, zone, challenge counts).");

                if (ImGui.CollapsingHeader("Preview what will be sent"))
                {
                    if (ImGui.BeginChild("##tc_logpreview", new Vector2(0, 200), true))
                    {
                        ImGui.TextUnformatted(DiagnosticLog.BuildEnvironmentReport(_config, _store));
                        ImGui.Separator();
                        ImGui.TextUnformatted(DiagnosticLog.Tail(40));
                    }
                    ImGui.EndChild();
                }
            }
            else
            {
                ImGui.TextWrapped("Send a suggestion or challenge idea straight to the developer.");
            }
            ImGui.Spacing();

            ImGui.TextUnformatted($"Your message  ({_suggestText.Length}/{SuggestionService.MaxMessageLength})");
            ImGui.InputTextMultiline("##tc_suggest_text", ref _suggestText,
                                     SuggestionService.MaxMessageLength, new Vector2(-1, 140));

            ImGui.Spacing();
            ImGui.TextUnformatted("Contact (optional) — Discord handle, so you can be replied to");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##tc_suggest_contact", ref _suggestContact, SuggestionService.MaxContactLength);

            // Character name + world now always accompany a report, and the dialog says so plainly
            // rather than leaving it to a tooltip. It replaced an opt-in checkbox: an anonymous bug
            // report usually cannot be followed up on, and an anonymous suggestion cannot be
            // credited or replied to. Stating it up front is the honest version of that trade.
            ImGui.Spacing();
            string sender = SuggestionService.CurrentSender() ?? "(not logged in)";
            ImGui.TextDisabled("Sent as: ");
            ImGui.SameLine();
            ImGui.TextColored(ColOk, sender);
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Your character name and world are included so the report can be "
                               + "followed up on.\nAlso sent: your message, the plugin version, and "
                               + "anything you type above.");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            string? blocked = SuggestionService.BlockedReason(_suggestText);
            bool canSend = blocked == null && !_suggestSending;

            if (!canSend) ImGui.BeginDisabled();
            if (ImGui.Button(_suggestSending ? "Sending…" : "Send", new Vector2(120, 30)))
            {
                _suggestSending = true;
                _suggestStatus  = string.Empty;

                string? character = SuggestionService.CurrentSender();

                // Fire and forget onto the thread pool: the draw loop must never await a socket.
                string msg     = _suggestText;
                string contact = _suggestContact;
                bool   bug     = _bugMode;

                // Blocked senders are dropped here, silently, and told it worked.
                //
                // Deliberate: the point of a blocklist is that the sender stops producing traffic
                // AND stops iterating to get around a refusal. An error message is a signal to try
                // again from another character. This is the one place in the plugin that reports
                // something it did not do, and it is confined to this one branch for that reason.
                //
                // A flag rather than an early return: returning from inside BeginPopupModal would
                // skip the EndPopup/Pop pairing below and only stay balanced by luck about which
                // style pushes happen to be active at the time.
                bool dropped = BanService.IsBanned;
                if (dropped)
                {
                    Plugin.Log.Information("[Suggestion] dropped: sender is on the blocklist.");
                    _suggestOk      = true;
                    _suggestStatus  = "Sent — thank you!";
                    _suggestSending = false;
                    _suggestText    = string.Empty;
                }

                // Snapshot the log on the game thread; the send happens off it.
                string logText = bug
                    ? DiagnosticLog.BuildEnvironmentReport(_config, _store)
                      + Environment.NewLine + "---- log ----" + Environment.NewLine
                      + DiagnosticLog.Dump()
                    : string.Empty;

                if (!dropped)
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    var (ok, status) = bug
                        ? await SuggestionService.SendBugReportAsync(msg, contact, character, logText)
                        : await SuggestionService.SendAsync(msg, contact, character);

                    _suggestOk      = ok;
                    _suggestStatus  = status;
                    _suggestSending = false;
                    if (ok) _suggestText = string.Empty;
                });
            }
            if (!canSend) ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Close", new Vector2(100, 30)))
            {
                _suggestPopupOpen = false;
                ImGui.CloseCurrentPopup();
            }

            if (blocked != null && !_suggestSending)
                ImGui.TextColored(ColWarn, blocked);

            if (!string.IsNullOrEmpty(_suggestStatus))
                ImGui.TextColored(_suggestOk ? ColOk : ColDanger, _suggestStatus);

            ImGui.EndPopup();
        }

        DialogTheme.Pop();
        if (!open) _suggestPopupOpen = false;
    }
}
