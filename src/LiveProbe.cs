#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// <b>Developer-only investigation harness.</b> Compiled out of the public build entirely.
///
/// <para>This exists to answer questions that static analysis cannot: what the game actually does
/// at runtime. It is NOT a feature and nothing in the plugin depends on it. Delete it once the
/// questions in <c>research/OPEN_QUESTIONS.md</c> Q13/Q17 are answered and recorded.</para>
///
/// <para><b>Everything here is reflection-based on purpose.</b> A probe that hard-codes the member
/// names it expects can only confirm what was already believed, and fails to compile the moment a
/// guess is wrong. Reflecting over whatever the runtime actually hands us means the probe reports
/// the real schema — including fields nobody thought to ask about. That is the entire point.</para>
///
/// <para><b>The question it is built to answer (Q13).</b> <c>IGameInventory.ItemAdded</c> reports
/// that an item arrived but not HOW it arrived. Gathered, crafted, bought, traded and withdrawn
/// from a retainer are indistinguishable from the event alone — which would mean "gather 20 copper
/// ore" is satisfiable by buying it from a vendor. The hypothesis under test is that sampling
/// <see cref="ICondition"/> at the instant of the event supplies the missing provenance. To test it
/// we record EVERY true condition flag alongside every inventory event and then compare the sets
/// across a gather, a craft and a purchase.</para>
/// </summary>
internal static class LiveProbe
{
    /// <summary>Events retained in memory. Bounded — a probe that leaks is a bad probe.</summary>
    private const int Capacity = 500;

    private static readonly object Gate = new();
    private static readonly List<ProbeEvent> Events = new();

    private static bool _attached;

    /// <summary>Set by the window so a capture can be scoped to one deliberate action.</summary>
    public static string CurrentLabel { get; set; } = "unlabelled";

    /// <summary>Whether events are being recorded. Off by default — no cost until asked for.</summary>
    public static bool Recording { get; private set; }

    public static int EventCount
    {
        get { lock (Gate) return Events.Count; }
    }

    // ── Capture ──────────────────────────────────────────────────────────────

    /// <summary>One inventory event plus the full game-state context at that instant.</summary>
    internal sealed class ProbeEvent
    {
        public string Time      { get; set; } = string.Empty;
        public string Label     { get; set; } = string.Empty;
        public string EventKind { get; set; } = string.Empty;

        /// <summary>Every public property found on the event args, by reflection.</summary>
        public Dictionary<string, string> Args { get; set; } = new();

        /// <summary>Every <see cref="ConditionFlag"/> that was TRUE at this instant.</summary>
        public List<string> Conditions { get; set; } = new();

        /// <summary>Zone at the time, for context.</summary>
        public uint Territory { get; set; }
    }

    public static void Start(string label)
    {
        CurrentLabel = string.IsNullOrWhiteSpace(label) ? "unlabelled" : label;
        Recording = true;
        Attach();
        Diag.Info($"[Probe] recording started: {CurrentLabel}");
    }

    public static void Stop()
    {
        Recording = false;
        Diag.Info($"[Probe] recording stopped ({EventCount} events held)");
    }

    public static void ClearEvents()
    {
        lock (Gate) Events.Clear();
    }

