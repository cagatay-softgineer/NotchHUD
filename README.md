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
- **Themes:** Ice, Mint, Violet, Amber, Rose, White, Synthwave, Matrix and Stealth.
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

## Settings

Double-click the tray icon to open **Settings**. Each of these can be turned on or off:

- Frame-time graph in the notch
- Smart alerts (heat, stutter, memory, FPS drops)
- Pre-flight scan when a game starts
- Session summary when a game closes
- Auto-optimizer
- Animations
- Voice alerts
- Spotify track
- Hardware health alerts
- Windows error alerts
- Network lag alerts
- Start with Windows

Settings also has the theme, position, size and opacity.

## Scripting

Scripts and other programs can control NotchHUD through files in `%LOCALAPPDATA%\NotchHUD`.

**`command.txt`** holds one word. NotchHUD reads it and deletes it within a second.

| Command | Effect |
|---|---|
| `quit` | Exit NotchHUD |
| `wide` | Toggle the wide detail bar |
| `panel` | Toggle the details panel |
| `bench` | Start / stop a benchmark |
| `hide` | Show / hide the notch |
| `settings` | Open / close Settings |
| `history` | Open / close History |

```powershell
Set-Content "$env:LOCALAPPDATA\NotchHUD\command.txt" wide
```

**`notify.txt`** shows your own message on the notch. Write one line per notice as `kind|title|detail`, where kind is `ok`, `warn`, `error` or `info`.

```powershell
Add-Content "$env:LOCALAPPDATA\NotchHUD\notify.txt" "ok|Render finished|scene.blend in 4:12"
```

## Install

1. Install the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download the zip from [Releases](../../releases) and unzip it anywhere.
3. Run `NotchHUD.exe` and accept the administrator prompt.

Administrator rights are needed to read hardware sensors and Windows frame-timing events.

## Build from source

Building needs the .NET 10 SDK:

```
git clone https://github.com/cagatay-softgineer/NotchHUD.git
cd NotchHUD
dotnet build -c Release
```

The program is built to `bin\Release\net10.0-windows\win-x64\NotchHUD.exe`.

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
| `Dialogs.cs` | Settings, History, Hardware health and Report windows |
| `Program.cs` | Entry point, config, themes |

## Data and privacy

Everything stays on your PC. Settings, history and a log are kept in `%LOCALAPPDATA%\NotchHUD` (`config.json`, `history.json` and `log.txt`). The only network traffic is ping (ICMP) to 1.1.1.1 and to your router.

## Troubleshooting

- **No FPS shown**: run NotchHUD as administrator. Some games with anti-cheat protection block frame-timing events.
- **Missing temperatures**: some motherboards and laptops don't expose their sensors. NotchHUD shows "–" for those.
- **Wrong drive temperature**: NotchHUD uses the drive's *Composite* reading, the same one Samsung Magician and CrystalDiskInfo show. If it still differs, open an issue with your drive model.
- **Hotkeys do nothing**: another program may already be using them.

## License

[MIT](LICENSE). NotchHUD uses [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and [Microsoft.Diagnostics.Tracing.TraceEvent](https://github.com/microsoft/perfview) (MIT).
