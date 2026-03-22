# AlienFx Lite

Lightweight Windows control surface for AlienFX lighting and Dell/AWCC thermals.

Current scope:

- mapped AlienFX HID lighting devices from the bundled `alienfx-tools` `devices.csv` database
- Dell/AWCC fan control through the local broker service
- framework-dependent release packaging for a thin installer footprint
- procedurally generated transparent app/tray icons

The current lighting backend covers the upstream HID families (`v2`-`v8`) through a native bridge built on top of the upstream SDK. ACPI-only lighting paths are not yet wired into the bridge.

The main desktop artifact is a single Windows app:

- `AlienFxLite.exe`
  - launch normally to open the WPF UI
  - launch as a Windows service under `LocalSystem` to host the privileged broker
  - launch with `--install-service` / `--uninstall-service` for service management

The repo also contains:

- `AlienFxLite.Broker`: shared broker/runtime library
- `AlienFxLite.Service`: console-friendly debug host for the broker
- `AlienFxLite.Tool`: unelevated CLI client

## Supported Keyboard Zones

Zone layout is mapping-driven. On the current Dell G3/G5 profile, the default surface resolves to:

- `KB Left`
- `KB Middle`
- `KB Right`
- `KB NumPad`

## Build

```powershell
dotnet build .\AlienFxLite.sln
```

## Local Packaging

Build the full release payload locally:

```powershell
.\scripts\build-release.ps1
```

That script:

- regenerates the icon set
- builds the native bridge
- publishes the desktop app and CLI
- produces the thin installer and release zips

That creates:

- `artifacts\release\AlienFxLite-Setup-win-x64-v0.2.0.exe`
- `artifacts\release\AlienFxLite-portable-win-x64-v0.2.0.zip`
- `artifacts\release\AlienFxLite.Tool-win-x64-v0.2.0.zip`
- `artifacts\release\SHA256SUMS.txt`
- `artifacts\app\AlienFxLite.exe`
- `artifacts\tool\AlienFxLite.Tool.exe`

If you only want the desktop app publish output:

```powershell
.\scripts\build-native-bridge.ps1
dotnet publish .\AlienFxLite.UI\AlienFxLite.UI.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -o .\artifacts\app
```

## Run The UI

```powershell
.\artifacts\app\AlienFxLite.exe
```

The desktop app includes:

- lighting and fan control
- selectable mapped lighting surfaces/profiles
- capability-driven effect lists per mapped surface
- minimize/close to tray
- per-user `Start with Windows`
- manual `Check for updates` against GitHub Releases
- automatic broker reconnect

## Service Management

The desktop binary now manages its own service registration.

Install the service directly:

```powershell
sudo .\artifacts\app\AlienFxLite.exe --install-service --binary-path .\artifacts\app\AlienFxLite.exe
```

Uninstall it:

```powershell
sudo .\artifacts\app\AlienFxLite.exe --uninstall-service
```

The PowerShell wrappers still exist for convenience:

```powershell
sudo powershell -ExecutionPolicy Bypass -File .\scripts\install-service.ps1
sudo powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-service.ps1
```

Verify the installed service:

```powershell
Get-Service AlienFxLiteService
sc.exe qc AlienFxLiteService
.\artifacts\tool\AlienFxLite.Tool.exe status
```

## Console Broker Debugging

```powershell
.\AlienFxLite.Service\bin\Debug\net8.0-windows\AlienFxLite.Service.exe
```

Or from the unified desktop binary:

```powershell
.\artifacts\app\AlienFxLite.exe --service-console
```

## GitHub Actions Release Flow

- `ci.yml`
  - builds the solution
  - builds the full release payload
  - smoke-installs the generated installer on `windows-latest`
- `release.yml`
  - runs on tags matching `v*`
  - builds the release payload
  - creates a GitHub Release with all release artifacts

Create a release tag:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

## CLI Examples

```powershell
.\artifacts\tool\AlienFxLite.Tool.exe service ping
.\artifacts\tool\AlienFxLite.Tool.exe status
.\artifacts\tool\AlienFxLite.Tool.exe devices list
.\artifacts\tool\AlienFxLite.Tool.exe fans max
.\artifacts\tool\AlienFxLite.Tool.exe lights apply --device "0|187C|0550|4|187C:0550:DELL_G5_5500:MAIN_LIGHTS" --zones left,right --effect pulse --primary FF5500 --speed 60 --brightness 100 --keepalive true
```

`devices list` now prints the effect set supported by each detected mapped surface. Surface capability currently varies by API family:

- `v2/v3`: `Static`, `Pulse`, `Morph`
- `v4/v7/v8`: `Static`, `Pulse`, `Morph`, `Breathing`, `Spectrum`, `Rainbow`
- `v6`: `Static`, `Pulse`, `Morph`, `Breathing`
- `v5`: `Static`, `Pulse`, `Morph`, `Breathing`, `Rainbow`

For `API v5`, animated effects are whole-surface effects. The UI, broker, and CLI now enforce selecting the entire mapped surface for those animations instead of pretending they can be applied per-zone.
