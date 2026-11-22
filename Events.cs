using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;

namespace NotchHUD;

/// <summary>One Windows event worth showing. Severity: 1 warning, 2 serious.</summary>
public sealed class WinEvent
{
    public DateTime Time { get; init; }
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public int Severity { get; init; }
    public string App { get; init; } = "";   // for crashes and hangs: the program's name without ".exe"
    public int Count { get; set; } = 1;      // identical events in quick succession are folded together
}

/// <summary>
/// Follows the Windows event logs and picks out the entries that matter:
/// hardware errors, unexpected shutdowns, blue screens, disk trouble, graphics
/// driver crashes, program crashes and malware detections. Everything else
/// in the logs is ignored.
/// </summary>
sealed class WinEvents : IDisposable
{
    const string SystemLog = "System", AppLog = "Application", DefenderLog = "Microsoft-Windows-Windows Defender/Operational";

    // Only these reach the classifier; the rest of the logs is far too chatty.
    const string SystemFilter = "(Level>=1 and Level<=3)";
    const string AppFilter = "((Level>=1 and Level<=3) or (Provider[@Name='Windows Error Reporting'] and EventID=1001))";
    const string DefenderFilter = "(EventID=1116 or EventID=1117)";

    readonly List<EventLogWatcher> _watchers = new();
    readonly List<WinEvent> _recent = new();
    readonly ConcurrentQueue<WinEvent> _pending = new();
    readonly object _gate = new();

    /// <summary>Newest first, at most 60 events from the last day.</summary>
    public IReadOnlyList<WinEvent> Recent
    {
        get { lock (_gate) return _recent.ToList(); }
    }

