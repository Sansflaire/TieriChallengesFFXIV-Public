using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace TieriChallengesFFXIV;

/// <summary>
/// The only thing a banned character sees. Everything else in the plugin is skipped before this
/// is reached — see <c>Plugin.DrawUI</c>.
///
/// <para>Raw ImGui deliberately, and with no dependency on <see cref="PanacheAvailability"/>: this
/// has to render even if PanacheUI failed to load, because "the fancy renderer is missing" must not
/// become a way to make the notice disappear and the plugin usable again.</para>
///
/// <para>It has no close button. Closing it would leave the plugin drawing nothing at all, which
/// looks like a crash rather than a decision — and the point is that the message is unmissable.</para>
/// </summary>
internal static class BanNotice
{
    public static void Draw()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Appearing);

        DialogTheme.Push();

        // No ref bool: there is nothing to close it with, by design.
        if (ImGui.Begin("FFXIV Miscellaneous Challenges##tc_banned",
                        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextColored(DialogTheme.Danger, "This plugin has been disabled for this character.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextUnformatted("Reason");
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(BanService.Reason)
                ? "No reason was recorded."
                : BanService.Reason);
            ImGui.PopTextWrapPos();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled("Challenge tracking, the challenge list, and bug reports are all "
                             + "turned off. If this is resolved, the plugin re-enables itself "
                             + "automatically — no reinstall is needed.");
            ImGui.PopTextWrapPos();
        }

        ImGui.End();
        DialogTheme.Pop();
    }
}
