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

    private bool  _speedApplied;
    private float _speedBefore = 1f;

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
            _speedApplied = false;
            _speedBefore  = 1f;
            return "animation speed reset to 1.0.";
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
        return "performing.";
    }

    /// <summary>Ends the performance and removes the prop. Safe to call when nothing is running.</summary>
    public string Stop()
    {
        _stage           = Stage.Off;
        _frames          = 0;
        _cancelRequested = false;

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
        if (_stage == Stage.Off) return;

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
                        try { chara->Timeline.PlayActionTimeline(_timeline, 0); }
                        catch (Exception ex) { Diag.Error($"[Prop] play failed: {ex.Message}"); Stop(); return; }

                        ApplySpeed(chara);

                        // The cap is measured from here — the moment the animation was asked for —
                        // not from Start(), so waiting for the model to attach never eats into it.
                        _playStartedMs = Environment.TickCount64;
                        _stage         = Stage.Playing;
                        _frames        = 0;
                    }
                    break;

                case Stage.Playing:
                    if (slot0 == _timeline) { _stage = Stage.Watching; _frames = 0; }
                    else if (_frames >= PlayGrace) Stop();
                    break;

                case Stage.Watching:
                    // Two ways out, whichever comes first. The game owns the cancel
                    // (IsMotionCanceledByMoving) and we only notice it; the cap is ours and ends a
                    // dig that nothing interrupted rather than letting it loop.
                    if (slot0 != _timeline) { Stop(); break; }

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
    private void ApplySpeed(Character* chara)
    {
        _speedApplied = false;
        _appliedSpeed = 1f;

        float speed = PlaybackSpeed;
        if (!float.IsFinite(speed)) return;

        speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        if (MathF.Abs(speed - 1f) < 0.001f) return;

        try
        {
            _speedBefore = chara->Timeline.TimelineSequencer.GetSlotSpeed(BaseSlot);
            if (!float.IsFinite(_speedBefore) || _speedBefore <= 0f) _speedBefore = 1f;

            chara->Timeline.TimelineSequencer.SetSlotSpeed(BaseSlot, speed);
            _speedApplied = true;
            _appliedSpeed = speed;

            Diag.Info($"[Prop] slot {BaseSlot} speed {_speedBefore:0.##} -> {speed:0.##}, "
                    + $"dig ends after {ScaleHold(speed)} ms");
        }
        catch (Exception ex)
        {
            Diag.Error($"[Prop] speed apply failed: {ex.Message}");
            _speedApplied = false;
        }
    }

    /// <summary>Puts back the speed that was in force before we touched it. Observed, not assumed.</summary>
    private void RestoreSpeed(Character* chara)
    {
        _appliedSpeed = 1f;

        if (!_speedApplied) return;
        _speedApplied = false;

        try   { chara->Timeline.TimelineSequencer.SetSlotSpeed(BaseSlot, _speedBefore); }
        catch (Exception ex) { Diag.Error($"[Prop] speed restore failed: {ex.Message}"); }
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
