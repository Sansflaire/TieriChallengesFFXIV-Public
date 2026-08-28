#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using LSheets = Lumina.Excel.Sheets;

using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace TieriChallengesFFXIV;

/// <summary>
/// <b>Developer-only. Throwaway.</b> Answers one question and then gets deleted: can the client
/// be made to play an arbitrary <c>ActionTimeline</c> row on the local player, for an animation
/// the account does not own?
///
/// <para>The motivating case is row <b>13383</b> — <c>ornament_sp/m6017/onm_sp01</c>. That row is
/// NOT in the <c>Emote</c> sheet, so <c>AgentEmote.ExecuteEmote</c> cannot reach it and
/// <c>UIState.IsEmoteUnlocked</c> never applies to it. The only way in is to drive the character's
/// own <see cref="TimelineContainer"/> directly, which takes an ActionTimeline row id and therefore
/// has no concept of ownership to check. What is genuinely unknown is whether the row's
/// <c>Resident</c> flag means the .pap is only loaded while ornament 6017 is actually attached —
/// if so, firing it bare does nothing and the ornament has to be attached locally first
/// (<see cref="OrnamentContainer.SetupOrnament"/>, also client-side).</para>
///
/// <para>Three things make the result readable rather than a guess:</para>
/// <list type="bullet">
/// <item>Slot 0 is sampled EVERY frame after a fire and the distinct values are listed with frame
/// counts. "the write never landed" and "the write landed and was reverted one frame later" look
/// identical in a still screenshot and need completely different fixes.</item>
/// <item>Four different entry points are offered side by side, because they are not equivalent —
/// <c>PlayActionTimeline</c> drives the container, <c>PlayTimeline</c> drives the sequencer, and
/// <c>SetSlotTimeline</c> pokes the slot with no driver at all.</item>
/// <item>Hold mode re-applies every frame, which separates "rejected" from "outrun".</item>
/// </list>
///
/// <para>Raw ImGui deliberately. Dev-only surfaces are exempt from the match-the-main-window rule
/// in CLAUDE.md §3, exactly as <c>ChallengeCreatorWindow</c> and <see cref="LiveProbeWindow"/> are.</para>
/// </summary>
internal sealed unsafe class TimelineProbeWindow
{
    public bool IsVisible;

    /// <summary>ActionTimeline row 3 is <c>normal/idle</c> — verified against the sheet, not assumed.</summary>
    private const ushort IdleTimeline = 3;

    /// <summary>The row this probe exists to test. <c>ornament_sp/m6017/onm_sp01</c>.</summary>
    private const int DefaultTimeline = 13383;

    /// <summary>
    /// Ornament row 57 = <b>Shovel</b> (Model 4936, AttachmentPoint 14) — the accessory row 13383's
    /// animation belongs to, confirmed by Trist recognising the animation on 2026-08-28.
    ///
    /// <para>An earlier guess here was 17, from an inferred "<c>m####</c> = 6000 + Ornament row id"
    /// mapping that fit the one live sample available (row 1 = Parasol ↔ <c>m6001</c>). It is WRONG:
    /// the Shovel is row 57, not 17. The <c>m####</c> appears to number ornament *archetypes* rather
    /// than rows — which is why ~58 ornament rows produce only a handful of <c>ornament_sp</c>
    /// timelines, all the parasols presumably sharing <c>m6001</c>. Not needed for anything here,
    /// since <see cref="OrnamentContainer.SetupOrnament"/> takes the row id, but do not resurrect
    /// the 6000+ mapping on the strength of the parasol coincidence.</para>
    /// </summary>
    private const int DefaultOrnament = 57;

    /// <summary>
    /// Frames to wait between attaching the model and firing the animation. The attach is not
    /// instant — the model has to load — and firing on the same frame races that.
    /// </summary>
    private int _attachDelay = 6;

    private int  _timelineId = DefaultTimeline;
    private int  _ornamentId = DefaultOrnament;
    private bool _hold;

