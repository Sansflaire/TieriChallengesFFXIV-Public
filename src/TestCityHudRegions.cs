#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Where the game's HUD is on screen, so the city can avoid covering it.</b>
///
/// <para><b>The problem is architectural, not a bug in the depth test.</b> Everything ImGui draws is
/// composited after the ENTIRE game frame — <c>InterfaceManager.DxgiSwapChainPresentDetour</c> calls
/// the render path before the original Present, and <c>Dx11Win32Backend.Render</c> rasterises every
/// queued command into a back buffer the game has already filled with its scene AND its native UI. So
/// a full-screen blit covers the HUD by construction; "background" orders us behind other ImGui
/// content, not behind the game. <b>And the depth buffer cannot help either:</b> the UI is 2D,
/// composited after the depth pass, writing no depth at all, so our per-pixel occlusion has no
/// information anywhere saying "a chat box is here".</para>
///
/// <para><b>So we cut holes in ourselves instead of repainting the UI.</b> The regions gathered here
/// are punched out of the city's own offscreen surface as transparent black before it is blitted,
/// which leaves the game's already-composited HUD showing through untouched.</para>
///
/// <para><b>Why not snapshot the back buffer and repaint the UI on top (FFXIV-TV's
/// <c>RestoreUiAddons</c>)?</b> That was implemented first, at 0.84.54.15, and replaced the same day.
/// A back-buffer snapshot holds <i>world plus UI</i>, never an isolated UI layer — so restoring a
/// rectangle puts the WORLD back between the glyphs and behind a translucent chat panel, which is the
/// identical hole this approach leaves, for the price of a full-screen copy every frame
/// (~14.75 MB payload, ~29.5 MB of traffic at 2560×1440). It also multiplies by the captured texture's
/// alpha — a white tint does NOT force sampled alpha to one — and can overwrite another plugin's
/// ImGui overlay with a stale snapshot. Same approximation, strictly more cost and more ways to be
/// wrong.</para>
///
/// <para><b>And not the hook route</b>, which is the only exact answer: it needs injection before the
/// first native HUD draw, whose detours dereference raw game COM pointers where a C# catch cannot
/// save us (BROKEN.md 012), and whose teardown runs on every DLL change — i.e. every rebuild. The
/// payoff is cosmetic and the failure mode is the game closing.</para>
///
/// <para><b>This is a readability fix, not exact UI-over-city composition, and the panel says so.</b>
/// The city is removed from a rectangle, so it is also removed from the transparent gaps inside that
/// rectangle. Enlarging rectangles to hide that is the wrong move; the honest escalation is a
/// pre-HUD pass.</para>
/// </summary>
internal sealed unsafe class TestCityHudRegions
{
    /// <summary>Plate slots in <c>AddonNamePlate</c>. The game's own constant
    /// (<c>NumNamePlateObjects</c>), not a guess — never inferred from the object table.</summary>
    private const int PlateSlots = 50;

    /// <summary>Ceilings. Engineering backstops, not measured game constants.</summary>
    private const int MaxRects        = 512;
    private const int MaxParentDepth  = 32;

    /// <summary>A root bigger than this fraction of the screen is REPORTED AND REJECTED, never
    /// masked. Masking it would erase the city wholesale, and an oversized unknown container is
    /// exactly the case where a generic heuristic does damage — a legitimately fullscreen menu needs
    /// its own policy, not a silent screen-wide hole.</summary>
    private const float RejectAreaFraction = 0.55f;

    private readonly List<Vector4> _rects = new(64);

    public IReadOnlyList<Vector4> Rects => _rects;

    public int    PlatesFound    { get; private set; }
    public int    AddonsFound    { get; private set; }
    public int    RejectedOversized { get; private set; }
    public string LastError      { get; private set; } = string.Empty;

    /// <summary>Logs every candidate's real geometry once, then clears itself.</summary>
    public bool DebugLogNextFrame;

