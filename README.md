# AlienFx Lite

Minimal Dell G3/G5 fan and keyboard lighting control for `VID_187C/PID_0550`.

The main desktop artifact is a single self-contained Windows app:

- `AlienFxLite.exe`
  - launch normally to open the WPF UI
  - launch as a Windows service under `LocalSystem` to host the privileged broker
  - launch with `--install-service` / `--uninstall-service` for service management

The repo also contains:

- `AlienFxLite.Broker`: shared broker/runtime library
- `AlienFxLite.Service`: console-friendly debug host for the broker
- `AlienFxLite.Tool`: unelevated CLI client

## Supported Keyboard Zones

- `KB Left`
- `KB Center`
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

That creates:

- `artifacts\release\AlienFxLite-Setup-win-x64-v0.1.0.exe`
- `artifacts\release\AlienFxLite-portable-win-x64-v0.1.0.zip`
- `artifacts\release\AlienFxLite.Tool-win-x64-v0.1.0.zip`
- `artifacts\release\SHA256SUMS.txt`

If you only want the desktop app publish output:

```powershell
dotnet publish .\AlienFxLite.UI\AlienFxLite.UI.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\app
```

## Run The UI

```powershell
.\artifacts\app\AlienFxLite.exe
```

The desktop app includes:

- lighting and fan control
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
git tag v0.1.0
git push origin v0.1.0
```

## CLI Examples

```powershell
.\artifacts\tool\AlienFxLite.Tool.exe service ping
.\artifacts\tool\AlienFxLite.Tool.exe status
.\artifacts\tool\AlienFxLite.Tool.exe fans max
.\artifacts\tool\AlienFxLite.Tool.exe lights apply --zones left,right --effect pulse --primary FF5500 --speed 60 --brightness 100 --keepalive true
```
