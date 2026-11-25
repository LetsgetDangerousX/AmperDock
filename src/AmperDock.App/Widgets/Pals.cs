using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmperDock.Core;
using Modes = AmperDock.App.MochiWidget.Modes;

namespace AmperDock.App;

/// <summary>
/// Bella, Mochi's puppy pal (the owner's own drawings; replica/tools/build_bella.py). She runs along her
/// row of the desktop and turns round at the edge of the screen, and she has her own things: a dog house
/// (she steps in, peeks out and falls asleep), a chew toy (she spots it, pounces, chomps, shakes, chews,
/// tumbles and sits proudly with it) and a food bowl (she eats it down). Ask her to do something from her
/// menu and she keeps at it until it's changed or she gets sleepy; then she sleeps in her house (or naps
/// with her toy) and carries on after. Click her and she bounces; drag her anywhere. She makes no sounds.
/// </summary>
public sealed class PalWidget : WidgetWindow
{
    public const string Bella = "pal-bella";

    private const double Speed = 78, TripSpeed = 90;   // DIPs a second: running about, and heading somewhere
    private enum Act { Run, Rest, Ready, Hop, Ouch, Trip, AtThing }

    private readonly double _w, _h, _feet;
    private readonly Image _pal = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 1), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ScaleTransform _flip = new(1, 1);
    private readonly TranslateTransform _lift = new();
    private readonly TextBlock _bubble = MochiWidget.Bubble();
    private readonly DispatcherTimer _tick;
    private Act _act = Act.Rest;
    private long _started, _until, _bubbleUntil, _last = Environment.TickCount64;
    private long _awakeSince = Environment.TickCount64, _upLateUntil, _nextAsk;
    private int _dir = 1, _visitStep;
    private bool _pressed;
    private Point _pressAt;
    private BellaThingWidget? _thing;

    public PalWidget(DesktopWidget data, DesktopWidgetManager manager)
        : base(data, manager, BellaArt.RunWidth * BellaArt.Px, BellaArt.RunHeight * BellaArt.Px + 34)
    {
        _w = BellaArt.RunWidth * BellaArt.Px;
        _h = BellaArt.RunHeight * BellaArt.Px;
        _feet = BellaArt.Feet * BellaArt.Px;
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
        Card.Effect = null;
        RenderOptions.SetBitmapScalingMode(_pal, BitmapScalingMode.HighQuality);
        _pal.Width = _w;
        _pal.Height = _h;
        _pal.RenderTransform = new TransformGroup { Children = { _flip, _lift } };
        _pal.Source = BellaArt.Picture("idle-1");
        _bubble.HorizontalAlignment = HorizontalAlignment.Center;
        _bubble.VerticalAlignment = VerticalAlignment.Top;
        Card.Child = new Grid { Width = _w, Height = _h + 34, Children = { _pal, _bubble } };
        Card.PreviewMouseLeftButtonDown += (_, e) => { _pressed = true; _pressAt = PointToScreen(e.GetPosition(this)); StopRunning(); };
        Card.MouseLeftButtonUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            var now = PointToScreen(e.GetPosition(this));
            if (Math.Abs(now.X - _pressAt.X) + Math.Abs(now.Y - _pressAt.Y) < 6) { Start(Act.Hop); Say(Hellos[Random.Shared.Next(Hellos.Length)]); }
            else Start(Act.Rest);   // put down somewhere new
        };
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        Closed += (_, _) => { _tick.Stop(); if (_thing is { } t) { _thing = null; t.Visit(null); } };
        Start(Act.Rest);
    }

    private static readonly string[] Hellos = ["woof!", "hi!", "♥", "arf!", "*wag wag*"];

    /// <summary>What she keeps doing (older copies only had "stay where I put you").</summary>
    private string Mode => Data.MochiMode ?? (Data.StayPut ? Modes.Stay : Modes.Free);

    protected override bool HasColourMenu => false;

    protected override void ApplyColour()
    {
        base.ApplyColour();
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
    }

    // ---- her menu

    protected override IEnumerable<object> MenuItems()
    {
        MenuItem Choice(string header, string mode, bool enabled = true, string? why = null)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = Mode == mode, IsEnabled = enabled, ToolTip = enabled ? null : why };
            mi.Click += (_, _) => SetMode(mode);
            return mi;
        }
        bool Has(string kind) => Manager.BellaThings(kind).Any();
        yield return Choice("_Wander freely", Modes.Free);
        yield return Choice("_Stay where I put you", Modes.Stay);
        yield return Choice("Keep _running", Modes.Walk);
        yield return Choice("_Play with her toy", Modes.Play, Has(BellaThingWidget.Toy), "Put out her chew toy first");
        yield return Choice("_Eat from her bowl", Modes.Eat, Has(BellaThingWidget.Bowl), "Put out her food bowl first");
        yield return Choice("Take a _nap", Modes.Nap, Has(BellaThingWidget.House), "Put out her dog house first");
        yield return Choice("Go _home to her house", Modes.Home, Has(BellaThingWidget.House), "Put out her dog house first");
        yield return new Separator();
        var things = new MenuItem { Header = "Put out her _things" };
        foreach (var (kind, name) in new[] { (BellaThingWidget.House, "_Dog house"), (BellaThingWidget.Toy, "_Chew toy"), (BellaThingWidget.Bowl, "_Food bowl") })
        {
            var mi = new MenuItem { Header = name, IsEnabled = !Has(kind) };
            mi.Click += (_, _) => Manager.Add(kind);
            things.Items.Add(mi);
        }
        yield return things;
    }

    /// <summary>Asked to do something (from her menu or one of her things): she's up for it, even at night, for a while.</summary>
    internal void SetMode(string mode)
    {
        Save(Data with { MochiMode = mode, StayPut = mode == Modes.Stay });
        _awakeSince = Environment.TickCount64;
        if (mode != Modes.Nap && mode != Modes.Home) _upLateUntil = _awakeSince + 30 * 60_000L;
        LeaveThing();
        Carry();
        ApplyColour();
    }

    /// <summary>Let out of her house (its menu): out she comes and carries on (running about, if she'd been sent home).</summary>
    internal void ExitHouse()
    {
        if (Mode is Modes.Home or Modes.Nap) SetMode(Modes.Free);
        else { _awakeSince = Environment.TickCount64; _upLateUntil = _awakeSince + 30 * 60_000L; }
        LeaveThing();
        Start(Act.Hop);
        Say("woof!");
    }

    internal bool InHouse => _act == Act.AtThing && _thing?.Kind == BellaThingWidget.House;

    protected override void OnDoubleClick() => Start(Act.Hop);

    // diagnostics: AMPERDOCK_MOCHI_DAY=1 makes it daytime (as for Mochi)
    private static readonly bool AlwaysDay = Environment.GetEnvironmentVariable("AMPERDOCK_MOCHI_DAY") == "1";
    private static bool Night => !AlwaysDay && DateTime.Now.Hour is >= 22 or < 7;

    /// <summary>Sleepy: night time, or she's been up and about for a good while (40 to 70 minutes).</summary>
    private bool Sleepy => (Night && Environment.TickCount64 > _upLateUntil) || Environment.TickCount64 - _awakeSince > (40 + (Data.Id.GetHashCode() & 31)) * 60_000L;

    /// <summary>Somewhere to sleep: her house, or else a nap with her toy (she has no way to sleep without either).</summary>
    private BellaThingWidget? Bed => Manager.BellaThings(BellaThingWidget.House).FirstOrDefault() ?? Manager.BellaThings(BellaThingWidget.Toy).FirstOrDefault();

    // ---- what she's doing

    private void Start(Act act, double seconds = 0)
    {
        if (act == Act.Run && Mode == Modes.Stay) act = Act.Rest;
        if (_act is Act.Run or Act.Trip && act is not (Act.Run or Act.Trip)) StopRunning();
        if (_act == Act.AtThing && act != Act.AtThing) LeaveThing();
        _act = act;
        _started = Environment.TickCount64;
        var ms = seconds > 0 ? (long)(seconds * 1000) : act switch
        {
            Act.Run => Mode == Modes.Walk ? 3_600_000L : 4000 + Random.Shared.Next(7000),
            Act.Hop => 520,
            Act.Ready => 2200 + Random.Shared.Next(1200),
            Act.Ouch => 900,
            Act.Trip => 120_000,          // gives up if she somehow can't get there
            _ => 2000 + Random.Shared.Next(3500),
        };
        _until = _started + ms;
        if (act is Act.Run or Act.Trip)
        {
            SavesMoves = false;
            if (act == Act.Run && Random.Shared.NextDouble() < 0.4) _dir = -_dir;
        }
        if (act != Act.AtThing && !IsVisible) Show();
    }

    private void StopRunning()
    {
        if (SavesMoves) return;
        SavesMoves = true;
        Save(Data with { X = Left, Y = Top });
    }

    /// <summary>Carry on with what she's been asked to do (or, wandering freely, pick something).</summary>
    private void Carry()
    {
        if (Sleepy && Mode is not (Modes.Stay or Modes.Walk) && Bed is { } bed) { GoTo(bed); return; }
        switch (Mode)
        {
            case Modes.Stay: Start(Act.Rest); break;
            case Modes.Walk: Start(Act.Run); break;
            case Modes.Play or Modes.Eat or Modes.Nap or Modes.Home:
                var kind = Mode switch { Modes.Play => BellaThingWidget.Toy, Modes.Eat => BellaThingWidget.Bowl, _ => BellaThingWidget.House };
                if (Manager.BellaThings(kind).FirstOrDefault() is { } thing)
                {
                    if (kind == BellaThingWidget.Bowl && thing.Data.Level == 0 && Near(thing.Approach()))
                    {
                        // an empty bowl: she waits beside it and asks now and then; once it's filled she eats
                        Face(thing);
                        Start(Act.Rest);
                        if (Environment.TickCount64 > _nextAsk) { Say("food?"); _nextAsk = Environment.TickCount64 + 25_000; }
                    }
                    else GoTo(thing);
                }
                else { Save(Data with { MochiMode = Modes.Free }); Carry(); }   // the thing was put away
                break;
            default: Wander(); break;
        }
    }

    /// <summary>Left to herself: mostly running about, a little rest now and then, sometimes a visit to one of her things.</summary>
    private void Wander()
    {
        var roll = Random.Shared.NextDouble();
        var things = Manager.BellaThings(null).Where(t => t.Kind != BellaThingWidget.Bowl || t.Data.Level > 0).ToList();
        if (things.Count > 0 && roll < 0.18) { GoTo(things[Random.Shared.Next(things.Count)]); return; }
        Start(_act == Act.Run ? (roll < 0.5 ? Act.Hop : Act.Rest) : roll < 0.8 ? Act.Run : Act.Hop);
    }

    private void Next()
    {
        switch (_act)
        {
            case Act.Ouch: Start(Mode is Modes.Free or Modes.Walk ? Act.Run : Act.Rest); break;
            case Act.AtThing: LeaveThing(); Start(Act.Ready); break;   // a visit is over: pleased with herself
            case Act.Trip: Start(Act.Rest); break;
            default: Carry(); break;
        }
    }

    /// <summary>Clicked in a scene at one of her things (the thing passes it on): asleep, she wakes; otherwise a hello.</summary>
    internal void ClickedAtThing()
    {
        var asleep = (InHouse && _visitStep >= 3) || (_thing?.Kind == BellaThingWidget.Toy && _visitStep == 99);
        if (asleep)
        {
            _awakeSince = Environment.TickCount64;
            _upLateUntil = _awakeSince + 30 * 60_000L;
            LeaveThing();
            Start(Act.Hop);
            Say("huh? woof!");
            return;
        }
        _thing?.Say(Hellos[Random.Shared.Next(Hellos.Length)]);
    }

    private void Say(string text)
    {
        _bubble.Text = text;
        _bubbleUntil = Environment.TickCount64 + 1500;
    }

    // ---- going to her things

    private void GoTo(BellaThingWidget thing)
    {
        _thing = thing;
        if (Near(thing.Approach())) { Arrive(); return; }
        Start(Act.Trip);
    }

    private bool Near(Point p)
    {
        var feet = Feet();
        return Math.Abs(feet.X - p.X) + Math.Abs(feet.Y - p.Y) < 8;
    }

    private double K => ActualWidth > 0 ? ActualWidth / (_w + 28) : 1;

    /// <summary>Where her feet are, in screen DIPs.</summary>
    private Point Feet() => new(Left + Width / 2, Top + Height - (14 + _feet) * K);

    private void MoveFeetTo(Point p)
    {
        Left = p.X - Width / 2;
        Top = p.Y - (Height - (14 + _feet) * K);
    }

    private void Face(BellaThingWidget thing)
    {
        _dir = thing.Left + thing.Width / 2 < Feet().X ? -1 : 1;
        _flip.ScaleX = _dir;
    }

    private void Arrive()
    {
        if (_thing is not { IsLoaded: true } thing) { _thing = null; Start(Act.Rest); return; }
        if (thing.Kind == BellaThingWidget.Bowl && thing.Data.Level == 0) { _thing = null; Face(thing); Start(Act.Rest); return; }
        StopRunning();
        // a free-time visit is a little while; one she was asked for (or bedtime) lasts until something changes
        var stays = Mode != Modes.Free || Sleepy;
        Start(Act.AtThing, stays ? 24 * 3600 : 25 + Random.Shared.NextDouble() * 25);
        _visitStep = 0;
        _stepStarted = Environment.TickCount64;
        _nextAsk = Environment.TickCount64 + 3_000;
        Hide();
        thing.Visit(this);
    }

    /// <summary>Done at a thing: it shows itself again and she stands beside it.</summary>
    private void LeaveThing()
    {
        if (_thing is not { } thing) return;
        _thing = null;
        if (thing.Visitor != this) return;
        thing.Visit(null);
        if (thing.IsLoaded) MoveFeetTo(thing.Approach());
        if (!IsVisible) Show();
        StopRunning();
    }

    // ---- every frame

    private void Tick()
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.2, (now - _last) / 1000.0);
        _last = now;
        if (_pressed) return;   // being dragged
        if (now >= _until) Next();
        // sleepy in the middle of something: she goes to bed (and carries on when she wakes)
        var inBed = _act == Act.AtThing && (_thing?.Kind == BellaThingWidget.House || _visitStep == 99);
        if (Sleepy && !inBed && _act is not Act.Trip && Mode is not (Modes.Stay or Modes.Walk) && Bed is not null && now - _started > 1500)
        {
            LeaveThing();
            _act = Act.Rest;
            Carry();
            return;
        }
        var t = now - _started;
        _lift.Y = 0;
        _lift.X = 0;
        switch (_act)
        {
            case Act.Run or Act.Trip:
                if (_act == Act.Run) Run(dt); else Travel(dt);
                // her own frames each way (not a mirror image): drawn facing where she's going
                _pal.Source = _dir > 0 ? BellaArt.Picture($"run-{t / 80 % BellaArt.RunFrames + 1}") : BellaArt.Picture($"left-{t / 80 % BellaArt.LeftFrames + 1}");
                _flip.ScaleX = 1;
                break;
            case Act.Hop:
                Stand("ready");
                _lift.Y = -Math.Sin(Math.Min(1, t / 520.0) * Math.PI) * 24;   // up and down again
                break;
            case Act.Ready:
                Stand("ready");
                break;
            case Act.Ouch:
                Stand("idle-1");
                _lift.X = t < 400 ? (t / 50 % 2 == 0 ? 2 : -2) : 0;           // a little shake
                break;
            case Act.AtThing:
                Visit(now);
                break;
            default:
                // standing about: still, a wag of her tail now and then
                var c = t % 6000;
                Stand(c is > 3500 and < 4100 ? "idle-2" : c > 5000 ? "idle-3" : "idle-1");
                break;
        }
        _bubble.Opacity = now < _bubbleUntil ? Math.Min(1, (_bubbleUntil - now) / 400.0) : 0;
    }

    /// <summary>A standing pose (drawn facing right), turned to face the way she's facing.</summary>
    private void Stand(string pose)
    {
        _pal.Source = BellaArt.Picture(pose);
        _flip.ScaleX = _dir;
    }

    /// <summary>Running to one of her things, the straight way, facing where she's going.</summary>
    private void Travel(double dt)
    {
        if (_thing is not { IsLoaded: true } thing) { _thing = null; Start(Act.Rest); return; }
        var to = thing.Approach();   // it may have been moved meanwhile
        var feet = Feet();
        var (dx, dy) = (to.X - feet.X, to.Y - feet.Y);
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 4) { Arrive(); return; }
        var step = Math.Min(dist, TripSpeed * dt);
        if (Math.Abs(dx) > 1) _dir = dx > 0 ? 1 : -1;
        MoveFeetTo(new Point(feet.X + dx / dist * step, feet.Y + dy / dist * step));
    }

    // her playing, a scene at a time (picture, milliseconds); after the first three it goes round again from "shake"
    private static readonly (string Pic, int Ms)[] Play =
        [("toy-spot", 1300), ("toy-pounce", 650), ("toy-chomp", 1500), ("toy-shake", 2200), ("toy-chew", 5000), ("toy-tumble", 3000), ("toy-proud", 2600)];

    private long _stepStarted;

    /// <summary>What she does at one of her things, a step at a time (the thing shows her scene).</summary>
    private void Visit(long now)
    {
        if (_thing is not { IsLoaded: true } thing || thing.Visitor != this) { _thing = null; if (!IsVisible) Show(); Start(Act.Rest); return; }
        var t = now - _stepStarted;   // how long this step has been going
        void Step(int n) { _visitStep = n; _stepStarted = now; t = 0; }
        switch (thing.Kind)
        {
            case BellaThingWidget.House:
                // steps in, gets cozy, peeks out for a while, gets sleepy, falls asleep; asked to nap (or at bedtime) she soon drops off
                var sleepNow = Mode == Modes.Nap || Sleepy;
                if (_visitStep == 0 && t > 1400) Step(1);
                else if (_visitStep == 1 && t > 2000) Step(2);
                else if (_visitStep == 2 && (t > 22_000 || (sleepNow && t > 2500))) Step(3);
                else if (_visitStep == 3 && t > 4000) Step(4);
                else if (_visitStep == 4 && t > 12_000) Step(5);
                var pic = _visitStep switch { 0 => "house-step", 1 => "house-cozy", 2 => "house-peek", 3 => "house-sleepy", 4 => "house-asleep", _ => "house-deep" };
                thing.Scene(pic, 0, _visitStep >= 4 ? Math.Sin(now / 900.0) * 0.8 : 0);
                if (_visitStep >= 4 && Mode is not (Modes.Home or Modes.Nap) && !Sleepy && now - _started > 15 * 60_000) _until = now;   // a nap's done
                break;
            case BellaThingWidget.Toy:
                if (_visitStep == 99 || ((Sleepy || Mode == Modes.Nap) && !Manager.BellaThings(BellaThingWidget.House).Any()))
                {
                    // no house: she naps with her toy
                    if (_visitStep != 99) Step(99);
                    thing.Scene("toy-nap", 0, Math.Sin(now / 900.0) * 0.8);
                    if (!Sleepy && Mode != Modes.Nap && t > 15 * 60_000) _until = now;
                    break;
                }
                if (t > Play[_visitStep].Ms) Step(_visitStep + 1 < Play.Length ? _visitStep + 1 : 3);
                var scene = Play[_visitStep].Pic;
                var shake = scene == "toy-shake" ? (now / 60 % 2 == 0 ? 2.0 : -2.0) : 0;
                var bob = scene == "toy-chew" ? Math.Abs(Math.Sin(now / 200.0)) * 1.5 : 0;
                thing.Scene(scene, shake, bob);
                break;
            case BellaThingWidget.Bowl:
                var level = thing.Data.Level;
                if (_visitStep == 0) Step(level == 2 ? 1 : 2);
                if (_visitStep == 1)
                {
                    thing.Scene("eat-arrive", 0, 0);   // coming up to it, tail wagging
                    if (t > 1200) Step(2);
                }
                else if (_visitStep == 2 && level > 0)
                {
                    // a few seconds per helping, her head bobbing as she munches
                    thing.Scene(level == 2 ? "eat-full" : "eat-half", 0, Math.Abs(Math.Sin(now / 180.0)) * 2);
                    if (t > 6000) { thing.SetLevel(level - 1); Step(level - 1 == 0 ? 3 : 2); }
                }
                else if (_visitStep == 3 && t < 3000) thing.Scene("eat-done", 0, 0);   // licking her lips
                else
                {
                    // all gone: she steps away (asked to eat, she waits beside it for more)
                    _nextAsk = now + 8_000;
                    LeaveThing();
                    Start(Act.Rest);
                }
                break;
        }
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

/// <summary>Bella's pictures and what build_bella.py says about them (Assets/Pals/bella/things.json).</summary>
public static class BellaArt
{
    public sealed record Pic(string thing, double width, double height, double base_x, double base_y);

    private sealed record Meta(double px, int run_frames, int left_frames, int idle_frames, double feet, Dictionary<string, Pic> pics,
        double house_approach, double toy_approach, double bowl_approach);

    private static readonly Meta M = Load();
    private static readonly Dictionary<string, BitmapImage> Cache = new();

    private static Meta Load()
    {
        using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Pals/bella/things.json"))!.Stream;
        return JsonSerializer.Deserialize<Meta>(s)!;
    }

    /// <summary>DIPs per picture pixel.</summary>
    public static double Px => M.px;
    public static int RunFrames => M.run_frames;
    public static int LeftFrames => M.left_frames;
    public static double Feet => M.feet;
    public static double RunWidth => Picture("run-1").PixelWidth;
    public static double RunHeight => Picture("run-1").PixelHeight;
    public static IReadOnlyDictionary<string, Pic> Pics => M.pics;

    /// <summary>Where she stands to come to a thing, from its base (picture pixels; negative is to its left).</summary>
    public static double Approach(string thing) => thing switch { "house" => M.house_approach, "toy" => M.toy_approach, _ => M.bowl_approach };

    public static BitmapImage Picture(string name)
    {
        if (Cache.TryGetValue(name, out var img)) return img;
        img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/Assets/Pals/bella/{name}.png");
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return Cache[name] = img;
    }
}

/// <summary>
/// One of Bella's things on the desktop: her dog house, chew toy or food bowl. Drag it where you like.
/// When Bella comes to it, it shows her scene with it (lined up on the thing's base) while she's there.
/// Double-click the bowl, or use its menu, to fill it up.
/// </summary>
public sealed class BellaThingWidget : WidgetWindow
{
    public const string House = "bella-house", Toy = "bella-toy", Bowl = "bella-bowl";

    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _bubble = MochiWidget.Bubble();
    private readonly double _left, _up, _w, _h;   // the picture area: room left of and above the base, in picture pixels
    private string? _sceneName;

    public string Kind => Data.Kind;
    private static string ThingOf(string kind) => kind switch { House => "house", Toy => "toy", _ => "bowl" };
    public PalWidget? Visitor { get; private set; }

    private static (double left, double up, double w, double h) Room(string kind)
    {
        double l = 0, r = 0, u = 0, d = 0;
        foreach (var p in BellaArt.Pics.Values.Where(p => p.thing == ThingOf(kind)))
        {
            l = Math.Max(l, p.base_x); r = Math.Max(r, p.width - p.base_x);
            u = Math.Max(u, p.base_y); d = Math.Max(d, p.height - p.base_y);
        }
        var head = 30 / BellaArt.Px;   // a little headroom for a speech bubble
        return (l, u + head, l + r, u + head + d);
    }

    public BellaThingWidget(DesktopWidget data, DesktopWidgetManager manager) : this(data, manager, Room(data.Kind)) { }

    private BellaThingWidget(DesktopWidget data, DesktopWidgetManager manager, (double left, double up, double w, double h) room)
        : base(data, manager, room.w * BellaArt.Px, room.h * BellaArt.Px)
    {
        (_left, _up, _w, _h) = room;
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
        Card.Effect = null;
        _canvas.Width = _w * BellaArt.Px;
        _canvas.Height = _h * BellaArt.Px;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_bubble);
        Card.Child = _canvas;
        Card.MouseLeftButtonUp += (_, _) => { if (Visitor is { } b && !_dragged) b.ClickedAtThing(); };
        Card.PreviewMouseLeftButtonDown += (_, e) => { _down = e.GetPosition(this); _dragged = false; };
        Card.PreviewMouseMove += (_, e) => { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && (e.GetPosition(this) - _down).Length > 5) _dragged = true; };
        _bubbleTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Background, (_, _) => FadeBubble(), Dispatcher);
        Closed += (_, _) => _bubbleTimer.Stop();
        Repaint();
    }

    private Point _down;
    private bool _dragged;
    private readonly DispatcherTimer _bubbleTimer;
    private long _bubbleUntil;

    protected override bool HasColourMenu => false;

    protected override void ApplyColour()
    {
        base.ApplyColour();
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
    }

    protected override IEnumerable<object> MenuItems()
    {
        // Bella herself (the one visiting, else the first one out); in her house she's hidden, so her things speak for her
        var bella = Visitor ?? Manager.Bellas().FirstOrDefault();
        MenuItem Ask(string header, string mode)
        {
            var mi = new MenuItem { Header = header, IsEnabled = bella is not null, ToolTip = bella is null ? "Add Bella from the Widgets tab first" : null };
            mi.Click += (_, _) => bella?.SetMode(mode);
            return mi;
        }
        switch (Kind)
        {
            case Toy:
                yield return Ask("Let Bella _play", MochiWidget.Modes.Play);
                break;
            case Bowl:
                yield return Ask("Let Bella _eat", MochiWidget.Modes.Eat);
                var fill = new MenuItem { Header = "_Fill the bowl", IsEnabled = Data.Level < 2 };
                fill.Click += (_, _) => SetLevel(2);
                yield return fill;
                break;
            case House:
                var inside = Manager.Bellas().Where(b => b.InHouse).ToList();
                var exit = new MenuItem { Header = "E_xit", IsEnabled = inside.Count > 0, ToolTip = inside.Count > 0 ? "Let Bella out of her house" : "Bella isn't in her house" };
                exit.Click += (_, _) => { foreach (var b in inside) b.ExitHouse(); };
                yield return exit;
                yield return Ask("Let Bella go _home", MochiWidget.Modes.Home);
                break;
        }
    }

    protected override void OnDoubleClick() { if (Kind == Bowl) SetLevel(2); }

    public void SetLevel(int level)
    {
        Save(Data with { Level = Math.Clamp(level, 0, 2) });
        if (_sceneName is null) Repaint();
        ApplyColour();
    }

    private double Scale => ActualWidth > 0 ? ActualWidth / (_w * BellaArt.Px + 28) : 1;

    private Point BaseOnScreen()
    {
        var k = Scale;
        return new Point(Left + (14 + _left * BellaArt.Px) * k, Top + (14 + _up * BellaArt.Px) * k);
    }

    /// <summary>Where Bella stands to come to it (her feet, screen DIPs): on its left, where her scenes start.</summary>
    public Point Approach()
    {
        var b = BaseOnScreen();
        return new Point(b.X + BellaArt.Approach(ThingOf(Kind)) * BellaArt.Px * Scale, b.Y);
    }

    /// <summary>Bella arrives (or leaves, with null).</summary>
    public void Visit(PalWidget? bella)
    {
        Visitor = bella;
        if (bella is null) { _sceneName = null; Repaint(); }
    }

    /// <summary>Show one of her scenes here, lined up on this thing's base; shake and bob move it a touch.</summary>
    public void Scene(string name, double shake, double bob)
    {
        if (name != _sceneName) { _sceneName = name; Place(name); }
        _image.RenderTransform = shake == 0 && bob == 0 ? null : new TranslateTransform(shake, -bob);
    }

    /// <summary>Its own picture (the bowl as full as it is).</summary>
    public void Repaint()
    {
        if (_sceneName is { } scene) { Place(scene); return; }
        Place(Kind switch { House => "house", Toy => "toy", _ => Data.Level switch { 2 => "bowl-full", 1 => "bowl-half", _ => "bowl-empty" } });
        _image.RenderTransform = null;
    }

    private void Place(string name)
    {
        var p = BellaArt.Pics[name];
        _image.Source = BellaArt.Picture(name);
        _image.Width = p.width * BellaArt.Px;
        _image.Height = p.height * BellaArt.Px;
        Canvas.SetLeft(_image, (_left - p.base_x) * BellaArt.Px);
        Canvas.SetTop(_image, (_up - p.base_y) * BellaArt.Px);
    }

    public void Say(string text)
    {
        _bubble.Text = text;
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_bubble, Math.Max(0, (_w * BellaArt.Px - _bubble.DesiredSize.Width) / 2));
        Canvas.SetTop(_bubble, 0);
        _bubbleUntil = Environment.TickCount64 + 1800;
        _bubbleTimer.Start();
    }

    private void FadeBubble()
    {
        var left = _bubbleUntil - Environment.TickCount64;
        _bubble.Opacity = left > 0 ? Math.Min(1, left / 400.0) : 0;
        if (left <= 0) _bubbleTimer.Stop();
    }
}
