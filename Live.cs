using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NotchHUD;

/// <summary>Rolling latency statistics for one ping target.</summary>
sealed class PingStats
{
    const int Size = 30;
    readonly float[] _ring = new float[Size];
    int _n, _next;
    readonly object _gate = new();

    public void Add(float ms)
    {
        lock (_gate)
        {
            _ring[_next] = ms;
            _next = (_next + 1) % Size;
            if (_n < Size) _n++;
        }
    }

    public int Samples { get { lock (_gate) return _n; } }

    /// <summary>Average, jitter (mean change between replies) and loss percentage of the recent replies.</summary>
    public (float avg, float jitter, float loss) Get()
    {
        lock (_gate)
        {
            if (_n == 0) return (float.NaN, float.NaN, float.NaN);
            int lost = 0, ok = 0;
            double sum = 0, jit = 0;
            float prev = float.NaN;
            for (int i = 0; i < _n; i++)
            {
                float v = _ring[(_next - _n + i + Size * 2) % Size];
                if (float.IsNaN(v)) { lost++; prev = float.NaN; continue; }
                ok++;
                sum += v;
                if (!float.IsNaN(prev)) jit += Math.Abs(v - prev);
                prev = v;
            }
            return (ok > 0 ? (float)(sum / ok) : float.NaN, ok > 1 ? (float)(jit / (ok - 1)) : 0, lost * 100f / _n);
        }
    }
}

/// <summary>
/// Pings the router and an internet host every few seconds, so a lag spike can
/// be told apart: trouble reaching the router is the Wi-Fi or cable, trouble
/// only beyond it is the provider.
/// </summary>
sealed class NetProbe : IDisposable
{
    const string Internet = "1.1.1.1";

    public readonly PingStats Net = new(), Router = new();
    public volatile string RouterAddress = "";

    /// <summary>The latest internet round trip in ms, NaN when the last ping got no reply.</summary>
    public volatile float LastMs = float.NaN;

    System.Threading.Timer? _timer;
    int _busy, _ticks;
    IPAddress? _gateway;
    readonly Dictionary<string, long> _lastFired = new();
    long _startedAt = Environment.TickCount64;

    public void Start() => _timer = new System.Threading.Timer(_ => Tick(), null, 3000, 2500);

    void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (_ticks++ % 24 == 0) _gateway = FindGateway();
            RouterAddress = _gateway?.ToString() ?? "";
            float ms = Send(IPAddress.Parse(Internet));
            LastMs = ms;
            Net.Add(ms);
            if (_gateway != null) Router.Add(Send(_gateway));
        }
        catch (Exception ex) { Log.Write("Ping failed: " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    static float Send(IPAddress addr)
    {
        try
        {
            using var ping = new Ping();
            var r = ping.Send(addr, 1000);
            return r.Status == IPStatus.Success ? r.RoundtripTime : float.NaN;
        }
        catch { return float.NaN; }
    }

    static IPAddress? FindGateway()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var g in nic.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) return g.Address;
            }
        }
        catch { }
        return null;
    }

    bool Ready(string key, int cooldownSec)
    {
        long now = Environment.TickCount64;
        if (_lastFired.TryGetValue(key, out long last) && now - last < cooldownSec * 1000L) return false;
        _lastFired[key] = now;
        return true;
    }

    /// <summary>An alert when the connection has been bad for a while, naming where the trouble is.</summary>
    public Alert? Check()
    {
        if (Environment.TickCount64 - _startedAt < 60_000 || Net.Samples < 15) return null;
        var (avg, jitter, loss) = Net.Get();
        var (rAvg, _, rLoss) = Router.Samples >= 15 ? Router.Get() : (float.NaN, 0f, 0f);

        bool bad = loss >= 8 || (!float.IsNaN(avg) && (avg >= 120 || jitter >= 40));
        if (!bad) return null;

        bool localBad = rLoss >= 8 || (!float.IsNaN(rAvg) && rAvg >= 30);
        if (localBad && Ready("net-local", 300))
            return new Alert("net", "Wi-Fi or router trouble",
                $"router {(float.IsNaN(rAvg) ? "not answering" : rAvg.ToString("0") + " ms")} · {rLoss:0}% lost · try a cable", Theme.Warn);
        if (!localBad && Ready("net-internet", 300))
            return new Alert("net", "Internet lag",
                $"{(float.IsNaN(avg) ? "no reply" : avg.ToString("0") + " ms")} · {loss:0}% lost · router is fine, the problem is beyond it", Theme.Warn);
        return null;
    }

    public void Dispose() => _timer?.Dispose();
}

