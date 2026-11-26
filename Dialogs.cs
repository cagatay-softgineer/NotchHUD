namespace NotchHUD;

static class Dark
{
    public static readonly Color Back = Color.FromArgb(24, 24, 27);
    public static readonly Color Field = Color.FromArgb(44, 44, 48);
    public static readonly Color Text = Color.FromArgb(236, 236, 240);
    public static readonly Color Dim = Color.FromArgb(150, 152, 160);

    public static void Apply(Form f, string title, Size size)
    {
        f.Text = title;
        f.ClientSize = size;
        f.BackColor = Back;
        f.ForeColor = Text;
        f.Font = new Font("Segoe UI", 9.5f);
        f.StartPosition = FormStartPosition.CenterScreen;
        f.ShowIcon = false;
        f.AutoScaleMode = AutoScaleMode.Dpi;
        f.TopMost = true; // stays visible over a borderless game while you adjust things
    }
}

/// <summary>Live hardware health: temperatures, clocks, throttling, drives, fans and battery.</summary>
sealed class HealthForm : Form
{
    readonly Sensors _sensors;
    readonly ListView _list;
    readonly Label _summary;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    readonly WinEvents _events;
    readonly ListView _eventList;

    public HealthForm(Sensors sensors, WinEvents events)
    {
        _sensors = sensors;
        _events = events;
        Dark.Apply(this, "NotchHUD hardware health", new Size(720, 640));

        _eventList = new ListView
        {
            Dock = DockStyle.Bottom, Height = 220, View = System.Windows.Forms.View.Details, FullRowSelect = true,
            BackColor = Dark.Back, ForeColor = Dark.Text, BorderStyle = BorderStyle.None,
        };
        _eventList.Columns.Add("Windows events, last 24 h", 220);
        _eventList.Columns.Add("When", 110);
        _eventList.Columns.Add("Detail", 360);
        Controls.Add(_eventList);

        _summary = new Label
        {
            Dock = DockStyle.Top, Height = 54, Padding = new Padding(14, 12, 0, 0),
            Font = new Font("Segoe UI Semibold", 11f),
        };
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true,
            BackColor = Dark.Back, ForeColor = Dark.Text, BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Part", 110);
        _list.Columns.Add("Reading", 200);
        _list.Columns.Add("Status", 360);
        Controls.Add(_list);
        Controls.Add(_summary);
        _list.BringToFront();

        Fill();
        _timer.Tick += (_, _) => Fill();
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    void Fill()
    {
        var items = _sensors.Health;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var i in items.OrderByDescending(x => x.Level))
        {
            var row = new ListViewItem(new[]
            {
                i.Group, $"{i.Name}: {i.Value}",
                i.Level == 0 ? "OK" : (i.Level == 1 ? "Worth a look: " : "Act now: ") + i.Advice,
            })
            {
                ForeColor = i.Level == 2 ? Theme.Hot : i.Level == 1 ? Theme.Warn : Dark.Text,
            };
            _list.Items.Add(row);
        }
        _list.EndUpdate();

        _eventList.BeginUpdate();
        _eventList.Items.Clear();
        foreach (var e in _events.Recent)
            _eventList.Items.Add(new ListViewItem(new[]
            {
                e.Title + (e.Count > 1 ? $" (×{e.Count})" : ""),
                e.Time.Date == DateTime.Today ? e.Time.ToString("HH:mm:ss") : e.Time.ToString("dd MMM HH:mm"),
                e.Detail,
            }) { ForeColor = e.Severity >= 2 ? Theme.Hot : Theme.Warn });
        if (_eventList.Items.Count == 0)
            _eventList.Items.Add(new ListViewItem(new[] { "No important errors", "", "" }) { ForeColor = Theme.Good });
        _eventList.EndUpdate();

        int bad = items.Count(x => x.Level == 2), warn = items.Count(x => x.Level == 1);
        if (items.Count == 0)
        {
            _summary.Text = "Reading sensors…";
            _summary.ForeColor = Dark.Dim;
        }
        else if (bad > 0)
        {
            _summary.Text = $"{bad} problem{(bad == 1 ? "" : "s")} need attention";
            _summary.ForeColor = Theme.Hot;
        }
        else if (warn > 0)
        {
            _summary.Text = $"{warn} thing{(warn == 1 ? "" : "s")} worth a look";
            _summary.ForeColor = Theme.Warn;
        }
        else
        {
            _summary.Text = "All hardware looks healthy, no throttling";
            _summary.ForeColor = Theme.Good;
        }
    }
}

/// <summary>One session explained: the numbers, why it went the way it did, and a timeline of what happened.</summary>
sealed class ReportForm : Form
{
    public ReportForm(SessionRecord r)
    {
        Dark.Apply(this, $"NotchHUD report: {r.Game}", new Size(760, 640));

        var box = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Dark.Back, ForeColor = Dark.Text, Font = new Font("Segoe UI", 10f),
            DetectUrls = false,
        };
        Controls.Add(box);

