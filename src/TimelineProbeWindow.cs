#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using LSheets = Lumina.Excel.Sheets;

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace TieriChallengesFFXIV;

/// <summary>
/// <b>Developer-only. Throwaway.</b> Drives an arbitrary <c>ActionTimeline</c> row onto the local
/// player, to answer whether an animation the account does not own can be played.
///
/// <para><b>ANSWERED YES</b> (2026-08-28, live): row <b>13383</b> — <c>ornament_sp/m6017/onm_sp01</c>,
/// the Shovel accessory's animation — played with no ornament attached and nothing owned. Nothing is
/// being bypassed: that row is absent from the <c>Emote</c> sheet, so <c>AgentEmote.ExecuteEmote</c>
/// cannot reach it and <c>UIState.IsEmoteUnlocked</c> never applies. <c>PlayActionTimeline</c> takes
/// an ActionTimeline row id and has no ownership concept to check.</para>
///
/// <para><b>THE ORNAMENT ATTACH IS GONE, AND MUST NOT COME BACK WITHOUT A VERIFIED DETACH.</b>
/// An earlier revision called <c>OrnamentContainer.SetupOrnament(-1, 0)</c> to remove the model.
/// <c>-1</c> was invented, never verified, and <b>it hard-crashed the game</b> — see
/// <c>BROKEN.md</c> 012. Every field the client stores an ornament id in is UNSIGNED
/// (<c>OrnamentContainer.OrnamentId</c> and <c>CommonSpawnData.OrnamentId</c> are <c>ushort</c>,
/// <c>Ornament.OrnamentId</c> is <c>uint</c>), so a negative id was never a sentinel the game
/// recognises — as a <c>short</c> it reaches the callee as 0xFFFF and indexes a ~58-row sheet.
/// Attaching a model with no known way to remove it is a trap, so the whole attach path is
/// withdrawn rather than repaired with a second guess. This window is now read-only about
/// ornaments.</para>
///
/// <para><b>A C# try/catch does not make a native call safe.</b> An access violation inside game
/// code is a corrupted-state exception; .NET terminates the process rather than delivering it to a
/// catch block. The <c>catch</c>es here only cover the managed side (a null function pointer
/// resolving, a sheet miss). Everything that reaches native code must be guarded by a
/// <b>precondition</b> — see <see cref="TargetIsSafe"/> — never by a catch.</para>
///
/// <para>Raw ImGui deliberately. Dev-only surfaces are exempt from the match-the-main-window rule
/// in CLAUDE.md §3, exactly as <c>ChallengeCreatorWindow</c> and <see cref="LiveProbeWindow"/> are.</para>
/// </summary>
internal sealed unsafe class TimelineProbeWindow
{
    public bool IsVisible;

    /// <summary>ActionTimeline row 3 is <c>normal/idle</c> — read from the sheet, not assumed.</summary>
    private const ushort IdleTimeline = 3;

    /// <summary>The row this probe was built for. <c>ornament_sp/m6017/onm_sp01</c>, the Shovel.</summary>
    private const int DefaultTimeline = 13383;

    private int  _timelineId = DefaultTimeline;
    private bool _hold;

    // Rolling capture of slot 0 after a fire, as (id, consecutiveFrames). "the write never landed"
    // and "the write landed and was reverted one frame later" are indistinguishable in a still
    // screenshot and need completely different fixes, so the frame counts are the whole point.
    private readonly List<(ushort Id, int Frames)> _trace = new();
    private bool _tracing;
    private int  _traceFrames;
    private const int TraceLimit = 240;

    private readonly List<string> _log = new();

    private static Lumina.Excel.ExcelSheet<LSheets.ActionTimeline>? _sheet;
    private static Lumina.Excel.ExcelSheet<LSheets.Ornament>?       _ornSheet;

    // ── the precondition that stands in for a catch ──────────────────────────

    /// <summary>
    /// Whether <see cref="_timelineId"/> is safe to hand to game code. A row that does not exist, or
    /// one whose <c>Key</c> is empty, has no animation behind it and is exactly the shape of input
    /// that makes native code dereference something it should not.
    ///
    /// <para>This is a gate, not a warning. The fire buttons are disabled when it is false, because
    /// a catch block downstream would not save the process.</para>
    /// </summary>
    private bool TargetIsSafe => TimelineIsSafe((ushort)_timelineId);

