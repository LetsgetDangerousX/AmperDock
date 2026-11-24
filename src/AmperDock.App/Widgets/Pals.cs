using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmperDock.Core;
using Modes = AmperDock.App.MochiWidget.Modes;

namespace AmperDock.App;

/// <summary>
/// Mochi's Pals, from the owner's game Racing for Pi: Bella the puppy, the bunny and the dino. Like Mochi,
/// each one can wander (running along its row of the desktop, stopping for a breather, hopping, bumping into
/// the edge of the screen with an "ouch!"), stay put, keep running, hop about, nap, or go to Mochi's things:
/// scratch the post, eat from the bowl (eating it down) and go into the house. Asked to do something, a pal
/// keeps at it until it's changed or the pal gets sleepy; then it sleeps (in the house if there is one) and
/// carries on after. Click one and it hops (or wakes up); drag it anywhere. Pals make no sounds.
/// </summary>
public sealed class PalWidget : WidgetWindow
{
    public const string Bella = "pal-bella", Bunny = "pal-bunny", Dino = "pal-dino";
    public static readonly (string Kind, string Name, string Folder, int Jumps)[] All =
        [(Bella, "Bella", "bella", 8), (Bunny, "Bunny", "bunny", 5), (Dino, "Dino", "dino", 5)];

    private const double Tall = 110;              // DIPs
    private const double Speed = 78, TripSpeed = 90;   // DIPs a second: running about, and heading somewhere
    private enum Act { Run, Rest, Hop, Ouch, Sleep, Wake, Trip, AtThing }

