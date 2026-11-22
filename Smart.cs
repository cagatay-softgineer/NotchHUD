using System.Diagnostics;
using System.Text.Json;

namespace NotchHUD;

/// <summary>One finished play session or benchmark run.</summary>
public sealed class SessionRecord
{
    public string Kind { get; set; } = "session"; // session | benchmark
    public string Game { get; set; } = "";
    public string Exe { get; set; } = "";
    public DateTime Start { get; set; }
    public int Seconds { get; set; }
    public float Avg { get; set; }
    public float Low1 { get; set; }
    public float Low01 { get; set; }
    public int Stutters { get; set; }
    public int Smoothness { get; set; }
    public string StutterCause { get; set; } = "";
    public float MaxCpuTemp { get; set; }
    public float MaxGpuTemp { get; set; }
    public float AvgGpuLoad { get; set; }
    public float PeakVramMB { get; set; }
    public string Limit { get; set; } = "";       // what held the frame rate back most of the time
    public string Note { get; set; } = "";        // one-sentence debrief
    public int ThrottleSeconds { get; set; }      // time the CPU or GPU spent slowing itself down
    public string ThrottleWhat { get; set; } = ""; // e.g. "CPU heat", "GPU heat", "GPU power limit"
    public float MaxCpuClockMHz { get; set; }
    public float AvgPing { get; set; }
    public float MaxPing { get; set; }
    public float PingLoss { get; set; }           // percent of pings that got no reply
    public List<string> Events { get; set; } = new(); // "m:ss  what happened", in order

    // Left out of the numbers above (Seconds is gameplay only).
    public int LoadingSeconds { get; set; }
    public int CutsceneSeconds { get; set; }
    public int MenuSeconds { get; set; }
    public int Loads { get; set; }
    public int Cutscenes { get; set; }
    public int Freezes { get; set; }
}

/// <summary>What is normal for one game on this PC, learned from earlier sessions.</summary>
public sealed record Baseline(int Sessions, float Avg, float Low1, float MaxCpu, float MaxGpu)
{
    public string Word => Sessions >= 2 ? "your usual" : "last time";
}

static class History
{
    static string FilePath => Path.Combine(Log.Folder, "history.json");
    static List<SessionRecord>? _cache;

    public static List<SessionRecord> All()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { Log.Write("History load failed: " + ex.Message); }
        return _cache ??= new List<SessionRecord>();
    }

    /// <summary>The most recent earlier record for the same game and kind.</summary>
    public static SessionRecord? LastFor(string exe, string kind) =>
        All().LastOrDefault(r => r.Kind == kind && string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>Median of the last few real sessions of a game.</summary>
    public static Baseline? BaselineFor(string exe)
    {
        var list = All().Where(r => r.Kind == "session" && r.Seconds >= 120 && r.Avg > 0
                                    && string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase))
                        .TakeLast(8).ToList();
        if (list.Count == 0) return null;
        return new Baseline(list.Count, Median(list.Select(r => r.Avg)), Median(list.Select(r => r.Low1)),
            Median(list.Select(r => r.MaxCpuTemp)), Median(list.Select(r => r.MaxGpuTemp)));
    }

    /// <summary>
    /// Long-term drift for a game: the last three sessions against the ones
    /// before them. Null when there is too little history or nothing changed.
    /// </summary>
    public static Check? Drift(string exe)
    {
        var list = All().Where(r => r.Kind == "session" && r.Seconds >= 120 && r.Avg > 0
                                    && string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count < 6) return null;
        var recent = list.TakeLast(3).ToList();
        var older = list.Take(list.Count - 3).TakeLast(6).ToList();

        float tempNow = Median(recent.Where(r => r.MaxGpuTemp > 0).Select(r => r.MaxGpuTemp));
        float tempThen = Median(older.Where(r => r.MaxGpuTemp > 0).Select(r => r.MaxGpuTemp));
        if (tempNow > 0 && tempThen > 0 && tempNow - tempThen >= 5)
            return new Check(false, $"GPU peaks {tempNow - tempThen:0}° hotter than in earlier sessions. Check for dust");

        float fpsNow = Median(recent.Select(r => r.Avg)), fpsThen = Median(older.Select(r => r.Avg));
        if (fpsThen <= 0) return null;
        float d = (fpsNow - fpsThen) / fpsThen * 100f;
        if (d <= -8) return new Check(false, $"FPS in this game is down {-d:0}% compared with earlier sessions");
        if (d >= 8) return new Check(true, $"FPS in this game is up {d:0}% compared with earlier sessions");
        return null;
    }

    static float Median(IEnumerable<float> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    public static void Add(SessionRecord r)
    {
        var all = All();
        all.Add(r);
        if (all.Count > 1000) all.RemoveRange(0, all.Count - 1000);
        try
        {
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("History save failed: " + ex.Message); }
    }
}

