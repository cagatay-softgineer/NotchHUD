using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace NotchHUD;

/// <summary>
/// Hardware readings polled once a second on a background thread:
/// temperatures, GPU load and video memory, CPU load, RAM, and (while a game
/// runs) the heaviest background process.
/// </summary>
public sealed class Sensors : IDisposable
{
    const int PollMs = 1000;

    Computer? _pc;
    IHardware? _cpuHw, _gpuHw;
    ISensor? _cpu, _gpu, _gpuLoad, _vramUsed, _vramTotal, _cpuTotal;
    List<ISensor> _cpuThreads = new();
    System.Threading.Timer? _timer;
    int _busy, _gpuTries, _polls;

    public volatile float CpuTemp = float.NaN;
    public volatile float GpuTemp = float.NaN;
    public volatile float GpuLoad = float.NaN;
    public volatile float VramUsedMB = float.NaN;
    public volatile float VramTotalMB = float.NaN;
    public volatile float CpuLoad = float.NaN;
    public volatile float CpuMaxThread = float.NaN;
    public volatile float RamUsedGB, RamTotalGB;

    // Throttling and health. Clocks are in MHz.
    public volatile float CpuClockMHz = float.NaN;
    public volatile float GpuClockMHz = float.NaN;
    public volatile float CpuPowerW = float.NaN;
    public volatile float GpuPowerW = float.NaN;

    /// <summary>"" when fine, otherwise "thermal" or "power".</summary>
    public volatile string CpuThrottle = "";
    public volatile string GpuThrottle = "";
    public int CpuThrottleSeconds => _cpuThr.Seconds;
    public int GpuThrottleSeconds => _gpuThr.Seconds;

    /// <summary>Everything the health window shows. Replaced as a whole every few seconds.</summary>
    public volatile IReadOnlyList<HealthItem> Health = Array.Empty<HealthItem>();

    // Ryzen parts hold 95° by design, so 93° under load is the limit; most graphics cards start backing off in the low 80s.
    readonly ThrottleDetector _cpuThr = new(hotTemp: 85, limitTemp: 93, dropRatio: 0.88f, detectPower: false);
    readonly ThrottleDetector _gpuThr = new(hotTemp: 80, limitTemp: 95, dropRatio: 0.85f, detectPower: true);
    ISensor? _cpuPower, _gpuClock, _gpuPower, _gpuFan, _gpuHotSpot;
    List<ISensor> _cpuClocks = new();
    readonly List<IHardware> _slowHw = new();   // drives, motherboard, battery: updated every few seconds
    readonly List<ISensor> _fans = new();
    readonly HashSet<string> _fanSeen = new();

    /// <summary>Set while a game is in the foreground; enables background-process sampling.</summary>
    public volatile int GamePid;
    public volatile string HogName = "";
    public volatile float HogPct;
    public volatile int HogPid;

    /// <summary>Load of every CPU thread, 0-100. Replaced as a whole on each poll.</summary>
    public volatile float[] ThreadLoads = Array.Empty<float>();

    /// <summary>"Artist - Song" from Spotify, or "" when nothing is playing.</summary>
    public volatile string NowPlaying = "";
    public volatile bool WatchMusic = true;

    public float VramPct => VramTotalMB > 0 ? VramUsedMB / VramTotalMB * 100f : float.NaN;
    public float RamPct => RamTotalGB > 0 ? RamUsedGB / RamTotalGB * 100f : float.NaN;

