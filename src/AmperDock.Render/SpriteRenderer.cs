using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DXGI.AlphaMode;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace AmperDock.Render;

/// <summary>One sprite instance for a frame: centre, size (px), rotation (radians), horizontal squash, opacity.</summary>
public readonly record struct SpriteDraw(string Key, float X, float Y, float Size, float Rotation, float ScaleX, float Opacity);

/// <summary>
/// Transparent full-window sprite layer (the cursor trail). Same GPU path as <see cref="DockRenderer"/>:
/// D3D11 → Direct2D → premultiplied composition swap chain → DirectComposition visual on the HWND.
/// Draws nothing and costs nothing between frames.
/// </summary>
public sealed class SpriteRenderer : IDisposable
{
    private readonly nint _hwnd;
    private int _width, _height;
    private ID3D11Device? _d3d;
    private IDXGIDevice? _dxgi;
    private ID2D1Factory1? _factory;
    private ID2D1Device? _device;
    private ID2D1DeviceContext? _ctx;
    private IDXGISwapChain1? _swap;
    private ID2D1Bitmap1? _target;
    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _dcompTarget;
    private IDCompositionVisual? _visual;
    private readonly Dictionary<string, ID2D1Bitmap1> _sprites = new();
    private readonly Dictionary<string, (int w, int h, byte[] px)> _source = new();
    private bool _cleared;

    public SpriteRenderer(nint hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        Create();
    }

    private void Create()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var hr = D3D11.D3D11CreateDevice((IDXGIAdapter?)null, Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out _d3d, out _, out var imm);
        if (hr.Failure)
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, Vortice.Direct3D.DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
                out _d3d, out _, out imm).CheckError();
        imm?.Dispose();
        _dxgi = _d3d!.QueryInterface<IDXGIDevice>();
        _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        _device = _factory.CreateDevice(_dxgi);
        _ctx = _device.CreateDeviceContext(DeviceContextOptions.None);
        _ctx.UnitMode = UnitMode.Pixels;

        using var adapter = _dxgi.GetAdapter();
        using var dxgiFactory = adapter.GetParent<IDXGIFactory2>();
        _swap = dxgiFactory.CreateSwapChainForComposition(_d3d, new SwapChainDescription1
        {
            Width = (uint)_width, Height = (uint)_height, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied,
        });
        BindTarget();

        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgi);
        _dcomp.CreateTargetForHwnd(_hwnd, true, out _dcompTarget).CheckError();
        _dcomp.CreateVisual(out _visual).CheckError();
        _visual!.SetContent(_swap);
        _dcompTarget!.SetRoot(_visual);
        _dcomp.Commit();
        foreach (var (k, s) in _source) _sprites[k] = Upload(s.w, s.h, s.px);
        _cleared = false;
    }

    private void BindTarget()
    {
        using var surface = _swap!.GetBuffer<IDXGISurface>(0);
        _target = _ctx!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
        _ctx.Target = _target;
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height) return;
        (_width, _height) = (width, height);
        _ctx!.Target = null;
        _target?.Dispose();
        _swap!.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        BindTarget();
        _cleared = false;
    }

    public bool HasSprite(string key) => _sprites.ContainsKey(key);

    /// <summary>Upload premultiplied BGRA pixels as a sprite.</summary>
    public void SetSprite(string key, int width, int height, byte[] bgra)
    {
        if (_sprites.Remove(key, out var old)) old.Dispose();
        _source[key] = (width, height, bgra);
        _sprites[key] = Upload(width, height, bgra);
    }

    private unsafe ID2D1Bitmap1 Upload(int w, int h, byte[] bgra)
    {
        fixed (byte* p = bgra)
            return _ctx!.CreateBitmap(new SizeI(w, h), (nint)p, (uint)(w * 4),
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
    }

    /// <summary>Draw one frame. An empty list clears the layer once and then does nothing.</summary>
    public void Draw(IReadOnlyList<SpriteDraw> sprites)
    {
        if (sprites.Count == 0 && _cleared) return;
        var ctx = _ctx!;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));
        foreach (var s in sprites)
        {
            if (!_sprites.TryGetValue(s.Key, out var bmp)) continue;
            var half = s.Size / 2;
            ctx.Transform = Matrix3x2.CreateScale(s.ScaleX, 1, Vector2.Zero)
                            * Matrix3x2.CreateRotation(s.Rotation)
                            * Matrix3x2.CreateTranslation(s.X, s.Y);
            ctx.DrawBitmap(bmp, new Rect(-half, -half, s.Size, s.Size), Math.Clamp(s.Opacity, 0, 1), InterpolationMode.Linear, null, null);
        }
        ctx.Transform = Matrix3x2.Identity;
        var hr = ctx.EndDraw();
        var present = _swap!.Present(1, PresentFlags.None);
        _cleared = sprites.Count == 0;
        if (hr.Failure || present.Failure) { Release(); Create(); }
    }

    private void Release()
    {
        foreach (var b in _sprites.Values) b.Dispose();
        _sprites.Clear();
        if (_ctx is not null) _ctx.Target = null;
        _target?.Dispose(); _visual?.Dispose(); _dcompTarget?.Dispose(); _dcomp?.Dispose();
        _swap?.Dispose(); _ctx?.Dispose(); _device?.Dispose(); _factory?.Dispose(); _dxgi?.Dispose(); _d3d?.Dispose();
    }

    public void Dispose() => Release();
}
