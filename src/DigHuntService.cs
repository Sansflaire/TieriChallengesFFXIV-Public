using System;
using System.Numerics;

namespace TieriChallengesFFXIV;

/// <summary>
/// <b>Test 1 — Sense Hunt.</b> One spot is buried somewhere around the
/// player. They get a compass Sense on demand and a proximity readout that warms red → yellow →
/// green and finally reads DIG. Digging on the spot ends it and reports the time.
///
/// <para><b>The bearing is a snapshot, not a live feed.</b> The HUD keeps showing whatever the last
/// Sense said even as the player moves and it goes stale — which is the point. A bearing that
/// updated itself would make Sense a button nobody presses twice, and the hunt would collapse into
/// following an arrow.</para>
///
/// <para><b>Escape does not stop a hunt</b>, so this is never reached from
/// <c>Plugin.HandleEscape</c>. Escape releases, it does not destroy — a running hunt is a running
/// clock exactly like a race, and silently ending one the player is four minutes into is the case
/// the standing rule exists to prevent. Escape still cancels the dig <i>animation</i> through
/// <c>PropService</c>, which is a release and is correct.</para>
/// </summary>
internal sealed class DigHuntService : IDigTest
{
    private enum Phase { Off, Placing, Hunting, Found }

    /// <summary>Frames spent looking for somewhere to bury it before giving up.</summary>
    private const int PlacementTicks = 6;

    /// <summary>Ground samples per frame while placing. Split across frames so there is no hitch.</summary>
    private const int SamplesPerTick = 40;

    /// <summary>How long the result stays on the HUD after a successful dig.</summary>
    private const long FoundHoldMs = 15_000;

    private readonly Random _rng = new();

    private Phase   _phase;
    private Vector3 _goal;
    private uint    _territory;
    private long    _startedAtMs;
    private long    _endedAtMs;
    private int     _placeTicks;
    private float   _distance = float.MaxValue;
    private DigBand _band;

    public string Name => "Sense Hunt";

    public bool IsActive => _phase != Phase.Off;

    public DigBand Band => _phase == Phase.Found ? DigBand.Green : _band;

    /// <summary>Compass word from the last Sense — deliberately stale. See the class remark.</summary>
    public string LastSense { get; private set; } = string.Empty;

    public int SenseCount { get; private set; }

    /// <summary>
    /// Live distance to the spot. <b>Never show this to the player</b> — a live number turns the
    /// whole hunt into walking down a gradient. The lab window shows it; the HUD must not.
    /// </summary>
    public float DebugDistance => _distance;

    public float? RadarCloseness => null;

    public double ElapsedSeconds => _phase switch
    {
        Phase.Hunting => (Environment.TickCount64 - _startedAtMs) / 1000.0,
        Phase.Found   => (_endedAtMs - _startedAtMs) / 1000.0,
        _             => 0.0,
    };

    public string Headline => _phase switch
    {
        Phase.Found   => "FOUND IT!",
        Phase.Placing => "BURYING…",
        _ => _band switch
        {
            DigBand.Dig                                     => "DIG!!!",
            DigBand.Green or DigBand.Yellow or DigBand.Red   => "NEARBY!!!",
            _                                               => "HUNTING",
        },
    };

    public string Subtitle => _phase switch
    {
        Phase.Found   => $"{SenseCount} sense(s) used.",
        Phase.Placing => "Finding somewhere to bury it…",
        _ => _band switch
        {
            DigBand.Dig    => "The ground gives under your boot.",
            DigBand.Green  => "It is right about here.",
            DigBand.Yellow => "Warmer.",
            DigBand.Red    => "Something is close.",
            _              => string.IsNullOrEmpty(LastSense)
                                  ? "Sense to find your bearing."
                                  : $"You sensed it {LastSense}.",
        },
    };

    // ── the player's actions ─────────────────────────────────────────────────

    /// <summary>
    /// Arms a hunt. <b>The spot is not chosen here</b> — <see cref="Tick"/> does the sampling,
    /// because sampling fires raycasts into the game's collision world and a chat command handler
    /// is a context this code does not control, whereas Tick is called from the draw loop and is
    /// known to be the main thread. Guessing at thread affinity for a native call is the mistake
    /// in BROKEN.md 012.
    /// </summary>
    public string Start()
    {
        if (!PropService.CanPerform(out string why)) return "cannot start — " + why;
        if (Plugin.ObjectTable.LocalPlayer == null)  return "no character loaded.";

        _phase      = Phase.Placing;
        _territory  = Plugin.ClientState.TerritoryType;
        _placeTicks = 0;
        _distance   = float.MaxValue;
        _band       = DigBand.Cold;
        SenseCount  = 0;
        LastSense   = string.Empty;

        return "searching for a likely spot…";
    }