        void Line(string text, Color color, float size = 10f, bool bold = false)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionColor = color;
            box.SelectionFont = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size);
            box.AppendText(text + Environment.NewLine);
        }

        int s = r.Seconds;
        string len = s >= 3600 ? $"{s / 3600} h {s / 60 % 60} min" : $"{s / 60} min {s % 60} s";
        Line(r.Game, Dark.Text, 15f, true);
        Line($"{r.Start:dddd d MMMM yyyy, HH:mm} · {len} · {(r.Kind == "benchmark" ? "benchmark" : "play session")}", Dark.Dim);
        Line("", Dark.Text);

        Line("NUMBERS", Dark.Dim, 8.5f, true);
        Line($"{r.Avg:0} FPS average · {r.Low1:0} 1% low · {r.Low01:0} 0.1% low · smoothness {r.Smoothness}/100", Dark.Text);
        Line($"CPU up to {r.MaxCpuTemp:0}°" + (r.MaxCpuClockMHz > 0 ? $" at {r.MaxCpuClockMHz / 1000:0.00} GHz" : "") +
             $" · GPU up to {r.MaxGpuTemp:0}° · {r.AvgGpuLoad:0}% GPU load · {r.PeakVramMB / 1024:0.0} GB video memory at peak", Dark.Text);
        if (r.AvgPing > 0) Line($"Ping {r.AvgPing:0} ms average, {r.MaxPing:0} ms worst, {r.PingLoss:0.#}% lost", Dark.Text);
        Line("", Dark.Text);

        Line("WHAT HAPPENED AND WHY", Dark.Dim, 8.5f, true);
        foreach (var (text, level) in SessionReport.Explain(r))
            Line((level == 2 ? "▲ " : level == 1 ? "● " : "✓ ") + text, level == 2 ? Theme.Hot : level == 1 ? Theme.Warn : Theme.Good);
        Line("", Dark.Text);

        Line("TIMELINE", Dark.Dim, 8.5f, true);
        if (r.Events.Count == 0)
            Line("Nothing notable was recorded: no alerts, throttling or Windows errors. (Sessions from before this update have no timeline.)", Dark.Dim);
        foreach (string e in r.Events)
            Line(e, e.Contains("Windows:") || e.Contains("Throttling started") ? Theme.Warn : Theme.Soft);

        box.SelectionStart = 0;
    }
}

/// <summary>Every recorded session and benchmark, newest first.</summary>
sealed class HistoryForm : Form
{
    public HistoryForm()
    {
        Dark.Apply(this, "NotchHUD history (double-click a row for its report)", new Size(1320, 480));

        var list = new ListView
        {
            Dock = DockStyle.Fill, View = System.Windows.Forms.View.Details, FullRowSelect = true,
            BackColor = Dark.Back, ForeColor = Dark.Text, BorderStyle = BorderStyle.None,
        };
        (string, int, HorizontalAlignment)[] cols =
        {
            ("Date", 130, HorizontalAlignment.Left), ("Game", 220, HorizontalAlignment.Left),
            ("Type", 80, HorizontalAlignment.Left), ("Time", 65, HorizontalAlignment.Right),
            ("Avg", 50, HorizontalAlignment.Right), ("1% low", 60, HorizontalAlignment.Right),
            ("0.1% low", 65, HorizontalAlignment.Right), ("Stutters", 60, HorizontalAlignment.Right),
            ("CPU max", 65, HorizontalAlignment.Right), ("GPU max", 65, HorizontalAlignment.Right),
            ("Limit", 55, HorizontalAlignment.Left), ("Smooth", 60, HorizontalAlignment.Right),
            ("Throttled", 120, HorizontalAlignment.Left),
            ("Summary", 420, HorizontalAlignment.Left),
        };
        foreach (var (name, width, align) in cols) list.Columns.Add(name, width, align);

        foreach (var r in Enumerable.Reverse(History.All()))
        {
            int s = r.Seconds;
            list.Items.Add(new ListViewItem(new[]
            {
                r.Start.ToString("yyyy-MM-dd HH:mm"), r.Game, r.Kind == "benchmark" ? "Benchmark" : "Session",
                s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}",
                r.Avg.ToString("0"), r.Low1.ToString("0"), r.Low01.ToString("0"), r.Stutters.ToString(),
                r.MaxCpuTemp > 0 ? r.MaxCpuTemp.ToString("0") + "°" : "–",
                r.MaxGpuTemp > 0 ? r.MaxGpuTemp.ToString("0") + "°" : "–",
                r.Limit, r.Smoothness > 0 ? r.Smoothness.ToString() : "–",
                r.ThrottleSeconds >= 10 ? $"{r.ThrottleSeconds}s {r.ThrottleWhat}" : "–", r.Note,
            }) { Tag = r });
        }
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is SessionRecord rec) new ReportForm(rec).Show();
        };

        if (list.Items.Count == 0)
            Controls.Add(new Label
            {
                Text = "Nothing recorded yet. Sessions longer than a minute and every benchmark run appear here.",
                Dock = DockStyle.Top, Height = 44, ForeColor = Dark.Dim, Padding = new Padding(14, 14, 0, 0),
            });
        Controls.Add(list);
        list.BringToFront();
    }
}