    /// <summary>
    /// Where the coupled attach → play → auto-detach sequence is up to. The whole point is that the
    /// model's lifetime is tied to the animation's: the game cancels the timeline on movement
    /// (<c>IsMotionCanceledByMoving</c>), <see cref="Stage.Watching"/> notices slot 0 change away
    /// from the target, and the model comes off in the same frame. Nothing here asks the game to
    /// stop the animation — it is the game stopping it that drives the teardown.
    /// </summary>
    private enum Stage { Off, Attaching, Playing, Watching }

    private Stage _stage;
    private int   _stageFrames;
    private string _stageNote = string.Empty;

    /// <summary>Frames to allow the fired timeline to appear in slot 0 before giving up.</summary>
    private const int PlayGrace = 30;

    // Rolling capture of slot 0 after a fire. Stored as (id, consecutiveFrames) so a one-frame
    // flicker is visible as "13383 x1" rather than vanishing between two draws.
    private readonly List<(ushort Id, int Frames)> _trace = new();
    private bool _tracing;
    private int  _traceFrames;
    private const int TraceLimit = 240;

    private readonly List<string> _log = new();

    private static Lumina.Excel.ExcelSheet<LSheets.ActionTimeline>? _sheet;

    public void Draw()
    {
        if (!IsVisible)
        {
            // Closing the window mid-performance must not strand the model on the character.
            if (_stage != Stage.Off) ReleasePerformance("window closed");
            return;
        }

        // Runs before the early-out below so the sequence keeps advancing even on a frame where
        // the window is collapsed and Begin returns false.
        TickPerformance(LocalChara());

        ImGui.SetNextWindowSize(new Vector2(660, 760), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Timeline Probe (dev, throwaway)##tc_anim", ref IsVisible))
        {
            ImGui.End();
            return;
        }

        try
        {
            var chara = LocalChara();

            SampleTrace(chara);
            if (_hold) Reapply(chara);

            DrawLive(chara);
            ImGui.Separator();
            DrawTarget();
            ImGui.Separator();
            DrawFireButtons(chara);
            ImGui.Separator();
            DrawOrnament(chara);
            ImGui.Separator();
            DrawTrace();
            ImGui.Separator();
            DrawLog();
        }
        catch (Exception ex)
        {
            // A dev tool throwing into the draw loop would take the game down. Never.
            Diag.Error($"[AnimProbe] draw failed: {ex.Message}");
        }

        ImGui.End();
    }

    // ── live state ───────────────────────────────────────────────────────────

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

        ushort orn = chara->OrnamentData.OrnamentId;
        bool   obj = chara->OrnamentData.OrnamentObject != null;
        ImGui.Text($"  OrnamentId    {(orn == 0 ? "(none)" : orn.ToString())}   object {(obj ? "attached" : "null")}");
    }

