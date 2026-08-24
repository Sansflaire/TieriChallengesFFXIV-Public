namespace TieriChallengesFFXIV;

/// <summary>
/// The one place the UI-scale step becomes a multiplier.
///
/// <para>Static, and deliberately so. Every Panache surface in the plugin needs this number, and
/// the toasts are constructed without a <see cref="Configuration"/> in reach — threading one into
/// each of them to read a single float would be more coupling, not less. It is set once per frame
/// from <c>Plugin.DrawUI</c>, which runs whether or not any window is open.</para>
///
/// <para><b>The mapping lives here and nowhere else.</b> It used to be duplicated in MainWindow,
/// which is exactly how the toasts ended up unscaled: a second copy of a constant is a second
/// place to forget.</para>
/// </summary>
internal static class UiScale
{
    /// <summary>
    /// Multiplier handed to <c>PanacheSurface.Scale</c>. Step 1 is exactly 1.0, so the default
    /// look is bit-for-bit what it was before scaling existed.
    /// </summary>
    public static float Factor { get; private set; } = 1f;

    /// <summary>Called once per frame. Out-of-range steps fall back to 1 rather than throwing.</summary>
    public static void Set(int step) =>
        Factor = step switch { 3 => 1.32f, 2 => 1.15f, _ => 1f };
}