    public string PalName { get; }
    private readonly string _folder;
    private readonly int _jumps;
    private readonly double _w;
    private readonly Image _pal = new() { Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 1), VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ScaleTransform _flip = new(1, 1);
    private readonly TranslateTransform _lift = new();
    private readonly TextBlock _bubble = MochiWidget.Bubble();
    private readonly DispatcherTimer _tick;
    private Act _act = Act.Rest;
    private long _started, _until, _bubbleUntil, _last = Environment.TickCount64;
    private long _awakeSince = Environment.TickCount64, _upLateUntil, _nextAsk, _nextZ;
    private int _dir = 1, _visitStep;
    private bool _pressed;
    private Point _pressAt;
    private MochiThingWidget? _thing;

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
        (PalName, _folder, _jumps) = (pal.Name, pal.Folder, pal.Jumps);
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
            if (Math.Abs(now.X - _pressAt.X) + Math.Abs(now.Y - _pressAt.Y) < 6) Clicked();
            else { LeaveThing(); Start(Act.Rest); }   // put down somewhere new
        };
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        Closed += (_, _) => { _tick.Stop(); if (_thing is { } th) { th.Guest(this, false); th.Wiggle(0); _thing = null; } };
        Start(Act.Rest);
    }

    private static readonly string[] Hellos = ["hi!", "wheee!", "hop!", "♥", "race you!"];

    /// <summary>What it keeps doing (older copies only had "stay where I put you").</summary>
    private string Mode => Data.MochiMode ?? (Data.StayPut ? Modes.Stay : Modes.Free);

    protected override bool HasColourMenu => false;

    protected override void ApplyColour()
    {
        base.ApplyColour();
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
    }

    // ---- its menu: the same choices as Mochi's

    protected override IEnumerable<object> MenuItems()
    {
        MenuItem Choice(string header, string mode, bool enabled = true, string? why = null)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = Mode == mode, IsEnabled = enabled, ToolTip = enabled ? null : why };
            mi.Click += (_, _) => SetMode(mode);
            return mi;
        }
        bool Has(string kind) => Manager.Things(kind).Any();
        yield return Choice("_Wander freely", Modes.Free);
        yield return Choice("_Stay where I put you", Modes.Stay);
        yield return Choice("Keep _running", Modes.Walk);
        yield return Choice("_Hop and play", Modes.Play);
        yield return Choice("Take a _nap", Modes.Nap);
        yield return Choice("S_cratch the post", Modes.Scratch, Has(MochiThingWidget.Post), "Put out Mochi's scratch post first");
        yield return Choice("_Eat from the bowl", Modes.Eat, Has(MochiThingWidget.Bowl), "Put out Mochi's food bowl first");
        yield return Choice("Go _home to the house", Modes.Home, Has(MochiThingWidget.House), "Put out Mochi's house first");
        yield return new Separator();
        var things = new MenuItem { Header = "Put out Mochi's _things" };
        foreach (var (kind, name) in new[] { (MochiThingWidget.Post, "_Scratch post"), (MochiThingWidget.Bowl, "_Food bowl"), (MochiThingWidget.House, "Cat _house") })
        {
            var mi = new MenuItem { Header = name, IsEnabled = !Has(kind) };
            mi.Click += (_, _) => Manager.Add(kind);
            things.Items.Add(mi);
        }
        yield return things;
    }

    /// <summary>Asked to do something (from its menu or one of Mochi's things): it's up for it, even at night, for a while.</summary>
    internal void SetMode(string mode)
    {
        Save(Data with { MochiMode = mode, StayPut = mode == Modes.Stay });
        _awakeSince = Environment.TickCount64;
        if (mode != Modes.Nap && mode != Modes.Home) _upLateUntil = _awakeSince + 30 * 60_000L;
        LeaveThing();
        Carry();
        ApplyColour();
    }

    /// <summary>Let out of the house (its menu): wakes, steps out and carries on (running about, if it had been sent home).</summary>
    internal void ExitHouse()
    {
        if (Mode is Modes.Home or Modes.Nap) SetMode(Modes.Free);
        else { _awakeSince = Environment.TickCount64; _upLateUntil = _awakeSince + 30 * 60_000L; }
        LeaveThing();
        Start(Act.Wake);
        Say("hi!");
    }

    internal bool InHouse => _act == Act.AtThing && _thing?.Kind == MochiThingWidget.House;

    protected override void OnDoubleClick() => Start(Act.Hop);

    // diagnostics: AMPERDOCK_MOCHI_DAY=1 makes it daytime (as for Mochi)
    private static readonly bool AlwaysDay = Environment.GetEnvironmentVariable("AMPERDOCK_MOCHI_DAY") == "1";
    private static bool Night => !AlwaysDay && DateTime.Now.Hour is >= 22 or < 7;

    /// <summary>Sleepy: night time, or it's been up and about for a good while (40 to 70 minutes).</summary>
    private bool Sleepy => (Night && Environment.TickCount64 > _upLateUntil) || Environment.TickCount64 - _awakeSince > (40 + (Data.Id.GetHashCode() & 31)) * 60_000L;

    // ---- what it's doing

    private void Start(Act act, double seconds = 0)
    {
        if (act == Act.Run && Mode == Modes.Stay) act = Act.Rest;
        if (_act is Act.Run or Act.Trip && act is not (Act.Run or Act.Trip)) StopRunning();
        if (_act == Act.AtThing && act != Act.AtThing) LeaveThing();
        if (_act == Act.Sleep && act != Act.Sleep) _awakeSince = Environment.TickCount64;
        _act = act;
        _started = Environment.TickCount64;
        var ms = seconds > 0 ? (long)(seconds * 1000) : act switch
        {
            Act.Run => Mode == Modes.Walk ? 3_600_000L : 4000 + Random.Shared.Next(7000),
            Act.Hop => _jumps * 110 + 120,
            Act.Ouch => 1100,
            Act.Wake => 900,
            Act.Sleep => Mode == Modes.Nap ? 3_600_000L : Night ? 900_000 + Random.Shared.Next(900_000) : 900_000 + Random.Shared.Next(900_000),
            Act.Trip => 120_000,          // gives up if it somehow can't get there
            _ => 2500 + Random.Shared.Next(5000),
        };
        _until = _started + ms;
        if (act is Act.Run or Act.Trip)
        {
            SavesMoves = false;
            if (act == Act.Run && Random.Shared.NextDouble() < 0.4) _dir = -_dir;
        }
        _nextZ = _started + 600;
    }

    private void StopRunning()
    {
        if (SavesMoves) return;
        SavesMoves = true;
        Save(Data with { X = Left, Y = Top });
    }

    /// <summary>Carry on with what it's been asked to do (or, wandering freely, pick something).</summary>
    private void Carry()
    {
        if (Sleepy && Mode != Modes.Nap)
        {
            if (Manager.Things(MochiThingWidget.House).FirstOrDefault() is { } house && Mode != Modes.Stay) { GoTo(house); return; }
            Start(Act.Sleep);
            return;
        }
        switch (Mode)
        {
            case Modes.Stay: Start(Random.Shared.NextDouble() < 0.25 ? Act.Hop : Act.Rest); break;
            case Modes.Walk: Start(Act.Run); break;
            case Modes.Play: Start(Random.Shared.NextDouble() < 0.7 ? Act.Hop : Act.Rest, Random.Shared.NextDouble() < 0.7 ? 0 : 1.2); break;
            case Modes.Nap: Start(Act.Sleep); break;
            case Modes.Scratch or Modes.Eat or Modes.Home:
                var kind = Mode == Modes.Scratch ? MochiThingWidget.Post : Mode == Modes.Eat ? MochiThingWidget.Bowl : MochiThingWidget.House;
                if (Manager.Things(kind).FirstOrDefault() is { } thing)
                {
                    if (kind == MochiThingWidget.Bowl && thing.Data.Level == 0 && Near(thing.Approach()))
                    {
                        // an empty bowl: it waits beside it and asks now and then; once it's filled it eats
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

    /// <summary>Left to itself: mostly running about and resting, sometimes hopping, now and then a visit to one of Mochi's things.</summary>
    private void Wander()
    {
        var roll = Random.Shared.NextDouble();
        var things = Manager.Things(null).Where(t => t.Kind != MochiThingWidget.Bowl || t.Data.Level > 0).ToList();
        if (things.Count > 0 && roll < 0.15) { GoTo(things[Random.Shared.Next(things.Count)]); return; }
        Start(_act switch
        {
            Act.Run => roll < 0.45 ? Act.Hop : Act.Rest,
            _ => roll < 0.7 ? Act.Run : roll < 0.85 ? Act.Hop : Act.Rest,
        });
    }

    private void Next()
    {
        switch (_act)
        {
            case Act.Sleep: Start(Act.Wake); break;
            case Act.Ouch: Start(Mode is Modes.Free or Modes.Walk ? Act.Run : Act.Rest); break;
            case Act.AtThing: LeaveThing(); Start(Act.Rest, 2); break;   // a free-time visit is over
            case Act.Trip: Start(Act.Rest); break;
            default: Carry(); break;
        }
    }

    private void Clicked()
    {
        if (_act == Act.Sleep)
        {
            _awakeSince = Environment.TickCount64;
            Start(Act.Wake);
            Say("huh?");
            return;
        }
        if (_act == Act.AtThing) { Say(Hellos[Random.Shared.Next(Hellos.Length)]); return; }   // busy: just a hello
        Start(Act.Hop);
        Say(Hellos[Random.Shared.Next(Hellos.Length)]);
    }

    private void Say(string text)
    {
        _bubble.Text = text;
        _bubbleUntil = Environment.TickCount64 + 1500;
    }

    // ---- going to Mochi's things

    private void GoTo(MochiThingWidget thing)
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

    /// <summary>Where its feet are, in screen DIPs (the middle of the bottom of its picture).</summary>
    private Point Feet() => new(Left + Width / 2, Top + Height - 14 * K);

    private void MoveFeetTo(Point p)
    {
        Left = p.X - Width / 2;
        Top = p.Y - (Height - 14 * K);
    }

    /// <summary>Turn to look at the thing.</summary>
    private void Face(MochiThingWidget thing)
    {
        _dir = thing.Left + thing.Width / 2 < Feet().X ? -1 : 1;
        _flip.ScaleX = _dir;
    }

    private void Arrive()
    {
        if (_thing is not { IsLoaded: true } thing) { _thing = null; Start(Act.Rest); return; }
        StopRunning();
        _visitStep = 0;
        _nextAsk = Environment.TickCount64 + 3_000;
        Face(thing);
        // a free-time visit is a little while; one it was asked for (or bedtime) lasts until something changes
        var stays = Mode != Modes.Free || Sleepy;
        Start(Act.AtThing, stays ? 24 * 3600 : 20 + Random.Shared.NextDouble() * 25);
        if (thing.Kind == MochiThingWidget.House) { thing.Guest(this, true); Hide(); }
    }

    /// <summary>Done at a thing: out of the house (if it was in), standing beside it.</summary>
    private void LeaveThing()
    {
        if (_thing is not { } thing) return;
        _thing = null;
        _lift.Y = 0;
        thing.Wiggle(0);
        if (thing.Kind == MochiThingWidget.House)
        {
            thing.Guest(this, false);
            if (thing.IsLoaded) MoveFeetTo(thing.Approach());
        }
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
        // sleepy in the middle of something it was asked to do: it drops off (and carries on when it wakes)
        var busyElsewhere = _act == Act.AtThing && _thing?.Kind != MochiThingWidget.House;
        if (Sleepy && Mode != Modes.Nap && (_act is Act.Run or Act.Hop or Act.Rest || busyElsewhere) && now - _started > 1500)
        {
            LeaveThing();
            _act = Act.Rest;
            Carry();
            return;
        }
        var t = now - _started;
        _lift.Y = 0;
        _flip.ScaleY = 1;
        switch (_act)
        {
            case Act.Run:
                _pal.Source = Frame(_folder, $"run-{t / 110 % 4 + 1}");
                _flip.ScaleX = _dir;
                Run(dt);
                break;
            case Act.Trip:
                _pal.Source = Frame(_folder, $"run-{t / 110 % 4 + 1}");
                Travel(dt);
                break;
            case Act.Hop:
                var i = (int)Math.Min(_jumps - 1, t / 110);
                _pal.Source = Frame(_folder, $"jump-{i + 1}");
                _lift.Y = -Math.Sin(Math.Min(1, t / (_jumps * 110.0)) * Math.PI) * 26;   // up and down again
                break;
            case Act.Ouch:
                _pal.Source = Frame(_folder, "ouch-1");
                break;
            case Act.Sleep:
                // curled up still, breathing slowly, with the odd "z"
                _pal.Source = Frame(_folder, "run-1");
                _flip.ScaleY = 0.96 + Math.Sin(t / 700.0) * 0.025;
                if (now > _nextZ) { Say("z z z"); _nextZ = now + 4000; }
                break;
            case Act.AtThing:
                Visit(now);
                break;
            default:   // resting, waking up
                _pal.Source = Frame(_folder, "run-1");
                break;
        }
        _bubble.Opacity = now < _bubbleUntil ? Math.Min(1, (_bubbleUntil - now) / 400.0) : 0;
    }

    /// <summary>Running to one of Mochi's things, the straight way, facing where it's going.</summary>
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
        _flip.ScaleX = _dir;
        MoveFeetTo(new Point(feet.X + dx / dist * step, feet.Y + dy / dist * step));
    }

    /// <summary>What it does at one of Mochi's things, a step at a time.</summary>
    private void Visit(long now)
    {
        if (_thing is not { IsLoaded: true } thing) { _thing = null; if (!IsVisible) Show(); Start(Act.Rest); return; }
        var t = now - _started;
        switch (thing.Kind)
        {
            case MochiThingWidget.Post:
                // pawing at the post (it wobbles), with a hop up it now and then
                Face(thing);
                var cycle = t % 9000;
                if (cycle > 7500)
                {
                    var i = (int)Math.Min(_jumps - 1, (cycle - 7500) / 110);
                    _pal.Source = Frame(_folder, $"jump-{i + 1}");
                    _lift.Y = -Math.Sin(Math.Min(1, (cycle - 7500) / (_jumps * 110.0)) * Math.PI) * 30;
                    thing.Wiggle(0);
                }
                else
                {
                    _pal.Source = Frame(_folder, $"run-{t / 140 % 2 + 2}");
                    thing.Wiggle(now / 70 % 2 == 0 ? 1.6 : -1.6);
                }
                break;
            case MochiThingWidget.Bowl:
                Face(thing);
                var level = thing.Data.Level;
                if (_visitStep == 0) { _visitStep = 1; _started = now; t = 0; }
                if (level > 0 && _visitStep == 1)
                {
                    // a few seconds per helping, head bobbing down to the bowl
                    _pal.Source = Frame(_folder, "run-1");
                    _lift.Y = Math.Abs(Math.Sin(now / 180.0)) * 3;
                    if (t > 6000) { thing.SetLevel(level - 1); _started = now; if (level - 1 == 0) { _visitStep = 2; Say("yum!"); } }
                }
                else if (_visitStep == 2 && t < 2500) _pal.Source = Frame(_folder, "run-1");
                else
                {
                    // all gone: it steps away (asked to eat, it waits beside the bowl for more)
                    _nextAsk = now + 8_000;
                    LeaveThing();
                    Start(Act.Rest);
                }
                break;
            case MochiThingWidget.House:
                // inside, out of sight: the house shows the odd "z" once it's asleep
                if (_visitStep == 0 && (Sleepy || t > 25_000)) _visitStep = 99;
                if (_visitStep == 99 && now > _nextZ) { thing.Say($"{PalName}: z z z"); _nextZ = now + 9000 + Random.Shared.Next(6000); }
                if (_visitStep == 99 && Mode != Modes.Home && !Sleepy && t > 15 * 60_000) _until = now;   // a nap's done
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
