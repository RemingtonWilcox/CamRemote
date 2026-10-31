# CamRemote

A Windows app for controlling a Sony A7S III (ILCE-7SM3) from your PC over Wi-Fi. It replaces Sony's Monitor & Control phone app with big, simple controls and a connection that comes back by itself.

It was built and tested on an A7S III only. Other cameras in Sony's Camera Remote SDK list (A7 IV, FX3, FX30, A1 and others) may work, but nobody has tried them.

**What it does**

- **Live picture:** the camera's live view over Wi-Fi with no cable. Or, if you have an HDMI capture card, the camera's HDMI output at full quality.
- **Click to focus:** click the picture to focus there. Right-click cancels.
- **Exposure:** ISO, shutter and iris, with one-click Base ISO 640 / 12800 for S-Log3.
- **Focus:** AF/MF switch, a focus ring for manual focus, and focus magnifier.
- **Record:** start and stop with the big button or **F9**. The picture gets a red outline while recording.
- **Status:** battery and card time, with warnings when either runs low.
- **Audio:** record level, audio on/off and wind noise reduction. Live audio meters only work through a capture card, because the camera sends no sound over Wi-Fi.
- **Rotate the picture** for a camera mounted on its side.
- **More settings:** white balance, color temperature, focus area, file format, record setting, frame rate, LUT and HDMI output.
- **Reconnects by itself:** turn the camera off and on, walk out of range, and it comes back without you doing anything. It keeps the PC awake while connected.

**What you need**

- A Windows 10 or 11 PC (64-bit). **This app is Windows only.** Sony's SDK also comes for Mac and Linux, but this app's window is built with Windows-only tools.
- A Sony A7S III, and a Wi-Fi network that both the camera and the PC are on (your home router is fine). Distance between you and the camera doesn't matter, as long as the camera has a good Wi-Fi signal.
- About 30–60 minutes the first time, mostly installing tools.

