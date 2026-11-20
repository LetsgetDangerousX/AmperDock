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
    Argb LabelBg, Argb LabelText, Argb Shadow, Argb? BackgroundEnd = null, float GradientAngle = 0, float OutlineWidth = 1,
    IndicatorKind IndicatorKind = IndicatorKind.Paw, string? IndicatorSprite = null);

/// <summary>How running apps are marked: a drawn paw, a drawn dot, or a little picture (a sprite key).</summary>
public enum IndicatorKind { Paw, Dot, Sprite }

/// <summary>The band's outline.</summary>
public enum BandShape { Rounded, Leaf }

/// <summary>
/// A decoration picture. (CenterX, CenterY) and the size are in window pixels; Orient is the 2x2 part
/// of the transform that turns the art to face away from the screen edge (identity for upright art).
/// Behind = drawn under the band, so it peeks out from behind it.
/// </summary>
public readonly record struct DecorDraw(string Key, float CenterX, float CenterY, float Width, float Height, Matrix3x2 Orient, bool Behind, float Opacity = 1);

/// <summary>Shapes that grow out of the band itself, painted with its paint and sharing its outline.</summary>
public enum AttachKind
{
    Cup,    // the far edge swells into a round cup; a planet sits down inside it
    Ring,   // a planet at one end, its ring passing behind it; the band is the front of the ring
    Moon,   // one end of the band flows into a crescent moon
}

/// <summary>
/// One attachment. Along = where its centre is, in band heights from the chosen end; Size = its radius
/// in band heights; Sprite = the picture (planet, or the moon's face); Color = the moon's paint.
/// </summary>
public readonly record struct Attachment(AttachKind Kind, bool AtEnd, float Along, float Size, string? Sprite, Argb Color = default);

public readonly record struct IconDraw(string? IconKey, RectF Rect, float Opacity, bool Missing, bool NoReflection = false);
public readonly record struct IndicatorDraw(float CenterX, float CenterY, float Size, bool Focused);
/// <summary>Which side of the anchor point the label pill sits on.</summary>
public enum LabelSide { Above, Below, Right, Left }

/// <summary>A label pill. (X, Y) is the point it attaches to; Side says where the pill goes from there.</summary>
public sealed record LabelDraw(string Text, float X, float Y, LabelSide Side = LabelSide.Above);

