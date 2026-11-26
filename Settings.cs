using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace NotchHUD;

/// <summary>What the Settings window needs from the notch.</summary>
interface ISettingsHost
{
    Config Config { get; }
    string ProfileInUse { get; }
    bool ProfileByGame { get; }
    (string Key, string Name)? CurrentGame { get; }
    void Changed();
    void ShowLayoutChange();
    void UseProfile(string name, bool byGame = false, bool announce = false);
    void AddProfile(Profile p, bool use);
    string UniqueProfileName(string name);
    bool RenameProfile(string from, string to);
    bool DeleteProfile(string name);
    void SetGameProfile(string key, string game, string? profile);
}

/// <summary>
/// Settings, in pages: Appearance, Layout, Profiles, Behaviour, Alerts and Advanced.
/// Every change applies at once. Look and layout are saved into the profile in use.
/// </summary>
sealed class SettingsForm : Form
{
    static readonly string[] Pages = { "Appearance", "Layout", "Profiles", "Behaviour", "Alerts", "Advanced" };
    static readonly Color Side = Color.FromArgb(18, 18, 21);
    static readonly Color Hover = Color.FromArgb(34, 34, 39);

    readonly ISettingsHost _host;
    readonly Panel _nav = new() { Dock = DockStyle.Left, Width = 176, BackColor = Side };
    readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 0, 20) };
    readonly List<Button> _navButtons = new();
    string _page = "Appearance";
    int _y;
    Surface _surface = Surface.Game;
    string _selectedProfile = "";

    Config C => _host.Config;
    static Color AccentOf(Config c) => Theme.Get(c.Accent).Light ? Theme.Get(c.Accent).Accent2 : Theme.Get(c.Accent).Accent;

    public SettingsForm(ISettingsHost host)
    {
        _host = host;
        Dark.Apply(this, "NotchHUD settings", new Size(780, 700));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        Controls.Add(_content);
        Controls.Add(_nav);
        BuildNav();
        ShowPage(_page);
    }

    /// <summary>Called when the profile in use changes from outside (tray, hotkey, a game starting).</summary>
    public void Reload()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke((Action)Reload); return; }
        BuildNav();
        ShowPage(_page);
    }

    // ------------------------------------------------------------------ frame

    void BuildNav()
    {
        _nav.Controls.Clear();
        _navButtons.Clear();
        _nav.Controls.Add(new Label
        {
            Text = "NotchHUD", Left = 20, Top = 20, AutoSize = true, ForeColor = Dark.Text,
            Font = new Font("Segoe UI Semibold", 13f),
        });
        _nav.Controls.Add(new Label
        {
            Text = "v" + Updater.CurrentText, Left = 22, Top = 48, AutoSize = true, ForeColor = Dark.Dim,
            Font = new Font("Segoe UI", 8.5f),
        });
        int y = 86;
        foreach (string page in Pages)
        {
            var b = new Button
            {
                Text = "   " + page, Left = 0, Top = y, Width = _nav.Width, Height = 38, FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = Dark.Text, BackColor = Side, Tag = page,
                Font = new Font("Segoe UI", 10f), Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Hover;
            b.Click += (_, _) => ShowPage(page);
            b.Paint += (_, e) =>
            {
                if ((string)b.Tag! != _page) return;
                using var bar = new SolidBrush(AccentOf(C));
                e.Graphics.FillRectangle(bar, 0, 8, 3, b.Height - 16);
            };
            _nav.Controls.Add(b);
            _navButtons.Add(b);
            y += 40;
        }

        // Which profile the edits go into.
        var footer = new Label
        {
            Left = 16, Top = 620, Width = _nav.Width - 24, Height = 64, ForeColor = Dark.Dim, Font = new Font("Segoe UI", 8.5f),
            Text = "Editing profile\n" + _host.ProfileInUse + (_host.ProfileByGame && _host.CurrentGame is { } g ? $"\n(for {g.Name})" : ""),
        };
        _nav.Controls.Add(footer);
    }

    public void Open(string page) => ShowPage(page);

    void ShowPage(string page)
    {
        _page = page;
        foreach (var b in _navButtons)
        {
            bool on = (string)b.Tag! == page;
            b.BackColor = on ? Hover : Side;
            b.Font = new Font(on ? "Segoe UI Semibold" : "Segoe UI", 10f);
            b.Invalidate();
        }
        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList()) c.Dispose();
        _content.Controls.Clear();
        _content.AutoScrollPosition = Point.Empty;
        _y = 22;
        Title(page);
        switch (page)
        {
            case "Appearance": AppearancePage(); break;
            case "Layout": LayoutPage(); break;
            case "Profiles": ProfilesPage(); break;
            case "Behaviour": BehaviourPage(); break;
            case "Alerts": AlertsPage(); break;
            default: AdvancedPage(); break;
        }
        _content.Controls.Add(new Label { Left = 0, Top = _y + 10, Width = 1, Height = 1 }); // bottom padding when scrolling
        _content.ResumeLayout();
    }

    void Changed() => _host.Changed();

    // ------------------------------------------------------------------ pages

    void AppearancePage()
    {
        ProfileNote();
        Header("THEME");
        var swatch = new Panel { Left = 28, Width = 520, Height = 46 };
        var about = new Label { Left = 28, Width = 520, Height = 36, ForeColor = Dark.Dim, Font = new Font("Segoe UI", 9f) };
        Combo("Theme", Theme.AccentNames, C.Accent, v =>
        {
            C.Accent = v;
            about.Text = Theme.Get(v).About;
            swatch.Invalidate();
            BuildNavAccent();
        });
        swatch.Top = _y;
        swatch.Paint += (_, e) => PaintSwatch(e.Graphics, swatch.ClientRectangle, Theme.Get(C.Accent));
        _content.Controls.Add(swatch);
        _y += 52;
        about.Top = _y;
        about.Text = Theme.Get(C.Accent).About;
        _content.Controls.Add(about);
        _y += 40;

        Header("PLACEMENT");
        Combo("Position", new[] { "Left", "Center", "Right" }, C.Position, v => C.Position = v);
        Slider("Nudge left / right", -600, 600, C.OffsetX, v => C.OffsetX = v, v => v == 0 ? "none" : $"{v:+0;-0} px");
        var screens = Screen.AllScreens;
        var names = new List<string> { "Primary screen" };
        names.AddRange(screens.Select((x, i) => $"Screen {i + 1}  ({x.Bounds.Width}×{x.Bounds.Height}){(x.Primary ? " · primary" : "")}"));
        int sel = Array.FindIndex(screens, x => x.DeviceName == C.Monitor);
        Combo("Monitor", names.ToArray(), sel >= 0 ? names[sel + 1] : names[0], v =>
        {
            int i = names.IndexOf(v);
            C.Monitor = i <= 0 ? "" : screens[i - 1].DeviceName;
        }, note: "for every profile");
        Check("Wide detail bar instead of the notch  (Ctrl+Alt+F8)", C.Wide, v => C.Wide = v);

        Header("SIZE AND GLASS");
        Slider("Size", 70, 180, (int)Math.Round(C.Scale * 100), v => C.Scale = v / 100.0, v => $"{v}%");
        Slider("Background opacity", 40, 100, C.Opacity, v => C.Opacity = v, v => $"{v}%");
    }

    void BuildNavAccent()
    {
        foreach (var b in _navButtons) b.Invalidate();
    }

    void LayoutPage()
    {
        ProfileNote();
        Note("Choose what the notch shows and in what order. Tick to show, use the arrows to move. " +
             "Separators are added between groups by themselves.");
        _y += 4;

        Surface[] surfaces = Enum.GetValues<Surface>();
        Combo("Edit", surfaces.Select(Modules.SurfaceName).ToArray(), Modules.SurfaceName(_surface), v =>
        {
            _surface = surfaces.First(x => Modules.SurfaceName(x) == v);
            ShowPage("Layout");
        }, apply: false);

        var list = new CheckedListBox
        {
            Left = 28, Top = _y, Width = 360, Height = 330, BackColor = Dark.Field, ForeColor = Dark.Text,
            BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false, Font = new Font("Segoe UI", 10f),
        };
        var slots = C.Layout.Get(_surface);
        void Fill(int select)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var slot in slots) list.Items.Add(Modules.Find(_surface, slot.Id)?.Name ?? slot.Id, slot.On);
            if (select >= 0 && select < list.Items.Count) list.SelectedIndex = select;
            list.EndUpdate();
        }
        bool filling = false;
        list.ItemCheck += (_, e) =>
        {
            if (filling) return;
            slots[e.Index].On = e.NewValue == CheckState.Checked;
            BeginInvoke((Action)Changed);
        };
        filling = true; Fill(0); filling = false;
        _content.Controls.Add(list);

        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= slots.Count) return;
            (slots[i], slots[j]) = (slots[j], slots[i]);
            filling = true; Fill(j); filling = false;
            Changed();
        }
        void All(bool on)
        {
            foreach (var slot in slots) slot.On = on;
            filling = true; Fill(list.SelectedIndex); filling = false;
            Changed();
        }

        int bx = 404, by = _y;
        Btn("▲  Move up", bx, by, 150, () => Move(-1));
        Btn("▼  Move down", bx, by + 40, 150, () => Move(1));
        Btn("Move to top", bx, by + 80, 150, () => { int i = list.SelectedIndex; if (i > 0) Move(-i); });
        Btn("Move to bottom", bx, by + 120, 150, () => { int i = list.SelectedIndex; if (i >= 0) Move(slots.Count - 1 - i); });
        Btn("Show all", bx, by + 176, 150, () => All(true));
        Btn("Hide all", bx, by + 216, 150, () => All(false));
        Btn("Reset to default", bx, by + 272, 150, () =>
        {
            slots = Modules.Default(_surface);
            C.Layout.Set(_surface, slots);
            filling = true; Fill(0); filling = false;
            Changed();
        });
        _y += 342;
        Note(_surface switch
        {
            Surface.Game => "Shows while a game is in front.",
            Surface.Idle => "Shows on the desktop when no game is in front.",
            _ => "Shows in the wide detail bar (Ctrl+Alt+F8). The middle keeps the clock and alerts.",
        });
    }

    void ProfilesPage()
    {
        Note("A profile keeps a theme, placement, size and a full layout. Switch with the tray menu or Ctrl+Alt+F7. " +
             "Give a game its own profile and NotchHUD switches to it while that game runs.");
        Header("PROFILES");

        var list = new ListBox
        {
            Left = 28, Top = _y, Width = 360, Height = 200, BackColor = Dark.Field, ForeColor = Dark.Text,
            BorderStyle = BorderStyle.None, IntegralHeight = false, Font = new Font("Segoe UI", 10f),
        };
        string Describe(Profile p) => p.Name
            + (string.Equals(p.Name, C.ActiveProfile, StringComparison.OrdinalIgnoreCase) ? "   ★ default" : "")
            + (string.Equals(p.Name, _host.ProfileInUse, StringComparison.OrdinalIgnoreCase) ? "   ● in use" : "")
            + $"   ·  {p.Accent}";
        void Fill()
        {
            list.Items.Clear();
            foreach (var p in C.Profiles) list.Items.Add(Describe(p));
            int i = C.Profiles.FindIndex(p => string.Equals(p.Name, _selectedProfile, StringComparison.OrdinalIgnoreCase));
            if (i < 0) i = C.Profiles.FindIndex(p => string.Equals(p.Name, _host.ProfileInUse, StringComparison.OrdinalIgnoreCase));
            list.SelectedIndex = Math.Max(0, i);
        }
        Profile? Selected() => list.SelectedIndex >= 0 && list.SelectedIndex < C.Profiles.Count ? C.Profiles[list.SelectedIndex] : null;
        list.SelectedIndexChanged += (_, _) => { if (Selected() is { } p) _selectedProfile = p.Name; };
        list.DoubleClick += (_, _) => { if (Selected() is { } p) _host.UseProfile(p.Name, announce: true); };
        Fill();
        _content.Controls.Add(list);

        int bx = 404, by = _y;
        Btn("Use  (make default)", bx, by, 160, () => { if (Selected() is { } p) _host.UseProfile(p.Name, announce: true); });
        Btn("New from current", bx, by + 40, 160, () =>
        {
            if (Prompt.Ask(this, "New profile", "Name for the new profile:", _host.UniqueProfileName("My profile")) is { } name)
            {
                _selectedProfile = _host.UniqueProfileName(name);
                _host.AddProfile(Profile.From(C, name), use: true);
                ShowPage("Profiles");
            }
        });
        Btn("Duplicate", bx, by + 80, 160, () =>
        {
            if (Selected() is not { } p) return;
            string name = _host.UniqueProfileName(p.Name + " copy");
            _selectedProfile = name;
            _host.AddProfile(p.Copy(name), use: false);
            ShowPage("Profiles");
        });
        Btn("Rename…", bx, by + 120, 160, () =>
        {
            if (Selected() is not { } p) return;
            if (Prompt.Ask(this, "Rename profile", "New name:", p.Name) is { } name)
            {
                if (_host.RenameProfile(p.Name, name)) { _selectedProfile = name.Trim(); BuildNav(); ShowPage("Profiles"); }
                else MessageBox.Show(this, "That name is empty or already used.", "Rename profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        });
        Btn("Delete", bx, by + 160, 160, () =>
        {
            if (Selected() is not { } p) return;
            if (C.Profiles.Count < 2) { MessageBox.Show(this, "Keep at least one profile.", "Delete profile"); return; }
            if (MessageBox.Show(this, $"Delete the profile \"{p.Name}\"? Games set to it go back to the default.", "Delete profile",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _host.DeleteProfile(p.Name);
            BuildNav();
            ShowPage("Profiles");
        });
        _y += 208;
        Btn("Export…", 28, _y, 110, () => ExportProfile(Selected()));
        Btn("Import…", 146, _y, 110, ImportProfile);
        _y += 52;

        // ---- game profiles
        Header("GAME PROFILES");
        var games = new ListView
        {
            Left = 28, Top = _y, Width = 536, Height = 150, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Dark.Field, ForeColor = Dark.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5f),
        };
        games.Columns.Add("Game", 330);
        games.Columns.Add("Profile", 190);
        foreach (var g in C.GameProfiles)
            games.Items.Add(new ListViewItem(new[] { g.Game.Length > 0 ? g.Game : g.Key, g.Profile }) { Tag = g.Key });
        if (C.GameProfiles.Count == 0)
            games.Items.Add(new ListViewItem(new[] { "No games yet. Pick one below.", "" }) { ForeColor = Dark.Dim });
        _content.Controls.Add(games);
        _y += 160;

        // Games to choose from: the one running now, then everything in history.
        var choices = new List<(string key, string name)>();
        if (_host.CurrentGame is { } now) choices.Add((now.Key, now.Name));
        foreach (var r in History.All().Where(r => r.Kind == "session" && r.Exe.Length > 0).Reverse())
            if (!choices.Any(c => string.Equals(c.key, r.Exe, StringComparison.OrdinalIgnoreCase))) choices.Add((r.Exe, r.Game));
        string Show((string key, string name) c) => c.name + (_host.CurrentGame is { } n && n.Key == c.key ? "   (running now)" : "");

        var gameBox = new ComboBox
        {
            Left = 28, Top = _y, Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Dark.Field, ForeColor = Dark.Text,
        };
        DarkCombo(gameBox);
        foreach (var c in choices) gameBox.Items.Add(Show(c));
        if (gameBox.Items.Count > 0) gameBox.SelectedIndex = 0;
        else { gameBox.Items.Add("Play a game first"); gameBox.SelectedIndex = 0; gameBox.Enabled = false; }
        var profileBox = new ComboBox
        {
            Left = 336, Top = _y, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Dark.Field, ForeColor = Dark.Text,
        };
        DarkCombo(profileBox);
        foreach (var p in C.Profiles) profileBox.Items.Add(p.Name);
        profileBox.SelectedIndex = Math.Max(0, C.Profiles.FindIndex(p => p.Name == _selectedProfile));
        _content.Controls.Add(gameBox);
        _content.Controls.Add(profileBox);
        Btn("Assign", 484, _y - 1, 80, () =>
        {
            if (!gameBox.Enabled || gameBox.SelectedIndex < 0 || profileBox.SelectedItem is not string prof) return;
            var (key, name) = choices[gameBox.SelectedIndex];
            _host.SetGameProfile(key, name, prof);
            ShowPage("Profiles");
        });
        _y += 40;
        Btn("Remove selected game", 28, _y, 180, () =>
        {
            foreach (ListViewItem item in games.SelectedItems)
                if (item.Tag is string key) _host.SetGameProfile(key, "", null);
            ShowPage("Profiles");
        });
        _y += 44;
    }

    void BehaviourPage()
    {
        Header("IN GAMES");
        Check("Pre-flight scan when a game starts", C.Intro, v => C.Intro = v);
        Check("Session summary when a game closes", C.Summary, v => C.Summary = v);
        Check("Auto-optimizer: calm background apps while you play", C.Optimizer, v => C.Optimizer = v);
        Check("Always on: FPS for any app, not just games", C.AlwaysOn, v => C.AlwaysOn = v);
        Check("Show over OpenGL/Vulkan fullscreen (Minecraft)", C.FullscreenFix, v => C.FullscreenFix = v);
        Header("ON THE DESKTOP");
        Check("Follow the Spotify track that is playing", C.NowPlaying, v => C.NowPlaying = v);
        Note("Which readings show on the notch is set per profile on the Layout page.");
        Header("ANIMATION");
        Check("Animations (boot sequence, counting numbers, transitions)", C.Animate, v => C.Animate = v);
        Check("Theme motion (aurora, shimmer, scanlines, stars)", C.Motion, v => C.Motion = v);
    }

    void AlertsPage()
    {
        Header("WHAT TO ALERT ON");
        Check("Smart alerts (heat, stutter, memory, FPS drops)", C.Alerts, v => C.Alerts = v);
        Check("Hardware health: throttling and failing parts", C.HealthWatch, v => C.HealthWatch = v);
        Check("Windows errors: crashes, disk, driver, hardware faults", C.EventWatch, v => C.EventWatch = v);
        Check("Network lag alerts (ping and packet loss)", C.NetWatch, v => C.NetWatch = v);
        Check("Read alerts out loud", C.Voice, v => C.Voice = v);
        Slider("How long alerts stay", 50, 300, C.AlertTime, v => C.AlertTime = v, v => v == 100 ? "normal" : $"{v / 100.0:0.##}×");

        Header("COLOUR THRESHOLDS");
        Note("Readings turn amber at the first number and red at the second.");
        Pair("CPU temperature (°C)", 40, 110, C.CpuWarn, C.CpuHot, (a, b) => { C.CpuWarn = a; C.CpuHot = b; });
        Pair("GPU temperature (°C)", 40, 110, C.GpuWarn, C.GpuHot, (a, b) => { C.GpuWarn = a; C.GpuHot = b; });
        Pair("Ping (ms)", 10, 1000, C.PingWarn, C.PingHot, (a, b) => { C.PingWarn = a; C.PingHot = b; });
    }

    void AdvancedPage()
    {
        Header("PERFORMANCE");
        Slider("Theme motion frame rate", 5, 60, C.MotionFps, v => C.MotionFps = v, v => $"{v} fps");
        Note("Living themes redraw at this rate once the notch has settled. Lower uses less CPU.");

        Header("SYSTEM");
        Check("Check GitHub for updates", C.CheckUpdates, v => C.CheckUpdates = v);
        var auto = new CheckBox { Text = "Start with Windows", Left = 28, Top = _y, Width = 500, Checked = Autostart.IsEnabled() };
        auto.CheckedChanged += (_, _) => Autostart.Set(auto.Checked);
        _content.Controls.Add(auto);
        _y += 32;

        Header("HOTKEYS");
        foreach (var (keys, what) in new[]
                 {
                     ("Ctrl+Alt+F7", "Next profile"), ("Ctrl+Alt+F8", "Wide detail bar"), ("Ctrl+Alt+F9", "Show / hide the notch"),
                     ("Ctrl+Alt+F10", "Start / stop a benchmark"), ("Ctrl+Alt+F11", "Details panel"),
                 })
        {
            _content.Controls.Add(new Label { Text = keys, Left = 28, Top = _y, Width = 120, ForeColor = Dark.Text, Font = new Font("Segoe UI Semibold", 9.5f) });
            _content.Controls.Add(new Label { Text = what, Left = 150, Top = _y, Width = 360, ForeColor = Dark.Dim });
            _y += 24;
        }

        Header("DATA");
        Btn("Open data folder", 28, _y, 160, () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", Log.Folder) { UseShellExecute = true }); } catch { }
        });
        Btn("Reset this profile's layout", 196, _y, 210, () =>
        {
            if (MessageBox.Show(this, $"Put every layout of \"{_host.ProfileInUse}\" back to the default?", "Reset layout",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            C.Layout = new LayoutSet();
            Changed();
        });
        _y += 44;
    }

    // ------------------------------------------------------------- profiles io

    void ExportProfile(Profile? p)
    {
        if (p == null) return;
        using var dlg = new SaveFileDialog
        {
            Title = "Export profile", Filter = "NotchHUD profile (*.json)|*.json", FileName = $"NotchHUD {p.Name}.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (string.Equals(p.Name, _host.ProfileInUse, StringComparison.OrdinalIgnoreCase)) p.CaptureFrom(C);
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not save: " + ex.Message, "Export profile"); }
    }

    void ImportProfile()
    {
        using var dlg = new OpenFileDialog { Title = "Import profile", Filter = "NotchHUD profile (*.json)|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(dlg.FileName)) ?? throw new InvalidDataException("empty file");
            p.Layout = (p.Layout ?? new LayoutSet()).Normalized();
            p.Accent = Theme.Get(p.Accent).Name;
            p.Position = p.Position is "Left" or "Right" ? p.Position : "Center";
            p.Scale = Math.Clamp(p.Scale, 0.7, 1.8);
            p.Opacity = Math.Clamp(p.Opacity, 40, 100);
            p.OffsetX = Math.Clamp(p.OffsetX, -600, 600);
            string name = _host.UniqueProfileName(string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileNameWithoutExtension(dlg.FileName) : p.Name);
            p.Name = name;
            _selectedProfile = name;
            _host.AddProfile(p, use: false);
            ShowPage("Profiles");
        }
        catch (Exception ex) { MessageBox.Show(this, "That file is not a NotchHUD profile.\n\n" + ex.Message, "Import profile"); }
    }

    // ------------------------------------------------------------- controls

    void Title(string text)
    {
        _content.Controls.Add(new Label
        {
            Text = text, Left = 26, Top = _y, AutoSize = true, ForeColor = Dark.Text, Font = new Font("Segoe UI Semibold", 15f),
        });
        _y += 46;
    }

    void ProfileNote()
    {
        string text = _host.ProfileByGame && _host.CurrentGame is { } g
            ? $"Changes here go into the profile \"{_host.ProfileInUse}\", which {g.Name} is using right now."
            : $"Changes here go into the profile \"{_host.ProfileInUse}\".";
        Note(text);
    }

    void Header(string text)
    {
        _y += 6;
        _content.Controls.Add(new Label
        {
            Text = text, Left = 28, Top = _y, Width = 520, ForeColor = AccentOf(C), Font = new Font("Segoe UI Semibold", 8.5f),
        });
        _y += 26;
    }

    void Note(string text)
    {
        var l = new Label { Text = text, Left = 28, Top = _y, Width = 536, ForeColor = Dark.Dim, Font = new Font("Segoe UI", 9f) };
        using (var g = CreateGraphics())
            l.Height = (int)Math.Ceiling(g.MeasureString(text, l.Font, l.Width).Height) + 4;
        _content.Controls.Add(l);
        _y += l.Height + 6;
    }

    void Combo(string label, string[] items, string value, Action<string> set, bool apply = true, string? note = null)
    {
        _content.Controls.Add(new Label { Text = label, Left = 28, Top = _y + 4, Width = 170 });
        var box = new ComboBox
        {
            Left = 210, Top = _y, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat, BackColor = Dark.Field, ForeColor = Dark.Text, MaxDropDownItems = 16,
        };
        DarkCombo(box);
        box.Items.AddRange(items);
        box.SelectedItem = items.Contains(value) ? value : items[0];
        box.SelectedIndexChanged += (_, _) => { set((string)box.SelectedItem!); if (apply) Changed(); };
        _content.Controls.Add(box);
        if (note != null) _content.Controls.Add(new Label { Text = note, Left = 480, Top = _y + 4, Width = 100, ForeColor = Dark.Dim, Font = new Font("Segoe UI", 8.5f) });
        _y += 38;
    }

    void Slider(string label, int min, int max, int value, Action<int> set, Func<int, string> show)
    {
        _content.Controls.Add(new Label { Text = label, Left = 28, Top = _y + 4, Width = 170 });
        var shown = new Label { Left = 480, Top = _y + 4, Width = 90, ForeColor = Dark.Dim, Text = show(Math.Clamp(value, min, max)) };
        var bar = new TrackBar
        {
            Left = 202, Top = _y - 2, Width = 276, Minimum = min, Maximum = max, TickStyle = TickStyle.None,
            Value = Math.Clamp(value, min, max), AutoSize = false, Height = 30,
            SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10),
        };
        bar.ValueChanged += (_, _) => { set(bar.Value); shown.Text = show(bar.Value); Changed(); };
        _content.Controls.Add(bar);
        _content.Controls.Add(shown);
        _y += 40;
    }

    void Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox { Text = label, Left = 28, Top = _y, Width = 520, Checked = value };
        box.CheckedChanged += (_, _) => { set(box.Checked); Changed(); };
        _content.Controls.Add(box);
        _y += 30;
    }

    /// <summary>Two linked numbers: amber from, red from. The second is kept above the first.</summary>
    void Pair(string label, int min, int max, int warn, int hot, Action<int, int> set)
    {
        _content.Controls.Add(new Label { Text = label, Left = 28, Top = _y + 4, Width = 170 });
        NumericUpDown Num(int left, int value) => new()
        {
            Left = left, Top = _y, Width = 70, Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max),
            BackColor = Dark.Field, ForeColor = Dark.Text, BorderStyle = BorderStyle.FixedSingle,
        };
        var a = Num(262, warn);
        var b = Num(392, hot);
        _content.Controls.Add(new Label { Text = "amber", Left = 210, Top = _y + 4, Width = 30, ForeColor = Theme.Get("Ice").Warn, AutoSize = true });
        _content.Controls.Add(new Label { Text = "red", Left = 360, Top = _y + 4, Width = 30, ForeColor = Theme.Get("Ice").Hot, AutoSize = true });
        void Update(object? sender, EventArgs e)
        {
            if (b.Value <= a.Value)
            {
                if (sender == a) b.Value = Math.Min(max, a.Value + 1);
                else a.Value = Math.Max(min, b.Value - 1);
            }
            set((int)a.Value, (int)b.Value);
            Changed();
        }
        a.ValueChanged += Update;
        b.ValueChanged += Update;
        _content.Controls.Add(a);
        _content.Controls.Add(b);
        _y += 36;
    }

    /// <summary>Draws a drop-down list in the dark palette (Windows paints it white otherwise).</summary>
    static void DarkCombo(ComboBox box)
    {
        box.DrawMode = DrawMode.OwnerDrawFixed;
        box.ItemHeight = 20;
        box.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using var back = new SolidBrush(hot ? Color.FromArgb(64, 64, 72) : Dark.Field);
            e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(e.Graphics, box.Items[e.Index]?.ToString(), box.Font,
                new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 4, e.Bounds.Height),
                box.Enabled ? Dark.Text : Dark.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
    }

    Button Btn(string text, int x, int y, int w, Action click)
    {
        var b = new Button
        {
            Text = text, Left = x, Top = y, Width = w, Height = 32, FlatStyle = FlatStyle.Flat,
            BackColor = Dark.Field, ForeColor = Dark.Text, Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 78);
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(58, 58, 64);
        b.Click += (_, _) => click();
        _content.Controls.Add(b);
        return b;
    }

    /// <summary>A small picture of the theme: its body, edge and colours.</summary>
    static void PaintSwatch(Graphics g, Rectangle r, ThemeSpec t)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var body = new RectangleF(r.X + 1, r.Y + 1, 300, r.Height - 3);
        using (var path = RoundRect(body, t.Shape == Shape.Pill ? body.Height / 2 : t.Shape is Shape.Slab or Shape.Blade ? 3 : 12))
        {
            using (var b = new LinearGradientBrush(body, t.BodyTop, t.BodyBottom, LinearGradientMode.Vertical)) g.FillPath(b, path);
            if (t.Edge != Edge.None)
                using (var eb = new LinearGradientBrush(body, t.Accent, t.Accent2, LinearGradientMode.Horizontal))
                using (var pen = new Pen(eb, 1.5f))
                    g.DrawPath(pen, path);
        }
        using (var f = new Font(Theme.Font(t.NumBold, "Bahnschrift SemiBold"), 17f, GraphicsUnit.Pixel))
        using (var lf = new Font(Theme.Font(t.NumRegular, "Bahnschrift"), 11f, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(t.Ink))
        using (var label = new SolidBrush(t.Label))
        using (var good = new SolidBrush(t.Good))
        {
            g.DrawString("144", f, ink, body.X + 16, body.Y + 11);
            g.DrawString("FPS", lf, label, body.X + 56, body.Y + 16);
            g.DrawString("CPU", lf, label, body.X + 104, body.Y + 16);
            g.DrawString("64°", f, good, body.X + 132, body.Y + 11);
        }
        using (var pen = new Pen(t.Accent, 2f))
        {
            var pts = new[] { 0, 2, 1, 3, 1, 9, 1, 2, 4, 1, 2 }.Select((v, i) => new PointF(body.X + 186 + i * 9, body.Bottom - 12 - v * 2)).ToArray();
            g.DrawLines(pen, pts);
        }
        float x = body.Right + 16;
        foreach (var c in new[] { t.Accent, t.Accent2, t.Accent3, t.Good, t.Warn, t.Hot })
        {
            using var b = new SolidBrush(c);
            g.FillEllipse(b, x, r.Y + r.Height / 2f - 9, 18, 18);
            x += 26;
        }
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>A one-line text prompt.</summary>
static class Prompt
{
    public static string? Ask(IWin32Window owner, string title, string question, string value)
    {
        using var f = new Form();
        Dark.Apply(f, title, new Size(380, 140));
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = f.MinimizeBox = false;
        f.StartPosition = FormStartPosition.CenterParent;
        f.Controls.Add(new Label { Text = question, Left = 18, Top = 16, Width = 340 });
        var box = new TextBox { Left = 18, Top = 42, Width = 340, Text = value, BackColor = Dark.Field, ForeColor = Dark.Text, BorderStyle = BorderStyle.FixedSingle };
        var ok = new Button { Text = "OK", Left = 192, Top = 88, Width = 80, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", Left = 278, Top = 88, Width = 80, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
        f.Controls.AddRange(new Control[] { box, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        box.SelectAll();
        return f.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
