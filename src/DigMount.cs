#if DEV_BUILD
using System;
using System.Collections.Generic;

using Dalamud.Game.ClientState.Conditions;

using FFXIVClientStructs.FFXIV.Client.Game;

using LSheets = Lumina.Excel.Sheets;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. Gets the player off their mount so a dig can happen, instead of refusing the
/// dig because they are on one.
///
/// <para><b>Why this is dev-only while <see cref="PropService"/> ships.</b> PropService is in the
/// public artifact because challenge content calls it. This is not: it issues a real action to the
/// server, and nothing public asks for that yet. Adding a server action to the shipped DLL for the
/// benefit of a test lab would be shipping capability nobody has asked for — the §4A question about
/// what is different in the shipped artifact, answered before writing rather than after.</para>
///
/// <para><b>The action id is LOOKED UP, never written down.</b> A general-action id is exactly the
/// class of value <c>BROKEN.md</c> 012 exists about: <c>SetupOrnament(-1)</c> was reasoned from a
/// parameter's type, arrived as <c>0xFFFF</c>, and crashed the game twice. So this finds the row by
/// its name in the <c>GeneralAction</c> sheet and uses that row's id. If no such row exists, it
/// <b>refuses and dumps every row in the sheet to the log</b> — one reload then answers the question
/// permanently, which is strictly better than a literal that looks right and is not.</para>
/// </summary>
internal static unsafe class DigMount
{
    /// <summary>Whether the player is on a mount right now — the state that blocks a dig.</summary>
    public static bool Mounted
    {
        get
        {
            try
            {
                var c = Plugin.Condition;
                return c[ConditionFlag.Mounted] || c[ConditionFlag.RidingPillion];
            }
            catch { return false; }
        }
    }

    private static uint _actionId;
    private static bool _resolved;

    /// <summary>
    /// The <c>GeneralAction</c> row id for Dismount, or 0 when the sheet has no such row.
    ///
    /// <para>Resolved once and cached. The name match is against the client's own language, which is
    /// English here; a non-English client would find nothing and get the refusal path, which is the
    /// correct outcome rather than a wrong id.</para>
    /// </summary>
    public static uint DismountActionId
    {
        get
        {
            if (!_resolved) Resolve();
            return _actionId;
        }
    }

    private static void Resolve()
    {
        _resolved = true;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<LSheets.GeneralAction>();
            if (sheet == null) { Diag.Error("[Dig] GeneralAction sheet unavailable."); return; }

            var names = new List<string>();

            foreach (var row in sheet)
            {
                string n = row.Name.ExtractText();
                if (n.Length == 0) continue;

                names.Add($"{row.RowId}={n}");

                if (string.Equals(n, "Dismount", StringComparison.OrdinalIgnoreCase))
                    _actionId = row.RowId;
            }

            if (_actionId != 0)
            {
                Diag.Info($"[Dig] Dismount resolved to GeneralAction row {_actionId}.");
                return;
            }

            // THE WHOLE POINT OF THIS BRANCH. Not finding the row is not a dead end, it is a
            // question — and the sheet is small enough to answer it in full right here rather than
            // leaving the next session to guess again.
            Diag.Warn("[Dig] no GeneralAction row is named \"Dismount\". "
                    + "The sheet contains: " + string.Join(", ", names));
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] dismount lookup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks the game to dismount. Returns the line to print; never throws.
    ///
    /// <para><b>This is the same request the player's own dismount button makes</b> — a normal action
    /// issued in response to a click they performed. Nothing is forged and no packet is built.</para>
    /// </summary>
    public static string RequestDismount()
    {
        if (!Mounted) return "not mounted.";

        uint id = DismountActionId;

        if (id == 0)
            return "cannot dismount for you — this client's GeneralAction sheet has no \"Dismount\" "
                 + "row (the full sheet is in the log). Dismount yourself and dig again.";

        try
        {
            var am = ActionManager.Instance();
            if (am == null) return "cannot dismount right now.";

            bool ok = am->UseAction(ActionType.GeneralAction, id);
            Diag.Info($"[Dig] UseAction(GeneralAction, {id}) -> {ok}");

            return ok ? "dismounting…" : "the game refused the dismount just now.";
        }
        catch (Exception ex)
        {
            Diag.Error($"[Dig] dismount failed: {ex.Message}");
            return $"dismount failed: {ex.Message}";
        }
    }
}
#endif
