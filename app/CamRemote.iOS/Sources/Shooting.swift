import SwiftUI

private let tiles: [(code: UInt16, label: String)] = [
    (Code.shutter, "Shutter"), (Code.aperture, "Iris"), (Code.iso, "ISO"), (Code.exposure, "Exp"), (Code.focusMode, "Focus"),
]
private let focusShort = ["Manual": "MF", "Single": "AF-S", "Continuous": "AF-C", "Auto": "AF-A", "Direct manual": "DMF"]

/// Upright, the picture stays put with the controls under it. Turned sideways, it's the picture and little else.
struct ShootingView: View {
    @ObservedObject var camera: Camera
    @AppStorage("pictureTurn") private var pictureTurn = 0
    @AppStorage("showGrid") private var showGrid = false
    @AppStorage("showHistogram") private var showHistogram = false
    @AppStorage("showPeaking") private var showPeaking = false
    @AppStorage("showZebra") private var showZebra = false
    @AppStorage("showExposure") private var showExposure = true
    @AppStorage("showSettings") private var showSettings = true
    @AppStorage("showStatus") private var showStatus = true
    @State private var fullScreen = false
    @State private var selected = Code.shutter
    @State private var dialOpen = false
    @State private var chooser: Row?
    @State private var setupOpen = false
    @State private var timeLapseOpen = false
    @State private var pressing = false
    @State private var shooting = false
    @State private var blink = 0.0
    @Environment(\.scenePhase) private var phase

    var body: some View {
        Group {
            if fullScreen { fullScreenView } else { uprightView }
        }
        // A call or the button changing mid-press never reports the finger lifting, and the camera would keep firing.
        .onChange(of: phase) { if $0 != .active { letGoOfShutter() } }
        .onChange(of: camera.photoMode) { _ in letGoOfShutter() }
        .onChange(of: camera.timeLapseRunning) { _ in letGoOfShutter() }
        .overlay(alignment: .bottom) { toast }
        .sheet(item: $chooser) { row in
            ChoiceSheet(row: row) { camera.set(row.code, $0) }
                .presentationDetents([.medium, .large])
        }
        .sheet(isPresented: $setupOpen) { SetupSheet(camera: camera) }
        .sheet(isPresented: $timeLapseOpen) { TimeLapseSheet(camera: camera) }
        .onAppear(perform: syncGuides)
        .onChange(of: pictureTurn) { _ in
            if fullScreen { turn(toFullScreen: true) }
        }
        .onChange(of: showHistogram) { _ in syncGuides() }
        .onChange(of: showZebra) { _ in syncGuides() }
        .onChange(of: showPeaking) { _ in syncGuides() }
        .onDisappear {
            if fullScreen { turn(toFullScreen: false) }
        }
    }

    /// Only the buttons do this, so moving the phone around never changes the view. A vertical picture stays upright.
    private func turn(toFullScreen on: Bool) {
        fullScreen = on
        dialOpen = false
        let orientations: UIInterfaceOrientationMask = on && !vertical ? .landscape : .portrait
        AppDelegate.orientations = orientations
        guard let scene = UIApplication.shared.connectedScenes.first as? UIWindowScene else { return }
        scene.keyWindow?.rootViewController?.setNeedsUpdateOfSupportedInterfaceOrientations()
        scene.requestGeometryUpdate(.iOS(interfaceOrientations: orientations))
    }

    private var vertical: Bool { pictureTurn != 0 }

    @ViewBuilder private var batteryAndCard: some View {
        Text(camera.battery).foregroundColor(camera.lowBattery ? .warn : nil)
        Text(camera.card).foregroundColor(camera.lowCard ? .warn : nil)
    }

    // MARK: Upright

