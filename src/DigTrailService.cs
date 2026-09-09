#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Test 3 — Clue Trail.</b> An ordered chain of spots. Each one carries a
/// clue to the next, so digging up spot N is what tells you roughly where spot N+1 is. Reach the
/// end and the trail pays out.
///
/// <para><b>The radar is the whole interaction.</b> A ring appears once the player is within
/// <see cref="DigTuning.TrailRadar"/> of the current spot and pulses faster the closer they get,
/// going solid inside the dig radius. It deliberately does <i>not</i> exist further out: a radar
/// visible from anywhere would be a distance readout, and the clue — a compass bearing and a woolly
/// range — is supposed to be the thing that gets you into the neighbourhood. The radar only closes
/// the last few yalms.</para>
///
/// <para><b>Solid means the dig will land.</b> That is a promise the tuning has to keep: the ring
/// goes solid at exactly the distance <see cref="Dig"/> accepts, so a player who waits for solid is
/// never told "nothing but dirt". <see cref="DigTuning.TrailRadar"/> is clamped above
/// <see cref="DigTuning.TrailDig"/> in the lab for the same reason.</para>
/// </summary>
internal sealed class DigTrailService : IDigTest
{
    private enum Phase { Off, Placing, Running, Done }

    private const long DoneHoldMs = 15_000;

    /// <summary>Per-spot placement attempts, and how many frames placement may take.</summary>
    private const int AttemptsPerSpot = 200;
    private const int PlacementTicks  = 8;

    private readonly Random        _rng   = new();
    private readonly List<Vector3> _stops = new();

    private Phase   _phase;
    private uint    _territory;
    private long    _startedAtMs;
    private long    _endedAtMs;
    private int     _index;
    private int     _placeTicks;
    private int     _digs;
    private float   _distance = float.MaxValue;
    private string  _clue     = string.Empty;

    public string Name => "Clue Trail";

    public bool IsActive => _phase != Phase.Off;

    public int  StopTotal   => _stops.Count;
    public int  StopIndex   => _index;
    public int  Digs        => _digs;
    public float DebugDistance => _distance;

    public DigBand Band => _phase switch
    {
        Phase.Done => DigBand.Green,
        Phase.Running => _distance <= DigTuning.TrailDig   ? DigBand.Dig
                       : _distance <= DigTuning.TrailRadar ? DigBand.Yellow
                       : DigBand.Cold,
        _ => DigBand.Cold,
    };

    /// <summary>
    /// 0 at the edge of radar range, 1 on the spot. Null whenever there is no radar to draw, which
    /// is every state except "running and close enough".
    /// </summary>
    public float? RadarCloseness
    {
        get
        {
            if (_phase != Phase.Running) return null;

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
        Phase.Placing => "LAYING THE TRAIL…",
        Phase.Running => RadarCloseness is >= 1f
                             ? "DIG!!!"
                             : $"CLUE  {_index + 1} / {_stops.Count}",
        _             => "CLUE TRAIL",
    };

    public string Subtitle => _phase switch
    {
        Phase.Done    => $"{_digs} dig(s) along the way.",
        Phase.Placing => "Working out where it goes…",
        Phase.Running => RadarCloseness is >= 1f ? "This is the place. Dig." : _clue,
        _             => string.Empty,
    };

    // ── the player's actions ─────────────────────────────────────────────────

    public string Start()
    {
        if (!PropService.CanPerform(out string why)) return "cannot start — " + why;
        if (Plugin.ObjectTable.LocalPlayer == null)  return "no character loaded.";

        _stops.Clear();
        _index      = 0;
        _digs       = 0;
        _placeTicks = 0;
        _distance   = float.MaxValue;
        _clue       = string.Empty;
        _territory  = Plugin.ClientState.TerritoryType;
        _phase      = Phase.Placing;

        return "laying a trail…";
    }

    /// <summary>
    /// Digs. On the current spot this turns up the next clue and advances; on the last spot it ends
    /// the trail. As with the other tests the animation always plays — digging in the wrong place
    /// is allowed and costs only the time.
    /// </summary>
    public string Dig()
    {
        string animation = Plugin.Props.Dig();

        if (_phase != Phase.Running) return animation;

        _digs++;

        if (_distance > DigTuning.TrailDig) return animation + " Nothing but dirt here.";

        _index++;

        if (_index < _stops.Count)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null) _clue = ClueFor(player.Position, _stops[_index]);

            try { Plugin.Sound.Play(SoundService.Cue.ObjectiveProgress); }
            catch (Exception ex) { Diag.Error($"[Trail] clue cue failed: {ex.Message}"); }

            return animation + $" A clue! {_clue}";
        }

