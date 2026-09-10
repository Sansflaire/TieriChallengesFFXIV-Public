#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Test 3 — Clue Trail.</b> An ordered chain of <b>authored</b> stops. Each
/// carries a clue written by whoever placed it; digging on a stop reveals the clue to the next.
///
/// <para><b>The stops and clues are authored, not generated, and that is the whole point.</b> The
/// first version placed random spots and produced its own clues — "NORTH-EAST, far away" — which is
/// not a clue but a search order: a bearing across a whole zone leaves the player sweeping hundreds
/// of yalms with no way to distinguish progress from luck. A clue works because someone who knows
/// the place wrote something someone who does not can act on, and no amount of geometry substitutes
/// for that. See <see cref="DigTrailStore"/>.</para>
///
/// <para><b>The radar is the last few yalms only.</b> A ring appears within
/// <see cref="DigTuning.TrailRadar"/> and pulses faster the closer the player gets, going solid
/// inside the dig radius. It deliberately does not exist further out: a radar visible from anywhere
/// would be a distance readout, and then the clue would be decoration. The clue gets you to the
/// neighbourhood; the radar closes the last stretch.</para>
///
/// <para><b>Solid means the dig will land</b> — the ring goes solid at exactly the distance
/// <see cref="Dig"/> accepts, which is why radar range is clamped above the dig radius in the lab
/// rather than merely warned about.</para>
///
/// <para><b>A trail may cross zones.</b> Leaving the current stop's territory is travel, not
/// failure, so this does not abandon on a zone change the way the other two tests do — it says
/// where to go instead.</para>
/// </summary>
internal sealed class DigTrailService : IDigTest
{
    private enum Phase { Off, Running, Done }

    private const long DoneHoldMs = 15_000;

    private readonly List<TrailStop> _stops = new();

    private Phase  _phase;
    private long   _startedAtMs;
    private long   _endedAtMs;
    private int    _index;
    private int    _digs;
    private float  _distance = float.MaxValue;
    private bool   _inZone;

    public string Name => "Clue Trail";

    public bool IsActive => _phase != Phase.Off;

    /// <summary>
    /// True once the last stop has been dug, for as long as the result is still on screen.
    ///
    /// <para>Plain state, deliberately — not an event and not a call into the HUD. The overlay
    /// edge-detects this and <see cref="IsActive"/> to decide when to throw its two banners up,
    /// which keeps every presentation decision on the presentation side. A service that reached
    /// into the overlay to play an animation would be the thing that makes "nothing in the overlay
    /// decides anything" stop being true.</para>
    /// </summary>
    public bool IsFinished => _phase == Phase.Done;

    public int   StopTotal     => _stops.Count;
    public int   StopIndex     => _index;
    public int   Digs          => _digs;
    public float DebugDistance => _distance;

    /// <summary>The stop being looked for, or null when the trail is not running.</summary>
    private TrailStop? Current =>
        _phase == Phase.Running && _index >= 0 && _index < _stops.Count ? _stops[_index] : null;

    public DigBand Band => _phase switch
    {
        Phase.Done    => DigBand.Green,
        Phase.Running => !_inZone                             ? DigBand.Cold
                       : _distance <= DigTuning.TrailDig      ? DigBand.Dig
                       : _distance <= DigTuning.TrailRadar    ? DigBand.Yellow
                       : DigBand.Cold,
        _ => DigBand.Cold,
    };

    /// <summary>0 at the edge of radar range, 1 on the spot. Null whenever there is no radar.</summary>
    public float? RadarCloseness
    {
        get
        {
            if (_phase != Phase.Running || !_inZone) return null;

            float outer = MathF.Max(DigTuning.TrailRadar, DigTuning.TrailDig + 1f);
            if (_distance > outer) return null;

            float inner = DigTuning.TrailDig;
            if (_distance <= inner) return 1f;

            return Math.Clamp(1f - (_distance - inner) / (outer - inner), 0f, 1f);
        }
    }

    public double ElapsedSeconds => _phase switch
    {
        Phase.Running => (Environment.TickCount64 - _startedAtMs) / 1000.0,
        Phase.Done    => (_endedAtMs - _startedAtMs) / 1000.0,
        _             => 0.0,
    };

    public string Headline => _phase switch
    {
        Phase.Done    => "TRAIL'S END!",
        Phase.Running => RadarCloseness is >= 1f ? "DIG!!!" : $"CLUE  {_index + 1} / {_stops.Count}",
        _             => "CLUE TRAIL",
    };

    public string Subtitle
    {
        get
        {
            if (_phase == Phase.Done)    return $"{_digs} dig(s) along the way.";
            if (_phase != Phase.Running) return string.Empty;

            var stop = Current;
            if (stop == null) return string.Empty;

            // Wrong zone is travel, not failure — say where, since a clue for a place you are not
            // in reads as a broken clue.
            if (!_inZone) return $"Travel to {ZoneName(stop.Territory)}.";

            if (RadarCloseness is >= 1f) return "This is the place. Dig.";

            return string.IsNullOrWhiteSpace(stop.Clue) ? "(no clue written for this stop)" : stop.Clue;
        }
    }

    // ── the player's actions ─────────────────────────────────────────────────

