# Instructions for AI agents

You're helping someone set up CamRemote, a Windows app that controls a Sony A7S III over Wi-Fi. The human-facing steps are in README.md. Your job is the PC side: check the machine, install what's missing, get the SDK in place and build. Keep the person in the loop in plain language; many users aren't developers.

## Before you start

1. **Check the OS.** This app is Windows 10/11 x64 only (WPF). On a Mac or Linux machine, stop and tell the person plainly: it won't run there without a port. Don't attempt the build.
2. **Check what's installed** before installing anything: `git --version`, `cmake --version`, `dotnet --list-sdks` (needs a 10.x SDK), `python --version` (3.9+; on Windows, `python` can be the Microsoft Store stub, which prints nothing useful), and a C++ compiler (look for Visual Studio 2022 or newer with the VC tools, e.g. `& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`).
3. **Install only what's missing**, using the winget commands in README.md section 3. Tell the person before you start an install; the Visual Studio Build Tools one is large and slow (run it in the background if your harness supports that). After installing, open a new shell so PATH updates.

## The Sony SDK

- The SDK can't be downloaded by you: it sits behind Sony's licence agreement. Send the person to https://support.d-imaging.sony.co.jp/app/sdk/en/index.html for the **Windows** zip and wait for them.
- It belongs in `vendor\CrSDK\` so that `vendor\CrSDK\RemoteCli.zip` (or an unpacked `vendor\CrSDK\RemoteCli\`) exists. `build.ps1` unpacks `RemoteCli.zip` itself. If the person unzipped into a nested folder (e.g. `vendor\CrSDK\CrSDK_v2.02.00_...\`), move the contents up one level.
- Success check: `vendor\CrSDK\RemoteCli\external\crsdk\Cr_Core.lib` exists after the build starts.
- **Never commit anything under `vendor\`**, and never commit `app\CamRemote\SdkNames.g.cs` (it's generated from Sony's headers). Both are in `.gitignore`; keep it that way.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

It ends with `Built dist\CamRemote.exe`. Common failures:

- **CMake can't find a Visual Studio generator:** the C++ build tools are missing or CMake is older than the installed Visual Studio. Update CMake (`winget upgrade Kitware.CMake`) or install the VC tools workload.
- **`python` not found / does nothing:** the Store stub is first on PATH. Install Python with winget and open a new shell.
- **`gen_names.py` errors about a missing enum:** a newer SDK renamed something. Read the error, find the new name in `vendor\CrSDK\RemoteCli\app\CRSDK\*.h`, and update `VALUE_ENUMS` in `tools\gen_names.py`. Tell the person what changed.
- **NuGet restore fails:** the machine is offline or behind a proxy. Tell the person; don't change their network settings.

## After the build

- Offer to put a shortcut to `dist\CamRemote.exe` on the desktop.
- The person does the camera setup (README sections 1–2) and the first connection (section 7) themselves.

## Don't

- Don't ask for, type or store the camera's Access Authen. password. The person types it into the app; the app keeps it in Windows Credential Manager.
- Don't change firewall rules, network settings or router settings. If Windows Firewall prompts, the person answers it (Private networks only).
- Don't change camera firmware or camera settings for them.
- Don't add telemetry, auto-updaters or background services.
