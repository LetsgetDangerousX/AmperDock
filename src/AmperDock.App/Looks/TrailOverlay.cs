using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using AmperDock.App.Dock;
using AmperDock.Render;
using AmperDock.Shell;
using static AmperDock.App.Dock.Win32;

namespace AmperDock.App.Looks;

/// <summary>
/// The cursor trail: a transparent, click-through, always-on-top window covering the monitor the cursor
/// is on. Particles are spawned as the cursor travels and drawn with <see cref="SpriteRenderer"/>.
/// When the mouse is still and the last particle has faded, it stops drawing and only checks the cursor
/// position every 30 ms. Pauses during fullscreen games and presentations.
/// </summary>
internal sealed partial class TrailOverlay : IDisposable
{
    private const string ClassName = "AmperDock.Trail";
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    private static WndProc? s_proc;

    private sealed class Particle
    {
        public string Key = "";
        public float X, Y, Vx, Vy, Size, Rot, Spin, Age, Life, Phase;
    }

    private readonly TrailInfo _trail;
    private readonly nint _hwnd;
    private readonly SpriteRenderer _renderer;
    private readonly FrameClock _clock;
    private readonly DispatcherTimer _idle;
    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();
    private readonly string[] _keys;

    private RECT _bounds;          // monitor the overlay currently covers, screen px
    private float _scale = 1;
    private float _lastX, _lastY, _travel;
    private bool _haveLast, _running, _side;
    private long _lastTick, _lastBusyCheck;
    private bool _busy;

    public TrailOverlay(TrailInfo trail)
    {
        _trail = trail;
        var inst = GetModuleHandle(0);
        if (s_proc is null)
        {
            s_proc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
            var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = inst, lpszClassName = ClassName };
            RegisterClassEx(ref wc);
        }
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP,
            ClassName, "AmperDock trail", WS_POPUP, 0, 0, 1, 1, 0, 0, inst, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        SetLayeredWindowAttributes(_hwnd, 0, 255, 2 /* LWA_ALPHA */);   // layered + transparent = clicks pass straight through

        GetCursorPos(out var p);
        FollowMonitor(p);
        _renderer = new SpriteRenderer(_hwnd, _bounds.Right - _bounds.Left, _bounds.Bottom - _bounds.Top);
        _keys = trail.Sprites;
        foreach (var key in _keys)
        {
            var (w, h, px) = LookCatalog.Pixels(key);
            _renderer.SetSprite(key, w, h, px);
        }
        ShowWindow(_hwnd, SW_SHOWNA);
        App.Trace($"trail overlay up: {_keys.Length} sprites, monitor {_bounds.Left},{_bounds.Top},{_bounds.Right},{_bounds.Bottom}");