    private var uprightView: some View {
        VStack(spacing: 8) {
            HStack(spacing: 10) {
                Circle().fill(Color.good).frame(width: 8, height: 8)
                batteryAndCard
                Spacer()
                Button { setupOpen = true } label: {
                    Label("Setup", systemImage: "gearshape").font(.footnote.weight(.semibold))
                }
                .foregroundColor(.white)
            }
            .font(.footnote)
            .foregroundColor(.dim)
            .padding(.horizontal, 12)

            liveView
                .aspectRatio(vertical ? 1 : 3 / 2, contentMode: .fit)
                .overlay(alignment: .top) {
                    VStack(alignment: .trailing, spacing: 4) {
                        SettingsBar(camera: camera).frame(maxWidth: .infinity, alignment: .leading)
                        if showHistogram { HistogramView(camera: camera) }
                    }
                    .padding(6)
                    .allowsHitTesting(false)
                }
                .overlay(alignment: .bottom) {
                    HStack(spacing: 4) {
                        guideButtons
                        Spacer()
                        roundIcon("arrow.up.left.and.arrow.down.right") { turn(toFullScreen: true) }
                    }
                    .padding(8)
                }

            VStack(spacing: 8) {
                tileRow(background: .panel)
                dial
                HStack {
                    modeSwitch.frame(maxWidth: .infinity)
                    captureButton(size: 72)
                    captureNote.frame(maxWidth: .infinity)
                }
                ScrollView {
                    ChipGrid {
                        ForEach(1...3, id: \.self) { slot in
                            PresetChip(camera: camera, slot: slot)
                        }
                        chip(Code.mode)
                        if camera.photoMode {
                            chip(Code.drive)
                            Button { timeLapseOpen = true } label: {
                                ChipFace(label: "Time-lapse", value: camera.timeLapseRunning ? "Running" : "Plan", on: camera.timeLapseRunning)
                            }
                            .buttonStyle(Press())
                            chip(Code.whiteBalance)
                            colorTempChip
                            chip(Code.metering)
                        } else {
                            chip(Code.frameRate)
                            chip(Code.movieFormat)
                            chip(Code.recordSetting)
                            chip(Code.whiteBalance)
                            colorTempChip
                        }
                        chip(Code.focusArea)
                        SilentChip(camera: camera)
                    }
                }
            }
            .padding(.horizontal, 12)
        }
        .background(Color.page.ignoresSafeArea())
    }

    @ViewBuilder private var colorTempChip: some View {
        if camera.rows[Code.whiteBalance]?.text == "Color temp" { chip(Code.colorTemp) }
    }

    @ViewBuilder private func chip(_ code: UInt16) -> some View {
        if let row = camera.rows[code] {
            Chip(row: row) { chooser = row }
        }
    }

    private var modeSwitch: some View {
        HStack(spacing: 4) {
            modeButton("video.fill", selected: !camera.photoMode) { camera.useMode(photo: false) }
            modeButton("camera.fill", selected: camera.photoMode) { camera.useMode(photo: true) }
        }
    }

