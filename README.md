# NotchHUD

A notch-style overlay at the top of the screen for Windows gaming PCs.

- **FPS and frame pacing**: live FPS, average, 1% and 0.1% lows, frame-time graph, stutter detection and a smoothness score (ETW present events, works with any game).
- **Scene detection**: cutscenes, loading screens, menus and freezes are recognised and kept out of the performance numbers.
- **Hardware health**: CPU and GPU thermal and power throttling, temperatures, clocks, fans, drive temperature and wear, battery wear.
- **Windows events**: alerts for WHEA errors, blue screens, unexpected shutdowns, GPU driver resets, disk errors, Defender detections and game crashes.
- **Network**: ping to the internet and to the router, with lag alerts that tell which one is at fault.
- **Session reports**: per-session history, timeline and a ranked explanation of what happened.
- **Wide detail bar** (Ctrl+Alt+F8): a wider notch with every reading at once.

## Hotkeys

| Keys | Action |
|---|---|
| Ctrl+Alt+F8 | Wide detail bar |
| Ctrl+Alt+F9 | Show / hide |
| Ctrl+Alt+F10 | Start / stop benchmark |
| Ctrl+Alt+F11 | Details panel |

## Requirements

Windows 10/11 x64, [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). Runs as administrator (needed for hardware sensors and frame tracing).

## Build

```
dotnet build -c Release
```