/// <summary>Running maxima and averages for a session or a benchmark.</summary>
sealed class Acc
{
    public float MaxCpu, MaxGpu, PeakVram, MaxCpuClock;
    double _gpuLoadSum;
    int _n, _gpuBound, _cpuBound, _capped, _throttle, _cpuHeat, _gpuHeat, _gpuPower;

    public int ThrottleSeconds => _throttle;

    double _pingSum;
    int _pingN, _pingLost;
    public float MaxPing;
    public float AvgPing => _pingN > 0 ? (float)(_pingSum / _pingN) : 0;
    public float PingLoss => _pingN + _pingLost > 0 ? _pingLost * 100f / (_pingN + _pingLost) : 0;

    public void AddPing(float ms)
    {
        if (float.IsNaN(ms)) { _pingLost++; return; }
        _pingSum += ms;
        _pingN++;
        if (ms > MaxPing) MaxPing = ms;
    }

    /// <summary>What throttled the most this session, or "".</summary>
    public string ThrottleWhat
    {
        get
        {
            int max = Math.Max(_cpuHeat, Math.Max(_gpuHeat, _gpuPower));
            if (max == 0) return "";
            return max == _cpuHeat ? "CPU heat" : max == _gpuHeat ? "GPU heat" : "GPU power limit";
        }
    }

    public void Add(Sensors m, string tag)
    {
        if (m.CpuClockMHz > MaxCpuClock) MaxCpuClock = m.CpuClockMHz;
        if (m.CpuThrottle.Length > 0 || m.GpuThrottle.Length > 0) _throttle++;
        if (m.CpuThrottle.Length > 0) _cpuHeat++;
        if (m.GpuThrottle == "thermal") _gpuHeat++;
        else if (m.GpuThrottle == "power") _gpuPower++;
        if (m.CpuTemp > MaxCpu) MaxCpu = m.CpuTemp;
        if (m.GpuTemp > MaxGpu) MaxGpu = m.GpuTemp;
        if (m.VramUsedMB > PeakVram) PeakVram = m.VramUsedMB;
        if (!float.IsNaN(m.GpuLoad)) { _gpuLoadSum += m.GpuLoad; _n++; }
        if (tag == Advisor.GpuBound) _gpuBound++;
        else if (tag == Advisor.CpuBound) _cpuBound++;
        else if (tag == Advisor.Capped) _capped++;
    }

    public float AvgGpuLoad => _n > 0 ? (float)(_gpuLoadSum / _n) : 0;

    public string Limit
    {
        get
        {
            int max = Math.Max(_gpuBound, Math.Max(_cpuBound, _capped));
            if (max == 0) return "";
            return max == _gpuBound ? "GPU" : max == _cpuBound ? "CPU" : "Cap";
        }
    }
}

public sealed record Alert(string Key, string Title, string Detail, Color Color);

/// <summary>
/// The "smart" part: watches the live numbers, works out what is holding the
/// frame rate back, learns what is normal for each game, ties stutters to
/// their likely cause, predicts trouble before it arrives, and writes short
/// plain-language advice.
/// </summary>
sealed class Advisor
{
    public const string GpuBound = "GPU-BOUND", CpuBound = "CPU-BOUND", Capped = "CAPPED";

    public string Tag { get; private set; } = "";
    public readonly List<string> Insights = new();
    public Baseline? Base;

    /// <summary>Live smoothness, 0-100: how even the frame pacing is.</summary>
    public int Score { get; private set; } = -1;

    float _gpuEma = -1, _jitterEma = -1;
    int _vramHigh, _cpuHot, _gpuHot, _drop, _hog, _ramHigh, _pendingCount, _lastStutters, _samples;
    int _freezeSeq = -1, _halved, _hz = -1;
    string _pendingTag = "";
    bool _baselineAlerted;
    readonly Dictionary<string, long> _lastFired = new();
    readonly Dictionary<string, int> _causes = new();
    readonly float[] _cpuTrend = new float[64], _vramTrend = new float[64];

    public void Reset()
    {
        Tag = "";
        _pendingTag = "";
        Insights.Clear();
        Base = null;
        Score = -1;
        _gpuEma = _jitterEma = -1;
        _vramHigh = _cpuHot = _gpuHot = _drop = _hog = _ramHigh = _lastStutters = _samples = _halved = 0;
        _freezeSeq = _hz = -1;
        _baselineAlerted = false;
        _lastFired.Clear();
        _causes.Clear();
    }

    /// <summary>The condition most stutters coincided with this session, or "".</summary>
    public string TopCause
    {
        get
        {
            if (_causes.Count == 0) return "";
            var top = _causes.MaxBy(kv => kv.Value);
            int total = _causes.Values.Sum();
            return top.Key != "" && top.Value * 2 >= total ? top.Key : "";
        }
    }