    private func modeButton(_ icon: String, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon)
                .frame(width: 46, height: 38)
                .background(selected ? Color.chosen : Color.panel, in: RoundedRectangle(cornerRadius: 8))
        }
        .foregroundColor(selected ? .white : .dim)
        .disabled(selected || camera.recording || camera.timeLapseRunning)
    }

    @ViewBuilder private var captureNote: some View {
        Group {
            if camera.timeLapseRunning {
                Text(camera.timeLapsePlan.map { "Time-lapse: \(camera.timeLapseTaken.formatted()) of \($0.shots.formatted())" } ?? "Time-lapse running")
                    .foregroundColor(.good)
            } else if camera.photoMode {
                Text(shooting ? "Shooting…" : holdFires ? "Hold for burst" : "")
            } else if camera.recordBusy {
                Text("Wait…")
            } else if camera.recording, let since = camera.recordingSince {
                Text(since, style: .timer).foregroundColor(.rec).monospacedDigit()
            }
        }
        .font(.footnote.weight(.semibold))
        .foregroundColor(.dim)
    }

    private var holdFires: Bool {
        let drive = camera.rows[Code.drive]?.text ?? ""
        return drive.hasPrefix("Burst") || drive.hasPrefix("BRK C")
    }

    // MARK: Full screen

    private var fullScreenView: some View {
        Group {
            if vertical { verticalFullScreen } else { sidewaysFullScreen }
        }
        .statusBarHidden()
        .persistentSystemOverlays(.hidden)
        .defersSystemGestures(on: .all) // a drag near an edge shouldn't throw you out of the app
    }

    private var eyeMenu: some View {
        Menu {
            Toggle("Exposure", isOn: $showExposure)
            Toggle("Settings", isOn: $showSettings)
            Toggle("Battery and card", isOn: $showStatus)
        } label: {
            icon("eye", on: false)
        }
    }

    @ViewBuilder private var statusLine: some View {
        if showStatus {
            HStack(spacing: 10) { batteryAndCard }
            .font(.caption)
            .foregroundColor(.white)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(Color.black.opacity(0.55), in: Capsule())
        }
    }

    /// For a camera on its side: the phone stays upright and the tall picture fills it.
    private var verticalFullScreen: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            liveView.ignoresSafeArea()

            VStack(spacing: 6) {
                HStack(spacing: 4) {
                    roundIcon("arrow.down.right.and.arrow.up.left") { turn(toFullScreen: false) }
                    eyeMenu
                    Spacer()
                    guideButtons
                }
                if showSettings { SettingsBar(camera: camera) }
                statusLine
                if showHistogram { HistogramView(camera: camera).frame(maxWidth: .infinity, alignment: .trailing) }
                Spacer()
                if dialOpen {
                    HStack(spacing: 6) {
                        dial
                        roundIcon("xmark") { dialOpen = false }
                    }
                }
                if showExposure { tileRow(background: Color.black.opacity(0.55)) }
                captureNote
                captureButton(size: 68)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
        }
    }

    private var sidewaysFullScreen: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            liveView.ignoresSafeArea()

            HStack {
                VStack(spacing: 10) {
                    roundIcon("arrow.down.right.and.arrow.up.left") { turn(toFullScreen: false) }
                    eyeMenu
                    guideButtons
                    Spacer()
                }
                Spacer()
                VStack(spacing: 8) {
                    Spacer()
                    captureNote
                    captureButton(size: 68)
                    Spacer()
                }
            }
            .padding(.vertical, 12)
            .padding(.horizontal, 8)

            VStack(spacing: 6) {
                if showSettings { SettingsBar(camera: camera) }
                statusLine
                if showHistogram { HistogramView(camera: camera).frame(maxWidth: .infinity, alignment: .trailing) }
                Spacer()
                if dialOpen {
                    HStack(spacing: 6) {
                        dial
                        roundIcon("xmark") { dialOpen = false }
                    }
                }
                if showExposure { tileRow(background: Color.black.opacity(0.55)) }
            }
            .frame(maxWidth: 440)
            .padding(.vertical, 8)
        }
    }

    // MARK: Parts of both

    private var liveView: some View {
        LiveView(camera: camera, turn: pictureTurn, grid: showGrid, blink: blink)
    }

    private func tileRow(background: Color) -> some View {
        HStack(spacing: 4) {
            ForEach(tiles, id: \.code) { tile in
                let picked = selected == tile.code && (!fullScreen || dialOpen)
                Button {
                    withAnimation(.easeOut(duration: 0.15)) { tapped(tile.code) }
                } label: {
                    VStack(spacing: 1) {
                        Text(tile.label).font(.caption2).foregroundColor(.dim)
                        Text(tileValue(tile.code))
                            .font(.callout.weight(.semibold))
                            .monospacedDigit()
                            .foregroundColor(lockedReason(tile.code) == nil ? .white : .dim)
                            .lineLimit(1)
                            .minimumScaleFactor(0.7)
                    }
                    .frame(maxWidth: .infinity, minHeight: 46)
                    .background(picked ? Color.chosen : background, in: RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(Press())
            }
        }
    }

    /// Why the camera is deciding this one for itself right now, if it is.
    private func lockedReason(_ code: UInt16) -> String? {
        let mode = camera.rows[Code.mode]?.text ?? ""
        let auto = mode.hasSuffix("Program") || mode == "Auto"
        switch code {
        case Code.shutter where auto || mode.hasSuffix("Aperture"): return "The camera sets this in \(mode) mode"
        case Code.aperture where auto || mode.hasSuffix("Shutter"): return "The camera sets this in \(mode) mode"
        case Code.exposure where mode.hasSuffix("Manual") && camera.rows[Code.iso]?.text != "ISO AUTO": return "Only works with Auto ISO in Manual"
        default: return nil
        }
    }

    private func tileValue(_ code: UInt16) -> String {
        let text = camera.rows[code]?.text ?? "--"
        return code == Code.focusMode ? focusShort[text] ?? text : text.replacingOccurrences(of: "ISO ", with: "")
    }

    /// The first tap puts a setting on the dial. The next tap lists all its values.
    private func tapped(_ code: UInt16) {
        if selected == code, !fullScreen || dialOpen, let row = camera.rows[code], !row.choices.isEmpty {
            chooser = row
            dialOpen = false
        } else {
            selected = code
            dialOpen = true
        }
    }

    @ViewBuilder private var dial: some View {
        if selected == Code.focusMode {
            Dial(hint: camera.manualFocus ? nil : "Focus ring works in Manual", value: "", perStep: 5, largest: 7, buttonStep: 3, dragRightAdds: true) {
                camera.focusStep($0)
            }
            .id(selected)
        } else {
            Dial(hint: lockedReason(selected), value: tileValue(selected), perStep: 14, largest: 1, buttonStep: 1, dragRightAdds: false) {
                camera.step(selected, by: $0)
            }
            .id(selected)
        }
    }

    @ViewBuilder private var guideButtons: some View {
        guideButton("Hist", on: $showHistogram)
        guideButton("Peak", on: $showPeaking)
        guideButton("Zebra", on: $showZebra)
        guideButton("Grid", on: $showGrid)
        Button {
            pictureTurn = pictureTurn == 0 ? 90 : pictureTurn == 90 ? 270 : 0 // a camera can lie on either side
        } label: {
            Image(systemName: pictureTurn == 270 ? "rotate.left" : "rotate.right")
                .font(.caption.weight(.semibold))
                .foregroundColor(vertical ? .black : .white)
                .frame(width: 36, height: 30)
                .background(vertical ? Color.warn : Color.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 7))
        }
    }

    private func guideButton(_ title: String, on: Binding<Bool>) -> some View {
        Button { on.wrappedValue.toggle() } label: {
            Text(title)
                .font(.caption2.weight(.semibold))
                .foregroundColor(on.wrappedValue ? .black : .white)
                .frame(width: 42, height: 30)
                .background(on.wrappedValue ? Color.warn : Color.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 7))
        }
    }

    private func syncGuides() {
        camera.setGuides(histogram: showHistogram, zebra: showZebra, peaking: showPeaking)
    }

    private func icon(_ name: String, on: Bool) -> some View {
        Image(systemName: name)
            .font(.footnote.weight(.semibold))
            .foregroundColor(on ? .warn : .white)
            .frame(width: 36, height: 36)
            .background(Color.black.opacity(0.55), in: Circle())
    }

    private func roundIcon(_ name: String, on: Bool = false, action: @escaping () -> Void) -> some View {
        Button(action: action) { icon(name, on: on) }
    }

    @ViewBuilder private func captureButton(size: CGFloat) -> some View {
        if camera.timeLapseRunning {
            Button(action: camera.stopTimeLapse) {
                ZStack {
                    Circle().stroke(Color.white, lineWidth: 3)
                    RoundedRectangle(cornerRadius: 6).fill(Color.white).padding(size * 0.28)
                }
                .frame(width: size, height: size)
            }
        } else if camera.photoMode {
            ZStack {
                Circle().stroke(Color.white, lineWidth: 3)
                Circle().fill(Color.white).padding(shooting ? 14 : 6).opacity(shooting ? 0.7 : 1)
            }
            .frame(width: size, height: size)
            .contentShape(Circle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { _ in
                    guard !pressing else { return }
                    pressing = true
                    if camera.shutterDown() { showShot() }
                }
                .onEnded { _ in
                    pressing = false
                    camera.shutterUp()
                    Task {
                        try? await Task.sleep(nanoseconds: 200_000_000) // long enough to see on a quick tap
                        if !pressing { shooting = false }
                    }
                })
        } else {
            Button {
                UIImpactFeedbackGenerator(style: .rigid).impactOccurred()
                camera.toggleRecord()
            } label: {
                ZStack {
                    Circle().stroke(Color.white, lineWidth: 3)
                    RoundedRectangle(cornerRadius: camera.recording ? 6 : size / 2)
                        .fill(Color.rec)
                        .padding(camera.recording ? size * 0.28 : 6)
                }
                .frame(width: size, height: size)
            }
            .disabled(camera.recordBusy)
            .opacity(camera.recordBusy ? 0.4 : 1)
        }
    }

    private func letGoOfShutter() {
        pressing = false
        shooting = false
        camera.shutterUp()
    }

    private func showShot() {
        shooting = true
        UIImpactFeedbackGenerator(style: .rigid).impactOccurred()
        blink = 0.8
        DispatchQueue.main.async {
            withAnimation(.easeOut(duration: 0.25)) { blink = 0 }
        }
    }

    @ViewBuilder private var toast: some View {
        if !camera.message.isEmpty {
            Text(camera.message)
                .padding(10)
                .frame(maxWidth: 420)
                .background(Color.black.opacity(0.85), in: RoundedRectangle(cornerRadius: 10))
                .padding()
                .onTapGesture { camera.message = "" }
                .task(id: camera.message) {
                    try? await Task.sleep(nanoseconds: 3_500_000_000)
                    if !Task.isCancelled { camera.message = "" } // cancelled means a newer message took over
                }
        }
    }
}

