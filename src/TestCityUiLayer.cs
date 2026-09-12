#if DEV_BUILD
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;

using FFXIVClientStructs.FFXIV.Component.GUI;

using Vortice.Direct3D11;
using Vortice.DXGI;

using CsDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace TieriChallengesFFXIV;

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Puts the game's own UI back on top of the city.</b>
///
/// <para><b>The problem this solves is architectural, not a bug.</b> Everything ImGui draws is
/// composited after the ENTIRE game frame: <c>Dx11Win32Backend.Render()</c> runs at present time and
/// rasterises every queued draw command in one pass, into a back buffer the game has already filled
/// with its scene AND its native UI. So a full-screen <c>AddImageQuad</c> necessarily lands on top of
/// the chat log and the nameplates. <c>GetBackgroundDrawList()</c> does not help — "background" means
/// beneath other ImGui windows, not beneath the game.</para>
///
/// <para><b>And the depth buffer cannot help either</b>, which is the deeper half: the native UI is
/// 2D, composited after the depth pass, and writes no depth values. Our per-pixel occlusion compares
/// against the 3D scene depth, so there is simply no information anywhere in it saying "a chat box is
/// here". No amount of correctness in the depth test would fix this.</para>
///
/// <para><b>So we repaint the UI ourselves.</b> Snapshot the back buffer BEFORE our city is
/// composited — which at <c>UiBuilder.Draw</c> time means it holds game + native UI and nothing of
/// ours — then, after queueing the city, queue each visible UI element's screen rectangle sampled
/// back out of that snapshot. Draw commands execute in submission order, so the UI lands on top.
/// This is XivMediaPlayer's UILayerCapture pattern, by way of FFXIV-TV's
/// <c>CopyBlitRenderer.RestoreUiAddons</c>.</para>
///
/// <para><b>Chosen over the hook route deliberately.</b> FFXIV-TV's <c>D3DRenderer</c> gets the HUD
/// drawing over injected geometry properly, but costs ELEVEN hooks on the hottest functions in the
/// pipeline — <c>DrawIndexed</c>, <c>Draw</c>, <c>CopyResource</c>, <c>Dispatch</c> and friends —
/// whose detours dereference raw game COM pointers, where a C# catch cannot save us (BROKEN.md 012).
/// Worse for this repo specifically, its teardown <c>Disable()</c>s and <c>Dispose()</c>s all eleven,
/// and Dalamud runs Dispose on every DLL change: freeing a trampoline while the render thread is
/// inside the detour is a crash, and we rebuild constantly. Nothing here hooks anything, so the worst
/// case is a cosmetic artefact rather than the game closing.</para>
/// </summary>
internal sealed unsafe class TestCityUiLayer : IDisposable
{
    /// <summary>Ceiling on restored rectangles per frame. A backstop, not a budget — the curated
    /// list cannot reach it, but a full-screen addon that descends into children could.</summary>
    private const int MaxRects = 96;

    /// <summary>An addon covering more than this fraction of the screen is not restored as one
    /// rectangle — doing so would repaint the whole screen and erase the city completely. Instead we
    /// descend one level and restore its visible children individually.</summary>
    private const float DescendAreaFraction = 0.55f;

    private ID3D11Texture2D?          _tex;
    private ID3D11ShaderResourceView? _srv;
    private uint   _w, _h;
    private Format _fmt = Format.Unknown;

    public bool   Captured       { get; private set; }
    public int    RectsLastFrame { get; private set; }
    public string LastError      { get; private set; } = string.Empty;
    public string Info           { get; private set; } = "not captured";

    /// <summary>Set to log every candidate addon's geometry once. Clears itself.</summary>
    public bool DebugLogNextFrame;

