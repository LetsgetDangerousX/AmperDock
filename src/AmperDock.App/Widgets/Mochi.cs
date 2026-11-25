using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AmperDock.Core;

namespace AmperDock.App;

/// <summary>
/// Mochi, a little white cat who lives on your desktop. Left to herself ("wander freely") she walks about,
/// sits, plays with her yarn, scratches her post, eats from her bowl and naps in her house. Ask her to do
/// one thing from her right-click menu and she keeps at it until you change your mind or she gets sleepy
/// (at night, or after a long while awake): then she sleeps (in her house if she has one) and carries on when
/// she wakes. Click her and she purrs or meows; double-click and she plays; drag her anywhere, even on top of
/// other widgets. Drawn from replica/design/assets/mochi-*.jpg.
/// </summary>
public sealed class MochiWidget : WidgetWindow
{
    private const double Px = 0.62;                       // picture pixels to DIPs
    private const double FrameW = 338 * Px, FrameH = 203 * Px;
    private const double BubbleRoom = 30;
    private const double WalkSpeed = 46, TripSpeed = 62;  // DIPs a second: strolling, and heading somewhere

    private enum Act { Sit, Walk, Sleep, Wake, Play, Happy, Trip, AtThing }

    /// <summary>What she keeps doing until told otherwise (stored in the widget's settings).</summary>
    public static class Modes
    {
        public const string Free = "free", Stay = "stay", Walk = "walk", Play = "play", Nap = "nap",
            Scratch = "scratch", Eat = "eat", Home = "home";
    }

    private static readonly Dictionary<string, BitmapSource> Poses = new();   // by picture and fur colour

    private static readonly (string Value, string Name)[] Furs =
        [("#FFFFFF", "White"), ("#FFF1DE", "Cream"), ("#D9D4E3", "Grey"), ("#4A4356", "Black"), ("#F2A65A", "Ginger"),
         ("#C79A6B", "Tabby brown"), ("#F7C6D9", "Pink"), ("#D9C6FF", "Lilac"), ("#BFE3FF", "Sky blue")];

    private string _fur = "#FFFFFF";

    private readonly Image _cat = new() { Width = FrameW, Height = FrameH, Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _flip = new(1, 1);
    private readonly TextBlock _bubble;
    private readonly DispatcherTimer _tick;
    private Act _act = Act.Sit;
    private long _actStarted, _actUntil, _bubbleUntil, _lastTick, _awakeSince = Environment.TickCount64;
    private int _dir = 1;                                 // 1 = facing right (the way she's drawn), -1 = left
    private Point _pressAt;
    private bool _pressed;

    // a trip to one of her things, and the visit
    private MochiThingWidget? _thing;                     // where she's going, or where she is
    private Point _tripTo;                                // where her feet are heading (screen DIPs)
    private int _visitStep;
    private long _nextPerch, _nextAsk;

    public MochiWidget(DesktopWidget data, DesktopWidgetManager manager) : base(data, manager, FrameW, FrameH + BubbleRoom)
    {
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
        Card.Effect = null;
        RenderOptions.SetBitmapScalingMode(_cat, BitmapScalingMode.HighQuality);
        _cat.RenderTransform = _flip;
        _bubble = Bubble();
        var body = new Grid { Width = FrameW, Height = FrameH + BubbleRoom };
        _cat.VerticalAlignment = VerticalAlignment.Bottom;
        body.Children.Add(_cat);
        body.Children.Add(new Border { Child = _bubble, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top });
        Card.Child = body;

        // an older Mochi was tall and narrow: don't squash the new one into that size
        if (Data.Width > 0 && Data.Height > Data.Width * 0.9) { Width = FrameW + 28; Height = FrameH + BubbleRoom + 28; Save(Data with { Width = 0, Height = 0 }); }
        // older copies only knew "stay where I put you"
        if (Data.MochiMode is null && Data.StayPut) Save(Data with { MochiMode = Modes.Stay });

        Card.PreviewMouseLeftButtonDown += (_, e) => { _pressed = true; _pressAt = PointToScreen(e.GetPosition(this)); StopMoving(); };
        Card.MouseLeftButtonUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            var now = PointToScreen(e.GetPosition(this));
            if (Math.Abs(now.X - _pressAt.X) + Math.Abs(now.Y - _pressAt.Y) < 6) Clicked();
            else Start(Act.Sit);   // put down somewhere new: she sits there a moment, then carries on
        };
        _lastTick = Environment.TickCount64;
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _tick.Start();
        // closing: let go of the thing she's at, without showing this (closed) window again
        Closed += (_, _) => { _tick.Stop(); if (_thing is { } th) { _thing = null; if (th.Visitor == this) th.Visit(null); } };
        // diagnostics: AMPERDOCK_MOCHI=walk|play|sleep|sit starts her doing that; AMPERDOCK_MOCHI_MODE sets what she keeps doing
        if (Environment.GetEnvironmentVariable("AMPERDOCK_MOCHI_MODE") is { } m) Save(Data with { MochiMode = m });
        if (Enum.TryParse<Act>(Environment.GetEnvironmentVariable("AMPERDOCK_MOCHI"), true, out var first)) Start(first);
        else Dispatcher.BeginInvoke(Carry, DispatcherPriority.ApplicationIdle);   // once her things are on the desktop too
    }

