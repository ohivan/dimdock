# DimDock

Lightweight Windows 11 system tray utility for controlling external monitor brightness over hardware DDC/CI, designed to match Quick Settings.

## Features

- **Hardware DDC/CI control** — Adjusts your monitor's actual backlight rather than applying a software tint.
- **Native Windows 11 UI** — Acrylic flyout and tray menu matching your system theme and accent color.
- **Tray icon scrolling** — Hover the tray icon and scroll to change brightness in 1%, 2%, 5%, or 10% steps.
- **Multi-monitor support** — Control monitors independently or link them together, with configurable tray scroll targets.
- **Dynamic tray icon** — DPI-aware sun glyph that scales with current brightness.
- **Startup & updates** — Optional *Start with Windows* toggle and automatic updates via Velopack.

## Requirements

- Windows 11 (x64)
- External monitor(s) with **DDC/CI** enabled in the monitor's OSD settings

## Install

**WinGet**

```powershell
winget install ohivan.DimDock
```

**Manual**

Download the latest `DimDock-win-Setup.exe` from [GitHub Releases](https://github.com/ohivan/dimdock/releases/latest).

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/) (and [Bun](https://bun.sh/) for the website).

```powershell
# Build & test the Windows app
dotnet build DimDock.slnx
dotnet test DimDock.slnx

# Run locally
dotnet run --project src/DimDock

# Preview the website
cd web
bun run dev
```

### Repository Structure

- [`src/DimDock`](./src/DimDock) — Windows 11 .NET 10 WPF tray application
  - `Interop/` — Win32 P/Invoke declarations and struct layouts (`dxva2`, `dwmapi`, `user32`, `shell32`, `gdi32`)
  - `Services/` — DDC/CI monitor backend, coalescing brightness controller, tray icon/scroll hook, settings, theme, logging, and Velopack updater
  - `UI/` — Acrylic Quick Settings flyout, native-Hz `DwmFlush` slide animator, shared taskbar placement helper, and Fluent tray context menu
- [`tests/DimDock.Tests`](./tests/DimDock.Tests) — xUnit unit tests (controller coalescing/concurrency, Win32 struct layouts, placement geometry, easing curves, settings, and updater state machine)
- [`web`](./web) — Project website (see [`web/README.md`](./web/README.md))

## Troubleshooting

- **Monitor not detected**: Open your monitor's on-screen display (OSD) menu and verify **DDC/CI** is enabled. Some HDMI/DisplayPort docks or KVMs block I2C/DDC traffic.
- **Settings & logs**:
  - Settings: `%LocalAppData%\DimDock\settings.json`
  - Diagnostic log: `%LocalAppData%\DimDock\dimdock.log`

## License

[MIT](./LICENSE)