    public void Start()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            // The last day first, so the window has something to show. Serious
            // events from the last two hours also alert once: a crash that led
            // to this very start-up is exactly what the user wants to hear about.
            Backfill(SystemLog, SystemFilter);
            Backfill(AppLog, AppFilter);
            Backfill(DefenderLog, DefenderFilter);
            Watch(SystemLog, SystemFilter);
            Watch(AppLog, AppFilter);
            Watch(DefenderLog, DefenderFilter);
        });
    }

    void Backfill(string log, string filter)
    {
        try
        {
            string query = $"*[System[{filter} and TimeCreated[timediff(@SystemTime) <= 86400000]]]";
            using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query));
            for (EventRecord? e; (e = reader.ReadEvent()) != null;)
            {
                using (e)
                {
                    var ev = Classify(e);
                    if (ev == null) continue;
                    Add(ev, alert: ev.Severity >= 2 && ev.Time > DateTime.Now.AddHours(-2));
                }
            }
        }
        catch (Exception ex) { Log.Write($"Event log '{log}' backfill failed: {ex.Message}"); }
    }

    void Watch(string log, string filter)
    {
        try
        {
            var w = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, $"*[System[{filter}]]"));
            w.EventRecordWritten += (_, args) =>
            {
                try
                {
                    using var rec = args.EventRecord;
                    if (rec == null) return;
                    var ev = Classify(rec);
                    if (ev != null) Add(ev, alert: true);
                }
                catch (Exception ex) { Log.Write("Event handling failed: " + ex.Message); }
            };
            w.Enabled = true;
            _watchers.Add(w);
        }
        catch (Exception ex) { Log.Write($"Cannot watch event log '{log}': {ex.Message}"); }
    }

    void Add(WinEvent ev, bool alert)
    {
        bool fresh = true;
        lock (_gate)
        {
            // The same problem logged again within a minute is one problem.
            var same = _recent.FirstOrDefault(x => x.Key == ev.Key && Math.Abs((x.Time - ev.Time).TotalSeconds) < 60);
            if (same != null) { same.Count++; fresh = false; }
            else
            {
                _recent.Add(ev);
                _recent.Sort((a, b) => b.Time.CompareTo(a.Time));
                if (_recent.Count > 60) _recent.RemoveRange(60, _recent.Count - 60);
            }
        }
        if (!fresh) return;
        Log.Write($"event: [{ev.Severity}] {ev.Title}: {ev.Detail}");
        if (alert)
        {
            _pending.Enqueue(ev);
            while (_pending.Count > 20) _pending.TryDequeue(out _);
        }
    }

    /// <summary>
    /// The next event worth interrupting the user for, or null. Serious events
    /// always qualify; a crash or hang only when it was the game being played.
    /// </summary>
    public WinEvent? NextAlert(string? gameExe)
    {
        while (_pending.TryDequeue(out var ev))
        {
            bool theGame = ev.App.Length > 0 && gameExe is { Length: > 0 }
                           && string.Equals(ev.App, gameExe, StringComparison.OrdinalIgnoreCase);
            if (ev.Severity >= 2 || theGame) return ev;
        }
        return null;
    }

    // ------------------------------------------------ what counts as important

    static WinEvent? Classify(EventRecord e)
    {
        string provider = e.ProviderName ?? "";
        int id = e.Id;
        string title, advice = "";
        int sev = 2;
        string app = "";

        bool Is(string name) => provider.Equals(name, StringComparison.OrdinalIgnoreCase);

        if (Is("Microsoft-Windows-WHEA-Logger"))
        {
            title = "Hardware error (WHEA)";
            advice = "the CPU, memory or a PCIe device reported a fault";
        }
        else if (Is("Microsoft-Windows-Kernel-Power") && id == 41)
        {
            title = "Unexpected shutdown";
            advice = "the PC lost power or crashed; check temperatures and the power supply";
        }
        else if (Is("Microsoft-Windows-WER-SystemErrorReporting") && id == 1001)
        {
            title = "Blue screen";
            advice = "Windows crashed and restarted";
        }
        else if (Is("Display") && id == 4101)
        {
            title = "Graphics driver crashed";
            advice = "it stopped responding and recovered; update or roll back the driver";
        }
        else if (provider.Equals("amdkmdag", StringComparison.OrdinalIgnoreCase)
                 || provider.Equals("amdwddmg", StringComparison.OrdinalIgnoreCase)
                 || provider.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase))
        {
            title = "Graphics driver error";
            advice = "the display driver reported a problem";
            sev = 1;
        }
        else if (Is("Windows Error Reporting") && id == 1001)
        {
            // Information-level report of a hardware hang: LiveKernelEvent 141 is a graphics card that stopped answering.
            string text = Format(e);
            if (!text.Contains("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)) return null;
            // The report names its kind in the dump path: ...\LiveKernelReports\<Category>\...
            string category = "";
            int at = text.IndexOf("LiveKernelReports\\", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                string rest = text[(at + 18)..];
                int end = rest.IndexOfAny(new[] { '\\', '\r', '\n' });
                category = end > 0 ? rest[..end] : "";
                if (category.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)) category = category[..category.IndexOf('-')];
            }
            // Most of these (timeouts, minor resets) are routine; only a watchdog or display hang is serious.
            bool serious = category.Contains("WATCHDOG", StringComparison.OrdinalIgnoreCase)
                           || category.Contains("DISPLAY", StringComparison.OrdinalIgnoreCase);
            title = "Driver or device hang" + (category.Length > 0 ? $" ({category})" : "");
            advice = serious ? "a driver or device stopped responding; update the driver" : "Windows logged a minor driver timeout";
            sev = serious ? 2 : 1;
            provider += ":" + category;
        }
        else if (Is("disk") && id is 7 or 11 or 15 or 52 or 153)
        {
            title = "Disk error";
            advice = "a drive reported bad blocks or failed commands; back up and check its health";
        }
        else if (Is("disk") && id == 51)
        {
            title = "Disk error";
            advice = "a drive had a paging error";
            sev = 1;
        }
        else if (Is("Ntfs") && id == 55)
        {
            title = "File system corruption";
            advice = "run a disk check";
        }
        else if ((Is("stornvme") || Is("storahci") || Is("storport")) && id is 129 or 153)
        {
            title = "Drive reset";
            advice = "the storage controller had to reset a drive";
            sev = 1;
        }
        else if (Is("Microsoft-Windows-Kernel-Processor-Power") && id == 37)
        {
            title = "CPU speed limited";
            advice = "firmware is holding the CPU below its full speed";
            sev = 1;
        }
        else if (Is("Microsoft-Windows-Windows Defender") && id is 1116 or 1117)
        {
            title = id == 1116 ? "Malware detected" : "Malware action taken";
            advice = "open Windows Security to review it";
        }
        else if (Is("Application Error") && id == 1000)
        {
            app = StripExe(Prop(e, 0));
            title = $"{(app.Length > 0 ? app : "A program")} crashed";
            advice = "faulting module " + Prop(e, 3);
            sev = 1;
        }
        else if (Is("Application Hang") && id == 1002)
        {
            app = StripExe(Prop(e, 0));
            title = $"{(app.Length > 0 ? app : "A program")} stopped responding";
            sev = 1;
        }
        else return null;

        string detail = advice.Length > 0 ? advice : FirstLine(Format(e));
        string key = $"{provider}:{id}:{app}";
        return new WinEvent
        {
            Time = e.TimeCreated ?? DateTime.Now,
            Key = key,
            Title = title,
            Detail = Clip(detail, 140),
            Severity = sev,
            App = app,
        };
    }

    static string Prop(EventRecord e, int i)
    {
        try { return i < e.Properties.Count ? e.Properties[i].Value?.ToString() ?? "" : ""; }
        catch { return ""; }
    }

    static string Format(EventRecord e)
    {
        try { return e.FormatDescription() ?? ""; }
        catch { return ""; }
    }

    static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    static string FirstLine(string text)
    {
        int nl = text.IndexOfAny(new[] { '\r', '\n' });
        return (nl >= 0 ? text[..nl] : text).Trim();
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { w.Enabled = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }
}
