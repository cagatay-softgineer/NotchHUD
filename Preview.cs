using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NotchHUD;

/// <summary>
/// Developer aid: <c>NotchHUD --preview &lt;folder&gt;</c> renders every theme in its
/// main views with sample readings and saves one PNG per theme plus a gallery.
/// Nothing is measured and no overlay is shown.
/// </summary>
sealed partial class NotchWindow
{
    Action<Bitmap>? _capture;

    public static void RenderPreviews(string dir)
    {
        Directory.CreateDirectory(dir);
        using var win = new NotchWindow(preview: true);
        win.PreparePreview();
        var gallery = new List<(string name, Bitmap shot)>();
        foreach (string name in Theme.AccentNames)
        {
            var shots = win.PreviewTheme(name);
            using var sheet = Stack(shots, name, Theme.Get(name));
            sheet.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
            gallery.Add((name, (Bitmap)shots[0].Clone()));
            foreach (var b in shots) b.Dispose();
        }
        using var all = Stack(gallery.Select(g => g.shot).ToList(), "All themes · game view", null, gallery.Select(g => g.name).ToList());
        all.Save(Path.Combine(dir, "_gallery.png"), ImageFormat.Png);
        foreach (var (_, b) in gallery) b.Dispose();
        win.PreviewLayouts(dir);
        win.PreviewSettings(dir);
    }

    /// <summary>Rows built by the real layout pipeline: default layouts, then a custom one.</summary>
    void PreviewLayouts(string dir)
    {
        _config.Accent = "Cyberpunk";
        _config.Layout = new LayoutSet();
        EnsureFonts();
        _session = new GameSession { Name = "Sample Game", Key = "sample", StartTick = Environment.TickCount64 - 754_000 };
        _inGame = true;
        _stats = new FrameStats { Current = 144, Average = 141, Low1 = 112, Low01 = 96, Stutters10s = 2 };
        _fpsShown = 144;
        var shots = new List<Bitmap>();
        var captions = new List<string>();
        void Row(Surface surface, string caption)
        {
            _row.Clear();
            AddModules(_row, surface, false);
            shots.Add(Shoot(View.Compact, new List<Item>(_row), S(32)));
            captions.Add(caption);
        }
        Row(Surface.Game, "game default");
        Row(Surface.WideSystem, "wide sys");

        // Custom: temperatures first, FPS after, load meters on, graph and lows off, clock at the end.
        var game = _config.Layout.Game;
        string[] order = { "cputemp", "gputemp", "fps", "frametime", "cpuload", "gpuload", "score", "status", "clock" };
        foreach (var slot in game) slot.On = order.Contains(slot.Id);
        game.Sort((a, b) => Order(a.Id).CompareTo(Order(b.Id)));
        int Order(string id) { int i = Array.IndexOf(order, id); return i < 0 ? 99 : i; }
        Row(Surface.Game, "game custom");

        _inGame = false;
        var idle = _config.Layout.Idle;
        foreach (var slot in idle) slot.On = slot.Id is "clock" or "date" or "cpuload" or "gpuload" or "ram" or "cputemp";
        Row(Surface.Idle, "idle custom");
        _session = null;

        using var sheet = Stack(shots, "Layouts through the module pipeline", null, captions);
        sheet.Save(Path.Combine(dir, "_layouts.png"), ImageFormat.Png);
        foreach (var b in shots) b.Dispose();
    }

