using System;
using System.Collections.Concurrent;

using Dalamud.Plugin.Services;

namespace TieriChallengesFFXIV;

/// <summary>
/// The plugin's audio owner. Everything that wants a sound asks HERE and forgets about it; the
/// service plays it on its own tick, answerable to nothing else.
///
/// <para><b>Why this exists.</b> The cues used to be fired from inside the popup queues, on the
/// frame a popup happened to surface. That quietly made audio a slave to the UI: a popup that was
/// queued behind another, dropped by a cap, or not drawn because its renderer was inactive took
/// its sound down with it. Steps landing in quick succession lost their cue entirely. Sound is
/// the highest-priority feedback in this plugin and must not be able to fail for a UI reason.</para>
///
/// <para><b>The contract.</b> <see cref="Play"/> is fire-and-forget: safe from any thread, never
/// blocks, never throws, and does not care whether any window, popup or renderer exists. Requests
/// are drained on the framework tick, which is also what makes calling from a background thread
/// safe — the game's sound engine is touched only from the framework thread, never from the
/// caller's.</para>
/// </summary>
internal sealed class SoundService : IDisposable
{
    /// <summary>What happened, not which .scd entry — callers never name entries.</summary>
    public enum Cue
    {
        /// <summary>One step of a multi-step challenge landed.</summary>
        ObjectiveProgress,

        /// <summary>A challenge completed outright.</summary>
        ChallengeComplete,

        /// <summary>The user confirmed wiping all progress.</summary>
        ResetConfirmed,

        /// <summary>Arrived somewhere with at least one challenge still open.</summary>
        ZoneAvailable,
    }

    /// <summary>
    /// Backstop on a single tick's burst. Sweeping a cluster of small volumes can land several
    /// steps at once and every one of them SHOULD sound, but an unbounded drain would let a
    /// runaway producer stack arbitrarily many voices into one frame. The engine holds only 256
    /// SoundData entries in total.
    /// </summary>
    private const int MaxPerTick = 8;

    /// <summary>Bound on the backlog itself, so a stalled framework thread cannot grow it forever.</summary>
    private const int MaxPending = 32;

    /// <summary>Gap between entries while scanning — long enough to tell one sting from the next.</summary>
    private const int ScanIntervalMs = 900;

    /// <summary>
    /// Hard cap on a single scan. An unbounded "play everything" would be minutes of noise with
    /// no way to reason about where you were, and the whole point is to identify ONE number.
    /// </summary>
    private const uint MaxScanSpan = 64;

    /// <summary>
    /// Queued cues as (bank path, entry, ignore-mute) — each cue names its own .scd.
    ///
    /// <para><see cref="Play"/> never sets IgnoreMute: an ordinary cue is filtered out by
    /// <see cref="IsEnabled"/> before it is ever queued. It is set only by the deliberate one-off
    /// requests — the settings preview buttons and the dev audition commands — where the player has
    /// just pressed something specifically in order to hear it.</para>
    /// </summary>
    private readonly ConcurrentQueue<(string Bank, uint Entry, bool IgnoreMute)> _pending = new();

    // Scan state. Driven off the same tick as playback — never a sleeping thread.
    private bool _scanning;
    private uint _scanCurrent;
    private uint _scanEnd;
    private long _scanNextAtMs;

    public void Attach() => Plugin.Framework.Update += OnUpdate;

    /// <summary>
    /// Unhooks and silences. Releasing on unload matters now that sounds are started with
    /// <c>autoRelease: false</c> — a looping cue left running would outlive the plugin with
    /// nothing able to stop it.
    /// </summary>
    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        _scanning = false;
        _pending.Clear();
        GameSound.StopAll();
    }

    /// <summary>
    /// Set once at startup so cue requests can be filtered without threading a config reference
    /// through every caller. Null before binding, which simply means nothing is filtered.
    /// </summary>
    private static Configuration? _config;

    public static void Bind(Configuration config) => _config = config;

    /// <summary>
    /// Is this cue switched on? Checked at REQUEST time rather than at playback, so a disabled cue
    /// costs nothing and cannot occupy a slot in the backlog.
    /// </summary>
    public static bool IsEnabled(Cue cue)
    {
        if (_config == null) return true;
        if (_config.SoundMuted) return false;

        var off = _config.DisabledCues;
        return off == null || !off.Contains(cue.ToString());
    }

    /// <summary>Every cue the plugin can raise, with player-facing wording for the settings list.</summary>
    public static readonly (Cue Cue, string Label, string When)[] PublicCues =
    {
        (Cue.ObjectiveProgress, "Objective progress",  "part of a challenge is done"),
        (Cue.ChallengeComplete, "Challenge complete",  "a challenge finishes"),
        (Cue.ZoneAvailable,     "Zone has challenges", "you arrive somewhere with one still open"),
        (Cue.ResetConfirmed,    "Progress wiped",      "you confirm a reset"),
    };

    /// <summary>Request a cue. Fire-and-forget, thread-safe, never throws.</summary>
    public void Play(Cue cue)
    {
        if (!IsEnabled(cue)) return;

        var (bank, entry) = GameSound.CueTarget(cue);
        Request((bank, entry, false));
    }

    /// <summary>
    /// Play a cue ignoring the enable/mute filter — the settings list's preview buttons, where the
    /// point is to hear what you are about to switch off.
    /// </summary>
    /// <remarks>
    /// Bypassing <see cref="IsEnabled"/> is only half of it, and for a long time it was the only
    /// half that was implemented: <c>GameSound.Muted</c> is read again down in the playback path,
    /// so a preview of a .wav cue was silent whenever the master mute was on. The settings window
    /// leaves these buttons enabled while muted and carries a comment saying a Play button that
    /// does nothing is the worse answer — so it was a live button that did nothing, next to a note
    /// asserting the opposite. IgnoreMute is what makes the two agree.
    /// </remarks>
    public void Preview(Cue cue)
    {
        var (bank, entry) = GameSound.CueTarget(cue);
        Request((bank, entry, true));
    }

