using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace NotchHUD;

public struct FrameStats
{
    public float Current;      // FPS over the last second
    public float Average;      // FPS over the rolling window
    public float Low1;         // average of the slowest 1% of frames, as FPS
    public float Low01;        // average of the slowest 0.1% of frames, as FPS
    public float MedianMs;     // median frame time in the rolling window
    public float P5Fps;        // FPS that 95% of frames beat
    public float Jitter;       // frame-time variation over the last second (std / mean)
    public int Stutters10s;    // frame-time spikes in the last 10 seconds
    public int FramesLastSec;
    public int Samples;
    public long AgeMs;         // time since the last present was seen

    // What kind of scene is on screen. Frames outside gameplay are left out of every statistic.
    public string Phase;       // null/"" gameplay, "loading", "cutscene", "menu"
    public float PhaseSec;     // how long the current non-gameplay phase has lasted
    public float PhaseFps;     // frame rate during it (the cutscene's cap, for example)
    public int FreezeSeq;      // goes up by one with every freeze (a pause in gameplay without a frame)
    public float LastFreezeMs; // length of the latest freeze
    public float SpikePeriodMs; // > 0 when recent stutters repeat on a regular timer
}

/// <summary>Frame-time histogram: whole-session statistics in a few kilobytes.</summary>
public sealed class Hist
{
    const int N = 4096;
    const float PerMs = 20f; // 0.05 ms buckets, up to ~205 ms
    readonly int[] _b = new int[N];

    public long Count;
    public double SumMs;
    public float MaxMs;
    public int Stutters;

    // Time left out of the statistics, and how often it happened.
    public double LoadingMs, CutsceneMs, MenuMs;
    public int Loads, Cutscenes, Freezes;

    public void Add(float ft, int stutters)
    {
        int i = (int)(ft * PerMs);
        _b[i >= N ? N - 1 : i]++;
        Count++;
        SumMs += ft;
        if (ft > MaxMs) MaxMs = ft;
        Stutters += stutters;
    }

    public Hist Clone()
    {
        var h = new Hist
        {
            Count = Count, SumMs = SumMs, MaxMs = MaxMs, Stutters = Stutters,
            LoadingMs = LoadingMs, CutsceneMs = CutsceneMs, MenuMs = MenuMs,
            Loads = Loads, Cutscenes = Cutscenes, Freezes = Freezes,
        };
        Array.Copy(_b, h._b, N);
        return h;
    }

    public double Seconds => SumMs / 1000.0;
    public float AvgFps => Count > 0 && SumMs > 0 ? (float)(Count * 1000.0 / SumMs) : 0;

    /// <summary>Average of the slowest fraction of frames, as FPS.</summary>
    public float LowFps(double fraction)
    {
        if (Count == 0) return 0;
        long k = Math.Max(1, (long)Math.Ceiling(Count * fraction)), left = k;
        double sum = 0;
        for (int i = N - 1; i >= 0 && left > 0; i--)
        {
            long take = Math.Min(left, _b[i]);
            if (take == 0) continue;
            double ms = i == N - 1 ? Math.Max(MaxMs, (i + 0.5) / PerMs) : (i + 0.5) / PerMs;
            sum += take * ms;
            left -= take;
        }
        return sum > 0 ? (float)(k * 1000.0 / sum) : 0;
    }
}

/// <summary>
/// Measures frame times without touching the game: listens to the Windows
/// present events (DXGI, Direct3D 9, and the graphics kernel as a fallback
/// for OpenGL/Vulkan) over ETW, the same source PresentMon uses.
/// </summary>
public sealed class FrameTracker : IDisposable
{
    static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    const string SessionName = "NotchHUD-Presents";
    const int DxgiPresentStart = 42;
    const int D3D9PresentStart = 1;
    const int DxgKrnlPresent = 184;
    const ulong DxgKrnlPresentKeyword = 0x8000000;

    const int Cap = 1 << 15;                // frames kept per process
    public const double WindowMs = 120_000; // rolling stats window

    sealed class Proc
    {
        public double[]? Ts;
        public float[]? Ft;
        public long Count;
        public bool HasLast;
        public double LastTs;
        public double LastRuntimeTs = double.NegativeInfinity;
        public double ResetTs = double.NegativeInfinity;
        public long LastTick;
        public float Ema;
        public int Pending;
        public long MuteUntil;
        public readonly double[] SpikeTs = new double[32];
        public int SpikeIdx;
        public Hist? Session, Bench;
        public bool Pinned;