    /// <summary>
    /// Ordinary addons, as a candidate registry rather than a claim of coverage.
    ///
    /// <para><b>Deliberately short.</b> Every name costs a lookup per frame, and this is the HUD a
    /// player actually reads while walking around. Unlisted addons stay unprotected, which is why the
    /// panel reports "partial HUD coverage" rather than implying completeness.</para>
    ///
    /// <para><b>NamePlate is NOT in this list</b> — it has its own typed provider below, because its
    /// content lives in <c>NamePlateObjectArray</c> and is not reachable by walking
    /// <c>RootNode.ChildNode</c> siblings.</para>
    /// </summary>
    private static readonly string[] Addons =
    {
        "_ChatLog", "_ChatLogPanel_0", "_ChatLogPanel_1", "_ChatLogPanel_2", "_ChatLogPanel_3",
        "_PartyList", "_ParameterWidget", "_NaviMap", "_ToDoList", "_ScenarioTree",
        "_TargetInfo", "_TargetInfoMainTarget", "_TargetInfoBuffDebuff", "_TargetInfoCastBar",
        "_FocusTargetInfo", "_LimitBreak", "_ExpBar",
        "_HotBar", "_HotBar1", "_HotBar2", "_HotBar3", "_HotBar4",
        "_HotBar5", "_HotBar6", "_HotBar7", "_HotBar8", "_HotBar9",
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
        "_ActionCross", "_ActionDoubleCrossL", "_ActionDoubleCrossR",
        "_Status", "_StatusCustom0", "_StatusCustom1", "_StatusCustom2", "_StatusCustom3",
        "_StatusEnhancements", "_StatusEnfeeblements", "_StatusOthers",
    };

    public void Gather(uint vpW, uint vpH)
    {
        _rects.Clear();
        PlatesFound = AddonsFound = RejectedOversized = 0;

        bool log = DebugLogNextFrame;
        DebugLogNextFrame = false;

        try
        {
            GatherNamePlates(vpW, vpH, log);
            GatherAddons(vpW, vpH, log);
        }
        catch (Exception ex)
        {
            // Gather everything before any GPU work, so a failure here simply means no mask this
            // frame rather than a half-masked surface.
            LastError = ex.Message;
            _rects.Clear();
        }
    }

    /// <summary>
    /// Every visible nameplate, one rectangle each.
    ///
    /// <para><b>The addon is <c>"NamePlate"</c>, with NO leading underscore.</b> Verified against
    /// <c>NamePlateGui.RequestRedraw</c>, which calls <c>GetAddonByName("NamePlate")</c>, and the
    /// <c>[Addon("NamePlate")]</c> annotation on the struct. A first cut of this feature used
    /// <c>"_NamePlate"</c> — copied from the shape of every other HUD addon name — and the lookup
    /// simply never matched, so nameplate protection was dead code that reported success.</para>
    ///
    /// <para><b>The plate root is never masked as one rectangle.</b> Each plate is taken
    /// individually from <c>NamePlateObjectArray</c>, so whether the addon's own root happens to be
    /// fullscreen never matters — a fact that is still not established and does not need to be.</para>
    /// </summary>
    private void GatherNamePlates(uint vpW, uint vpH, bool log)
    {
        nint ptr;
        try   { ptr = (nint)Plugin.GameGui.GetAddonByName("NamePlate"); }
        catch { return; }

        if (ptr == 0) return;

        var addon = (AddonNamePlate*)ptr;
        if (!addon->AtkUnitBase.IsVisible) return;

        var array = addon->NamePlateObjectArray;
        if (array == null) return;

        for (int i = 0; i < PlateSlots; i++)
        {
            if (_rects.Count >= MaxRects) return;

            var plate = &array[i];
            var root  = plate->RootComponentNode;
            if (root == null) continue;

            var node = (AtkResNode*)root;
            if ((node->NodeFlags & NodeFlags.Visible) == 0) continue;

            if (!TryBounds(node, vpW, vpH, out var rect)) continue;

            _rects.Add(rect);
            PlatesFound++;

            if (log)
                Diag.Info($"[City] HUD plate[{i,2}] ({rect.X:F0},{rect.Y:F0})-({rect.Z:F0},{rect.W:F0})");
        }
    }