#if DEV_BUILD
    /// <summary>
    /// Every cue the plugin can raise, with the wording used in the dev sound-test panel. Adding
    /// a cue here makes it appear there automatically — the panel iterates this rather than
    /// hardcoding a list that would silently fall behind.
    ///
    /// <para>Gated on DEV_BUILD because nothing in the public build reads it: these are display
    /// labels for a panel that does not ship, and leaving them in put "Objective progress" in the
    /// public DLL for no reason.</para>
    /// </summary>
    public static readonly (Cue Cue, string Label, string When)[] AllCues =
    {
        (Cue.ObjectiveProgress, "Objective progress", "a step lands, except the last"),
        (Cue.ChallengeComplete, "Challenge complete", "a challenge finishes"),
        (Cue.ResetConfirmed,    "Reset confirmed",    "a progress wipe is accepted"),
        (Cue.ZoneAvailable,     "Zone has challenges","arriving where one is still open"),
    };
#endif

    // The audition commands are the same shape as Preview: the player typed a command whose whole
    // purpose is to hear one specific entry, so the master mute is not what they are asking about.

    /// <summary>Request an entry from the UI bank. Used by the audition command.</summary>
    public void PlayEntry(uint entry) => Request((GameSound.UiBank, entry, true));

    /// <summary>Request an entry from a named bank.</summary>
    public void PlayFrom(string bank, uint entry) => Request((bank, entry, true));

    /// <summary>
    /// Walk a range of bank entries, playing one every <see cref="ScanIntervalMs"/> and naming it
    /// in chat as it goes.
    ///
    /// <para>Exists because an index in an .scd is not a promise of audio — plenty of slots are
    /// empty, and playing one succeeds silently rather than failing. That is why entries 55 and
    /// 85 produced nothing while 50 worked, with no error anywhere to explain it. Finding an
    /// audible entry is an ear problem, so this makes it one pass instead of one rebuild per
    /// guess.</para>
    /// </summary>
    public void StartScan(uint from, uint to)
    {
        if (to < from) (from, to) = (to, from);

        if (to - from + 1 > MaxScanSpan)
        {
            to = from + MaxScanSpan - 1;
            Plugin.ChatGui.Print($"[Challenges] Scan capped at {MaxScanSpan} entries — stopping at {to}.");
        }

        _scanCurrent  = from;
        _scanEnd      = to;
        _scanning     = true;
        _scanNextAtMs = 0;   // fire the first one on the very next tick

        Plugin.ChatGui.Print($"[Challenges] Scanning {GameSound.UiBank} entries {from}–{to}. "
                           + "Note the numbers you actually hear. /tchallenges sfx stop to cancel.");
    }

    /// <summary>
    /// Stop the scan AND silence everything the plugin has started. The second half is the point:
    /// some bank entries loop, and a looping sound never ends on its own.
    /// </summary>
    public void StopScan()
    {
        _scanning = false;
        _pending.Clear();
        GameSound.StopAll();
        Plugin.ChatGui.Print("[Challenges] Stopped — scan cancelled and all plugin sounds silenced.");
    }

    public bool IsScanning => _scanning;

    private void Request((string Bank, uint Entry, bool IgnoreMute) cue)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cue.Bank)) return;

            if (_pending.Count >= MaxPending)
            {
                Diag.Warn($"[Sound] backlog full — dropping {cue.Bank} #{cue.Entry}.");
                return;
            }

            _pending.Enqueue(cue);
        }
        catch (Exception ex)
        {
            // A cue must never propagate a failure into whatever was reporting real progress.
            Diag.Error(ex, "[Sound] request failed");
        }
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            int played = 0;
            while (played < MaxPerTick && _pending.TryDequeue(out var cue))
            {
                GameSound.Play(cue.Bank, cue.Entry, ignoreMute: cue.IgnoreMute);
                played++;
            }

            if (!_scanning) return;

            long now = Environment.TickCount64;
            if (now < _scanNextAtMs) return;

            if (_scanCurrent > _scanEnd)
            {
                _scanning = false;
                Plugin.ChatGui.Print("[Challenges] Scan finished. Set a cue with "
                                   + "/tchallenges sfx complete <n> (or progress / reset).");
                return;
            }

            // Silence the previous entry before starting the next. Without this a looping entry
            // partway through the bank keeps playing under every remaining step, and by the end
            // of a scan several loops are stacked on top of each other.
            GameSound.StopAll();

            // Named BEFORE it plays, so the line is already on screen when the sound lands.
            // Tracked so a looping entry can be stopped by the next step or by sfx stop.
            Plugin.ChatGui.Print($"[Challenges] entry {_scanCurrent}");
            GameSound.Play(GameSound.UiBank, _scanCurrent, trackForStop: true, ignoreMute: true);

            _scanCurrent++;
            _scanNextAtMs = now + ScanIntervalMs;
        }
        catch (Exception ex)
        {
            Diag.Error(ex, "[Sound] drain failed");
        }
    }
}
