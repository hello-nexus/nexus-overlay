# nexus-overlay

Windows desktop host for the three browser-rendered surfaces that [Nexus](https://hellonexus.com) needs outside the system tray: the main dashboard, the floating per-monitor widget overlays, and the fullscreen HYTE Y70/Y80 touch panel. One process, one Chromium browser tree, shared GPU/network/utility processes - much cheaper than spawning `msedge --app` for each.

Native AOT, no Microsoft.Web.WebView2.Core dependency: the WebView2 host calls `WebView2Loader.dll` directly and walks the COM vtables by hand so the binary stays small and AOT-clean.

## Surfaces

| Window | Source URL (served by `nexus-service`) | What it is |
|---|---|---|
| `DashboardWindow` | `/` | The main Nexus dashboard. Replaces the old `msedge --app` spawn the tray used. Native Windows 11 Mica backdrop behind a transparent WebView2, themed to the in-app theme. Hidden, not destroyed, when the user clicks X so the next open is instant. |
| `OverlayWindow` (one per monitor) | `/overlay` | Transparent, click-through-where-empty floating widgets. `SetWindowRgn` carves the window down to the widget rectangles reported by the SPA so input outside them falls through to the desktop. |
| `PanelKioskWindow` | `/panel/:deviceId` | Fullscreen tool-window for the HYTE Y70/Y80 secondary touch display, plus one per monitor the user assigns a panel to (reconciled from the service's monitor-panel assignments). Topmost, sized to its monitor, hidden from the taskbar. |

All three surfaces are the same React app from [`nexus-web`](https://github.com/hello-nexus/nexus-web); the URL path picks which view loads.

## How it's driven

`nexus-overlay.exe` runs as a per-session singleton (named `Local\Nexus.Overlay.Singleton`). The tray in [`nexus-service`](https://github.com/hello-nexus/nexus-service) signals it by registered Win32 messages:

- `Nexus.Overlay.ShowDashboard` - show / focus the dashboard window.
- `Nexus.Overlay.ShowPanelKioskWindow` / `…HidePanelKiosk` - toggle the Y70/Y80 panel.
- `Nexus.Overlay.PrefsChanged` - re-read overlay preferences (enabled toggle, always-on-top, monitor selection) from the service.

When no surface needs to be visible (overlays disabled, no widgets pinned, dashboard hidden, panel hidden) the process self-terminates after a short grace window. The next "Open Nexus" click re-spawns it.

## Source layout

```
src/
  Program.cs              # entry, singleton mutex, prefs polling, idle exit
  DashboardWindow.cs      # / dashboard window
  OverlayWindow.cs        # /overlay per-monitor widget host (transparent, regioned)
  PanelKioskWindow.cs     # /panel/:deviceId fullscreen kiosk (Y70/Y80)
  PanelDisplay.cs         # monitor → panel target resolver
  DisplayIdentity.cs      # GDI device name → EDID-stable display id (assignment key)
  MonitorKioskManager.cs  # one kiosk per monitor-panel assignment, reconciled from the service
  MonitorKioskPlan.cs     # pure spawn/close reconcile math for the monitor kiosks
  PanelMonitorGuard.cs    # relocates foreign windows off the kiosk's monitor
  PanelGuardGeometry.cs   # pure placement math for evicted windows
  RegionLayout.cs         # SPA-reported widget rects → SetWindowRgn
  NexusApi.cs             # tiny REST/WS client to nexus-service for prefs + auth
  Logger.cs               # rolling file logger
  WebView2/               # hand-rolled WebView2 COM bindings (no MSWebView2.Core)
  Win32/                  # P/Invoke surface (windows, monitors, regions, messages)
```

## Build

```sh
dotnet publish -c Release -r win-x64 \
  /p:PublishAot=true /p:PublishSingleFile=true /p:SelfContained=true
```

The csproj is `<PublishAot>true</PublishAot>` and Windows-only (`net10.0-windows10.0.19041.0`). The published `nexus-overlay.exe` is dropped next to `nexus-service` and discovered by absolute path.

## Tests

```sh
dotnet test
```

Tests live under `tests/`. They cover the AOT-safe bits (region math, prefs polling, idle exit) - the WebView2 surfaces are exercised end-to-end via the service's integration harness.

## Why a separate process

`nexus-service` is `ASP.NET Core / AOT` and runs as a Windows service. The overlay needs interactive desktop access (HWNDs, foreground activation, per-monitor DPI, transparent windowing) - none of which a service can do cleanly. Splitting it out also lets the overlay process exit when idle so a paired-but-empty install has no overlay process running at all.
