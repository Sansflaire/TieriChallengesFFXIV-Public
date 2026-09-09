using System;

using Dalamud.Game.ClientState.Conditions;

using LSheets = Lumina.Excel.Sheets;

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace TieriChallengesFFXIV;

/// <summary>
/// Spawns a Fashion Accessory model onto the player client-side, plays an animation with it, and
/// takes it off again when the game cancels the animation. <b>Ships in the public build</b> — challenge
/// content depends on it.
///
/// <para><b>No ownership is involved and nothing is bypassed.</b>
/// <see cref="OrnamentContainer.SetupOrnament"/> is the client-side spawn — the call the client makes
/// <i>after</i> the server has already decided — so it carries no unlock check to defeat. The
/// animation rows used here are absent from the <c>Emote</c> sheet, so <c>AgentEmote.ExecuteEmote</c>
/// cannot reach them and <c>UIState.IsEmoteUnlocked</c> never applies. Verified live on a character
/// that owns no accessory at all. Nothing is sent to the server; this is local display only.</para>
///
/// <para><b>Why this class is paranoid.</b> The two values it passes were both <i>observed</i> being
/// written by the client (attach = a real <c>Ornament</c> row, detach = <c>0</c>). An earlier version
/// invented <c>-1</c> and hard-crashed the game twice — see <c>BROKEN.md</c> 012. That post-mortem
/// produced three rules this class exists to honour, and they matter far more now that the code runs
/// on other people's machines:</para>
/// <list type="number">
/// <item><b>Guard with a precondition, never a catch.</b> A native access violation is a
/// corrupted-state exception; .NET terminates the process rather than delivering it to a
/// <c>catch</c>. Every id is validated against the sheet <i>before</i> the call.</item>
/// <item><b>Never call into game code from a teardown path.</b> <c>Dispose</c> and the Escape handler
/// run at moments the user did not choose — unload fires on every hot reload. Both only set
/// <see cref="_cancelRequested"/>; the detach happens on the next <see cref="Tick"/>, in a normal
/// frame context.</item>
/// <item><b>Refuse rather than risk it.</b> If the player is loading, mounted, in a cutscene or
/// otherwise occupied, the request is declined instead of attempted.</item>
/// </list>
/// </summary>
internal sealed unsafe class PropService
{
    /// <summary>
    /// The client's "no accessory" value. Observed, not assumed — the client wrote
    /// <c>OrnamentId 57 -&gt; 0</c> when an accessory was dismissed from its own menu.
    /// </summary>
    private const short None = 0;

    private enum Stage { Off, WaitAttach, Playing, Watching }

    private Stage  _stage;
    private int    _frames;
    private short  _ornament;
    private ushort _timeline;
    private bool   _cancelRequested;
    private long   _playStartedMs;

    /// <summary>
    /// <b>Short Dig — 4 seconds.</b> The default, and the length a routine dig should be.
    /// Found by testing with the lab slider, not chosen.
    /// </summary>
    public const int ShortDigMilliseconds = 4000;

    /// <summary>
    /// <b>Long Dig — 9 seconds.</b> The length at which an uninterrupted dig reads as a whole,
    /// deliberate piece of work rather than a quick scrape. Also found by testing.
    ///
    /// <para>Both exist as named numbers because challenge content will want to ask for "a short
    /// dig" or "a long dig" rather than carry a millisecond literal — a literal in content is a
    /// number nobody can re-tune later without hunting for every copy of it.</para>
    /// </summary>
    public const int LongDigMilliseconds = 9000;

    /// <summary>What a dig is unless something asks otherwise: the Short Dig.</summary>
    public const int DefaultHoldMilliseconds = ShortDigMilliseconds;

    /// <summary>
    /// The animation slot a dig plays on. <c>Base = 0</c> per the slot list on
    /// <c>ActionTimelineSequencer</c>, and the slot <see cref="Tick"/> already watches.
    /// </summary>
    private const uint BaseSlot = 0;