    // ── the row under test ───────────────────────────────────────────────────

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
            ImGui.TextDisabled("  row not found");
            return;
        }

        string key = row.Value.Key.ExtractText();
        ImGui.Text($"  Key   {(string.IsNullOrEmpty(key) ? "(empty)" : key)}");
        ImGui.Text($"  Slot {row.Value.Slot}   LoadType {row.Value.LoadType}   Type {row.Value.Type}");
        ImGui.Text($"  Resident {row.Value.Resident}   IsLoop {row.Value.IsLoop}   CanceledByMoving {row.Value.IsMotionCanceledByMoving}");

        if (string.IsNullOrEmpty(key))
            ImGui.TextColored(Warn, "  Empty key — this row has no animation and will do nothing.");
    }

    // ── the four entry points ────────────────────────────────────────────────

    private void DrawFireButtons(Character* chara)
    {
        ImGui.TextColored(Accent, "Fire");
        ImGui.TextDisabled("  These are NOT equivalent — that is the point of offering all four.");

        bool ok = chara != null;
        if (!ok) ImGui.BeginDisabled();

        ushort id = (ushort)_timelineId;

        if (ImGui.Button("PlayActionTimeline(id, 0)", new Vector2(220, 0)))
            Fire(chara, "PlayActionTimeline(id,0)", () => chara->Timeline.PlayActionTimeline(id, 0));

        ImGui.SameLine();

        if (ImGui.Button("PlayActionTimeline(id, id)", new Vector2(220, 0)))
            Fire(chara, "PlayActionTimeline(id,id)", () => chara->Timeline.PlayActionTimeline(id, id));

        if (ImGui.Button("Sequencer.PlayTimeline(id)", new Vector2(220, 0)))
            Fire(chara, "Sequencer.PlayTimeline(id)", () => chara->Timeline.TimelineSequencer.PlayTimeline(id));

        ImGui.SameLine();

        if (ImGui.Button("SetSlotTimeline(0, id)", new Vector2(220, 0)))
            Fire(chara, "SetSlotTimeline(0,id)", () => chara->Timeline.TimelineSequencer.SetSlotTimeline(0, id));

        ImGui.Spacing();

        // IsLoop is true on 13383, so without an explicit stop it runs until something else
        // cancels it. Movement will, but the probe should not depend on that.
        if (ImGui.Button("Stop -> normal/idle", new Vector2(220, 0)))
        {
            _hold = false;
            Fire(chara, "stop -> idle", () => chara->Timeline.PlayActionTimeline(IdleTimeline, 0));
        }

        ImGui.SameLine();
        ImGui.Checkbox("Hold (re-apply every frame)", ref _hold);

        if (!ok) ImGui.EndDisabled();

        ImGui.TextDisabled("  Hold separates \"the write was rejected\" from \"the write was overwritten\".");
    }

    private void Reapply(Character* chara)
    {
        if (chara == null) return;
        try { chara->Timeline.TimelineSequencer.SetSlotTimeline(0, (ushort)_timelineId); }
        catch (Exception ex)
        {
            _hold = false;
            Note($"hold aborted: {ex.Message}");
        }
    }

    private void Fire(Character* chara, string label, Action call)
    {
        if (chara == null) return;

        ushort before = chara->Timeline.TimelineSequencer.TimelineIds[0];
        try
        {
            call();
            Note($"{label} id={_timelineId} — slot0 was {before}");
            StartTrace();
        }
        catch (Exception ex)
        {
            Note($"{label} THREW: {ex.Message}");
        }
    }

    // ── what actually happened, frame by frame ───────────────────────────────

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
        ImGui.BeginChild("##tc_anim_trace", new Vector2(0, 120), true);
        foreach (var (id, frames) in _trace)
            ImGui.Text($"  {id,-8} x{frames,-5} {KeyOf(id)}");
        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.TextDisabled("  One entry = it stuck. Two entries ending back at the old id = overwritten.");
    }

    // ── local ornament attach, the fallback if Resident needs the model ──────

    private void DrawOrnament(Character* chara)
    {
        ImGui.TextColored(Accent, "Ornament (client-side attach)");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Ornament row id##tc_anim_orn", ref _ornamentId);
        if (_ornamentId < 0)     _ornamentId = 0;
        if (_ornamentId > short.MaxValue) _ornamentId = short.MaxValue;

        ImGui.SameLine();
        ImGui.TextDisabled($"  {OrnamentLabel((uint)_ornamentId)}");

        bool ok = chara != null;
        if (!ok) ImGui.BeginDisabled();

        if (ImGui.Button("SetupOrnament(id)", new Vector2(220, 0)))
            Attach(chara, (short)_ornamentId, "manual attach");

        ImGui.SameLine();

        if (ImGui.Button("SetupOrnament(-1)  detach", new Vector2(220, 0)))
            Attach(chara, -1, "manual detach");

        ImGui.Spacing();
        ImGui.TextColored(Accent, "Coupled performance");
        ImGui.TextDisabled("  Attach the model, play the animation, and pull the model the instant");
        ImGui.TextDisabled("  the game cancels the animation. The teardown is driven BY the cancel.");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Attach delay (frames)##tc_anim_delay", ref _attachDelay);
        if (_attachDelay < 0)   _attachDelay = 0;
        if (_attachDelay > 240) _attachDelay = 240;

        if (_stage == Stage.Off)
        {
            if (ImGui.Button("Perform  (attach -> play -> auto-detach)", new Vector2(340, 0)))
                StartPerformance(chara);
        }
        else
        {
            if (ImGui.Button("Cancel performance", new Vector2(340, 0)))
                ReleasePerformance("cancelled by button");
        }

        if (!ok) ImGui.EndDisabled();

        ImGui.Text($"  Stage: {_stage}  frame {_stageFrames}");
        if (!string.IsNullOrEmpty(_stageNote)) ImGui.TextDisabled($"  {_stageNote}");
    }

    // ── coupled attach → play → auto-detach ──────────────────────────────────

    private void StartPerformance(Character* chara)
    {
        if (chara == null) return;

        _stage       = Stage.Attaching;
        _stageFrames = 0;
        _stageNote   = "attaching model";

        Attach(chara, (short)_ornamentId, "perform: attach");
        StartTrace();
    }

    /// <summary>
    /// Advances the sequence one frame. Split out of the draw body so the stage machine cannot
    /// stall on a frame where the window is collapsed.
    /// </summary>
    private void TickPerformance(Character* chara)
    {
        if (_stage == Stage.Off) return;

        if (chara == null)
        {
            // Logging out mid-performance: forget the sequence rather than poking a null character.
            _stage     = Stage.Off;
            _stageNote = "character gone";
            return;
        }

        _stageFrames++;
        ushort slot0 = chara->Timeline.TimelineSequencer.TimelineIds[0];
        ushort want  = (ushort)_timelineId;

        switch (_stage)
        {
            case Stage.Attaching:
                if (_stageFrames < _attachDelay) break;
                try
                {
                    chara->Timeline.PlayActionTimeline(want, 0);
                    Note($"perform: play {want}");
                }
                catch (Exception ex)
                {
                    ReleasePerformance($"play threw: {ex.Message}");
                    break;
                }
                _stage       = Stage.Playing;
                _stageFrames = 0;
                _stageNote   = "waiting for the timeline to take";
                break;

            case Stage.Playing:
                if (slot0 == want)
                {
                    _stage       = Stage.Watching;
                    _stageFrames = 0;
                    _stageNote   = "running — will detach when the game cancels it";
                }
                else if (_stageFrames >= PlayGrace)
                {
                    ReleasePerformance("timeline never took");
                }
                break;

            case Stage.Watching:
                // The game owns the cancel (IsMotionCanceledByMoving). We only notice and clean up.
                if (slot0 != want) ReleasePerformance($"animation ended (slot 0 -> {slot0})");
                break;
        }
    }

    /// <summary>
    /// Ends a performance and takes the model off. Safe to call at any time, including when nothing
    /// is running — Escape and unload both route here.
    /// </summary>
    public void ReleasePerformance(string why)
    {
        bool wasRunning = _stage != Stage.Off;

        _stage       = Stage.Off;
        _stageFrames = 0;
        _stageNote   = why;

        var chara = LocalChara();
        if (chara == null) return;

        Attach(chara, -1, wasRunning ? $"perform: detach ({why})" : $"detach ({why})");
    }

    private void Attach(Character* chara, short ornamentId, string label)
    {
        if (chara == null) return;
        try
        {
            chara->OrnamentData.SetupOrnament(ornamentId, 0);
            Note($"{label}: SetupOrnament({ornamentId})");
        }
        catch (Exception ex) { Note($"{label} THREW: {ex.Message}"); }
    }

    // ── log ──────────────────────────────────────────────────────────────────

    private void DrawLog()
    {
        ImGui.TextColored(Accent, "Log");

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.25f));
        ImGui.BeginChild("##tc_anim_log", new Vector2(0, 110), true);
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

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly Vector4 Accent = new(1.00f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 Warn   = new(1.00f, 0.55f, 0.35f, 1f);

    private static Character* LocalChara()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return null;
        return (Character*)lp.Address;
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

    private static Lumina.Excel.ExcelSheet<LSheets.Ornament>? _ornSheet;

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

    private static string KeyOf(ushort id)
    {
        if (id == 0) return "(none)";
        var row = RowOf(id);
        if (row == null) return "(no row)";
        string k = row.Value.Key.ExtractText();
        return string.IsNullOrEmpty(k) ? "(empty key)" : k;
    }
}
#endif
