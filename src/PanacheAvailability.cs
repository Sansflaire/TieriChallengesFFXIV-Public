using System;
using System.Runtime.CompilerServices;

namespace TieriChallengesFFXIV;

/// <summary>
/// Answers "can this plugin actually use PanacheUI right now?" without dying if it cannot.
///
/// <para><b>Why this is fiddly.</b> The CLR resolves an assembly when it JITs the first method
/// that mentions one of its types — and it loads a type's fields when the type itself is loaded.
/// So a class holding a <c>PanacheSurface</c> field, or a <c>static readonly PColor</c>, will
/// throw <c>TypeInitializationException</c> at construction if PanacheUI.dll is missing, long
/// before any code could show a friendly message. That is why every Panache-touching type lives
/// behind this probe and is only constructed once it returns true.</para>
///
/// <para><see cref="ProbeInner"/> is deliberately <c>NoInlining</c>: the resolution failure has
/// to happen when <see cref="Probe"/> calls it, inside the try, not while <see cref="Probe"/>
/// itself is being JITted.</para>
/// </summary>
internal static class PanacheAvailability
{
    private static bool?  _available;
    private static string _reason = string.Empty;

    /// <summary>Empty when available; otherwise why not, for the UI to show.</summary>
    public static string FailureReason => _reason;

    /// <summary>Cached — the answer cannot change without a plugin reload.</summary>
    public static bool IsAvailable
    {
        get
        {
            _available ??= Probe();
            return _available.Value;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Probe()
    {
        try
        {
            ProbeInner();
            Plugin.Log.Information("[Panache] library available.");
            return true;
        }
        catch (Exception ex)
        {
            // Missing DLL, wrong architecture, or a version whose types no longer match.
            _reason = ex is TypeLoadException or System.IO.FileNotFoundException
                             or BadImageFormatException or TypeInitializationException
                ? $"{ex.GetType().Name}"
                : $"{ex.GetType().Name}: {ex.Message}";

            Plugin.Log.Warning($"[Panache] unavailable ({_reason}). Falling back to plain ImGui.");
            return false;
        }
    }

    /// <summary>
    /// Touches PanacheUI in two ways that between them force the whole dependency chain to
    /// resolve: a Core type, and the theming path that pulls in SkiaSharp underneath.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeInner()
    {
        var probe = PanacheUI.Core.PColor.FromHex("#E3B341");
        var themed = PanacheUI.Components.Theme.Base;

        // Use the results so nothing can be optimised away.
        if (probe.A == 0 && themed.A == 0)
            throw new InvalidOperationException("PanacheUI returned degenerate colours.");
    }
}