    /// <summary>
    /// How long the animation is allowed to run before the performance ends itself and the prop is
    /// removed, in milliseconds. <b>0 means no cap</b> — run until the game cancels the animation,
    /// which was the behaviour before <see cref="DefaultHoldMilliseconds"/> was settled on.
    ///
    /// <para><b>Every dig goes through here</b>, because every dig goes through
    /// <see cref="Start"/> — the tests, the chat commands and any challenge content all call
    /// <see cref="Dig"/>, and the cap is enforced in <see cref="Tick"/>'s watching stage that all of
    /// them pass through. There is no second path to keep in step.</para>
    ///
    /// <para>A cap and the game's own cancel are not alternatives; whichever happens first wins. The
    /// cap does not stop the player walking out of a dig, it only stops a dig that nothing
    /// interrupts from running to the end of its loop.</para>
    ///
    /// <para>Settable because the right number is a feel, not a fact — see the dig lab's slider.
    /// Once a value is settled on, it becomes the default here and the slider just moves off it.</para>
    ///
    /// <para>A property rather than a field on purpose: its only writer is the dev-only lab, so as a
    /// field it raised CS0649 ("never assigned") in the Release build — and this project treats a
    /// Release warning as a signal that a dev branch has leaked, which is a signal worth keeping
    /// sharp rather than learning to ignore.</para>
    /// </summary>
    public int HoldMilliseconds { get; set; } = DefaultHoldMilliseconds;

    /// <summary>
    /// Playback speed multiplier for the dig animation. 1 = untouched, 2 = twice as fast.
    ///
    /// <para><b>Driven through <c>ActionTimelineSequencer.SetSlotSpeed(slot, speed)</c></b>, which is
    /// a real member function in FFXIVClientStructs, not a guess — and the game bounds-checks the
    /// slot itself (<c>cmp edx, 0Eh / jae</c>, i.e. it returns for any slot ≥ 14) so an out-of-range
    /// slot is a no-op rather than a fault. We only ever touch slot 0.</para>
    ///
    /// <para><b>The baseline is READ, never assumed.</b> <c>GetSlotSpeed</c> exists, so the speed in
    /// force before we touch it is observed and restored verbatim. That is what makes this an
    /// acquire with a genuinely verified release rather than one that guesses 1.0 on the way out —
    /// the distinction BROKEN.md 012 exists to enforce.</para>
    ///
    /// <para><b>Speed shortens the dig, it does not lengthen it.</b> See
    /// <see cref="EffectiveHoldMilliseconds"/> — the hold is a length of ANIMATION, so a Short Dig
    /// at 2× is over in half the wall-clock time having played the same amount of dig. The two
    /// knobs are therefore independent, which they were not at first: the hold used to be raw wall
    /// clock, so 2× simply played twice as much of the loop and "short dig" stopped meaning
    /// anything.</para>
    /// </summary>
    public float PlaybackSpeed { get; set; } = 1f;

    /// <summary>Slowest and fastest we will ask for. A multiplier, so neither end is a sentinel.</summary>
    public const float MinSpeed = 0.1f;
    public const float MaxSpeed = 5f;

    /// <summary>
    /// Which lever to pull to change animation pace. <b>Three exist and it was not knowable from
    /// static reading which one actually wins</b>, so this is selectable and the lab reports all
    /// three live — the same measure-don't-assume protocol that settled the ornament question.
    /// </summary>
    public enum SpeedMode
    {
        /// <summary>
        /// <c>SetSlotSpeed</c> — the game's own setter. <b>The default: measured to work on its own
        /// (2026-09-09), and preferred over the raw array write because if the setter does any
        /// bookkeeping beyond storing the float, writing the field skips it.</b>
        /// </summary>
        SlotFunction = 0,

        /// <summary>
        /// Write <c>TimelineSpeeds[0]</c> directly. Also measured to work on its own; kept as the
        /// fallback if the setter's signature ever moves under a patch.
        /// </summary>
        SlotArray = 1,

        /// <summary>
        /// <c>TimelineContainer.OverallSpeed</c> — container-level, so it scales <b>everything the
        /// character does</b>, not just this animation. Measurement showed it is <b>not needed</b>:
        /// either per-slot lever is sufficient. Kept only for investigation; do not make it a
        /// default.
        /// </summary>
        Overall = 2,

        /// <summary>
        /// All three. Was the default while it was unknown which lever worked; now redundant, and
        /// it drags <see cref="Overall"/> along with it.
        /// </summary>
        Everything = 3,
    }

    /// <summary>
    /// Which lever to pull. Defaults to <see cref="SpeedMode.SlotFunction"/> — the narrowest one
    /// that was measured to work, rather than the broadest one that was measured to work.
    /// </summary>
    public SpeedMode SpeedMethod { get; set; } = SpeedMode.SlotFunction;

    /// <summary>The mode actually written, so the restore puts back only what it touched.</summary>
    private SpeedMode _appliedMode = SpeedMode.SlotFunction;

    private bool  _speedApplied;
    private float _speedBefore    = 1f;
    private float _arrayBefore    = 1f;
    private float _overallBefore  = 1f;

