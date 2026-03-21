# AlienFx Lite

Minimal Dell G3/G5 fan and keyboard lighting control for `VID_187C/PID_0550`.

The normal desktop artifact is now a single Windows executable:

- `AlienFxLite.exe`
  - launch it normally to open the WPF UI
  - install that same binary as `LocalSystem` and it runs as the broker service

Optional companion projects remain in the repo:

- `AlienFxLite.Service`: console-friendly broker host for debugging
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

## Publish The Single Executable

```powershell
dotnet publish .\AlienFxLite.UI\AlienFxLite.UI.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o .\artifacts\app
```

That produces:

```powershell
.\artifacts\app\AlienFxLite.exe
```

Optional CLI publish:

```powershell
dotnet publish .\AlienFxLite.Tool\AlienFxLite.Tool.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\tool
```

## Run The UI

```powershell
.\artifacts\app\AlienFxLite.exe
```

The desktop app includes:

- lighting and fan control
- minimize/close to tray
- per-user `Start with Windows`
- automatic broker reconnect

## Install The Broker Service

The normal path is a one-time `sudo.exe` install from a regular terminal:

```powershell
sudo powershell -ExecutionPolicy Bypass -File .\scripts\install-service.ps1
```

That installs `AlienFxLiteService` as `LocalSystem`, preserves the persisted state under `C:\ProgramData\AlienFxLite`, and grants the current desktop user access to the broker pipe.

You can also run the script manually from an elevated PowerShell:

```powershell
.\scripts\install-service.ps1 -BinaryPath .\artifacts\app\AlienFxLite.exe
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

## Debug Broker In Console Mode

```powershell
.\AlienFxLite.Service\bin\Debug\net8.0-windows\AlienFxLite.Service.exe
```

## CLI Examples

```powershell
.\artifacts\tool\AlienFxLite.Tool.exe service ping
.\artifacts\tool\AlienFxLite.Tool.exe status
.\artifacts\tool\AlienFxLite.Tool.exe fans max
.\artifacts\tool\AlienFxLite.Tool.exe lights apply --zones left,right --effect pulse --primary FF5500 --speed 60 --brightness 100 --keepalive true
```