    public static int ScoreOf(float avg, float low1, float stuttersPerMin)
    {
        if (avg <= 0 || low1 <= 0) return -1;
        float pacing = Math.Clamp((low1 / avg - 0.3f) / 0.55f, 0f, 1f) * 100f;
        return (int)Math.Clamp(pacing - Math.Min(30f, 5f * stuttersPerMin), 0f, 100f);
    }

    public static int ScoreOf(Hist h) =>
        ScoreOf(h.AvgFps, h.LowFps(0.01), h.Seconds > 0 ? (float)(h.Stutters / (h.Seconds / 60.0)) : 0);

    /// <summary>Change per second over the last half minute.</summary>
    float Slope(float[] ring)
    {
        if (_samples < 31) return 0;
        float now = ring[(_samples - 1) & 63], then = ring[(_samples - 31) & 63];
        return float.IsNaN(now) || float.IsNaN(then) ? 0 : (now - then) / 30f;
    }

    /// <summary>Call once a second while the game is in the foreground.</summary>
    public Alert? Update(in FrameStats st, Sensors m, long sessionAgeMs, Hist? session)
    {
        float gpu = m.GpuLoad, vramPct = m.VramPct, cpuT = m.CpuTemp, gpuT = m.GpuTemp;
        // Loading screens, cutscenes and menus are not gameplay: no verdicts, drops or stutter alerts.
        bool scene = st.Phase is { Length: > 0 };
        if (_hz < 0)
        {
            try { _hz = Preflight.DisplayRefresh().hz; } catch { _hz = 0; }
        }
        if (!float.IsNaN(gpu)) _gpuEma = _gpuEma < 0 ? gpu : _gpuEma + (gpu - _gpuEma) * 0.3f;
        if (st.FramesLastSec >= 10) _jitterEma = _jitterEma < 0 ? st.Jitter : _jitterEma + (st.Jitter - _jitterEma) * 0.3f;

        _cpuTrend[_samples & 63] = cpuT;
        _vramTrend[_samples & 63] = m.VramUsedMB;
        _samples++;

        Score = ScoreOf(st.Average, st.Low1, st.Stutters10s * 6f);

        // What is limiting the frame rate right now? A verdict has to hold for
        // a few seconds before it replaces the current one.
        bool steady = _jitterEma >= 0 && _jitterEma < 0.06f;
        if (_gpuEma >= 0 && !scene)
        {
            string verdict = _gpuEma >= 93 ? GpuBound
                : _gpuEma < 88 && steady ? Capped
                : _gpuEma < 85 ? CpuBound
                : Tag;
            if (verdict == Tag) _pendingTag = "";
            else if (verdict == _pendingTag) { if (++_pendingCount >= 4 || Tag.Length == 0) Tag = verdict; }
            else { _pendingTag = verdict; _pendingCount = 1; if (Tag.Length == 0) Tag = verdict; }
        }

        // Tie each new stutter to what was going on when it happened.
        if (session != null)
        {
            int fresh = session.Stutters - _lastStutters;
            _lastStutters = session.Stutters;
            if (fresh > 0 && fresh <= 14)
            {
                string cause = st.SpikePeriodMs > 0 ? "background polling (stutters on a timer)"
                    : m.CpuThrottle.Length > 0 || m.GpuThrottle.Length > 0 ? "throttling"
                    : vramPct >= 92 ? "full video memory"
                    : m.HogPct >= 8 ? "background apps"
                    : m.RamPct >= 90 ? "low system memory"
                    : cpuT >= 90 ? "CPU heat"
                    : "";
                _causes[cause] = _causes.GetValueOrDefault(cause) + fresh;
            }
        }

        _vramHigh = vramPct >= 95 ? _vramHigh + 1 : 0;
        _cpuHot = cpuT >= 90 ? _cpuHot + 1 : 0;
        _gpuHot = gpuT >= 85 ? _gpuHot + 1 : 0;
        _ramHigh = m.RamPct >= 93 ? _ramHigh + 1 : 0;
        // A steady low frame rate with an idle GPU is a menu or a frame cap, not a drop.
        bool deliberate = scene || steady && _gpuEma < 88;
        _drop = !deliberate && st.Average > 0 && st.Current > 0 && st.Current < st.Average * 0.6f ? _drop + 1 : 0;
        _hog = m.HogPct >= 8 ? _hog + 1 : 0;

        // Predictions from the last half minute.
        float cpuSlope = Slope(_cpuTrend), vramSlope = Slope(_vramTrend);
        float heatEta = cpuSlope > 0.05f && cpuT >= 76 && cpuT < 90 ? (90 - cpuT) / cpuSlope : float.NaN;
        float vramEta = vramSlope > 2f && vramPct >= 80 && vramPct < 95
            ? (m.VramTotalMB * 0.97f - m.VramUsedMB) / vramSlope : float.NaN;

        // How does this session compare with what is normal for this game?
        float vsUsual = float.NaN;
        if (Base != null && Base.Avg > 0 && session != null && session.Seconds >= 120)
            vsUsual = (session.AvgFps - Base.Avg) / Base.Avg * 100f;

        // V-Sync on a fixed-refresh screen drops a game that cannot hold the
        // refresh rate to exactly half of it: steady, GPU maxed, at hz / 2.
        float half = _hz / 2f;
        _halved = !scene && _hz >= 50 && st.FramesLastSec >= 10 && st.Jitter < 0.05f && _gpuEma >= 85
                  && Math.Abs(st.Current - half) <= half * 0.04f ? _halved + 1 : 0;

        BuildInsights(st, m, session, heatEta, vramEta, vsUsual);

        // A freeze is announced whenever it happens, even early on.
        if (_freezeSeq < 0) _freezeSeq = st.FreezeSeq;
        else if (st.FreezeSeq != _freezeSeq)
        {
            _freezeSeq = st.FreezeSeq;
            string why = vramPct >= 92 ? " · video memory is full"
                : m.RamPct >= 90 ? " · system memory is nearly full"
                : m.HogPct >= 8 ? $" · {m.HogName} was busy"
                : "";
            return new Alert("freeze", "Game froze", $"{st.LastFreezeMs / 1000:0.0} s without a new frame{why}", Theme.Hot);
        }

        if (sessionAgeMs < 20_000) return null; // let the game finish loading first

        // Outside gameplay only heat matters.
        if (scene)
        {
            if (_cpuHot >= 5 && Ready("cputemp", 180))
                return new Alert("cputemp", "CPU running hot", $"{cpuT:0}° · the limit is 95°", Theme.Hot);
            if (_gpuHot >= 5 && Ready("gputemp", 180))
                return new Alert("gputemp", "GPU running hot", $"{gpuT:0}° · check fans and airflow", Theme.Hot);
            return null;
        }

        if (_halved >= 8 && Ready("vsync", 900))
            return new Alert("vsync", "V-Sync is halving your FPS",
                $"locked at {half:0} on a {_hz} Hz display · use FreeSync or a frame cap instead", Theme.Warn);
        if (st.SpikePeriodMs > 0 && st.Stutters10s >= 2 && Ready("periodic", 600))
            return new Alert("periodic", "Stutter on a timer",
                $"a hitch every {st.SpikePeriodMs / 1000:0.0} s · usually monitoring or RGB software polling", Theme.Warn);

        if (_vramHigh >= 5 && Ready("vram", 300))
            return new Alert("vram", "Video memory full",
                $"{m.VramUsedMB / 1024:0.0} / {m.VramTotalMB / 1024:0.0} GB · lower texture quality", Theme.Hot);
        if (_cpuHot >= 5 && Ready("cputemp", 180))
            return new Alert("cputemp", "CPU running hot", $"{cpuT:0}° · the limit is 95°", Theme.Hot);
        if (_gpuHot >= 5 && Ready("gputemp", 180))
            return new Alert("gputemp", "GPU running hot", $"{gpuT:0}° · check fans and airflow", Theme.Hot);
        if (heatEta < 120 && Ready("heatsoon", 300))
            return new Alert("heatsoon", "CPU heating up", $"{cpuT:0}° now · 90° in about {Math.Max(1, heatEta / 60):0} min", Theme.Warn);
        if (vramEta < 90 && Ready("vramsoon", 300))
            return new Alert("vramsoon", "Video memory filling up", $"full in about {Math.Max(10, vramEta):0} s at this rate", Theme.Warn);
        if (st.Stutters10s >= 3 && st.Stutters10s <= 14 && Ready("stutter", 90))
        {
            // Name the most likely cause when there is one.
            string why = vramPct >= 90 ? " · video memory is nearly full"
                : m.HogPct >= 8 ? $" · {m.HogName} is busy in the background"
                : m.RamPct >= 90 ? " · system memory is nearly full"
                : "";
            return new Alert("stutter", "Stutter", $"{st.Stutters10s} frame spikes in 10 s{why}", Theme.Warn);
        }
        if (_drop >= 3 && Ready("drop", 120))
            return new Alert("drop", "FPS drop", $"{st.Current:0} now · {st.Average:0} average", Theme.Warn);
        if (vsUsual <= -12 && !_baselineAlerted && session!.Seconds >= 180)
        {
            _baselineAlerted = true;
            string why = Base!.MaxGpu > 0 && gpuT > Base.MaxGpu + 4 ? $" · GPU is {gpuT - Base.MaxGpu:0}° hotter than normal"
                : m.HogPct >= 5 ? $" · {m.HogName} is running in the background"
                : "";
            return new Alert("usual", $"{-vsUsual:0}% below {Base.Word}", $"{session.AvgFps:0} vs {Base.Avg:0} FPS{why}", Theme.Warn);
        }
        if (_hog >= 10 && Ready("hog", 300))
            return new Alert("hog", "Background load", $"{m.HogName} is using {m.HogPct:0}% of the CPU", Theme.Warn);
        if (_ramHigh >= 5 && Ready("ram", 300))
            return new Alert("ram", "Memory nearly full", $"{m.RamUsedGB:0.0} / {m.RamTotalGB:0.0} GB in use", Theme.Warn);
        return null;
    }

