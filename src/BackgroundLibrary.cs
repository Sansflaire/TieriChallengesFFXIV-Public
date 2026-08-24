using System;
using System.Collections.Generic;
using System.IO;

namespace TieriChallengesFFXIV;

/// <summary>
/// The built-in Appearance backgrounds shipped beside the DLL, plus the default "none" option.
///
/// <para>Shipped as plain files in a <c>backgrounds\</c> folder next to the DLL — same shape as
/// <see cref="GameSound"/>'s cue audio — rather than as embedded resources, so a built-in and a
/// user's own custom path go through the exact same "decode this file" code in
/// <c>MainWindow.ResolveBackground</c>. There is no second, stream-based decode path to keep in
/// sync with the first.</para>
/// </summary>
internal static class BackgroundLibrary
{
    private const string Folder = "backgrounds";

    /// <summary>One selectable background: a display name and the absolute path it resolves to.</summary>
    public sealed record Option(string Name, string Path);

    /// <summary>The default — no background image, the plain theme.</summary>
    public const string NoneName = "Blank (default)";

    /// <summary>Folder the built-ins live in, resolved beside the running DLL.</summary>
    public static string FolderPath
    {
        get
        {
            string dir = Plugin.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty;
            return Path.Combine(dir, Folder);
        }
    }

    /// <summary>
    /// Every shipped background, alphabetical by display name. Re-scanned on every call rather
    /// than cached — this is a handful of files read once when the Appearance dialog is open,
    /// not a per-frame cost, and a cache would need its own invalidation for no real benefit.
    /// </summary>
    public static List<Option> BuiltIn()
    {
        var list = new List<Option>();

        try
        {
            string dir = FolderPath;
            if (!Directory.Exists(dir)) return list;

            foreach (string file in Directory.GetFiles(dir, "*.png"))
                list.Add(new Option(Path.GetFileNameWithoutExtension(file), file));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Appearance] could not list built-in backgrounds: {ex.Message}");
        }

        list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>
    /// The display name for a configured path — a built-in's name if it matches one, "Custom" for
    /// anything else, or <see cref="NoneName"/> when nothing is set.
    /// </summary>
    public static string DescribeCurrent(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return NoneName;

        foreach (var opt in BuiltIn())
            if (string.Equals(opt.Path, configuredPath, StringComparison.OrdinalIgnoreCase))
                return opt.Name;

        return "Custom";
    }
}
