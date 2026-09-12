using System;
using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// The authored activity maps, shipped beside the DLL and read at startup.
///
/// <para><b>This file exists because the authoring used to be LOCAL STATE, and that shipped broken.</b>
/// "Which maps is Wild Trail authored for" was answered from <see cref="DigTuning"/>'s hand-drawn box
/// — a value in the author's own <c>dig-tuning.json</c>. On the author's machine there was one map and
/// everything worked; on every other install <c>BoxSet</c> was false, so the tab said "no map is ready
/// for this yet" and no amount of travelling to the right zone could change it, because the check
/// never looked at where the player was standing.</para>
///
/// <para>That is the shipped-artifact question the design pre-flight exists to ask, and it was not
/// asked: <b>a per-user config file is not content.</b> Authored bounds and exclusions are content —
/// somebody walked the zone and drew them — so they ship like the help document and the artwork do.</para>
///
/// <para><b>The null zones matter as much as the box.</b> A box is a rectangle and a housing ward is
/// not, so the 45 hand-drawn exclusions are what keep a clue out of the sea, out of the walls and off
/// the roofs. Shipping the box alone would have produced a map that placed trails and placed a lot of
/// them somewhere unreachable — a worse failure than refusing, because it looks like it worked.</para>
/// </summary>
internal static class ActivityMaps
{
    private const string FileName = "activity-maps.json";

    /// <summary>An authored rectangle in WORLD coordinates — (x, z), +X east and +Z south.</summary>
    internal sealed class Box
    {
        public float MinX { get; set; }
        public float MinZ { get; set; }
        public float MaxX { get; set; }
        public float MaxZ { get; set; }

        /// <summary>A box with no area cannot bound anything and is treated as absent.</summary>
        public bool IsUsable => MaxX - MinX > 5f && MaxZ - MinZ > 5f;

        public bool Contains(float x, float z) =>
            x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    }

    internal sealed class AuthoredMap
    {
        public string    ActivityId { get; set; } = string.Empty;
        public uint      Territory  { get; set; }
        public string    Name       { get; set; } = string.Empty;
        public Box?      Box        { get; set; }
        public List<Box> NullZones  { get; set; } = new();
    }

    private sealed class Document
    {
        public List<AuthoredMap> Maps { get; set; } = new();
    }

    private static List<AuthoredMap> _maps = new();

    /// <summary>What went wrong loading the file, or an empty string. Surfaced in the lab.</summary>
    public static string Status { get; private set; } = "not loaded yet.";

    public static IReadOnlyList<AuthoredMap> All => _maps;

    /// <summary>
    /// Reads the shipped file. <b>A failure is non-fatal and leaves the list empty</b>, which the
    /// Activity pane already reports as "no map is ready" — the same outcome as a genuinely
    /// unauthored install, and never a crash on load.
    /// </summary>
    public static void Load()
    {
        _maps = new List<AuthoredMap>();

        try
        {
            string dir  = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty;
            string path = Path.Combine(dir, FileName);

            if (!File.Exists(path))
            {
                Status = $"{FileName} is missing from the plugin folder — no maps are authored.";
                Diag.Error("[Activity] " + Status);
                return;
            }

            var doc = JsonConvert.DeserializeObject<Document>(File.ReadAllText(path));

            if (doc?.Maps == null)
            {
                Status = $"{FileName} could not be parsed.";
                Diag.Error("[Activity] " + Status);
                return;
            }

            // A map with no usable box is dropped rather than offered. Offering it would put a
            // playable-looking entry in the list whose trails fall back to the coordinate range —
            // which is mostly empty square, and is exactly the unreachable placement this data is
            // here to prevent.
            foreach (var m in doc.Maps)
            {
                if (m.Territory == 0 || m.Box is not { } b || !b.IsUsable) continue;
                m.NullZones ??= new List<Box>();
                _maps.Add(m);
            }

            int zones = 0;
            foreach (var m in _maps) zones += m.NullZones.Count;

            Status = $"{_maps.Count} authored map(s), {zones} null zone(s).";
            Plugin.Log.Information($"[Activity] {Status}");
        }
        catch (Exception ex)
        {
            Status = $"{FileName} failed to load: {ex.Message}";
            Diag.Error("[Activity] " + Status);
        }
    }

    /// <summary>The authored map for a territory, or null.</summary>
    public static AuthoredMap? For(uint territory)
    {
        foreach (var m in _maps)
            if (m.Territory == territory) return m;

        return null;
    }

    /// <summary>Whether a world position falls inside an authored exclusion for this territory.</summary>
    public static bool InNullZone(uint territory, float x, float z)
    {
        if (For(territory) is not { } map) return false;

        foreach (var n in map.NullZones)
            if (n.Contains(x, z)) return true;

        return false;
    }
}