    bool Ready(string key, int cooldownSec)
    {
        long now = Environment.TickCount64;
        if (_lastFired.TryGetValue(key, out long last) && now - last < cooldownSec * 1000L) return false;
        _lastFired[key] = now;
        return true;
    }

    void BuildInsights(in FrameStats st, Sensors m, Hist? session, float heatEta, float vramEta, float vsUsual)
    {
        Insights.Clear();
        float gpu = _gpuEma;

        if (st.Phase is { Length: > 0 } phase)
        {
            Insights.Add(phase switch
            {
                "cutscene" => $"Cutscene locked at {st.PhaseFps:0} FPS. It is left out of your stats and alerts.",
                "loading" => "Loading screen. Statistics are paused until gameplay resumes.",
                _ => "Menu or pause screen. Statistics are paused until gameplay resumes.",
            });
            if (st.PhaseSec >= 60 && phase == "loading")
                Insights.Add($"This load has taken {st.PhaseSec:0} s. A slow drive or full memory can cause long loads.");
            return;
        }

        if (_halved >= 8)
            Insights.Add($"V-Sync is holding the game at {_hz / 2} FPS, half your {_hz} Hz refresh. FreeSync, triple buffering or a frame cap avoids the halving.");
        if (st.SpikePeriodMs > 0)
            Insights.Add($"Stutters repeat every {st.SpikePeriodMs / 1000:0.0} s, like clockwork. Monitoring, RGB or launcher software polling the hardware is the usual cause.");

        if (m.CpuThrottle == "thermal")
            Insights.Add($"CPU is throttling from heat ({m.CpuTemp:0}°, {m.CpuClockMHz / 1000:0.0} GHz). A better cooler or airflow gives FPS back.");
        else if (m.CpuThrottle == "power")
            Insights.Add($"CPU clocks are dropping under load ({m.CpuClockMHz / 1000:0.0} GHz) without heat. Look at the power limits.");
        if (m.GpuThrottle == "thermal")
            Insights.Add($"GPU is throttling from heat ({m.GpuTemp:0}°, {m.GpuClockMHz:0} MHz). Check its fans and case airflow.");
        else if (m.GpuThrottle == "power")
            Insights.Add($"GPU clocks fell to {m.GpuClockMHz:0} MHz at full load. A power or driver limit is holding it back.");

        if (m.VramPct >= 92)
            Insights.Add($"Video memory is full ({m.VramUsedMB / 1024:0.0}/{m.VramTotalMB / 1024:0.0} GB). Lower texture quality to stop hitching.");
        else if (vramEta < 180)
            Insights.Add($"Video memory is filling up and will be full in about {Math.Max(10, vramEta):0} s at this rate.");
        if (m.CpuTemp >= 88)
            Insights.Add($"CPU at {m.CpuTemp:0}° is close to its 95° limit. Check the cooler and case airflow.");
        else if (heatEta < 180)
            Insights.Add($"CPU is heating up: about {Math.Max(1, heatEta / 60):0} min to 90° at this rate.");
        if (m.GpuTemp >= 84)
            Insights.Add($"GPU at {m.GpuTemp:0}° is running hot. Check its fans and case airflow.");

        if (!float.IsNaN(vsUsual) && Math.Abs(vsUsual) >= 8 && Base != null)
            Insights.Add($"Running {Math.Abs(vsUsual):0}% {(vsUsual < 0 ? "below" : "above")} {Base.Word} in this game ({session!.AvgFps:0} vs {Base.Avg:0} FPS).");

        if (Tag == GpuBound)
            Insights.Add($"The graphics card is the limit ({gpu:0}% load). Resolution scale and shadows win back the most FPS.");
        else if (Tag == CpuBound)
            Insights.Add($"The GPU has headroom ({gpu:0}% load). You can raise graphics settings without losing FPS.");
        else if (Tag == Capped)
            Insights.Add($"Frame rate is capped near {st.Current:0} FPS with {Math.Max(0, 100 - gpu):0}% GPU headroom to spare.");

        if (session != null && session.Stutters >= 5)
        {
            string cause = TopCause;
            Insights.Add(cause.Length > 0
                ? $"{session.Stutters} stutters this session, mostly during {cause}."
                : $"{session.Stutters} stutters this session with no clear outside cause, so likely the game itself.");
        }
        if (Tag != Capped && st.Low1 > 0 && st.Average > 0 && st.Low1 < st.Average * 0.55f && st.P5Fps >= 35)
        {
            int cap = (int)(st.P5Fps / 5) * 5;
            Insights.Add($"Uneven pacing: 1% low is {st.Low1 / st.Average * 100:0}% of average. A {cap} FPS cap would feel smoother.");
        }
        if (m.HogPct >= 5 && m.HogName.Length > 0)
            Insights.Add($"{m.HogName} is using {m.HogPct:0}% of the CPU in the background.");

        if (Insights.Count == 0) Insights.Add("Running clean. No issues detected.");
        if (Insights.Count > 4) Insights.RemoveRange(4, Insights.Count - 4);
    }

