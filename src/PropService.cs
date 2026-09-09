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
        if (chara == null) return "stopped.";

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
                        _stage  = Stage.Playing;
                        _frames = 0;
                    }
                    break;

                case Stage.Playing:
                    if (slot0 == _timeline) { _stage = Stage.Watching; _frames = 0; }
                    else if (_frames >= PlayGrace) Stop();
                    break;

                case Stage.Watching:
                    // The game owns the cancel (IsMotionCanceledByMoving). We only notice and clean up.
                    if (slot0 != _timeline) Stop();
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