/// The camera's picture. Tap it to focus there.
struct LiveView: View {
    @ObservedObject var camera: Camera
    let turn: Int
    let grid: Bool
    let blink: Double
    @State private var focusMark: CGPoint?

    private var sideways: Bool { turn != 0 }

    var body: some View {
        GeometryReader { box in
            let area = pictureArea(in: box.size)
            ZStack(alignment: .topLeading) {
                Color.black
                ZStack {
                    if let frame = camera.frame { Image(uiImage: frame).resizable() }
                    if let marks = camera.guideImage { Image(uiImage: marks).resizable() }
                }
                .frame(width: sideways ? area.height : area.width, height: sideways ? area.width : area.height)
                .rotationEffect(.degrees(Double(turn)))
                .position(x: area.midX, y: area.midY)
                if grid {
                    Path { path in
                        for third in [1.0 / 3, 2.0 / 3] {
                            path.move(to: CGPoint(x: area.minX + area.width * third, y: area.minY))
                            path.addLine(to: CGPoint(x: area.minX + area.width * third, y: area.maxY))
                            path.move(to: CGPoint(x: area.minX, y: area.minY + area.height * third))
                            path.addLine(to: CGPoint(x: area.maxX, y: area.minY + area.height * third))
                        }
                    }
                    .stroke(Color.white.opacity(0.5), lineWidth: 1)
                }
                if let focusMark {
                    Rectangle().stroke(Color.warn, lineWidth: 2).frame(width: 52, height: 52)
                        .position(focusMark)
                }
                Color.black.opacity(blink).allowsHitTesting(false)
                // A frozen picture looks just like a live one, so say so.
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    if context.date.timeIntervalSince(camera.frameTime) > 3 {
                        Text("Waiting for the camera")
                            .font(.footnote.weight(.semibold))
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(Color.black.opacity(0.7), in: Capsule())
                            .position(x: box.size.width / 2, y: box.size.height / 2)
                    }
                }
                .allowsHitTesting(false)
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0).onEnded { touch in
                tapped(touch.location, area: area)
            })
        }
        .overlay(RoundedRectangle(cornerRadius: 6).stroke(camera.recording ? Color.rec : .clear, lineWidth: 3))
    }

    private func pictureArea(in size: CGSize) -> CGRect {
        guard let frame = camera.frame, frame.size.width > 0, frame.size.height > 0 else { return CGRect(origin: .zero, size: size) }
        let across = sideways ? frame.size.height : frame.size.width, tall = sideways ? frame.size.width : frame.size.height
        let scale = min(size.width / across, size.height / tall)
        let width = across * scale, height = tall * scale
        return CGRect(x: (size.width - width) / 2, y: (size.height - height) / 2, width: width, height: height)
    }

    private func tapped(_ point: CGPoint, area: CGRect) {
        guard camera.frame != nil, area.contains(point) else { return }
        focusMark = point
        let x = (point.x - area.minX) / area.width, y = (point.y - area.minY) / area.height
        switch turn {
        case 90: camera.focus(x: y, y: 1 - x) // the camera still counts from its own top left
        case 270: camera.focus(x: 1 - y, y: x)
        default: camera.focus(x: x, y: y)
        }
        Task {
            try? await Task.sleep(nanoseconds: 2_000_000_000)
            if focusMark == point { focusMark = nil }
        }
    }
}