    /// <summary>Screenshots of every Settings page, drawn off-screen.</summary>
    void PreviewSettings(string dir)
    {
        _config.Accent = "Aurora";
        _config.Layout = new LayoutSet();
        if (_config.Profiles.Count < 2) _config.Profiles.Add(Profile.From(_config, "Competitive"));
        using var form = new SettingsForm(this) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
        form.Show();
        foreach (string page in new[] { "Appearance", "Layout", "Profiles", "Behaviour", "Alerts", "Advanced" })
        {
            form.Open(page);
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(dir, "settings-" + page + ".png"), ImageFormat.Png);
        }
        form.Close();
    }

    void PreparePreview()
    {
        _config.Scale = 1.0;
        _config.Opacity = 94;
        _shown = true;
        var m = _sensors;
        m.CpuTemp = 64; m.GpuTemp = 71; m.GpuLoad = 97; m.CpuLoad = 38; m.CpuMaxThread = 82;
        m.VramUsedMB = 6200; m.VramTotalMB = 8192; m.RamUsedGB = 14.2f; m.RamTotalGB = 32;
        var rnd = new Random(3);
        m.ThreadLoads = Enumerable.Range(0, 16).Select(i => (float)(i == 3 ? 88 : 15 + rnd.NextDouble() * 55)).ToArray();
        for (int i = 0; i < _recent.Length; i++)
            _recent[i] = 6.9f + (float)(rnd.NextDouble() - 0.5) * 1.2f + (i % 97 == 40 ? 14f : 0) + (i % 211 == 7 ? 9f : 0);
        _recentN = _recent.Length;
        _recentMedian = 6.9f;
    }

    List<Bitmap> PreviewTheme(string name)
    {
        _config.Accent = name;
        EnsureFonts();
        var accent = Theme.Current.Accent;
        var shots = new List<Bitmap>();

        // Game notch.
        _inGame = false;
        _alert = null;
        var game = new List<Item>
        {
            new() { Kind = K.Spark, Color = accent },
            new() { Kind = K.BigPair, A = "FPS", B = "144", Color = Theme.Ink },
            new() { Kind = K.Pair, A = "AVG", B = "141", Color = Theme.Ink },
            new() { Kind = K.Pair, A = "1%", B = "112", Color = Theme.Ink },
            new() { Kind = K.Pair, A = "0.1%", B = "96", Color = Theme.Ink },
            new() { Kind = K.Ring, A = "87", V = 87, Color = ScoreColor(87) },
            new() { Kind = K.Sep },
            new() { Kind = K.Pair, A = "CPU", B = "64°", Color = TempColor(64, 75, 88) },
            new() { Kind = K.Pair, A = "GPU", B = "71°", Color = TempColor(71, 75, 85) },
            new() { Kind = K.Pair, A = "PING", B = "18", Color = Theme.Good },
            new() { Kind = K.Chip, A = "GPU-BOUND", Color = accent },
        };
        shots.Add(Shoot(View.Compact, game, S(32)));

        // Idle notch with music.
        shots.Add(Shoot(View.Idle, new List<Item>
        {
            new() { Kind = K.Title, A = "21:47", Color = Theme.Ink },
            new() { Kind = K.Sep },
            new() { Kind = K.Pair, A = "CPU", B = "48°", Color = TempColor(48, 75, 88) },
            new() { Kind = K.Pair, A = "GPU", B = "52°", Color = TempColor(52, 75, 85) },
            new() { Kind = K.Sep },
            new() { Kind = K.Dot, Color = accent },
            new() { Kind = K.Text, A = "Daft Punk — Contact", Color = Theme.Soft },
        }, S(28)));

        // Alert.
        _alert = new Alert("heat", "GPU running hot", "86° · near its limit · check the fans", Theme.Hot);
        shots.Add(Shoot(View.Alert, new List<Item>
        {
            new() { Kind = K.Dot, Color = Theme.Hot },
            new() { Kind = K.Title, A = _alert.Title, Color = Theme.Ink },
            new() { Kind = K.Text, A = _alert.Detail, Color = Theme.Soft },
        }, S(32)));
        _alert = null;

        // Wide-bar style row with meters and a throttle chip.
        shots.Add(Shoot(View.Compact, new List<Item>
        {
            new() { Kind = K.Meter, A = "CPU", B = "38%", V = 38, Color = accent },
            new() { Kind = K.Meter, A = "GPU", B = "97%", V = 97, Color = Theme.Hot },
            new() { Kind = K.Meter, A = "VRAM", B = "6.1 GB", V = 76, Color = accent },
            new() { Kind = K.Meter, A = "RAM", B = "14 GB", V = 44, Color = accent },
            new() { Kind = K.Sep },
            new() { Kind = K.Chip, A = "CPU THROTTLING", Color = Theme.Hot },
            new() { Kind = K.Chip, A = "ALL SYSTEMS GO", Color = Theme.Good },
        }, S(32)));

        // Details panel.
        _inGame = true;
        _row.Clear();
        _row.AddRange(game.Take(5));
        _view = View.Panel;
        _w = S(600);
        _h = PanelLayout(null, _w);
        shots.Add(Snapshot());
        _inGame = false;
        return shots;
    }

    Bitmap Shoot(View v, List<Item> row, float h)
    {
        _row.Clear();
        _row.AddRange(row);
        _view = v;
        _w = (float)Math.Ceiling(RowWidth(_row) + 2 * S(18) + 2 * S(Ear));
        _h = h;
        return Snapshot();
    }

    Bitmap Snapshot()
    {
        _fade = 1;
        Bitmap? shot = null;
        _capture = b => shot = (Bitmap)b.Clone();
        Render();
        _capture = null;
        return shot!;
    }

    /// <summary>Lays shots out one under another on a desktop-like backdrop.</summary>
    static Bitmap Stack(List<Bitmap> shots, string title, ThemeSpec? spec, List<string>? captions = null)
    {
        int pad = 24, gap = 18, capW = captions != null ? 110 : 0;
        int w = shots.Max(s => s.Width) + 2 * pad + capW, h = 56 + shots.Sum(s => s.Height + gap) + pad;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        bool light = spec?.Light == true;
        using (var back = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                   light ? Color.FromArgb(150, 170, 200) : Color.FromArgb(40, 46, 62),
                   light ? Color.FromArgb(205, 215, 230) : Color.FromArgb(14, 16, 22), 60f))
            g.FillRectangle(back, 0, 0, w, h);
        using var f = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
        using var fs = new Font("Segoe UI", 11.5f, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(light ? Color.FromArgb(20, 22, 28) : Color.FromArgb(230, 232, 240));
        using var dim = new SolidBrush(light ? Color.FromArgb(60, 64, 72) : Color.FromArgb(150, 155, 170));
        g.DrawString(title, f, ink, pad, 14);
        if (spec != null) g.DrawString(spec.About, fs, dim, pad, 33);
        int y = 56;
        for (int i = 0; i < shots.Count; i++)
        {
            var s = shots[i];
            if (captions != null) g.DrawString(captions[i], f, ink, pad, y + 10);
            g.DrawImage(s, pad + capW + (w - 2 * pad - capW - s.Width) / 2, y);
            y += s.Height + gap;
        }
        return bmp;
    }
}