/// <summary>
/// GPU use per process from the Windows "GPU Engine" counters, the same ones
/// Task Manager reads: each process's busiest engine type (3D, copy, video…).
/// Sampled on a worker thread because listing the counters is slow.
/// </summary>
sealed class GpuUsage : IDisposable
{
    readonly Dictionary<string, PerformanceCounter> _counters = new();
    long _lastScan;
    int _busy;
    bool _failed;

    public volatile Dictionary<int, float> ByPid = new();

    public void Kick()
    {
        if (_failed || Interlocked.Exchange(ref _busy, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Run(); }
            catch (Exception ex) { _failed = true; Log.Write("GPU engine counters unavailable: " + ex.Message); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    void Run()
    {
        long now = Environment.TickCount64;
        if (now - _lastScan > 5000)
        {
            _lastScan = now;
            var names = new HashSet<string>(new PerformanceCounterCategory("GPU Engine").GetInstanceNames());
            foreach (string gone in _counters.Keys.Where(k => !names.Contains(k)).ToList())
            {
                _counters[gone].Dispose();
                _counters.Remove(gone);
            }
            foreach (string name in names)
                if (!_counters.ContainsKey(name))
                    _counters[name] = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true);
        }

        // Instance names look like "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D".
        var perEngine = new Dictionary<(int pid, string type), float>();
        foreach (var (name, counter) in _counters.ToList())
        {
            float v;
            try { v = counter.NextValue(); }
            catch { counter.Dispose(); _counters.Remove(name); continue; }
            if (v <= 0 || !name.StartsWith("pid_")) continue;
            int end = name.IndexOf('_', 4);
            if (end < 0 || !int.TryParse(name[4..end], out int pid)) continue;
            int t = name.IndexOf("engtype_", StringComparison.Ordinal);
            string type = t >= 0 ? name[(t + 8)..] : "";
            perEngine[(pid, type)] = perEngine.GetValueOrDefault((pid, type)) + v;
        }
        var result = new Dictionary<int, float>();
        foreach (var ((pid, _), v) in perEngine)
            result[pid] = Math.Max(result.GetValueOrDefault(pid), Math.Min(100, v));
        ByPid = result;
    }

    public void Dispose()
    {
        _failed = true; // no new samples
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // a sample is running; leave the counters to the garbage collector
        foreach (var c in _counters.Values) c.Dispose();
        _counters.Clear();
    }
}

/// <summary>What is going on right now: connection, disk, network traffic and the busiest programs.</summary>
sealed class LiveForm : Form
{
    sealed record Proc(string Name, int Pid, float Cpu, float RamMB, float IoMBs, float Gpu = 0);

    readonly GpuUsage _gpu = new();

    readonly Sensors _sensors;
    readonly NetProbe _net;
    readonly Label _headline;
    readonly ListView _now, _procs;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    PerformanceCounter? _diskTime, _diskBytes;
    string _plan = "reading…";
    int _sortCol = 1;

    // Per-process state between samples.
    Dictionary<int, (TimeSpan cpu, ulong io)> _prev = new();
    long _prevTick;
    long _prevNetBytes, _prevNetTick;
    List<Proc> _top = new();

    public LiveForm(Sensors sensors, NetProbe net)
    {
        _sensors = sensors;
        _net = net;
        Dark.Apply(this, "NotchHUD: what is happening now", new Size(760, 700));

        _headline = new Label
        {
            Dock = DockStyle.Top, Height = 52, Padding = new Padding(14, 12, 0, 0),
            Font = new Font("Segoe UI Semibold", 11f), ForeColor = Dark.Text,
        };
        _now = Make(DockStyle.Top, 300);
        _now.Columns.Add("Right now", 180);
        _now.Columns.Add("", 540);
        _procs = Make(DockStyle.Fill, 0);
        _procs.Columns.Add("Busiest programs (click a column to sort)", 300);
        _procs.Columns.Add("CPU", 80, HorizontalAlignment.Right);
        _procs.Columns.Add("GPU", 80, HorizontalAlignment.Right);
        _procs.Columns.Add("Memory", 100, HorizontalAlignment.Right);
        _procs.Columns.Add("Disk I/O", 110, HorizontalAlignment.Right);
        _procs.ColumnClick += (_, e) => { if (e.Column > 0) { _sortCol = e.Column; Fill(); } };
        _now.HeaderStyle = ColumnHeaderStyle.Nonclickable;

        Controls.Add(_procs);
        Controls.Add(_now);
        Controls.Add(_headline);
        _procs.BringToFront();

        // Counters and the power plan take a moment; keep them off the UI thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _diskTime = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                _diskBytes = new PerformanceCounter("PhysicalDisk", "Disk Bytes/sec", "_Total", true);
                _diskTime.NextValue(); _diskBytes.NextValue();
            }
            catch (Exception ex) { Log.Write("Disk counters unavailable: " + ex.Message); _diskTime = _diskBytes = null; }
            _plan = PowerPlan();
        });

