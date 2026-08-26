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

    /// <summary>
    /// Extensions treated as a built-in background, in no particular order.
    ///
    /// <para>Enumerated rather than hardcoded to one format so the shipped set can change container
    /// without a code change — which is exactly what happened when the four backgrounds moved from
    /// PNG to JPEG and shrank the download from 14 MB to under 8. SkiaSharp decodes all of these,
    /// and the custom-path box has always advertised "png, jpg, bmp", so nothing new is being asked
    /// of the decoder.</para>
    /// </summary>
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp" };

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

            foreach (string file in Directory.GetFiles(dir))
            {
                if (Array.IndexOf(Extensions, Path.GetExtension(file).ToLowerInvariant()) < 0)
                    continue;

                list.Add(new Option(Path.GetFileNameWithoutExtension(file), file));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Appearance] could not list built-in backgrounds: {ex.Message}");
        }

        list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    /// <summary>
    /// Follow a built-in whose file extension changed under the player, e.g. the four shipped
    /// backgrounds moving from <c>.png</c> to <c>.jpg</c>.
    ///
    /// <para>Returns the configured path unchanged unless ALL of the following hold: it is set, it
    /// no longer exists, it points inside the built-in folder, and a built-in with the same base
    /// name is there now. Without that last pair of conditions a user's own missing image could be
    /// silently repointed at one of ours purely because the file names happened to match, which
    /// would be a stranger failure than the blank background it replaced.</para>
    /// </summary>
    /// <remarks>
    /// Re-encoding the shipped assets is the sort of change that looks purely internal and is not:
    /// the chosen background is persisted as an absolute PATH, so changing the container renames
    /// the file out from under every config that named it, and the player's window would simply
    /// come back blank with nothing to explain it.
    /// </remarks>
    public static string ResolveRenamed(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return configuredPath;

        try
        {
            if (File.Exists(configuredPath)) return configuredPath;

            string dir = FolderPath;
            if (string.IsNullOrEmpty(dir)) return configuredPath;

            // Only ever rewrite a path that was pointing at one of ours.
            string? configuredDir = Path.GetDirectoryName(configuredPath);
            if (!string.Equals(configuredDir?.TrimEnd(Path.DirectorySeparatorChar),
                               dir.TrimEnd(Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase))
                return configuredPath;

            string name = Path.GetFileNameWithoutExtension(configuredPath);

            foreach (var opt in BuiltIn())
            {
                if (!string.Equals(opt.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

                Plugin.Log.Information(
                    $"[Appearance] built-in background \"{name}\" moved to "
                  + $"{Path.GetExtension(opt.Path)}; config updated.");
                return opt.Path;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Appearance] could not re-resolve the background path: {ex.Message}");
        }

        return configuredPath;
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