You can follow the steps below yourself, or have an AI coding agent (Claude Code, Codex and similar) do the PC side for you: see [Let an AI agent set it up](#let-an-ai-agent-set-it-up).

---

## 1. Update the camera

Update the A7S III to Sony's latest firmware. It was tested on version 5.01. Sony's firmware page is under *Support* for the ILCE-7SM3 on your country's Sony site. The menu names below are for firmware 3.00 and later.

## 2. Set up the camera (once)

Close the Monitor & Control phone app first: the camera only takes one remote at a time.

1. **Join your Wi-Fi.** **MENU › Network › Wi-Fi › Wi-Fi Connect › On**, then **Access Point Set.** and pick your home network. If the camera offers **Wi-Fi Frequency Band**, choose 5GHz. Sony suggests turning Bluetooth off.
2. **Allow remote control.** **MENU › Network › Cnct./Remote Sht. › Remote Shoot Function › Remote Shooting › On**.
3. **Find the login.** **MENU › Network › Network Option › Access Authen. Settings**. Leave **Access Authen.** on. **Access Authen. Info** in the same menu shows a **user**, **password** and **fingerprint**. You'll type these into the app once. Keep them somewhere safe, like a password manager.
4. **Find the camera's address.** **MENU › Network › Wi-Fi › Display Wi-Fi Info.** shows its **IP address** (for example `192.168.1.40`). The app usually finds the camera by itself, but write this down in case. If you can, reserve that address for the camera in your router's settings so it never changes.
5. **For click-to-focus,** turn **Touch Operation** on. Sony only allows remote touch focus on the A7S III in movie mode.
6. **Only if you use an HDMI capture card:** set **HDMI Resolution** to **1080p** (most capture cards can't take 4K). Turn **HDMI Info. Display** off for a clean picture.

## 3. Install the tools

Open **Terminal** (or PowerShell) and run these one at a time. Each takes a few minutes.

```powershell
winget install --id Git.Git -e
winget install --id Kitware.CMake -e
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id Python.Python.3.13 -e
winget install --id Microsoft.VisualStudio.BuildTools -e --override "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

The last one is Microsoft's C++ compiler. If you already have Visual Studio 2022 or newer with **Desktop development with C++**, skip it.

Close Terminal and open a new one afterwards, so it sees the new tools.

## 4. Get the code

```powershell
cd $HOME\Documents
git clone https://github.com/RemingtonWilcox/CamRemote.git
cd CamRemote
```

## 5. Get the Sony SDK

Sony's Camera Remote SDK is free but can't be shared here, so you download it yourself.

1. Go to Sony's Camera Remote SDK page: https://support.d-imaging.sony.co.jp/app/sdk/en/index.html and follow the download link. You'll accept Sony's licence and may have to give an email address.
2. Download the **Windows** version (a zip named like `CrSDK_v2.02.00_..._Win64.zip`). Version 2.02.00 is what this was built with; newer should work.
3. Unzip it so that the folder `CamRemote\vendor\CrSDK` contains `RemoteCli.zip` (and other files). The build unpacks `RemoteCli.zip` for you.

You should end up with:

```
CamRemote\
  vendor\
    CrSDK\
      RemoteCli.zip
      ...
```

## 6. Build it

In the `CamRemote` folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

It ends with `Built dist\CamRemote.exe`. If it stops with an error, the message says what's missing.

Tip: right-click `dist\CamRemote.exe` › **Show more options › Send to › Desktop (create shortcut)**.

## 7. Connect

1. Turn the camera on and make sure it's on Wi-Fi.
2. Run `dist\CamRemote.exe`. Under **Picture** on the right, pick **Camera over Wi-Fi** (or your capture card).
3. Click **Connect camera…**. Pick your camera under **Cameras found on Wi-Fi / USB**, or type its IP address in the box below, then enter the **user** and **password** from step 2.3 and click **Connect**.
4. If Windows Firewall asks about CamRemote, allow it on **Private networks** only.

After this, the app connects by itself every time you open it, and reconnects whenever the camera comes back. The password is stored in Windows Credential Manager, not in a file.

---

## Using it

- **ISO / Shutter / Iris:** ◀ ▶ step through values (▶ is always the bigger number). Click the value to see every choice.
- **Base ISO 640 / 12800:** the A7S III's two native ISOs in S-Log3. 12800 is the cleanest choice in low light.
- **Focus ring:** works in MF (or DMF). Drag or scroll the wheel like a real ring, or use the arrows for set steps. Right is farther, left is nearer.
- **Focus magnifier:** Off / ×4 / ×8, or whatever ratios the camera lists. While magnified, click the picture to move the magnified area.
- **Click the picture** to focus there (movie mode). Right-click cancels. In MF it autofocuses once, then returns to MF.
- **Tap does:** Focus or Tracking. Sony's SDK doesn't offer the setting for a remote tap's function on the A7S III, so Tracking may only focus; the app tells you if the camera didn't report tracking.
- **Picture source:** **Camera over Wi-Fi** needs no cable. Any HDMI capture card Windows sees is listed too, at its highest resolution. Plugged one in after opening the app? Click **↻**.
- **Rotate:** ⟲ / ⟳ under **Picture**, for a camera mounted in portrait. Remembered next time.
- **Record:** the big button or **F9**.
- **Only the focus wheel reacts to the mouse wheel,** so you can't change a setting by accident while scrolling.
- **Shooting guides:** the bar above the picture toggles Histogram (**H**), Zebras (**Z**), Peaking (**P**) and Grid (**G**). Click the histogram or **⋯** for levels, colours and an RGB histogram. These are worked out by the app from the preview, not the recorded file, so a Monitor LUT changes them. Peaking at Wi-Fi preview size shows the plane of focus; use the magnifier for critical focus. The exposure meter comes from the camera.
- **Recording timer:** keeps counting through a dropped link or an app restart. A take already rolling when the app first sees it counts from then, shown with a **+**.
- **Phone control:** tick **Allow phone control** under **Phone**, then open one of the addresses shown there in your phone's browser and enter the access code. It works on your home network and over Tailscale, and only answers those addresses. The PC must stay on with the app open, and stays awake while this is on. On an iPhone, use Share › Add to Home Screen for a full-screen app.
- **More camera settings** holds everything else, collapsed.
- While connected, the app's settings win over the camera dials. The camera goes back to normal when you disconnect.

**Audio level tip:** aim for loud sounds peaking around −12 dB and never reaching 0. On the A7S III the record level runs roughly 0–31 (the app shows the camera's real range). Close sources usually want the high teens to low 20s; distant or quiet ones the mid 20s. Check it once on the camera's own audio meter (or through a capture card), then leave it.

## Troubleshooting

- **The camera isn't in the list:** type its IP address from step 2.4. Make sure the PC and the camera are on the same Wi-Fi network (not a guest network).
- **"The camera rejected the user or password":** re-check the spelling (it's case-sensitive) and use the user and password from **Access Authen. Info**. The user is often not `admin`.
- **"The camera's security fingerprint has changed":** expected after you reset the camera or change its Access Authen. settings. If you didn't, say No. The app remembers the fingerprint from the first connection.
- **Keeps reconnecting / choppy picture:** the camera's Wi-Fi signal is weak. Move the router or a mesh point closer to the camera. Recording carries on in the camera even if the link drops.
- **Click-to-focus does nothing:** the camera must be in movie mode with **Touch Operation** on.
- **Can't connect while the phone app is open:** close Monitor & Control. One remote at a time.

## Let an AI agent set it up

If you use an AI coding agent on your PC, you can hand it the PC side. Do the camera steps (1 and 2) yourself, then paste this into the agent:

> Clone https://github.com/RemingtonWilcox/CamRemote into my Documents folder and follow its AGENTS.md to install what's missing and build it. Tell me when I need to download the Sony SDK, and where to put it.

The agent will still need you to download the Sony SDK (it requires accepting Sony's licence) and to type the camera password into the app yourself.

## How it's built

- `native/CamBridge/`: a small C++ DLL that wraps Sony's SDK in a flat C API returning JSON.
- `app/CamRemote/`: the WPF app (.NET 10): camera session (`CameraService.cs`), Wi-Fi live view (`LiveViewFeed.cs`), capture card feed (`VideoFeed.cs`), audio meters (`AudioMeter.cs`) and the UI.
- `tools/gen_names.py`: generates `SdkNames.g.cs` (property codes and value names) from the SDK headers at build time.
- `tools/make_icon.py`: draws the app icon (`CamRemote.ico`).
- `build.ps1`: generates the names, builds the DLL and publishes the app to `dist\`.

Notes from Sony's SDK reference (v2.02.00) for the A7S III:

- Remote touch focus works in movie mode only. Focus positions are a 640×480 grid over the picture.
- MovieRecord "Down" toggles recording; Sony asks for "Up" right after it.
- Over Wi-Fi the camera reports battery in bars, not percent, so the app shows a range.
- There's no live audio level over the SDK; meters need the HDMI audio through a capture card.

## Licence

This app's code is MIT licensed (see `LICENSE`). Sony's Camera Remote SDK is not included and has its own licence from Sony. This project isn't made by or affiliated with Sony.