    /// <summary>
    /// The UI we put back, in draw order.
    ///
    /// <para><b>Deliberately short.</b> FFXIV-TV restores about a hundred addons because a video
    /// screen can cover any of them; the city is translucent and this is a dev experiment, so the
    /// list is the HUD a player actually reads while walking around. Every entry costs a rectangle
    /// blit per frame, and a name that is never visible costs only a failed lookup.</para>
    ///
    /// <para><b><c>_NamePlate</c> is first and is the interesting one.</b> FFXIV-TV's list omits it
    /// entirely, and it is one of the two elements reported wrong. It is believed to be a single
    /// full-screen addon holding every nameplate as a child — which is exactly why
    /// <see cref="DescendAreaFraction"/> exists rather than a special case: if it is full-screen we
    /// descend automatically, and if that belief is wrong the ordinary path still works.</para>
    /// </summary>
    private static readonly string[] Addons =
    {
        "_NamePlate",
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

    private readonly List<Vector4> _rects = new(MaxRects);

    /// <summary>
    /// Copies the back buffer into a texture we can sample.
    ///
    /// <para>Re-fetched every frame rather than cached, for the same reason the depth capture is: a
    /// resize destroys and recreates the game's textures, and a cached pointer would be a stale COM
    /// object. Null-checked at every hop — these are field reads, never calls into game code.</para>
    /// </summary>
    public bool TryCapture(ID3D11Device device, ID3D11DeviceContext ctx)
    {
        Captured = false;

        try
        {
            var kernel = CsDevice.Instance();
            if (kernel == null) return false;

            var swap = kernel->SwapChain;
            if (swap == null) return false;

            var bb = swap->BackBuffer;
            if (bb == null) return false;

            nint srcPtr = (nint)bb->D3D11Texture2D;
            if (srcPtr == 0) return false;

            Marshal.AddRef(srcPtr);
            using var src = new ID3D11Texture2D(srcPtr);
            var desc = src.Description;

            if (_tex == null || _w != desc.Width || _h != desc.Height || _fmt != desc.Format)
            {
                _srv?.Dispose(); _srv = null;
                _tex?.Dispose(); _tex = null;

                _tex = device.CreateTexture2D(new Texture2DDescription
                {
                    Width             = desc.Width,
                    Height            = desc.Height,
                    MipLevels         = 1,
                    ArraySize         = 1,
                    Format            = desc.Format,
                    SampleDescription = desc.SampleDescription,
                    Usage             = ResourceUsage.Default,
                    BindFlags         = BindFlags.ShaderResource,
                });

                _srv = device.CreateShaderResourceView(_tex);

                _w   = desc.Width;
                _h   = desc.Height;
                _fmt = desc.Format;
                Info = $"{desc.Width}×{desc.Height} {desc.Format}";

                Diag.Info($"[City] UI layer capture: {Info}");
            }

            ctx.CopyResource(_tex, src);
            Captured = true;
            return true;
        }
        catch (Exception ex)
        {
            // Per-frame, so never logged at 60 Hz — the panel shows it.
            LastError = $"ui capture failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Queues every visible UI rectangle on top of whatever has already been queued this frame.
    ///
    /// <para>Called AFTER the city's blit. ImGui executes draw commands in submission order, so this
    /// is what puts the HUD back in front.</para>
    /// </summary>
    public void Restore(uint vpW, uint vpH)
    {
        RectsLastFrame = 0;

        if (!Captured || _srv == null || vpW == 0 || vpH == 0) return;

        CollectRects(vpW, vpH);

        if (_rects.Count == 0) return;

        var list  = ImGui.GetBackgroundDrawList();
        var texId = new ImTextureID(_srv.NativePointer);

        foreach (var r in _rects)
        {
            var pMin  = new Vector2(r.X, r.Y);
            var pMax  = new Vector2(r.Z, r.W);
            var uvMin = new Vector2(r.X / vpW, r.Y / vpH);
            var uvMax = new Vector2(r.Z / vpW, r.W / vpH);

            list.AddImageQuad(texId,
                              pMin, new Vector2(pMax.X, pMin.Y), pMax, new Vector2(pMin.X, pMax.Y),
                              uvMin, new Vector2(uvMax.X, uvMin.Y), uvMax, new Vector2(uvMin.X, uvMax.Y),
                              0xFFFFFFFFu);
        }

        RectsLastFrame = _rects.Count;
    }

    private void CollectRects(uint vpW, uint vpH)
    {
        _rects.Clear();

        bool log = DebugLogNextFrame;
        DebugLogNextFrame = false;

        float screenArea = vpW * (float)vpH;

        foreach (var name in Addons)
        {
            AtkUnitBase* addon;
            try
            {
                addon = (AtkUnitBase*)(nint)Plugin.GameGui.GetAddonByName(name);
            }
            catch
            {
                continue;
            }

            if (addon == null) continue;
            if (!addon->IsVisible) continue;

            var root = addon->RootNode;
            if (root == null) continue;

            var rect = NodeRect(root, vpW, vpH);
            float area = Area(rect);

            if (log)
                Diag.Info($"[City] UILAYER {name,-24} vis=1 rect=({rect.X:F0},{rect.Y:F0})-({rect.Z:F0},{rect.W:F0}) "
                        + $"area={area / screenArea:P1} children={root->ChildCount}");

            if (area <= 0f) continue;

            // Too big to repaint as one piece: it would cover the city entirely. Descend instead.
            if (area / screenArea > DescendAreaFraction)
            {
                int before = _rects.Count;
                Descend(root, vpW, vpH);

                if (log)
                    Diag.Info($"[City] UILAYER {name,-24} DESCENDED -> {_rects.Count - before} child rect(s)");

                continue;
            }

            Add(rect);
        }
    }

    /// <summary>One level of children only. A nameplate is a component node directly under the
    /// addon's root, and a full recursive walk would cost far more pointer chasing for rectangles
    /// that are already covered by their parent.</summary>
    private void Descend(AtkResNode* parent, uint vpW, uint vpH)
    {
        for (var child = parent->ChildNode; child != null; child = child->PrevSiblingNode)
        {
            if (_rects.Count >= MaxRects) return;
            if ((child->NodeFlags & NodeFlags.Visible) == 0) continue;

            var rect = NodeRect(child, vpW, vpH);
            if (Area(rect) <= 0f) continue;

            Add(rect);
        }
    }

    /// <summary>A node's screen rectangle, clamped to the viewport. <c>ScreenX</c>/<c>ScreenY</c> are
    /// already resolved through the whole parent chain, so no transform composition is needed here —
    /// which is the one place this could plausibly have gone wrong.</summary>
    private static Vector4 NodeRect(AtkResNode* node, uint vpW, uint vpH)
    {
        float w = node->Width  * node->ScaleX;
        float h = node->Height * node->ScaleY;

        float x0 = MathF.Max(node->ScreenX, 0f);
        float y0 = MathF.Max(node->ScreenY, 0f);
        float x1 = MathF.Min(node->ScreenX + w, vpW);
        float y1 = MathF.Min(node->ScreenY + h, vpH);

        return new Vector4(x0, y0, x1, y1);
    }

    private static float Area(in Vector4 r)
    {
        float w = r.Z - r.X, h = r.W - r.Y;
        return w <= 0f || h <= 0f ? 0f : w * h;
    }

    private void Add(in Vector4 r)
    {
        if (_rects.Count < MaxRects) _rects.Add(r);
    }

    /// <summary>Managed COM releases only — no game code is called from here, because unload runs on
    /// every rebuild at a moment the player did not choose (BROKEN.md 012).</summary>
    public void Dispose()
    {
        try
        {
            _srv?.Dispose(); _srv = null;
            _tex?.Dispose(); _tex = null;
        }
        catch (Exception ex)
        {
            Diag.Error($"[City] UI layer teardown failed: {ex.Message}");
        }
    }
}
#endif
