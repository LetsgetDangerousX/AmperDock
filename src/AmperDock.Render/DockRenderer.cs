using System.Numerics;
using AmperDock.Core;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using AlphaMode = Vortice.DXGI.AlphaMode;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using PixelFormat = Vortice.DCommon.PixelFormat;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;
using RectangleF = System.Drawing.RectangleF;

namespace AmperDock.Render;

/// <summary>Colours as straight-alpha 0xAARRGGBB, converted to Color4 at draw time.</summary>
public readonly record struct Argb(uint Value)
{
    public static Argb FromRgba(string hex)   // "#RRGGBB" or "#RRGGBBAA" (tokens.json format)
    {
        var h = hex.TrimStart('#');
        var rgb = Convert.ToUInt32(h[..6], 16);
        var a = h.Length >= 8 ? Convert.ToUInt32(h[6..8], 16) : 0xFF;
        return new Argb((a << 24) | rgb);
    }
    public byte Alpha => (byte)(Value >> 24);

    /// <summary>The same colour with its alpha multiplied by <paramref name="k"/> (0..1).</summary>
    public Argb WithAlphaScaled(float k) => new(((uint)Math.Clamp(MathF.Round(Alpha * k), 0, 255) << 24) | (Value & 0xFFFFFF));

    /// <summary>The same RGB with an absolute alpha (0..1).</summary>
    public Argb WithAlpha(float a) => new(((uint)Math.Clamp(MathF.Round(a * 255), 0, 255) << 24) | (Value & 0xFFFFFF));

    internal Color4 ToColor4(float opacity = 1) => new(
        ((Value >> 16) & 0xFF) / 255f, ((Value >> 8) & 0xFF) / 255f, (Value & 0xFF) / 255f, ((Value >> 24) & 0xFF) / 255f * opacity);
}

/// <summary>
/// Paint for one dock. BackgroundEnd set = a linear gradient from Background to BackgroundEnd at GradientAngle
/// (degrees, 0 = left to right, 90 = top to bottom). OutlineWidth is in DIPs.
/// </summary>
public sealed record DockStyle(Argb Background, Argb Outline, Argb Highlight, Argb Indicator, Argb IndicatorFocused,
    Argb LabelBg, Argb LabelText, Argb Shadow, Argb? BackgroundEnd = null, float GradientAngle = 0, float OutlineWidth = 1);

public readonly record struct IconDraw(string? IconKey, RectF Rect, float Opacity, bool Missing);
public readonly record struct IndicatorDraw(float CenterX, float CenterY, float Size, bool Focused);
/// <summary>Which side of the anchor point the label pill sits on.</summary>
public enum LabelSide { Above, Below, Right, Left }

/// <summary>A label pill. (X, Y) is the point it attaches to; Side says where the pill goes from there.</summary>
public sealed record LabelDraw(string Text, float X, float Y, LabelSide Side = LabelSide.Above);

/// <summary>Everything one frame needs. Built by the app from DockLayout each frame.</summary>
public sealed record DockScene(RectF Background, float Radius, IReadOnlyList<IconDraw> Icons,
    IReadOnlyList<IndicatorDraw> Indicators, LabelDraw? Label, float Scale);

/// <summary>
/// GPU renderer for one dock window: D3D11 device → Direct2D device context → a premultiplied-alpha
/// composition swap chain → DirectComposition visual on the HWND. Per-pixel transparency without
/// layered-window copies, and nothing runs between frames.
/// </summary>
public sealed class DockRenderer : IDisposable
{
    private readonly nint _hwnd;
    private int _width, _height;

    private ID3D11Device? _d3d;
    private IDXGIDevice? _dxgi;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _ctx;
    private IDXGISwapChain1? _swap;
    private ID2D1Bitmap1? _target;
    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _dcompTarget;
    private IDCompositionVisual? _visual;
    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _labelFormat;
    private float _labelFormatScale;
    private readonly Dictionary<string, ID2D1Bitmap1> _icons = new();
    private readonly Dictionary<string, (int w, int h, byte[] px)> _iconSource = new();

    public DockRenderer(nint hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        CreateDevice();
    }

