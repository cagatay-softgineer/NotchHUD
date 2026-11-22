namespace NotchHUD;

/// <summary>One line of the hardware health report. Level: 0 fine, 1 worth a look, 2 act now.</summary>
public sealed record HealthItem(string Key, string Group, string Name, string Value, int Level, string Advice);

/// <summary>
/// Spots a processor or graphics card that is slowing itself down to stay
/// within its heat or power limits: busy, clocks well below the best seen
/// under load, and (for heat) a temperature close to the limit. A verdict has
/// to hold for a few seconds, and lasts a few seconds after the cause is gone,
/// so one noisy reading never flips it.
/// </summary>
sealed class ThrottleDetector
{
    readonly float _hotTemp, _limitTemp, _dropRatio;
    readonly bool _detectPower;
    float _peak;
    int _on, _off;

    /// <summary>"" when fine, otherwise "thermal" or "power".</summary>
    public string Kind { get; private set; } = "";

    /// <summary>Seconds spent throttling since the detector was last reset.</summary>
    public int Seconds { get; private set; }

    /// <param name="hotTemp">Clock drops at or above this temperature count as heat.</param>
    /// <param name="limitTemp">At or above this under load the chip is at its limit whatever the clock does.</param>
    /// <param name="dropRatio">Clock below this share of the peak counts as a drop.</param>
    /// <param name="detectPower">Also report drops that happen without heat (power or driver limit).</param>
    public ThrottleDetector(float hotTemp, float limitTemp, float dropRatio, bool detectPower)
    {
        _hotTemp = hotTemp;
        _limitTemp = limitTemp;
        _dropRatio = dropRatio;
        _detectPower = detectPower;
    }

    public void Reset()
    {
        Kind = "";
        Seconds = 0;
        _peak = 0;
        _on = _off = 0;
    }

    public void Update(float load, float clockMHz, float temp)
    {
        bool busy = !float.IsNaN(load) && load >= 60;
        bool haveClock = !float.IsNaN(clockMHz) && clockMHz > 100;
        if (busy && haveClock && clockMHz > _peak) _peak = clockMHz;

        string now = "";
        if (busy)
        {
            bool atLimit = !float.IsNaN(temp) && temp >= _limitTemp;
            bool drop = haveClock && _peak > 0 && clockMHz < _peak * _dropRatio;
            if (atLimit || (drop && !float.IsNaN(temp) && temp >= _hotTemp)) now = "thermal";
            else if (_detectPower && drop && load >= 90) now = "power";
        }

        if (now.Length > 0)
        {
            _off = 0;
            if (++_on >= 3) Kind = now;
        }
        else
        {
            _on = 0;
            if (Kind.Length > 0 && ++_off >= 5) Kind = "";
        }
        if (Kind.Length > 0) Seconds++;
    }
}

/// <summary>
/// Turns the health items and throttle states into alerts, each repeated only
/// after a cooldown, so a problem is announced once and not nagged about.
/// </summary>
sealed class HealthWatch
{
    readonly Dictionary<string, long> _lastFired = new();
    long _startedAt = Environment.TickCount64;

    bool Ready(string key, int cooldownSec)
    {
        long now = Environment.TickCount64;
        if (_lastFired.TryGetValue(key, out long last) && now - last < cooldownSec * 1000L) return false;
        _lastFired[key] = now;
        return true;
    }

    /// <summary>Call once a second. Returns an alert when something deserves one.</summary>
    public Alert? Check(Sensors m)
    {
        // Sensors need a moment after start-up before the readings mean anything.
        if (Environment.TickCount64 - _startedAt < 30_000) return null;

        if (m.CpuThrottle.Length > 0 && Ready("cpu-throttle", 240))
            return m.CpuThrottle == "thermal"
                ? new Alert("cpu-throttle", "CPU is throttling", $"{m.CpuTemp:0}° · {m.CpuClockMHz / 1000:0.0} GHz · check the cooler and airflow", Theme.Hot)
                : new Alert("cpu-throttle", "CPU clocks dropping", $"{m.CpuClockMHz / 1000:0.0} GHz under load · power limit", Theme.Warn);

        if (m.GpuThrottle.Length > 0 && Ready("gpu-throttle", 240))
            return m.GpuThrottle == "thermal"
                ? new Alert("gpu-throttle", "GPU is throttling", $"{m.GpuTemp:0}° · {m.GpuClockMHz:0} MHz · check fans and airflow", Theme.Hot)
                : new Alert("gpu-throttle", "GPU clocks dropping", $"{m.GpuClockMHz:0} MHz under full load · power or driver limit", Theme.Warn);

        foreach (var item in m.Health)
        {
            if (item.Level >= 2 && Ready("health:" + item.Key, 1800))
                return new Alert("health", item.Name, $"{item.Value} · {item.Advice}", Theme.Hot);
        }
        return null;
    }
}
