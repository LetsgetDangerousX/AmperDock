using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmperDock.Core;
using AmperDock.Shell;

namespace AmperDock.App.Dock;

/// <summary>
/// Dock widgets: items that draw live pictures instead of an app's icon. Mochi the mascot, a clock, a
/// calendar page, the Recycle Bin and a CPU-and-memory meter. Each is drawn here in the app's style
/// (soft shapes, ink outlines, little faces) and redrawn only when what it shows changes.
/// </summary>
public static class Widgets
{
    public const string Prefix = "widget:";

    public static readonly (string Kind, string Name)[] All =
        [("mochi", "Mochi"), ("clock", "Clock"), ("calendar", "Calendar"), ("recyclebin", "Recycle Bin"), ("system", "CPU and memory")];

    public static string? KindOf(DockItem item) =>
        item.Type == ItemTypes.Widget && item.Target is { } t && t.StartsWith(Prefix, StringComparison.Ordinal) ? t[Prefix.Length..] : null;

    public static NewItem New(string kind) => new(ItemTypes.Widget, All.First(w => w.Kind == kind).Name, Prefix + kind);

    // ---------------------------------------------------------------- live values

    public enum Mood { Awake, Sleepy, Happy }

    /// <summary>What the widgets show, refreshed by the dock once a second.</summary>
    public sealed class State
    {
        public DateTime Now = DateTime.Now;
        public int Cpu, Memory;
        public long BinItems;
        public Mood Mochi;
        public bool Blink;
        public long HappyUntil;

        private ulong _idle, _kernel, _user;

