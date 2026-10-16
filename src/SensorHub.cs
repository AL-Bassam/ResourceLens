namespace ResourceLens;

/// <summary>
/// The one sensor reader, on its own thread, shared by the recorder, the overlay and the Performance page.
/// Vendor drivers and the Lenovo WMI provider can block for minutes (seen right after logon), so callers never
/// touch them directly: they get the latest reading, and the recorder waits at most a moment for a fresh one.
/// </summary>
internal static class SensorHub
{
    static readonly object Gate = new();
    static readonly AutoResetEvent Wake = new(false);
    static Thread? _thread;
    static volatile bool _enabled;
    static SensorReading? _last;
    static long _lastAt, _seq;
    static long _busySince; // TickCount64 when the current setup/read began, 0 = idle
    static bool _stallLogged;

    /// <summary>What the reader is doing right now (set by <see cref="Sensors"/>; reported when it gets stuck).</summary>
    public static volatile string Step = "";

    public static string? Sources { get; private set; }

    /// <summary>Follows the temperatures setting; the first enable starts the reader thread.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            lock (Gate) _thread ??= Start();
            Wake.Set();
        }
    }

    static Thread Start()
    {
        var t = new Thread(Run) { IsBackground = true, Name = "Sensors", Priority = ThreadPriority.BelowNormal };
        t.Start();
        return t;
    }

    static void Run()
    {
        Sensors? sensors = null;
        while (true)
        {
            Wake.WaitOne();
            if (!_enabled)
            {
                sensors?.Dispose();
                sensors = null;
                Sources = null;
                lock (Gate) _last = null;
                continue;
            }
            // Requests from the recorder, the overlay and the Performance page arrive together: read once.
            if (sensors is not null && Environment.TickCount64 - Volatile.Read(ref _lastAt) < 500) continue;

            Volatile.Write(ref _busySince, Environment.TickCount64);
            try
            {
#if DEBUG
                if (Environment.GetEnvironmentVariable("RESOURCELENS_TEST_STALL") == "sensors") { Step = "test stall"; Thread.Sleep(Timeout.Infinite); }
#endif
                sensors ??= new Sensors();
                Sources = sensors.Sources;
                var r = sensors.Read();
                lock (Gate)
                {
                    _last = r;
                    _lastAt = Environment.TickCount64;
                    _seq++;
                    _stallLogged = false;
                    Monitor.PulseAll(Gate);
                }
            }
            catch (Exception ex) { Log.Write("sensors", ex); }
            finally { Volatile.Write(ref _busySince, 0); }
        }
    }

    /// <summary>Latest reading if it is recent; never blocks (overlay and Performance page, once a second).</summary>
    public static SensorReading? Latest(int maxAgeMs = 3000)
    {
        if (!_enabled) return null;
        Wake.Set();
        lock (Gate) return _last is not null && Environment.TickCount64 - _lastAt <= maxAgeMs ? _last : null;
    }

    /// <summary>
    /// A reading taken within the last second, waiting up to <paramref name="waitMs"/> for it (the recorder).
    /// While the reader is stuck this returns at once, so a hung driver can never stall recording.
    /// </summary>
    public static SensorReading? Fresh(int waitMs, int maxAgeMs)
    {
        if (!_enabled) return null;
        long now = Environment.TickCount64;
        long busy = Volatile.Read(ref _busySince);
        bool stuck = busy != 0 && now - busy > 2000;
        lock (Gate)
        {
            if (stuck && now - busy > 30_000 && !_stallLogged)
            {
                _stallLogged = true;
                Log.Write($"sensors: stuck for {(now - busy) / 1000}s at '{Step}' (recording continues without them)");
            }
            if (!stuck && (_last is null || now - _lastAt >= 1000))
            {
                long seq = _seq;
                Wake.Set();
                long deadline = now + waitMs;
                while (_seq == seq)
                {
                    long left = deadline - Environment.TickCount64;
                    if (left <= 0) break;
                    Monitor.Wait(Gate, (int)left);
                }
            }
            return _last is not null && Environment.TickCount64 - _lastAt <= maxAgeMs ? _last : null;
        }
    }
}