    public void Start()
    {
        // Opening the hardware tree takes a moment; keep it off the UI thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _pc = new Computer
                {
                    IsCpuEnabled = true, IsGpuEnabled = true,
                    IsStorageEnabled = true, IsMotherboardEnabled = true, IsBatteryEnabled = true,
                };
                _pc.Open();
                Pick();
            }
            catch (Exception ex)
            {
                Log.Write("Sensor init failed: " + ex);
            }
            _timer = new System.Threading.Timer(_ => Poll(), null, 0, PollMs);
        });
    }

    static readonly Regex Integrated = new(@"Radeon(\(TM\))? Graphics$|UHD|Iris|HD Graphics", RegexOptions.IgnoreCase);

    void Pick()
    {
        IHardware? gpuFallback = null;
        foreach (var hw in _pc!.Hardware)
        {
            hw.Update();
            var temps = Of(hw, SensorType.Temperature);
            Log.Write($"hw {hw.HardwareType} '{hw.Name}': " +
                      string.Join(", ", temps.Select(t => $"{t.Name}={t.Value:0.#}")));

            if (hw.HardwareType == HardwareType.Cpu && _cpuHw == null)
            {
                _cpuHw = hw;
                _cpu = First(temps, "Tctl/Tdie") ?? First(temps, "Package") ?? First(temps, "Core Max")
                       ?? First(temps, "Core Average") ?? temps.FirstOrDefault();
                var loads = Of(hw, SensorType.Load);
                _cpuTotal = First(loads, "CPU Total");
                _cpuThreads = loads.Where(x => x.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase)).ToList();
                if (_cpuThreads.Count == 0 && First(loads, "CPU Core Max") is { } max) _cpuThreads.Add(max);
                _cpuClocks = Of(hw, SensorType.Clock)
                    .Where(x => x.Name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase)
                                && !x.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase)).ToList();
                _cpuPower = First(Of(hw, SensorType.Power), "Package");
            }
            else if (hw.HardwareType is HardwareType.Storage or HardwareType.Motherboard or HardwareType.Battery)
            {
                _slowHw.Add(hw);
                CollectFans(hw);
            }
            else if (hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
            {
                if (Integrated.IsMatch(hw.Name)) gpuFallback ??= hw;
                else _gpuHw ??= hw;
            }
        }

        _gpuHw ??= gpuFallback;
        PickGpuSensors();
        Log.Write($"using cpu='{_cpuHw?.Name}'/{_cpu?.Name} gpu='{_gpuHw?.Name}'/{_gpu?.Name}");
    }

    // Some GPU drivers only expose their sensors after a few updates.
    void PickGpuSensors()
    {
        if (_gpuHw == null) return;
        _gpuTries++;
        var temps = Of(_gpuHw, SensorType.Temperature);
        _gpu = First(temps, "GPU Core") ?? First(temps, "Core") ?? temps.FirstOrDefault();

        var loads = Of(_gpuHw, SensorType.Load);
        _gpuLoad = loads.FirstOrDefault(x => x.Name == "GPU Core") ?? loads.FirstOrDefault(x => x.Name == "D3D 3D")
                   ?? First(loads, "3D");
        var data = Of(_gpuHw, SensorType.SmallData);
        _vramUsed = data.FirstOrDefault(x => x.Name == "D3D Dedicated Memory Used") ?? First(data, "GPU Memory Used");
        _vramTotal = data.FirstOrDefault(x => x.Name == "D3D Dedicated Memory Total") ?? First(data, "GPU Memory Total");
        var clocks = Of(_gpuHw, SensorType.Clock);
        _gpuClock = clocks.FirstOrDefault(x => x.Name == "GPU Core") ?? First(clocks, "Core");
        _gpuPower = Of(_gpuHw, SensorType.Power).FirstOrDefault();
        _gpuFan = Of(_gpuHw, SensorType.Fan).FirstOrDefault();
        _gpuHotSpot = First(temps, "Hot Spot");

        if (_gpuTries == 3)
            Log.Write($"gpu sensors: temp={_gpu?.Name} load={_gpuLoad?.Name} vram={_vramUsed?.Name}/{_vramTotal?.Name}");
    }

    // Fan headers live on the motherboard's Super I/O chip, one level down.
    void CollectFans(IHardware hw)
    {
        _fans.AddRange(Of(hw, SensorType.Fan));
        foreach (var sub in hw.SubHardware) { sub.Update(); CollectFans(sub); }
    }

    static void UpdateAll(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateAll(sub);
    }

    static IEnumerable<IHardware> Tree(IHardware hw)
    {
        yield return hw;
        foreach (var sub in hw.SubHardware)
            foreach (var x in Tree(sub)) yield return x;
    }

    static List<ISensor> Of(IHardware hw, SensorType type) => hw.Sensors.Where(x => x.SensorType == type).ToList();

    static ISensor? First(List<ISensor> list, string part) =>
        list.FirstOrDefault(x => x.Name.Contains(part, StringComparison.OrdinalIgnoreCase));

    static float Val(ISensor? s) => s?.Value is float v ? v : float.NaN;

    void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            _polls++;
            _cpuHw?.Update();
            _gpuHw?.Update();
            if (_gpuHw != null && _gpuTries < 6 && (_gpu == null || _gpuLoad == null || _vramUsed == null))
                PickGpuSensors();

            CpuTemp = _cpu?.Value is float c && c > 0 ? c : float.NaN;
            GpuTemp = _gpu?.Value is float g && g > 0 ? g
                : _gpu == null && _gpuTries >= 3 ? KmtGpu.Read()
                : float.NaN;
            GpuLoad = Val(_gpuLoad);
            VramUsedMB = Val(_vramUsed);
            VramTotalMB = Val(_vramTotal);
            CpuLoad = Val(_cpuTotal);

            // Throttling: clocks, power, and a verdict from the detectors.
            float clock = float.NaN;
            foreach (var s in _cpuClocks)
                if (s.Value is float f && f > 0 && !(f <= clock)) clock = f;
            CpuClockMHz = clock;
            GpuClockMHz = Val(_gpuClock);
            CpuPowerW = Val(_cpuPower);
            GpuPowerW = Val(_gpuPower);
            _cpuThr.Update(CpuLoad, CpuClockMHz, CpuTemp);
            _gpuThr.Update(GpuLoad, GpuClockMHz, GpuTemp);
            CpuThrottle = _cpuThr.Kind;
            GpuThrottle = _gpuThr.Kind;

            if (_polls % 5 == 0)
            {
                foreach (var hw in _slowHw) UpdateAll(hw);
                Health = BuildHealth();
            }

            float max = float.NaN;
            var loads = new float[_cpuThreads.Count];
            for (int i = 0; i < loads.Length; i++)
            {
                if (_cpuThreads[i].Value is not float v) continue;
                loads[i] = v;
                if (!(v <= max)) max = v;
            }
            CpuMaxThread = max;
            ThreadLoads = loads;

            if (_polls % 2 == 0) PollMusic();

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                RamTotalGB = mem.ullTotalPhys / 1073741824f;
                RamUsedGB = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824f;
            }

            if (_polls % 5 == 0) SampleProcesses();
        }
        catch (Exception ex)
        {
            Log.Write("Sensor poll failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    // ------------------------------------------------ hardware health

    IReadOnlyList<HealthItem> BuildHealth()
    {
        var list = new List<HealthItem>();
        static int Level(float v, float warn, float bad) => v >= bad ? 2 : v >= warn ? 1 : 0;

        if (!float.IsNaN(CpuTemp))
        {
            string t = CpuTemp.ToString("0") + "°C";
            string state = CpuThrottle == "thermal" ? " · throttling" : "";
            int lvl = CpuThrottle == "thermal" ? 2 : Level(CpuTemp, 80, 90);
            list.Add(new HealthItem("cpu-temp", "Processor", "CPU temperature", t + state, lvl, "check the cooler and case airflow"));
        }
        if (!float.IsNaN(CpuClockMHz))
        {
            string state = CpuThrottle == "power" ? " · held back by a power limit" : "";
            list.Add(new HealthItem("cpu-clock", "Processor", "CPU clock",
                $"{CpuClockMHz / 1000:0.00} GHz" + (float.IsNaN(CpuPowerW) ? "" : $" · {CpuPowerW:0} W") + state,
                CpuThrottle == "power" ? 1 : 0, "check power limits in the BIOS"));
        }
        if (!float.IsNaN(GpuTemp))
        {
            string t = GpuTemp.ToString("0") + "°C";
            if (_gpuHotSpot?.Value is float hs && hs > 0) t += $" · hot spot {hs:0}°C";
            if (GpuThrottle.Length > 0) t += " · throttling";
            list.Add(new HealthItem("gpu-temp", "Graphics", "GPU temperature", t,
                GpuThrottle == "thermal" ? 2 : Level(GpuTemp, 75, 85), "check the fans and case airflow"));
        }
        if (!float.IsNaN(GpuClockMHz))
            list.Add(new HealthItem("gpu-clock", "Graphics", "GPU clock",
                $"{GpuClockMHz:0} MHz" + (float.IsNaN(GpuPowerW) ? "" : $" · {GpuPowerW:0} W") +
                (GpuThrottle == "power" ? " · held back by a power or driver limit" : ""),
                GpuThrottle == "power" ? 1 : 0, "check the power limit and driver settings"));
        if (_gpuFan?.Value is float gf && gf >= 0)
        {
            bool stalled = gf < 50 && GpuTemp >= 70;
            list.Add(new HealthItem("gpu-fan", "Graphics", "GPU fan",
                stalled ? $"{gf:0} RPM at {GpuTemp:0}°C" : $"{gf:0} RPM", stalled ? 2 : 0, "the fan may be stuck or failing"));
        }

        // Fans on the motherboard. A header that has spun before and now reads zero has stopped.
        foreach (var fan in _fans)
        {
            if (fan.Value is not float rpm) continue;
            string id = fan.Hardware.Name + "/" + fan.Name;
            if (rpm > 200) _fanSeen.Add(id);
            else if (!_fanSeen.Contains(id)) continue; // empty header
            bool stopped = rpm < 50 && !float.IsNaN(CpuTemp) && CpuTemp >= 60;
            list.Add(new HealthItem("fan:" + id, "Cooling", fan.Name, $"{rpm:0} RPM" + (rpm < 50 ? " · stopped" : ""),
                stopped ? 2 : rpm < 50 ? 1 : 0, "the fan stopped while the system is warm"));
        }

        foreach (var root in _slowHw)
        foreach (var hw in Tree(root))
        {
            if (hw.HardwareType == HardwareType.Storage)
            {
                // NVMe drives also report their warning and critical limits as
                // "temperatures"; those are fixed thresholds, not readings. The
                // composite reading is the drive's own overall figure, the one
                // Samsung Magician and similar tools show.
                var temps = Of(hw, SensorType.Temperature)
                    .Where(x => !x.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                                && !x.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase)
                                && !x.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase)
                                && !x.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                float? temp = (First(temps, "Composite") ?? temps.FirstOrDefault())?.Value;
                if (temp is not > 0) temp = null;
                if (temp is float tv)
                    list.Add(new HealthItem("drive-temp:" + hw.Name, "Storage", hw.Name + " temperature", $"{tv:0}°C",
                        Level(tv, 60, 70), "add airflow over the drive or a heatsink"));

                foreach (var s in Of(hw, SensorType.Level))
                {
                    if (s.Value is not float v) continue;
                    // "Available Spare Threshold" is the drive's fixed minimum, not a reading.
                    if (s.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase)
                        || s.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)) continue;
                    bool life = s.Name.Contains("Remaining", StringComparison.OrdinalIgnoreCase)
                                || s.Name.Contains("Life", StringComparison.OrdinalIgnoreCase);
                    bool spare = s.Name.Contains("Spare", StringComparison.OrdinalIgnoreCase);
                    if (!life && !spare) continue;
                    int lvl = v <= 10 ? 2 : v <= 25 ? 1 : 0;
                    list.Add(new HealthItem($"drive-{(life ? "life" : "spare")}:{hw.Name}", "Storage",
                        $"{hw.Name} {(life ? "life left" : "spare capacity")}", $"{v:0}%", lvl,
                        "back up the drive and plan a replacement"));
                }
            }
            else if (hw.HardwareType == HardwareType.Battery)
            {
                foreach (var s in Of(hw, SensorType.Level))
                {
                    if (s.Value is not float v) continue;
                    if (s.Name.Contains("Degradation", StringComparison.OrdinalIgnoreCase))
                        list.Add(new HealthItem("battery-wear", "Battery", "Battery wear", $"{v:0}%", v >= 40 ? 2 : v >= 25 ? 1 : 0,
                            "capacity has dropped; consider a new battery"));
                }
            }
        }

        if (list.Count > 0 && _polls % 60 == 0)
            Log.Write("health: " + string.Join(" | ", list.Select(i => $"{i.Name}={i.Value}")));
        return list;
    }

    // Spotify puts the current track in its window title while it plays.
    // Listing every process is the expensive part, so the Spotify window's
    // process is found once and then only its title is re-read.
    Process? _spotify;
    int _spotifyScanIn;

    void PollMusic()
    {
        if (!WatchMusic) { NowPlaying = ""; return; }
        string track = "";
        try
        {
            if (_spotify != null)
            {
                _spotify.Refresh();
                if (_spotify.HasExited) { _spotify.Dispose(); _spotify = null; }
            }
            if (_spotify == null && --_spotifyScanIn <= 0)
            {
                _spotifyScanIn = 10; // look again in about 20 seconds
                foreach (var p in Process.GetProcessesByName("Spotify"))
                {
                    if (_spotify == null && p.MainWindowTitle.Length > 0) _spotify = p;
                    else p.Dispose();
                }
            }
            if (_spotify != null)
            {
                string title = _spotify.MainWindowTitle;
                if (title.Length > 0 && !title.StartsWith("Spotify", StringComparison.OrdinalIgnoreCase)) track = title;
            }
        }
        catch { _spotify = null; }
        NowPlaying = track;
    }

    // ------------------------------------------------ background processes

    static readonly HashSet<string> IgnoredHogs = new(StringComparer.OrdinalIgnoreCase)
        { "Idle", "System", "dwm", "Memory Compression", "Registry", "NotchHUD", "csrss", "audiodg" };

    Dictionary<int, TimeSpan> _prevCpu = new();
    long _prevSampleTick;

    void SampleProcesses()
    {
        int game = GamePid;
        if (game == 0)
        {
            _prevCpu.Clear();
            HogName = "";
            HogPct = 0;
            return;
        }

        long now = Environment.TickCount64;
        double elapsedMs = now - _prevSampleTick;
        var cur = new Dictionary<int, TimeSpan>();
        string best = "";
        double bestPct = 0;
        int bestPid = 0;

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == game || p.Id == Environment.ProcessId || IgnoredHogs.Contains(p.ProcessName)) continue;
                TimeSpan t = p.TotalProcessorTime;
                cur[p.Id] = t;
                if (_prevCpu.TryGetValue(p.Id, out var before) && elapsedMs > 500 && elapsedMs < 20_000)
                {
                    double pct = (t - before).TotalMilliseconds / (elapsedMs * Environment.ProcessorCount) * 100;
                    if (pct > bestPct) { bestPct = pct; best = p.ProcessName; bestPid = p.Id; }
                }
            }
            catch { /* protected process */ }
            finally { p.Dispose(); }
        }

        _prevCpu = cur;
        _prevSampleTick = now;
        HogName = best;
        HogPid = bestPid;
        HogPct = (float)bestPct;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public void Dispose()
    {
        _timer?.Dispose();
        try { _pc?.Close(); } catch { }
    }
}