/// <summary>Everything one frame needs. Built by the app from DockLayout each frame.</summary>
/// <remarks>
/// Edge says which way "away from the screen" points, so shapes and toppings (ears, snow) face the
/// right way on every side. Ears and Snow are drawn with the band's own paint, so they follow its colour.
/// </remarks>
public sealed record DockScene(RectF Background, float Radius, IReadOnlyList<IconDraw> Icons,
    IReadOnlyList<IndicatorDraw> Indicators, LabelDraw? Label, float Scale,
    DockEdge Edge = DockEdge.Bottom, BandShape Shape = BandShape.Rounded, bool Ears = false, bool Snow = false,
    IReadOnlyList<DecorDraw>? Decor = null, IReadOnlyList<Attachment>? Attach = null, float Reflection = 0);

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
    private ID2D1StrokeStyle? _round;
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
        _round = _d2dFactory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });

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
        _visual.SetOffsetX(_offsetX);
        _visual.SetOffsetY(_offsetY);
        _dcompTarget!.SetRoot(_visual);
        _dcomp.Commit();

        _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);

        // re-upload icons after a device loss
        foreach (var (key, src) in _iconSource) _icons[key] = MakeBitmap(src.w, src.h, Tinted(key, src.px));
    }

    private void CreateTarget()
    {
        using var surface = _swap!.GetBuffer<IDXGISurface>(0);
        _target = _ctx!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw));
        _ctx.Target = _target;
    }

    private float _offsetX, _offsetY;

    /// <summary>
    /// Slide everything drawn by (x, y) pixels (auto-hide). DirectComposition moves the finished picture,
    /// so sliding costs no redraw; whatever leaves the window is simply clipped.
    /// </summary>
    public void SetOffset(float x, float y)
    {
        if (x == _offsetX && y == _offsetY) return;
        (_offsetX, _offsetY) = (x, y);
        if (_visual is null || _dcomp is null) return;
        _visual.SetOffsetX(x);
        _visual.SetOffsetY(y);
        _dcomp.Commit();
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
        _icons[key] = MakeBitmap(width, height, Tinted(key, bgra));
    }

    private Argb _tint;
    private float _tintAmount;

    /// <summary>
    /// Wash every app icon toward one colour (matching the wallpaper), by <paramref name="amount"/> 0..1.
    /// Lightness is kept, so icons stay recognisable; theme pictures are left alone.
    /// </summary>
    public void SetIconTint(Argb tint, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        if (tint.Alpha == 0) amount = 0;
        if (tint == _tint && MathF.Abs(amount - _tintAmount) < 0.001f) return;
        (_tint, _tintAmount) = (tint, amount);
        foreach (var (key, src) in _iconSource)
        {
            if (key.StartsWith("theme:", StringComparison.Ordinal)) continue;
            if (_icons.Remove(key, out var old)) old.Dispose();
            _icons[key] = MakeBitmap(src.w, src.h, Tinted(key, src.px));
        }
    }

    private byte[] Tinted(string key, byte[] bgra)
    {
        if (_tintAmount <= 0 || key.StartsWith("theme:", StringComparison.Ordinal)) return bgra;
        var c = _tint.ToColor4();
        float tr = c.R, tg = c.G, tb = c.B;
        var tl = MathF.Max(0.05f, 0.299f * tr + 0.587f * tg + 0.114f * tb);
        var k = _tintAmount;
        var outPx = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            outPx[i + 3] = a;
            if (a == 0) continue;
            // un-premultiply, take the lightness, re-colour it with the tint's hue, blend, premultiply again
            float af = a / 255f, b = bgra[i] / 255f / af, g = bgra[i + 1] / 255f / af, r = bgra[i + 2] / 255f / af;
            var l = 0.299f * r + 0.587f * g + 0.114f * b;
            var scale = l / tl;
            float nr = MathF.Min(1, tr * scale), ng = MathF.Min(1, tg * scale), nb = MathF.Min(1, tb * scale);
            r += (nr - r) * k; g += (ng - g) * k; b += (nb - b) * k;
            outPx[i] = (byte)Math.Clamp(b * af * 255 + 0.5f, 0, 255);
            outPx[i + 1] = (byte)Math.Clamp(g * af * 255 + 0.5f, 0, 255);
            outPx[i + 2] = (byte)Math.Clamp(r * af * 255 + 0.5f, 0, 255);
        }
        return outPx;
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

        var decor = scene.Decor ?? [];
        foreach (var a in scene.Attach ?? []) if (a.Kind == AttachKind.Ring) DrawRingBack(ctx, scene, style, a);
        foreach (var d in decor) if (d.Behind) DrawDecor(ctx, d);
        DrawBackground(ctx, scene, style);
        foreach (var d in decor) if (!d.Behind) DrawDecor(ctx, d);
        if (scene.Reflection > 0) DrawReflections(ctx, scene);

        foreach (var icon in scene.Icons)
        {
            var r = icon.Rect;
            var dest = new Rect(r.X, r.Y, r.W, r.H);
            if (icon.IconKey is not null && _icons.TryGetValue(icon.IconKey, out var bmp))
                ctx.DrawBitmap(bmp, dest, icon.Opacity * (icon.Missing ? 0.5f : 1f), InterpolationMode.HighQualityCubic, null, null);
            else
                DrawPlaceholder(ctx, r, style, icon.Opacity);
        }

        foreach (var ind in scene.Indicators) DrawIndicator(ctx, ind, style);
        if (scene.Label is not null) DrawLabel(ctx, scene.Label, scene.Scale, style);

        var hr = ctx.EndDraw();
        var present = _swap!.Present(1, PresentFlags.None);
        if (hr.Failure || present.Failure) RecoverDevice();
    }

    /// <summary>
    /// Maps band-local coordinates (x along the dock from its start, y across it with 0 = the side away
    /// from the screen edge) to window pixels, for a band occupying window rect <see cref="B"/>.
    /// </summary>
    private readonly record struct BandMap(RectF B, DockEdge Edge)
    {
        public bool Vertical => Edge is DockEdge.Left or DockEdge.Right;
        public float L => Vertical ? B.H : B.W;
        public float H => Vertical ? B.W : B.H;
        public Vector2 P(float x, float y) => Edge switch
        {
            DockEdge.Top => new(B.X + x, B.Bottom - y),
            DockEdge.Left => new(B.Right - y, B.Y + x),
            DockEdge.Right => new(B.X + y, B.Y + x),
            _ => new(B.X + x, B.Y + y),
        };
    }

    /// <summary>A path built in band-local coordinates. Dispose it after drawing.</summary>
    private sealed class LocalPath : IDisposable
    {
        private readonly BandMap _map;
        private readonly bool _filled;
        private ID2D1GeometrySink? _sink;
        public ID2D1PathGeometry Geometry { get; }

        public LocalPath(ID2D1Factory1 factory, BandMap map, bool filled = true)
        {
            _map = map;
            _filled = filled;
            Geometry = factory.CreatePathGeometry();
            _sink = Geometry.Open();
        }

        public LocalPath Begin(float x, float y) { _sink!.BeginFigure(_map.P(x, y), _filled ? FigureBegin.Filled : FigureBegin.Hollow); return this; }
        public LocalPath Line(float x, float y) { _sink!.AddLine(_map.P(x, y)); return this; }
        public LocalPath Curve(float x1, float y1, float x2, float y2, float x, float y)
        {
            _sink!.AddBezier(new BezierSegment { Point1 = _map.P(x1, y1), Point2 = _map.P(x2, y2), Point3 = _map.P(x, y) });
            return this;
        }
        public LocalPath Quad(float x1, float y1, float x, float y)
        {
            _sink!.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = _map.P(x1, y1), Point2 = _map.P(x, y) });
            return this;
        }
        public LocalPath End(bool close = true) { _sink!.EndFigure(close ? FigureEnd.Closed : FigureEnd.Open); return this; }

        /// <summary>Finish the figure list; the geometry is ready to draw.</summary>
        public ID2D1PathGeometry Done()
        {
            if (_sink is not null) { _sink.Close(); _sink.Dispose(); _sink = null; }
            return Geometry;
        }

        public void Dispose() { _sink?.Dispose(); Geometry.Dispose(); }
    }

    /// <summary>How far a leaf's pointed tips reach past the band's ends, as a fraction of the band height.</summary>
    public const float LeafTip = 0.42f;

    /// <summary>How far cat ears rise past the band's far edge, as a fraction of the band height.</summary>
    public const float EarHeight = 0.30f;

    private LocalPath BandPath(DockScene s, BandMap map)
    {
        float L = map.L, H = map.H;
        var p = new LocalPath(_d2dFactory!, map);
        if (s.Shape != BandShape.Leaf)
        {
            // rounded rectangle in local coordinates, so every edge uses the same code
            var r = MathF.Min(s.Radius, MathF.Min(L, H) / 2);
            const float k = 0.5523f;   // bezier circle constant
            p.Begin(r, 0).Line(L - r, 0).Curve(L - r + r * k, 0, L, r - r * k, L, r)
             .Line(L, H - r).Curve(L, H - r + r * k, L - r + r * k, H, L - r, H)
             .Line(r, H).Curve(r - r * k, H, 0, H - r + r * k, 0, H - r)
             .Line(0, r).Curve(0, r - r * k, r - r * k, 0, r, 0).End();
            p.Done();
            return p;
        }
        float e = LeafTip * H, arch = MathF.Min(0.07f * H, L * 0.02f);
        p.Begin(-e, 0.5f * H)
         .Curve(-e * 0.55f, 0.12f * H, -0.05f * H, 0, 0.3f * H, 0)
         .Curve(L * 0.3f, -arch, L * 0.7f, -arch, L - 0.3f * H, 0)
         .Curve(L + 0.05f * H, 0, L + e * 0.55f, 0.1f * H, L + e, 0.45f * H)
         .Curve(L + e * 0.5f, 0.85f * H, L + 0.05f * H, H, L - 0.3f * H, H)
         .Line(0.3f * H, H)
         .Curve(-0.05f * H, H, -e * 0.55f, 0.88f * H, -e, 0.5f * H)
         .End();
        p.Done();
        return p;
    }

    private void DrawBackground(ID2D1DeviceContext ctx, DockScene s, DockStyle style)
    {
        var b = s.Background;
        var hasFill = style.Background.Alpha > 0 || style.BackgroundEnd is { Alpha: > 0 };
        if (!hasFill && style.Outline.Alpha == 0) return;   // invisible dock: icons only
        var map = new BandMap(b, s.Edge);
        using var bandPath = BandPath(s, map);
        var band = bandPath.Geometry;

        // cups and moons join the band's silhouette: one outline (and one shadow) goes round them all
        float H = map.H, L = map.L;
        var owned = new List<IDisposable>();
        var cups = new List<(Attachment a, float x, float r, ID2D1Geometry circle)>();
        var moons = new List<(Attachment a, float cx, float r, ID2D1Geometry crescent)>();
        ID2D1Geometry shape = band;
        foreach (var a in s.Shape == BandShape.Rounded ? s.Attach ?? [] : [])
        {
            if (a.Kind == AttachKind.Cup)
            {
                float x = a.AtEnd ? L - a.Along * H : a.Along * H, r = a.Size * H;
                var circle = _d2dFactory!.CreateEllipseGeometry(new Ellipse(map.P(x, 0.02f * H), r, r));
                owned.Add(circle);
                cups.Add((a, x, r, circle));
                shape = Owned(owned, Combine(shape, circle, CombineMode.Union));
            }
            else if (a.Kind == AttachKind.Moon)
            {
                float r = a.Size * H, cx = a.AtEnd ? L + 0.3f * r : -0.3f * r, side = a.AtEnd ? 1 : -1;
                using var outer = _d2dFactory!.CreateEllipseGeometry(new Ellipse(map.P(cx, 0.46f * H), r, r));
                using var inner = _d2dFactory.CreateEllipseGeometry(new Ellipse(map.P(cx + side * 0.45f * r, 0.46f * H - 0.12f * r), 0.82f * r, 0.82f * r));
                var crescent = Owned(owned, Combine(outer, inner, CombineMode.Exclude));
                moons.Add((a, cx, r, crescent));
                shape = Owned(owned, Combine(shape, crescent, CombineMode.Union));
            }
        }
        try
        {

        // soft shadow: the band drawn a few times, wider and fainter each time
        if (style.Shadow.Alpha > 0)
        using (var shadow = ctx.CreateSolidColorBrush(style.Shadow.WithAlpha(1).ToColor4()))
        {
            var (dx, dy) = s.Edge switch { DockEdge.Top => (0f, -3f), DockEdge.Left => (-3f, 0f), DockEdge.Right => (3f, 0f), _ => (0f, 3f) };
            ctx.Transform = Matrix3x2.CreateTranslation(dx * s.Scale, dy * s.Scale);
            for (var i = 1; i <= 4; i++)
            {
                shadow.Opacity = 0.05f / i * (style.Shadow.Alpha / 255f);
                ctx.DrawGeometry(shape, shadow, i * 6 * s.Scale, _round);
                ctx.FillGeometry(shape, shadow);
            }
            ctx.Transform = Matrix3x2.Identity;
        }

        using var fill = FillBrush(ctx, style, b);
        using var outline = ctx.CreateSolidColorBrush(style.Outline.ToColor4());
        var ow = style.OutlineWidth * s.Scale;

        if (s.Ears && hasFill) DrawEars(ctx, map, fill, outline, style, ow);

        if (hasFill)
        {
            // a cradled planet sits between the cup's paint and the band's, so the band covers its lower half
            foreach (var (a, x, r, circle) in cups)
            {
                using (var cupOnly = Combine(circle, band, CombineMode.Exclude)) ctx.FillGeometry(cupOnly, fill);
                if (a.Sprite is { } key && _icons.TryGetValue(key, out var planet))
                {
                    // only the part above the band shows, even through see-through glass: it has sunk into the dock
                    var c = map.P(x, 0.02f * H - 0.45f * r);
                    var size = 2.56f * r;
                    using var everything = _d2dFactory!.CreateRectangleGeometry(new Rect(c.X - size, c.Y - size, size * 2, size * 2));
                    using var aboveBand = Combine(everything, band, CombineMode.Exclude);
                    ctx.PushLayer(new LayerParameters1 { ContentBounds = new Rect(-100000, -100000, 200000, 200000),
                        GeometricMask = aboveBand, MaskAntialiasMode = AntialiasMode.PerPrimitive, MaskTransform = Matrix3x2.Identity, Opacity = 1 }, null);
                    ctx.DrawBitmap(planet, new Rect(c.X - size / 2, c.Y - size / 2, size, size), 1, InterpolationMode.HighQualityCubic, null, null);
                    ctx.PopLayer();
                }
            }
            ctx.FillGeometry(band, fill);
            foreach (var (a, _, _, crescent) in moons)
            {
                using var moonPaint = ctx.CreateSolidColorBrush((a.Color.Alpha > 0 ? a.Color : new Argb(0xFFFFE9A8)).ToColor4());
                ctx.FillGeometry(crescent, moonPaint);
            }
        }
        if (s.Snow && hasFill) DrawSnow(ctx, map, band, style);
        if (s.Shape == BandShape.Leaf) DrawVeins(ctx, map, style, s.Scale);

        if (style.Outline.Alpha > 0 && !ReferenceEquals(shape, band))
        {
            ctx.DrawGeometry(shape, outline, ow, _round);
            // the cup's rim: a faint line where the planet sinks into the band
            using var rim = ctx.CreateSolidColorBrush(style.Outline.WithAlphaScaled(0.55f).ToColor4());
            foreach (var (_, x, r, _) in cups) ctx.DrawLine(map.P(x - r * 0.96f, 0), map.P(x + r * 0.96f, 0), rim, ow * 0.9f, _round);
        }
        else if (style.Outline.Alpha > 0)
        {
            if (s.Shape == BandShape.Rounded)
            {
                // keep the stroke inside the band so thick outlines don't get clipped by the window region
                var inner = new RoundedRectangle(new RectangleF(b.X + ow / 2, b.Y + ow / 2, b.W - ow, b.H - ow), MathF.Max(0, s.Radius - ow / 2), MathF.Max(0, s.Radius - ow / 2));
                ctx.DrawRoundedRectangle(inner, outline, ow);
            }
            else ctx.DrawGeometry(band, outline, ow, _round);
        }
        if (s.Shape == BandShape.Leaf) DrawStem(ctx, map, style);

        if (style.Highlight.Alpha > 0 && s.Shape == BandShape.Rounded)
        {
            using var highlight = ctx.CreateSolidColorBrush(style.Highlight.ToColor4());
            var inset = 1.5f * s.Scale;
            var r = MathF.Min(s.Radius, map.L / 2);
            ctx.DrawLine(map.P(r, inset), map.P(map.L - r, inset), highlight, 1 * s.Scale);
        }

        // the moon's sleepy face, on the thick side of the crescent
        foreach (var (a, cx, r, _) in moons)
        {
            if (a.Sprite is not { } key || !_icons.TryGetValue(key, out var face)) continue;
            var c = map.P(cx - (a.AtEnd ? 1 : -1) * 0.66f * r, 0.5f * H);
            var size = 0.62f * r;
            ctx.DrawBitmap(face, new Rect(c.X - size / 2, c.Y - size / 2, size, size), 1, InterpolationMode.HighQualityCubic, null, null);
        }
        }
        finally
        {
            foreach (var d in owned) d.Dispose();
        }
    }

    /// <summary>
    /// A soft mirror image of each icon on the dock's surface, on the screen side of the icon,
    /// fading out over about 40% of the icon's size.
    /// </summary>
    private void DrawReflections(ID2D1DeviceContext ctx, DockScene s)
    {
        var gap = 2 * s.Scale;
        using var stops = ctx.CreateGradientStopCollection(
            [new GradientStop(0, new Color4(1, 1, 1, Math.Clamp(s.Reflection, 0, 1))), new GradientStop(0.35f, new Color4(1, 1, 1, Math.Clamp(s.Reflection, 0, 1) * 0.55f)),
             new GradientStop(1, new Color4(1, 1, 1, 0))], ExtendMode.Clamp);
        foreach (var icon in s.Icons)
        {
            if (icon.NoReflection || icon.IconKey is null || !_icons.TryGetValue(icon.IconKey, out var bmp)) continue;
            var r = icon.Rect;
            var depth = 0.5f * MathF.Min(r.W, r.H);
            // mirror line, the fade's direction, and the patch the reflection may cover, for each edge
            var (mirror, from, to, area) = s.Edge switch
            {
                DockEdge.Top => (Matrix3x2.CreateScale(1, -1, new Vector2(0, r.Y - gap / 2)),
                    new Vector2(r.CenterX, r.Y - gap), new Vector2(r.CenterX, r.Y - gap - depth), new Rect(r.X, r.Y - gap - depth, r.W, depth)),
                DockEdge.Left => (Matrix3x2.CreateScale(-1, 1, new Vector2(r.X - gap / 2, 0)),
                    new Vector2(r.X - gap, r.Y + r.H / 2), new Vector2(r.X - gap - depth, r.Y + r.H / 2), new Rect(r.X - gap - depth, r.Y, depth, r.H)),
                DockEdge.Right => (Matrix3x2.CreateScale(-1, 1, new Vector2(r.Right + gap / 2, 0)),
                    new Vector2(r.Right + gap, r.Y + r.H / 2), new Vector2(r.Right + gap + depth, r.Y + r.H / 2), new Rect(r.Right + gap, r.Y, depth, r.H)),
                _ => (Matrix3x2.CreateScale(1, -1, new Vector2(0, r.Bottom + gap / 2)),
                    new Vector2(r.CenterX, r.Bottom + gap), new Vector2(r.CenterX, r.Bottom + gap + depth), new Rect(r.X, r.Bottom + gap, r.W, depth)),
            };
            using var fade = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(from, to), stops);
            ctx.PushLayer(new LayerParameters1
            {
                ContentBounds = area, OpacityBrush = fade, MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity, Opacity = icon.Opacity,
            }, null);
            ctx.Transform = mirror;
            ctx.DrawBitmap(bmp, new Rect(r.X, r.Y, r.W, r.H), 1, InterpolationMode.HighQualityCubic, null, null);
            ctx.Transform = Matrix3x2.Identity;
            ctx.PopLayer();
        }
    }

    private static T Owned<T>(List<IDisposable> owned, T item) where T : IDisposable
    {
        owned.Add(item);
        return item;
    }

    /// <summary>A new geometry: <paramref name="a"/> combined with <paramref name="b"/> (union, exclude...).</summary>
    private ID2D1PathGeometry Combine(ID2D1Geometry a, ID2D1Geometry b, CombineMode mode)
    {
        var g = _d2dFactory!.CreatePathGeometry();
        using var sink = g.Open();
        a.CombineWithGeometry(b, mode, sink);
        sink.Close();
        return g;
    }

    /// <summary>
    /// A ringed planet at one end: the back half of the ring (painted like the band, with its outline)
    /// and the planet in front of it. Drawn before the band, which then passes in front as the ring's front.
    /// </summary>
    private void DrawRingBack(ID2D1DeviceContext ctx, DockScene s, DockStyle style, Attachment a)
    {
        if (s.Shape != BandShape.Rounded) return;
        var hasFill = style.Background.Alpha > 0 || style.BackgroundEnd is { Alpha: > 0 };
        if (!hasFill) return;
        var map = new BandMap(s.Background, s.Edge);
        float H = map.H, L = map.L, pr = a.Size * H;
        float x = a.AtEnd ? L - a.Along * H : a.Along * H, rx = 1.95f * pr, ry = 0.6f * pr, yc = 0.06f * H;
        const float k = 0.5523f;
        using (var arc = new LocalPath(_d2dFactory!, map, filled: false))
        {
            arc.Begin(x - rx, yc).Curve(x - rx, yc - k * ry, x - k * rx, yc - ry, x, yc - ry)
               .Curve(x + k * rx, yc - ry, x + rx, yc - k * ry, x + rx, yc).End(close: false);
            var g = arc.Done();
            var w = 0.4f * pr;
            var ow = style.OutlineWidth * s.Scale;
            using var fill = FillBrush(ctx, style, s.Background);
            if (style.Outline.Alpha > 0)
                using (var outline = ctx.CreateSolidColorBrush(style.Outline.ToColor4())) ctx.DrawGeometry(g, outline, w + 2 * ow, _round);
            ctx.DrawGeometry(g, fill, w, _round);
        }
        if (a.Sprite is { } key && _icons.TryGetValue(key, out var planet))
        {
            var c = map.P(x, -0.25f * pr);
            var size = 2.29f * pr;
            ctx.DrawBitmap(planet, new Rect(c.X - size / 2, c.Y - size / 2, size, size), 1, InterpolationMode.HighQualityCubic, null, null);
        }
    }

    /// <summary>
    /// Two pairs of cat ears on the far edge, painted like the band so they follow its colour. Their
    /// bases stop at the band's edge (so they don't show through see-through glass) and only the sides
    /// get the outline.
    /// </summary>
    private void DrawEars(ID2D1DeviceContext ctx, BandMap map, ID2D1Brush fill, ID2D1Brush outline, DockStyle style, float ow)
    {
        float H = map.H, L = map.L;
        var a = MathF.Min(1, style.Background.Alpha / 255f + 0.2f);
        using var pink = ctx.CreateSolidColorBrush(new Color4(0xF4 / 255f, 0xA6 / 255f, 0xC8 / 255f, a));
        foreach (var c in new[] { MathF.Min(0.95f * H, L * 0.25f), MathF.Max(L - 0.95f * H, L * 0.75f) })
        {
            foreach (var side in new[] { -1f, 1f })
            {
                float inner = c + side * 0.06f * H, outer = c + side * 0.40f * H, tipX = c + side * 0.27f * H, tipY = -EarHeight * H;
                LocalPath Sides(bool closed)
                {
                    var p = new LocalPath(_d2dFactory!, map, filled: closed);
                    p.Begin(inner, 0.04f * H).Line(tipX - side * 0.05f * H, tipY + 0.06f * H)
                     .Quad(tipX, tipY - 0.02f * H, tipX + side * 0.05f * H, tipY + 0.06f * H)
                     .Line(outer, 0.04f * H).End(closed);
                    p.Done();
                    return p;
                }
                using (var ear = Sides(true)) ctx.FillGeometry(ear.Geometry, fill);
                using (var innerEar = new LocalPath(_d2dFactory!, map))
                {
                    innerEar.Begin(c + side * 0.13f * H, 0.03f * H).Line(tipX + side * 0.005f * H, tipY + 0.13f * H).Line(c + side * 0.33f * H, 0.03f * H).End();
                    ctx.FillGeometry(innerEar.Done(), pink);
                }
                if (style.Outline.Alpha > 0)
                    using (var edge = Sides(false)) ctx.DrawGeometry(edge.Geometry, outline, ow, _round);
            }
        }
    }

    /// <summary>A soft, scalloped snow cap along the far edge, clipped to the band.</summary>
    private void DrawSnow(ID2D1DeviceContext ctx, BandMap map, ID2D1Geometry band, DockStyle style)
    {
        float H = map.H, L = map.L;
        var k = MathF.Min(1, style.Background.Alpha / 255f + 0.2f);
        LocalPath Cap(float extra)
        {
            var p = new LocalPath(_d2dFactory!, map);
            p.Begin(-H, -H).Line(L + H, -H).Line(L + H, 0.24f * H + extra);
            // scallops from the far end back to the start; the depth varies so it looks hand-made
            var x = L + H * 0.2f;
            var i = 0;
            while (x > -H * 0.2f)
            {
                var w = H * (0.42f + 0.12f * ((i * 7) % 3));
                var depth = H * (0.2f + 0.06f * ((i * 5) % 3)) + extra;
                p.Quad(x - w / 2, depth + 0.16f * H, x - w, depth);
                if (i % 4 == 2)
                {
                    // a little icicle drip
                    p.Quad(x - w - 0.03f * H, depth + 0.22f * H, x - w - 0.07f * H, depth);
                    x -= 0.07f * H;
                }
                x -= w;
                i++;
            }
            p.Line(-H, 0.24f * H + extra).End();
            p.Done();
            return p;
        }
        ctx.PushLayer(new LayerParameters1
        {
            ContentBounds = new Rect(-100000, -100000, 200000, 200000),
            GeometricMask = band,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1,
        }, null);
        using (var shade = Cap(0.04f * H))
        using (var b = ctx.CreateSolidColorBrush(new Color4(0.80f, 0.88f, 0.97f, k)))
            ctx.FillGeometry(shade.Geometry, b);
        using (var cap = Cap(0))
        using (var w = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, k)))
            ctx.FillGeometry(cap.Geometry, w);
        ctx.PopLayer();
    }

    private void DrawVeins(ID2D1DeviceContext ctx, BandMap map, DockStyle style, float scale)
    {
        float H = map.H, L = map.L, e = LeafTip * H;
        var colour = style.Outline.Alpha > 0 ? style.Outline : style.Background;
        using var vein = ctx.CreateSolidColorBrush(colour.WithAlpha(0.4f).ToColor4());
        using (var rib = new LocalPath(_d2dFactory!, map, filled: false))
        {
            rib.Begin(-e * 0.6f, 0.5f * H).Curve(L * 0.3f, 0.56f * H, L * 0.7f, 0.44f * H, L + e * 0.7f, 0.46f * H).End(close: false);
            ctx.DrawGeometry(rib.Done(), vein, MathF.Max(1, 0.035f * H), _round);
        }
        var step = MathF.Max(0.9f * H, 24 * scale);
        for (var x = 0.25f * H; x < L - 0.2f * H; x += step)
        {
            using var v = new LocalPath(_d2dFactory!, map, filled: false);
            v.Begin(x + 0.38f * H, 0.1f * H).Quad(x + 0.08f * H, 0.3f * H, x, 0.5f * H).Quad(x + 0.08f * H, 0.7f * H, x + 0.38f * H, 0.9f * H).End(close: false);
            ctx.DrawGeometry(v.Done(), vein, MathF.Max(0.75f, 0.022f * H), _round);
        }
    }

    private void DrawStem(ID2D1DeviceContext ctx, BandMap map, DockStyle style)
    {
        float H = map.H, L = map.L, e = LeafTip * H;
        var colour = style.Outline.Alpha > 0 ? style.Outline.WithAlpha(1) : style.Background.WithAlpha(1);
        using var brush = ctx.CreateSolidColorBrush(colour.ToColor4());
        using var stem = new LocalPath(_d2dFactory!, map, filled: false);
        stem.Begin(L + e * 0.92f, 0.45f * H).Curve(L + e + 0.12f * H, 0.42f * H, L + e + 0.24f * H, 0.33f * H, L + e + 0.3f * H, 0.16f * H).End(close: false);
        ctx.DrawGeometry(stem.Done(), brush, MathF.Max(1.5f, 0.07f * H), _round);
    }

    private void DrawDecor(ID2D1DeviceContext ctx, DecorDraw d)
    {
        if (!_icons.TryGetValue(d.Key, out var bmp)) return;
        var c = new Vector2(d.CenterX, d.CenterY);
        ctx.Transform = Matrix3x2.CreateTranslation(-c) * d.Orient * Matrix3x2.CreateTranslation(c);
        ctx.DrawBitmap(bmp, new Rect(d.CenterX - d.Width / 2, d.CenterY - d.Height / 2, d.Width, d.Height), d.Opacity,
            InterpolationMode.HighQualityCubic, null, null);
        ctx.Transform = Matrix3x2.Identity;
    }

    private void DrawIndicator(ID2D1DeviceContext ctx, IndicatorDraw ind, DockStyle style)
    {
        switch (style.IndicatorKind)
        {
            case IndicatorKind.Sprite when style.IndicatorSprite is { } key && _icons.TryGetValue(key, out var bmp):
                var size = ind.Size * (ind.Focused ? 1.3f : 1.15f);
                ctx.DrawBitmap(bmp, new Rect(ind.CenterX - size / 2, ind.CenterY - size / 2, size, size), ind.Focused ? 1f : 0.85f,
                    InterpolationMode.HighQualityCubic, null, null);
                break;
            case IndicatorKind.Dot:
                using (var brush = ctx.CreateSolidColorBrush((ind.Focused ? style.IndicatorFocused : style.Indicator).ToColor4()))
                {
                    var r = ind.Size * (ind.Focused ? 0.24f : 0.19f);
                    ctx.FillEllipse(new Ellipse(new Vector2(ind.CenterX, ind.CenterY), r, r), brush);
                }
                break;
            default:
                DrawPaw(ctx, ind, style);
                break;
        }
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

    /// <summary>The paw-print running indicator (drawn in a 24-unit box).</summary>
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
        _round?.Dispose(); _round = null;
        if (_ctx is not null) _ctx.Target = null;
        _target?.Dispose(); _visual?.Dispose(); _dcompTarget?.Dispose(); _dcomp?.Dispose();
        _swap?.Dispose(); _ctx?.Dispose(); _d2dDevice?.Dispose(); _d2dFactory?.Dispose(); _dxgi?.Dispose(); _d3d?.Dispose();
        _dwrite?.Dispose();
    }

    public void Dispose() => ReleaseDevice();
}
