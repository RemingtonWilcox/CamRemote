<div align="center">

<img src="app/CamRemote/phone-icon.png" width="96" alt="Egret icon" />

# Egret

**Run your Sony camera from anywhere.**<br>
Live picture, focus, exposure, video, photos, slow motion and time-lapse,<br>
all in one clean window on your computer, with no cables.

<br>

<a href="https://github.com/RemingtonWilcox/Egret/releases/latest/download/CamRemote-Windows-Setup.exe"><img src="https://img.shields.io/badge/Download_for_Windows-0078D4?style=for-the-badge" alt="Download for Windows" height="44" /></a>
&nbsp;
<a href="https://github.com/RemingtonWilcox/Egret/releases/latest/download/CamRemote-Mac-AppleSilicon.dmg"><img src="https://img.shields.io/badge/Download_for_Mac-555555?style=for-the-badge" alt="Download for Mac" height="44" /></a>

<sub>Free and open source. No account, no subscription, no tracking. <a href="https://github.com/RemingtonWilcox/Egret/releases/latest/download/CamRemote-Mac-Intel.dmg">Older Intel Mac? Use this one.</a></sub><br>
<sub>Tested on the Sony A7S III. <a href="#which-cameras-work">Which cameras work?</a></sub>

<br><br>

<img src="docs/window.jpg" alt="Egret's window: a cattle egret beside a red ginger flower in the live picture, the histogram and meter in the corner, and the Video tab on the right with the Record button, four saved setups and exposure" width="860" />

</div>

<br>

## Your whole camera, in one window

Everything you'd reach for on the camera is a click away, and the picture stays big in the middle.

<table>
<tr>
<td width="33%" valign="top">

### See it

The live picture over Wi-Fi, or full quality through an HDMI capture card. Histogram, zebras, peaking, a grid and the camera's own meter switch on from the bar under the picture.

</td>
<td width="33%" valign="top">

### Focus it

Click the picture to focus or track. Drag the focus ring, jump along the focus bar, or magnify to check what's sharp.

</td>
<td width="33%" valign="top">

### Shoot it

Video, photos, bursts, brackets, S&Q slow motion, time-lapse and long exposures. Save to the card, your computer, or both.

</td>
</tr>
</table>

<table>
<tr>
<td width="50%" valign="top">
<img src="docs/peaking.jpg" alt="Close-up of the egret's head and neck in Egret, outlined in blue where the picture is sharp" />
</td>
<td width="50%" valign="top">

### See what's sharp

Click the bird and the camera focuses there. Turn on **Peaking** and the sharp edges light up, like the egret's eye and neck here, so you can tell what's in focus from across the yard. The camera's own focus boxes show what it's locked on, and the magnifier zooms in to check.

</td>
</tr>
</table>

### Clean, and quick

Three tabs, nothing hidden in menus. **Video** and **Photo** switch the camera for you and keep what you change while shooting right in front of you. Anything you only set once lives in **Setup**, out of the way.

The histogram and meter float over the corner of the picture: drag them anywhere, drag the corner to resize, and click for zebra and peaking levels. Settings your camera doesn't have are left out, so you only see what works. The window opens at the picture's shape, with no black bars, and comes back where you left it.

The live picture runs at around 20 frames a second over Wi-Fi on the A7S III. If the Wi-Fi drops, the camera keeps recording and the app reconnects by itself.

<p align="center">
<img src="docs/tabs.png" alt="Egret's three tabs side by side: Video with the Record button and four saved setups, Photo with Take photo and the Drive choices, and Setup with saving, photo and focus settings" width="760" />
</p>

<br>

## Run it from anywhere

Hide the camera by a feeder, up a tree or across the yard, and run it from wherever you are.

<table>
<tr>
<td width="50%" valign="top">

### From your phone

Tick **Allow phone control** in **Setup** and open the address it shows on your phone. You get the live picture, record, tap to focus, the focus ring and the camera's settings. It works on your home Wi-Fi, and from anywhere with [Tailscale](https://tailscale.com).

</td>
<td width="50%" valign="top">

