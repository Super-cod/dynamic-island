# Building Windows Dynamic Bar

## Requirements

| Tool | Version | Notes |
|---|---|---|
| Windows | 10 19041+ / 11 | x64 |
| .NET SDK | 8.0+ | `dotnet --version` (built with 8.0.425) |
| Inno Setup | 6.x | only for the installer (default: `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`) |
| Visual Studio | 2022 (optional) | any edition with .NET desktop workload |

One NuGet dependency: `System.Management 8.0.0`. Everything else is inbox (WPF, WinForms, `Microsoft.Windows.SDK.NET` from the `net8.0-windows10.0.19041.0` TFM).

## Build

```powershell
dotnet build WindowsDynamicBar.sln -c Debug -p:Platform=x64
```

Expected: **0 warnings, 0 errors**. Output: `src\WindowsDynamicBar\bin\x64\Debug\net8.0-windows10.0.19041.0\WindowsDynamicBar.exe`.

> The project sets `NoWarn=CA1416;SYSLIB0014;WFAC010` (known-OS-target and legacy-WebClient warnings).

## Test

`dotnet test` does not work here (it looks in the wrong RID folder). Run the exe directly:

```powershell
.\tests\WindowsDynamicBar.Tests\bin\x64\Debug\net8.0-windows10.0.19041.0\WindowsDynamicBar.Tests.exe
```

Expected: `passed: 20   failed: 0`, exit code 0. The suite is dependency-free (custom harness); it runs on the WPF UI thread.

## Publish (portable single file)

**All three flags are required** — omitting `IncludeNativeLibrariesForSelfExtract` crashes at startup (`DllNotFoundException` in `SetWindowLongPtr`):

```powershell
dotnet publish src\WindowsDynamicBar\WindowsDynamicBar.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:Platform=x64
```

Output: `src\WindowsDynamicBar\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\WindowsDynamicBar.exe`.

**Offline / flaky NuGet?** Use the local fallback source (cached runtime nupkgs 8.0.31):

```powershell
dotnet publish ... -p:RestoreSources="C:\Users\swaya\AppData\Local\Temp\nuget-local"
```

## Installer

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\WindowsDynamicBar.iss
```

Output: `release\WindowsDynamicBar-Setup.exe` (per-user install, `%LOCALAPPDATA%\Programs\Windows Dynamic Bar`, Start-Menu shortcuts, uninstaller).

## Release checklist

1. `dotnet build ... -c Release -p:Platform=x64` — 0/0
2. Debug test run — 20/20
3. Launch app once, confirm it stays alive and renders
4. Publish (command above)
5. Copy `WindowsDynamicBar.exe` → `release\`
6. Run ISCC → `release\WindowsDynamicBar-Setup.exe`
7. Write `release\README.txt`, generate `release\checksums.txt` (`Get-FileHash -Algorithm SHA256`)

## CI

`.github/workflows/ci.yml` runs build + tests on `windows-latest` for pushes and PRs. The installer job is manual (workflow_dispatch) since Inno Setup must be installed on the runner.
