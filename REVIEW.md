# CamRemote code review — November 2026

Branch: `claude/camremote-review` (from `main` at `1fbfb84`). Nothing on `main` was changed.

**What was reviewed:** all of `app/CamRemote` (the shared camera code and the WPF window), `app/CamRemote.Mac`, `native/CamBridge`, `tools/`, the build and packaging scripts, the docs and the git history. A second, independent reviewer covered the window, capture, audio, Mac native and phone-page code. Its findings are marked *(UI review)*. I traced each one I fixed.

**What couldn't be checked here:** Sony's SDK headers and libraries sit behind Sony's licence and aren't in the repo. So:
- CamBridge can't be compiled here (its `sscanf` change was checked as a standalone program).
- The WPF app needs Windows to build.
- Hex constants copied from the SDK, such as the error codes in `CameraService.Explain`, could not be checked against the real headers. See R1.

The Mac app *was* compiled on Linux with stand-in names, and CI now does the same on every push.

---

## Follow-ups (branch `claude/camremote-followups`, from `claude/camremote-review`)

Work on R1 and R2, plus R21. Nothing on `main` or `claude/camremote-review` was changed.

| Commit | What changed |
|---|---|
| `971c201` | **R1: Sony's errors are now explained by name, not by hand-typed numbers.** The new `SdkCodes.cs` looks each code up in `SdkNames.Errors`, which `gen_names.py` builds from the SDK the app is compiled with, so the numbers always come from Sony's real headers. When `Connect` itself returns the wrong-password error, auto-reconnect is now held too, not only when the error arrives as an event. |
| `c733288` | **R2, native part:** `cb_enum` refused to search whenever a session handle existed. It now refuses only while connected, and releases a handle left over from a dropped link first. |
| `cb8f461` | **The saved camera is known by its MAC address before its IP.** Before, a different camera that took the old IP would have been picked (only the fingerprint prompt stood in the way). Now it's skipped. |
| `19b061e` | **R2: `AutoReconnect`, a shared and tested reconnect loop.** After a drop it waits 15 s for the SDK's own resume. Then it searches, which releases the stale session, and reconnects to the same camera by MAC at its new IP. It backs off 10 → 20 → 40 → 60 s, retries a connect stuck for 45 s, and reports its status. |
| `0cc3be0` | Both windows use `AutoReconnect`. The drop banner shows the try number and when the next try is. Also fixes R21 (the misplaced doc comment). |

### How far the error codes could be verified (R1)

- **Sony's headers and API reference aren't public.** They come only in the SDK download, behind Sony's licence. Unofficial copies on GitHub were deliberately not used, so no number could be checked against `CrError.h` here.
- **So the app no longer depends on the numbers at all.** `SdkCodes` matches Sony's *names*, such as `CrError_Connect_FailBusy`, and the generator supplies the numbers from the real headers at build time.
- **The names themselves are marked UNVERIFIED in `SdkCodes.cs`.** They follow Sony's naming as recalled from the SDK. To check them:
  - `gen_names.py` now prints `warning: <name> (used in SdkCodes.cs) is not in the SDK headers` for each name that's missing.
  - The app writes the same list to its log at startup.
  - **Next step for someone with the SDK:** run `build.ps1` or `build-mac.sh` and fix any name that warning reports.