    private string Mode => Data.MochiMode ?? Modes.Free;

    internal static TextBlock Bubble() => new()
    {
        Text = "purr~", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x1F, 0x3D)),
        Padding = new Thickness(9, 3, 9, 3), Opacity = 0, IsHitTestVisible = false,
        Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
    };

    // ---- pictures and fur

    /// <summary>A picture in a fur colour (made once, then kept). Scenes with her things only dye her, not them.</summary>
    internal static ImageSource Picture(string path, string fur, bool scene = false)
    {
        var key = $"{path}|{fur}";
        if (Poses.TryGetValue(key, out var done)) return done;
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/Assets/Mochi/{path}.png");
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        var colour = Controls.ColorPicker.Parse(fur) ?? Colors.White;
        BitmapSource result = colour == Colors.White ? img : Recolour(img, colour, scene);
        result.Freeze();
        return Poses[key] = result;
    }

    /// <summary>
    /// Dye her fur: the white, colourless parts take the fur colour with their shading kept (multiplied),
    /// while colourful parts (pink ears, blush, the yarn) and her dark outline stay as drawn. On a dark coat
    /// the outline and eyes go light instead, so her face still shows. In scenes with her things only the
    /// whitest parts count as fur, so the cream post and the house stay their own colours.
    /// </summary>
    private static BitmapSource Recolour(BitmapSource src, Color fur, bool scene)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        var darkCoat = !scene && WidgetColours.Luma(fur) < 0.35;
        for (var i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] == 0) continue;
            double b = px[i], g = px[i + 1], r = px[i + 2];
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var sat = max <= 0 ? 0 : (max - min) / max;
            var lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
            var furness = scene
                ? Math.Clamp((0.13 - sat) / 0.06, 0, 1) * Math.Clamp((lum - 0.72) / 0.12, 0, 1)
                : Math.Clamp((0.42 - sat) / 0.3, 0, 1) * Math.Clamp((lum - 0.45) / 0.25, 0, 1);
            double nr = r, ng = g, nb = b;
            if (furness > 0)
            {
                nr = r + (r * fur.R / 255.0 - r) * furness;
                ng = g + (g * fur.G / 255.0 - g) * furness;
                nb = b + (b * fur.B / 255.0 - b) * furness;
            }
            if (darkCoat && lum < 0.45 && sat < 0.45)
            {
                var t = Math.Clamp((0.45 - lum) / 0.3, 0, 1);
                nr += (0xFF - nr) * t * 0.85;
                ng += (0xF1 - ng) * t * 0.85;
                nb += (0xE0 - nb) * t * 0.85;
            }
            px[i] = (byte)Math.Round(Math.Clamp(nb, 0, 255));
            px[i + 1] = (byte)Math.Round(Math.Clamp(ng, 0, 255));
            px[i + 2] = (byte)Math.Round(Math.Clamp(nr, 0, 255));
        }
        return BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Bgra32, null, px, stride);
    }

    internal string Fur => _fur;

    protected override string ColourMenuName => "_Fur colour";

    protected override IEnumerable<(string Value, string Name, Color Top, Color Bottom)> ColourChoices() =>
        new[] { (WallpaperColour, "Match my wallpaper", Color.FromRgb(0xB9, 0xA4, 0xFF), Color.FromRgb(0x7E, 0xD9, 0xAE)) }
            .Concat(Furs.Select(f => { var c = (Color)ColorConverter.ConvertFromString(f.Value); return (f.Value, f.Name, c, c); }));

    /// <summary>Her coat when it follows the wallpaper: a soft pastel of the wallpaper's most colourful shade.</summary>
    private string WallpaperFur()
    {
        var c = Colours;
        static double Sat(Color k) { var max = Math.Max(k.R, Math.Max(k.G, k.B)); return max == 0 ? 0 : (max - Math.Min(k.R, Math.Min(k.G, k.B))) / (double)max; }
        var main = new[] { c.Top, c.Bottom, c.Accent }.OrderByDescending(Sat).First();
        var pastel = WidgetColours.Mix(main, Colors.White, 0.35);
        return $"#{pastel.R:X2}{pastel.G:X2}{pastel.B:X2}";
    }

    protected override void ApplyColour()
    {
        base.ApplyColour();                 // builds the right-click menu
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
        var fur = Data.Colour == WallpaperColour ? WallpaperFur()
            : Controls.ColorPicker.Parse(Data.Colour) is not null ? Data.Colour : "#FFFFFF";
        if (!string.Equals(fur, _fur, StringComparison.OrdinalIgnoreCase))
        {
            // forget the old coat's pictures (a slideshow would otherwise pile up a set per wallpaper)
            var old = _fur;
            if (!Furs.Any(f => string.Equals(f.Value, old, StringComparison.OrdinalIgnoreCase)))
                foreach (var key in Poses.Keys.Where(k => k.EndsWith("|" + old, StringComparison.OrdinalIgnoreCase)).ToList()) Poses.Remove(key);
            _fur = fur;
            if (_shown is { } pose) { _shown = null; Show(pose); }
            _thing?.Repaint();
        }
    }

    // ---- her menu

    protected override IEnumerable<object> MenuItems()
    {
        MenuItem Choice(string header, string mode, bool enabled = true, string? why = null)
        {
            var mi = new MenuItem { Header = header, IsCheckable = true, IsChecked = Mode == mode, IsEnabled = enabled, ToolTip = why };
            mi.Click += (_, _) => SetMode(mode);
            return mi;
        }
        bool Has(string kind) => Manager.Things(kind).Any();
        yield return Choice("_Wander freely", Modes.Free);
        yield return Choice("_Stay where I put you", Modes.Stay);
        yield return Choice("Keep _walking", Modes.Walk);
        yield return Choice("_Play with yarn", Modes.Play);
        yield return Choice("Take a _nap", Modes.Nap);
        yield return Choice("S_cratch the post", Modes.Scratch, Has(MochiThingWidget.Post), "Put out her scratch post first");
        yield return Choice("_Eat from the bowl", Modes.Eat, Has(MochiThingWidget.Bowl), "Put out her food bowl first");
        yield return Choice("Go _home to her house", Modes.Home, Has(MochiThingWidget.House), "Put out her house first");
        yield return new Separator();
        var things = new MenuItem { Header = "Put out her _things" };
        foreach (var (kind, name) in new[] { (MochiThingWidget.Post, "_Scratch post"), (MochiThingWidget.Bowl, "_Food bowl"), (MochiThingWidget.House, "Cat _house") })
        {
            var mi = new MenuItem { Header = name, IsEnabled = !Has(kind) };
            mi.Click += (_, _) => Manager.Add(kind);
            things.Items.Add(mi);
        }
        yield return things;
        var sounds = new MenuItem { Header = "Purr and meow when _clicked", IsCheckable = true, IsChecked = !Data.Muted };
        sounds.Click += (_, _) => { Save(Data with { Muted = !sounds.IsChecked }); ApplyColour(); };
        yield return sounds;
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

    protected override void OnDoubleClick() => Start(Act.Play);

    // diagnostics: AMPERDOCK_MOCHI_DAY=1 makes it daytime for her (for checking what she does awake)
    private static readonly bool AlwaysDay = Environment.GetEnvironmentVariable("AMPERDOCK_MOCHI_DAY") == "1";
    private static bool Night => !AlwaysDay && DateTime.Now.Hour is >= 22 or < 7;

    /// <summary>Sleepy: night time, or she's been up and about for a good while (40 to 70 minutes).</summary>
    private bool Sleepy => (Night && Environment.TickCount64 > _upLateUntil) || Environment.TickCount64 - _awakeSince > (40 + (Data.Id.GetHashCode() & 31)) * 60_000L;

    /// <summary>Until when she stays up at night, having been asked to do something or let out of her house.</summary>
    private long _upLateUntil;

    /// <summary>Let out of her house (its menu): she wakes, steps out and carries on (wandering, if she'd been sent home).</summary>
    internal void ExitHouse()
    {
        if (Mode is Modes.Home or Modes.Nap) SetMode(Modes.Free);
        else { _awakeSince = Environment.TickCount64; _upLateUntil = _awakeSince + 30 * 60_000L; }
        LeaveThing();
        Start(Act.Wake);
        Say("mrrp!");
    }

    internal bool InHouse => _act == Act.AtThing && _thing?.Kind == MochiThingWidget.House;

    // ---- what she's doing

    private void Start(Act act, double seconds = 0)
    {
        if (act == Act.Walk && Mode == Modes.Stay) act = Act.Sit;
        if (_act is Act.Walk or Act.Trip && act is not (Act.Walk or Act.Trip)) StopMoving();
        if (_act == Act.AtThing && act != Act.AtThing) LeaveThing();
        if (_act == Act.Sleep && act != Act.Sleep) _awakeSince = Environment.TickCount64;
        _act = act;
        _actStarted = Environment.TickCount64;
        seconds = seconds > 0 ? seconds : act switch
        {
            Act.Walk => Mode == Modes.Walk ? 3600 : 4 + Random.Shared.NextDouble() * 8,
            Act.Sleep => Mode == Modes.Nap ? 3600 : Night ? 900 + Random.Shared.NextDouble() * 900 : 15 * 60 + Random.Shared.NextDouble() * 15 * 60,
            Act.Wake => 1.8,
            Act.Play => 6.5,
            Act.Happy => 1.8,
            Act.Trip => 120,               // gives up if she somehow can't get there
            _ => 3 + Random.Shared.NextDouble() * 6,
        };
        _actUntil = _actStarted + (long)(seconds * 1000);
        if (act is Act.Walk or Act.Trip)
        {
            SavesMoves = false;
            if (act == Act.Walk && Random.Shared.NextDouble() < 0.4) _dir = -_dir;
        }
        if (act != Act.AtThing) Show(PoseFor(0));
        ApplyColour();
    }

    private void StopMoving()
    {
        if (SavesMoves) return;
        SavesMoves = true;
        Save(Data with { X = Left, Y = Top });
    }

    /// <summary>Carry on with what she's been asked to do (or, wandering freely, pick something).</summary>
    private void Carry()
    {
        if (Sleepy && Mode != Modes.Nap)
        {
            // sleepy: bed (her house if she has one), however busy she was
            if (Manager.Things(MochiThingWidget.House).FirstOrDefault() is { } house && Mode != Modes.Stay) { GoTo(house); return; }
            Start(Act.Sleep);
            return;
        }
        switch (Mode)
        {
            case Modes.Stay: Start(Random.Shared.NextDouble() < 0.25 ? Act.Play : Act.Sit); break;
            case Modes.Walk: Start(Act.Walk); break;
            case Modes.Play: Start(Act.Play); break;
            case Modes.Nap: Start(Act.Sleep); break;
            case Modes.Scratch or Modes.Eat or Modes.Home:
                var kind = Mode == Modes.Scratch ? MochiThingWidget.Post : Mode == Modes.Eat ? MochiThingWidget.Bowl : MochiThingWidget.House;
                if (Manager.Things(kind).FirstOrDefault() is { } thing)
                {
                    if (kind == MochiThingWidget.Bowl && thing.Data.Level == 0 && Near(thing.Approach()))
                    {
                        // an empty bowl: she sits beside it and asks now and then; once it's filled she eats
                        _dir = thing.Approach().X < thing.Left + thing.Width / 2 ? 1 : -1;
                        _flip.ScaleX = _dir;
                        Start(Act.Sit);
                        if (Environment.TickCount64 > _nextAsk) { Say("food?"); _nextAsk = Environment.TickCount64 + 25_000; }
                    }
                    else GoTo(thing);
                }
                else { Save(Data with { MochiMode = Modes.Free }); Carry(); }   // the thing was put away
                break;
            default: Wander(); break;
        }
    }

    /// <summary>Left to herself: mostly strolling and sitting, sometimes playing, now and then a visit to one of her things.</summary>
    private void Wander()
    {
        var roll = Random.Shared.NextDouble();
        var things = Manager.Things(null).Where(t => t.Kind != MochiThingWidget.Bowl || t.Data.Level > 0).ToList();
        if (_act != Act.Trip && things.Count > 0 && roll < 0.18) { GoTo(things[Random.Shared.Next(things.Count)]); return; }
        if (_act == Act.Walk) Start(roll < 0.75 ? Act.Sit : Act.Play);
        else Start(roll < 0.6 ? Act.Walk : roll < 0.8 ? Act.Play : Act.Sit);
    }

    private void Next()
    {
        switch (_act)
        {
            case Act.Sleep: Start(Act.Wake); break;
            case Act.Wake or Act.Happy: Carry(); break;
            case Act.AtThing: LeaveThing(); Start(Act.Sit, 2); break;   // a free-time visit is over
            case Act.Trip: Start(Act.Sit); break;
            default: Carry(); break;
        }
    }

    private void Clicked()
    {
        if (_act == Act.Sleep || (_act == Act.AtThing && _visitStep == 99))
        {
            // woken up: a stretch and a sleepy little meow, then back to what she was doing
            _awakeSince = Environment.TickCount64;
            LeaveThing();
            Start(Act.Wake);
            Say("mrrp?");
            if (!Data.Muted) MochiSounds.Meow();
            return;
        }
        if (_act == Act.AtThing) { _thing?.Say(Hellos[Random.Shared.Next(Hellos.Length)]); PlayHello(); return; }
        Start(Act.Happy);
        Say(Hellos[Random.Shared.Next(Hellos.Length)]);
        PlayHello();
    }

    private void PlayHello()
    {
        if (Data.Muted) return;
        if (Random.Shared.NextDouble() < 0.55) MochiSounds.Purr(); else MochiSounds.Meow();
    }

    /// <summary>Clicked in a scene at one of her things (the thing passes it on).</summary>
    internal void ClickedAtThing() => Clicked();

    private static readonly string[] Hellos = ["purr~", "mrrp!", "hi!", "nya~", "♥", "*slow blink*"];

    private void Say(string text)
    {
        _bubble.Text = text;
        _bubbleUntil = Environment.TickCount64 + 1800;
    }

    // ---- going to her things

    private void GoTo(MochiThingWidget thing)
    {
        _thing = thing;
        _tripTo = thing.Approach();
        if (Near(_tripTo)) { Arrive(); return; }
        Start(Act.Trip);
    }

    private bool Near(Point p)
    {
        var feet = Feet();
        return Math.Abs(feet.X - p.X) + Math.Abs(feet.Y - p.Y) < 8;
    }

    /// <summary>Where her feet are, in screen DIPs (the middle of the bottom of her picture).</summary>
    private Point Feet()
    {
        var k = ActualWidth > 0 ? ActualWidth / (FrameW + 28) : 1;
        return new Point(Left + Width / 2, Top + Height - 14 * k);
    }

    private void MoveFeetTo(Point p)
    {
        var k = ActualWidth > 0 ? ActualWidth / (FrameW + 28) : 1;
        Left = p.X - Width / 2;
        Top = p.Y - (Height - 14 * k);
    }

    private void Arrive()
    {
        if (_thing is not { IsLoaded: true } thing) { Start(Act.Sit); return; }
        StopMoving();
        _visitStep = 0;
        _nextPerch = Environment.TickCount64 + 18_000 + Random.Shared.Next(15_000);
        _nextAsk = Environment.TickCount64 + 3_000;
        // a free-time visit is a little while; one she was asked for (or bedtime) lasts until something changes
        var stays = Mode != Modes.Free || Sleepy;
        Start(Act.AtThing, stays ? 24 * 3600 : 20 + Random.Shared.NextDouble() * 25);
        Hide();
        thing.Visit(this);
    }

    /// <summary>Step out of a scene: the thing shows itself again and she stands beside it.</summary>
    private void LeaveThing()
    {
        if (_thing is not { } thing) return;
        _thing = null;
        if (thing.Visitor != this) return;
        thing.Visit(null);
        if (thing.IsLoaded) MoveFeetTo(thing.Approach());
        if (!IsVisible) Show();
        StopMoving();
    }

    // ---- every frame

    private string PoseFor(long ms) => _act switch
    {
        Act.Walk or Act.Trip => $"walk-{ms / 115 % 8 + 1}",   // one calm face; her legs, body and tail do the moving
        Act.Sleep => ms % 9000 is > 6000 and < 6350 ? "sleep-twitch" : ms / 1300 % 2 == 0 ? "sleep" : "sleep-breathe",
        Act.Wake => "stretch",
        Act.Happy => "happy",
        Act.Play => ms switch
        {
            < 900 => "play-crouch",
            < 1500 => "play-bat",
            < 2100 => "play-crouch",
            < 2700 => "play-bat",
            < 4500 => "play-roll",
            _ => "play-sit",
        },
        _ => "stand",
    };

    private string? _shown;

    private void Show(string pose)
    {
        if (pose == _shown) return;
        _shown = pose;
        _cat.Source = Picture(pose, _fur);
    }

    private void Tick()
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.2, (now - _lastTick) / 1000.0);
        _lastTick = now;
        if (_pressed) return;   // being dragged
        if (now >= _actUntil) Next();
        // sleepy in the middle of something she was asked to do: she drops off (and carries on when she wakes)
        var busyElsewhere = _act == Act.AtThing && _thing?.Kind != MochiThingWidget.House;
        if (Sleepy && Mode != Modes.Nap && (_act is Act.Walk or Act.Play or Act.Sit || busyElsewhere) && now - _actStarted > 1500)
        {
            LeaveThing();
            _act = Act.Sit;
            Carry();
            return;
        }
        switch (_act)
        {
            case Act.AtThing: Visit(now); break;
            case Act.Trip: Travel(dt); break;
            case Act.Walk: Show(PoseFor(now - _actStarted)); _flip.ScaleX = _dir; Walk(dt); break;
            default: Show(PoseFor(now - _actStarted)); break;
        }
        _bubble.Opacity = now < _bubbleUntil ? Math.Min(1, (_bubbleUntil - now) / 400.0) : 0;
    }

    /// <summary>Walking to one of her things, the straight way, facing where she's going.</summary>
    private void Travel(double dt)
    {
        if (_thing is not { IsLoaded: true } thing) { Start(Act.Sit); return; }
        _tripTo = thing.Approach();   // it may have been moved meanwhile
        var feet = Feet();
        var (dx, dy) = (_tripTo.X - feet.X, _tripTo.Y - feet.Y);
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 4) { Arrive(); return; }
        var step = Math.Min(dist, TripSpeed * dt);
        if (Math.Abs(dx) > 1) _dir = dx > 0 ? 1 : -1;
        _flip.ScaleX = _dir;
        Show(PoseFor(Environment.TickCount64 - _actStarted));
        MoveFeetTo(new Point(feet.X + dx / dist * step, feet.Y + dy / dist * step));
    }

    /// <summary>What she does at one of her things, a step at a time.</summary>
    private void Visit(long now)
    {
        if (_thing is not { IsLoaded: true } thing || thing.Visitor != this) { _thing = null; if (!IsVisible) Show(); Start(Act.Sit); return; }
        var t = now - _actStarted;
        switch (thing.Kind)
        {
            case MochiThingWidget.Post:
                // scratching, with a perch on top now and then
                if (now > _nextPerch && _visitStep == 0) { _visitStep = 1; _nextPerch = now + 7000; }
                else if (now > _nextPerch && _visitStep == 1) { _visitStep = 0; _nextPerch = now + 20_000 + Random.Shared.Next(20_000); }
                if (_visitStep == 1) thing.Scene("post-perch", shake: 0, bob: Math.Sin(now / 500.0) * 1.2);
                else thing.Scene("post-scratch", shake: (now / 70 % 2 == 0 ? 1.8 : -1.8), bob: 0);
                break;
            case MochiThingWidget.Bowl:
                var level = thing.Data.Level;
                if (_visitStep == 0) { _visitStep = 1; _actStarted = now; t = 0; }
                if (level > 0 && _visitStep == 1)
                {
                    // a few seconds per helping, a little bob of the head while she munches
                    thing.Scene(level == 2 ? "eat-full" : "eat-half", shake: 0, bob: Math.Abs(Math.Sin(now / 180.0)) * 2);
                    if (t > 6000) { thing.SetLevel(level - 1); _actStarted = now; if (level - 1 == 0) _visitStep = 2; }
                }
                else if (_visitStep == 2 && t < 3500) thing.Scene("eat-done", shake: 0, bob: 0);   // licking her lips
                else
                {
                    // all gone: she steps away from the bowl (asked to eat, she waits beside it for more)
                    _nextAsk = now + 8_000;
                    LeaveThing();
                    Start(Act.Sit);
                }
                break;
            case MochiThingWidget.House:
                // peeking out for a while, then curled up asleep inside; at bedtime, straight to sleep
                if (_visitStep == 0 && (Sleepy || t > 25_000)) _visitStep = 99;
                thing.Scene(_visitStep == 99 ? "house-sleep" : "house-peek", shake: 0, bob: _visitStep == 99 ? Math.Sin(now / 900.0) * 0.8 : 0);
                if (_visitStep == 99 && Mode != Modes.Home && !Sleepy && t > 15 * 60_000) _actUntil = now;   // a nap's done
                break;
        }
    }

    /// <summary>A few steps along, turning round at the edges of the screen she's on.</summary>
    private void Walk(double dt)
    {
        if (PresentationSource.FromVisual(this) is null) return;
        var (left, right) = ScreenEdges();
        var x = Left + _dir * WalkSpeed * dt;
        if (x < left - 14) { x = left - 14; _dir = 1; }
        if (x + Width > right + 14) { x = right + 14 - Width; _dir = -1; }
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
        if (hwnd == 0 || !GetMonitorInfoW(MonitorFromWindow(hwnd, 2 /* nearest */), ref mi))
            return (SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return (mi.rcWork.Left / scale, mi.rcWork.Right / scale);
    }
}