        _endedAtMs = Environment.TickCount64;
        _phase     = Phase.Done;

        try { Plugin.Sound.Play(SoundService.Cue.ChallengeComplete); }
        catch (Exception ex) { Diag.Error($"[Trail] end cue failed: {ex.Message}"); }

        string time = CompletionStore.FormatRaceTime(ElapsedSeconds);
        Diag.Info($"[Trail] finished in {time} over {_digs} dig(s).");

        return $"That's the end of the trail! {time}, {_digs} dig(s).";
    }

    public string Stop()
    {
        if (_phase == Phase.Off) return "no trail is running.";
        _phase = Phase.Off;
        _stops.Clear();
        return "trail abandoned.";
    }

    /// <summary>Repeats the clue for the spot currently being looked for.</summary>
    public string Recall()
    {
        if (_phase != Phase.Running) return "no trail is running.";
        return string.IsNullOrEmpty(_clue) ? "you have no clue yet." : _clue;
    }

    public void DrawWorld() { /* drawing the spots would replace the radar with an answer */ }

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

            if (Plugin.ClientState.TerritoryType != _territory)
            {
                Stop();
                Plugin.ChatGui.Print("[Challenges] Trail abandoned — you left the zone.");
                return;
            }

            if (_phase == Phase.Placing) { Lay(player.Position); return; }

            _distance = DigGround.Flat(player.Position, _stops[_index]);
        }
        catch (Exception ex)
        {
            Diag.Error($"[Trail] tick failed: {ex.Message}");
            _phase = Phase.Off;
        }
    }

    /// <summary>
    /// Lays the chain, each spot roughly <see cref="DigTuning.TrailSpacing"/> from the last so the
    /// trail walks somewhere rather than circling one field. A short trail is accepted rather than
    /// retried forever — the count the player is shown is the count that actually got placed.
    /// </summary>
    private void Lay(Vector3 from)
    {
        int want = Math.Max(1, DigTuning.TrailStops);

        float near = DigTuning.TrailSpacing * 0.7f;
        float far  = DigTuning.TrailSpacing * 1.3f;

        var cursor = from;

        for (int n = 0; n < want; n++)
        {
            if (!DigGround.TryPointNear(cursor, near, far, _rng, out var spot, AttemptsPerSpot)) break;
            _stops.Add(spot);
            cursor = spot;
        }

        if (_stops.Count == 0)
        {
            if (++_placeTicks < PlacementTicks) return;

            _phase = Phase.Off;
            Plugin.ChatGui.PrintError(
                "[Challenges] Could not lay a trail here — try somewhere more open.");
            return;
        }

        _phase       = Phase.Running;
        _index       = 0;
        _startedAtMs = Environment.TickCount64;
        _clue        = ClueFor(from, _stops[0]);
        _distance    = DigGround.Flat(from, _stops[0]);

        Diag.Info($"[Trail] laid {_stops.Count} stop(s) of {want} requested.");
        Plugin.ChatGui.Print($"[Challenges] A trail of {_stops.Count} lies ahead. First clue: {_clue}");
    }

    /// <summary>
    /// A clue is a bearing plus a woolly range — the same vocabulary the Sense Hunt uses, so the
    /// two tests teach the same reading rather than each inventing its own.
    /// </summary>
    private static string ClueFor(Vector3 from, Vector3 to)
        => $"{DigGround.Compass(from, to)}, {DigGround.Vagueness(DigGround.Flat(from, to))}.";
}
#endif
