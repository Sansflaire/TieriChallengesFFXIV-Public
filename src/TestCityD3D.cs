#if DEV_BUILD
using System;
using System.Numerics;
using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

using CsDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using CsRtm    = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;
using CameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace TieriChallengesFFXIV;

/// <summary>One vertex of the city mesh: a world position and a colour. 28 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CityVertex
{
    public Vector3 Position;
    public Vector4 Colour;

    public CityVertex(Vector3 position, Vector4 colour)
    {
        Position = position;
        Colour   = colour;
    }

    public const int Stride = 28;
}

/// <summary>
/// DEVELOPER BUILD ONLY. <b>Real D3D11 rendering of the Test City, with true per-pixel occlusion
/// against the game's depth buffer.</b> Replaces the raycast approximation, which could only see the
/// collision mesh and only at a 3×3 granularity per face.
///
/// <para><b>Architecture: ZERO game render hooks.</b> Everything happens at <c>UiBuilder.Draw</c>
/// time, which is where this plugin already draws:</para>
/// <list type="number">
/// <item><c>CopyResource</c> the game's depth-stencil into a plugin-owned, SRV-bindable texture.</item>
/// <item>Draw the city's triangles and gridlines into a plugin-owned offscreen target, with our own
/// depth buffer for self-occlusion, and a pixel shader that discards any fragment the captured scene
/// depth says is behind the world.</item>
/// <item>Blit that offscreen surface onto the ImGui background draw list, exactly where the painted
/// quads used to go.</item>
/// </list>
///
/// <para><b>This is a direct port of the approach in FFXIV-TV's <c>CopyBlitRenderer</c> and
/// XivMediaPlayer's <c>DepthTestedRenderer</c>, read before writing a line of this.</b> Its
/// alternative — FFXIV-TV's own <c>D3DRenderer</c> — reaches the same occlusion by hooking
/// <c>ID3D11DeviceContext::DrawIndexed</c> and <c>OMSetRenderTargets</c> to inject into the game's
/// HUD pass, which its <c>Golden-Standard-Rendering.md</c> records as taking dozens of versions of
/// diagnostics to stabilise. That buys one thing this does not need: the game's HUD drawing on TOP
/// of our geometry. Our painted city has always drawn over the HUD, so there is nothing to gain and
/// a render-pipeline hook to lose.</para>
///
/// <para><b>The four facts this depends on, all read from source rather than inferred:</b></para>
/// <list type="bullet">
/// <item><c>Device.Instance()->D3D11DeviceContext</c> is the game's immediate context, and
/// <c>.Device</c> off it is the device — both plain field reads
/// (<c>Graphics.Kernel.Device</c> +0x218/+0x220 region).</item>
/// <item><c>RenderTargetManager.Instance()->DepthStencil->D3D11Texture2D</c> is the scene depth, and
/// it is re-fetched EVERY frame because a window resize destroys and recreates it.</item>
/// <item><b>FFXIV is reverse-Z: near = 1, far = 0.</b> So a scene sample GREATER than ours is
/// closer than us, and we are hidden. This is why the shader's test reads the way it does, and it is
/// FFXIV-TV's documented, confirmed-working comparison rather than a convention guessed at here.</item>
/// <item>Depth textures are stored typeless and need a concrete SRV format —
/// <c>R24G8_Typeless → R24_UNorm_X8_Typeless</c>, <c>R32_Typeless → R32_Float</c>,
/// <c>R32G8X24_Typeless → R32_Float_X8X24_Typeless</c>.</item>
/// </list>
///
/// <para><b>Two consequences of drawing real geometry rather than painting quads, and both are
/// deletions:</b> the painter's-algorithm sort is gone, because a depth buffer orders fragments
/// exactly; and back-face culling is gone, because a depth buffer already hides the far side of a
/// box. The rasterizer runs with <c>CullMode.None</c> so there is no winding convention left to get
/// wrong.</para>
///
/// <para><b>Everything is translucent uniformly, at BLIT time, not per fragment.</b> The offscreen
/// pass is fully opaque with blending off, which is what makes it order-independent; the whole city
/// then gets its opacity from the tint on one <c>AddImageQuad</c>. Alpha-blending the geometry
/// instead would reintroduce the sorting problem the depth buffer just removed.</para>
///
/// <para><b>If anything here fails, the feature degrades rather than throwing.</b>
/// <see cref="IsReady"/> goes false, <see cref="LastError"/> says why, and the city falls back to
/// the painted path. A dev experiment must never be the reason the plugin stops drawing.</para>
/// </summary>
internal sealed unsafe class TestCityD3D : IDisposable
{
    /// <summary>Vertex-buffer ceiling. 200k vertices is 5.6 MB and about 66k triangles — far more
    /// than the nearest-64-buildings render set can produce, and a backstop rather than a budget.</summary>
    public const int MaxVertices = 200_000;

