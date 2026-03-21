# AlienFx Lite

Minimal Dell G3/G5 fan and keyboard lighting control built as:

- `AlienFxLite.Service`: privileged Windows service broker
- `AlienFxLite.UI`: unelevated WPF desktop client
- `AlienFxLite.Tool`: unelevated CLI client

V1 targets `VID_187C/PID_0550` with the 4 keyboard zones:

- `KB Left`
- `KB Center`
- `KB Right`
- `KB NumPad`

## Build

```powershell
dotnet build .\AlienFxLite.sln
```

## Publish

```powershell
dotnet publish .\AlienFxLite.Service\AlienFxLite.Service.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\service
dotnet publish .\AlienFxLite.UI\AlienFxLite.UI.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\ui
dotnet publish .\AlienFxLite.Tool\AlienFxLite.Tool.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\tool
```

## Run The Service In Console Mode

This is useful for testing lights from a normal user session. Fan control is expected to remain unavailable here unless the broker is running with elevated service rights.

```powershell
.\artifacts\service\AlienFxLite.Service.exe
```

## Install As A Windows Service

The normal path is a one-time `sudo.exe` install from a regular terminal:

```powershell
sudo powershell -ExecutionPolicy Bypass -File .\scripts\install-service.ps1
```

That installs `AlienFxLiteService` as `LocalSystem`, preserves the current persisted state under `C:\ProgramData\AlienFxLite`, and grants the current desktop user access to the broker pipe.

You can also run the script manually from an elevated PowerShell:

```powershell
.\scripts\install-service.ps1 -BinaryPath .\artifacts\service\AlienFxLite.Service.exe
```

Verify the installed service:

```powershell
Get-Service AlienFxLiteService
sc.exe qc AlienFxLiteService
.\artifacts\tool\AlienFxLite.Tool.exe status
```

Remove the service:

```powershell
sudo powershell -ExecutionPolicy Bypass -File .\scripts\uninstall-service.ps1
```

## UI

Run:

```powershell
.\artifacts\ui\AlienFxLite.UI.exe
```

## CLI Examples

```powershell
.\artifacts\tool\AlienFxLite.Tool.exe service ping
.\artifacts\tool\AlienFxLite.Tool.exe status
.\artifacts\tool\AlienFxLite.Tool.exe fans max
.\artifacts\tool\AlienFxLite.Tool.exe lights apply --zones left,right --effect pulse --primary FF5500 --speed 60 --brightness 100 --keepalive true
```
