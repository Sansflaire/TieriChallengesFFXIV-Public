#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. One authored stop on a clue trail: a place someone stood, and the words
/// that should lead a player to it.
///
/// <para><b>The clue is written, never generated.</b> The first cut produced them — "NORTH-EAST, far
/// away" — and that is not a clue, it is a search: a bearing over a whole zone leaves the player
/// sweeping hundreds of yalms with no way to tell progress from luck. A clue works because a person
/// who knows the place wrote something a person who does not can act on. Nothing here can compute
/// that, so nothing here tries.</para>
/// </summary>
internal sealed class TrailStop
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>Territory this position belongs to. A trail may cross zones.</summary>
    public uint Territory { get; set; }

    /// <summary>Captured with the position, for the same reason challenge areas capture it.</summary>
    public uint MapId { get; set; }

    /// <summary>The clue leading TO this stop. Shown while the player is looking for it.</summary>
    public string Clue { get; set; } = string.Empty;

    /// <summary>Author's own label. Never shown to a player — it is for the list in the lab.</summary>
    public string Name { get; set; } = string.Empty;

    public Vector3 Position => new(X, Y, Z);

    public void SetPosition(Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }

    public TrailStop Clone() => new()
    {
        X = X, Y = Y, Z = Z, Territory = Territory, MapId = MapId, Clue = Clue, Name = Name,
    };
}

/// <summary>
/// DEVELOPER BUILD ONLY. The authored trail, persisted to its own file.
///
/// <para>Separate from <c>dig-tuning.json</c> on purpose: that file holds knobs, this holds
/// <i>content</i>. Wiping tuning back to defaults must not throw away a trail somebody spent twenty
/// minutes walking out, and re-authoring a trail must not disturb tuning.</para>
/// </summary>
internal static class DigTrailStore
{
    public static readonly List<TrailStop> Stops = new();

    private static string Path =>
        System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "dig-trail.json");

    public static void Load()
    {
        try
        {
            Stops.Clear();
            if (!File.Exists(Path)) return;

            var loaded = JsonSerializer.Deserialize<List<TrailStop>>(File.ReadAllText(Path));
            if (loaded == null) return;

            foreach (var s in loaded)
            {
                if (s == null) continue;
                s.Clue ??= string.Empty;
                s.Name ??= string.Empty;
                Stops.Add(s);
            }

            Diag.Info($"[Trail] loaded {Stops.Count} authored stop(s).");
        }
        catch (Exception ex)
        {
            Diag.Error($"[Trail] load failed: {ex.Message}");
            Stops.Clear();
        }
    }

    public static void Save()
    {
        try
        {
            // Temp-then-replace, like every other store here. A half-written trail read on the next
            // launch would silently lose authored work, which is the one thing this file holds that
            // cannot be regenerated.
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Stops, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(tmp, Path, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception ex)
        {
            Diag.Error($"[Trail] save failed: {ex.Message}");
        }
    }

    /// <summary>Captures the player's current position as a new stop at the end of the trail.</summary>
    public static string AddHere()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return "no character loaded.";

        var stop = new TrailStop
        {
            Territory = Plugin.ClientState.TerritoryType,
            MapId     = PlayerStateReader.MapIdFor((ushort)Plugin.ClientState.TerritoryType),
            Name      = $"Stop {Stops.Count + 1}",
        };
        stop.SetPosition(player.Position);

        Stops.Add(stop);
        Save();

        return $"stop {Stops.Count} captured. Write its clue in the lab.";
    }

    public static void Move(int index, int delta)
    {
        int target = index + delta;
        if (index < 0 || index >= Stops.Count || target < 0 || target >= Stops.Count) return;

        (Stops[index], Stops[target]) = (Stops[target], Stops[index]);
        Save();
    }

    public static void Remove(int index)
    {
        if (index < 0 || index >= Stops.Count) return;
        Stops.RemoveAt(index);
        Save();
    }

    public static void Clear()
    {
        Stops.Clear();
        Save();
    }

    /// <summary>A snapshot for the runner, so editing the list mid-run cannot shift it underfoot.</summary>
    public static List<TrailStop> Snapshot()
    {
        var copy = new List<TrailStop>(Stops.Count);
        foreach (var s in Stops) copy.Add(s.Clone());
        return copy;
    }
}
#endif
