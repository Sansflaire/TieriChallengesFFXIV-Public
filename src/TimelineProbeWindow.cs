#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using LSheets = Lumina.Excel.Sheets;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

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

    /// <summary>Ornament row 57 = Shovel — the accessory row 13383's animation belongs to.</summary>
    private const int DefaultOrnament = 57;

    private int  _timelineId = DefaultTimeline;
    private int  _ornamentId = DefaultOrnament;
    private bool _hold;

    // Every transition of OrnamentContainer.OrnamentId, newest last. This is how the game's own
    // "no ornament" value gets LEARNED rather than guessed: summon an accessory, dismiss it, and
    // read the value the client itself writes. Guessing that number is what crashed the game.
    private readonly List<string> _ornTrace = new();
    private ushort _lastOrnId;
    private bool   _ornSeen;

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

    /// <summary>
    /// Runs EVERY frame from <c>Plugin.DrawUI</c>, window open or not.
    ///
    /// <para>Sampling used to live inside <see cref="Draw"/>, which early-returns when the window is
    /// closed — so firing anything from a chat command produced no trace at all, and "did stop
    /// actually work?" had to be asked rather than read off the log. Same blind spot as the trace
    /// only ever existing on screen. Observation is cheap and belongs where it cannot be switched
    /// off by closing a window.</para>
    /// </summary>
    public void Tick()
    {
        try
        {
            var chara = LocalChara();
            SampleTrace(chara);
            SampleOrnament(chara);
            TickDig(chara);
            TickNativeShovel(chara);
        }
        catch (Exception ex)
        {
            Diag.Error($"[AnimProbe] tick failed: {ex.Message}");
        }
    }

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

            // Sampling happens in Tick(), which runs whether or not this window is open.
            ApplyHold(chara);

            DrawLive(chara);
            ImGui.Separator();
            DrawTarget();
            ImGui.Separator();
            DrawFireButtons(chara);
            ImGui.Separator();
            DrawNativeOrnament(chara);
            ImGui.Separator();
            DrawOrnamentReadOnly(chara);
            ImGui.Separator();
            DrawWeaponModels(chara);
            ImGui.Separator();
            DrawLoadedModelPaths(chara);
            ImGui.Separator();
            DrawShovelSwap(chara);
            ImGui.Separator();
            DrawModelPathCheck();
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
    /// Ornaments are READ-ONLY here, and stay that way. See the class doc: writing the container
    /// crashed the game, so the supported route is to let the PLAYER summon the accessory through
    /// the game's own Fashion Accessory menu and for this window only to watch.
    ///
    /// <para>Two things this panel exists to answer. <b>Ownership</b> —
    /// <see cref="PlayerState.IsOrnamentUnlocked"/> is a read, so it settles whether the supported
    /// route is even available for a given accessory without touching anything. <b>The game's own
    /// "none" value</b> — the transition recorder captures what the client writes to
    /// <c>OrnamentId</c> when an accessory is dismissed. That is the number the removed attach path
    /// needed and invented instead.</para>
    /// </summary>
    private void DrawOrnamentReadOnly(Character* chara)
    {
        ImGui.TextColored(Accent, "Ornament (read-only — the game does the attaching)");

        if (chara == null)
        {
            ImGui.TextDisabled("  not logged in");
            return;
        }

        ushort orn = chara->OrnamentData.OrnamentId;
        bool   obj = chara->OrnamentData.OrnamentObject != null;

        ImGui.Text($"  Live OrnamentId  {orn}   object {(obj ? "attached" : "null")}");
        if (orn != 0) ImGui.TextDisabled($"  {OrnamentLabel(orn)}");

        ImGui.Spacing();
        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Ornament row id##tc_anim_orn", ref _ornamentId);
        if (_ornamentId < 0) _ornamentId = 0;
        if (_ornamentId > ushort.MaxValue) _ornamentId = ushort.MaxValue;

        ImGui.SameLine();
        ImGui.TextDisabled($"  {OrnamentLabel((uint)_ornamentId)}");

        // Precondition before the native read, per the rule the crash bought: validate the id
        // against the sheet first and simply do not call when it is not a real row.
        if (OrnamentRowOf((uint)_ornamentId) == null)
        {
            ImGui.TextColored(Warn, "  Not a real Ornament row — ownership not checked.");
        }
        else
        {
            bool? owned = IsOrnamentOwned((uint)_ornamentId);
            if (owned == null)
                ImGui.TextDisabled("  Ownership unavailable (PlayerState not ready).");
            else if (owned.Value)
                ImGui.TextColored(Good, "  OWNED — summon it from the game's Fashion Accessory menu.");
            else
                ImGui.TextColored(Warn, "  NOT owned — the supported route is unavailable for this one.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"  OrnamentId transitions seen this session ({_ornTrace.Count}):");
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.25f));
        ImGui.BeginChild("##tc_anim_orntrace", new Vector2(0, 74), true);
        if (_ornTrace.Count == 0)
            ImGui.TextDisabled("  Summon and dismiss an accessory to record what the game writes.");
        else
            for (int i = _ornTrace.Count - 1; i >= 0; i--) ImGui.TextUnformatted(_ornTrace[i]);
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// Records every change to <c>OrnamentContainer.OrnamentId</c>. Pure observation — the value
    /// the game writes on dismiss is the fact the crashed revision should have gone and looked up.
    /// </summary>
    private void SampleOrnament(Character* chara)
    {
        if (chara == null) return;

        ushort now = chara->OrnamentData.OrnamentId;
        if (_ornSeen && now == _lastOrnId) return;

        if (_ornSeen)
        {
            string line = $"  {_lastOrnId} -> {now}   ({OrnamentLabel(now)})";
            _ornTrace.Add(line);
            if (_ornTrace.Count > 40) _ornTrace.RemoveAt(0);
            Diag.Info($"[AnimProbe] OrnamentId {_lastOrnId} -> {now}");
        }

        _lastOrnId = now;
        _ornSeen   = true;
    }

    /// <summary>
    /// Dumps the weapon-model triple the game is using for the attached accessory, and for the
    /// player's own hands.
    ///
    /// <para>This exists to MEASURE the one number the Glamourer-style route needs. A fashion
    /// accessory is a separate <see cref="Ornament"/> actor, but it is a <c>Character</c>, so it
    /// carries its own <c>DrawDataContainer</c> — and the model it renders is a
    /// <see cref="WeaponModelId"/> exactly like a weapon. <c>DrawDataContainer.LoadWeapon</c> is a
    /// client-side loader with no ownership concept, so if the Shovel's triple can be read off an
    /// accessory the player DOES own, the same triple can later be loaded onto a character that
    /// does not own it.</para>
    ///
    /// <para>Reading it is the whole point. The alternative was inventing a <c>Type</c>/<c>Variant</c>
    /// pair, which is the exact move that produced BROKEN.md 012.</para>
    /// </summary>
    private void DrawWeaponModels(Character* chara)
    {
        ImGui.TextColored(Accent, "Weapon models (read-only — measuring the shovel's triple)");

        if (chara == null)
        {
            ImGui.TextDisabled("  not logged in");
            return;
        }

        DumpWeapon("  player main", chara, 0);
        DumpWeapon("  player off ", chara, 1);

        var orn = chara->OrnamentData.OrnamentObject;
        if (orn == null)
        {
            ImGui.TextDisabled("  ornament: not summoned — bring one out to read its model");
            return;
        }

        var oc = (Character*)orn;
        DumpWeapon("  ORNAMENT   ", oc, 0);
        DumpWeapon("  ornament 2 ", oc, 1);

        // The weapon slots came back all zeros on a summoned Shovel, so the accessory does NOT
        // render through DrawDataContainer at all — "read the triple, load it as a weapon" is dead.
        // Its model must therefore live on the actor itself; ModelContainer is where that is.
        try
        {
            var mc = oc->ModelContainer;
            string line = $"  ORNAMENT model  ModelCharaId {mc.ModelCharaId}  Skeleton {mc.ModelSkeletonId}"
                        + $"  (alt {mc.ModelCharaId_2}/{mc.ModelSkeletonId_2})";
            ImGui.TextUnformatted(line);
            if (_weaponSeen.Add("ornmodel|" + mc.ModelCharaId + "/" + mc.ModelSkeletonId))
                Diag.Info($"[AnimProbe]{line}");
        }
        catch (Exception ex)
        {
            ImGui.TextDisabled($"  ORNAMENT model unreadable ({ex.Message})");
        }
    }

    private void DumpWeapon(string label, Character* c, int slot)
    {
        try
        {
            var span = c->DrawData.WeaponData;
            if (slot >= span.Length) { ImGui.TextDisabled($"{label}  (no slot)"); return; }

            var m = span[slot].ModelId;
            string line = $"{label}  Id {m.Id}  Type {m.Type}  Variant {m.Variant}  "
                        + $"Stain {m.Stain0}/{m.Stain1}";
            ImGui.TextUnformatted(line);

            // Logged as well as shown: the on-screen-only mistake has cost two round trips already.
            string key = $"{label}|{m.Value}";
            if (_weaponSeen.Add(key)) Diag.Info($"[AnimProbe] weapon{line}");
        }
        catch (Exception ex)
        {
            ImGui.TextDisabled($"{label}  unreadable ({ex.Message})");
        }
    }

    private readonly HashSet<string> _weaponSeen = new();

    // ── ask the game what it actually loaded ─────────────────────────────────

    /// <summary>
    /// Prints the real model file paths the summoned accessory is rendering from.
    ///
    /// <para>All six guessed path conventions came back negative, which means the conventions were
    /// wrong — the asset is obviously present, the game is drawing it. So stop guessing shapes and
    /// read the answer: the Ornament actor has a <c>DrawObject</c>, which is a
    /// <see cref="CharacterBase"/>, whose models each carry a <c>ModelResourceHandle</c> with the
    /// <c>FileName</c> the game loaded. That is the exact path, with no convention involved.</para>
    ///
    /// <para>Once known, it is the input to the Penumbra redirect route: point an equippable
    /// weapon's path at this file and the shovel renders in her hand with no memory writes.</para>
    /// </summary>
    private void DrawLoadedModelPaths(Character* chara)
    {
        ImGui.TextColored(Accent, "Loaded model paths (read-only — the real answer)");

        if (chara == null) { ImGui.TextDisabled("  not logged in"); return; }

        var orn = chara->OrnamentData.OrnamentObject;
        if (orn == null)
        {
            ImGui.TextDisabled("  Summon the accessory — its paths can only be read while it is drawn.");
            return;
        }

        try
        {
            var draw = ((Character*)orn)->GameObject.DrawObject;
            if (draw == null) { ImGui.TextDisabled("  ornament has no DrawObject yet"); return; }

            var cb = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)draw;
            int slots = cb->SlotCount;
            ImGui.Text($"  SlotCount {slots}");

            var models = cb->ModelsSpan;
            for (int i = 0; i < models.Length; i++)
            {
                var m = models[i].Value;
                if (m == null) continue;

                var mrh = m->ModelResourceHandle;
                if (mrh == null) continue;

                string path = mrh->ResourceHandle.FileName.ToString();
                if (string.IsNullOrEmpty(path)) continue;

                ImGui.TextColored(Good, $"  [{i}] {path}");
                if (_weaponSeen.Add("mdl|" + path))
                    Diag.Info($"[AnimProbe] ORNAMENT MODEL PATH [{i}] {path}");
            }
        }
        catch (Exception ex)
        {
            ImGui.TextDisabled($"  unreadable ({ex.Message})");
        }
    }

    // ── native attach: the game's own accessory spawn, no ownership ──────────

    /// <summary>
    /// The game's "no accessory" value. <b>Observed, not invented</b> — the client wrote
    /// <c>OrnamentId 57 -> 0</c> when the Shovel was dismissed from its own menu on 2026-09-06.
    /// The value that crashed the game was <c>-1</c>, which nothing in the client ever writes:
    /// every field storing an ornament id is unsigned. See BROKEN.md 012.
    /// </summary>
    private const short OrnamentNone = 0;

    private string _nativeStatus = string.Empty;

    /// <summary>
    /// Calls <c>OrnamentContainer.SetupOrnament</c> — the client-side accessory spawn.
    ///
    /// <para>No ownership is involved: this is the call the client makes <i>after</i> the server has
    /// already decided, so it carries no unlock check. That is what makes it the only route that
    /// satisfies "works for a brand-new player with zero accessories".</para>
    ///
    /// <para><b>Precondition, not a catch.</b> The id must be either <see cref="OrnamentNone"/> or a
    /// real <c>Ornament</c> row. A C# catch cannot survive a bad native call, so the gate is here
    /// and the caller is disabled when it fails — the lesson BROKEN.md 012 paid for.</para>
    /// </summary>
    private static string SetOrnamentNative(Character* chara, short id)
    {
        if (chara == null) return "not logged in.";

        if (id != OrnamentNone && OrnamentRowOf((uint)id) == null)
            return $"refused: {id} is not a real Ornament row.";

        try
        {
            ushort before = chara->OrnamentData.OrnamentId;
            chara->OrnamentData.SetupOrnament(id, 0);
            Diag.Info($"[AnimProbe] SetupOrnament({id}) — OrnamentId was {before}");
            return id == OrnamentNone ? $"detach fired (was {before})." : $"attach {id} fired (was {before}).";
        }
        catch (Exception ex)
        {
            Diag.Error($"[AnimProbe] SetupOrnament({id}) threw: {ex.Message}");
            return $"failed: {ex.Message}";
        }
    }

    private void DrawNativeOrnament(Character* chara)
    {
        ImGui.TextColored(Accent, "Native accessory attach (no ownership — the real route)");

        if (chara == null) { ImGui.TextDisabled("  not logged in"); return; }

        ushort live = chara->OrnamentData.OrnamentId;
        ImGui.Text($"  live OrnamentId {live}");

        ImGui.TextColored(Warn, "  STEP 1 — verify detach FIRST, while the Shovel is legitimately summoned.");
        ImGui.TextDisabled("  Owned state means the server still holds truth, so a desync is recoverable");
        ImGui.TextDisabled("  by zoning or re-summoning. Test the teardown before building on it.");

        if (ImGui.Button("TEST detach: SetupOrnament(0)", new Vector2(280, 0)))
            _nativeStatus = SetOrnamentNative(chara, OrnamentNone);

        ImGui.Spacing();
        ImGui.TextDisabled("  STEP 2 — once detach is proven, these need no accessory owned at all.");

        if (ImGui.Button("Attach Shovel (57)", new Vector2(200, 0)))
            _nativeStatus = SetOrnamentNative(chara, DefaultOrnament);

        ImGui.SameLine();
        if (ImGui.Button("Detach", new Vector2(120, 0)))
            _nativeStatus = SetOrnamentNative(chara, OrnamentNone);

        ImGui.SameLine();
        if (ImGui.Button("Full: attach -> dig -> detach", new Vector2(240, 0)))
            _nativeStatus = StartNativeShovel();

        if (!string.IsNullOrEmpty(_nativeStatus)) ImGui.TextDisabled($"  {_nativeStatus}");

        // ── the proof that ownership is irrelevant ──────────────────────────
        ImGui.Spacing();
        ImGui.TextColored(Warn, "  STEP 3 — PROVE it needs no ownership.");
        ImGui.TextDisabled("  Every test so far ran on the Shovel, which IS owned, so none of them");
        ImGui.TextDisabled("  actually prove ownership is irrelevant. Attaching an accessory that is");
        ImGui.TextDisabled("  NOT owned is the only thing that settles it.");

        if (ImGui.Button("Find accessories I do NOT own", new Vector2(280, 0)))
            _unownedList = FindUnowned();

        if (!string.IsNullOrEmpty(_unownedList)) ImGui.TextDisabled($"  {_unownedList}");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Unowned row to attach##tc_unowned", ref _unownedTry);
        if (_unownedTry < 1) _unownedTry = 1;

        bool? owns = IsOrnamentOwned((uint)_unownedTry);
        ImGui.SameLine();
        if (owns == false)     ImGui.TextColored(Good, $"  NOT owned — {OrnamentLabel((uint)_unownedTry)}");
        else if (owns == true) ImGui.TextColored(Warn, "  you OWN this one — proves nothing, pick another");

        bool provable = owns == false && OrnamentRowOf((uint)_unownedTry) != null;
        if (!provable) ImGui.BeginDisabled();
        if (ImGui.Button("Attach UNOWNED accessory", new Vector2(240, 0)))
            _nativeStatus = SetOrnamentNative(chara, (short)_unownedTry);
        if (!provable) ImGui.EndDisabled();
    }

    private int    _unownedTry = 1;
    private string _unownedList = string.Empty;

    /// <summary>Scans the Ornament sheet for rows the player does not own. Pure reads.</summary>
    private static string FindUnowned()
    {
        var found = new List<string>();
        try
        {
            _ornSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Ornament>();
            if (_ornSheet == null) return "sheet unavailable";

            foreach (var row in _ornSheet)
            {
                if (found.Count >= 6) break;
                if (row.RowId == 0 || row.Model == 0) continue;
                if (IsOrnamentOwned(row.RowId) != false) continue;

                string n = row.Singular.ExtractText();
                found.Add($"{row.RowId} ({(string.IsNullOrEmpty(n) ? "?" : n)})");
            }
        }
        catch (Exception ex) { return $"scan failed: {ex.Message}"; }

        return found.Count == 0 ? "you appear to own every accessory" : "not owned: " + string.Join(", ", found);
    }

    // ── the full sequence, driven from Tick ──────────────────────────────────

    private enum ShovelStage { Off, WaitAttach, Playing, WatchAnim }

    private ShovelStage _shovelStage;
    private int         _shovelFrames;

    private const int AttachGrace = 300;   // frames to wait for the model to appear
    private const int PlayGrace2  = 60;    // frames to wait for the timeline to take

    /// <summary>Attach the Shovel, dig, and pull it the moment the game cancels the animation.</summary>
    public string StartNativeShovel()
    {
        var chara = LocalChara();
        if (chara == null) return "not logged in.";
        if (!TimelineIsSafe(DigTimelineId)) return "dig timeline unavailable.";

        string r = SetOrnamentNative(chara, DefaultOrnament);
        if (r.StartsWith("refused", StringComparison.Ordinal) || r.StartsWith("failed", StringComparison.Ordinal))
            return r;

        _shovelStage  = ShovelStage.WaitAttach;
        _shovelFrames = 0;
        return "attaching, then digging.";
    }

    public string StopNativeShovel()
    {
        _shovelStage = ShovelStage.Off;

        var chara = LocalChara();
        if (chara == null) return "not logged in.";

        try
        {
            chara->Timeline.PlayActionTimeline(IdleTimeline, 0);
            chara->Timeline.TimelineSequencer.SetSlotTimeline(0, IdleTimeline);
        }
        catch (Exception ex) { Diag.Error($"[AnimProbe] stop threw: {ex.Message}"); }

        return "stopped. " + SetOrnamentNative(chara, OrnamentNone);
    }

    private void TickNativeShovel(Character* chara)
    {
        if (_shovelStage == ShovelStage.Off) return;
        if (chara == null) { _shovelStage = ShovelStage.Off; return; }

        _shovelFrames++;
        ushort slot0 = chara->Timeline.TimelineSequencer.TimelineIds[0];

        switch (_shovelStage)
        {
            case ShovelStage.WaitAttach:
                if (chara->OrnamentData.OrnamentId == DefaultOrnament || _shovelFrames >= AttachGrace)
                {
                    try { chara->Timeline.PlayActionTimeline(DigTimelineId, 0); StartTrace(); }
                    catch (Exception ex) { Diag.Error($"[AnimProbe] dig threw: {ex.Message}"); }
                    _shovelStage  = ShovelStage.Playing;
                    _shovelFrames = 0;
                }
                break;

            case ShovelStage.Playing:
                if (slot0 == DigTimelineId) { _shovelStage = ShovelStage.WatchAnim; _shovelFrames = 0; }
                else if (_shovelFrames >= PlayGrace2) { _nativeStatus = StopNativeShovel(); }
                break;

            case ShovelStage.WatchAnim:
                // The game owns the cancel; we only notice it and put the accessory away.
                if (slot0 != DigTimelineId) _nativeStatus = StopNativeShovel();
                break;
        }
    }

    // ── Penumbra file swap: make a held weapon render as the shovel ──────────

    /// <summary>The measured path, read off the accessory's own CharacterBase — not a convention.</summary>
    private const string ShovelModelPath = "chara/monster/m6017/obj/body/b0002/model/m6017b0002.mdl";

    private const string SwapTag      = "TieriChallenges-ShovelSwap";
    private const int    SwapPriority = 999;

    private string _swapStatus = string.Empty;
    private bool   _swapActive;

    /// <summary>Ornament row 1 = Parasol — the cheapest accessory most players already own.</summary>
    private int _swapSourceOrn = 1;

    /// <summary>
    /// Redirects the player's equipped main-hand weapon model to the Shovel's model file, then
    /// redraws.
    ///
    /// <para>No ownership is involved and no game memory is written — Penumbra redirects a game
    /// path to another file, which is what every model mod does. Fully reversible by removing the
    /// temporary mod and redrawing again.</para>
    ///
    /// <para>I predicted this would fail on a "skeleton mismatch" because the asset lives under
    /// <c>chara/monster/</c>. That was inference from a folder name, not a finding — and the
    /// counter-evidence is strong: the shovel is authored to sit in a hand, which is the same thing
    /// a weapon model is. Testing costs nothing and cannot crash, so it gets tested rather than
    /// predicted.</para>
    /// </summary>
    private void DrawShovelSwap(Character* chara)
    {
        ImGui.TextColored(Accent, "Shovel swap (Penumbra file redirect — no ownership, no memory writes)");
        ImGui.TextDisabled("  Makes the equipped main-hand weapon render as the Shovel model.");

        if (chara == null) { ImGui.TextDisabled("  not logged in"); return; }

        ImGui.TextDisabled("  The Shovel is a Fashion Accessory, so redirect it onto ANOTHER accessory —");
        ImGui.TextDisabled("  same system, same attach point. The weapon slot rejected it for that reason.");

        ImGui.SetNextItemWidth(160);
        ImGui.InputInt("Source accessory (one you OWN)##tc_swap_src", ref _swapSourceOrn);
        if (_swapSourceOrn < 1)     _swapSourceOrn = 1;
        if (_swapSourceOrn > 10000) _swapSourceOrn = 10000;

        ImGui.SameLine();
        ImGui.TextDisabled($"  {OrnamentLabel((uint)_swapSourceOrn)}");

        bool? owned = IsOrnamentOwned((uint)_swapSourceOrn);
        if (owned == true)       ImGui.TextColored(Good, "  OWNED — you can summon this one.");
        else if (owned == false) ImGui.TextColored(Warn, "  NOT owned — pick an accessory you actually have.");

        string srcPath = OrnamentModelPath((uint)_swapSourceOrn, out string srcDetail);
        ImGui.TextUnformatted($"  source model: {(string.IsNullOrEmpty(srcPath) ? "(" + srcDetail + ")" : srcPath)}");
        ImGui.TextDisabled($"  shovel model: {ShovelModelPath}");

        bool ready = !string.IsNullOrEmpty(srcPath) && owned == true;
        if (!ready) ImGui.BeginDisabled();

        if (ImGui.Button("Swap accessory -> Shovel", new Vector2(240, 0)))
            _swapStatus = ApplySwap(srcPath);

        if (!ready) ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Remove swap", new Vector2(160, 0)))
            _swapStatus = RemoveSwap();

        string weaponPath = srcPath;

        if (_swapActive) ImGui.TextColored(Good, "  swap ACTIVE");
        if (!string.IsNullOrEmpty(_swapStatus)) ImGui.TextDisabled($"  {_swapStatus}");

        // Self-diagnosing. "Penumbra accepted the redirect" and "the redirect is actually reaching
        // this character" are different claims, and only the second one matters. Asking Penumbra to
        // resolve the path settles it here rather than requiring an out-of-game query — and it
        // separates "not applying" (a fixable plumbing problem) from "applying but not rendering"
        // (a model-format problem). Those need completely different fixes.
        if (!string.IsNullOrEmpty(weaponPath))
        {
            string resolved = ResolvePlayerPath(weaponPath);
            if (string.IsNullOrEmpty(resolved))
            {
                ImGui.TextDisabled("  resolve: unavailable");
            }
            else if (resolved.Equals(weaponPath, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.TextColored(Warn, "  resolve: NOT redirected — Penumbra is not applying it here.");
            }
            else
            {
                ImGui.TextColored(Good, $"  resolve: REDIRECTED -> {resolved}");
                ImGui.TextDisabled("  If it resolves but nothing renders, the model is the problem, not the plumbing.");
            }
        }
    }

    private static string ResolvePlayerPath(string gamePath)
    {
        try
        {
            return Plugin.PluginInterface
                .GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath")
                .InvokeFunc(gamePath) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Resolves an <c>Ornament</c> row to the model file the game will actually load.
    ///
    /// <para><c>Ornament.Model</c> is NOT a model id — it is a <c>ModelChara</c> row id. That row
    /// carries the real <c>Model</c>, <c>Type</c> and <c>Base</c>. Missing this indirection is why
    /// six guessed paths failed: the Shovel's <c>Ornament.Model</c> is 4936, but the model is 6017
    /// and the body is b0002, both of which live on <c>ModelChara</c> row 4936.</para>
    ///
    /// <para>Checked, not assumed: this formula reproduces
    /// <c>chara/monster/m6017/obj/body/b0002/model/m6017b0002.mdl</c> — the path measured off the
    /// live accessory — exactly.</para>
    /// </summary>
    private static string OrnamentModelPath(uint ornamentRowId, out string detail)
    {
        detail = string.Empty;
        try
        {
            var orn = OrnamentRowOf(ornamentRowId);
            if (orn == null) { detail = "no such Ornament row"; return string.Empty; }

            uint modelCharaId = orn.Value.Model;
            var mc = Plugin.DataManager.GetExcelSheet<LSheets.ModelChara>()?.GetRowOrDefault(modelCharaId);
            if (mc == null) { detail = $"no ModelChara row {modelCharaId}"; return string.Empty; }

            // Type 3 is the monster-model family every fashion accessory measured so far uses.
            if (mc.Value.Type != 3)
            {
                detail = $"ModelChara {modelCharaId} is Type {mc.Value.Type}, not the expected 3";
                return string.Empty;
            }

            int model = mc.Value.Model;
            int b     = mc.Value.Base;
            string p  = $"chara/monster/m{model:D4}/obj/body/b{b:D4}/model/m{model:D4}b{b:D4}.mdl";

            try { if (!Plugin.DataManager.FileExists(p)) { detail = "derived path not in sqpack"; return string.Empty; } }
            catch { }

            return p;
        }
        catch (Exception ex) { detail = ex.Message; return string.Empty; }
    }

    /// <summary>
    /// Builds the game path for the equipped main hand, verifying it against sqpack before use.
    /// Weapon paths key the body folder off either Type or Variant depending on the item; rather
    /// than pick one, both are tried and the one that actually EXISTS wins.
    /// </summary>
    private static string CurrentMainHandPath(Character* chara, out string detail)
    {
        detail = string.Empty;
        try
        {
            var span = chara->DrawData.WeaponData;
            if (span.Length == 0) { detail = "(no weapon data)"; return string.Empty; }

            var m = span[0].ModelId;
            if (m.Id == 0) { detail = "(no weapon equipped — equip one first)"; return string.Empty; }

            foreach (int b in new[] { m.Type, m.Variant, 1 })
            {
                string p = $"chara/weapon/w{m.Id:D4}/obj/body/b{b:D4}/model/w{m.Id:D4}b{b:D4}.mdl";
                try { if (Plugin.DataManager.FileExists(p)) return p; } catch { }
            }

            detail = $"(no path found for weapon Id {m.Id} Type {m.Type} Variant {m.Variant})";
            return string.Empty;
        }
        catch (Exception ex) { detail = $"({ex.Message})"; return string.Empty; }
    }

    private string ApplySwap(string weaponPath)
    {
        try
        {
            var add = Plugin.PluginInterface
                .GetIpcSubscriber<string, Dictionary<string, string>, string, int, int>(
                    "Penumbra.AddTemporaryModAll.V5");

            var paths = new Dictionary<string, string>
            {
                [weaponPath] = ShovelModelPath,
            };

            int ec = add.InvokeFunc(SwapTag, paths, string.Empty, SwapPriority);
            Diag.Info($"[AnimProbe] AddTemporaryModAll -> {ec}  {weaponPath} => {ShovelModelPath}");

            Redraw();
            _swapActive = ec == 0;
            return ec == 0 ? "swap applied, redrawing." : $"Penumbra returned {ec}.";
        }
        catch (Exception ex)
        {
            Diag.Error($"[AnimProbe] swap failed: {ex.Message}");
            return $"failed: {ex.Message}";
        }
    }

    private string RemoveSwap()
    {
        try
        {
            var remove = Plugin.PluginInterface
                .GetIpcSubscriber<string, int, int>("Penumbra.RemoveTemporaryModAll.V5");

            int ec = remove.InvokeFunc(SwapTag, SwapPriority);
            Diag.Info($"[AnimProbe] RemoveTemporaryModAll -> {ec}");

            Redraw();
            _swapActive = false;
            return ec == 0 ? "swap removed, redrawing." : $"Penumbra returned {ec}.";
        }
        catch (Exception ex)
        {
            Diag.Error($"[AnimProbe] remove swap failed: {ex.Message}");
            return $"failed: {ex.Message}";
        }
    }

    private static void Redraw()
    {
        try
        {
            // 0 = local player object index; 0 = RedrawType.Redraw.
            Plugin.PluginInterface
                .GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5")
                .InvokeAction(0, 0);
        }
        catch (Exception ex) { Diag.Warn($"[AnimProbe] redraw failed: {ex.Message}"); }
    }

    // ── does the model actually exist as a weapon-format asset? ──────────────

    private int  _pathModelId = 4936;   // Ornament 57 (Shovel) Model
    private bool _pathChecked;
    private readonly List<string> _pathResults = new();

    /// <summary>
    /// Asks sqpack whether the accessory's model exists under the ordinary weapon path convention.
    ///
    /// <para>The previous conclusion — "the accessory carries no weapon triple, therefore the weapon
    /// route is dead" — conflated two different claims. It rules out COPYING a triple off the
    /// ornament actor. It says nothing about whether the model is a weapon-format asset that could
    /// be loaded as one, or redirected onto one with Penumbra. That is a file-existence question,
    /// and <c>IDataManager.FileExists</c> answers it as a pure read.</para>
    /// </summary>
    private void DrawModelPathCheck()
    {
        ImGui.TextColored(Accent, "Model path check (read-only — does the asset exist?)");

        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Model id##tc_anim_model", ref _pathModelId)) _pathChecked = false;
        if (_pathModelId < 0)     _pathModelId = 0;
        if (_pathModelId > 65535) _pathModelId = 65535;

        ImGui.SameLine();
        if (ImGui.Button("Check paths##tc_anim_paths", new Vector2(140, 0))) { _pathChecked = false; }

        if (!_pathChecked)
        {
            _pathChecked = true;
            _pathResults.Clear();

            foreach (string p in CandidateModelPaths(_pathModelId))
            {
                bool exists;
                try { exists = Plugin.DataManager.FileExists(p); }
                catch (Exception ex) { _pathResults.Add($"  ERR  {p}  ({ex.Message})"); continue; }

                string line = (exists ? "  YES  " : "  no   ") + p;
                _pathResults.Add(line);
                if (exists) Diag.Info($"[AnimProbe] path EXISTS {p}");
            }
            Diag.Info($"[AnimProbe] path check for model {_pathModelId} complete");
        }

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0.25f));
        ImGui.BeginChild("##tc_anim_paths_out", new Vector2(0, 130), true);
        foreach (var r in _pathResults)
        {
            if (r.StartsWith("  YES", StringComparison.Ordinal)) ImGui.TextColored(Good, r);
            else ImGui.TextDisabled(r);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// The conventional shapes a rigid held model can take. Weapon variants first, since that is the
    /// hypothesis under test; the ornament-specific shapes are included so a negative on the weapon
    /// paths still tells us where the asset actually lives rather than just "not there".
    /// </summary>
    private static IEnumerable<string> CandidateModelPaths(int id)
    {
        string w = $"w{id:D4}";
        for (int b = 1; b <= 3; b++)
            yield return $"chara/weapon/{w}/obj/body/b{b:D4}/model/{w}b{b:D4}.mdl";

        string m = $"m{id:D4}";
        yield return $"chara/monster/{m}/obj/body/b0001/model/{m}b0001.mdl";

        string n = $"n{id:D4}";
        yield return $"chara/ornament/{n}/obj/body/b0001/model/{n}b0001.mdl";
        yield return $"chara/ornament/{w}/obj/body/b0001/model/{w}b0001.mdl";
    }

    /// <summary>Read-only ownership check. Null when PlayerState is not available yet.</summary>
    private static bool? IsOrnamentOwned(uint ornamentId)
    {
        try
        {
            var ps = PlayerState.Instance();
            if (ps == null) return null;
            return ps->IsOrnamentUnlocked(ornamentId);
        }
        catch { return null; }
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
        {
            _trace[^1] = (now, _trace[^1].Frames + 1);
        }
        else
        {
            _trace.Add((now, 1));
            // Logged, not just displayed. The on-screen panel answers "did the write take?" but it
            // dies with the next hot-reload, so the one question the probe exists to answer was
            // unreadable from outside the game and had to be asked. Diag survives.
            Diag.Info($"[AnimProbe] trace slot0 -> {now}  {KeyOf(now)}");
        }

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

    // ── one-shot playback, for the chat command ──────────────────────────────

    /// <summary>
    /// Plays one ActionTimeline row on the local player and returns a human-readable result. This
    /// is the whole of what Trist actually wanted out of this investigation — the dig animation, on
    /// demand, with no accessory owned — so it lives behind a command rather than only inside a
    /// probe window that has to be opened and typed into.
    ///
    /// <para>Gated by the same precondition as the window (<see cref="TimelineIsSafe"/>): the row
    /// must exist and carry a non-empty <c>Key</c>. The `catch` is for the managed side only; the
    /// precondition is what actually keeps this call safe. See the class doc.</para>
    /// </summary>
    public static string PlayTimelineNow(ushort id)
    {
        var chara = LocalChara();
        if (chara == null) return "not logged in.";

        if (!TimelineIsSafe(id))
            return $"ActionTimeline {id} is not playable (no such row, or it has no animation).";

        try
        {
            ushort before = chara->Timeline.TimelineSequencer.TimelineIds[0];
            chara->Timeline.PlayActionTimeline(id, 0);
            Diag.Info($"[AnimProbe] command play {id} — slot0 was {before}");
            return $"playing {id} ({KeyOf(id)}).";
        }
        catch (Exception ex)
        {
            Diag.Error($"[AnimProbe] command play {id} failed: {ex.Message}");
            return $"failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns the player to <c>normal/idle</c> (row 3, read from the sheet) and dismisses the
    /// accessory if the dig sequence summoned one.
    ///
    /// <para>13383 has <c>IsLoop: true</c>, and the first version of this only called
    /// <c>PlayActionTimeline(3, 0)</c>, which did not visibly stop the dig. Both writes are used
    /// now — <c>PlayActionTimeline</c> drives the container, <c>SetSlotTimeline</c> pokes slot 0
    /// directly — because they are not the same operation and both are already proven safe from the
    /// probe's fire buttons. A trace is started so the log shows which one took, instead of the
    /// question having to be asked again.</para>
    /// </summary>
    public string StopDigNow()
    {
        _digStage = DigStage.Off;

        var chara = LocalChara();
        if (chara == null) return "not logged in.";

        StartTrace();

        try
        {
            chara->Timeline.PlayActionTimeline(IdleTimeline, 0);
            chara->Timeline.TimelineSequencer.SetSlotTimeline(0, IdleTimeline);
            Diag.Info("[AnimProbe] command stop -> PlayActionTimeline(3) + SetSlotTimeline(0,3)");
        }
        catch (Exception ex)
        {
            Diag.Error($"[AnimProbe] command stop failed: {ex.Message}");
            return $"failed: {ex.Message}";
        }

        // Only put the accessory away if this plugin is what brought it out. Dismissing one the
        // player summoned themselves would be undoing something we did not do.
        if (_digSummoned && chara->OrnamentData.OrnamentId == DefaultOrnament)
        {
            _digSummoned = false;
            string orn = ToggleOrnament((uint)DefaultOrnament);
            return $"stopped. {orn}";
        }

        _digSummoned = false;
        return "stopped — back to idle.";
    }

    // ── dig: summon the accessory the game's way, then play the animation ────

    private enum DigStage { Off, WaitingForOrnament }

    private DigStage _digStage;
    private int      _digFrames;
    private bool     _digSummoned;

    /// <summary>Frames to wait for the game to attach the accessory before playing regardless.</summary>
    private const int DigSummonGrace = 300;

    /// <summary>
    /// Plays the dig, bringing the Shovel out first when the player owns it.
    ///
    /// <para>The accessory is summoned through <c>ActionManager.UseAction(ActionType.Ornament, …)</c>
    /// — the game's own action, the same one the Fashion Accessory menu fires. It is
    /// server-validated, requires ownership, and toggles off with the identical call, so there is no
    /// invented teardown anywhere in this path. That is the whole difference between this and the
    /// removed <c>SetupOrnament</c> attempt (BROKEN.md 012).</para>
    ///
    /// <para>Not owning the Shovel is not an error: the animation itself needs no ownership at all,
    /// so it plays regardless and the player simply mimes it.</para>
    /// </summary>
    public string StartDigNow()
    {
        var chara = LocalChara();
        if (chara == null) return "not logged in.";

        if (!TimelineIsSafe(DigTimelineId))
            return $"ActionTimeline {DigTimelineId} is not playable.";

        // Already holding it — nothing to summon.
        if (chara->OrnamentData.OrnamentId == DefaultOrnament)
            return PlayDig(chara, "already holding the Shovel");

        bool? owned = IsOrnamentOwned(DefaultOrnament);
        if (owned != true)
            return PlayDig(chara, "Shovel not owned — animation only, empty-handed");

        string summon = ToggleOrnament((uint)DefaultOrnament);
        if (summon.StartsWith("could not", StringComparison.Ordinal))
            return PlayDig(chara, summon + "; animation only");

        _digSummoned = true;
        _digStage    = DigStage.WaitingForOrnament;
        _digFrames   = 0;
        return $"{summon} — animation follows once it is in hand.";
    }

    /// <summary>Waits for the game to finish attaching, then fires. Never writes the container.</summary>
    private void TickDig(Character* chara)
    {
        if (_digStage != DigStage.WaitingForOrnament) return;

        if (chara == null) { _digStage = DigStage.Off; return; }

        _digFrames++;

        if (chara->OrnamentData.OrnamentId == DefaultOrnament)
        {
            _digStage = DigStage.Off;
            PlayDig(chara, "accessory in hand");
            return;
        }

        if (_digFrames >= DigSummonGrace)
        {
            _digStage = DigStage.Off;
            Diag.Info("[AnimProbe] dig: accessory never arrived, playing anyway");
            PlayDig(chara, "accessory did not arrive");
        }
    }

    private string PlayDig(Character* chara, string why)
    {
        try
        {
            ushort before = chara->Timeline.TimelineSequencer.TimelineIds[0];
            chara->Timeline.PlayActionTimeline(DigTimelineId, 0);
            StartTrace();
            Diag.Info($"[AnimProbe] dig play {DigTimelineId} ({why}) — slot0 was {before}");
            return $"digging ({why}).";
        }
        catch (Exception ex)
        {
            Diag.Error($"[AnimProbe] dig play failed: {ex.Message}");
            return $"failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Fires the game's own ornament action, which both summons and dismisses.
    ///
    /// <para>Which id <c>ActionType.Ornament</c> wants — the <c>Ornament</c> row or the
    /// <c>Ornament.Action</c> row it points at — is not documented anywhere I can read, so it is
    /// <b>measured rather than assumed</b>: <c>GetActionStatus</c> is a read, and both candidates
    /// are queried and logged before either is used. Status 0 means usable. If neither reads usable
    /// nothing is fired. This is the shape the ornament work should have had from the start.</para>
    /// </summary>
    private static string ToggleOrnament(uint ornamentRowId)
    {
        try
        {
            var am = ActionManager.Instance();
            if (am == null) return "could not reach ActionManager";

            uint viaRow = am->GetActionStatus(ActionType.Ornament, ornamentRowId);

            uint actionRowId = 0;
            var  row = OrnamentRowOf(ornamentRowId);
            if (row != null) actionRowId = row.Value.Action.RowId;

            uint viaAction = actionRowId != 0
                ? am->GetActionStatus(ActionType.Ornament, actionRowId)
                : uint.MaxValue;

            Diag.Info($"[AnimProbe] GetActionStatus(Ornament, row {ornamentRowId}) = {viaRow}; "
                    + $"(Ornament, action {actionRowId}) = {viaAction}");

            uint use = viaRow == 0 ? ornamentRowId
                     : viaAction == 0 ? actionRowId
                     : 0;

            if (use == 0)
                return $"could not use the accessory action (status {viaRow}/{viaAction})";

            bool ok = am->UseAction(ActionType.Ornament, use);
            Diag.Info($"[AnimProbe] UseAction(Ornament, {use}) -> {ok}");
            return ok ? $"accessory action fired (id {use})" : $"could not fire accessory action (id {use})";
        }
        catch (Exception ex)
        {
            Diag.Error($"[AnimProbe] ToggleOrnament failed: {ex.Message}");
            return "could not fire accessory action";
        }
    }

    /// <summary>The Shovel's dig. Shared by the window's default and the chat command.</summary>
    private const ushort DigTimelineId = (ushort)DefaultTimeline;

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
    private static readonly Vector4 Good   = new(0.45f, 0.90f, 0.50f, 1f);

    private static LSheets.Ornament? OrnamentRowOf(uint rowId)
    {
        if (rowId == 0) return null;
        try
        {
            _ornSheet ??= Plugin.DataManager.GetExcelSheet<LSheets.Ornament>();
            return _ornSheet?.GetRowOrDefault(rowId);
        }
        catch { return null; }
    }

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