    private ID3D11Device?        _device;
    private ID3D11DeviceContext? _context;

    // ── captured game depth ──────────────────────────────────────────────────
    private ID3D11Texture2D?          _depthCopy;
    private ID3D11ShaderResourceView? _depthSrv;
    private uint   _depthW, _depthH;
    private Format _depthFormat = Format.Unknown;

    // ── our own offscreen colour + depth ─────────────────────────────────────
    private ID3D11Texture2D?          _colourTex;
    private ID3D11RenderTargetView?   _colourRtv;
    private ID3D11ShaderResourceView? _colourSrv;
    private ID3D11Texture2D?          _depthTex;
    private ID3D11DepthStencilView?   _depthDsv;
    private uint _rtW, _rtH;

    // ── pipeline ─────────────────────────────────────────────────────────────
    private ID3D11VertexShader?      _vs;
    private ID3D11PixelShader?       _ps;
    private ID3D11InputLayout?       _layout;
    private ID3D11Buffer?            _cbuffer;
    private ID3D11Buffer?            _vertexBuffer;
    private ID3D11DepthStencilState? _depthState;
    private ID3D11BlendState?        _blendOpaque;
    private ID3D11RasterizerState?   _raster;
    private ID3D11SamplerState?      _depthSampler;

    private bool _initialised;
    private bool _failed;

    /// <summary>Why the last attempt failed, for the panel. Empty when nothing has gone wrong.</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>Whether the depth path can draw this frame.</summary>
    public bool IsReady => _initialised && !_failed && _vs != null && _ps != null;

    /// <summary>Whether the game's depth buffer was captured on the last frame. False means the
    /// geometry still draws, but unoccluded — surfaced, because "occlusion silently stopped" is
    /// otherwise indistinguishable from "occlusion is working and nothing is in the way".</summary>
    public bool DepthCaptured { get; private set; }

    /// <summary>
    /// Whether a draw call was actually issued on the last frame.
    ///
    /// <para><b>It means SUBMITTED, and deliberately not "visible".</b> Nothing here inspects the
    /// finished surface, so this cannot and must not be reported as pixels reaching the screen — that
    /// claim is what hid the zero write mask through a whole release. A draw that wrote no colour
    /// channel at all still sets this true, because from the CPU's side it succeeded. Anything the
    /// panel says on the strength of this has to be phrased as what was measured.</para>
    /// </summary>
    public bool DrawSubmitted { get; private set; }

    /// <summary>
    /// DIAGNOSTIC. Clears the offscreen target to opaque magenta, draws no geometry, and blits it at
    /// full opacity.
    ///
    /// <para>A clear does not depend on a shader, a transform, a vertex buffer, a depth test or a
    /// colour-write mask, so magenta on screen isolates one question and answers it completely: does
    /// a surface this class produced reach the screen at all? Black screen ⇒ the whole presentation
    /// route is the fault and no amount of geometry debugging would ever have helped. Magenta ⇒
    /// presentation is fine and the fault is in what we draw into it.</para>
    /// </summary>
    public bool DebugClearOnly;

