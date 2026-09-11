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
/// <para>The whole feature is dev-only, gate and all — Sansflaire's call: these are tests, and a player
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
    public readonly DigRoamService  Roam  = new();

    private readonly List<IDigTest> _all;

    public DigTests() => _all = new List<IDigTest> { Hunt, Site, Trail, Roam };

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

    /// <summary>
    /// Digs with whichever test is running, or just plays the animation if none is.
    ///
    /// <para><b>Being mounted is not a refusal, it is a step.</b> A dig request from a mounted player
    /// dismounts them and digs the moment the game lets go — "you are mounted" was making the player
    /// do by hand something the click could obviously have done for them. The dig is deferred rather
    /// than retried in a loop: the dismount takes a beat, and <see cref="Tick"/> is already running.</para>
    /// </summary>
    public string Dig()
    {
        if (DigMount.Mounted)
        {
            string r = DigMount.RequestDismount();

            // Only arm the follow-up if the dismount was actually issued. Arming it after a refusal
            // would spend the whole window re-discovering that we cannot dismount, then print a
            // second failure for the same cause.
            if (r.StartsWith("dismounting", StringComparison.OrdinalIgnoreCase))
                _digAfterDismountBy = Environment.TickCount64 + DismountWaitMs;

            return r;
        }

        _digAfterDismountBy = 0;

        var active = Active;
        return active?.Dig() ?? Plugin.Props.Dig();
    }

    /// <summary>
    /// Buries one bench spot for a single clue category, stopping whatever else was running.
    ///
    /// <para>Routed through here rather than called on <see cref="Roam"/> directly so the
    /// one-test-at-a-time rule still holds. A bench spawn is a real running test — it owns the HUD
    /// while it lasts — so leaving another test running would put two headlines in one slot, which
    /// is the exact thing <see cref="Start"/> exists to prevent.</para>
    /// </summary>
    /// <summary>
    /// Starts a Wild Trail for the ACTIVITY mode, with its own clue count and difficulty.
    ///
    /// <para>Routed through here for the same reason the bench spawn is: the one-test-at-a-time rule
    /// belongs to this class, and an activity is a running test like any other. Starting one while a
    /// lab test is up would put two headlines in one HUD slot.</para>
    /// </summary>
    public string StartRoamActivity(int clues, float difficulty)
    {
        string prefix = string.Empty;

        foreach (var t in _all)
        {
            if (ReferenceEquals(t, Roam) || !t.IsActive) continue;
            t.Stop();
            prefix = $"({t.Name} stopped) ";
        }

        return prefix + Roam.Start(clues, difficulty);
    }

    public string StartRoamSingle(ClueCategory category, float difficulty)
    {
        string prefix = string.Empty;

        foreach (var t in _all)
        {
            if (ReferenceEquals(t, Roam) || !t.IsActive) continue;
            t.Stop();
            prefix = $"({t.Name} stopped) ";
        }

        return prefix + Roam.StartSingle(category, difficulty);
    }

    /// <summary>How long to wait for a dismount before giving up on the queued dig.</summary>
    private const long DismountWaitMs = 4000;

    /// <summary>Deadline for a dig queued behind a dismount, or 0 when nothing is queued.</summary>
    private long _digAfterDismountBy;

    /// <summary>
    /// Fires the dig that was waiting on a dismount, or abandons it once the deadline passes.
    /// Abandoning is loud — a queued action that silently evaporates is indistinguishable from a
    /// click that never registered.
    /// </summary>
    private void ServiceQueuedDig()
    {
        if (_digAfterDismountBy == 0) return;

        if (DigMount.Mounted)
        {
            if (Environment.TickCount64 < _digAfterDismountBy) return;

            _digAfterDismountBy = 0;
            Plugin.ChatGui.Print("[Challenges] Still mounted — the dig was dropped. Try again.");
            return;
        }

        _digAfterDismountBy = 0;

        try { Plugin.ChatGui.Print("[Challenges] " + Dig()); }
        catch (Exception ex) { Diag.Error($"[Dig] queued dig failed: {ex.Message}"); }
    }

    /// <summary>
    /// Says again whatever the running test last told the player. What the dial does when it is
    /// clicked from OUT of range, where a dig would be pointless.
    /// </summary>
    public string Recall()
    {
        var active = Active;

        return active switch
        {
            DigTrailService trail => trail.Recall(),
            DigRoamService  roam  => roam.Recall(),
            DigHuntService  hunt  => hunt.Sense(),
            null                  => "nothing is running.",

            // The site test has no clue to repeat — its subtitle already carries the count, so
            // saying that back is the honest equivalent of "here is where you are".
            _ => active.Subtitle.Length > 0 ? active.Subtitle : "nothing more to say.",
        };
    }

    public string StopAll()
    {
        var active = Active;
        if (active == null) return "nothing is running.";

        string r = active.Stop();
        return active.Name + ": " + r;
    }

    /// <summary>Whether a prop performance was running last frame — see <see cref="Tick"/>.</summary>
    private bool _wasPerforming;

    /// <summary>
    /// Ticks every test, not just the active one — a test that is winding down its result screen is
    /// no longer "active" by some readings but still needs its clock read. Each guards itself with
    /// an early-out, so the idle cost is three branch tests.
    /// </summary>
    public void Tick()
    {
        // Watch for the dig animation ending, and tell the active test whether it ran to the end.
        // Polled rather than evented: this class already ticks every frame, and a poll cannot leave
        // a subscription behind on a plugin reload.
        //
        // Ordered after Props.Tick in the draw path, so a performance that ended THIS frame is seen
        // this frame rather than one late.
        bool performing = Plugin.Props.IsPerforming;

        if (_wasPerforming && !performing)
        {
            var active = Active;
            try { active?.DigFinished(Plugin.Props.LastPerformanceCompleted); }
            catch (Exception ex) { Diag.Error($"[Dig] dig-finished failed: {ex.Message}"); }
        }

        _wasPerforming = performing;

        // After the dig-finished check, before the tests tick: a dig fired here belongs to this
        // frame's state, and firing it above would let the same frame see it both start and end.
        try { ServiceQueuedDig(); }
        catch (Exception ex) { Diag.Error($"[Dig] queued dig service failed: {ex.Message}"); }

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