    /// <summary>One sentence that sums a finished session up.</summary>
    public static string Debrief(SessionRecord r, Baseline? usual)
    {
        string quality = r.Smoothness >= 80 ? "Smooth" : r.Smoothness >= 55 ? "Mostly smooth" : "Rough";
        string text = $"{quality} {(r.Kind == "benchmark" ? "run" : "session")} at {r.Avg:0} FPS";
        if (usual != null && usual.Avg > 0 && r.Kind == "session")
        {
            float d = (r.Avg - usual.Avg) / usual.Avg * 100f;
            text += Math.Abs(d) < 3 ? $", in line with {usual.Word}" : $", {Math.Abs(d):0}% {(d > 0 ? "above" : "below")} {usual.Word}";
        }
        if (r.Stutters > 0)
            text += $", {r.Stutters} stutter{(r.Stutters == 1 ? "" : "s")}" +
                    (r.StutterCause.Length > 0 ? $" mostly during {r.StutterCause}" : "");
        if (r.Limit == "GPU") text += ". Limited by the graphics card";
        else if (r.Limit == "CPU") text += ". Limited by the CPU or the game engine";
        if (r.ThrottleSeconds >= 10)
            text += $". Throttled for {(r.ThrottleSeconds >= 120 ? r.ThrottleSeconds / 60 + " min" : r.ThrottleSeconds + " s")} ({r.ThrottleWhat})";
        if (r.AvgPing >= 80 || r.PingLoss >= 2)
            text += $". Connection was laggy ({r.AvgPing:0} ms, {r.PingLoss:0}% lost)";
        if (r.Freezes > 0) text += $". Froze {r.Freezes} time{(r.Freezes == 1 ? "" : "s")}";
        var left = new List<string>();
        if (r.Loads > 0) left.Add($"{r.Loads} loading screen{(r.Loads == 1 ? "" : "s")}");
        if (r.Cutscenes > 0) left.Add($"{r.Cutscenes} cutscene{(r.Cutscenes == 1 ? "" : "s")}");
        if (r.MenuSeconds >= 30) left.Add("menus");
        if (left.Count > 0) text += $". {string.Join(", ", left)} left out of the numbers";
        return text + ".";
    }
}