/// What the camera is set to right now, on the picture where you can't miss it.
struct SettingsBar: View {
    @ObservedObject var camera: Camera

    var body: some View {
        HStack(spacing: 7) {
            if camera.recording, let since = camera.recordingSince {
                HStack(spacing: 3) {
                    Circle().fill(Color.white).frame(width: 6, height: 6)
                    Text(since, style: .timer).monospacedDigit()
                }
                .font(.footnote.weight(.bold))
                .padding(.horizontal, 6)
                .background(Color.rec, in: RoundedRectangle(cornerRadius: 4))
            }
            if camera.timeLapseRunning, let plan = camera.timeLapsePlan {
                Text("\(camera.timeLapseTaken.formatted())/\(plan.shots.formatted())")
                    .font(.footnote.weight(.bold))
                    .monospacedDigit()
                    .foregroundColor(.black)
                    .padding(.horizontal, 6)
                    .background(Color.good, in: RoundedRectangle(cornerRadius: 4))
            }
            if let slot = camera.activeSlot {
                Text("C\(slot)")
                    .font(.footnote.weight(.bold))
                    .foregroundColor(.black)
                    .padding(.horizontal, 6)
                    .background(Color.good, in: RoundedRectangle(cornerRadius: 4))
            }
            Text(text(Code.mode).map(Format.modeLetter) ?? "").font(.footnote.weight(.bold))
            if camera.photoMode {
                part(Code.fileFormat, strong: true)
                part(Code.drive)
                part(Code.aspectRatio)
            } else {
                part(Code.frameRate, strong: true)
                part(Code.movieFormat)
                part(Code.recordSetting)
            }
        }
        .foregroundColor(.white)
        .lineLimit(1)
        .minimumScaleFactor(0.7)
        .padding(.horizontal, 6)
        .padding(.vertical, 3)
        .background(Color.black.opacity(0.6), in: RoundedRectangle(cornerRadius: 6))
    }

