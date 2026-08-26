using System;
using System.Collections.Generic;
using System.Numerics;

namespace TieriChallengesFFXIV;

/// <summary>
/// A user-recolourable UI slot. Persisted BY NAME (the enum's string), never by ordinal, so slots
/// can be reordered or retired without repainting somebody's window at random.
/// </summary>
public enum PaletteSlot
{
    /// <summary>The house gold. Headline accent, primary pills, most icons.</summary>
    Accent,

    /// <summary>Challenge and section titles.</summary>
    Title,

    /// <summary>The description line under a title.</summary>
    Description,

    /// <summary>A revealed hint. Deliberately a different hue from Accent — see MainWindow.</summary>
    Hint,

    /// <summary>Done, satisfied, progressing.</summary>
    Success,

    /// <summary>Destructive: reset, abandon, failure.</summary>
    Danger,

    /// <summary>Pending, unknown, unrated, masked.</summary>
    Neutral,

    /// <summary>Quest chains.</summary>
    Quest,

    /// <summary>Adventures.</summary>
    Adventure,
}

/// <summary>
/// The plugin's recolourable palette, shared by BOTH renderers.
///
/// <para><b>Framework-neutral on purpose.</b> Colours are held as hex strings and handed out as
/// <see cref="Vector4"/>. This file imports nothing from PanacheUI, because
/// <see cref="DialogTheme"/> reads it and that file's whole contract is that it survives PanacheUI
/// being absent. <c>MainWindow</c> converts to <c>PColor</c> on its own side, where the reference
/// is already present and safe.</para>
///
/// <para><b>Every slot has a shipped default and a Reset.</b> A colour picker without a way back is
/// a trap: one bad drag on the Description slot and the list is unreadable, with the control to fix
/// it rendered in the colour that was just broken.</para>
/// </summary>
internal static class Palette
{
    /// <summary>
    /// The shipped values. These ARE the design system's palette — do not change one without
    /// changing DESIGN_SYSTEM, since every plugin in the suite is meant to agree on the semantics
    /// even where the hues differ.
    /// </summary>
    public static readonly IReadOnlyDictionary<PaletteSlot, string> Defaults =
        new Dictionary<PaletteSlot, string>
        {
            [PaletteSlot.Accent]      = "#E3B341",
            [PaletteSlot.Title]       = "#EBEBEB",
            [PaletteSlot.Description] = "#9E9AA6",
            [PaletteSlot.Hint]        = "#A9C9F0",
            [PaletteSlot.Success]     = "#7FD6A9",
            [PaletteSlot.Danger]      = "#E57B72",
            [PaletteSlot.Neutral]     = "#8B8794",
            [PaletteSlot.Quest]       = "#8FB8E8",
            [PaletteSlot.Adventure]   = "#7FD6A9",
        };

    /// <summary>Human labels for the settings list.</summary>
    public static readonly IReadOnlyDictionary<PaletteSlot, (string Label, string What)> Describe =
        new Dictionary<PaletteSlot, (string, string)>
        {
            [PaletteSlot.Accent]      = ("Accent",       "Headings, buttons and most icons"),
            [PaletteSlot.Title]       = ("Titles",       "Challenge names"),
            [PaletteSlot.Description] = ("Descriptions", "The line under each challenge name"),
            [PaletteSlot.Hint]        = ("Hints",        "A revealed hint"),
            [PaletteSlot.Success]     = ("Completed",    "Done challenges, progress, ticks"),
            [PaletteSlot.Danger]      = ("Warnings",     "Reset, abandon, run failed"),
            [PaletteSlot.Neutral]     = ("Muted",        "Pending, unrated and hidden challenges"),
            [PaletteSlot.Quest]       = ("Quests",       "Quest chains"),
            [PaletteSlot.Adventure]   = ("Adventures",   "Multi-objective challenges"),
        };

    /// <summary>
    /// Set once by Plugin at startup. Static because both renderers, every dialog and the toasts
    /// read colours, and threading a config reference through every one of them to recolour a pill
    /// would be a worse trade than this.
    /// </summary>
    private static Configuration? _config;

    public static void Bind(Configuration config) => _config = config;

    /// <summary>The configured hex for a slot, or its shipped default.</summary>
    public static string Hex(PaletteSlot slot)
    {
        var custom = _config?.PaletteOverrides;
        if (custom != null && custom.TryGetValue(slot.ToString(), out var hex)
            && !string.IsNullOrWhiteSpace(hex))
            return hex;

        return Defaults[slot];
    }

    public static Vector4 Vec(PaletteSlot slot) => Parse(Hex(slot), Defaults[slot]);

    /// <summary>True when this slot has been changed from the shipped value.</summary>
    public static bool IsOverridden(PaletteSlot slot)
    {
        var custom = _config?.PaletteOverrides;
        return custom != null && custom.ContainsKey(slot.ToString());
    }

    public static void Set(PaletteSlot slot, Vector4 colour)
    {
        if (_config == null) return;
        _config.PaletteOverrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
        _config.PaletteOverrides[slot.ToString()] = ToHex(colour);
    }

    public static void Reset(PaletteSlot slot) => _config?.PaletteOverrides?.Remove(slot.ToString());

    public static void ResetAll() => _config?.PaletteOverrides?.Clear();

    public static bool AnyOverridden =>
        _config?.PaletteOverrides is { Count: > 0 };

    // ── Conversion ───────────────────────────────────────────────────────────

    /// <summary>
    /// "#RRGGBB" or "#RRGGBBAA" to a Vector4. Falls back to the slot's own default rather than to
    /// black or magenta: a hand-edited config with a typo in it should look unchanged, not
    /// vandalised.
    /// </summary>
    public static Vector4 Parse(string hex, string fallback)
    {
        if (TryParse(hex, out var c)) return c;
        return TryParse(fallback, out var f) ? f : new Vector4(1f, 1f, 1f, 1f);
    }

    private static bool TryParse(string? hex, out Vector4 colour)
    {
        colour = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        string s = hex.Trim().TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;

        try
        {
            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            byte a = s.Length == 8 ? Convert.ToByte(s.Substring(6, 2), 16) : (byte)255;

            colour = new Vector4(r / 255f, g / 255f, b / 255f, a / 255f);
            return true;
        }
        catch { return false; }
    }

    public static string ToHex(Vector4 c)
    {
        static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return $"#{B(c.X):X2}{B(c.Y):X2}{B(c.Z):X2}{B(c.W):X2}";
    }
}
