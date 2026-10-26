# nexus-overlay

Windows desktop host for the browser-rendered surfaces that [Nexus](https://hellonexus.com) needs outside the system tray: the main dashboard, per-display panels (including the HYTE Y70/Y80 touch panel), and streamed panels. One process, one Chromium browser tree, shared GPU/network/utility processes - much cheaper than spawning `msedge --app` for each.

Native AOT, no Microsoft.Web.WebView2.Core dependency: the WebView2 host calls `WebView2Loader.dll` directly and walks the COM vtables by hand so the binary stays small and AOT-clean.

## Surfaces

| Window | Source URL (served by `nexus-service`) | What it is |
|---|---|---|
| `DashboardWindow` | `/` | The main Nexus dashboard. Replaces the old `msedge --app` spawn the tray used. Native Windows 11 Mica backdrop behind a transparent WebView2, themed to the in-app theme. Hidden, not destroyed, when the user clicks X so the next open is instant. |
| `PanelKioskWindow` | `/panel/:deviceId` | Fullscreen tool-window for the HYTE Y70/Y80 secondary touch display, plus one per display the user assigns a panel to (reconciled from the service's display assignments), including the primary display. A see-through panel starts with an empty window region and the page's `nexus.panel.hitTest` reports widen it, so empty space clicks through to the desktop; `nexus.panel.raise` lifts a not-on-top panel above other windows. On the primary display the panel fills the work area and runs no window guard, mouse wall or taskbar hiding. Otherwise topmost or bottom of the stack, sized to its monitor, hidden from the taskbar. With the Y70 compatibility mode setting on, the Y70 kiosk runs in its own `PanelCompatWebView2` environment with DirectComposition disabled. The panel's Web View widget shows real sites in native WebView2 views layered over the kiosk (separate `WidgetWebView2` profile, or `PanelCompatWidgetWebView2` in compatibility mode), driven by `nexus.webview.*` messages from the page. |
| `StreamPanelHost` | `/panel/:deviceId` (off-screen) | Streamed-panel render host: an off-screen WebView2 captured via Windows.Graphics.Capture, hardware-encoded to H.264 (Media Foundation), and pushed to the service's `/panel/streams/{sessionId}/ingest`, which paces it onto a USB display device. Reconciled from `/panel/streams/assignments`; runs in its own `StreamWebView2` environment so its browser arguments never touch the other surfaces. |

All surfaces load the same React app from [`nexus-web`](https://github.com/hello-nexus/nexus-web); the URL path picks which view loads.

## How it's driven

`nexus-overlay.exe` runs as a per-session singleton (named `Local\Nexus.Overlay.Singleton`). The tray in [`nexus-service`](https://github.com/hello-nexus/nexus-service) signals it by registered Win32 messages:

- `Nexus.Overlay.ShowDashboard` - show / focus the dashboard window.
- `Nexus.Overlay.ShowPanelKiosk` / `…HidePanelKiosk` - toggle the Y70/Y80 panel.
- `Nexus.Overlay.PrefsChanged` - re-read the panel and display assignments from the service.

At boot, and on every display change, the host posts its EnumDisplayMonitors index to stable display id map to the service's `/overlay/monitors`.

When no surface needs to be visible (no panel assignments, dashboard hidden, panel hidden) the process self-terminates after a short grace window. The next "Open Nexus" click re-spawns it.

## Source layout

```
src/
  Program.cs              # entry, singleton mutex, state reconcile, idle exit
  DashboardWindow.cs      # / dashboard window
  PanelKioskWindow.cs     # /panel/:deviceId fullscreen kiosk (Y70/Y80)
  WebViewWidgetHost.cs    # kiosk Web View widget: native WebView2 child views driven by the panel page's bridge
  WebViewWidgetPlan.cs    # pure bridge protocol, rect mapping and view registry for the Web View widget
  PanelDisplay.cs         # monitor → panel target resolver
  DisplayIdentity.cs      # GDI device name → EDID-stable display id (assignment key)
  MonitorKioskManager.cs  # one kiosk per monitor-panel assignment, reconciled from the service
  MonitorKioskPlan.cs     # pure spawn/close reconcile math for the monitor kiosks
  PanelMonitorGuard.cs    # relocates foreign windows off the kiosk's monitor
  PanelGuardGeometry.cs   # pure placement math for evicted windows
  PanelHostMessage.cs     # pure parser for the page's nexus.panel.* host messages (hit-test rects, raise)
  PanelInputState.cs      # pure window-region, lease and raise state of a monitor kiosk
  KioskPolicy.cs          # pure primary-display rules, work-area bounds, monitor map
  MonitorMapPublisher.cs  # serialized, coalescing post of the monitor map
  RegionLayout.cs         # hit-test rect reader
  NexusApi.cs             # tiny REST client to nexus-service for /pair + /overlay/state
  Logger.cs               # rolling file logger
  Capture/                # streamed panels: off-screen host + manager + hand-rolled Windows.Graphics.Capture / D3D11 interop
  Media/                  # streamed panels: hand-rolled Media Foundation H.264 encoder, frame framing, ingest client
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

Tests live under `tests/`. They cover the AOT-safe bits (region math, kiosk plan math, state parsing) - the WebView2 surfaces are exercised end-to-end via the service's integration harness.

## Why a separate process

`nexus-service` is `ASP.NET Core / AOT` and runs as a Windows service. The overlay needs interactive desktop access (HWNDs, foreground activation, per-monitor DPI, transparent windowing) - none of which a service can do cleanly. Splitting it out also lets the overlay process exit when idle so a paired-but-empty install has no overlay process running at all.

## License

`nexus-overlay` is licensed under the **GNU Affero General Public License v3.0**
(AGPL-3.0); see [`LICENSE`](LICENSE) for the full text.

Copyright (C) 2026 Hello Nexus