    private func text(_ code: UInt16) -> String? { camera.rows[code]?.text }

    @ViewBuilder private func part(_ code: UInt16, strong: Bool = false) -> some View {
        if let value = text(code) {
            Text(value).font(strong ? .footnote.weight(.bold) : .caption)
        }
    }
}

struct HistogramView: View {
    @ObservedObject var camera: Camera

    var body: some View {
        VStack(alignment: .trailing, spacing: 1) {
            GeometryReader { box in
                Path { path in
                    guard camera.histogram.count == 256 else { return }
                    for (x, bar) in camera.histogram.enumerated() {
                        let across = box.size.width * CGFloat(x) / 256
                        path.move(to: CGPoint(x: across, y: box.size.height))
                        path.addLine(to: CGPoint(x: across, y: box.size.height * (1 - bar)))
                    }
                }
                .stroke(Color.white.opacity(0.85), lineWidth: 0.6)
                Rectangle().fill(Color.warn)
                    .frame(width: 1)
                    .position(x: box.size.width * CGFloat(zebraPercent) / 100, y: box.size.height / 2)
                    .frame(height: box.size.height)
            }
            .frame(width: 128, height: 44)
            Text(String(format: "Over %d%%: %.1f%%", zebraPercent, camera.overPercent))
                .font(.caption2)
                .foregroundColor(camera.overPercent >= 0.5 ? .warn : .white)
        }
        .padding(4)
        .background(Color.black.opacity(0.6), in: RoundedRectangle(cornerRadius: 6))
        .allowsHitTesting(false)
    }
}

/// One dial for whatever setting is picked. Drag it like a lens ring, or use the arrows for one step.
struct Dial: View {
    let hint: String? // says why it's off
    let value: String
    let perStep: CGFloat
    let largest: Int
    let buttonStep: Int
    let dragRightAdds: Bool
    let send: (Int) -> Bool
    @State private var lastX: CGFloat?
    @State private var owed: CGFloat = 0
    @State private var turned: CGFloat = 0
    private let tick = UISelectionFeedbackGenerator()