    private void GatherAddons(uint vpW, uint vpH, bool log)
    {
        float screenArea = vpW * (float)vpH;

        foreach (var name in Addons)
        {
            if (_rects.Count >= MaxRects) return;

            nint ptr;
            try   { ptr = (nint)Plugin.GameGui.GetAddonByName(name); }
            catch { continue; }

            if (ptr == 0) continue;

            var addon = (AtkUnitBase*)ptr;
            if (!addon->IsVisible) continue;

            var root = addon->RootNode;
            if (root == null) continue;

            if (!TryBounds(root, vpW, vpH, out var rect)) continue;

            float frac = Area(rect) / screenArea;

            if (log)
                Diag.Info($"[City] HUD {name,-24} ({rect.X:F0},{rect.Y:F0})-({rect.Z:F0},{rect.W:F0}) area={frac:P1}");

            // Rejected and reported, never silently turned into a screen-wide hole.
            if (frac > RejectAreaFraction)
            {
                RejectedOversized++;
                if (log) Diag.Info($"[City] HUD {name,-24} REJECTED — oversized, would erase the city");
                continue;
            }

            _rects.Add(rect);
            AddonsFound++;
        }
    }

    /// <summary>
    /// A node's screen rectangle, transformed through its whole ancestor chain.
    ///
    /// <para><b>Not <c>ScreenX + Width * ScaleX</c>.</b> That ignores ancestor scale and rotation, so
    /// it is wrong for exactly the moving, scaled elements this needs to follow — a nameplate inside a
    /// scaled container above all. Four local corners are carried up through each parent applying
    /// offset, origin-relative scale and rotation, matching the algorithm Dalamud's own UI inspector
    /// uses (<c>NodeBounds.TransformPoints</c>), then min/max'd and rounded outward.</para>
    ///
    /// <para>Pure arithmetic on field reads — no native calls. <c>AtkResNode.GetBounds</c> and
    /// <c>IsVisible()</c> are native methods and are deliberately not used; per BROKEN.md 012 a C#
    /// catch cannot survive a bad one, so the managed transform is both safer and sufficient.</para>
    /// </summary>
    private static bool TryBounds(AtkResNode* node, uint vpW, uint vpH, out Vector4 rect)
    {
        rect = default;

        float w = node->Width, h = node->Height;
        if (w <= 0f || h <= 0f) return false;

        Span<Vector2> pts = stackalloc Vector2[4];
        pts[0] = new Vector2(0f, 0f);
        pts[1] = new Vector2(w,  0f);
        pts[2] = new Vector2(w,  h);
        pts[3] = new Vector2(0f, h);

        int depth = 0;

        // Depth-capped rather than cycle-tracked: a malformed chain then costs 32 wasted iterations
        // and a wrong rectangle, never a hang.
        for (var cur = node; cur != null && depth < MaxParentDepth; cur = cur->ParentNode, depth++)
        {
            var offset = new Vector2(cur->X, cur->Y);
            var origin = offset + new Vector2(cur->OriginX, cur->OriginY);

            float sx = cur->ScaleX, sy = cur->ScaleY, rot = cur->Rotation;
            float cos = MathF.Cos(rot), sin = MathF.Sin(rot);

            for (int i = 0; i < 4; i++)
            {
                var p = pts[i] + offset - origin;
                p = new Vector2(p.X * sx, p.Y * sy);
                p = new Vector2(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos);
                pts[i] = p + origin;
            }
        }

        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;

        for (int i = 0; i < 4; i++)
        {
            if (!float.IsFinite(pts[i].X) || !float.IsFinite(pts[i].Y)) return false;
            x0 = MathF.Min(x0, pts[i].X); y0 = MathF.Min(y0, pts[i].Y);
            x1 = MathF.Max(x1, pts[i].X); y1 = MathF.Max(y1, pts[i].Y);
        }

        // Rounded OUTWARD, then clamped: a half-pixel short leaves a tinted fringe on the UI edge.
        x0 = MathF.Max(MathF.Floor(x0), 0f);
        y0 = MathF.Max(MathF.Floor(y0), 0f);
        x1 = MathF.Min(MathF.Ceiling(x1), vpW);
        y1 = MathF.Min(MathF.Ceiling(y1), vpH);

        if (x1 - x0 <= 0f || y1 - y0 <= 0f) return false;

        rect = new Vector4(x0, y0, x1, y1);
        return true;
    }

    private static float Area(in Vector4 r) => (r.Z - r.X) * (r.W - r.Y);

    /// <summary>Outlines every gathered region, so the boxes can be checked against the pixels they
    /// are meant to cover in the same frame.</summary>
    public void DrawPreview()
    {
        var list = ImGui.GetForegroundDrawList();

        foreach (var r in _rects)
            list.AddRect(new Vector2(r.X, r.Y), new Vector2(r.Z, r.W), 0xFF00FF00u);
    }
}
#endif