/// <summary>
/// One of Mochi's things on the desktop: her scratch post, food bowl or cat house. Drag it where you like.
/// When Mochi comes to it, the thing shows her scene with it (lined up on the thing's base) while she's
/// there. Double-click the bowl, or use its menu, to fill it up.
/// </summary>
public sealed class MochiThingWidget : WidgetWindow
{
    public const string Post = "mochi-post", Bowl = "mochi-bowl", House = "mochi-house";
    private const double Px = 0.62;

    private sealed record Pic(string thing, double width, double height, double base_x, double base_y);

    private static readonly Dictionary<string, Pic> Pics = LoadPics();

    private static Dictionary<string, Pic> LoadPics()
    {
        using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Mochi/Things/things.json"))!.Stream;
        return JsonSerializer.Deserialize<Dictionary<string, Pic>>(s) ?? [];
    }

    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _bubble = MochiWidget.Bubble();
    private readonly double _left, _up, _w, _h;   // the picture area: room left of and above the base, in picture pixels
    private string? _sceneName;

    public string Kind => Data.Kind;
    public MochiWidget? Visitor { get; private set; }

    private static IEnumerable<string> PicturesOf(string kind) => Pics.Where(p => p.Value.thing == kind switch { Post => "post", Bowl => "bowl", _ => "house" }).Select(p => p.Key);

