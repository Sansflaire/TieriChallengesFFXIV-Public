#if DEV_BUILD
using System;
using System.Collections.Generic;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Owns the three dig tests and guarantees that <b>at most one runs at a
/// time</b>.
///
/// <para>That guarantee is not tidiness. All three share one HUD slot and one Dig verb, so two
/// running at once would put two headlines in the same place and leave "which spot did that dig
/// mean" genuinely unanswerable. Starting a test therefore stops whatever else was running, and
/// says so.</para>
///
/// <para>The whole feature is dev-only, gate and all — Trist's call: these are tests, and a player
/// must not be able to reach the rules, the commands, the buttons or the settings. That is a
/// stricter split than <see cref="PropService"/>, which ships because challenge content calls it.
/// If a hunt ever becomes real challenge content, un-gating is deleting the <c>#if</c> from these
/// files and moving the commands, not a rewrite.</para>
/// </summary>
internal sealed class DigTests
{
    public readonly DigHuntService  Hunt  = new();
    public readonly DigSiteService  Site  = new();
    public readonly DigTrailService Trail = new();

    private readonly List<IDigTest> _all;

    public DigTests() => _all = new List<IDigTest> { Hunt, Site, Trail };

    public IReadOnlyList<IDigTest> All => _all;

    /// <summary>The one running test, or null.</summary>
    public IDigTest? Active
    {
        get
        {
            foreach (var t in _all) if (t.IsActive) return t;
            return null;
        }
    }

    /// <summary>
    /// Starts one test, stopping any other first. Returns the line to print.
    /// </summary>
    public string Start(IDigTest test)
    {
        string prefix = string.Empty;

        foreach (var t in _all)
        {
            if (ReferenceEquals(t, test) || !t.IsActive) continue;
            t.Stop();
            prefix = $"({t.Name} stopped) ";
        }

        return prefix + test.Name + ": " + test.Start();
    }

    /// <summary>Digs with whichever test is running, or just plays the animation if none is.</summary>
    public string Dig()
    {
        var active = Active;
        return active?.Dig() ?? Plugin.Props.Dig();
    }

    public string StopAll()
    {
        var active = Active;
        if (active == null) return "nothing is running.";

        string r = active.Stop();
        return active.Name + ": " + r;
    }

    /// <summary>
    /// Ticks every test, not just the active one — a test that is winding down its result screen is
    /// no longer "active" by some readings but still needs its clock read. Each guards itself with
    /// an early-out, so the idle cost is three branch tests.
    /// </summary>
    public void Tick()
    {
        foreach (var t in _all)
        {
            try { t.Tick(); }
            catch (Exception ex) { Diag.Error($"[Dig] {t.Name} tick failed: {ex.Message}"); }
        }
    }

    /// <summary>In-world drawing for whichever test has any.</summary>
    public void DrawWorld()
    {
        foreach (var t in _all)
        {
            if (!t.IsActive) continue;
            try { t.DrawWorld(); }
            catch (Exception ex) { Diag.Error($"[Dig] {t.Name} world draw failed: {ex.Message}"); }
        }
    }
}
#endif