        public void Sample(bool cpuAndMemory, bool bin)
        {
            Now = DateTime.Now;
            if (cpuAndMemory)
            {
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    ulong di = idle - _idle, dk = kernel - _kernel, du = user - _user, total = dk + du;
                    if (_kernel != 0 && total > 0) Cpu = (int)Math.Clamp(100 - di * 100 / total, 0, 100);
                    (_idle, _kernel, _user) = (idle, kernel, user);
                }
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref mem)) Memory = (int)mem.dwMemoryLoad;
            }
            if (bin)
            {
                var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBin(null, ref info) == 0) BinItems = info.i64NumItems;
            }
            Mochi = Environment.TickCount64 < HappyUntil ? Mood.Happy : Now.Hour >= 22 || Now.Hour < 7 ? Mood.Sleepy : Mood.Awake;
        }

        /// <summary>A short summary per widget; when it changes, the widget's picture is redrawn.</summary>
        public string Key(string kind) => kind switch
        {
            "clock" => Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            "calendar" => Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "recyclebin" => BinItems == 0 ? "empty" : "full",
            "system" => $"{Cpu / 4}-{Memory / 4}",   // steps of 4% so the picture doesn't flicker
            "mochi" => $"{Mochi}-{Blink}",
            _ => "",
        };

        public string Label(string kind, string name) => kind switch
        {
            "clock" => $"{Now.ToString("t", CultureInfo.CurrentCulture)} · {Now.ToString("dddd d MMMM", CultureInfo.CurrentCulture)}",
            "calendar" => Now.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture),
            "recyclebin" => BinItems switch { 0 => $"{name} · empty", 1 => $"{name} · 1 item", var n => $"{name} · {n:N0} items" },
            "system" => $"CPU {Cpu}% · Memory {Memory}%",
            "mochi" => Mochi switch { Mood.Sleepy => $"{name} is napping", Mood.Happy => $"{name} is happy", _ => name },
            _ => name,
        };
    }

    // ---------------------------------------------------------------- pictures

    private static readonly Color Ink = Color.FromRgb(0x2A, 0x1F, 0x3D);
    private static readonly Brush InkBrush = Frozen(new SolidColorBrush(Ink));
    private static readonly Brush Blush = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0xF4, 0xA6, 0xC8)));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static Brush B(string hex) => Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
    private static Pen P(Brush b, double w) => Frozen(new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    /// <summary>The widget's picture, drawn on a 64-unit square scaled to <paramref name="px"/> pixels.</summary>
    public static IconPixels Render(string kind, State s, int px)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / 64.0, px / 64.0));
            switch (kind)
            {
                case "clock": Clock(dc, s.Now); break;
                case "calendar": Calendar(dc, s.Now); break;
                case "recyclebin": Bin(dc, s.BinItems > 0); break;
                case "system": Meters(dc, s.Cpu, s.Memory); break;
                default: Mochi(dc, s.Mochi, s.Blink); break;
            }
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var bytes = new byte[px * px * 4];
        bmp.CopyPixels(bytes, px * 4, 0);
        return new IconPixels(px, px, bytes);
    }

    private static void Face(DrawingContext dc, double cx, double cy, double k = 1, bool sleepy = false, bool happy = false, bool blink = false)
    {
        var e = 6 * k;
        foreach (var x in new[] { cx - e, cx + e })
        {
            if (sleepy || blink)
                dc.DrawGeometry(null, P(InkBrush, 1.8 * k), Geometry.Parse(FormattableString.Invariant($"M{x - 2.4 * k},{cy} q{2.4 * k},{2.6 * k} {4.8 * k},0")));
            else if (happy)
                dc.DrawGeometry(null, P(InkBrush, 1.8 * k), Geometry.Parse(FormattableString.Invariant($"M{x - 2.4 * k},{cy + 1 * k} q{2.4 * k},{-3 * k} {4.8 * k},0")));
            else
            {
                dc.DrawEllipse(InkBrush, null, new Point(x, cy), 1.9 * k, 1.9 * k);
                dc.DrawEllipse(Brushes.White, null, new Point(x + .6 * k, cy - .7 * k), .6 * k, .6 * k);
            }
        }
        dc.DrawEllipse(Blush, null, new Point(cx - 9.5 * k, cy + 3.4 * k), 2.6 * k, 1.5 * k);
        dc.DrawEllipse(Blush, null, new Point(cx + 9.5 * k, cy + 3.4 * k), 2.6 * k, 1.5 * k);
        dc.DrawGeometry(null, P(InkBrush, 1.4 * k), Geometry.Parse(FormattableString.Invariant(
            $"M{cx - 2.2 * k},{cy + 3.2 * k} q{1.1 * k},{1.4 * k} {2.2 * k},0 q{1.1 * k},{1.4 * k} {2.2 * k},0")));
    }

    /// <summary>Mochi: a round white cat face with pink inner ears (the mascot from the design preview).</summary>
    private static void Mochi(DrawingContext dc, Mood mood, bool blink)
    {
        var white = B("#FFFFFF");
        dc.DrawGeometry(white, P(InkBrush, 2.4), Geometry.Parse("M12,26 L9,8.5 C8.7,6.9 10.4,5.8 11.7,6.7 L25,15.5 M52,26 L55,8.5 C55.3,6.9 53.6,5.8 52.3,6.7 L39,15.5"));
        dc.DrawGeometry(B("#F4A6C8"), null, Geometry.Parse("M13.4,12.2 L15,21 L20.6,16.5 Z M50.6,12.2 L49,21 L43.4,16.5 Z"));
        dc.DrawEllipse(white, P(InkBrush, 2.4), new Point(32, 37), 24, 20);
        Face(dc, 32, 36.5, 1.25, sleepy: mood == Mood.Sleepy, happy: mood == Mood.Happy, blink: blink);
        dc.DrawGeometry(B("#7444D6"), null, Geometry.Parse("M30.6,40.2 L33.4,40.2 L32,41.6 Z"));
        if (mood == Mood.Sleepy)
        {
            var z = new FormattedText("z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Display"), 9, B("#9C7BE6"), 1);
            dc.DrawText(z, new Point(51, 14));
            dc.DrawText(new FormattedText("z", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Display"), 6, B("#B497FF"), 1), new Point(57, 9));
        }
        if (mood == Mood.Happy) Sparkle(dc, 53, 22, 4.5, "#FFE07A");
    }

    private static void Sparkle(DrawingContext dc, double x, double y, double r, string hex)
    {
        var g = Geometry.Parse(FormattableString.Invariant(
            $"M{x},{y - r} Q{x + r * .18},{y - r * .18} {x + r},{y} Q{x + r * .18},{y + r * .18} {x},{y + r} Q{x - r * .18},{y + r * .18} {x - r},{y} Q{x - r * .18},{y - r * .18} {x},{y - r} Z"));
        dc.DrawGeometry(B(hex), null, g);
    }

    /// <summary>A cat-eared clock face with the real time.</summary>
    private static void Clock(DrawingContext dc, DateTime now)
    {
        var rim = B("#B497FF");
        dc.DrawGeometry(rim, P(InkBrush, 2.2), Geometry.Parse("M14,20 L11,6 L24,12 Z M50,20 L53,6 L40,12 Z"));
        dc.DrawGeometry(B("#F4A6C8"), null, Geometry.Parse("M14.4,14 L13.2,9 L18,11.4 Z M49.6,14 L50.8,9 L46,11.4 Z"));
        dc.DrawEllipse(rim, P(InkBrush, 2.4), new Point(32, 35), 25, 25);
        dc.DrawEllipse(B("#FFF8F0"), P(InkBrush, 1.4), new Point(32, 35), 20, 20);
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6;
            var r = i % 3 == 0 ? 1.6 : .9;
            dc.DrawEllipse(i % 3 == 0 ? InkBrush : B("#9C7BE6"), null, new Point(32 + Math.Sin(a) * 16, 35 - Math.Cos(a) * 16), r, r);
        }
        dc.DrawEllipse(Blush, null, new Point(23, 42), 2.6, 1.5);
        dc.DrawEllipse(Blush, null, new Point(41, 42), 2.6, 1.5);
        var h = (now.Hour % 12 + now.Minute / 60.0) * Math.PI / 6;
        var m = now.Minute * Math.PI / 30;
        dc.DrawLine(P(InkBrush, 3.2), new Point(32, 35), new Point(32 + Math.Sin(h) * 9.5, 35 - Math.Cos(h) * 9.5));
        dc.DrawLine(P(B("#7444D6"), 2.4), new Point(32, 35), new Point(32 + Math.Sin(m) * 14, 35 - Math.Cos(m) * 14));
        dc.DrawEllipse(B("#F4A6C8"), P(InkBrush, 1), new Point(32, 35), 2.2, 2.2);
    }

    /// <summary>A calendar page: the month on a lilac band and today's date, big.</summary>
    private static void Calendar(DrawingContext dc, DateTime now)
    {
        var page = new RectangleGeometry(new Rect(9, 10, 46, 46), 9, 9);
        dc.DrawGeometry(B("#FFFDF8"), P(InkBrush, 2.4), page);
        dc.PushClip(page);
        dc.DrawRectangle(B("#B497FF"), null, new Rect(9, 10, 46, 14));
        dc.Pop();
        dc.DrawGeometry(null, P(InkBrush, 2.4), page);
        dc.DrawLine(P(InkBrush, 2), new Point(9, 24), new Point(55, 24));
        foreach (var x in new[] { 21.0, 43.0 })
        {
            dc.DrawRoundedRectangle(B("#F4A6C8"), P(InkBrush, 1.4), new Rect(x - 2.2, 6, 4.4, 9), 2.2, 2.2);
        }
        var month = now.ToString("MMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);
        var mt = new FormattedText(month, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 8.5, Brushes.White, 1);
        dc.DrawText(mt, new Point(32 - mt.Width / 2, 17.2 - mt.Height / 2));
        var day = new FormattedText(now.Day.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Display"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal), 22, InkBrush, 1);
        dc.DrawText(day, new Point(32 - day.Width / 2, 40.5 - day.Height / 2));
        dc.DrawEllipse(Blush, null, new Point(16, 47), 2.4, 1.4);
        dc.DrawEllipse(Blush, null, new Point(48, 47), 2.4, 1.4);
    }

    /// <summary>The Recycle Bin: a lilac bin with a cat-ear lid; papers peek out when it has something in it.</summary>
    private static void Bin(DrawingContext dc, bool full)
    {
        if (full)
        {
            dc.DrawGeometry(B("#FFF8F0"), P(InkBrush, 1.6), Geometry.Parse("M22,20 L26,8 L36,11 L33,22 Z"));
            dc.DrawGeometry(B("#E6F3FF"), P(InkBrush, 1.6), Geometry.Parse("M33,21 L40,10 L47,15 L41,23 Z"));
        }
        var body = Geometry.Parse("M15,24 L49,24 L45.5,55 C45.2,57.4 43.4,59 41,59 L23,59 C20.6,59 18.8,57.4 18.5,55 Z");
        dc.DrawGeometry(B("#C9B4F2"), P(InkBrush, 2.4), body);
        foreach (var x in new[] { 26.0, 32.0, 38.0 }) dc.DrawLine(P(B("#9C7BE6"), 1.6), new Point(x, 30), new Point(x, 53));
        dc.DrawGeometry(B("#B497FF"), P(InkBrush, 2), Geometry.Parse("M17,24 L14,13 L23,18 Z M47,24 L50,13 L41,18 Z"));
        dc.DrawRoundedRectangle(B("#B497FF"), P(InkBrush, 2.4), new Rect(11, 19, 42, 7), 3.5, 3.5);
        Face(dc, 32, 41, .9, sleepy: !full);
    }

    /// <summary>CPU and memory as two little bars that fill up, pink and mint.</summary>
    private static void Meters(DrawingContext dc, int cpu, int memory)
    {
        dc.DrawRoundedRectangle(B("#F3ECFF"), P(InkBrush, 2.4), new Rect(8, 8, 48, 48), 12, 12);
        void Bar(double x, int pct, string fill, string label)
        {
            var track = new Rect(x, 15, 12, 30);
            dc.DrawRoundedRectangle(B("#FFFFFF"), P(InkBrush, 1.6), track, 6, 6);
            var h = Math.Max(4, 28 * pct / 100.0);
            dc.PushClip(new RectangleGeometry(track, 6, 6));
            dc.DrawRectangle(B(fill), null, new Rect(x, 45 - h, 12, h));
            dc.Pop();
            dc.DrawRoundedRectangle(null, P(InkBrush, 1.6), track, 6, 6);
            var t = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 6.5, InkBrush, 1);
            dc.DrawText(t, new Point(x + 6 - t.Width / 2, 47));
        }
        Bar(17, cpu, "#F4A6C8", "CPU");
        Bar(35, memory, "#7ED9AE", "RAM");
    }

    // ---------------------------------------------------------------- Windows calls

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO { public uint cbSize; public long i64Size, i64NumItems; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(nint hwnd, string? root, uint flags);

    /// <summary>Empty the Recycle Bin. Windows asks "are you sure?" itself, as it does from the desktop.</summary>
    public static void EmptyBin(nint owner) => SHEmptyRecycleBin(owner, null, 0);

    private static readonly string[] Purrs =
        ["Mochi purrs.", "Mochi stretches and yawns.", "Mochi gives you a slow blink.", "Mochi kneads the dock happily.", "Mochi says hi!"];

    /// <summary>What a click on the widget does. Returns a short message to show, or null.</summary>
    public static string? Click(string kind, State s)
    {
        string? Open(string target)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); return null; }
            catch (Exception ex) { return $"Couldn't open it: {ex.Message}"; }
        }
        switch (kind)
        {
            case "clock": return Open("ms-clock:");
            case "calendar": return Open("ms-actioncenter:");
            case "recyclebin": return Open("shell:RecycleBinFolder");
            case "system": return Open("taskmgr.exe");
            default:
                s.HappyUntil = Environment.TickCount64 + 3000;
                return Purrs[Random.Shared.Next(Purrs.Length)];
        }
    }
}