- **One code is confirmed from a public source:** `CrError_Connect_RemoteTransfer_NotSupported` = 0x8219, in a CrSDK 2.01 log in [goudvuur/sonshell issue #2](https://github.com/goudvuur/sonshell/issues/2). It now gets its own message.
- **A wrong name can't give wrong advice.** That error just gets the general message with its number. The old numbers are kept only as a fallback for codes the headers give no name at all, so a correct name always wins.
- **Tests:** the fake header gives these names made-up numbers, away from the old ones. It also puts an unrelated name on 0x8216, the number that used to mean "wrong password". So the tests pass only if the lookup really goes by name.

### Rediscovery: what's still unproven (R2)

- `CamBridge.cpp` can't be compiled without the SDK. The change is three lines in `cb_enum`.
- **Camera identity is the MAC address from enumeration plus the pinned SSH fingerprint.** That's what the app already reads through the SDK. A serial number or GUID would need SDK calls that can't be checked here, so none were added.
- **Hardware test still needed:**
  1. Connect over Wi-Fi.
  2. Make the router hand the camera a new address: a short DHCP lease, or a reservation change while the camera is off.
  3. Power-cycle the camera, or walk out of range.
  4. Expect the banner to show the tries, then "The camera moved to …" in the log, then the connection.
  5. Also check that a quick drop at the same IP still resumes through the SDK, with no search in the log.
- **R5 is partly addressed.** The app now waits 15 s after a drop before acting, and its retries back off. The SDK's own reconnect is still on, and the app takes over after the grace period.

Tests now: **193 C#** (was 156) and **9 Python** (was 7). Both suites pass on Linux. The Mac app compiles against the made-up names. The WPF app's C# also compiles on Linux with `-p:EnableWindowsTargeting=true`; only copying the native DLL fails, which needs the SDK.

---

## Summary

| | |
|---|---|
| Secrets or signing material committed | **None found**, in the current tree or in any of the 11 commits. Notarization uses a Keychain profile (`package-mac.sh`), and no SDK files or `SdkNames.g.cs` were ever committed. |
| Bugs fixed | 9, each in its own commit with a test where one is possible |
| Tests added | 156 C# tests and 7 Python tests, run on Linux, Windows and macOS by `.github/workflows/tests.yml`, plus a compile check of the Mac app |
| Recommendations | 21, ranked below |

---

## Fixed (one commit each)

| # | Severity | What was wrong | Commit |
|---|---|---|---|
| F1 | **High** | **The phone Record button could stop a take it had just started**, or leave the camera rolling. `SonyRemote.Record(on)` compared the request with `IsRecording`, which only updates on the next settings read (2 s or more). Two taps of Record on a slow link sent two toggles, so the take stopped. Record then Stop quickly sent only one, so it kept rolling. `CameraService.SetRecording` now remembers the pending request until the camera confirms it, or for 4 s. A press the camera refused clears it. | `bd0649e` |
| F2 | **High** | **Phone focus buttons could keep driving the lens.** A second finger replaced the 200 ms repeat timer without clearing the first one. A button disabled mid-hold could also miss `pointerup`. Either way, focus steps went out until the page was closed. *(UI review)* | `7b1fe01` |
| F3 | Medium | **Focus-once in MF could leave the camera in AF and block every later tap.** An exception between switching to AF-C and back skipped the switch back and left `_focusOnceBusy` at 1. Every tap after that answered "Still focusing on the last tap" until a restart. That block also called `RequestRefresh` from the camera thread. | `4aa772d` |
| F4 | Medium | **A crash while saving could forget the camera's trusted fingerprint.** `settings.json` was rewritten in place, and `Load()` treats any parse error as defaults. The next connect would then trust whatever answered at that address without warning. Saves are now written to a new file and renamed over the old one, which is atomic. | `2c33b4a` |
| F5 | Medium | **A typed IP with a part over 255 connected to a different device.** `cb_make_ip_camera` used `sscanf("%u.%u.%u.%u")` and bit-shifts, so `192.168.1.300` became `192.168.1.44`, and trailing junk was ignored. The app now validates in `CameraConnect.IsIPv4`, and the native code range-checks too. | `209d52f` |
| F6 | Medium | **Auto-reconnect ran while the Connect dialog was open.** It could pop the fingerprint warning over the dialog, rewrite `CameraIp`, and end with two `Connect` calls. Fixed in both windows. *(UI review)* | `a1718f5` |
| F7 | Medium | **Phone control could stay listening on the network while the checkbox said off.** This happened after ticking it on and quickly off: the slower start finished last and kept its server. Fixed in both windows. *(UI review)* | `7f879e1` |
| F8 | Low–Med | **After a re-login, the phone page's picture never came back.** The frame loop exited on a 401 and was only ever started once. *(UI review)* | `7b1fe01` |
| F9 | Low–Med | **`gen_names.py` could give the app wrong property numbers after a Sony SDK update.** It stripped `u`/`l` suffixes after any hex-digit character, so names like `CrError_Level` broke. The member was dropped silently, and the members after it got wrong numbers. Now only number literals are touched, and a member that can't be worked out is reported. | `cbe0a00` |

Also in the test-suite commit, with no behaviour change:
- `CameraService` now reaches the SDK through `ICameraBridge`. `NativeBridge` is the real one; tests use `FakeCamera`.
- Tests can redirect `Settings.FolderOverride`.
- `UpdateCheck.NewerThanCurrent` and `PhoneServer.IsLocalOrTailscale` are exposed so they can be tested.
- `_refreshQueued` is now `volatile`.

**Merge note:** these commits touch `CameraService.cs`, `MainWindow.xaml.cs`, `MainWindow.axaml.cs` and `phone.html`, which are being edited on `main`. Every change is small and local. The biggest one is the mechanical `Native.cb_x` → `_bridge.X` rename in `CameraService.cs`. If it conflicts, re-apply that rename with search and replace rather than merging hunk by hunk.

---

## Recommendations (not changed: risky, design-level, or needs hardware or the SDK)

### High

- **R1. Check the hard-coded Sony codes against the real headers, or look them up by name.** *Done on `claude/camremote-followups`: looked up by name, and the names are checked at build time. See Follow-ups.*
  - **The issue:** `CameraService.Explain` maps 0x8202, 0x8207–0x820B, 0x8210 and 0x8212–0x8215 to plain-language advice. `ExplainNotice` does the same for 0x20001–0x2006A. `OnNativeEvent` uses 0x8216 for "wrong password". All these numbers were typed in by hand. If one is off by one, someone gets "Close Monitor & Control" for a password error, or a wrong password never pauses auto-reconnect, so the camera's lockout keeps getting hit.
  - **The fix:** `SdkNames.Errors` already maps codes to names from the headers. Turn that around and switch on names (`"CrError_Connect_FailBusy"`), so the generator keeps the numbers right. Or check each value once against `CrError.h`. The Explain tests in `CameraServiceTests` would then need the fake header to carry those names.
- **R2. "Find the camera by MAC address" doesn't work after a Wi-Fi drop, only at startup.** *Done on `claude/camremote-followups`; still needs a hardware test. See Follow-ups.*
  - **Why:** `cb_enum` returns `-2` whenever `g_handle` is set. `OnDisconnected` doesn't clear the handle, because the SDK is asked to reconnect by itself (`CrReconnecting_ON`). The app's 15-second retry then gets an empty camera list and keeps trying the old IP. If the router gave the camera a new address while it was away, the app never finds it again until it restarts.
  - **The fix:** in `cb_enum`, if `g_handle && !g_connected`, disconnect and release the stale handle before enumerating. Do this only after the SDK's own reconnect has had its chance; the app already waits 15 s. It needs testing on hardware with a DHCP change.
- **R3. Capture-card start and stop can overlap.** *(UI review)*
  - **Windows:** `VideoFeed.StartAsync` keeps `_capture` and `_reader` in fields across awaits, and `OnVideoDeviceChanged` is `async void`. Switching source mid-start can leak a reader, which holds the card in exclusive use, or keep the card running on Wi-Fi.
  - **Mac:** `cb_cap_start` (on a `Task.Run` thread) and `cb_cap_stop` (on the UI thread) touch `g_video` and `g_videoSink` without a lock. That can lead to a double release.
  - **The fix:** a `SemaphoreSlim` plus a generation check after each await. On the Mac side, a serial dispatch queue in `MacSupport.mm`.
- **R4. Nothing catches unhandled exceptions.**
  - **The issue:** `async void` handlers (`OnLoaded`, `OnConnectClick`, the ConnectWindow handlers, F12 dump) can throw, from bad bridge JSON or a full disk. That closes the app mid-shoot with no log line.
  - **The fix:** add `DispatcherUnhandledException` and `TaskScheduler.UnobservedTaskException` (and Avalonia's equivalent) that write to `Log` and show a message. Also put a try/catch in each `async void` handler.

### Medium

- **R5. Two reconnect loops.** The SDK reconnects by itself (`CrReconnecting_ON`) and the app also reconnects every 15 s. When both act, the app's `cb_connect` tears down the SDK's half-resumed session. Pick one. Either let the SDK own short drops and have the app act only after `CrWarning_Connect_Reconnecting` has gone unanswered for 30 s or so, or turn the SDK's off. Log both paths, and test by walking out of Wi-Fi range mid-take.
- **R6. Phone access can't be revoked, and the token travels in clear text.** The 8-character code and the 64-hex token are made once and never change. The cookie lasts a year and goes over plain HTTP, so anyone sniffing the Wi-Fi can replay it. Suggestions:
  - a **"New phone code"** button that regenerates both;
  - a shorter cookie lifetime;
  - optionally a self-signed HTTPS listener, or a note to use Tailscale, which encrypts traffic.
- **R7. "Tailscale" trust covers all of 100.64.0.0/10.** That is carrier-grade NAT space. On some mobile hotspots, Starlink and some ISPs, other customers have addresses there. Only trust 100.64/10 when the connection arrived on the Tailscale interface: check that `ctx.Connection.LocalIpAddress` belongs to a Tailscale NIC. The code is still required, so this is defence in depth.
- **R8. The Mac UI freezes on the microphone permission prompt.** `cb_audio_start` → `allowed()` waits on a semaphore on the main thread. Start the meter off the UI thread, as video already does. *(UI review)*
- **R9. Settings that fail to load are silently reset.** F4 makes corruption unlikely. Still, `Settings.Load` should rename a broken file to `settings.json.bad` and log it, rather than quietly losing the fingerprint pin.
- **R10. `package-mac.sh` can notarize an ad-hoc-signed app.** `build-mac.sh` falls back to ad-hoc signing with no hardened runtime. The packaging script should stop early if there's no Developer ID. *(UI review)*
- **R11. Entitlement `allow-unsigned-executable-memory`** is broader than .NET normally needs (`allow-jit`). Try removing it and smoke-test a notarized build on Apple and Intel Macs. *(UI review)*
- **R12. The app re-reads every property on every change.** CamBridge already sends one `EvPropertyChanged` per changed code (`OnPropertyChangedCodes`), but the app ignores the code and re-reads all several hundred properties. Collect the changed codes and use `GetSelectDeviceProperties` for them. Keep the 2 s full read as a backstop. That means less Wi-Fi traffic competing with live view.

### Low

- **R13.** `EvDisconnected` clears `Props` but doesn't raise `PropsChanged`, so anything bound only to that event shows stale values until the next change. `Disconnect()` and `RestartLink()` do raise it.
- **R14.** `LiveViewFeed`:
  - `lastFrame` isn't reset after an error, so if a reconnected camera's first frame number equals the old one, that frame is skipped.
  - `Stop()` joins for 2 s and then a `Start()` can run a second loop on the same `_buf`.
  - It calls `Native.cb_liveview` directly. Route it through `ICameraBridge` so it can be tested too.
- **R15.** `FrameLoop` reads `State` (UI-thread state) from its own thread, and so does `OnNativeEvent`. It's harmless today, but make `State` volatile, or read a separate atomic flag, before adding logic that depends on it.
- **R16.** `PictureAssist.DrawHistogram` indexes out of range if `ZebraLevel > 100`, which can only happen by hand-editing `settings.json`. Clamp it in `Settings.Load`.
- **R17.** `cb_fingerprint` passes a fixed `char fp[512]` with no length to `GetFingerprint`. Check Sony's documented maximum, and use `len` to size the buffer if the SDK allows.
- **R18.** `MacSupport.mm`: `copyOut` calls `strlen(nil)` if JSON serialization fails, and an early return in `cb_cap_start` skips `commitConfiguration`. *(UI review)*
- **R19.** On Windows, `AskFingerprint`'s MessageBox has no owner, so it can open behind the dialog. On the Mac it passes an owner. *(UI review)*
- **R20.** `AudioMeter` reads 24-bit PCM as silence. *(UI review)*
- **R21.** *(Fixed on `claude/camremote-followups`.)* The XML doc comment that belongs to `TryReconnectAsync` sits on `ShowUpdateAsync` in `MainWindow.xaml.cs`.

---

## Checks that came out fine

- **No XSS on the phone page.** It builds everything with `textContent` and `createElement`, never `innerHTML` with server data.
- **The phone API is protected against cross-site requests.** It needs a `SameSite=Strict`, `HttpOnly` cookie *and* a custom `X-CamRemote` header on every change, and it only answers private and Tailscale addresses. Tests cover all three. Guessing the code isn't practical: the code has about 40 bits, and every wrong try costs 500 ms.
- **The camera password** goes only to Credential Manager or Keychain, never to `settings.json` (tested). It's typed by the person, as AGENTS.md requires.
- **The fingerprint warning works:** a declined change keeps the old pin and pauses auto-reconnect (tested).
- **The first-launch agreement is present and versioned.** The update check is a single GET that only shows a button.
- **`.gitignore` covers `vendor/`** and the generated `SdkNames.g.cs`, and neither ever appeared in history.

---

## Tests

```
dotnet test tests/CamRemote.Tests/CamRemote.Tests.csproj     # 156 tests, about 10 s, no camera or SDK
python -m unittest discover -s tests -p "test_*.py"           # 7 tests for gen_names.py
```

- `FakeCamera` (in `Harness.cs`) behaves like CamBridge: the same JSON, return codes and event thread. It records every call, toggles recording on "Down" like the camera does, and can be made to refuse, ignore or throw.
- `TestUi` is a real single UI thread whose timers only fire when a test says so, so nothing depends on timing.
- The test build generates `SdkNames.g.cs` from `FakeSdk/FakeSdk.h`, with made-up numbers rather than Sony's, using the real `gen_names.py`. So the generator is exercised on every run.

| Area | What's covered |
|---|---|
| Connection | connect, taking control, refused connections, auto-reconnect pause after a wrong password, dropped links clearing stale values |
| Recovery | dials taking control 5 times → link restart; camera not answering → link restart |
| Settings | quick clicks coalescing to the newest value; refused settings; base ISO; meter; magnifier; focus position |
| Focus | tap → 640×480 grid; focus-once in MF; focus-step clamp; camera focus boxes, including malformed data |
| Recording | record down/up |
| CameraConnect | camera found by MAC; saved-IP fallback; fingerprint accept, decline and first use; unreachable camera; IP validation |
| Phone | SonyRemote validation and record idempotency; PhoneServer auth, header, 409 messages, frame long-poll, address filter |
| Pure code | Format (F-number, ISO, shutter, battery, duration…), CameraModels prefix matching, PictureZoom, PictureAssist (histogram, zebras, peaking crisp vs soft, specks, bounds), Settings round trip and phone secrets, UpdateCheck version comparison |

Not covered yet: the WPF and Avalonia windows, `LiveViewFeed` (R14), `VideoFeed` and `AudioMeter` (hardware), and the native bridge.

---

## Feature roadmap: what Sony's SDK offers that CamRemote doesn't use yet

Based on Sony's public SDK feature list and release notes. Check each item against the API reference for the A7S III before building it.

1. **Recording start/stop events** (SDK 1.14+). Use them for the record state instead of polling. This also removes the delay behind F1.
2. **Read only what changed** (R12). It's the cheapest win for live-view smoothness on weak Wi-Fi.
3. **Clip review and transfer.** "Remote transfer" or content transfer can list the clips on the card and pull thumbnails, or a proxy, to the computer or phone. That allows checking a take without walking to the camera. It needs the remote-transfer connection mode, and the error 0x8219 seen in the wild suggests it isn't supported over every link.
4. **Stills.** Half-press and full-press shutter (S1/S2 + Release) and save to PC, for photographers who use the A7S III or other listed bodies for stills. Also continuous shooting, and the drive-mode property.
5. **Power zoom.** A zoom-operation property for PZ lenses and the FX bodies, as a rocker like the focus ring.
6. **Exposure and colour presets.** Picture Profile choice, custom white balance capture (point the camera at grey, press a button), and ND on the FX6.
7. **Recording setup panel.** File format, frame rate, record setting, proxy on/off, timecode preset and run mode. Some are already read for display.
8. **Card tools.** Format a card (behind a clear confirmation) and warnings for remaining shots and time (partly done).
9. **Set the camera clock from the computer.** It keeps clip timestamps aligned across cameras.
10. **USB-C connection.** The SDK and the enumerate code already see USB. It gives a steadier link and faster live view for studio use.
11. **Several cameras.** `IRemoteCamera` and the phone page's tabs were already designed for more than one. CamBridge holds one handle globally, and would need a per-camera handle table.
12. **Save and load camera settings.** Camera setting file transfer, to restore a known look after a reset.