public sealed record Check(bool Ok, string Text);

/// <summary>
/// Explains a finished session in plain language: what went well, what went
/// wrong, and what most likely caused it. Level: 0 good, 1 worth knowing, 2 cost you frames.
/// </summary>
static class SessionReport
{
    public static List<(string text, int level)> Explain(SessionRecord r)
    {
        var why = new List<(string, int)>();

        // Compared with earlier sessions of the same game.
        var before = History.All().Where(x => x.Kind == "session" && x.Seconds >= 120 && x.Avg > 0 && x.Start < r.Start
                                              && string.Equals(x.Exe, r.Exe, StringComparison.OrdinalIgnoreCase))
                                  .TakeLast(8).Select(x => x.Avg).OrderBy(x => x).ToList();
        if (before.Count > 0 && r.Avg > 0)
        {
            float usual = before[before.Count / 2], d = (r.Avg - usual) / usual * 100f;
            if (d <= -8) why.Add(($"FPS was {-d:0}% below your usual {usual:0} in this game.", 2));
            else if (d >= 8) why.Add(($"FPS was {d:0}% above your usual {usual:0} in this game.", 0));
            else why.Add(($"FPS was in line with your usual {usual:0} in this game.", 0));
        }

        if (r.ThrottleSeconds >= 10)
        {
            string len = r.ThrottleSeconds >= 120 ? $"{r.ThrottleSeconds / 60} min" : $"{r.ThrottleSeconds} s";
            string fix = r.ThrottleWhat.StartsWith("CPU") ? "Clean the CPU cooler, check its mounting and case airflow."
                : r.ThrottleWhat.Contains("power") ? "Check the GPU power limit in the driver software."
                : "Clean the GPU fans and give the case more airflow.";
            why.Add(($"Hardware throttled for {len} ({r.ThrottleWhat}), lowering clocks and FPS. {fix}", 2));
        }

        if (r.MaxCpuTemp >= 90) why.Add(($"CPU peaked at {r.MaxCpuTemp:0}°, at its limit.", 2));
        else if (r.MaxCpuTemp >= 82) why.Add(($"CPU peaked at {r.MaxCpuTemp:0}°, warm but within limits.", 1));
        if (r.MaxGpuTemp >= 85) why.Add(($"GPU peaked at {r.MaxGpuTemp:0}°, hot enough to slow down.", 2));
        else if (r.MaxGpuTemp >= 78) why.Add(($"GPU peaked at {r.MaxGpuTemp:0}°, warm but within limits.", 1));

        if (r.Stutters > 0)
        {
            float perMin = r.Seconds > 0 ? r.Stutters / (r.Seconds / 60f) : 0;
            int lvl = perMin >= 1 ? 2 : perMin >= 0.2f ? 1 : 0;
            why.Add((r.StutterCause.Length > 0
                ? $"{r.Stutters} stutters, mostly during {r.StutterCause}."
                : $"{r.Stutters} stutters with no outside cause found, so most likely the game itself (shader compiling or streaming).", lvl));
        }
        else why.Add(("No stutters.", 0));

        if (r.Avg > 0 && r.Low1 > 0)
        {
            float ratio = r.Low1 / r.Avg;
            why.Add(ratio < 0.5f
                ? ($"Uneven frame pacing: the 1% low ({r.Low1:0}) is only {ratio * 100:0}% of the average. A frame cap near {Math.Max(30, (int)(r.Low1 * 1.2f / 5) * 5)} would feel smoother.", 1)
                : ($"Even frame pacing: the 1% low is {ratio * 100:0}% of the average.", 0));
        }

        if (r.Limit == "GPU") why.Add(("The graphics card set the frame rate most of the time. Lower resolution scale or shadows for more FPS.", 0));
        else if (r.Limit == "CPU") why.Add(("The CPU or the game engine set the frame rate, with GPU power to spare. Graphics settings can go up for free.", 0));
        else if (r.Limit == "Cap") why.Add(("The frame rate sat at a cap (V-Sync or a limiter) most of the time.", 0));

        if (r.AvgPing > 0)
        {
            int lvl = r.PingLoss >= 2 || r.AvgPing >= 100 ? 2 : r.AvgPing >= 60 || r.MaxPing >= 200 ? 1 : 0;
            why.Add(($"Connection: {r.AvgPing:0} ms average, {r.MaxPing:0} ms worst, {r.PingLoss:0.#}% of pings lost.", lvl));
        }

        if (r.Freezes > 0)
            why.Add(($"The game froze {r.Freezes} time{(r.Freezes == 1 ? "" : "s")} (over a second without a frame). See the timeline for what was going on.", 2));
        if (r.Events.Any(e => e.Contains("V-Sync is halving")))
            why.Add(("V-Sync halved the frame rate at times. FreeSync or a frame cap just under the refresh rate avoids that.", 1));
        if (r.Events.Any(e => e.Contains("Stutter on a timer")))
            why.Add(("Stutters came on a regular timer, a sign of background software polling the hardware rather than the game.", 1));

        var left = new List<string>();
        static string Len(int s) => s >= 120 ? $"{s / 60} min" : $"{s} s";
        if (r.Loads > 0) left.Add($"{r.Loads} loading screen{(r.Loads == 1 ? "" : "s")} ({Len(r.LoadingSeconds)})");
        if (r.Cutscenes > 0) left.Add($"{r.Cutscenes} cutscene{(r.Cutscenes == 1 ? "" : "s")} ({Len(r.CutsceneSeconds)})");
        if (r.MenuSeconds >= 10) left.Add($"menus ({Len(r.MenuSeconds)})");
        if (left.Count > 0)
            why.Add(($"Left out of the numbers so they show real gameplay only: {string.Join(", ", left)}.", 0));
        if (r.Loads > 0 && r.LoadingSeconds / r.Loads >= 40)
            why.Add(($"Loading screens averaged {r.LoadingSeconds / r.Loads} s. Installing the game on the NVMe drive shortens them.", 1));

        int winSerious = r.Events.Count(e => e.Contains("Windows:") && (e.Contains("Hardware error") || e.Contains("Blue screen")
                                              || e.Contains("Disk error") || e.Contains("Graphics driver crashed")));
        if (winSerious > 0) why.Add(($"Windows logged {winSerious} serious error{(winSerious == 1 ? "" : "s")} during the session; see the timeline.", 2));
        if (r.Events.Any(e => e.Contains("Windows:") && e.Contains(r.Game + " crashed", StringComparison.OrdinalIgnoreCase)
                              || e.Contains("Windows:") && e.Contains(r.Exe + " crashed", StringComparison.OrdinalIgnoreCase)))
            why.Add(("The game crashed at the end of this session.", 2));

        return why.OrderByDescending(x => x.Item2).ToList();
    }
}