        // Scene classification. Frames wait in half-second windows (Cur, then B,
        // then A) for about a second before they count, so when a cutscene or
        // loading screen is recognised its first frames, and the hitch going
        // into it, can still be left out.
        public byte[]? Ph;                      // per ring slot: 0 gameplay, else left out
        public List<Frame> A = new(), B = new(), Cur = new();
        public double WinStartTs;
        public string Phase = "", RawPhase = "";
        public int RawStreak, Grace, FreezeSeq;
        public double PhaseStartTs, PhaseFps, BaseFps, GapMs, GapStartTs;
        public float LastFreezeMs;
    }

    public readonly record struct Frame(float Ft, int Stutters, int Idx, double Ts);

    /// <summary>Latest GPU load, fed in from the sensors; helps tell a cutscene or menu from a heavy scene.</summary>
    public volatile float GpuLoadHint = float.NaN;

    readonly object _gate = new();
    readonly Dictionary<int, Proc> _procs = new();
    TraceEventSession? _session;
    Thread? _thread;
    System.Threading.Timer? _flush;
    long _lastPrune;

    public long EventsSeen;
    public string? Error { get; private set; }

    public void Start()
    {
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 16 };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, 0,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { DxgiPresentStart } });
            _session.EnableProvider(D3D9, TraceEventLevel.Informational, 0,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { D3D9PresentStart } });
            _session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, DxgKrnlPresentKeyword,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { DxgKrnlPresent } });

            _session.Source.AllEvents += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Error = ex.Message; Log.Write("ETW loop failed: " + ex); }
            })
            { IsBackground = true, Name = "etw", Priority = ThreadPriority.BelowNormal };
            _thread.Start();

            // Windows delivers real-time events about once a second; flushing
            // four times a second keeps the readout live.
            _flush = new System.Threading.Timer(_ => FlushSession(), null, 1000, 250);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Write("ETW start failed: " + ex);
        }
    }

    void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        Guid g = e.ProviderGuid;
        bool runtime;
        if (g == Dxgi)
        {
            if (id != DxgiPresentStart) return;
            // Payload: swap chain pointer, flags, sync interval. Skip DXGI_PRESENT_TEST.
            int off = e.PointerSize;
            if (e.EventDataLength >= off + 4 && (Marshal.ReadInt32(e.DataStart, off) & 0x1) != 0) return;
            runtime = true;
        }
        else if (g == D3D9)
        {
            if (id != D3D9PresentStart) return;
            runtime = true;
        }
        else if (g == DxgKrnl)
        {
            if (id != DxgKrnlPresent) return;
            runtime = false;
        }
        else return;

        int pid = e.ProcessID;
        if (pid <= 4) return;
        Present(pid, e.TimeStampRelativeMSec, Environment.TickCount64, runtime);
    }

    /// <summary>One present from a process. Separate from the ETW callback so it can be fed directly in tests.</summary>
    internal void Present(int pid, double ts, long tick, bool runtime)
    {
        lock (_gate)
        {
            EventsSeen++;
            if (!_procs.TryGetValue(pid, out var p)) { p = new Proc(); _procs[pid] = p; }

            // Kernel presents are only used for processes with no DXGI/D3D9 events
            // (OpenGL, Vulkan), otherwise every frame would be counted twice.
            if (runtime) p.LastRuntimeTs = ts;
            else if (ts - p.LastRuntimeTs < 2000) return;

            double delta = ts - p.LastTs;
            bool hadLast = p.HasLast;
            p.LastTs = ts;
            p.LastTick = tick;
            p.HasLast = true;

            // Gaps over a second are pauses, loading or alt-tab, not frames. The
            // next classification decides which: a freeze or a loading screen.
            if (hadLast && delta > 1000 && tick >= p.MuteUntil)
            {
                p.GapMs = delta;
                p.GapStartTs = ts - delta;
            }

            if (hadLast && delta > 0 && delta <= 1000 && tick >= p.MuteUntil)
            {
                float ft = (float)delta;
                p.Ts ??= new double[Cap];
                p.Ft ??= new float[Cap];
                p.Ph ??= new byte[Cap];
                int i = (int)(p.Count & (Cap - 1));
                p.Ts[i] = ts;
                p.Ft[i] = ft;
                p.Ph[i] = 0;
                p.Count++;

                // A stutter is one or two frames far slower than the recent pace.
                // Three in a row means the pace itself changed (menu, cutscene,
                // frame cap), which is not a stutter.
                bool spiky = p.Ema > 0 && ft > p.Ema * 2.5f && ft > p.Ema + 12f;
                int stutters = 0;
                if (spiky)
                {
                    if (++p.Pending >= 3) { p.Pending = 0; p.Ema = ft; }
                }
                else
                {
                    stutters = p.Pending;
                    p.Pending = 0;
                    // Hitches in a loading screen or cutscene are not stutters in the game.
                    if (p.Phase.Length == 0)
                        for (int k = 0; k < stutters; k++) p.SpikeTs[p.SpikeIdx++ & 31] = ts;
                    p.Ema = p.Ema <= 0 ? ft : p.Ema + (ft - p.Ema) * 0.05f;
                }

                // Frames reach the statistics through the classification windows.
                p.Cur.Add(new Frame(ft, stutters, i, ts));
                if (p.WinStartTs <= 0) p.WinStartTs = ts;
                if (ts - p.WinStartTs >= 500) Classify(p, ts);
            }

            if (tick - _lastPrune > 10_000)
            {
                _lastPrune = tick;
                List<int>? dead = null;
                foreach (var kv in _procs)
                    if (!kv.Value.Pinned && tick - kv.Value.LastTick > 30_000) (dead ??= new()).Add(kv.Key);
                if (dead != null) foreach (int d in dead) _procs.Remove(d);
            }
        }
    }

    // ------------------------------------------------- scene classification

    // Frame rates games lock cutscenes and videos to.
    static readonly float[] SceneCaps = { 24, 25, 30, 48, 50, 60 };

    static float NearCap(double fps)
    {
        foreach (float cap in SceneCaps)
            if (Math.Abs(fps - cap) <= cap * 0.04) return cap;
        return 0;
    }

    static byte Code(string phase) => phase switch { "loading" => 1, "cutscene" => 2, "menu" => 3, _ => 4 };

    /// <summary>
    /// Runs every half second of frames: decides what the last window was,
    /// applies hysteresis, and either releases the oldest waiting window into
    /// the statistics or leaves the windows out. Called under the lock.
    /// </summary>
    void Classify(Proc p, double ts)
    {
        var w = p.Cur;
        int n = w.Count, longN = 0;
        double sum = 0, sq = 0;
        foreach (var f in w)
        {
            sum += f.Ft;
            sq += (double)f.Ft * f.Ft;
            if (f.Ft > 50) longN++;
        }
        double mean = sum / n, fps = 1000.0 / mean;
        double cv = n > 2 ? Math.Sqrt(Math.Max(0, sq / n - mean * mean)) / mean : 0;
        float gpu = GpuLoadHint;
        bool knowBase = p.BaseFps > 0;

        string raw = "";
        // Loading: a crawl, or mostly long frames, far below the normal pace.
        if ((fps < 12 || (longN >= n * 0.25 && fps < 25)) && (!knowBase || fps < p.BaseFps * 0.5))
            raw = "loading";
        // Cutscene or video: locked hard to a standard rate well under the normal
        // pace, without the GPU being maxed (that would be a heavy scene under V-Sync).
        else if (n >= 8 && cv < 0.06 && NearCap(fps) is float cap and > 0 && knowBase && p.BaseFps > cap * 1.35 && !(gpu >= 90))
            raw = "cutscene";
        // Menu: running far faster than normal with the GPU mostly idle.
        else if (knowBase && fps > Math.Max(p.BaseFps * 1.8, 150) && !float.IsNaN(gpu) && gpu < 60)
            raw = "menu";

        // A pause in presenting just before this window: a short one in the
        // middle of gameplay is a freeze, anything else is loading.
        bool forced = false;
        if (p.GapMs > 0)
        {
            if (raw.Length == 0 && p.Phase.Length == 0 && p.GapMs < 5000)
            {
                p.FreezeSeq++;
                p.LastFreezeMs = (float)p.GapMs;
                if (p.Session != null) p.Session.Freezes++;
                if (p.Bench != null) p.Bench.Freezes++;
            }
            else if (p.Phase != "loading")
            {
                raw = "loading";
                forced = true;
            }
            p.GapMs = 0;
        }

        if (raw == p.RawPhase) p.RawStreak++;
        else { p.RawPhase = raw; p.RawStreak = 1; }
        string phase = forced || p.RawStreak >= 2 ? p.RawPhase : p.Phase;

        if (phase != p.Phase)
        {
            if (p.Phase.Length > 0) AddPhaseTime(p, p.Phase, ts - p.PhaseStartTs);
            if (phase.Length > 0)
            {
                // Entering: leave out everything still waiting, including the transition.
                byte code = Code(phase);
                Drop(p, p.A, code);
                Drop(p, p.B, code);
                Drop(p, p.Cur, code);
                for (int i = 0; i < p.SpikeTs.Length; i++)
                    if (p.SpikeTs[i] > ts - 2500) p.SpikeTs[i] = 0;
                p.PhaseStartTs = forced ? p.GapStartTs : ts - 1500;
                if (phase == "loading") Count(p, h => h.Loads++);
                else if (phase == "cutscene") Count(p, h => h.Cutscenes++);
            }
            else
            {
                // Back to gameplay: the first window still holds the transition.
                Drop(p, p.Cur, Code(p.Phase));
                p.Grace = 1;
            }
            p.Phase = phase;
            p.PhaseFps = fps;
        }
        else if (p.Phase.Length > 0)
        {
            Drop(p, p.Cur, Code(p.Phase));
            p.PhaseFps = p.PhaseFps + (fps - p.PhaseFps) * 0.3;
        }
        else if (p.Grace > 0)
        {
            p.Grace--;
            Drop(p, p.Cur, 4);
        }
        else
        {
            // Plain gameplay: the oldest window has waited long enough to count.
            var oldest = p.A;
            Commit(p, oldest);
            oldest.Clear();
            p.A = p.B;
            p.B = p.Cur;
            p.Cur = oldest;
            if (raw.Length == 0 && n >= 4)
                p.BaseFps = p.BaseFps <= 0 ? fps : p.BaseFps + (fps - p.BaseFps) * 0.03;
        }
        p.WinStartTs = ts;
    }

    static void Commit(Proc p, List<Frame> frames)
    {
        foreach (var f in frames)
        {
            p.Session?.Add(f.Ft, f.Stutters);
            if (f.Ts > p.ResetTs) p.Bench?.Add(f.Ft, f.Stutters);
        }
    }

    static void Drop(Proc p, List<Frame> frames, byte code)
    {
        if (p.Ph != null) foreach (var f in frames) p.Ph[f.Idx] = code;
        frames.Clear();
    }

    static void Count(Proc p, Action<Hist> add)
    {
        if (p.Session != null) add(p.Session);
        if (p.Bench != null) add(p.Bench);
    }

    static void AddPhaseTime(Proc p, string phase, double ms)
    {
        if (ms <= 0) return;
        Count(p, h =>
        {
            if (phase == "loading") h.LoadingMs += ms;
            else if (phase == "cutscene") h.CutsceneMs += ms;
            else if (phase == "menu") h.MenuMs += ms;
        });
    }

    // ------------------------------------------------- sessions, benchmarks

    /// <summary>Starts whole-session statistics for a game process.</summary>
    public void BeginSession(int pid)
    {
        lock (_gate)
        {
            if (!_procs.TryGetValue(pid, out var p)) { p = new Proc(); _procs[pid] = p; }
            p.Session = new Hist();
            p.Bench = null;
            p.Pinned = true;
        }
    }

    public Hist? GetSession(int pid)
    {
        lock (_gate) return _procs.TryGetValue(pid, out var p) ? p.Session?.Clone() : null;
    }

    public void EndSession(int pid)
    {
        lock (_gate)
            if (_procs.TryGetValue(pid, out var p)) { p.Pinned = false; p.Session = null; p.Bench = null; }
    }

    /// <summary>Frames of this process are not recorded until the given tick (TickCount64).</summary>
    public void Mute(int pid, long untilTick)
    {
        lock (_gate)
            if (_procs.TryGetValue(pid, out var p)) { p.MuteUntil = untilTick; p.Pending = 0; }
    }

    public void StartBench(int pid)
    {
        lock (_gate)
            if (_procs.TryGetValue(pid, out var p)) { p.Bench = new Hist(); p.ResetTs = p.LastTs; }
    }

    public Hist? StopBench(int pid)
    {
        lock (_gate)
        {
            if (!_procs.TryGetValue(pid, out var p)) return null;
            var h = p.Bench;
            p.Bench = null;
            return h;
        }
    }

    // ------------------------------------------------------------- queries

    /// <summary>
    /// Live statistics for a process. The light form (full = false) only fills
    /// the last-second values and is cheap enough to call several times a second.
    /// </summary>
    public bool TryGet(int pid, float[] scratch, bool full, out FrameStats s)
    {
        s = default;
        int n = 0, n1 = 0;
        double sum = 0, sum1 = 0, sq1 = 0;

        lock (_gate)
        {
            if (!_procs.TryGetValue(pid, out var p) || !p.HasLast) return false;
            s.AgeMs = Environment.TickCount64 - p.LastTick;
            s.Phase = p.Phase;
            s.PhaseSec = p.Phase.Length > 0 ? (float)((p.LastTs - p.PhaseStartTs) / 1000.0) : 0;
            s.PhaseFps = (float)p.PhaseFps;
            s.FreezeSeq = p.FreezeSeq;
            s.LastFreezeMs = p.LastFreezeMs;
            if (p.Ts == null || p.Ft == null) return true;

            double last = p.LastTs;
            double from = full ? Math.Max(last - WindowMs, p.ResetTs) : Math.Max(last - 1000, p.ResetTs);
            long avail = Math.Min(p.Count, Cap);
            for (long k = 0; k < avail && n < scratch.Length; k++)
            {
                int i = (int)((p.Count - 1 - k) & (Cap - 1));
                double ts = p.Ts[i];
                if (ts <= from) break;
                float ft = p.Ft[i];
                // The last second is what is on screen now, whatever it is; the
                // rolling statistics only cover gameplay.
                if (ts > last - 1000) { n1++; sum1 += ft; sq1 += (double)ft * ft; }
                if (p.Ph != null && p.Ph[i] != 0) continue;
                scratch[n++] = ft;
                sum += ft;
            }

            Span<double> spikes = stackalloc double[32];
            int ns = 0;
            for (int i = 0; i < p.SpikeTs.Length; i++)
            {
                double t = p.SpikeTs[i];
                if (t <= 0 || t <= p.ResetTs) continue;
                if (t > last - 10_000) s.Stutters10s++;
                if (t > last - 60_000) spikes[ns++] = t;
            }

            // Stutters on a timer (every 1.0 s, every 5 s…) come from something
            // polling in the background, not from the game.
            if (ns >= 6)
            {
                var sorted = spikes[..ns];
                sorted.Sort();
                double isum = 0, isq = 0;
                int ni = ns - 1;
                for (int i = 1; i < ns; i++) { double d = sorted[i] - sorted[i - 1]; isum += d; isq += d * d; }
                double im = isum / ni, icv = Math.Sqrt(Math.Max(0, isq / ni - im * im)) / im;
                if (icv < 0.15 && im >= 250) s.SpikePeriodMs = (float)im;
            }
        }

        s.Samples = n;
        s.FramesLastSec = n1;
        if (n1 > 0)
        {
            double mean = sum1 / n1;
            s.Current = (float)(1000.0 / mean);
            s.Jitter = n1 > 2 ? (float)(Math.Sqrt(Math.Max(0, sq1 / n1 - mean * mean)) / mean) : 0;
        }
        if (n == 0 || !full) return true;

        s.Average = (float)(n * 1000.0 / sum);
        Array.Sort(scratch, 0, n);
        s.MedianMs = scratch[n / 2];
        s.P5Fps = 1000f / scratch[Math.Min(n - 1, (int)(n * 0.95))];
        s.Low1 = LowFps(scratch, n, 0.01);
        s.Low01 = LowFps(scratch, n, 0.001);
        return true;
    }

    /// <summary>Copies the most recent frame times, oldest first, for the graph.</summary>
    public int CopyRecent(int pid, float[] dst, int max)
    {
        lock (_gate)
        {
            if (!_procs.TryGetValue(pid, out var p) || p.Ft == null) return 0;
            int n = (int)Math.Min(Math.Min(p.Count, Cap), Math.Min(max, dst.Length));
            for (int k = 0; k < n; k++)
                dst[k] = p.Ft[(int)((p.Count - n + k) & (Cap - 1))];
            return n;
        }
    }

    static float LowFps(float[] sorted, int n, double fraction)
    {
        int k = Math.Max(1, (int)Math.Ceiling(n * fraction));
        double sum = 0;
        for (int i = n - k; i < n; i++) sum += sorted[i];
        return (float)(k * 1000.0 / sum);
    }

    // ---------------------------------------------------------------- flush

    const int PropsSize = 120, NameBytes = 1024;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint control);

    IntPtr _props;

    void FlushSession()
    {
        try
        {
            if (_session == null) return;
            int total = PropsSize + 2 * NameBytes;
            if (_props == IntPtr.Zero) _props = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i += 8) Marshal.WriteInt64(_props, i, 0);
            Marshal.WriteInt32(_props, 0, total);                       // Wnode.BufferSize
            Marshal.WriteInt32(_props, 112, PropsSize + NameBytes);     // LogFileNameOffset
            Marshal.WriteInt32(_props, 116, PropsSize);                 // LoggerNameOffset
            ControlTraceW(0, SessionName, _props, 3 /* EVENT_TRACE_CONTROL_FLUSH */);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _flush?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
    }
}