    private var enabled: Bool { hint == nil }

    private func move(_ step: Int) -> Bool {
        let sent = send(step)
        if sent { tick.selectionChanged() }
        return sent
    }

    var body: some View {
        HStack(spacing: 4) {
            arrow("chevron.left", step: -buttonStep)
            GeometryReader { box in
                Path { path in
                    var x = turned.truncatingRemainder(dividingBy: 14)
                    while x < box.size.width {
                        path.move(to: CGPoint(x: x, y: 14))
                        path.addLine(to: CGPoint(x: x, y: 30))
                        x += 14
                    }
                }
                .stroke(Color.dim, lineWidth: 1)
                Rectangle().fill(Color.warn).frame(width: 2, height: 30).position(x: box.size.width / 2, y: 22)
            }
            .frame(height: 44)
            .background(Color.panel.opacity(0.85), in: RoundedRectangle(cornerRadius: 8))
            .opacity(enabled ? 1 : 0.5)
            .overlay {
                if let hint {
                    Text(hint)
                        .font(.caption)
                        .foregroundColor(.white)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(Color.panel, in: RoundedRectangle(cornerRadius: 5))
                } else if !value.isEmpty {
                    Text(value)
                        .font(.footnote.weight(.bold))
                        .monospacedDigit()
                        .foregroundColor(.black)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 2)
                        .background(Color.warn, in: RoundedRectangle(cornerRadius: 5))
                        .allowsHitTesting(false)
                }
            }
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { touch in
                    guard enabled else { return }
                    let moved = touch.location.x - (lastX ?? touch.location.x)
                    lastX = touch.location.x
                    turned += moved
                    owed = max(-perStep * CGFloat(largest + 2), min(perStep * CGFloat(largest + 2), owed + moved))
                    let step = max(-largest, min(largest, Int(owed / perStep)))
                    if step != 0, move(dragRightAdds ? step : -step) { owed -= CGFloat(step) * perStep }
                }
                .onEnded { _ in
                    lastX = nil
                    owed = 0
                })
            arrow("chevron.right", step: buttonStep)
        }
    }

    private func arrow(_ name: String, step: Int) -> some View {
        Button { _ = move(step) } label: {
            Image(systemName: name)
                .font(.footnote.weight(.semibold))
                .foregroundColor(.white)
                .frame(width: 40, height: 44)
                .background(Color.panel.opacity(0.85), in: RoundedRectangle(cornerRadius: 8))
        }
        .disabled(!enabled)
    }
}

struct ChipGrid<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 6), count: 3), spacing: 6) { content }
    }
}

/// A small setting: its name, its value, and a tap to change it.
struct Chip: View {
    let row: Row
    let action: () -> Void

    var body: some View {
        Button(action: action) { ChipFace(label: row.label, value: row.text, on: false) }
            .buttonStyle(Press())
            .disabled(row.choices.isEmpty)
    }
}

struct ChipFace: View {
    let label: String
    let value: String
    let on: Bool

    var body: some View {
        VStack(spacing: 1) {
            Text(label).font(.caption2).foregroundColor(.dim).lineLimit(1)
            Text(value)
                .font(.footnote.weight(.semibold))
                .foregroundColor(on ? .good : .white)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
        }
        .padding(.horizontal, 4)
        .frame(maxWidth: .infinity, minHeight: 46)
        .background(Color.panel, in: RoundedRectangle(cornerRadius: 8))
    }
}

/// Tap to switch to the saved settings, press and hold to save the current ones.
struct PresetChip: View {
    @ObservedObject var camera: Camera
    let slot: Int

    var body: some View {
        ChipFace(label: "C\(slot)", value: camera.preset(slot)?.summary ?? "Empty", on: camera.activeSlot == slot)
            .overlay {
                if camera.activeSlot == slot { RoundedRectangle(cornerRadius: 8).stroke(Color.good, lineWidth: 2) }
            }
            .onTapGesture { camera.applyPreset(slot) }
            .onLongPressGesture(minimumDuration: 0.6) {
                UIImpactFeedbackGenerator(style: .rigid).impactOccurred()
                camera.savePreset(slot)
            }
    }
}