    private static void Attach()
    {
        if (_attached) return;

        try
        {
            Plugin.GameInventory.ItemAdded   += OnEvent;
            Plugin.GameInventory.ItemRemoved += OnEvent;
            Plugin.GameInventory.ItemChanged += OnEvent;
            Plugin.GameInventory.ItemMoved   += OnEvent;
            Plugin.GameInventory.ItemMerged  += OnEvent;
            Plugin.GameInventory.ItemSplit   += OnEvent;
            _attached = true;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Probe] subscribe failed: {ex.Message}");
        }
    }

    public static void Detach()
    {
        if (!_attached) return;

        try
        {
            Plugin.GameInventory.ItemAdded   -= OnEvent;
            Plugin.GameInventory.ItemRemoved -= OnEvent;
            Plugin.GameInventory.ItemChanged -= OnEvent;
            Plugin.GameInventory.ItemMoved   -= OnEvent;
            Plugin.GameInventory.ItemMerged  -= OnEvent;
            Plugin.GameInventory.ItemSplit   -= OnEvent;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Probe] unsubscribe failed: {ex.Message}");
        }

        _attached = false;
    }

    /// <summary>
    /// Never throws. This runs inside Dalamud's inventory dispatch — an exception escaping here
    /// would propagate into the game, and a diagnostic tool taking the client down would be an
    /// especially stupid way to lose a session.
    /// </summary>
    private static void OnEvent(GameInventoryEvent type, InventoryEventArgs data)
    {
        if (!Recording) return;

        try
        {
            var ev = new ProbeEvent
            {
                Time       = DateTime.Now.ToString("HH:mm:ss.fff"),
                Label      = CurrentLabel,
                EventKind  = type.ToString(),
                Territory  = Plugin.ClientState.TerritoryType,
                Args       = ReflectToStrings(data),
                Conditions = TrueConditions(),
            };

            lock (Gate)
            {
                if (Events.Count >= Capacity) Events.RemoveAt(0);
                Events.Add(ev);
            }
        }
        catch (Exception ex)
        {
            Diag.Error($"[Probe] capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Every public property of an object rendered as a string, one level deep, plus a second level
    /// for anything that looks like the item payload. We do not know the arg type's shape and are
    /// deliberately not guessing it.
    /// </summary>
    private static Dictionary<string, string> ReflectToStrings(object? o, int depth = 0)
    {
        var map = new Dictionary<string, string>();
        if (o == null) return map;

        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string value;
            object? raw = null;

            try
            {
                raw = p.GetValue(o);
                value = raw?.ToString() ?? "null";
            }
            catch (Exception ex)
            {
                value = $"<threw: {ex.GetType().Name}>";
            }

            map[p.Name] = value;

            // Recurse once into the nested item payload — that is where the ids live.
            if (depth == 0 && raw != null && !(raw is string) && p.PropertyType.IsValueType
                && p.PropertyType.Namespace?.StartsWith("Dalamud", StringComparison.Ordinal) == true)
            {
                foreach (var kv in ReflectToStrings(raw, depth + 1))
                    map[$"{p.Name}.{kv.Key}"] = kv.Value;
            }
        }

        return map;
    }

    /// <summary>
    /// Every ConditionFlag currently true. Enumerated from the enum itself rather than from a
    /// hand-written list, so a flag nobody thought of still shows up in the report.
    /// </summary>
    private static List<string> TrueConditions()
    {
        var live = new List<string>();

        foreach (ConditionFlag flag in Enum.GetValues<ConditionFlag>())
        {
            try
            {
                if (Plugin.Condition[flag]) live.Add($"{flag} ({(int)flag})");
            }
            catch
            {
                // A flag the client rejects is simply not reported.
            }
        }

        return live;
    }

    // ── Sheet census (autonomous — needs no player action) ───────────────────

    /// <summary>
    /// Dumps the real schema and a row sample for the sheets the quest generator depends on.
    ///
    /// <para>Runs without any player involvement, which is why it is worth doing first: it answers
    /// Q11/Q12 (are mob drop tables present? does MonsterNote carry mob→zone→count?) from the live
    /// Lumina data rather than from expectation.</para>
    /// </summary>
    public static string RunSheetCensus()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sheet census");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        DumpSheet<Lumina.Excel.Sheets.MonsterNote>(sb, "MonsterNote", 3);
        DumpSheet<Lumina.Excel.Sheets.MonsterNoteTarget>(sb, "MonsterNoteTarget", 5);
        DumpSheet<Lumina.Excel.Sheets.Recipe>(sb, "Recipe", 2);
        DumpSheet<Lumina.Excel.Sheets.GatheringItem>(sb, "GatheringItem", 3);
        DumpSheet<Lumina.Excel.Sheets.GatheringPointBase>(sb, "GatheringPointBase", 2);

        // GilShopItem is deliberately absent. It is a SUBROW sheet (IExcelSubrow<T>, not
        // IExcelRow<T>) and will not bind to DumpSheet — discovered at compile time 2026-08-26.
        // Reaching it needs GetSubrowExcelSheet<T>(). Recorded here because the generator will
        // want vendor sourcing eventually and this is the trap waiting for it; it is not on the
        // critical path for Q11/Q12/Q13, so the probe does not carry a second code path for it.
        sb.AppendLine("## GilShopItem");
        sb.AppendLine("  SKIPPED — subrow sheet, needs GetSubrowExcelSheet<T>(). See comment in LiveProbe.cs.");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Schema + sample rows for one sheet, entirely by reflection. Column names are what we are
    /// trying to LEARN, so naming them in code would defeat the purpose.
    /// </summary>
    private static void DumpSheet<T>(StringBuilder sb, string name, int sampleRows)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        sb.AppendLine($"## {name}");

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<T>();
            if (sheet == null)
            {
                sb.AppendLine("  SHEET NOT AVAILABLE");
                sb.AppendLine();
                return;
            }

            sb.AppendLine($"  rows: {sheet.Count}");

            var props = typeof(T)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToArray();

            sb.AppendLine($"  columns ({props.Length}): {string.Join(", ", props.Select(p => $"{p.Name}:{Simple(p.PropertyType)}"))}");

            int shown = 0;
            foreach (var row in sheet)
            {
                if (shown >= sampleRows) break;
                shown++;

                sb.AppendLine($"  --- sample row {shown} ---");
                foreach (var p in props)
                {
                    string v;
                    try { v = p.GetValue(row)?.ToString() ?? "null"; }
                    catch (Exception ex) { v = $"<threw: {ex.GetType().Name}>"; }

                    if (v.Length > 160) v = v.Substring(0, 160) + "…";
                    sb.AppendLine($"    {p.Name} = {v}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine();
    }

    private static string Simple(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        return $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(Simple))}>";
    }

    // ── Report output ────────────────────────────────────────────────────────

    /// <summary>Where reports land. Under the plugin's own config directory, never the repo.</summary>
    public static string ReportDirectory
    {
        get
        {
            string dir = Path.Combine(
                Plugin.PluginInterface.GetPluginConfigDirectory(), "probe");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Writes everything captured so far plus the sheet census. Returns the path, or an error
    /// string — the caller shows it, so a failure must be visible rather than silent.
    /// </summary>
    public static string WriteReport()
    {
        try
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string dir   = ReportDirectory;

            ProbeEvent[] snapshot;
            lock (Gate) snapshot = Events.ToArray();

            string jsonPath = Path.Combine(dir, $"probe-{stamp}.json");
            File.WriteAllText(jsonPath, JsonConvert.SerializeObject(new
            {
                generated  = DateTime.Now.ToString("o"),
                plugin     = PluginVersion.Current,
                eventCount = snapshot.Length,
                events     = snapshot,
            }, Formatting.Indented));

            string censusPath = Path.Combine(dir, $"census-{stamp}.md");
            File.WriteAllText(censusPath, RunSheetCensus());

            Diag.Info($"[Probe] report written: {jsonPath}");
            return dir;
        }
        catch (Exception ex)
        {
            Diag.Error($"[Probe] report failed: {ex.Message}");
            return $"FAILED: {ex.Message}";
        }
    }

    /// <summary>
    /// Distinct condition-flag sets seen per label, which is the actual Q13 answer in one view:
    /// if "gather" and "buy" produce different sets, provenance works.
    /// </summary>
    public static string SummariseByLabel()
    {
        ProbeEvent[] snapshot;
        lock (Gate) snapshot = Events.ToArray();

        var sb = new StringBuilder();

        foreach (var group in snapshot.GroupBy(e => e.Label))
        {
            sb.AppendLine($"[{group.Key}] {group.Count()} event(s)");

            foreach (var kindGroup in group.GroupBy(e => e.EventKind))
            {
                var union = kindGroup
                    .SelectMany(e => e.Conditions)
                    .Distinct()
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToArray();

                sb.AppendLine($"   {kindGroup.Key}: {(union.Length == 0 ? "(no flags set)" : string.Join(", ", union))}");
            }

            sb.AppendLine();
        }

        return sb.Length == 0 ? "Nothing captured yet." : sb.ToString();
    }
}
#endif
