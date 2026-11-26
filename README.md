# NotchHUD

A notch-style overlay at the top of the screen that shows what your gaming PC is doing right now. It tells you how smooth the game really is, what is holding it back, whether the hardware is healthy, and when Windows logs something you should know about.

![Wide detail bar](docs/wide-bar.png)

It works with any game and doesn't inject anything into game processes. Frame timing comes from Windows' own presentation events (ETW), and sensor readings come from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).

---

## Contents

- [Views](#views)
- [Features](#features)
- [Alerts](#alerts)
- [Hotkeys and tray menu](#hotkeys-and-tray-menu)
- [Settings](#settings)
- [Scripting](#scripting)
- [Updates](#updates)
- [Install](#install)
- [Build from source](#build-from-source)
- [Data and privacy](#data-and-privacy)
- [Troubleshooting](#troubleshooting)

---

## Views

| View | When | What it shows |
|---|---|---|
| **Idle notch** | No game in front | Clock, CPU and GPU temperature, a throttling chip, the Spotify track |
| **Game notch** | A game is in front | FPS, average, 1% and 0.1% lows, frame-time graph, smoothness ring, CPU and GPU temperatures, ping, and what is limiting the game (GPU-bound, CPU-bound, cutscene, loading, menu) |
| **Wide detail bar** | Ctrl+Alt+F8 | Everything at once. Game stats are on the left and the clock or alerts in the middle. The right side has load bars for CPU, GPU, VRAM and RAM, plus temperatures, clocks, power and ping. It is only as wide as its content |
| **Details panel** | Ctrl+Alt+F11 | Large frame-time graph, smoothness dial, radar of six vital signs, load bars, session timeline, per-thread CPU load, and insights in plain language |
| **Pre-flight** | A game starts | Quick checklist: power plan, refresh rate, background programs using a lot of CPU, free memory, and your usual FPS in this game |
| **Session card** | A game closes | Duration, average, lows and a comparison with last time |

## Features

### FPS and frame pacing
- Live FPS with average, **1% low** and **0.1% low** over a rolling window.
- Frame-time graph: each spike is a stutter you felt.
- **Smoothness score** (0–100), based on how even the frame pacing is rather than how high the FPS is.
- **Bottleneck verdict**: GPU-bound, CPU-bound or capped. It is worked out from GPU load, per-thread CPU load and frame times.
- **Periodic stutter detection** finds hitches that repeat on a timer, a sign of background polling, and reports the interval.
- **V-Sync halving detection** warns when FPS is locked at exactly half the refresh rate.
- **Benchmark mode** (Ctrl+Alt+F10) records clean, repeatable runs and saves them to history.

### Always-on mode and Minecraft
- **Always on** (tray menu or Settings) measures FPS for any app that is drawing frames, not only games. Apps that are not games stay out of History and never trigger the optimizer or pre-flight check, and tracking stops as soon as you leave them.
- **Minecraft Java** is recognised from any launcher (official, Modrinth, CurseForge, Prism, MultiMC, ATLauncher, GDLauncher, TLauncher, Lunar, Feather), including OpenGL and Vulkan (VulkanMod) renderers. It is named from its window (for example *Minecraft 1.21.4*), shows the game's own icon, and keeps its own history instead of being lumped in with other Java programs. **Minecraft Bedrock** is recognised too.
- **Visible over Minecraft fullscreen.** Minecraft's Fullscreen (and other OpenGL or Vulkan games) gets true exclusive fullscreen from the graphics driver, which hides every overlay. NotchHUD makes such a window one pixel taller (the extra row is off-screen), so the game is drawn through the desktop and the notch stays on top. DirectX games are never changed. It can be turned off in Settings.

### Scene detection
Cutscenes, loading screens and menus are not counted as FPS drops:

| Scene | How it is recognised |
|---|---|
| **Cutscene** | FPS locks to a common cap (24, 25, 30, 48, 50 or 60) with very even pacing, well below gameplay FPS, while the GPU is not maxed out |
| **Loading** | FPS collapses with many long frames, or the game stops presenting frames for several seconds |
| **Menu** | FPS shoots far above gameplay FPS with low GPU load |
| **Freeze** | A short full stop followed straight away by normal gameplay |

Time spent in each scene is listed separately in the session report.

### Hardware health
- **Thermal and power throttling** for CPU and GPU. It is detected when clocks drop under load at a high temperature or at the power limit.
- Temperatures, clocks, power draw, GPU hot spot and fan speeds.
- **Drives**: NVMe and SATA temperature, life left and spare capacity, read from SMART.
- Battery wear on laptops.
- **Hardware health window**: every reading with a status and advice, plus the last 24 hours of important Windows events.

### Windows event watch
NotchHUD watches the System, Application and Microsoft Defender logs live and alerts on:
- WHEA hardware errors (CPU, memory, PCIe)
- Blue screens and unexpected shutdowns (Kernel-Power 41)
- GPU driver timeouts and resets (Display 4101, AMD and NVIDIA drivers, watchdog live kernel events)
- Disk, NTFS and storage controller errors
- CPU firmware power limiting
- Defender threat detections
- Crashes and hangs of the game you are playing

Repeated events are folded together, so you get one alert instead of twenty.

### Network
- Pings the internet (1.1.1.1) and your router every few seconds.
- Shows live ping in game. It alerts on lag or packet loss and says whether the **router or Wi-Fi** or the **internet** is at fault.

### What is happening now
A live window that lists the busiest programs by **CPU, GPU, memory and disk**. It also shows internet and router ping, network and disk throughput, the power plan and uptime. Click a column to sort by it.

### Session history and reports
- Every session longer than a minute is saved. Each record includes average FPS, lows, stutters, throttling, ping, time spent in each scene, and a timeline.
- **Session report**: the numbers, a ranked "what happened and why", and a timeline of events (throttling, freezes, alerts, Windows errors).
- Compare a session against your usual performance in the same game.

### Auto-optimizer
While a game is in front, NotchHUD raises its CPU priority. It also puts browsers, launchers and similar background programs into Windows **efficiency mode**. Nothing is closed or suspended, and everything is put back when the game loses focus or closes.

### Look
- **21 themes.** Each one sets its own shape, edge, surface, type and parts, not just a colour:

  | Theme | Look |
  |---|---|
  | Ice, Mint, Violet, Amber, Rose, White | Classic glass notch in one accent, with a soft top highlight |
  | Dracula, Nord | Popular editor palettes |
  | Synthwave | Neon tube edge, retro sun and a perspective grid rolling toward you |
  | Matrix, Phosphor | Green or amber CRT terminal: monospace, scanlines, dashed frame, block meters |
  | Cyberpunk | Hazard-yellow blade with chamfered corners, solid tags, tick gauges, carbon weave |
  | Tactical | Military HUD: olive grid, corner brackets, segmented meters |
  | Aurora | Floating glass capsule with northern lights drifting behind the numbers |
  | Holo | Iridescent edge that shifts colour as it travels around the notch |
  | Nebula | Deep purple with a twinkling star field |
  | Inferno | Molten orange with embers rising off the bottom edge |
  | Luxe | Black and gold: double gilt edge, serif titles, honeycomb |
  | Glass | Frosted, grainy capsule with a white rim |
  | Daylight | Light capsule with dark text, for bright desktops |
  | Stealth | No glow, no colour, square edges |

  Switch themes from **Settings** or the tray menu's **Theme** submenu.
- **Theme motion:** living themes (Aurora, Holo, Synthwave, Matrix, Phosphor, Nebula, Inferno) redraw about 20 times a second once the notch has settled, so they never compete with the game. Turn it off with *Theme motion* in Settings.
- **Layout:** position (left, centre or right), size and opacity can all be changed.
- **Animation:** spring animations and counting numbers, which can be turned off.

## Alerts

Alerts drop out of the notch with a coloured glow and go away on their own. They can also be read out loud (optional).

| Alert | Example |
|---|---|
| Heat | GPU running hot, near its limit |
| Throttling | CPU thermal throttling, clocks down 15% |
| Stutter | Stutter on a timer, every 2.0 s |
| FPS | V-Sync is halving your FPS |
| Memory | VRAM or RAM nearly full |
| Network | Wi-Fi or router trouble / Internet lag |
| Hardware | Drive life low, temperature high |
| Windows | GPU driver reset, disk error, game crashed |

## Hotkeys and tray menu

| Keys | Action |
|---|---|
| **Ctrl+Alt+F7** | Next profile |
| **Ctrl+Alt+F8** | Wide detail bar on/off |
| **Ctrl+Alt+F9** | Show / hide the notch |
| **Ctrl+Alt+F10** | Start / stop a benchmark |
| **Ctrl+Alt+F11** | Details panel |

From the tray icon menu you can also open:
- *What is happening now*
- *Hardware health*
- *Last session report*
- *Settings*
- *History* (double-click a session there to open its report)
- *Check for updates* (changes to *Install update* when a new version is out)

## Settings

Double-click the tray icon to open **Settings**. Every change applies at once. It has six pages:

| Page | What is on it |
|---|---|
| **Appearance** | Theme (with a preview), position, a left/right nudge, monitor, wide bar, size and opacity |
| **Layout** | What the notch shows and in what order, for each place it appears (see below) |
| **Profiles** | Saved looks, and which game uses which profile |
| **Behaviour** | Pre-flight scan, session summary, auto-optimizer, always-on, Minecraft fullscreen fix, Spotify, animations, theme motion |
| **Alerts** | Which alerts to show, voice, how long alerts stay, and the temperature and ping levels where readings turn amber and red |
| **Advanced** | Theme motion frame rate, update checks, start with Windows, hotkeys, data folder, reset layout |

### Layout

Pick a place and tick the readings you want, then use the arrows to put them in order. Separators are added between groups for you.

| Place | Readings you can show |
|---|---|
| **Game notch** | Game icon, frame-time graph, FPS, average, 1% and 0.1% lows, frame time, smoothness ring, stutters, CPU and GPU temperature, CPU, GPU, VRAM and RAM meters, ping, clock, session time, status chip |
| **Desktop notch** | Clock, date, CPU and GPU temperature, CPU, GPU and RAM meters, ping, throttling chip, Spotify track |
| **Wide bar: game side** | Game icon and name, FPS, average, lows, frame time, graph, smoothness ring, stutters, status chip |
| **Wide bar: system side** | Throttling chip, CPU and GPU load with temperature, their clocks and power, VRAM, RAM, ping |

### Profiles

A profile keeps a theme, position, nudge, size, opacity, wide-bar choice and all four layouts. Settings edits go into the profile in use.

- Switch from the tray menu's **Profile** submenu, with **Ctrl+Alt+F7**, or by double-clicking one in Settings. The profile you pick becomes the default.
- **Game profiles:** give a game its own profile and NotchHUD switches to it while that game is running, then back to your default when it closes. Pick the game from the ones in your history, or the one running now.
- **Export** a profile to a `.json` file to back it up or share it, and **Import** one back.

Settings from earlier versions become a profile called *Default*.

## Scripting

Scripts and other programs can control NotchHUD through files in `%LOCALAPPDATA%\NotchHUD`.

**`command.txt`** holds one word. NotchHUD reads it and deletes it within a second.

| Command | Effect |
|---|---|
| `quit` | Exit NotchHUD |
| `wide` | Toggle the wide detail bar |
| `always` | Toggle always-on mode |
| `update` | Check for an update, or install it if one was found |
| `panel` | Toggle the details panel |
| `bench` | Start / stop a benchmark |
| `hide` | Show / hide the notch |
| `settings` | Open / close Settings |
| `history` | Open / close History |
| `nextprofile` | Switch to the next profile |
| `profile <name>` | Switch to a profile by name |

```powershell
Set-Content "$env:LOCALAPPDATA\NotchHUD\command.txt" wide
```

**`notify.txt`** shows your own message on the notch. Write one line per notice as `kind|title|detail`, where kind is `ok`, `warn`, `error` or `info`.

```powershell
Add-Content "$env:LOCALAPPDATA\NotchHUD\notify.txt" "ok|Render finished|scene.blend in 4:12"
```

## Updates

NotchHUD checks this repository's [latest release](../../releases/latest) 30 seconds after it starts and then every 6 hours. When a newer version is out, it tells you on the notch, but not while you are in a game.

To install the update, choose **Install update** in the tray menu. NotchHUD closes, puts the new version in place and starts again. It downloads the same kind of package you installed: an installed copy runs the new setup silently, the portable exe replaces itself, and the zip version copies the new files over its folder. Settings and history are kept. You can also check at any time with **Check for updates**, or turn automatic checks off in Settings.

## Install

Every [release](../../releases/latest) has three downloads:

| Download | Size | Needs .NET | Best for |
|---|---|---|---|
| **`NotchHUD-x.y.z-setup.exe`** | ~38 MB | No | Most people. Installs to Program Files with a Start menu entry, an optional desktop shortcut, the option to start at sign-in, the PawnIO sensor driver, and an uninstaller |
| **`NotchHUD-x.y.z-portable.exe`** | ~51 MB | No | One file you can keep anywhere, such as a USB stick. Nothing is installed |
| **`NotchHUD-x.y.z-win-x64.zip`** | ~4 MB | Yes, the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) | A small download if you already have .NET 10 |

Run it and accept the administrator prompt. Administrator rights are needed to read hardware sensors and Windows frame-timing events.

**CPU temperature needs the [PawnIO](https://pawnio.eu) driver.** The installer offers to install it, and the option is ticked by default when the driver is missing. With the portable or zip download, install it yourself with `winget install namazso.PawnIO` or from pawnio.eu.

All three keep their settings and history in `%LOCALAPPDATA%\NotchHUD`, so you can switch between them without losing anything. Uninstalling keeps that folder too.

## Build from source

Building needs the .NET 10 SDK:

```
git clone https://github.com/cagatay-softgineer/NotchHUD.git
cd NotchHUD
dotnet build -c Release
```

The program is built to `bin\Release\net10.0-windows\win-x64\NotchHUD.exe`.

To build all three release downloads into `dist\`, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) and run:

```
.\build-release.ps1
```

The version comes from `<Version>` in `NotchHUD.csproj`. Upload all three files to the release with their names unchanged, because the updater finds its download by the end of the file name.

| File | Contents |
|---|---|
| `NotchWindow.cs` | The overlay: layout, drawing, animation, tray, hotkeys |
| `FrameTracker.cs` | ETW present events, frame statistics, scene detection |
| `Sensors.cs` | Hardware sensors, throttling, health readings |
| `Health.cs` | Throttle detector and health alerts |
| `Events.cs` | Windows event log watch |
| `Live.cs` | Network probe, per-process GPU usage, the *What is happening now* window |
| `Smart.cs` | Advisor, insights, session records and reports |
| `Optimizer.cs` | Efficiency mode for background programs |
| `Settings.cs` | The Settings window |
| `Layout.cs` | Layout modules, profiles and game profiles |
| `Dialogs.cs` | History, Hardware health and Report windows |
| `Program.cs` | Entry point, config |
| `Themes.cs` | Theme definitions: palette, shape, edge, texture, fonts and part styles |
| `Preview.cs` | `NotchHUD.exe --preview <folder>` renders every theme to PNG (developer aid) |
| `Update.cs` | Update check and installer for all three install kinds |
| `installer\NotchHUD.iss` | Inno Setup script for the installer |
| `build-release.ps1` | Builds the setup, portable and zip downloads |

## Data and privacy

Everything stays on your PC. Settings, history and a log are kept in `%LOCALAPPDATA%\NotchHUD` (`config.json`, `history.json` and `log.txt`). The only network traffic is ping (ICMP) to 1.1.1.1 and to your router, and the update check, which reads this repository's latest release from the GitHub API (it can be turned off in Settings).

## Troubleshooting

- **No FPS shown**: run NotchHUD as administrator. Some games with anti-cheat protection block frame-timing events.
- **No CPU temperature** (shows "–" or 0): the PawnIO driver is missing. Run the installer again and keep *Install the PawnIO sensor driver* ticked, or run `winget install namazso.PawnIO`, then restart NotchHUD.
- **Missing temperatures**: some motherboards and laptops don't expose their sensors. NotchHUD shows "–" for those.
- **Wrong drive temperature**: NotchHUD uses the drive's *Composite* reading, the same one Samsung Magician and CrystalDiskInfo show. If it still differs, open an issue with your drive model.
- **Hotkeys do nothing**: another program may already be using them.

## License

[MIT](LICENSE). NotchHUD uses [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) (MIT).
