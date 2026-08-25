using System;

using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace TieriChallengesFFXIV;

/// <summary>
/// Floating combat-style text, used to announce challenge progress over the player's head.
///
/// <para>Uses the game's own fly-text system rather than a drawn overlay, so the message obeys
/// the player's HUD layout, scaling and fly-text settings, and sits in the visual language they
/// already read during play.</para>
///
/// <para><see cref="FlyTextKind.Named"/> is the text-only kind — it renders the two strings and
/// no damage number, which is what a progress line needs. Kinds like <c>Damage</c> would draw
/// <c>val1</c> as a big numeral.</para>
/// </summary>
internal static class FlyTextService
{
    // Gold, matching the plugin accent. The colour is a packed uint; the game's fly text uses
    // ABGR byte order, so the helper packs it that way rather than the more obvious ARGB.
    private static readonly uint Gold  = Abgr(0xE3, 0xB3, 0x41);
    private static readonly uint Green = Abgr(0x7F, 0xD6, 0xA9);
    private static readonly uint Red   = Abgr(0xE5, 0x7B, 0x72);

    private static uint Abgr(byte r, byte g, byte b, byte a = 0xFF)
        => (uint)((a << 24) | (b << 16) | (g << 8) | r);

    /// <summary>"Challenge Name" / "2/4" — one step of a multi-step challenge landed.</summary>
    public static void ShowProgress(string title, int done, int total)
        => Show(title, $"{done}/{total}", Gold);

    /// <summary>"Challenge Name" / "Complete!"</summary>
    public static void ShowComplete(string title)
        => Show(title, "Complete!", Green);

    /// <summary>
    /// "Personal Best!" / "41.20s" — a race run that beat the stored time.
    ///
    /// <para>Gold rather than the green a completion uses: beating your own time is not the same
    /// event as finishing something for the first time, and the two must not look alike when they
    /// can happen seconds apart on the same challenge.</para>
    /// </summary>
    public static void ShowPersonalBest(double seconds)
        => Show("Personal Best!", CompletionStore.FormatRaceTime(seconds), Gold);

    /// <summary>A short red error line — right-click-to-teleport's two failure cases.</summary>
    public static void ShowError(string headline, string detail)
        => Show(headline, detail, Red);

    /// <summary>
    /// Never throws. Fly text is decoration — losing it must never propagate into the tracker
    /// tick that recorded the progress.
    /// </summary>
    private static void Show(string text1, string text2, uint color)
    {
        try
        {
            // Fly text is anchored to an actor. Anything other than the local player would put
            // the message over someone else's head.
            var lp = Plugin.ObjectTable.LocalPlayer;
            if (lp == null) return;

            Plugin.FlyTextGui.AddFlyText(
                kind:            FlyTextKind.Named,
                actorIndex:      lp.ObjectIndex,
                val1:            0u,
                val2:            0u,
                text1:           new SeString(new TextPayload(text1)),
                text2:           new SeString(new TextPayload(text2)),
                color:           color,
                icon:            0u,
                damageTypeIcon:  0u);
        }
        catch (Exception ex)
        {
            Diag.Debug($"[FlyText] failed: {ex.Message}");
        }
    }
}