    public int  VerticesLastFrame { get; private set; }
    public int  TrianglesLastFrame { get; private set; }
    public int  LinesLastFrame { get; private set; }
    public string DepthInfo { get; private set; } = "not captured";

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProj;

        /// <summary>x,y = viewport pixels; z,w = 1/viewport, the texel step the shader samples with.</summary>
        public Vector4 Viewport;

        /// <summary>x = occlude, y = depth valid, z = depth bias, w = compare direction.</summary>
        public Vector4 Options;
    }

    // ── setup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acquires the game's device and builds every pipeline object. Idempotent, and on failure it
    /// latches <see cref="_failed"/> so a broken environment costs one attempt rather than one per
    /// frame.
    /// </summary>
    public bool TryInitialise()
    {
        if (_initialised) return IsReady;
        if (_failed)      return false;

        try
        {
            var kernel = CsDevice.Instance();
            if (kernel == null) { Fail("game device singleton is null"); return false; }

            nint ctxPtr = (nint)kernel->D3D11DeviceContext;
            if (ctxPtr == 0) { Fail("game device context is null"); return false; }

            // AddRef before wrapping: the Vortice wrapper releases on Dispose, and the game owns
            // the underlying object. Without this we would decrement the game's own reference.
            Marshal.AddRef(ctxPtr);
            _context = new ID3D11DeviceContext(ctxPtr);
            _device  = _context.Device;

            CreateShaders();
            CreateState();

            _initialised = true;

            Diag.Info("[City] D3D11 depth renderer initialised.");
            return IsReady;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return false;
        }
    }

    private void CreateShaders()
    {
        var vsCode = Compiler.Compile(ShaderSource, "VS", "testcity_vs", "vs_5_0");
        var psCode = Compiler.Compile(ShaderSource, "PS", "testcity_ps", "ps_5_0");

        _vs = _device!.CreateVertexShader(vsCode.Span);
        _ps = _device!.CreatePixelShader(psCode.Span);

        var elements = new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float,    0,  0),
            new InputElementDescription("COLOR",    0, Format.R32G32B32A32_Float, 12, 0),
        };

        _layout = _device.CreateInputLayout(elements, vsCode.Span);
    }

    private void CreateState()
    {
        _cbuffer = _device!.CreateBuffer(new BufferDescription
        {
            ByteWidth      = (uint)((Marshal.SizeOf<Constants>() + 15) / 16 * 16),
            Usage          = ResourceUsage.Default,
            BindFlags      = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
        });

        _vertexBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth      = MaxVertices * CityVertex.Stride,
            Usage          = ResourceUsage.Dynamic,
            BindFlags      = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        });

        // REVERSE-Z, and every part of this follows from that: the game's projection puts the near
        // plane at 1 and the far plane at 0, so a nearer fragment has the GREATER value. Hence
        // Greater rather than Less, and hence clearing to 0 rather than 1.
        _depthState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable    = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc      = ComparisonFunction.Greater,
            StencilEnable  = false,
        });

        // Opaque. The offscreen pass must be order-independent, which is exactly what a depth
        // buffer with blending OFF gives; the city's translucency is applied once, at blit time.
        //
        // RenderTarget[0] IS ASSIGNED EXPLICITLY, AND THAT IS NOT CEREMONY. Vortice's
        // BlendDescription has no parameterless constructor (verified against the installed 3.8.2
        // assembly: only (Blend, Blend) and (Blend, Blend, Blend, Blend) exist), so an object
        // initializer leaves the struct zeroed — which means RenderTargetWriteMask is
        // ColorWriteEnable.None and the pass cannot write a single colour channel. That is a legal,
        // silent value: no exception, no warning, a draw call that reports success and produces a
        // blank surface. It shipped that way in 0.84.54.9. Disabling BLENDING and disabling COLOUR
        // WRITES are different settings, and only the first one was ever wanted.
        var opaque = new BlendDescription
        {
            AlphaToCoverageEnable  = false,
            IndependentBlendEnable = false,
        };

        opaque.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable           = false,
            SourceBlend           = Blend.One,
            DestinationBlend      = Blend.Zero,
            BlendOperation        = BlendOperation.Add,
            SourceBlendAlpha      = Blend.One,
            DestinationBlendAlpha = Blend.Zero,
            BlendOperationAlpha   = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };

        _blendOpaque = _device.CreateBlendState(opaque);

        // CullMode.None on purpose. A depth buffer already hides the far side of a closed box, so
        // there is no winding convention left to get backwards — which was the single most
        // dangerous thing about the painted renderer.
        _raster = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode        = FillMode.Solid,
            CullMode        = CullMode.None,
            DepthClipEnable = true,
        });

        _depthSampler = _device.CreateSamplerState(new SamplerDescription
        {
            // POINT, not linear. Depth is not a colour — interpolating between two depth samples
            // invents a surface that is at neither distance, and along a silhouette edge that
            // invented value is wrong by the whole gap between the two things.
            Filter         = Filter.MinMagMipPoint,
            AddressU       = TextureAddressMode.Clamp,
            AddressV       = TextureAddressMode.Clamp,
            AddressW       = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD         = 0,
            MaxLOD         = float.MaxValue,
        });
    }

    private void Fail(string message)
    {
        _failed   = true;
        LastError = message;
        Diag.Error($"[City] D3D11 depth renderer unavailable: {message}");
    }

    // ── per-frame resources ──────────────────────────────────────────────────

    /// <summary>Our offscreen colour target and the depth buffer that goes with it, at viewport
    /// size. Rebuilt only when the viewport changes.</summary>
    private bool EnsureTargets(uint w, uint h)
    {
        if (_colourTex != null && _rtW == w && _rtH == h) return true;

        _colourSrv?.Dispose(); _colourSrv = null;
        _colourRtv?.Dispose(); _colourRtv = null;
        _colourTex?.Dispose(); _colourTex = null;
        _depthDsv?.Dispose();  _depthDsv  = null;
        _depthTex?.Dispose();  _depthTex  = null;

        try
        {
            _colourTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width             = w,
                Height            = h,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Default,
                BindFlags         = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });

            _colourRtv = _device.CreateRenderTargetView(_colourTex);
            _colourSrv = _device.CreateShaderResourceView(_colourTex);

            _depthTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width             = w,
                Height            = h,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = Format.D32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Default,
                BindFlags         = BindFlags.DepthStencil,
            });

            _depthDsv = _device.CreateDepthStencilView(_depthTex);

            _rtW = w;
            _rtH = h;

            Diag.Info($"[City] D3D11 targets {w}×{h}.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"target create failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Copies the game's depth-stencil into a texture we can sample.
    ///
    /// <para><b>The source pointer is re-fetched every frame</b>, not cached: a window resize
    /// destroys and recreates the game's depth texture, and a cached pointer would then be a stale
    /// COM object. Copied rather than sampled in place because the game may still have that texture
    /// bound as a depth-stencil view, and D3D11 refuses to read a resource that is simultaneously
    /// bound for writing — it unbinds it and the reads come back as zero.</para>
    /// </summary>
    private bool TryCaptureDepth()
    {
        try
        {
            var rtm = CsRtm.Instance();
            if (rtm == null) return false;

            var texture = rtm->DepthStencil;
            if (texture == null) return false;

            nint srcPtr = (nint)texture->D3D11Texture2D;
            if (srcPtr == 0) return false;

            Marshal.AddRef(srcPtr);
            using var src = new ID3D11Texture2D(srcPtr);
            var desc = src.Description;

            if (_depthCopy == null || _depthW != desc.Width || _depthH != desc.Height
                                   || _depthFormat != desc.Format)
            {
                _depthSrv?.Dispose();  _depthSrv  = null;
                _depthCopy?.Dispose(); _depthCopy = null;

                _depthCopy = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width             = desc.Width,
                    Height            = desc.Height,
                    MipLevels         = 1,
                    ArraySize         = 1,

                    // The TYPELESS format is preserved so both a DSV and an SRV can be made from
                    // it. A concrete depth format could not also be read by a shader.
                    Format            = desc.Format,
                    SampleDescription = desc.SampleDescription,
                    Usage             = ResourceUsage.Default,
                    BindFlags         = BindFlags.DepthStencil | BindFlags.ShaderResource,
                });

                var srvFormat = desc.Format switch
                {
                    Format.R24G8_Typeless    => Format.R24_UNorm_X8_Typeless,
                    Format.D24_UNorm_S8_UInt => Format.R24_UNorm_X8_Typeless,
                    Format.R32_Typeless      => Format.R32_Float,
                    Format.D32_Float         => Format.R32_Float,
                    Format.R32G8X24_Typeless => Format.R32_Float_X8X24_Typeless,
                    _                        => desc.Format,
                };

                _depthSrv = _device.CreateShaderResourceView(_depthCopy,
                    new ShaderResourceViewDescription
                    {
                        Format        = srvFormat,
                        ViewDimension = ShaderResourceViewDimension.Texture2D,
                        Texture2D     = new Texture2DShaderResourceView
                        {
                            MipLevels       = 1,
                            MostDetailedMip = 0,
                        },
                    });

                _depthW      = desc.Width;
                _depthH      = desc.Height;
                _depthFormat = desc.Format;
                DepthInfo    = $"{desc.Width}×{desc.Height} {desc.Format} → {srvFormat}";

                Diag.Info($"[City] depth capture: {DepthInfo}");
            }

            _context!.CopyResource(_depthCopy, src);
            return true;
        }
        catch (Exception ex)
        {
            // Per-frame, so never logged — it would fill the log at 60 Hz. The panel shows it.
            LastError = $"depth capture failed: {ex.Message}";
            return false;
        }
    }

    // ── the frame ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the mesh and blits it over the world. Returns false if anything was not ready, so the
    /// caller can fall back to the painted path in the same frame.
    /// </summary>
    /// <param name="triangleCount">Vertices at the start of <paramref name="vertices"/> that form
    /// triangles. Everything after them is drawn as lines, in one buffer and two draw calls.</param>
    public bool Render(ReadOnlySpan<CityVertex> vertices, int triangleVertexCount,
                       Matrix4x4 viewProj, bool occlude, float depthBias, float opacity)
    {
        if (!IsReady) return false;

        // Every per-frame count is cleared BEFORE any early return, not just the one the old
        // zero-vertex path happened to touch. A stale triangle count surviving a frame that drew
        // nothing is a counter that lies in exactly the direction nobody checks.
        VerticesLastFrame  = 0;
        TrianglesLastFrame = 0;
        LinesLastFrame     = 0;
        DrawSubmitted      = false;

        if (vertices.Length == 0 && !DebugClearOnly) return true;

        try
        {
            var io  = ImGui.GetIO();
            uint vpW = (uint)io.DisplaySize.X;
            uint vpH = (uint)io.DisplaySize.Y;

            if (vpW == 0 || vpH == 0) return false;
            if (!EnsureTargets(vpW, vpH)) return false;

            // Presentation probe: clear, blit, and skip everything in between. Deliberately placed
            // before the vertex upload and the constant buffer so that not one of those steps can
            // affect the result — a probe that shares setup with the thing it is testing proves less
            // than it appears to.
            if (DebugClearOnly)
            {
                Draw(0, 0, vpW, vpH);
                Blit(vpW, vpH, 1f);
                return true;
            }

            DepthCaptured = occlude && TryCaptureDepth();

            int count = Math.Min(vertices.Length, MaxVertices);

            UploadVertices(vertices[..count]);

            var constants = new Constants
            {
                ViewProj = viewProj,
                Viewport = new Vector4(vpW, vpH, 1f / vpW, 1f / vpH),
                Options  = new Vector4(occlude ? 1f : 0f, DepthCaptured ? 1f : 0f, depthBias, 0f),
            };

            _context!.UpdateSubresource(constants, _cbuffer!);

            int triVerts  = Math.Clamp(triangleVertexCount, 0, count);
            int lineVerts = count - triVerts;

            Draw(triVerts, lineVerts, vpW, vpH);

            VerticesLastFrame  = count;
            TrianglesLastFrame = triVerts / 3;
            LinesLastFrame     = lineVerts / 2;
            DrawSubmitted      = triVerts >= 3 || lineVerts >= 2;

            Blit(vpW, vpH, opacity);

            return true;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return false;
        }
    }

    private void UploadVertices(ReadOnlySpan<CityVertex> vertices)
    {
        // WriteDiscard: the whole buffer is replaced each frame, and telling the driver so lets it
        // hand back fresh memory instead of stalling until the GPU has finished with the old.
        var mapped = _context!.Map(_vertexBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);

        try
        {
            fixed (CityVertex* src = vertices)
            {
                long bytes = (long)vertices.Length * CityVertex.Stride;
                Buffer.MemoryCopy(src, (void*)mapped.DataPointer, bytes, bytes);
            }
        }
        finally
        {
            _context.Unmap(_vertexBuffer!, 0);
        }
    }

    private void Draw(int triVerts, int lineVerts, uint vpW, uint vpH)
    {
        var ctx = _context!;

        // Saved and restored around our work. The game and Dalamud's ImGui renderer both set their
        // own state, but leaving someone else's render target bound is the kind of thing that shows
        // up as a corrupted frame three plugins away.
        var savedRtv = new ID3D11RenderTargetView[1];
        ctx.OMGetRenderTargets(1, savedRtv, out var savedDsv);

        try
        {
            ctx.ClearRenderTargetView(_colourRtv!,
                DebugClearOnly ? new Color4(1f, 0f, 1f, 1f)     // diagnostic: opaque magenta
                               : new Color4(0f, 0f, 0f, 0f));

            // Cleared to ZERO because of reverse-Z: 0 is the far plane. Clearing to 1 would put the
            // whole buffer at the near plane and nothing would ever pass the Greater test.
            ctx.ClearDepthStencilView(_depthDsv!, DepthStencilClearFlags.Depth, 0f, 0);

            ctx.OMSetRenderTargets(_colourRtv!, _depthDsv);
            ctx.OMSetDepthStencilState(_depthState);
            ctx.OMSetBlendState(_blendOpaque, new Color4(0f, 0f, 0f, 0f), 0xFFFFFFFF);

            ctx.RSSetViewport(0, 0, vpW, vpH, 0f, 1f);
            ctx.RSSetState(_raster);

            ctx.IASetInputLayout(_layout);
            ctx.IASetVertexBuffer(0, _vertexBuffer!, CityVertex.Stride, 0);
            ctx.IASetIndexBuffer(null, Format.Unknown, 0);

            ctx.VSSetShader(_vs);
            ctx.VSSetConstantBuffer(0, _cbuffer);

            ctx.PSSetShader(_ps);
            ctx.PSSetConstantBuffer(0, _cbuffer);
            ctx.PSSetSampler(0, _depthSampler);

            // A null SRV is a legal unbind in D3D11 — the binding's signature simply is not
            // annotated for it. When the capture failed this leaves slot 0 empty, which is safe
            // because the shader gates its sample on Options.y rather than on the slot's contents.
            ctx.PSSetShaderResource(0, _depthSrv!);

            if (triVerts >= 3)
            {
                ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                ctx.Draw((uint)triVerts, 0);
            }

            if (lineVerts >= 2)
            {
                ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
                ctx.Draw((uint)lineVerts, (uint)triVerts);
            }
        }
        finally
        {
            // The depth SRV must be unbound before the next frame rebinds that same resource as a
            // depth-stencil view — a resource cannot be readable and writable at once, and D3D11
            // resolves the conflict by silently unbinding, which reads as occlusion breaking.
            ctx.PSSetShaderResource(0, null!);

            ctx.OMSetRenderTargets(savedRtv, savedDsv);

            savedRtv[0]?.Dispose();
            savedDsv?.Dispose();
        }
    }

    /// <summary>
    /// Hands the finished surface to ImGui, on the background draw list — over the game, under every
    /// ImGui window, which is exactly where the painted city was.
    ///
    /// <para><c>AddImageQuad</c> rather than <c>AddImage</c>: Dalamud's binding types the latter's
    /// texture argument in a way that does not accept a raw pointer as cleanly, and four identity
    /// corners give the same 1:1 blit. FFXIV-TV's <c>CopyBlitRenderer</c> notes the same thing.</para>
    /// </summary>
    private void Blit(uint vpW, uint vpH, float opacity)
    {
        if (_colourSrv == null) return;

        var list  = ImGui.GetBackgroundDrawList();
        var texId = new ImTextureID(_colourSrv.NativePointer);

        // The city's opacity, applied ONCE to the whole surface. The geometry pass is opaque so it
        // can be order-independent; this is where translucency happens.
        byte a = (byte)Math.Clamp((int)(opacity * 255f), 0, 255);
        uint tint = 0x00FFFFFFu | ((uint)a << 24);

        list.AddImageQuad(texId,
                          new Vector2(0f, 0f), new Vector2(vpW, 0f),
                          new Vector2(vpW, vpH), new Vector2(0f, vpH),
                          new Vector2(0f, 0f), new Vector2(1f, 0f),
                          new Vector2(1f, 1f), new Vector2(0f, 1f),
                          tint);
    }

    // ── the game's view-projection ───────────────────────────────────────────

    /// <summary>
    /// The game's own view and projection matrices, multiplied.
    ///
    /// <para><b>Using the game's matrices rather than building our own is what makes the depth
    /// comparison exact.</b> A fragment transformed by this ViewProj lands at the same clip depth
    /// the game wrote into the depth buffer for the same world point, so the shader compares two
    /// numbers in one space — no linearisation, no near/far reconstruction, no reverse-Z guesswork.
    /// Rebuilding a projection from FoV and aspect would be a second opinion about the same thing,
    /// and any disagreement would show up as occlusion that is subtly wrong at distance.</para>
    ///
    /// <para>Field reads with a null check per hop, as everywhere else in this feature:
    /// <c>CameraManager → Camera → SceneCamera.RenderCamera</c>, whose <c>ViewMatrix</c> sits at
    /// +0x10 and <c>ProjectionMatrix</c> at +0x1A0.</para>
    /// </summary>
    public static bool TryViewProj(out Matrix4x4 viewProj)
    {
        viewProj = Matrix4x4.Identity;

        var manager = CameraManager.Instance();
        if (manager == null) return false;

        var camera = manager->Camera;
        if (camera == null) return false;

        var render = camera->SceneCamera.RenderCamera;
        if (render == null) return false;

        var view = render->ViewMatrix;
        var proj = render->ProjectionMatrix;

        viewProj = Matrix4x4.Multiply(
            new Matrix4x4(view.M11, view.M12, view.M13, view.M14,
                          view.M21, view.M22, view.M23, view.M24,
                          view.M31, view.M32, view.M33, view.M34,
                          view.M41, view.M42, view.M43, view.M44),
            new Matrix4x4(proj.M11, proj.M12, proj.M13, proj.M14,
                          proj.M21, proj.M22, proj.M23, proj.M24,
                          proj.M31, proj.M32, proj.M33, proj.M34,
                          proj.M41, proj.M42, proj.M43, proj.M44));

        return true;
    }

    // ── shader ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Compiled at runtime rather than shipped as bytecode: this is dev-only and a <c>.cso</c> would
    /// be a build step plus a file to keep in step with the source beside it.
    /// </summary>
    private const string ShaderSource = @"