### From a computer, through your iPhone

No Wi-Fi where the camera is? Put it on your iPhone's hotspot, open the Egret iPhone app, and run it from a computer somewhere else over Tailscale. This comes with the [iPhone app](#coming-soon-egret-for-iphone), which isn't out yet.

</td>
</tr>
</table>

<br>

## Your setups, one click away

**C1 to C4** save how the camera is set right now: mode, exposure, focus, and for video the file format and frame rate. The Video and Photo tabs each have their own four. Each button shows what's in it, like **S&Q 240fps** in XAVC S HD with its shutter, iris and ISO, and turns green while the camera matches it.

**S&Q slow motion** is right on the Video tab. Pick how fast the camera shoots and how fast it plays back: 240fps played at 24p is ten times slower. Save that to C1 and slow motion is one click away.

<p align="center">
<img src="docs/presets.png" alt="The Video tab with four saved setups: 24p, 60p and 120p in XAVC HS 4K, and S&Q 240fps in XAVC S HD, each with its shutter, iris and ISO" width="345" />
</p>

<br>

## Photos, time-lapse and long exposures

The Photo tab has three ways to shoot:

- **Photo:** pick **Single**, **Burst**, **Timer** or **Bracket** under **Drive**, then just the choices for that one: burst speed, timer delay, or bracket step and number of photos.
- **Time-lapse:** pick how often and how many, and press **Start time-lapse**. The app tells you how long it'll shoot and how long the clip will play. It takes one photo every 1 to 60 seconds, up to 9,999 photos, using the camera's own interval shooting.
- **Long exposure:** pick a length from 1 second to 8 minutes. Up to 30 seconds the camera times it. Longer, the app holds the shutter open on Bulb, so keep the window open until it's done. Tell it the ND filter on your lens and it suggests a length from the meter.

<p align="center">
<img src="docs/photo-modes.png" alt="The Photo tab three ways: Photo with a 1-stop, 3-photo bracket, Time-lapse with one photo every 10 seconds, and Long exposure set to 30 seconds with ND filter choices" width="760" />
</p>

<br>

## It can even film wildlife for you

Tick **Automatic recording** and walk away. The app watches the picture, starts recording when something shows up, focuses on it, and stops once it's been gone for a while.

- **Record when it sees:** any **Animal**, a **Bird** only, or a **Person**.
- **Stop once it's been gone for:** 10 seconds, 15 seconds, 30 seconds or a minute.
- **Focus on it:** on or off.

Here it has spotted the egret, started recording, put a green box around it and a red outline around the picture. The check runs on your computer, and no pictures leave it.

<p align="center">
<img src="docs/automatic-recording.jpg" alt="Automatic recording in Egret: a green box labelled animal around the egret, a red recording outline, REC 00:00:09 at the top left, and Animal, 15 s and Focus on it picked on the right" width="860" />
</p>

<br>

## Coming soon: Egret for iPhone

Run the camera with no computer at all, just your phone and its hotspot. The iPhone app has the live picture, tap to focus, exposure, video, S&Q, photos, burst, time-lapse, C1 to C4 saved setups and the animal trigger, and it can hand the camera to a computer anywhere over Tailscale.

<p align="center">
<img src="docs/iphone.jpg" alt="Egret for iPhone three ways: recording 60p at a bird feeder with the C1 to C4 setups below, the time-lapse planner set to two hours at one photo every 5 seconds for a one-minute clip, and Computer control on while a computer runs the camera at 120p" width="860" />
</p>

