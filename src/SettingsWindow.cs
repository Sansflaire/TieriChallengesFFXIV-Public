using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// The player-facing settings window: sound, notifications and colours.
///
/// <para><b>Raw ImGui, themed.</b> Permitted by DESIGN_SYSTEM §10 anti-pattern 8 (standard popups),
/// and required here because PanacheUI has no slider, no checkbox and no colour picker —
/// <c>ImGui.ColorEdit4</c> in particular has no equivalent anywhere in the framework. It pushes
/// <see cref="DialogTheme"/> so it matches the main window, per the standing rule that every
/// player-facing surface does. Because the theme now READS the palette this window edits, a colour
/// change is visible in this window's own chrome on the very next frame.</para>
///
/// <para><b>Every change saves immediately.</b> There is no OK/Cancel: a settings panel with an
/// apply step invites half-applied state, and every control here is individually reversible —
/// colours by their own Reset, everything else by putting the control back.</para>
/// </summary>
internal sealed class SettingsWindow
{
    private readonly Configuration _config;
    private readonly Action        _save;

    /// <summary>Pushes the audio settings into GameSound's statics — see Plugin.ApplySoundSettings.</summary>
    private readonly Action _applySound;

    public bool IsVisible;

    public SettingsWindow(Configuration config, Action save, Action applySound)
    {
        _config     = config;
        _save       = save;
        _applySound = applySound;
    }

