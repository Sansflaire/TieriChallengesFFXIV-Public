using System;

namespace TieriChallengesFFXIV;

/// <summary>
/// A race run that just ended, handed to the UI so it can say what happened.
/// </summary>
public sealed class RaceEndedEvent
{
    public string      Id      { get; init; } = string.Empty;
    public string      Title   { get; init; } = string.Empty;
    public RaceOutcome Outcome { get; init; }

    /// <summary>Elapsed seconds at the moment it ended. Meaningful for every outcome, not just a finish.</summary>
    public double Seconds { get; init; }

    /// <summary>Finished AND beat the stored time (or set the first one).</summary>
    public bool NewBest { get; init; }

    /// <summary>The time to beat before this run, if there was one.</summary>
    public double? PreviousBest { get; init; }

    /// <summary>
    /// This run completed the challenge for the FIRST time, so the normal completion path is also
    /// firing its fanfare and toast for it. Everything that celebrates a race must check this and
    /// stay quiet, or one event produces two popups.
    /// </summary>
    public bool FirstCompletion { get; init; }

    public string Describe() => Outcome switch
    {
        RaceOutcome.Finished  => NewBest
                                     ? $"New best — {CompletionStore.FormatRaceTime(Seconds)}"
                                     : $"Finished in {CompletionStore.FormatRaceTime(Seconds)}",
        RaceOutcome.TimedOut  => "Out of time",
        RaceOutcome.LeftArea  => "Left the course",
        RaceOutcome.Abandoned => "Run abandoned",
        _                     => "Run ended",
    };
}

/// <summary>
/// The single race currently being run. There is at most one — a player cannot be running two
/// races at once, and allowing it would mean two clocks, two failure conditions and two sets of
/// UI competing for the same corner of the screen for no gain.
/// </summary>
internal sealed class RaceRun
{
    public string Id = string.Empty;

    /// <summary><see cref="Environment.TickCount64"/> at the start, or at the last restart.</summary>
    public long StartedAtMs;

    /// <summary>
    /// Has the runner cleared the start volume since the clock started?
    ///
    /// <para><b>Load-bearing.</b> "Re-entering the start area restarts the clock" is the rule, but
    /// the player is standing IN the start area at the instant they press Start — without this
    /// latch the clock would reset on every tick until they walked out, and the race could never
    /// begin. The reset only arms once they have actually left.</para>
    /// </summary>
    public bool LeftStart;

    public double ElapsedSeconds => Math.Max(0, Environment.TickCount64 - StartedAtMs) / 1000.0;
}