    /// <summary>
    /// The multiplier actually in force on the animation, which is 1 whenever the apply was skipped
    /// or failed. The hold is divided by THIS rather than by <see cref="PlaybackSpeed"/>: if the
    /// speed never took, the animation is playing at normal pace and cutting the dig short would
    /// truncate it for no reason.
    /// </summary>
    private float _appliedSpeed = 1f;

    /// <summary>
    /// How long the dig will actually last in wall-clock milliseconds, given the requested speed.
    /// 0 means no cap.
    ///
    /// <para><see cref="HoldMilliseconds"/> is a length of ANIMATION measured at 1×, so this is it
    /// divided by the multiplier: a 4-second dig at 2× ends after 2 seconds having played exactly
    /// the same dig, and "Short Dig" keeps meaning one thing at every speed.</para>
    /// </summary>
    public int EffectiveHoldMilliseconds => ScaleHold(PlaybackSpeed);

    private int ScaleHold(float speed)
    {
        if (HoldMilliseconds <= 0) return 0;

        if (!float.IsFinite(speed) || speed <= 0f) speed = 1f;
        speed = Math.Clamp(speed, MinSpeed, MaxSpeed);

        return Math.Max(1, (int)MathF.Round(HoldMilliseconds / speed));
    }

    /// <summary>
    /// The speed the game currently reports for the base slot, or null if it cannot be read.
    /// Purely diagnostic — the lab shows it so a leaked speed is visible rather than mysterious.
    /// </summary>
    public float? CurrentSlotSpeed
    {
        get
        {
            var chara = LocalChara();
            if (chara == null) return null;

            try   { return chara->Timeline.TimelineSequencer.GetSlotSpeed(BaseSlot); }
            catch { return null; }
        }
    }

    /// <summary>
    /// All three speed values as the game currently reports them. The point of showing every one is
    /// that a write which "does nothing" and a write that is being reverted a frame later look
    /// identical from the outside — this tells them apart.
    /// </summary>
    public string SpeedReadout()
    {
        var chara = LocalChara();
        if (chara == null) return "no character loaded.";

        try
        {
            float fn      = chara->Timeline.TimelineSequencer.GetSlotSpeed(BaseSlot);
            float array   = chara->Timeline.TimelineSequencer.TimelineSpeeds[(int)BaseSlot];
            float overall = chara->Timeline.OverallSpeed;

            return $"GetSlotSpeed {fn:0.###}   TimelineSpeeds[0] {array:0.###}   OverallSpeed {overall:0.###}";
        }
        catch (Exception ex) { return $"unreadable: {ex.Message}"; }
    }

    /// <summary>
    /// Forces the base slot back to normal speed. Recovery for the one case the restore cannot
    /// cover: the plugin being unloaded mid-dig, since teardown paths are forbidden from calling
    /// game code and the animation slot therefore keeps whatever multiplier was in force.
    /// </summary>
    public string ResetPlaybackSpeed()
    {
        var chara = LocalChara();
        if (chara == null) return "no character loaded.";

        try
        {
            chara->Timeline.TimelineSequencer.SetSlotSpeed(BaseSlot, 1f);
            chara->Timeline.TimelineSequencer.TimelineSpeeds[(int)BaseSlot] = 1f;
            chara->Timeline.OverallSpeed = 1f;

            _speedApplied  = false;
            _speedBefore   = 1f;
            _arrayBefore   = 1f;
            _overallBefore = 1f;
            _appliedSpeed  = 1f;
            return "animation speed reset to 1.0 (all three).";
        }
        catch (Exception ex)
        {
            Diag.Error($"[Prop] speed reset failed: {ex.Message}");
            return $"failed: {ex.Message}";
        }
    }

    /// <summary>Frames to wait for the model to appear before playing anyway.</summary>
    private const int AttachGrace = 300;

    /// <summary>Frames to wait for the timeline to reach slot 0 before giving up.</summary>
    private const int PlayGrace = 60;

    private static Lumina.Excel.ExcelSheet<LSheets.Ornament>?       _ornSheet;
    private static Lumina.Excel.ExcelSheet<LSheets.ActionTimeline>? _timelineSheet;

    /// <summary>True while a prop is attached or an animation is being driven.</summary>
    public bool IsPerforming => _stage != Stage.Off;

    // ── preconditions ────────────────────────────────────────────────────────

