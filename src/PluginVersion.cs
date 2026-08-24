using System;
using System.Reflection;

namespace TieriChallengesFFXIV;

/// <summary>
/// The plugin's version, in the project's own A.B.C.D scheme.
///
/// <list type="bullet">
///   <item><b>A</b> — channel. 0 = beta, 1 = released.</item>
///   <item><b>B</b> — percent complete toward the next full release, 0–100, computed from the
///         weighted milestone table in <c>docs/How To Versioning.md</c>.</item>
///   <item><b>C</b> — major update counter. Unbounded.</item>
///   <item><b>D</b> — minor update counter. Unbounded; resets to 0 when C increments.</item>
/// </list>
///
/// <para><b>Single source of truth is the csproj.</b> This class reads the compiled assembly
/// version rather than declaring its own literal, so the number shown in the UI physically
/// cannot drift from the number that was built. Bump <c>&lt;Version&gt;</c> in
/// TieriChallengesFFXIV.csproj and everything else follows — with one exception: the manifest's
/// <c>AssemblyVersion</c> is a separate file and must be updated by hand to match. See the
/// release checklist in the versioning doc.</para>
/// </summary>
public static class PluginVersion
{
    /// <summary>Compiled assembly version. .NET maps A.B.C.D onto Major.Minor.Build.Revision.</summary>
    public static readonly Version Current =
        typeof(PluginVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>A — 0 while in beta, 1 once released.</summary>
    public static int Channel => Current.Major;

    /// <summary>B — percent toward the next full release.</summary>
    public static int PercentComplete => Current.Minor;

    /// <summary>C — major update counter.</summary>
    public static int MajorUpdate => Current.Build;

    /// <summary>D — minor update counter.</summary>
    public static int MinorUpdate => Current.Revision;

    public static bool IsBeta => Channel == 0;

    /// <summary>"v0.59.1.0" — the compact form shown in the window header.</summary>
    public static string Display =>
        $"v{Current.Major}.{Current.Minor}.{Current.Build}.{Current.Revision}";

    /// <summary>"v0.59.1.0 beta · 59% to 1.0" — the long form for logs and the help text.</summary>
    public static string DisplayLong =>
        IsBeta
            ? $"{Display} beta · {PercentComplete}% to 1.0"
            : $"{Display} release";
}