    public string Sense()
    {
        if (_phase != Phase.Hunting) return "no hunt is running.";

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        SenseCount++;
        LastSense = DigGround.Compass(player.Position, _goal);

        return $"You sense the spot is {LastSense}. It feels {DigGround.Vagueness(_distance)}.";
    }

    /// <summary>
    /// Digs. The animation plays wherever the player is standing — you are allowed to dig in the
    /// wrong place, and a hunt where a wrong guess costs nothing but the seconds it takes is a
    /// better hunt than one that refuses to let you try.
    /// </summary>
    public string Dig()
    {
        string animation = Plugin.Props.Dig();

        if (_phase != Phase.Hunting) return animation;

        // Judged now, banked later. Position cannot change while the dig runs — input is blocked —
        // so evaluating here and applying on completion gives the same answer as re-testing at the
        // end, without needing the player to still be there for a second check.
        _pendingHit = _distance <= DigTuning.HuntDig;

        return animation + (_pendingHit ? " You strike something…" : " Nothing but dirt here.");
    }

    public void DigFinished(bool completed)
    {
        bool hit = _pendingHit;
        _pendingHit = false;

        if (!hit || _phase != Phase.Hunting) return;

        if (!completed)
        {
            Plugin.ChatGui.Print("[Challenges] You stopped digging too soon — it is still down there.");
            return;
        }

        _endedAtMs = Environment.TickCount64;
        _phase     = Phase.Found;

        try { Plugin.Sound.Play(SoundService.Cue.ChallengeComplete); }
        catch (Exception ex) { Diag.Error($"[Hunt] found cue failed: {ex.Message}"); }

        string time = CompletionStore.FormatRaceTime(ElapsedSeconds);
        Diag.Info($"[Hunt] found in {time} after {SenseCount} sense(s).");

        Plugin.ChatGui.Print($"[Challenges] You found it! {time}, {SenseCount} sense(s).");
    }

    /// <summary>Whether the dig currently running would find the spot, if it is allowed to finish.</summary>
    private bool _pendingHit;

    public string Stop()
    {
        if (_phase == Phase.Off) return "no hunt is running.";
        _phase = Phase.Off;
        _band  = DigBand.Cold;
        return "hunt abandoned.";
    }

    public void DrawWorld() { /* nothing to show — finding it unaided is the whole test */ }

    // ── per-frame ────────────────────────────────────────────────────────────

    public void Tick()
    {
        if (_phase == Phase.Off) return;

        try
        {
            if (_phase == Phase.Found)
            {
                if (Environment.TickCount64 - _endedAtMs >= FoundHoldMs) _phase = Phase.Off;
                return;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null || !Plugin.ClientState.IsLoggedIn) { _phase = Phase.Off; return; }

            // The spot is a point in one zone's coordinate space and means nothing outside it.
            if (Plugin.ClientState.TerritoryType != _territory)
            {
                _phase = Phase.Off;
                Plugin.ChatGui.Print("[Challenges] Hunt abandoned — you left the zone.");
                return;
            }

            if (_phase == Phase.Placing) { Place(player.Position); return; }

            _distance = DigGround.Flat(player.Position, _goal);
            _band     = BandFor(_distance);
        }
        catch (Exception ex)
        {
            Diag.Error($"[Hunt] tick failed: {ex.Message}");
            _phase = Phase.Off;
        }
    }

    /// <summary>
    /// Spends one frame's raycast budget looking for somewhere to bury it, and gives up cleanly
    /// rather than searching forever. Failure is a real outcome — a small enclosed zone genuinely
    /// may have nowhere valid inside the placement ring, and saying so beats hanging.
    /// </summary>
    private void Place(Vector3 from)
    {
        if (DigGround.TryPointNear(from, DigTuning.HuntMinPlacement, DigTuning.HuntMaxPlacement,
                                   _rng, out var spot, SamplesPerTick))
        {
            _goal        = spot;
            _phase       = Phase.Hunting;
            _startedAtMs = Environment.TickCount64;
            _distance    = DigGround.Flat(from, spot);
            _band        = BandFor(_distance);

            Diag.Info($"[Hunt] spot placed {_distance:0} yalms away in territory {_territory}.");
            Plugin.ChatGui.Print("[Challenges] Something is buried nearby. Sense to find your bearing.");
            return;
        }

        if (++_placeTicks < PlacementTicks) return;

        _phase = Phase.Off;
        Plugin.ChatGui.PrintError(
            "[Challenges] Could not find anywhere to bury it around here — try somewhere more open.");
    }

    private static DigBand BandFor(float d)
    {
        if (d <= DigTuning.HuntDig)    return DigBand.Dig;
        if (d <= DigTuning.HuntHot)    return DigBand.Green;
        if (d <= DigTuning.HuntWarm)   return DigBand.Yellow;
        if (d <= DigTuning.HuntNearby) return DigBand.Red;
        return DigBand.Cold;
    }
}