**You can't download it yet.** It's still being tested, and it'll come to TestFlight, Apple's beta testing app, first. [More about it](#on-your-iphone).

<br>

## Everything it does

| | |
|---|---|
| **See** | Live view over Wi-Fi, or full quality through an HDMI capture card. Rotate it for a camera on its side |
| **Focus** | Click the picture to focus or track, in MF or DMF drag the focus ring or jump along the focus bar. Focus boxes show what the camera is doing |
| **Check focus** | Focus magnifier and peaking |
| **Expose** | ISO, shutter and iris, with a histogram, zebras, the camera's meter and one-click base ISO on the A7S III |
| **Record** | One big button, with a red outline around the picture while rolling |
| **Slow motion** | S&Q, up to 240fps in HD on the A7S III |
| **Shoot photos** | Single shots, bursts, self-timer, brackets, time-lapses and long exposures, saved to your card, your computer or both |
| **Save your setups** | **C1** to **C4**, four for video and four for photos, each back with one click |
| **Set up the camera** | Video format, frame rate, Super 35 crop, RAW or JPEG, white balance, audio and more, without touching a menu |
| **Use both cards** | See what's left on each slot, and format a card from the app. It asks first |
| **Run it from anywhere** | Phone control at home or over Tailscale, and soon from a computer through the iPhone app |
| **Film wildlife** | **Automatic recording** starts and stops for animals, birds or people |
| **Stay informed** | Battery and card warnings before you run out |
| **Stop worrying** | Reconnects by itself, and the camera keeps recording if Wi-Fi drops |
| **Get help** | **Send log to the developer** in **Setup** sends what went wrong in one click, only when you press it |

**Handy keys:** **H** histogram · **Z** zebras · **P** peaking · **G** grid · **R** rotate · **F9** record, take a photo or start a long exposure (on a Mac, you may need to hold **fn**)

<br>

## Set it up in 3 steps

Allow about 10 minutes. You only do this once.

| | | |
|---|---|---|
| **1** | **[Install the app](#1-install-the-app)** | 2 minutes |
| **2** | **[Set up your camera](#2-set-up-your-camera)** | 5 minutes |
| **3** | **[Connect](#3-connect)** | 1 minute |

> [!TIP]
> **Have an AI assistant?** It can walk you through all of this. [Here's what to paste](#let-your-ai-do-it).

**You'll need:** [a supported camera](#which-cameras-work), Wi-Fi at the place you're shooting, and a Windows 11 PC or a Mac on macOS 14.1 or newer.

<br>

## Which cameras work?

Egret is tested on the **A7S III**. Sony says its remote software can control the cameras below over Wi-Fi, so they should work too, but nobody has tried them yet.

| | |
|---|---|
| **Alpha full frame** | A1 II · A1 · A9 III · A7R VI · A7R V · A7CR · **A7S III (tested)** · A7 V · A7 IV · A7C II |
| **Alpha APS-C** | a6700 |
| **Cinema Line** | FX6 (firmware 3.00 or later) · FX3 (firmware 2.00 or later) · FX3A · FX2 · FX30 · BURANO |
| **Vlogging** | ZV-E1 · ZV-E10 II |
| **Camcorders** | PXW-Z300 · PXW-Z200 · HXR-NX800 |
| **Other** | RX1R III · ILX-LR1 |

Update your camera to its latest firmware first. Some features, such as click to focus, may behave differently on your camera.

**A9 II:** Sony doesn't list it for Wi-Fi, but a user has it connected, with click to focus working. On the camera, turn **Ctrl w/ Smartphone** off, join your Wi-Fi under **Wi-Fi Settings › Access Point Set.**, then under **PC Remote Function** turn **PC Remote** on, set **PC Remote Cnct Method** to **Wi-Fi Access Point**, and choose **Pairing** while Egret is searching. To click to focus, set **Focus area** to **Spot S**, **Spot M** or **Spot L** (the camera calls them Flexible Spot). Time-lapse and card formatting don't work on it.

**Won't work over Wi-Fi:** A7R IV, A7R IVA, A7C, RX0 II, BRC-AM7 and FR7. Cameras not listed at all, such as the A7 III or a6400, aren't supported by Sony's software.

<br>

## 1. Install the app

<details open>
<summary><b>On Windows</b></summary>

<br>

1. Open **CamRemote-Windows-Setup.exe** from your downloads.
2. If a blue **Windows protected your PC** box appears, click **More info**, then **Run anyway**. It shows up because the app is new and free, not because anything is wrong.
3. Egret installs itself and puts an icon on your desktop.

</details>

<details open>
<summary><b>On a Mac</b></summary>

<br>

1. Open **CamRemote-Mac** from your downloads.
2. Drag **Egret** onto the **Applications** folder.
3. Open Egret from Applications. If your Mac asks whether you're sure, click **Open**.

Not sure which Mac you have? Click the Apple menu at the top left, then **About This Mac**. If it says **Chip: Apple**, use the main download. If it says **Processor: Intel**, use the Intel one.

</details>

The first time it opens, you'll see a short note about Sony's terms. Click **OK** or **I agree** to continue.

<br>

## 2. Set up your camera

Close Sony's **Monitor & Control** app first. The camera only talks to one remote at a time.

These are the A7S III's menus. Most newer Sony cameras are very similar. If yours look different, search your camera's Help Guide for the name in bold.

On the camera, press **MENU**, then:

| Do this | Where to find it |
|---|---|
| **Join your Wi-Fi.** Pick your network and type its password. | Network › Wi-Fi › Wi-Fi Connect › **On**, then **Access Point Set.** |
| **Allow remote control.** | Network › Cnct./Remote Sht. › Remote Shoot Function › Remote Shooting › **On** |
| **Find your login.** Leave **Access Authen.** on. Then take a photo of the **user** and **password** on **Access Authen. Info**. You'll need them in a minute. | Network › Network Option › Access Authen. Settings |
| **Turn on touch.** This lets you click the picture to focus. | Touch Operation › **On** |
| **Keep it awake.** Otherwise the camera sleeps after a minute and the app can't find it. | Setup › Power Setting Option › Power Save Start Time › **30 Min.** |

> [!NOTE]
> Menu names can differ on older firmware. If yours don't match, update the camera first, using the firmware from your country's Sony support site. Egret was tested on the A7S III with firmware 5.01.

<br>

## 3. Connect

1. Make sure your computer and camera are on the **same Wi-Fi**.
2. Open Egret and click **Connect camera…**
3. Click your camera in the list.
4. Type the **user** and **password** from your photo.
5. Click **Connect**.

If your computer asks about network access, say yes:

- **Windows:** tick **Private networks** and click **Allow**.
- **Mac:** click **Allow** when it asks about your **local network**. Without this, the app can't find the camera.

**That's it.** Your picture appears, and from now on Egret reconnects by itself whenever you open it.

<br>

## On your iPhone

There's also an iPhone app that runs the camera with no computer at all, just your phone's hotspot. It has the live picture, tap to focus, exposure, video, S&Q, photos, burst, time-lapse and C1 to C4 saved setups. Photos and video stay on the camera's card. Close Egret on your computer first: the camera only takes one remote at a time.

It can also record when an animal shows up, with the same rules as on a computer: turn on **Automatic recording** on the Video page and leave **Record when it sees** on **Animal**. **It only works while Egret is open on the phone's screen.** iPhones stop apps that aren't on screen, so if you lock the phone or switch apps, it stops watching (and stops a take it started). While the option is on, the app keeps the screen awake, so keep the phone plugged in: a screen that's on for hours, the hotspot and the detector all use battery. The first time you turn it on, the detector takes up to a minute to get ready.

**Control it from a computer through the phone.** Leave the camera outside on your phone's hotspot and run it from a computer somewhere else. Both need [Tailscale](https://tailscale.com) signed in to the same account. In the iPhone app, open **Setup** and turn on **Allow control from a computer**. Then, on the computer, open the Tailscale address it shows and type the access code. You get the same page as phone control on a computer: live picture, record, tap to focus, the focus ring and camera settings.

- **It only works while Egret stays open on the iPhone's screen.** iOS pauses apps in the background, so locking the phone or switching apps cuts the computer off until you come back. While the switch is on, the screen stays on, so keep the phone plugged in.
- **It uses mobile data.** The computer gets a smaller picture than the phone shows: 640 pixels wide, and 1, 3 or 8 pictures a second, which you pick in Setup. That's about 100, 280 or 750 MB an hour while a computer is watching, and nothing while none is. Setup shows how much it has really sent.
- **The access code** stays hidden until you tap **Show code**. **New code** signs out every computer that used the old one. Only Tailscale and local network addresses are answered.

**You can't download it yet.** It's still being tested, and it'll come to TestFlight, Apple's beta testing app, first. If you have a Mac and a paid Apple developer account, you can [build it yourself](#build-the-iphone-app).

<br>

## Let your AI do it

Using ChatGPT, Claude or another assistant? Paste this in, and it'll guide you one step at a time, from the camera menus to your first connection:

```text
Help me set up Egret for my Sony camera, one step at a time.
Read the guide at https://github.com/RemingtonWilcox/Egret/blob/main/AGENTS.md
Ask which camera I have and whether I'm on Windows or Mac, then walk me through it in plain language.
Don't ask me for my camera password.
```

<br>

## Something not working?

**Send the developer a log.** In **Setup**, under **Something not working?**, say what happened if you like and click **Send log to the developer**. You'll get a short reference, like K7Q2MX, to quote when you get in touch. Nothing is sent until you click. The log includes your camera's model and its address on your home network, but no passwords, photos or video, and it's kept for 90 days.

<details>
<summary><b>My camera isn't in the list</b></summary>

<br>

Check that the camera is on, on the same Wi-Fi as your computer (not a guest network), and has **Remote Shooting** on. If it still doesn't appear, find its address under Network › Wi-Fi › **Display Wi-Fi Info.**, type that **IP address** into the box in Egret and pick your camera underneath.

On a Mac, also check System Settings › Privacy & Security › **Local Network**, and turn Egret on.

</details>

<details>
<summary><b>It says the user or password is wrong</b></summary>

<br>

Use the exact **user** and **password** from **Access Authen. Info**. Capitals matter, and the user often isn't `admin`.

</details>

<details>
<summary><b>It won't connect while Monitor & Control is open</b></summary>

<br>

Close Sony's phone app. The camera only takes one remote at a time.

The same goes for Egret itself: the computer and the iPhone app can't both be connected. Each keeps trying while it's open, so if the phone lets go (the screen locks, or you switch apps), the computer takes the camera back. Close Egret on the computer before you use the phone.

</details>

<details>
<summary><b>The picture is choppy or keeps dropping</b></summary>

<br>

The camera's Wi-Fi signal is weak. Move your router or a mesh point closer to the camera. Recording carries on in the camera even if the link drops.

</details>

<details>
<summary><b>Clicking the picture doesn't focus</b></summary>

<br>

Check that **Tap does** isn't set to **Off**, and that **Touch operation** is on in **Setup**.

If there's no **Touch operation** in **Setup**, the camera doesn't let the app change it, so make sure **Touch Operation** is on in the camera's own menu.

On the A9 II, set **Focus area** to **Spot S**, **Spot M** or **Spot L** (the camera calls them Flexible Spot). A click then moves the spot there and focuses.

</details>

<details>
<summary><b>The camera dials stopped working</b></summary>

<br>

While connected, Egret is in charge of the settings. The dials go back to normal when you disconnect.

</details>

<details>
<summary><b>An update won't install because Egret is in use</b></summary>

<br>

Versions before 1.0.9 could leave a hidden copy running after you closed them, while they were still looking for the camera. Restart your computer, then open the installer again. Newer installers work around it on their own.

</details>

<details>
<summary><b>It says the camera's fingerprint changed</b></summary>

<br>

That's normal after resetting the camera or changing its Access Authen. settings. If you did neither, choose **Don't connect**.

</details>

<br>

## Good to know

- **Only tested on the A7S III.** The other cameras on the list should work, but nobody has tried them yet.
- **Warranty:** Sony's terms say a camera controlled through an app like this is no longer covered by its manufacturer warranty.
- **Egret isn't made by, endorsed by or supported by Sony.**

<br>

## Specifications

| | |
|---|---|
| **Computer** | Windows 11 x64, or macOS 14.1 or newer on Apple or Intel. Windows on ARM and Linux aren't supported |
| **Connection** | Wi-Fi on the same network as the camera, with an optional HDMI capture card for the picture |
| **Wi-Fi live view** | 1024×576 at around 20 frames a second on the A7S III |
| **Photos to your computer** | Full size, or a small JPEG. A full-size RAW took 4 to 12 seconds on the A7S III in our tests, depending on your network |
| **Time-lapse** | One photo every 1 to 60 seconds, up to 9,999 photos, using the camera's own interval shooting |
| **Animal detection** | Runs on your computer once a second. It knows birds, common mammals and people, and no pictures leave your computer |
| **Phone control** | A web page on your phone, over your home network or Tailscale, protected by an access code. The computer stays on with the app open |
| **Your camera password** | Stays on your computer, in Windows Credential Manager or your Mac's Keychain |
| **Built on** | Sony's Camera Remote SDK v2.02.00 |
| **Price** | Free. The code is MIT licensed |

<br>

## Other cameras

**More Sony cameras.** The A7R IV, A7R IVA, A7C, RX0 II, BRC-AM7 and FR7 are in Sony's software but can't be reached over Wi-Fi. (The A9 II can, [as above](#which-cameras-work).) A wired connection could bring some of them in. It hasn't been built yet.

**Other brands.** Egret only speaks to Sony cameras today. Other makers publish their own developer kits, so the same idea could work there, but each brand would need its own connection built and tested from scratch:

| | |
|---|---|
| **Canon** | [EDSDK and CCAPI](https://www.canon-europe.com/pro/stories/eos-digital-sdk-explained/). CCAPI works over Wi-Fi on supported EOS cameras |
| **Fujifilm** | [X and GFX camera control SDK](https://www.fujifilm-x.com/en-sg/?p=118519), over USB or Wi-Fi |
| **Nikon** | A developer SDK, by sign-up, with support varying by model |

If you'd use one of these, [open an issue](https://github.com/RemingtonWilcox/Egret/issues) and say which camera you have.

<br>

---

<details>
<summary><b>More details and tips</b></summary>

<br>

- **Video, Photo and Setup:** the **Video** and **Photo** tabs switch the camera's mode and hold what you change while shooting. **Setup** holds what you set once or now and then, and shows the photo or video settings to match the camera.
- **ISO / Shutter / Iris:** ◀ ▶ step through values. Click the value to see every choice.
- **Base ISO 640 / 12800:** the A7S III's two native ISOs in S-Log3. 12800 is the cleanest choice in low light. These buttons only appear on the A7S III. On other cameras, use the ISO control.
- **Focus wheel:** works in MF (or DMF). Right is farther, left is nearer. Drag slowly for fine focus and faster for bigger moves.
- **Focus bar:** shows where focus sits between close up and infinity, if your camera and lens report it. Click it to jump there, or hold the flower or mountain button to keep moving.
- **Focus boxes:** the boxes on the picture are the ones the camera reports: green means in focus, and thicker boxes are tracking or a face. The outline of a large focus area isn't drawn. A small circle only shows where you clicked.
- **Focus magnifier:** the A7S III won't magnify over Wi-Fi in movie mode, so the ×2 and ×4 buttons enlarge the preview in the app instead. Click the picture to move around. It's the same preview pixels, so a Wi-Fi picture gets soft; a capture card shows more detail. If your camera does allow it, the buttons use the camera's own magnifier.
- **Click the picture:** works on the Video and Photo tabs. Right-click cancels a touch focus or tracking if the camera is holding one (Control-click on a Mac). In MF it autofocuses once, then returns to MF.
- **Tap does:** Focus, Tracking or Off. Off stops clicks doing anything. Sony's SDK doesn't offer the setting for a remote tap's function on the A7S III, so Tracking may only focus. The app tells you if the camera didn't start tracking.
- **Picture source:** in **Setup**. **Camera over Wi-Fi** needs no cable. Any HDMI capture card your computer sees is listed too. Plugged one in after opening the app? Click **↻**. Set the camera's **HDMI Resolution** to **1080p** and turn **HDMI Info. Display** off. On a Mac, the app asks for Camera and Microphone access the first time you pick one.
- **Format a card:** click **▾** next to the card readout. It works while the camera isn't recording, erases the whole card, and asks first. The app can't tell whether you've copied your footage off, so check that yourself.
- **Rotate:** ⟲ / ⟳ at the right of the bar under the picture, or **R** (Shift+R turns the other way), for a camera mounted on its side. The window reshapes to fit.
- **Histogram and meter:** drag the box to move it anywhere over the picture, and drag its corner to resize it. Click it, or **Levels ▴** under the picture, to set the zebra level and the peaking strength and colour.
- **Drive:** **Single**, **Burst**, **Timer** or **Bracket**, then that one's choices. For a bracket, **One press** takes the whole set and **Press each** takes one photo of it per press, and **2+** and **2−** take the normal photo and one brighter or darker. Other drive modes your camera has, like white balance brackets, are under **▾**.
- **Burst:** pick **Burst** and a speed under **Drive**, then hold **Take photo** down.
- **Long exposure:** needs the camera in **M**. Up to 30 seconds the camera times it itself. Longer ones use Bulb, with this app holding the shutter, so keep the window open and the computer awake until it's done. With **Long Exposure NR** on, the camera then takes as long again to finish. When you go back to **Photo**, your shutter speed comes back.
- **Super 35 crop:** in **Setup**, on cameras that have it, like the A7S III and FX3. No Sony camera in this list records Open Gate over Sony's software.
- **C1 to C4:** click an empty one to save how the camera is set now: mode, exposure, focus, and for video the format and frame rate. Click it again later to put all of that back. The Video and Photo tabs each have their own four. A preset turns green while the camera matches it. Right-click to save over it or clear it (Control-click on a Mac). If the camera's mode dial is somewhere a preset can't reach, the app says so.
- **S&Q (slow and quick motion):** under **Recording** on the Video tab, switch between **Normal** and **S&Q**. In S&Q, **S&Q shoots at** is how many frames a second the camera captures, and **S&Q plays at** is the speed it's played back at, so 240fps played at 24p is ten times slower. The fastest rates need an HD file format. Save one to a preset, like 1080p at 240fps on C1, to get to it in one click.
- **Time-lapse:** pick **Time-lapse** at the top of the Photo tab. The big button then says **Start time-lapse**, and **Stop time-lapse** once it's running.
- **Photos on your computer:** choose **Computer**, **Card** or **Both** under **Saving** in **Setup**. They go to `Pictures/CamRemote` until you click **Change folder**. A full-size RAW can take 4 to 12 seconds to arrive, so for bursts and time-lapses use **Both** with **Small JPEG**: the card keeps the full files.
- **Automatic recording:** turn it on under **Record**, then pick what to watch for, how long to wait after it's gone, and whether to focus on it. It runs on your computer, and no pictures leave it. **Animal** means birds and a few other animals (cats, dogs, horses, sheep, cows, elephants, bears, zebras and giraffes), and something it doesn't know, like a squirrel, may not set it off. **Bird** ignores everything else, and **Person** watches for people instead. A green box follows what it found while the option is on. Set **Rotate** to match how the camera is mounted first: it sees far better the right way up. Takes stop at 5 minutes and start again if the animal is still there. If it keeps being set off by something that isn't an animal, like splashing water, it learns to ignore that spot for 10 minutes. It only stops takes it started itself.
- **Updates:** when the app opens, it asks GitHub once for the latest version number. If there's a newer one, a green button appears at the bottom of the window. It opens the download page and installs nothing by itself.
- **Shooting guides:** worked out from the preview, not the recorded file, so a Monitor LUT changes them. At Wi-Fi preview size, peaking shows the plane of focus. Use the magnifier for critical focus.
- **Recording timer:** keeps counting through a dropped link or an app restart. A take already rolling when the app first sees it shows a **+**.
- **Phone control:** works on your home network and over Tailscale, and needs the access code. The app hides it until you click **Show code**, and **New code** replaces it and signs phones out. The computer must stay on with the app open. On an iPhone, use Share › Add to Home Screen for a full-screen app.
- **Audio:** live meters need HDMI through a capture card, because the camera sends no sound over Wi-Fi. Aim for loud sounds peaking around −12 dB and never reaching 0.
- **Keeping the same address:** reserve the camera's IP address in your router so it never changes. The app usually finds the camera again anyway.

</details>

<details>
<summary><b>Build it yourself (for developers)</b></summary>

<br>

You'll need Sony's Camera Remote SDK from [their site](https://support.d-imaging.sony.co.jp/app/sdk/en/index.html). Unzip the version for your system into `vendor/CrSDK` so `vendor/CrSDK/RemoteCli.zip` exists. Never commit anything under `vendor/`.

The animal detector needs one more file, which the app can be built without (the option just won't appear). Save it as `vendor/BirdModel/rfdetr_nano_fp16.onnx`:

```bash
curl -L --create-dirs -o vendor/BirdModel/rfdetr_nano_fp16.onnx https://huggingface.co/onnx-community/rfdetr_nano-ONNX/resolve/main/onnx/model_fp16.onnx
```

The iPhone app uses the full-size version of the same model instead, because Core ML can't run the half-size one. It adds about 110 MB to the app:

```bash
curl -L --create-dirs -o vendor/BirdModel/rfdetr_nano.onnx https://huggingface.co/onnx-community/rfdetr_nano-ONNX/resolve/main/onnx/model.onnx
```

**Windows:** install Git, CMake, the .NET 10 SDK, Python and Visual Studio's C++ tools, then:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

**Mac:** install Xcode's command line tools, then CMake, Python and the .NET 10 SDK, then:

```bash
./build-mac.sh
```

<a id="build-the-iphone-app"></a>**iPhone:** it doesn't use Sony's SDK. The only thing it takes from `vendor/` is the animal detector's `rfdetr_nano.onnx`, and it builds without it. On a Mac with Xcode, install CMake and XcodeGen, then:

```bash
cd app/CamRemote.iOS
./build-deps.sh
DEVELOPMENT_TEAM=<your team id> ./build-iphone.sh
```

Run these in Terminal on the Mac itself, so signing can reach your Keychain. Then install the app on your phone from Xcode's **Devices** window.

**Making the downloads:** `package-windows.ps1` builds the installer with Inno Setup. `package-mac.sh` builds both notarized disk images, and its comments explain the one-time setup. Upload those files and `libusb-source.zip` to a GitHub release.

**How it's put together:**

- `native/CamBridge/`: a small C++ library that wraps Sony's SDK in a flat C API returning JSON. `MacSupport.mm` adds the Mac's Keychain, keep-awake, capture and audio code.
- `app/CamRemote/`: the Windows app (WPF, .NET 10) and the camera code both apps share.
- `app/CamRemote.Mac/`: the Mac app (Avalonia, .NET 10). A change to a window or control usually needs making in both.
- `app/CamRemote.iOS/`: the iPhone app (SwiftUI). It talks to the camera directly over SSH and PTP/IP, and shares no code with the others. `AnimalTrigger.swift` follows the same rules as `BirdTrigger.cs`, so a change to one usually needs making in both.
- `tools/`: generates names from the SDK headers, draws the icon, writes the notices and holds the packaging settings.

Notes from Sony's SDK reference (v2.02.00) for the A7S III:

- Remote touch focus acts by itself in movie mode only. In photo mode the app half-presses the shutter to make the camera focus. Focus positions are a 640×480 grid over the picture.
- MovieRecord "Down" toggles recording; Sony asks for "Up" right after it.
- Over Wi-Fi the camera reports battery in bars, not percent, so the app shows a range.
- There's no live audio level over the SDK.

</details>

<details>
<summary><b>Licence</b></summary>

<br>

Egret's code is MIT licensed (see `LICENSE`). The downloads for Windows and Mac include Sony's Camera Remote SDK libraries under [Sony's licence](https://support.d-imaging.sony.co.jp/app/sdk/licenseagreement/en-US.html), plus open-source parts listed in `NOTICES.md`. The SDK isn't in this repository, and the iPhone app doesn't use it.

</details>