    /// <summary>
    /// Whether it is safe to attach a prop right now. Declining is always preferable to attempting:
    /// a refusal is a chat line, a bad native call is the player's game closing.
    /// </summary>
    public static bool CanPerform(out string why)
    {
        why = string.Empty;

        if (!Plugin.ClientState.IsLoggedIn)          { why = "not logged in.";              return false; }
        if (Plugin.ObjectTable.LocalPlayer == null)  { why = "no character loaded.";        return false; }

        var c = Plugin.Condition;
        if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51])
                                                     { why = "still loading.";              return false; }
        if (c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene]
            || c[ConditionFlag.WatchingCutscene78])   { why = "in a cutscene.";              return false; }
        if (c[ConditionFlag.Mounted] || c[ConditionFlag.RidingPillion])
                                                     { why = "mounted.";                    return false; }
        if (c[ConditionFlag.Occupied] || c[ConditionFlag.Occupied33] || c[ConditionFlag.Occupied38])
                                                     { why = "busy.";                       return false; }
        if (c[ConditionFlag.Unconscious])            { why = "unconscious.";                return false; }

        return true;
    }

    /// <summary>An accessory row that actually exists. The gate that replaces a try/catch.</summary>
    private static bool OrnamentIsReal(short id)
    {
        if (id == None) return true;
        if (id < 0) return false;   // every ornament id the client stores is UNSIGNED. See BROKEN.md 012.
        try
        {
            _ornSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Ornament>();
            var row = _ornSheet?.GetRowOrDefault((uint)id);
            return row != null && row.Value.Model != 0;
        }
        catch { return false; }
    }

    /// <summary>An animation row that exists and actually has an animation behind it.</summary>
    private static bool TimelineIsReal(ushort id)
    {
        if (id == 0) return false;
        try
        {
            _timelineSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.ActionTimeline>();
            var row = _timelineSheet?.GetRowOrDefault(id);
            return row != null && !string.IsNullOrEmpty(row.Value.Key.ExtractText());
        }
        catch { return false; }
    }

    // ── public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches <paramref name="ornamentRow"/>, plays <paramref name="timelineId"/>, and removes the
    /// prop when the game cancels the animation. Returns a human-readable result; never throws.
    /// </summary>
    public string Start(short ornamentRow, ushort timelineId)
    {
        if (!CanPerform(out string why))       return why;
        if (!OrnamentIsReal(ornamentRow))      return $"accessory {ornamentRow} is not a real row.";
        if (!TimelineIsReal(timelineId))       return $"animation {timelineId} has nothing behind it.";

        if (IsPerforming) Stop();

        _ornament = ornamentRow;
        _timeline = timelineId;

        string? r = SetOrnament(ornamentRow);
        if (r != null) return r;

        _stage           = Stage.WaitAttach;
        _frames          = 0;
        _cancelRequested = false;

        // Held from the moment the prop is asked for, not from when the animation starts: the
        // attach takes frames, and a step taken during them would cancel the dig before it began.
        SetInputBlocked(true);

        return "performing.";
    }

    /// <summary>Ends the performance and removes the prop. Safe to call when nothing is running.</summary>
    public string Stop()
    {
        _stage           = Stage.Off;
        _frames          = 0;
        _cancelRequested = false;

        // FIRST, and before any early return below. Every way a performance can end comes through
        // here, so this is the one place that can guarantee the player gets their controls back —
        // releasing it later, or per exit path, is how input stays stuck after an odd failure.
        SetInputBlocked(false);

        var chara = LocalChara();
        if (chara == null) { _speedApplied = false; return "stopped."; }

        // Speed first, so the idle we drop back to plays at normal pace rather than inheriting the
        // dig's multiplier for a frame.
        RestoreSpeed(chara);

        try
        {
            chara->Timeline.PlayActionTimeline(3, 0);   // 3 = normal/idle, read from the sheet
            chara->Timeline.TimelineSequencer.SetSlotTimeline(0, 3);
        }
        catch (Exception ex) { Diag.Error($"[Prop] stop animation failed: {ex.Message}"); }

        SetOrnament(None);
        return "stopped.";
    }

    /// <summary>
    /// Requests cancellation WITHOUT touching game code. This is what the Escape handler and unload
    /// call: the actual detach happens on the next <see cref="Tick"/>, in a normal frame context.
    /// Calling into the game from those paths is what crashed the client in BROKEN.md 012.
    /// </summary>
    public void RequestCancel()
    {
        if (_stage != Stage.Off) _cancelRequested = true;
    }

    // ── per-frame ────────────────────────────────────────────────────────────

    public void Tick()
    {
        if (_stage == Stage.Off)
        {
            // Belt and braces. Stop() is the only way out and it always releases, but "the player
            // cannot move" is severe enough to be worth a second, unconditional guarantee: if the
            // block is somehow still on with nothing running, it comes off this frame.
            if (Plugin.Input.Blocking) SetInputBlocked(false);
            return;
        }

        try
        {
            if (_cancelRequested) { Stop(); return; }

            var chara = LocalChara();
            if (chara == null) { _stage = Stage.Off; return; }

            _frames++;
            ushort slot0 = chara->Timeline.TimelineSequencer.TimelineIds[0];

            switch (_stage)
            {
                case Stage.WaitAttach:
                    if (chara->OrnamentData.OrnamentId == (ushort)_ornament || _frames >= AttachGrace)
                    {
                        // Baseline BEFORE the play call, while the values still describe what the
                        // character was doing rather than what we just did to it.
                        CaptureSpeedBaseline(chara);

                        try { chara->Timeline.PlayActionTimeline(_timeline, 0); }
                        catch (Exception ex) { Diag.Error($"[Prop] play failed: {ex.Message}"); Stop(); return; }

                        _stage  = Stage.Playing;
                        _frames = 0;
                    }
                    break;

                case Stage.Playing:
                    if (slot0 == _timeline)
                    {
                        // The animation is genuinely in the slot now — this is the first moment a
                        // speed write can survive, and the first honest moment to start the clock.
                        ApplySpeed(chara, announce: true);

                        _playStartedMs = Environment.TickCount64;
                        _stage         = Stage.Watching;
                        _frames        = 0;
                    }
                    else if (_frames >= PlayGrace) Stop();
                    break;

                case Stage.Watching:
                    // Two ways out, whichever comes first. The game owns the cancel
                    // (IsMotionCanceledByMoving) and we only notice it; the cap is ours and ends a
                    // dig that nothing interrupted rather than letting it loop.
                    if (slot0 != _timeline) { Stop(); break; }

                    // Every frame — the game recalculates its own speeds, so a single write can be
                    // undone on the next update. See ApplySpeed.
                    ApplySpeed(chara, announce: false);

                    // Scaled by the speed that actually took, so the cap always ends the same
                    // amount of DIG rather than the same amount of clock.
                    int cap = ScaleHold(_appliedSpeed);
                    if (cap > 0 && Environment.TickCount64 - _playStartedMs >= cap)
                        Stop();
                    break;
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Prop] tick failed: {ex.Message}");
            _stage = Stage.Off;
        }
    }

    // ── the one native write ─────────────────────────────────────────────────

    /// <summary>
    /// Calls the client-side accessory spawn. Returns null on success or a message on refusal.
    /// The id is validated by the caller; this re-checks anyway, because it is the last line before
    /// game code and a catch here would not save the process.
    /// </summary>
    private static string? SetOrnament(short id)
    {
        if (!OrnamentIsReal(id)) return $"refused: {id} is not a valid accessory id.";

        var chara = LocalChara();
        if (chara == null) return "no character loaded.";

        try
        {
            chara->OrnamentData.SetupOrnament(id, 0);
            Diag.Info($"[Prop] SetupOrnament({id})");
            return null;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Prop] SetupOrnament({id}) failed: {ex.Message}");
            return $"failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Reads the slot's current speed, then applies ours. Does nothing at all when the requested
    /// speed is 1 — an acquire that changes nothing is still an acquire, and skipping it means
    /// there is no restore to get wrong on the way out.
    /// </summary>
    /// <summary>
    /// Records every speed value we might overwrite, BEFORE the animation is asked for. Captured
    /// this early because the values are only trustworthy while the character is still doing
    /// whatever it was doing — reading them back after the dig has been installed would capture our
    /// own change, and the restore would then put back the wrong number.
    /// </summary>
    private void CaptureSpeedBaseline(Character* chara)
    {
        _speedApplied = false;
        _appliedSpeed = 1f;

        try
        {
            _speedBefore   = chara->Timeline.TimelineSequencer.GetSlotSpeed(BaseSlot);
            _arrayBefore   = chara->Timeline.TimelineSequencer.TimelineSpeeds[(int)BaseSlot];
            _overallBefore = chara->Timeline.OverallSpeed;

            if (!float.IsFinite(_speedBefore)   || _speedBefore   <= 0f) _speedBefore   = 1f;
            if (!float.IsFinite(_arrayBefore)   || _arrayBefore   <= 0f) _arrayBefore   = 1f;
            if (!float.IsFinite(_overallBefore) || _overallBefore <= 0f) _overallBefore = 1f;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Prop] speed baseline read failed: {ex.Message}");
            _speedBefore = _arrayBefore = _overallBefore = 1f;
        }
    }

    /// <summary>
    /// Writes the multiplier. Called EVERY FRAME while the dig runs, not once.
    ///
    /// <para><b>Once was the bug.</b> 0.84.42.7 set the speed immediately after asking for the
    /// animation — before the timeline had actually been installed into the slot, which takes a
    /// frame or more (that delay is why <see cref="Stage.Playing"/> exists at all). The install
    /// then reset the slot and the dig played at normal pace. On top of that the game runs its own
    /// <c>CalculateAndApplyOverallSpeed</c>, so even a correctly-timed single write can be undone
    /// on the next update. Re-writing every frame beats both without needing to know which one
    /// happened.</para>
    /// </summary>
    private void ApplySpeed(Character* chara, bool announce)
    {
        float speed = PlaybackSpeed;
        if (!float.IsFinite(speed)) return;

        speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        if (MathF.Abs(speed - 1f) < 0.001f) { _appliedSpeed = 1f; return; }

        try
        {
            var mode = SpeedMethod;

            if (mode is SpeedMode.SlotFunction or SpeedMode.Everything)
                chara->Timeline.TimelineSequencer.SetSlotSpeed(BaseSlot, speed);

            if (mode is SpeedMode.SlotArray or SpeedMode.Everything)
                chara->Timeline.TimelineSequencer.TimelineSpeeds[(int)BaseSlot] = speed;

            if (mode is SpeedMode.Overall or SpeedMode.Everything)
                chara->Timeline.OverallSpeed = speed;

            _speedApplied = true;
            _appliedSpeed = speed;
            _appliedMode  = mode;

            if (announce)
                Diag.Info($"[Prop] speed {speed:0.##}x via {mode} "
                        + $"(was slot {_speedBefore:0.##} / array {_arrayBefore:0.##} / overall {_overallBefore:0.##}), "
                        + $"dig ends after {ScaleHold(speed)} ms");
        }
        catch (Exception ex)
        {
            Diag.Error($"[Prop] speed apply failed: {ex.Message}");
        }
    }

    /// <summary>Puts back every value we touched. Observed at capture, not assumed.</summary>
    private void RestoreSpeed(Character* chara)
    {
        _appliedSpeed = 1f;

        if (!_speedApplied) return;
        _speedApplied = false;

        try
        {
            // Only what we actually wrote. Restoring a lever we never touched would write a stale
            // baseline over whatever the game legitimately changed during the dig — mounting,
            // haste, anything that moves OverallSpeed on its own.
            var mode = _appliedMode;

            if (mode is SpeedMode.SlotFunction or SpeedMode.Everything)
                chara->Timeline.TimelineSequencer.SetSlotSpeed(BaseSlot, _speedBefore);

            if (mode is SpeedMode.SlotArray or SpeedMode.Everything)
                chara->Timeline.TimelineSequencer.TimelineSpeeds[(int)BaseSlot] = _arrayBefore;

            if (mode is SpeedMode.Overall or SpeedMode.Everything)
                chara->Timeline.OverallSpeed = _overallBefore;
        }
        catch (Exception ex) { Diag.Error($"[Prop] speed restore failed: {ex.Message}"); }
    }

    /// <summary>
    /// Turns the movement/jump block on or off. Managed state only — it flips a bool that two
    /// already-installed hooks read, so it is safe from anywhere, including the paths that must
    /// never call game code.
    /// </summary>
    private static void SetInputBlocked(bool blocked)
    {
        try { Plugin.Input.Blocking = blocked; }
        catch (Exception ex) { Diag.Error($"[Prop] input block toggle failed: {ex.Message}"); }
    }

    private static Character* LocalChara()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return null;
        var addr = lp.Address;
        return addr == IntPtr.Zero ? null : (Character*)addr;
    }

    // ── the content this ships with ──────────────────────────────────────────

    /// <summary>Ornament row 57 — the Shovel.</summary>
    public const short Shovel = 57;

    /// <summary><c>ornament_sp/m6017/onm_sp01</c> — the Shovel's dig.</summary>
    public const ushort DigTimeline = 13383;

    /// <summary>Spawn the Shovel and dig with it. The first prop performance the plugin ships.</summary>
    public string Dig() => Start(Shovel, DigTimeline);
}