        _timer.Tick += (_, _) => Fill();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Dispose(); _diskTime?.Dispose(); _diskBytes?.Dispose(); _gpu.Dispose(); };
        Sample();
        Fill();
    }

    static ListView Make(DockStyle dock, int height) => new()
    {
        Dock = dock, Height = height, View = System.Windows.Forms.View.Details, FullRowSelect = true,
        BackColor = Dark.Back, ForeColor = Dark.Text, BorderStyle = BorderStyle.None,
    };

    static string PowerPlan()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            string text = p!.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            int a = text.IndexOf('('), b = text.IndexOf(')', a + 1);
            return a >= 0 && b > a ? text[(a + 1)..b] : "unknown";
        }
        catch { return "unknown"; }
    }

    // ------------------------------------------------ sampling

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    static ulong IoBytes(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return 0;
        try { return GetProcessIoCounters(h, out var c) ? c.ReadBytes + c.WriteBytes : 0; }
        finally { CloseHandle(h); }
    }

    void Sample()
    {
        long now = Environment.TickCount64;
        double sec = (now - _prevTick) / 1000.0;
        var cur = new Dictionary<int, (TimeSpan, ulong)>();
        var list = new List<Proc>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0 || p.Id == 4) continue;
                var cpu = p.TotalProcessorTime;
                ulong io = IoBytes(p.Id);
                cur[p.Id] = (cpu, io);
                if (_prev.TryGetValue(p.Id, out var before) && sec is > 0.3 and < 10)
                {
                    float cpuPct = (float)((cpu - before.cpu).TotalSeconds / (sec * Environment.ProcessorCount) * 100);
                    float ioMBs = io >= before.io ? (float)((io - before.io) / sec / 1048576.0) : 0;
                    list.Add(new Proc(p.ProcessName, p.Id, Math.Max(0, cpuPct), p.WorkingSet64 / 1048576f, ioMBs));
                }
            }
            catch { /* protected process */ }
            finally { p.Dispose(); }
        }
        _prev = cur;
        _prevTick = now;
        if (list.Count > 0) _top = list;
    }

    // ------------------------------------------------ display

    void Fill()
    {
        Sample();
        _gpu.Kick();
        var gpuByPid = _gpu.ByPid;

        // Programs, sorted by the chosen column; processes with the same name are added together.
        var grouped = _top.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Proc(g.Key, g.First().Pid, g.Sum(x => x.Cpu), g.Sum(x => x.RamMB), g.Sum(x => x.IoMBs),
                                  Math.Min(100, g.Sum(x => gpuByPid.GetValueOrDefault(x.Pid))))).ToList();
        IEnumerable<Proc> sorted = _sortCol switch
        {
            2 => grouped.OrderByDescending(p => p.Gpu),
            3 => grouped.OrderByDescending(p => p.RamMB),
            4 => grouped.OrderByDescending(p => p.IoMBs),
            _ => grouped.OrderByDescending(p => p.Cpu),
        };
        _procs.BeginUpdate();
        _procs.Items.Clear();
        foreach (var p in sorted.Take(14))
            _procs.Items.Add(new ListViewItem(new[]
            {
                p.Name, $"{p.Cpu:0.0}%", p.Gpu >= 0.5f ? $"{p.Gpu:0}%" : "–", p.RamMB >= 1024 ? $"{p.RamMB / 1024:0.0} GB" : $"{p.RamMB:0} MB",
                p.IoMBs >= 0.05f ? $"{p.IoMBs:0.0} MB/s" : "–",
            }));
        _procs.EndUpdate();

        var rows = new List<(string, string, Color)>();
        var m = _sensors;

        var (avg, jit, loss) = _net.Net.Get();
        rows.Add(("Internet", Latency(avg, jit, loss), LatencyColor(avg, jit, loss)));
        if (_net.RouterAddress.Length > 0)
        {
            var (rAvg, rJit, rLoss) = _net.Router.Get();
            rows.Add(("Router " + _net.RouterAddress, Latency(rAvg, rJit, rLoss), LatencyColor(rAvg * 3, rJit * 3, rLoss)));
        }
        rows.Add(("Network traffic", NetSpeed(), Dark.Text));

        if (_diskTime != null && _diskBytes != null)
        {
            try
            {
                float busy = Math.Min(100, _diskTime.NextValue()), bytes = _diskBytes.NextValue() / 1048576f;
                rows.Add(("Disk", $"{busy:0}% busy · {bytes:0.0} MB/s", busy >= 95 ? Theme.Hot : busy >= 80 ? Theme.Warn : Dark.Text));
            }
            catch { }
        }

        rows.Add(("Processor", Join(Num(m.CpuLoad, "0", "% load"), Num(m.CpuTemp, "0", "°C"),
            Num(m.CpuClockMHz / 1000, "0.00", " GHz"), Num(m.CpuPowerW, "0", " W"),
            m.CpuThrottle.Length > 0 ? "throttling" : ""),
            m.CpuThrottle.Length > 0 ? Theme.Hot : Dark.Text));
        rows.Add(("Graphics", Join(Num(m.GpuLoad, "0", "% load"), Num(m.GpuTemp, "0", "°C"),
            Num(m.GpuClockMHz, "0", " MHz"), Num(m.VramPct, "0", "% video memory"),
            m.GpuThrottle.Length > 0 ? "throttling" : ""),
            m.GpuThrottle.Length > 0 ? Theme.Hot : Dark.Text));
        rows.Add(("Memory", $"{m.RamUsedGB:0.0} / {m.RamTotalGB:0.0} GB ({m.RamPct:0}%)",
            m.RamPct >= 93 ? Theme.Hot : m.RamPct >= 85 ? Theme.Warn : Dark.Text));
        rows.Add(("Power plan", _plan, Dark.Text));
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        rows.Add(("Uptime", up.TotalDays >= 1 ? $"{(int)up.TotalDays} d {up.Hours} h" : $"{up.Hours} h {up.Minutes} min", Dark.Text));

        _now.BeginUpdate();
        _now.Items.Clear();
        foreach (var (name, value, color) in rows)
            _now.Items.Add(new ListViewItem(new[] { name, value }) { ForeColor = color });
        _now.EndUpdate();

        var busiest = grouped.OrderByDescending(p => p.Cpu).FirstOrDefault();
        _headline.Text = busiest != null && busiest.Cpu >= 5
            ? $"{busiest.Name} is the busiest program, using {busiest.Cpu:0}% of the CPU"
            : "Nothing is hogging the PC right now";
        _headline.ForeColor = busiest != null && busiest.Cpu >= 25 ? Theme.Warn : Theme.Good;
    }

    string NetSpeed()
    {
        long total = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                {
                    var s = nic.GetIPv4Statistics();
                    total += s.BytesReceived + s.BytesSent;
                }
        }
        catch { return "unavailable"; }
        long now = Environment.TickCount64;
        double mbit = _prevNetTick > 0 && now > _prevNetTick ? (total - _prevNetBytes) * 8.0 / ((now - _prevNetTick) / 1000.0) / 1e6 : 0;
        _prevNetBytes = total;
        _prevNetTick = now;
        return $"{Math.Max(0, mbit):0.0} Mbit/s total (down + up)";
    }

    static string Latency(float avg, float jitter, float loss) =>
        float.IsNaN(avg) ? (loss >= 100 ? "no reply" : "measuring…") : $"{avg:0} ms · jitter {jitter:0} ms · {loss:0}% lost";

    static Color LatencyColor(float avg, float jitter, float loss) =>
        loss >= 8 || avg >= 150 || jitter >= 50 ? Theme.Hot
        : loss >= 2 || avg >= 80 || jitter >= 20 ? Theme.Warn : Theme.Good;

    static string Num(float v, string fmt, string unit) => float.IsNaN(v) ? "" : v.ToString(fmt) + unit;
    static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => p.Length > 0));
}
