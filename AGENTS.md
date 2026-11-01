# Instructions for AI agents

You're helping someone set up CamRemote, an app for Windows and Mac that controls Sony cameras over Wi-Fi. It's tested on the A7S III. Most people here are photographers, not developers, and they may only be chatting with you, with no access to their computer. Use plain language, give one step at a time, and wait for them to confirm each step.

## Help someone set it up

Follow the three steps in README.md: install the app, set up the camera, connect. **Don't build anything.** The ready-made downloads need no tools, accounts or Sony SDK.

1. **Ask what they have.** Which camera? Check it against "Which cameras work?" in README.md. If it isn't on the list, say so plainly and stop. If it's listed but isn't the A7S III, tell them it should work but hasn't been tested, and its menus may differ a little. Windows or Mac? On a Mac, is it Apple or Intel (Apple menu › About This Mac)? Windows 11 x64 and macOS 14.1 or newer are supported. Windows on ARM and Linux aren't: say so plainly.
2. **Install.** Send them the matching download link from the top of README.md. Windows may show a blue **Windows protected your PC** box: **More info**, then **Run anyway**. On a Mac, they drag CamRemote to Applications.
3. **The camera menus.** Go through the table in README.md section 2, one row at a time. They press the buttons themselves. Ask them to close Sony's Monitor & Control app first.
4. **Connect.** Follow README.md section 3. On a Mac, **Local Network** must be allowed or the camera won't be found. If something fails, use the "Something not working?" section.

They agree to the note on first launch themselves. Explain it if asked; don't agree on their behalf.

## Build from source

Only when someone asks to build it or change the code.

1. **Check what's installed** before installing anything: `git`, `cmake`, the .NET 10 SDK, Python 3.9+ and a C++ compiler (Visual Studio 2022 or newer with the VC tools, or Xcode's command line tools on a Mac). On Windows, `python` can be the Microsoft Store stub, which prints nothing useful.
2. **Install only what's missing,** and tell the person before you start. Visual Studio's C++ tools are large and slow.
3. **The Sony SDK can't be downloaded by you:** it sits behind Sony's licence agreement. Send the person to https://support.d-imaging.sony.co.jp/app/sdk/en/index.html for the **Windows** or **Mac** zip and wait for them.
4. It belongs in `vendor\CrSDK\` so that `vendor\CrSDK\RemoteCli.zip` (or an unpacked `vendor\CrSDK\RemoteCli\`) exists. If it was unzipped into a nested folder, move the contents up one level.
5. **Build.** On Windows, `powershell -ExecutionPolicy Bypass -File .\build.ps1` ends with `Built dist\CamRemote.exe`. On a Mac, `./build-mac.sh` ends with `Built dist/CamRemote.app`.

Common failures:

- **CMake can't find a Visual Studio generator:** the C++ build tools are missing or CMake is older than Visual Studio. Update CMake or install the VC tools workload.
- **`python` not found or does nothing:** the Store stub is first on PATH. Install Python and open a new shell.
- **`gen_names.py` errors about a missing enum:** a newer SDK renamed something. Find the new name in `vendor\CrSDK\RemoteCli\app\CRSDK\*.h`, update `VALUE_ENUMS` in `tools\gen_names.py`, and tell the person what changed.
- **NuGet restore fails:** the machine is offline or behind a proxy. Tell the person; don't change their network settings.
- **The Mac app crashes at launch over SSH:** the display is asleep. Wake it first.

**Never commit anything under `vendor\`**, and never commit `app\CamRemote\SdkNames.g.cs` (it's generated from Sony's headers). Both are in `.gitignore`; keep it that way. Sony's licence allows its libraries inside the finished app, not the SDK itself.

## Don't

- Don't ask for, type or store the camera's Access Authen. password. The person types it into the app; the app keeps it in Windows Credential Manager or the macOS Keychain.
- Don't change firewall rules, network settings or router settings. If a Windows Firewall or macOS privacy prompt appears, the person answers it.
- Don't change camera firmware or camera settings for them.
- Don't add telemetry, auto-updaters or background services. The app looks once at launch for a newer release on GitHub and only shows a button; keep it that way.
- Don't remove the first-launch agreement. Sony's licence requires it for anyone distributing the app.