    public void Draw()
    {
        if (!IsVisible) return;

        float scale = UiScale.Factor;
        ImGui.SetNextWindowSize(new Vector2(470 * scale, 560 * scale), ImGuiCond.FirstUseEver);

        bool open = true;

        // Push/Pop must bracket Begin, and both must run even when Begin returns false, or the
        // style stack goes unbalanced for every window drawn afterwards.
        DialogTheme.Push();
        bool shown = ImGui.Begin("Challenges — Settings###tc_settings", ref open,
                                 ImGuiWindowFlags.NoSavedSettings);

        if (shown)
        {
            if (ImGui.BeginTabBar("##tc_settings_tabs"))
            {
                if (ImGui.BeginTabItem("Sound"))         { DrawSound();         ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Notifications")) { DrawNotifications(); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Colours"))       { DrawColours();       ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
        }

        ImGui.End();
        DialogTheme.Pop();

        if (!open) IsVisible = false;
    }

    // ── Sound ────────────────────────────────────────────────────────────────

    private void DrawSound()
    {
        ImGui.Spacing();

        bool muted = _config.SoundMuted;
        if (ImGui.Checkbox("Mute all sounds", ref muted))
        {
            _config.SoundMuted = muted;
            _applySound();
            _save();
        }

        // Separate from the volume level on purpose: unmuting must bring back the level you had,
        // not whatever the slider happened to be dragged to on the way to zero.
        if (muted) ImGui.BeginDisabled();

        float volume = _config.SoundVolume * 100f;
        ImGui.SetNextItemWidth(-90 * UiScale.Factor);
        if (ImGui.SliderFloat("Volume##sfx", ref volume, 0f, 100f, "%.0f%%"))
        {
            _config.SoundVolume = Math.Clamp(volume / 100f, 0f, 1f);
            _applySound();
            _save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Test##sfx", new Vector2(70 * UiScale.Factor, 0)))
            Plugin.Sound.Preview(SoundService.Cue.ChallengeComplete);

        if (muted) ImGui.EndDisabled();

        ImGui.TextDisabled("Volume is applied to the plugin's own sounds only. It does not change "
                         + "anything else in the game.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Individual sounds");
        ImGui.TextDisabled("Switch off the ones you would rather not hear.");
        ImGui.Spacing();

        foreach (var (cue, label, when) in SoundService.PublicCues)
        {
            ImGui.PushID(cue.ToString());

            bool on = !_config.DisabledCues.Contains(cue.ToString());
            if (ImGui.Checkbox(label, ref on))
            {
                if (on) _config.DisabledCues.Remove(cue.ToString());
                else if (!_config.DisabledCues.Contains(cue.ToString()))
                    _config.DisabledCues.Add(cue.ToString());
                _save();
            }

            ImGui.SameLine();
            // Right-aligned so the preview buttons form a column rather than ragging along the
            // ends of labels of different lengths.
            float w = 70 * UiScale.Factor;
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - w - 20 * UiScale.Factor);

            // Previews ignore the enable/mute filter — the point is to hear what you are about to
            // switch off, and a Play button that does nothing because the thing is muted is a
            // worse answer than playing it.
            if (ImGui.Button("Play", new Vector2(w, 0))) Plugin.Sound.Preview(cue);

            ImGui.TextDisabled($"      when {when}");
            ImGui.PopID();
        }
    }

    // ── Notifications ────────────────────────────────────────────────────────

    private void DrawNotifications()
    {
        ImGui.Spacing();

        bool banner = _config.ShowCompletionBanner;
        if (ImGui.Checkbox("Completion banner", ref banner))
        {
            _config.ShowCompletionBanner = banner;
            _save();
        }
        ImGui.TextDisabled("      The large popup when a challenge finishes.");

        bool progress = _config.ShowProgressPopups;
        if (ImGui.Checkbox("Progress popups", ref progress))
        {
            _config.ShowProgressPopups = progress;
            _save();
        }
        ImGui.TextDisabled("      The small corner popup when part of a challenge is done.");

        bool fly = _config.ShowFlyText;
        if (ImGui.Checkbox("Floating text", ref fly))
        {
            _config.ShowFlyText = fly;
            _save();
        }
        ImGui.TextDisabled("      Text that rises over your character, like combat text.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        float seconds = _config.PopupSeconds;
        ImGui.SetNextItemWidth(-90 * UiScale.Factor);
        if (ImGui.SliderFloat("On screen for##dur", ref seconds, 2f, 15f, "%.1f s"))
        {
            _config.PopupSeconds = Math.Clamp(seconds, 2f, 15f);
            ApplyDurations();
            _save();
        }

        ImGui.Spacing();

        bool hold = _config.SuppressInCombat;
        if (ImGui.Checkbox("Hold notifications in combat and duties", ref hold))
        {
            _config.SuppressInCombat = hold;
            _save();
        }
        ImGui.TextDisabled("      Sounds still play — only the on-screen popups are held back.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool racePrompt = !_config.RacePromptSuppressed;
        if (ImGui.Checkbox("Offer to start a race when you stand at the line", ref racePrompt))
        {
            _config.RacePromptSuppressed = !racePrompt;
            _save();
        }
        ImGui.TextDisabled("      Off: start races from the challenge list instead. The running "
                         + "clock always shows.");
    }

    /// <summary>
    /// Push the configured duration into both toast timers.
    /// </summary>
    /// <remarks>
    /// The second line used to target <c>CompletionToast.HoldSeconds</c>, which nothing read — the
    /// completion banner was governed by a const inside <see cref="ToastQueue"/>. Both queues now
    /// expose the same <c>TotalSeconds</c> knob, and each is genuinely the value its own timing is
    /// computed from.
    /// </remarks>
    public void ApplyDurations()
    {
        ProgressQueue.TotalSeconds = _config.PopupSeconds;
        ToastQueue.TotalSeconds    = _config.PopupSeconds;
    }

    // ── Colours ──────────────────────────────────────────────────────────────

    private void DrawColours()
    {
        ImGui.Spacing();
        ImGui.TextDisabled("Changes apply as you drag. Every colour has its own reset.");
        ImGui.Spacing();

        foreach (PaletteSlot slot in Enum.GetValues<PaletteSlot>())
        {
            var (label, what) = Palette.Describe[slot];
            ImGui.PushID(slot.ToString());

            var colour = Palette.Vec(slot);

            // NoAlpha: the palette's alpha is decided per use site (a border at 0.22, a fill at
            // 0.12 and so on), so a user-set alpha would be multiplied by those and read as
            // "this control does nothing" at the low end.
            if (ImGui.ColorEdit4(label, ref colour,
                                 ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
            {
                Palette.Set(slot, colour);
                _save();
            }

            if (Palette.IsOverridden(slot))
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Reset"))
                {
                    Palette.Reset(slot);
                    _save();
                }
            }

            ImGui.TextDisabled($"      {what}");
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (!Palette.AnyOverridden) ImGui.BeginDisabled();
        if (ImGui.Button("Reset all colours", new Vector2(180 * UiScale.Factor, 0)))
        {
            Palette.ResetAll();
            _save();
        }
        if (!Palette.AnyOverridden) ImGui.EndDisabled();

        ImGui.TextDisabled("Background image and window opacity live under Appearance.");
    }
}