        _clock = new FrameClock(Dispatcher.CurrentDispatcher, OnFrame);
        _idle = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background, (_, _) => CheckForMovement(), Dispatcher.CurrentDispatcher);
        _idle.Start();
    }

    /// <summary>Cover whichever monitor the point is on (resizing the swap chain when it changes).</summary>
    private bool FollowMonitor(POINT p)
    {
        var mon = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        var b = mi.rcMonitor;
        if (b.Left == _bounds.Left && b.Top == _bounds.Top && b.Right == _bounds.Right && b.Bottom == _bounds.Bottom) return false;
        _bounds = b;
        _scale = GetDpiForMonitor(mon, 0, out var dpi, out _) == 0 ? dpi / 96f : 1;
        SetWindowPos(_hwnd, 0, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, SWP_NOACTIVATE | SWP_NOZORDER);
        _renderer?.Resize(b.Right - b.Left, b.Bottom - b.Top);
        return true;
    }

    private void CheckForMovement()
    {
        if (_running) return;
        GetCursorPos(out var p);
        if (_haveLast && Math.Abs(p.X - _lastX) < 1 && Math.Abs(p.Y - _lastY) < 1) return;
        _running = true;
        _lastTick = Stopwatch.GetTimestamp();
        _clock.Start();
    }

    private void OnFrame()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (float)Math.Min(0.05, (now - _lastTick) / (double)Stopwatch.Frequency);
        _lastTick = now;

        if (Environment.TickCount64 - _lastBusyCheck > 500)
        {
            _lastBusyCheck = Environment.TickCount64;
            _busy = SystemState.FullscreenOrBusy();
        }

        GetCursorPos(out var p);
        if (FollowMonitor(p)) { _particles.Clear(); _haveLast = false; }
        if (!_busy) Spawn(p.X, p.Y);
        else _particles.Clear();
        Step(dt);

        var draws = new List<SpriteDraw>(_particles.Count);
        foreach (var q in _particles)
        {
            var f = q.Age / q.Life;                                   // 0 → 1 over the particle's life
            var alpha = MathF.Min(1, (1 - f) * 1.4f);
            var size = q.Size;
            if (_trail.Pop && f < 0.15f) size *= 0.75f + f / 0.15f * 0.25f;
            if (_trail.Shrink) size *= 1 - f;
            var sx = _trail.Flap ? MathF.Abs(MathF.Sin(q.Phase + q.Age * 21)) * 0.6f + 0.4f : 1;
            draws.Add(new SpriteDraw(q.Key, q.X, q.Y, size, q.Rot, sx, alpha));
        }
        _renderer.Draw(draws);

        var still = _haveLast && Math.Abs(p.X - _lastX) < 1 && Math.Abs(p.Y - _lastY) < 1;
        if (_particles.Count == 0 && (still || _busy))
        {
            _clock.Stop();
            _running = false;
        }
    }

    private float Rand(float[] range) => range.Length < 2 ? 0 : range[0] + (float)_rng.NextDouble() * (range[1] - range[0]);

    private void Spawn(int sx, int sy)
    {
        // window-local position
        float x = sx - _bounds.Left, y = sy - _bounds.Top;
        if (!_haveLast) { (_lastX, _lastY, _haveLast) = (sx, sy, true); return; }
        float dx = sx - _lastX, dy = sy - _lastY;
        var moved = MathF.Sqrt(dx * dx + dy * dy);
        _lastX = sx; _lastY = sy;
        if (moved < 0.5f) return;
        _travel += moved;
        var spacing = _trail.Spacing * _scale;
        if (_travel < spacing) return;
        _travel = 0;

        var heading = MathF.Atan2(dy, dx);
        for (var i = 0; i < Math.Max(1, _trail.Count); i++)
        {
            var q = new Particle
            {
                Key = _keys[_rng.Next(_keys.Length)],
                X = x + (float)(_rng.NextDouble() - 0.5) * 2 * _trail.Jitter * _scale,
                Y = y + (float)(_rng.NextDouble() - 0.5) * 2 * _trail.Jitter * _scale,
                Vx = Rand(_trail.Vx) * _scale,
                Vy = Rand(_trail.Vy) * _scale,
                Size = Rand(_trail.Size) * _scale,
                Rot = _trail.Align ? heading + MathF.PI / 2 : Rand(_trail.Rot),
                Spin = Rand(_trail.Spin),
                Life = _trail.Life,
                Phase = (float)_rng.NextDouble() * 6,
            };
            if (_trail.Alternate > 0)
            {
                // footprints: step left, step right of the path
                _side = !_side;
                var off = _trail.Alternate * _scale * (_side ? 1 : -1);
                q.X += MathF.Cos(heading + MathF.PI / 2) * off;
                q.Y += MathF.Sin(heading + MathF.PI / 2) * off;
            }
            if (q.Size <= 0) q.Size = 16 * _scale;
            _particles.Add(q);
        }
        if (_particles.Count > 400) _particles.RemoveRange(0, _particles.Count - 400);   // a hard cap, just in case
    }

    private void Step(float dt)
    {
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var q = _particles[i];
            q.Age += dt;
            if (q.Age >= q.Life) { _particles.RemoveAt(i); continue; }
            q.Vy += _trail.Gravity * _scale * dt;
            q.X += q.Vx * dt + (_trail.Sway > 0 ? MathF.Sin(q.Phase + q.Age * 6) * _trail.Sway * _scale * dt : 0);
            q.Y += q.Vy * dt;
            q.Rot += q.Spin * dt;
        }
    }

    public void Dispose()
    {
        _idle.Stop();
        _clock.Dispose();
        _renderer.Dispose();
        DestroyWindow(_hwnd);
    }
}