/// <summary>
/// Pre-flight: a quick look over the system the moment a game starts, so
/// problems show up before they cost frames.
/// </summary>
static class Preflight
{
    static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { "Idle", "System", "Memory Compression", "Registry", "NotchHUD", "MsMpEng", "dwm", "explorer", "vmmem" };

    /// <summary>Dedicated video memory the game itself holds, from the Windows GPU counters.</summary>
    static float GameVramMB(int pid)
    {
        var category = new PerformanceCounterCategory("GPU Process Memory");
        string key = $"pid_{pid}_";
        double bytes = 0;
        foreach (string name in category.GetInstanceNames())
        {
            if (!name.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", name, true);
            bytes += counter.RawValue;
        }
        return (float)(bytes / 1048576.0);
    }

    public static List<Check> Run(Sensors m, int gamePid, string exe, int optimized = -1)
    {
        var list = new List<Check>();
        if (optimized > 0)
            list.Add(new Check(true, $"Optimizer engaged: {optimized} background processes in efficiency mode"));
        else if (optimized == 0)
            list.Add(new Check(true, "Optimizer engaged: nothing in the background needed calming"));

        // Video memory held by everything except the game.
        float idleVramMB = float.NaN;
        try { idleVramMB = Math.Max(0, m.VramUsedMB - GameVramMB(gamePid)); }
        catch (Exception ex) { Log.Write("pre-flight: video memory counter unavailable: " + ex.Message); }

        // Display refresh rate against what the panel can do at this resolution.
        try
        {
            var (hz, maxHz) = DisplayRefresh();
            if (hz > 0)
                list.Add(maxHz > hz + 1
                    ? new Check(false, $"Display is running at {hz} Hz but supports {maxHz} Hz")
                    : new Check(true, $"Display at {hz} Hz"));
        }
        catch { }

        float free = m.RamTotalGB - m.RamUsedGB;
        if (m.RamTotalGB > 0)
            list.Add(free / m.RamTotalGB < 0.25f
                ? new Check(false, $"Only {free:0.0} GB of system memory is free")
                : new Check(true, $"{free:0} GB of system memory free"));

        if (!float.IsNaN(idleVramMB) && m.VramTotalMB > 0)
            list.Add(idleVramMB / m.VramTotalMB >= 0.25f
                ? new Check(false, $"Other apps already hold {idleVramMB / 1024:0.0} GB of video memory")
                : new Check(true, "Video memory is clear"));

        // Heaviest background program by memory, all of its processes together.
        try
        {
            var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != gamePid && !Ignored.Contains(p.ProcessName))
                        byName[p.ProcessName] = byName.GetValueOrDefault(p.ProcessName) + p.WorkingSet64;
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (byName.Count > 0)
            {
                var top = byName.MaxBy(kv => kv.Value);
                double gb = top.Value / 1073741824.0;
                list.Add(gb >= 2
                    ? new Check(false, $"{top.Key} is holding {gb:0.0} GB of memory in the background")
                    : new Check(true, "Background apps are light"));
            }
        }
        catch { }

        float cpu = m.CpuTemp, gpu = m.GpuTemp;
        if (!float.IsNaN(cpu))
            list.Add(cpu >= 80
                ? new Check(false, $"CPU is already at {cpu:0}° before the game has warmed up")
                : new Check(true, float.IsNaN(gpu) ? $"Temperatures normal (CPU {cpu:0}°)" : $"Temperatures normal (CPU {cpu:0}°, GPU {gpu:0}°)"));

        if (History.Drift(exe) is { } drift) list.Add(drift);
        return list;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string? device, int mode, IntPtr devMode);