    private void CreateDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var hr = D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels,
            out _d3d, out _, out var imm);
        if (hr.Failure)   // no usable GPU (remote desktop, broken driver): fall back to WARP, the software rasteriser
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels,
                out _d3d, out _, out imm).CheckError();
        imm?.Dispose();

        _dxgi = _d3d!.QueryInterface<IDXGIDevice>();
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(_dxgi);
        _ctx = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _ctx.UnitMode = UnitMode.Pixels;   // the app works in device pixels

        using var adapter = _dxgi.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swap = factory.CreateSwapChainForComposition(_d3d, new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
        });
        CreateTarget();

        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgi);
        _dcomp.CreateTargetForHwnd(_hwnd, true, out _dcompTarget).CheckError();
        _dcomp.CreateVisual(out _visual).CheckError();
        _visual!.SetContent(_swap);
        _dcompTarget!.SetRoot(_visual);
        _dcomp.Commit();

        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);

        // re-upload icons after a device loss
        foreach (var (key, src) in _iconSource) _icons[key] = MakeBitmap(src.w, src.h, src.px);
    }

    private void CreateTarget()
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
        _width = width;
        _height = height;
        _ctx!.Target = null;
        _target?.Dispose();
        _swap!.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        CreateTarget();
    }

    public bool HasIcon(string key) => _icons.ContainsKey(key);

    /// <summary>Upload premultiplied BGRA pixels as an icon bitmap under a key.</summary>
    public void SetIcon(string key, int width, int height, byte[] bgra)
    {
        if (_icons.Remove(key, out var old)) old.Dispose();
        _iconSource[key] = (width, height, bgra);
        _icons[key] = MakeBitmap(width, height, bgra);
    }

    private unsafe ID2D1Bitmap1 MakeBitmap(int w, int h, byte[] bgra)
    {
        fixed (byte* p = bgra)
        {
            return _ctx!.CreateBitmap(new SizeI(w, h), (nint)p, (uint)(w * 4), new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        }
    }

    public void Draw(DockScene scene, DockStyle style)
    {
        var ctx = _ctx!;
        ctx.BeginDraw();
        ctx.Clear(new Color4(0, 0, 0, 0));

        DrawBackground(ctx, scene, style);

        foreach (var icon in scene.Icons)
        {
            var r = icon.Rect;
            var dest = new Rect(r.X, r.Y, r.W, r.H);
            if (icon.IconKey is not null && _icons.TryGetValue(icon.IconKey, out var bmp))
                ctx.DrawBitmap(bmp, dest, icon.Opacity * (icon.Missing ? 0.5f : 1f), InterpolationMode.HighQualityCubic, null, null);
            else
                DrawPlaceholder(ctx, r, style, icon.Opacity);
        }

        foreach (var ind in scene.Indicators) DrawPaw(ctx, ind, style);
        if (scene.Label is not null) DrawLabel(ctx, scene.Label, scene.Scale, style);

        var hr = ctx.EndDraw();
        var present = _swap!.Present(1, PresentFlags.None);
        if (hr.Failure || present.Failure) RecoverDevice();
    }

    private static void DrawBackground(ID2D1DeviceContext ctx, DockScene s, DockStyle style)
    {
        var b = s.Background;
        var hasFill = style.Background.Alpha > 0 || style.BackgroundEnd is { Alpha: > 0 };
        if (!hasFill && style.Outline.Alpha == 0) return;   // invisible dock: icons only
        // soft shadow: a few expanding, fading rounded rects (a real blur effect comes with the effects work)
        if (style.Shadow.Alpha > 0)
        using (var shadow = ctx.CreateSolidColorBrush(style.Shadow.WithAlpha(1).ToColor4()))
        {
            for (var i = 1; i <= 4; i++)
            {
                shadow.Opacity = 0.05f / i * (style.Shadow.Alpha / 255f);
                var g = i * 3 * s.Scale;
                ctx.FillRoundedRectangle(new RoundedRectangle(new RectangleF(b.X - g, b.Y - g + 4 * s.Scale, b.W + g * 2, b.H + g * 2), s.Radius + g, s.Radius + g), shadow);
            }
        }
        var rr = new RoundedRectangle(new RectangleF(b.X, b.Y, b.W, b.H), s.Radius, s.Radius);
        using var outline = ctx.CreateSolidColorBrush(style.Outline.ToColor4());
        using var highlight = ctx.CreateSolidColorBrush(style.Highlight.ToColor4());
        if (hasFill)
        {
            using var fill = FillBrush(ctx, style, b);
            ctx.FillRoundedRectangle(rr, fill);
        }
        if (style.Outline.Alpha > 0)
        {
            // keep the stroke inside the band so thick outlines don't get clipped by the window region
            var w = style.OutlineWidth * s.Scale;
            var inner = new RoundedRectangle(new RectangleF(b.X + w / 2, b.Y + w / 2, b.W - w, b.H - w), MathF.Max(0, s.Radius - w / 2), MathF.Max(0, s.Radius - w / 2));
            ctx.DrawRoundedRectangle(inner, outline, w);
        }
        var inset = 1.5f * s.Scale;
        if (style.Highlight.Alpha > 0)
        ctx.DrawLine(new Vector2(b.X + s.Radius, b.Y + inset), new Vector2(b.Right - s.Radius, b.Y + inset), highlight, 1 * s.Scale);
    }

    private static ID2D1Brush FillBrush(ID2D1DeviceContext ctx, DockStyle style, RectF b)
    {
        if (style.BackgroundEnd is not Argb end) return ctx.CreateSolidColorBrush(style.Background.ToColor4());
        // a line through the band's centre at the given angle, long enough to span the rectangle
        var rad = style.GradientAngle * MathF.PI / 180;
        var dir = new Vector2(MathF.Cos(rad), MathF.Sin(rad));
        var half = (MathF.Abs(dir.X) * b.W + MathF.Abs(dir.Y) * b.H) / 2;
        var c = new Vector2(b.CenterX, b.Y + b.H / 2);
        using var stops = ctx.CreateGradientStopCollection(
            [new GradientStop(0, style.Background.ToColor4()), new GradientStop(1, end.ToColor4())], ExtendMode.Clamp);
        return ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(c - dir * half, c + dir * half), stops);
    }

    private static void DrawPlaceholder(ID2D1DeviceContext ctx, RectF r, DockStyle style, float opacity)
    {
        using var brush = ctx.CreateSolidColorBrush(style.Indicator.ToColor4(0.35f * opacity));
        ctx.FillRoundedRectangle(new RoundedRectangle(new RectangleF(r.X, r.Y, r.W, r.H), r.W * 0.24f, r.W * 0.24f), brush);
    }

    /// <summary>The paw-print running indicator. Same geometry as replica/design/assets/paw.svg (24-unit box).</summary>
    private static void DrawPaw(ID2D1DeviceContext ctx, IndicatorDraw ind, DockStyle style)
    {
        using var brush = ctx.CreateSolidColorBrush((ind.Focused ? style.IndicatorFocused : style.Indicator).ToColor4());
        var k = ind.Size / 24f;
        var ox = ind.CenterX - 12 * k;
        var oy = ind.CenterY - 12 * k;
        void Pad(float cx, float cy, float rx, float ry, float deg)
        {
            var c = new Vector2(ox + cx * k, oy + cy * k);
            ctx.Transform = Matrix3x2.CreateRotation(deg * MathF.PI / 180, c);
            ctx.FillEllipse(new Ellipse(c, rx * k, ry * k), brush);
        }
        Pad(12, 16.2f, 5.6f, 4.6f, 0);
        Pad(5.2f, 10.4f, 2.2f, 2.7f, -18);
        Pad(9.4f, 6.2f, 2.3f, 2.9f, -6);
        Pad(14.6f, 6.2f, 2.3f, 2.9f, 6);
        Pad(18.8f, 10.4f, 2.2f, 2.7f, 18);
        ctx.Transform = Matrix3x2.Identity;
    }

    private void DrawLabel(ID2D1DeviceContext ctx, LabelDraw label, float scale, DockStyle style)
    {
        if (_labelFormat is null || Math.Abs(_labelFormatScale - scale) > 0.01f)
        {
            _labelFormat?.Dispose();
            _labelFormat = _dwrite!.CreateTextFormat("Segoe UI Variable Text", null, FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal,
                FontStretch.Normal, 13 * scale, "en-us");
            _labelFormatScale = scale;
        }
        using var layout = _dwrite!.CreateTextLayout(label.Text, _labelFormat, 600 * scale, 100 * scale);
        var m = layout.Metrics;
        float padX = 10 * scale, padY = 4 * scale;
        var w = m.Width + padX * 2;
        var h = m.Height + padY * 2;
        var (x, y) = label.Side switch
        {
            LabelSide.Below => (label.X - w / 2, label.Y),
            LabelSide.Right => (label.X, label.Y - h / 2),
            LabelSide.Left => (label.X - w, label.Y - h / 2),
            _ => (label.X - w / 2, label.Y - h),
        };
        x = Math.Clamp(x, 2, Math.Max(2, _width - w - 2));
        y = Math.Clamp(y, 2, Math.Max(2, _height - h - 2));
        using var bg = ctx.CreateSolidColorBrush(style.LabelBg.ToColor4());
        using var fg = ctx.CreateSolidColorBrush(style.LabelText.ToColor4());
        ctx.FillRoundedRectangle(new RoundedRectangle(new RectangleF(x, y, w, h), h / 2, h / 2), bg);
        ctx.DrawTextLayout(new Vector2(x + padX, y + padY), layout, fg, DrawTextOptions.EnableColorFont);
    }

    private void RecoverDevice()
    {
        ReleaseDevice();
        CreateDevice();
    }

    private void ReleaseDevice()
    {
        foreach (var b in _icons.Values) b.Dispose();
        _icons.Clear();
        _labelFormat?.Dispose(); _labelFormat = null;
        if (_ctx is not null) _ctx.Target = null;
        _target?.Dispose(); _visual?.Dispose(); _dcompTarget?.Dispose(); _dcomp?.Dispose();
        _swap?.Dispose(); _ctx?.Dispose(); _d2dDevice?.Dispose(); _d2dFactory?.Dispose(); _dxgi?.Dispose(); _d3d?.Dispose();
        _dwrite?.Dispose();
    }

    public void Dispose() => ReleaseDevice();
}