cbuffer Constants : register(b0)
{
    // row_major IS LOAD-BEARING AND ITS ABSENCE IS SILENT. HLSL packs a cbuffer matrix
    // COLUMN-major unless told otherwise, while .NET's Matrix4x4 is laid out row by row — so an
    // unqualified declaration reads our row i as its column i and the shader computes transpose(M)
    // times v instead of v times M. Verified by disassembling this exact source through this exact
    // Compiler.Compile overload: unqualified emits four dp4s against consecutive cbuffer registers
    // (a column-vector product), row_major emits the mul/mad chain (the row-vector product FFXIV's
    // matrices are built for). Nothing errors; every vertex simply lands somewhere meaningless, and
    // clip.w goes with it, which takes the depth comparison down too. FFXIV-TV's confirmed-working
    // CopyBlitRenderer declares row_major for the same reason.
    //
    // DO NOT ALSO TRANSPOSE ON THE CPU. Both together is the same bug again, spelled twice.
    row_major float4x4 ViewProj;
    float4   Viewport;   // xy = pixels, zw = 1/pixels
    float4   Options;    // x = occlude, y = depth valid, z = bias, w = spare
};

Texture2D    DepthTexture : register(t0);
SamplerState DepthSampler : register(s0);

struct VS_IN
{
    float3 pos : POSITION;
    float4 col : COLOR0;
};