/// Silent mode takes care of the shutter type too, so this is the only switch there is.
struct SilentChip: View {
    @ObservedObject var camera: Camera

    var body: some View {
        if let row = camera.rows[Code.silent] {
            let on = row.text == "On"
            Button { camera.set(Code.silent, on ? 1 : 2) } label: {
                ChipFace(label: row.label, value: row.text, on: on)
            }
            .buttonStyle(Press())
        }
    }
}

struct SetupSheet: View {
    @ObservedObject var camera: Camera
    @State private var chooser: Row?
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Setup").font(.title3.weight(.semibold))
                    Spacer()
                    Button("Done") { dismiss() }
                }
                .padding(.bottom, 8)

                section("VIDEO")
                ChipGrid {
                    chip(Code.movieFormat)
                    chip(Code.frameRate)
                    chip(Code.recordSetting)
                }
                section("PHOTO")
                ChipGrid {
                    chip(Code.fileFormat)
                    if camera.rows[Code.fileFormat]?.text != "RAW" {
                        chip(Code.jpegQuality)
                        chip(Code.imageSize)
                    }
                    chip(Code.aspectRatio)
                    chip(Code.drive)
                    chip(Code.metering)
                }
                section("FOCUS")
                ChipGrid {
                    chip(Code.focusMode)
                    chip(Code.focusArea)
                }
                section("CAMERA")
                ChipGrid {
                    chip(Code.mode)
                    chip(Code.whiteBalance)
                    SilentChip(camera: camera)
                }
                section("CONNECTION")
                Text("Photos and video stay on the camera's card.")
                    .font(.footnote)
                    .foregroundColor(.dim)
                Button("Disconnect") {
                    dismiss()
                    camera.disconnect()
                }
                .buttonStyle(Big(color: .panel))
                Text("CamRemote \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")")
                    .font(.caption2)
                    .foregroundColor(.dim)
                    .frame(maxWidth: .infinity)
                    .padding(.top, 12)
            }
            .padding()
        }
        .background(Color.page.ignoresSafeArea())
        .sheet(item: $chooser) { row in
            ChoiceSheet(row: row) { camera.set(row.code, $0) }
                .presentationDetents([.medium, .large])
        }
    }

    private func section(_ title: String) -> some View {
        Text(title).font(.caption.weight(.semibold)).tracking(1).foregroundColor(.dim).padding(.top, 8)
    }

    @ViewBuilder private func chip(_ code: UInt16) -> some View {
        if let row = camera.rows[code] {
            Chip(row: row) { chooser = row }
        }
    }
}

struct ChoiceSheet: View {
    let row: Row
    let choose: (Int64) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            Text(row.label).font(.caption).foregroundColor(.dim).padding(.top)
            VStack(alignment: .leading, spacing: 6) {
                ForEach(groups, id: \.self) { group in
                    if !group.isEmpty {
                        Text(group).font(.caption.weight(.semibold)).foregroundColor(.dim).padding(.top, 8)
                    }
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 6), count: 3), spacing: 6) {
                        ForEach(row.choices.filter { $0.group == group }) { choice in
                            Button(choice.text) {
                                choose(choice.value)
                                dismiss()
                            }
                            .buttonStyle(Big(color: choice.text == row.text ? .chosen : .page))
                        }
                    }
                }
            }
            .padding(12)
        }
        .background(Color.panel.ignoresSafeArea())
    }

    private var groups: [String] {
        row.choices.reduce(into: []) { found, choice in
            if !found.contains(choice.group) { found.append(choice.group) }
        }
    }
}

/// A small give under the finger, so a tap feels like it landed.
struct Press: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.96 : 1)
            .opacity(configuration.isPressed ? 0.8 : 1)
            .animation(.easeOut(duration: 0.08), value: configuration.isPressed)
    }
}

struct Big: ButtonStyle {
    let color: Color
    var height: CGFloat = 46
    @Environment(\.isEnabled) private var enabled

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .lineLimit(1)
            .minimumScaleFactor(0.7)
            .padding(.horizontal, 4)
            .foregroundColor(.white)
            .frame(maxWidth: .infinity, minHeight: height)
            .background(configuration.isPressed ? Color.chosen : color, in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.line))
            .opacity(enabled ? 1 : 0.4)
    }
}