    private static (double left, double up, double w, double h) Room(string kind)
    {
        double l = 0, r = 0, u = 0, d = 0;
        foreach (var name in PicturesOf(kind))
        {
            var p = Pics[name];
            l = Math.Max(l, p.base_x); r = Math.Max(r, p.width - p.base_x);
            u = Math.Max(u, p.base_y); d = Math.Max(d, p.height - p.base_y);
        }
        return (l, u + 30 / Px, l + r, u + 30 / Px + d);   // a little headroom for her speech bubble
    }

    public MochiThingWidget(DesktopWidget data, DesktopWidgetManager manager)
        : this(data, manager, Room(data.Kind)) { }

    private MochiThingWidget(DesktopWidget data, DesktopWidgetManager manager, (double left, double up, double w, double h) room)
        : base(data, manager, room.w * Px, room.h * Px)
    {
        (_left, _up, _w, _h) = room;
        Card.Background = Brushes.Transparent;
        Card.BorderThickness = new Thickness(0);
        Card.Effect = null;
        _canvas.Width = _w * Px;
        _canvas.Height = _h * Px;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _canvas.Children.Add(_image);
        _canvas.Children.Add(_bubble);
        Card.Child = _canvas;
        Card.MouseLeftButtonUp += (_, _) => { if (Visitor is { } m && !_dragged) m.ClickedAtThing(); };
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
        // Mochi herself (the one visiting, else the first one out); in her house she's hidden, so her things speak for her
        var mochi = Visitor ?? Manager.Mochis().FirstOrDefault();
        IEnumerable<object> Ask(string verb, string mode)
        {
            var mi = new MenuItem { Header = $"Let Mochi _{verb}", IsEnabled = mochi is not null, ToolTip = mochi is null ? "Add Mochi from the Widgets tab first" : null };
            mi.Click += (_, _) => mochi?.SetMode(mode);
            yield return mi;
        }
        switch (Kind)
        {
            case Post:
                foreach (var mi in Ask("scratch", MochiWidget.Modes.Scratch)) yield return mi;
                break;
            case Bowl:
                foreach (var mi in Ask("eat", MochiWidget.Modes.Eat)) yield return mi;
                yield return new Separator();
                var fill = new MenuItem { Header = "_Fill the bowl", IsEnabled = Data.Level < 2 };
                fill.Click += (_, _) => SetLevel(2);
                yield return fill;
                break;
            case House:
                var mochiIn = Manager.Mochis().Where(m => m.InHouse).ToList();
                var exit = new MenuItem { Header = "E_xit", IsEnabled = mochiIn.Count > 0, ToolTip = mochiIn.Count > 0 ? "Let Mochi out of her house" : "Mochi isn't in her house" };
                exit.Click += (_, _) => { foreach (var m in mochiIn) m.ExitHouse(); };
                yield return exit;
                yield return new Separator();
                foreach (var mi in Ask("go home", MochiWidget.Modes.Home)) yield return mi;
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

    /// <summary>Where Mochi stands to come to it (her feet, screen DIPs): beside it, on the side her scenes put her.</summary>
    public Point Approach()
    {
        var b = BaseOnScreen();
        var k = Scale;
        var side = Kind switch { Post => 1.0, Bowl => -1.0, _ => -1.0 };
        var gap = Kind switch { Post => 150, Bowl => 130, _ => 170 } * Px * k;
        return new Point(b.X + side * gap, b.Y);
    }

    private double Scale => ActualWidth > 0 ? ActualWidth / (_w * Px + 28) : 1;

    private Point BaseOnScreen()
    {
        var k = Scale;
        return new Point(Left + (14 + _left * Px) * k, Top + (14 + _up * Px) * k);
    }

    /// <summary>Mochi arrives (or leaves, with null).</summary>
    public void Visit(MochiWidget? mochi)
    {
        Visitor = mochi;
        if (mochi is null) { _sceneName = null; Repaint(); }
    }

    /// <summary>Show one of her scenes here, lined up on this thing's base; shake and bob move it a touch.</summary>
    public void Scene(string name, double shake, double bob, bool bowlEmpty = false)
    {
        // waiting by an empty bowl: the arriving scene has a full bowl in it, so show the empty bowl instead
        // and stand her beside it in the picture's place
        var pic = bowlEmpty ? "eat-arrive" : name;
        if (pic != _sceneName) { _sceneName = pic; Place(pic, Visitor?.Fur ?? "#FFFFFF", scene: true); }
        _image.RenderTransform = new TranslateTransform(shake, -bob);
    }

    /// <summary>Its own picture (the bowl as full as it is).</summary>
    public void Repaint()
    {
        if (_sceneName is { } scene) { Place(scene, Visitor?.Fur ?? "#FFFFFF", scene: true); return; }
        var name = Kind switch { Post => "post", House => "house", _ => Data.Level switch { 2 => "bowl-full", 1 => "bowl-half", _ => "bowl-empty" } };
        Place(name, "#FFFFFF", scene: false);
        _image.RenderTransform = null;
    }

    private void Place(string name, string fur, bool scene)
    {
        var p = Pics[name];
        _image.Source = MochiWidget.Picture("Things/" + name, fur, scene);
        _image.Width = p.width * Px;
        _image.Height = p.height * Px;
        Canvas.SetLeft(_image, (_left - p.base_x) * Px);
        Canvas.SetTop(_image, (_up - p.base_y) * Px);
    }

    public void Say(string text)
    {
        _bubble.Text = text;
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_bubble, Math.Max(0, (_w * Px - _bubble.DesiredSize.Width) / 2));
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

/// <summary>
/// Mochi's purrs and meows: real recordings of cats (public domain or CC0, from Wikimedia Commons; see
/// Assets/Mochi/Sounds/CREDITS.txt). Played only when she's clicked. A purr plays for a few seconds and fades out.
/// </summary>
public static class MochiSounds
{
    private static readonly MediaPlayer Player = new();
    private static DispatcherTimer? _fade;

    private static string FileFor(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "Mochi", "Sounds", name + ".wav");

    public static void Purr() => Play("purr", seconds: 3.5);

    public static void Meow() => Play($"meow-{Random.Shared.Next(1, 6)}", seconds: 0);

    private static void Play(string name, double seconds)
    {
        var file = FileFor(name);
        if (!File.Exists(file)) return;
        try
        {
            _fade?.Stop();
            Player.Stop();
            Player.Volume = 0.55;
            Player.Open(new Uri(file));
            Player.Play();
            if (seconds <= 0) return;
            var start = Environment.TickCount64;
            _fade = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
            {
                var t = (Environment.TickCount64 - start) / 1000.0;
                if (t < seconds - 0.8) return;
                Player.Volume = Math.Max(0, 0.55 * (seconds - t) / 0.8);
                if (t >= seconds) { Player.Stop(); _fade?.Stop(); }
            }, Dispatcher.CurrentDispatcher);
            _fade.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { App.Log(ex); }
    }
}