    private static bool TimelineIsSafe(ushort id)
    {
        if (id == 0) return false;
        var row = RowOf(id);
        if (row == null) return false;
        return !string.IsNullOrEmpty(row.Value.Key.ExtractText());
    }

    // ── draw ─────────────────────────────────────────────────────────────────

    public void Draw()
    {
        if (!IsVisible)
        {
            // Nothing native to unwind any more, but the plugin must stop driving the animation.
            _hold = false;
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(660, 700), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Timeline Probe (dev, throwaway)##tc_anim", ref IsVisible))
        {
            ImGui.End();
            return;
        }

        try
        {
            var chara = LocalChara();

            SampleTrace(chara);
            ApplyHold(chara);

            DrawLive(chara);
            ImGui.Separator();
            DrawTarget();
            ImGui.Separator();
            DrawFireButtons(chara);
            ImGui.Separator();
            DrawOrnamentReadOnly(chara);
            ImGui.Separator();
            DrawTrace();
            ImGui.Separator();
            DrawLog();
        }
        catch (Exception ex)
        {
            // Managed faults only. A native access violation never arrives here — see the class doc.
            Diag.Error($"[AnimProbe] draw failed: {ex.Message}");
        }

        ImGui.End();
    }

    private void DrawLive(Character* chara)
    {
        ImGui.TextColored(Accent, "Live");

        if (chara == null)
        {
            ImGui.TextDisabled("  not logged in");
            return;
        }

        ushort slot0 = chara->Timeline.TimelineSequencer.TimelineIds[0];
        float  spd0  = chara->Timeline.TimelineSequencer.TimelineSpeeds[0];

        ImGui.Text($"  Mode          {chara->Mode} ({(byte)chara->Mode})  param {chara->ModeParam}");
        ImGui.Text($"  Slot 0        {slot0}  {KeyOf(slot0)}");
        ImGui.Text($"  Slot 0 speed  {spd0:0.000}    OverallSpeed {chara->Timeline.OverallSpeed:0.000}");
        ImGui.Text($"  BaseOverride  {(chara->Timeline.BaseOverride == 0 ? "(none)" : chara->Timeline.BaseOverride.ToString())}");
    }

    private void DrawTarget()
    {
        ImGui.TextColored(Accent, "Target row");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("ActionTimeline id##tc_anim_id", ref _timelineId);
        if (_timelineId < 0) _timelineId = 0;
        if (_timelineId > ushort.MaxValue) _timelineId = ushort.MaxValue;

        var row = RowOf((ushort)_timelineId);
        if (row == null)
        {
            ImGui.TextColored(Warn, "  Row does not exist — firing is disabled.");
            return;
        }

        string key = row.Value.Key.ExtractText();
        ImGui.Text($"  Key   {(string.IsNullOrEmpty(key) ? "(empty)" : key)}");
        ImGui.Text($"  Slot {row.Value.Slot}   LoadType {row.Value.LoadType}   Type {row.Value.Type}");
        ImGui.Text($"  Resident {row.Value.Resident}   IsLoop {row.Value.IsLoop}   CanceledByMoving {row.Value.IsMotionCanceledByMoving}");

        if (string.IsNullOrEmpty(key))
            ImGui.TextColored(Warn, "  Empty key — no animation behind this row. Firing is disabled.");
    }

    private void DrawFireButtons(Character* chara)
    {
        ImGui.TextColored(Accent, "Fire");

        bool safe = chara != null && TargetIsSafe;

        if (!safe)
        {
            ImGui.TextColored(Warn, chara == null
                ? "  Not logged in."
                : "  Target row is not safe to fire (missing row or empty key).");
        }

        if (!safe) ImGui.BeginDisabled();

        ushort id = (ushort)_timelineId;

        // No lambdas here on purpose: the calls are written out so the guarded pointer is used
        // directly in the same scope it was checked in, with nothing captured.
        if (ImGui.Button("PlayActionTimeline(id, 0)", new Vector2(220, 0)) && safe)
        {
            ushort before = chara->Timeline.TimelineSequencer.TimelineIds[0];
            try { chara->Timeline.PlayActionTimeline(id, 0); Note($"PlayActionTimeline({id},0) — slot0 was {before}"); StartTrace(); }
            catch (Exception ex) { Note($"PlayActionTimeline threw: {ex.Message}"); }
        }

        ImGui.SameLine();

        if (ImGui.Button("PlayActionTimeline(id, id)", new Vector2(220, 0)) && safe)
        {
            ushort before = chara->Timeline.TimelineSequencer.TimelineIds[0];
            try { chara->Timeline.PlayActionTimeline(id, id); Note($"PlayActionTimeline({id},{id}) — slot0 was {before}"); StartTrace(); }
            catch (Exception ex) { Note($"PlayActionTimeline threw: {ex.Message}"); }
        }

        if (ImGui.Button("Sequencer.PlayTimeline(id)", new Vector2(220, 0)) && safe)
        {
            ushort before = chara->Timeline.TimelineSequencer.TimelineIds[0];
            try { chara->Timeline.TimelineSequencer.PlayTimeline(id); Note($"PlayTimeline({id}) — slot0 was {before}"); StartTrace(); }
            catch (Exception ex) { Note($"PlayTimeline threw: {ex.Message}"); }
        }

        ImGui.SameLine();

        if (ImGui.Button("SetSlotTimeline(0, id)", new Vector2(220, 0)) && safe)
        {
            ushort before = chara->Timeline.TimelineSequencer.TimelineIds[0];
            try { chara->Timeline.TimelineSequencer.SetSlotTimeline(0, id); Note($"SetSlotTimeline(0,{id}) — slot0 was {before}"); StartTrace(); }
            catch (Exception ex) { Note($"SetSlotTimeline threw: {ex.Message}"); }
        }

        if (!safe) ImGui.EndDisabled();

        ImGui.Spacing();

        // Row 3 is normal/idle and is verified to exist, so Stop needs no gate beyond being logged in.
        if (chara == null) ImGui.BeginDisabled();
        if (ImGui.Button("Stop -> normal/idle", new Vector2(220, 0)) && chara != null)
        {
            _hold = false;
            try { chara->Timeline.PlayActionTimeline(IdleTimeline, 0); Note("stop -> normal/idle"); }
            catch (Exception ex) { Note($"stop threw: {ex.Message}"); }
        }
        if (chara == null) ImGui.EndDisabled();

        ImGui.SameLine();

        if (!safe) ImGui.BeginDisabled();
        ImGui.Checkbox("Hold (re-apply every frame)", ref _hold);
        if (!safe) ImGui.EndDisabled();

        ImGui.TextDisabled("  Hold separates \"the write was rejected\" from \"the write was overwritten\".");
    }

    /// <summary>
    /// Re-applies the target every frame while Hold is on. Re-checks the precondition each frame
    /// rather than trusting the one made when the box was ticked — the id is editable while Hold
    /// is running, so a safe target can become an unsafe one between frames.
    /// </summary>
    private void ApplyHold(Character* chara)
    {
        if (!_hold) return;

        if (chara == null || !TargetIsSafe)
        {
            _hold = false;
            Note("hold released — target no longer safe");
            return;
        }

        try { chara->Timeline.TimelineSequencer.SetSlotTimeline(0, (ushort)_timelineId); }
        catch (Exception ex)
        {
            _hold = false;
            Note($"hold released — threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Ornaments are READ-ONLY here. See the class doc: the attach path crashed the game and is
    /// withdrawn until a detach is verified rather than guessed.
    /// </summary>
    private void DrawOrnamentReadOnly(Character* chara)
    {
        ImGui.TextColored(Accent, "Ornament (read-only)");

        if (chara == null)
        {
            ImGui.TextDisabled("  not logged in");
            return;
        }

        ushort orn = chara->OrnamentData.OrnamentId;
        bool   obj = chara->OrnamentData.OrnamentObject != null;

        ImGui.Text($"  OrnamentId  {(orn == 0 ? "(none)" : orn.ToString())}   object {(obj ? "attached" : "null")}");
        if (orn != 0) ImGui.TextDisabled($"  {OrnamentLabel(orn)}");

        ImGui.TextColored(Warn, "  Attach/detach removed — SetupOrnament(-1) crashed the game.");
        ImGui.TextDisabled("  Do not re-add it without a VERIFIED way to remove an attached model.");
        ImGui.TextDisabled("  See BROKEN.md 012.");
    }

    // ── trace ────────────────────────────────────────────────────────────────

    private void StartTrace()
    {
        _trace.Clear();
        _traceFrames = 0;
        _tracing     = true;
    }

    private void SampleTrace(Character* chara)
    {
        if (!_tracing || chara == null) return;

        ushort now = chara->Timeline.TimelineSequencer.TimelineIds[0];

        if (_trace.Count > 0 && _trace[^1].Id == now)
            _trace[^1] = (now, _trace[^1].Frames + 1);
        else
            _trace.Add((now, 1));

        if (++_traceFrames >= TraceLimit) _tracing = false;
    }

    private void DrawTrace()
    {
        ImGui.TextColored(Accent, $"Slot 0 trace  ({(_tracing ? $"sampling {_traceFrames}/{TraceLimit}" : "idle")})");

        if (_trace.Count == 0)
        {
            ImGui.TextDisabled("  Fire something to start a trace.");
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.25f));
        ImGui.BeginChild("##tc_anim_trace", new Vector2(0, 110), true);
        foreach (var (id, frames) in _trace)
            ImGui.Text($"  {id,-8} x{frames,-5} {KeyOf(id)}");
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.TextDisabled("  One entry = it stuck. Two entries ending back at the old id = overwritten.");
    }

    // ── log ──────────────────────────────────────────────────────────────────

    private void DrawLog()
    {
        ImGui.TextColored(Accent, "Log");

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.25f));
        ImGui.BeginChild("##tc_anim_log", new Vector2(0, 100), true);
        for (int i = _log.Count - 1; i >= 0; i--) ImGui.TextUnformatted(_log[i]);
        ImGui.EndChild();
        ImGui.PopStyleColor();

        if (ImGui.Button("Clear log", new Vector2(120, 0))) _log.Clear();
    }

    private void Note(string line)
    {
        _log.Add(line);
        if (_log.Count > 60) _log.RemoveAt(0);
        Diag.Info($"[AnimProbe] {line}");
    }

    // ── release ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops the plugin driving the player's animation. <b>Deliberately touches nothing native</b> —
    /// it is called from <c>Plugin.HandleEscape</c>, which runs on every Escape press, and from
    /// unload. The revision that called into game code from here turned every Escape press in a dev
    /// build into a crash. Managed state only; this method cannot fail.
    /// </summary>
    public void ReleaseHold()
    {
        if (!_hold) return;
        _hold = false;
        Note("hold released — escape/unload");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly Vector4 Accent = new(1.00f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Warn   = new(1.00f, 0.55f, 0.35f, 1f);

    private static Character* LocalChara()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return null;
        var addr = lp.Address;
        return addr == IntPtr.Zero ? null : (Character*)addr;
    }

    private static LSheets.ActionTimeline? RowOf(ushort id)
    {
        try
        {
            _sheet ??= Plugin.DataManager.GetExcelSheet<LSheets.ActionTimeline>();
            return _sheet?.GetRowOrDefault(id);
        }
        catch { return null; }
    }

    private static string KeyOf(ushort id)
    {
        if (id == 0) return "(none)";
        var row = RowOf(id);
        if (row == null) return "(no row)";
        string k = row.Value.Key.ExtractText();
        return string.IsNullOrEmpty(k) ? "(empty key)" : k;
    }

    private static string OrnamentLabel(uint rowId)
    {
        if (rowId == 0) return "(none)";
        try
        {
            _ornSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Ornament>();
            var row = _ornSheet?.GetRowOrDefault(rowId);
            if (row == null) return "(no row)";
            string n = row.Value.Singular.ExtractText();
            return string.IsNullOrEmpty(n)
                ? "(unnamed)"
                : $"{n}  (model {row.Value.Model}, attach {row.Value.AttachmentPoint})";
        }
        catch { return "(lookup failed)"; }
    }
}
#endif
