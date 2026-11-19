using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AmperDock.App.Dock;

/// <summary>
/// Calls a frame callback on the UI thread once per display refresh while running, and nothing at all
/// when stopped. A background thread waits on DwmFlush (blocks until the next composition pass, i.e.
/// vsync) and posts the frame to the dispatcher. Independent of WPF's own render loop, which doesn't
/// tick reliably when no WPF visuals change.
/// </summary>
internal sealed partial class FrameClock : IDisposable
{
    [LibraryImport("dwmapi.dll")] private static partial int DwmFlush();

    private readonly Dispatcher _dispatcher;
    private readonly Action _onFrame;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _running, _disposed;
    private int _pending;   // 1 while a frame is queued on the dispatcher, so slow frames don't pile up

    public FrameClock(Dispatcher dispatcher, Action onFrame)
    {
        _dispatcher = dispatcher;
        _onFrame = onFrame;
        _thread = new Thread(Loop) { IsBackground = true, Name = "AmperDock frame clock", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public bool Running => _running;

    public void Start() { _running = true; _wake.Set(); }

    public void Stop() { _running = false; _wake.Reset(); }

    private void Loop()
    {
        while (!_disposed)
        {
            _wake.Wait();
            if (_disposed) return;
            if (DwmFlush() < 0) Thread.Sleep(16);   // composition unavailable: fall back to ~60 Hz
            if (!_running || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) continue;
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                Interlocked.Exchange(ref _pending, 0);
                if (_running) _onFrame();
            });
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _running = false;
        _wake.Set();
    }
}
