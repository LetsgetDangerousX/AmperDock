using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmperDock.Core;

namespace AmperDock.App;

/// <summary>
/// Mochi's Pals, from the owner's game Racing for Pi: Bella the puppy, the bunny and the dino. Each runs
/// along its row of the desktop, stops for a breather, hops now and then, and bumps into the edge of the
/// screen ("ouch!") before turning round. Click one and it jumps; drag it anywhere; its menu can keep it
/// where you put it.
/// </summary>
public sealed class PalWidget : WidgetWindow
{
    public const string Bella = "pal-bella", Bunny = "pal-bunny", Dino = "pal-dino";
    public static readonly (string Kind, string Name, string Folder, int Jumps)[] All =
        [(Bella, "Bella", "bella", 8), (Bunny, "Bunny", "bunny", 5), (Dino, "Dino", "dino", 5)];

    private const double Tall = 110;              // DIPs
    private const double Speed = 78;              // DIPs a second
    private enum Act { Run, Rest, Hop, Ouch }

    private readonly string _folder;
    private readonly int _jumps;
    private readonly double _w;
    private readonly Image _pal = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ScaleTransform _flip = new(1, 1);
    private readonly TranslateTransform _lift = new();
    private readonly TextBlock _bubble = MochiWidget.Bubble();
    private readonly DispatcherTimer _tick;
    private Act _act = Act.Rest;
    private long _started, _until, _bubbleUntil, _last = Environment.TickCount64;
    private int _dir = 1;
    private bool _pressed;
    private Point _pressAt;

    private static readonly Dictionary<string, BitmapImage> Frames = new();

    private static (double w, double h) SizeOf(string folder)
    {
        var f = Frame(folder, "run-1");
        return (f.PixelWidth * Tall / f.PixelHeight, Tall);
    }

    private static BitmapImage Frame(string folder, string name)
    {
        var key = folder + "/" + name;
        if (Frames.TryGetValue(key, out var img)) return img;
        img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/Assets/Pals/{key}.png");
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return Frames[key] = img;
    }

    public PalWidget(DesktopWidget data, DesktopWidgetManager manager) : this(data, manager, All.First(p => p.Kind == data.Kind)) { }

    private PalWidget(DesktopWidget data, DesktopWidgetManager manager, (string Kind, string Name, string Folder, int Jumps) pal)
        : base(data, manager, SizeOf(pal.Folder).w, Tall + 34)
    {
        (_folder, _jumps) = (pal.Folder, pal.Jumps);
        _w = SizeOf(pal.Folder).w;
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
        Card.Effect = null;
        // the bunny is painted; Bella and the dino are pixel art and stay crisp
        RenderOptions.SetBitmapScalingMode(_pal, pal.Folder == "bunny" ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        _pal.Width = _w;
        _pal.Height = Tall;
        _pal.RenderTransform = new TransformGroup { Children = { _flip, _lift } };
        _bubble.HorizontalAlignment = HorizontalAlignment.Center;
        _bubble.VerticalAlignment = VerticalAlignment.Top;
        Card.Child = new Grid { Width = _w, Height = Tall + 34, Children = { _pal, _bubble } };
        Card.PreviewMouseLeftButtonDown += (_, e) => { _pressed = true; _pressAt = PointToScreen(e.GetPosition(this)); StopRunning(); };
        Card.MouseLeftButtonUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            var now = PointToScreen(e.GetPosition(this));
            if (Math.Abs(now.X - _pressAt.X) + Math.Abs(now.Y - _pressAt.Y) < 6) { Start(Act.Hop); Say(Hellos[Random.Shared.Next(Hellos.Length)]); }
            else Start(Act.Rest);
        };
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        Closed += (_, _) => { _tick.Stop(); StopRunning(); };
        Start(Act.Rest);
    }

    private static readonly string[] Hellos = ["hi!", "wheee!", "hop!", "♥", "race you!"];

    protected override bool HasColourMenu => false;

    protected override void ApplyColour()
    {
        base.ApplyColour();
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
    }

    protected override IEnumerable<object> MenuItems()
    {
        var stay = new MenuItem { Header = "_Stay where I put you", IsCheckable = true, IsChecked = Data.StayPut };
        stay.Click += (_, _) => { Save(Data with { StayPut = stay.IsChecked }); if (Data.StayPut) Start(Act.Rest); ApplyColour(); };
        yield return stay;
    }

    protected override void OnDoubleClick() => Start(Act.Hop);

    private void Start(Act act)
    {
        if (act == Act.Run && Data.StayPut) act = Act.Rest;
        if (_act == Act.Run && act != Act.Run) StopRunning();
        _act = act;
        _started = Environment.TickCount64;
        _until = _started + act switch
        {
            Act.Run => 4000 + Random.Shared.Next(7000),
            Act.Hop => _jumps * 110 + 120,
            Act.Ouch => 1100,
            _ => 2500 + Random.Shared.Next(5000),
        };
        if (act == Act.Run)
        {
            SavesMoves = false;
            if (Random.Shared.NextDouble() < 0.4) _dir = -_dir;
        }
    }

    private void StopRunning()
    {
        if (SavesMoves) return;
        SavesMoves = true;
        Save(Data with { X = Left, Y = Top });
    }

    private void Say(string text)
    {
        _bubble.Text = text;
        _bubbleUntil = Environment.TickCount64 + 1500;
    }

    private void Tick()
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.2, (now - _last) / 1000.0);
        _last = now;
        if (_pressed) return;
        if (now >= _until)
        {
            var roll = Random.Shared.NextDouble();
            Start(_act switch
            {
                Act.Run => roll < 0.3 ? Act.Hop : Act.Rest,
                Act.Ouch => Act.Run,
                _ => Data.StayPut ? (roll < 0.25 ? Act.Hop : Act.Rest) : roll < 0.7 ? Act.Run : roll < 0.85 ? Act.Hop : Act.Rest,
            });
        }
        var t = now - _started;
        _lift.Y = 0;
        switch (_act)
        {
            case Act.Run:
                _pal.Source = Frame(_folder, $"run-{t / 110 % 4 + 1}");
                _flip.ScaleX = _dir;
                Run(dt);
                break;
            case Act.Hop:
                var i = (int)Math.Min(_jumps - 1, t / 110);
                _pal.Source = Frame(_folder, $"jump-{i + 1}");
                _lift.Y = -Math.Sin(Math.Min(1, t / (_jumps * 110.0)) * Math.PI) * 26;   // up and down again
                break;
            case Act.Ouch:
                _pal.Source = Frame(_folder, "ouch-1");
                break;
            default:
                _pal.Source = Frame(_folder, "run-1");
                break;
        }
        _bubble.Opacity = now < _bubbleUntil ? Math.Min(1, (_bubbleUntil - now) / 400.0) : 0;
    }

    private void Run(double dt)
    {
        if (PresentationSource.FromVisual(this) is null) return;
        var (left, right) = ScreenEdges();
        var x = Left + _dir * Speed * dt;
        if (x < left - 14 || x + Width > right + 14)
        {
            // bonk: an ouch at the edge of the screen, then off the other way
            x = Math.Clamp(x, left - 14, right + 14 - Width);
            _dir = -_dir;
            Say("ouch!");
            Start(Act.Ouch);
        }
        Left = x;
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO mi);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private (double Left, double Right) ScreenEdges()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hwnd == 0 || !GetMonitorInfoW(MonitorFromWindow(hwnd, 2), ref mi))
            return (SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return (mi.rcWork.Left / scale, mi.rcWork.Right / scale);
    }
}