    /// <summary>Current and highest refresh rate of the primary display at its current resolution.</summary>
    public static (int hz, int maxHz) DisplayRefresh()
    {
        const int Size = 220, OffSize = 68, OffWidth = 172, OffHeight = 176, OffHz = 184;
        IntPtr mem = System.Runtime.InteropServices.Marshal.AllocHGlobal(Size);
        try
        {
            void Clear()
            {
                for (int i = 0; i < Size; i += 4) System.Runtime.InteropServices.Marshal.WriteInt32(mem, i, 0);
                System.Runtime.InteropServices.Marshal.WriteInt16(mem, OffSize, Size);
            }
            int Read(int off) => System.Runtime.InteropServices.Marshal.ReadInt32(mem, off);

            Clear();
            if (!EnumDisplaySettingsW(null, -1, mem)) return (0, 0);
            int w = Read(OffWidth), h = Read(OffHeight), hz = Read(OffHz), max = hz;
            for (int i = 0; i < 2000; i++)
            {
                Clear();
                if (!EnumDisplaySettingsW(null, i, mem)) break;
                if (Read(OffWidth) == w && Read(OffHeight) == h) max = Math.Max(max, Read(OffHz));
            }
            return (hz, max);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(mem); }
    }
}

/// <summary>Optional spoken alerts through the Windows speech engine.</summary>
static class Voice
{
    static dynamic? _voice;
    static bool _failed;

    public static void Say(string text)
    {
        if (_failed) return;
        try
        {
            if (_voice == null)
            {
                Type? t = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (t == null) { _failed = true; return; }
                _voice = Activator.CreateInstance(t);
            }
            _voice!.Speak(text.Replace("·", ","), 1 | 2); // asynchronous, cancel anything still speaking
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Write("Voice failed: " + ex.Message);
        }
    }
}