    /// <summary>
    /// Begins the authored trail. Refuses when nothing has been authored — there is nothing to fall
    /// back on, deliberately: a generated trail is what this test was changed away from.
    /// </summary>
    public string Start()
    {
        if (!PropService.CanPerform(out string why)) return "cannot start — " + why;
        if (Plugin.ObjectTable.LocalPlayer == null)  return "no character loaded.";

        var authored = DigTrailStore.Snapshot();
        if (authored.Count == 0)
            return "no trail authored yet — add stops and write their clues in the lab first.";

        // A snapshot, so editing the list mid-run cannot shift the trail underfoot.
        _stops.Clear();
        _stops.AddRange(authored);

        _index       = 0;
        _digs        = 0;
        _distance    = float.MaxValue;
        _inZone      = false;
        _startedAtMs = Environment.TickCount64;
        _phase       = Phase.Running;

        var first = _stops[0];
        string clue = string.IsNullOrWhiteSpace(first.Clue) ? "(no clue written)" : first.Clue;

        return $"trail of {_stops.Count} begun. First clue: {clue}";
    }

    /// <summary>
    /// Digs. On the current stop this reveals the next clue and advances; on the last it ends the
    /// trail. The animation always plays — digging in the wrong place is allowed and costs only the
    /// time.
    /// </summary>
    public string Dig()
    {
        string animation = Plugin.Props.Dig();

        if (_phase != Phase.Running) return animation;

        _digs++;

        if (!_inZone)                       return animation + " Wrong zone.";
        if (_distance > DigTuning.TrailDig)  return animation + " Nothing but dirt here.";

        // Judged now, banked on completion — see IDigTest.DigFinished.
        _pendingAdvance = true;
        return animation + " You strike something…";
    }

    public void DigFinished(bool completed)
    {
        bool advance = _pendingAdvance;
        _pendingAdvance = false;

        if (!advance || _phase != Phase.Running) return;

        if (!completed)
        {
            Plugin.ChatGui.Print("[Challenges] You stopped digging too soon — the clue stays buried.");
            return;
        }

        _index++;

        if (_index < _stops.Count)
        {
            try { Plugin.Sound.Play(SoundService.Cue.ObjectiveProgress); }
            catch (Exception ex) { Diag.Error($"[Trail] clue cue failed: {ex.Message}"); }

            var next = _stops[_index];
            string clue = string.IsNullOrWhiteSpace(next.Clue) ? "(no clue written)" : next.Clue;

            Plugin.ChatGui.Print($"[Challenges] A clue! {clue}");
            return;
        }

        _endedAtMs = Environment.TickCount64;
        _phase     = Phase.Done;

        try { Plugin.Sound.Play(SoundService.Cue.ChallengeComplete); }
        catch (Exception ex) { Diag.Error($"[Trail] end cue failed: {ex.Message}"); }

        string time = CompletionStore.FormatRaceTime(ElapsedSeconds);
        Diag.Info($"[Trail] finished in {time} over {_digs} dig(s).");

        Plugin.ChatGui.Print($"[Challenges] That's the end of the trail! {time}, {_digs} dig(s).");
    }

    /// <summary>Whether the running dig would turn up the next clue, if allowed to finish.</summary>
    private bool _pendingAdvance;

    public string Stop()
    {
        if (_phase == Phase.Off) return "no trail is running.";
        _phase = Phase.Off;
        _stops.Clear();
        return "trail abandoned.";
    }

    /// <summary>Repeats the clue for the stop currently being looked for.</summary>
    public string Recall()
    {
        if (_phase != Phase.Running) return "no trail is running.";

        var stop = Current;
        if (stop == null) return "no clue.";

        string where = _inZone ? string.Empty : $" (in {ZoneName(stop.Territory)})";
        return (string.IsNullOrWhiteSpace(stop.Clue) ? "(no clue written for this stop)" : stop.Clue) + where;
    }

    public void DrawWorld() { /* drawing the stops would replace the clue with an answer */ }

    // ── per-frame ────────────────────────────────────────────────────────────

    public void Tick()
    {
        if (_phase == Phase.Off) return;

        try
        {
            if (_phase == Phase.Done)
            {
                if (Environment.TickCount64 - _endedAtMs >= DoneHoldMs) Stop();
                return;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null || !Plugin.ClientState.IsLoggedIn) { Stop(); return; }

            var stop = Current;
            if (stop == null) { Stop(); return; }

            // NOT abandoned on a zone change: an authored trail is allowed to cross zones, and
            // walking to the next one is playing it, not failing it.
            _inZone = Plugin.ClientState.TerritoryType == stop.Territory;

            _distance = _inZone
                ? DigGround.Flat(player.Position, stop.Position)
                : float.MaxValue;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Trail] tick failed: {ex.Message}");
            _phase = Phase.Off;
        }
    }

    /// <summary>
    /// Dev-only zone name. Uses <c>ZoneIndex.ZoneName</c> rather than <c>DisplayName</c> on purpose:
    /// the spoiler mask exists for player-facing surfaces, and this whole class is compiled out of
    /// the public build. Flagged here so a future grep for unmasked zone names has its answer.
    /// </summary>
    private static string ZoneName(uint territory)
    {
        try   { return ZoneIndex.ZoneName((ushort)territory); }
        catch { return $"territory {territory}"; }
    }
}
#endif