struct VS_OUT
{
    float4 pos  : SV_Position;
    float4 col  : COLOR0;
    float4 clip : TEXCOORD0;
};

VS_OUT VS(VS_IN input)
{
    VS_OUT o;
    o.pos  = mul(float4(input.pos, 1.0f), ViewProj);
    o.clip = o.pos;
    o.col  = input.col;
    return o;
}

float4 PS(VS_OUT input) : SV_Target
{
    if (Options.x > 0.5f && Options.y > 0.5f)
    {
        // Our own clip depth, in exactly the space the game's depth buffer holds: both came
        // through the same ViewProj, so this is a comparison rather than a conversion.
        float w = max(input.clip.w, 1e-6f);
        float myDepth = input.clip.z / w;

        float2 uv = input.pos.xy * Viewport.zw;
        float sceneDepth = DepthTexture.SampleLevel(DepthSampler, uv, 0).r;

        // REVERSE-Z: near = 1, far = 0, so a GREATER scene sample is closer to the camera than
        // we are, and we are behind the world. The bias keeps a wall that stands exactly on the
        // terrain from fighting with it along its base.
        if (sceneDepth > myDepth + Options.z) discard;
    }

    // Premultiplied, so the offscreen surface composites correctly when ImGui blits it.
    return float4(input.col.rgb * input.col.a, input.col.a);
}
";

    // ── teardown ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Releases every resource we created.
    ///
    /// <para><b>Managed COM releases only — no game code is called from here</b>, which matters
    /// because unload runs on every rebuild, at a moment the player did not choose (BROKEN.md 012).
    /// The device and context are released too, but only the reference this class took with
    /// <c>Marshal.AddRef</c>; the game's own reference is untouched.</para>
    /// </summary>
    public void Dispose()
    {
        try
        {
            _depthSampler?.Dispose();
            _raster?.Dispose();
            _blendOpaque?.Dispose();
            _depthState?.Dispose();
            _vertexBuffer?.Dispose();
            _cbuffer?.Dispose();
            _layout?.Dispose();
            _ps?.Dispose();
            _vs?.Dispose();

            _depthDsv?.Dispose();
            _depthTex?.Dispose();
            _colourSrv?.Dispose();
            _colourRtv?.Dispose();
            _colourTex?.Dispose();

            _depthSrv?.Dispose();
            _depthCopy?.Dispose();

            _context?.Dispose();
        }
        catch (Exception ex)
        {
            Diag.Error($"[City] D3D11 teardown failed: {ex.Message}");
        }

        _initialised = false;
    }
}
#endif
