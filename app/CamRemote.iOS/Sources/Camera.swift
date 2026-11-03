import Foundation
import UIKit

private let ok: UInt16 = 0x2001
private let liveView: UInt32 = 0xFFFFC002
private let stillImage: UInt32 = 0xFFFFC001
private let setValueOp: UInt16 = 0x9205
private let pressOp: UInt16 = 0x9207
private let up: UInt16 = 1
private let down: UInt16 = 2
private let appControl: Int64 = 1 // lets the app overrule the dial, the way CamRemote on a computer does
private let cardOnly: Int64 = 0x10
private let continuousFocus: Int64 = 0x8004
let zebraPercent = 90
private let pressGap = 0.35
private let matchingModes: [(photo: Int64, movie: Int64)] = [(2, 0x8050), (3, 0x8051), (4, 0x8052), (1, 0x8053)]
private let modeMask: Int64 = 0xFFFF // the camera adds a group number above the mode, like 0x78053 for Movie M
private let sizes: [UInt16: Int] = [1: 1, 2: 1, 3: 2, 4: 2, 5: 4, 6: 4, 7: 8, 8: 8, 9: 16, 10: 16]

enum Code {
    static let shutter: UInt16 = 0xD20D
    static let aperture: UInt16 = 0x5007
    static let iso: UInt16 = 0xD21E
    static let exposure: UInt16 = 0x5010
    static let whiteBalance: UInt16 = 0x5005
    static let colorTemp: UInt16 = 0xD20F
    static let focusMode: UInt16 = 0x500A
    static let focusArea: UInt16 = 0xD22C
    static let movieFormat: UInt16 = 0xD241
    static let recordSetting: UInt16 = 0xD242
    static let frameRate: UInt16 = 0xD286
    static let timeLapseMode: UInt16 = 0xD24F
    static let timeLapseStatus: UInt16 = 0xD250
    static let timeLapseDelay: UInt16 = 0xD16A
    static let timeLapseInterval: UInt16 = 0xD16B
    static let timeLapseShots: UInt16 = 0xD16C
    static let drive: UInt16 = 0x5013
    static let metering: UInt16 = 0x500B
    static let fileFormat: UInt16 = 0xD253
    static let jpegQuality: UInt16 = 0xD252
    static let imageSize: UInt16 = 0xD203
    static let aspectRatio: UInt16 = 0xD211
    static let silent: UInt16 = 0xD0DB
    static let mode: UInt16 = 0x500E
    static let priority: UInt16 = 0xD25A
    static let recording: UInt16 = 0xD21D
    static let saveTo: UInt16 = 0xD222
    static let pendingPhotos: UInt16 = 0xD215
    static let manualFocusReady: UInt16 = 0xD235
    static let batteryPercent: UInt16 = 0xD218
    static let batteryLevel: UInt16 = 0xD20E
    static let cardTime: UInt16 = 0xD24A
    static let cardShots: UInt16 = 0xD249
    static let movieButton: UInt16 = 0xD2C8
    static let halfPress: UInt16 = 0xD2C1
    static let fullPress: UInt16 = 0xD2C2
    static let nearFar: UInt16 = 0xD2D1
    static let touch: UInt16 = 0xD2E4
    static let touchCancel: UInt16 = 0xD2E5
}

struct Choice: Identifiable {
    let value: Int64
    let text: String
    var group = ""
    var id: Int64 { value }
}

/// Kept on the phone, so reopening the app still knows what the camera was asked to do.
struct TimeLapsePlan: Codable {
    let starts: Date
    let ends: Date
    let shots: Int
    let cardShots: Int? // shots left on the card just before the first photo
    let drive: Int64? // what to put back afterwards
}

/// A C1 to C3 slot: the camera's values at the moment it was saved.
struct Preset: Codable {
    let summary: String
    let values: [UInt16: Int64]
}

// The order they go back on. Format and frame rate decide which record settings exist.
private let presetCodes = [Code.mode, Code.movieFormat, Code.frameRate, Code.recordSetting, Code.shutter, Code.aperture, Code.iso,
                           Code.exposure, Code.whiteBalance, Code.colorTemp, Code.focusMode, Code.focusArea, Code.drive]

/// One camera setting, ready to show.
struct Row: Identifiable {
    let code: UInt16
    let label: String
    let text: String
    let choices: [Choice]
    var id: UInt16 { code }
}

private struct PtpError: Error { let text: String }

private struct Setting {
    var kind: UInt16
    var value: Int64
    var choices: [Int64]
}

private let shownRows: [(UInt16, String, (Int64) -> String)] = [
    (Code.shutter, "Shutter speed", Format.shutter),
    (Code.aperture, "Aperture", Format.fNumber),
    (Code.iso, "ISO", Format.iso),
    (Code.exposure, "Exposure compensation", Format.exposure),
    (Code.whiteBalance, "White balance", Format.named(Format.whiteBalance)),
    (Code.colorTemp, "Color temperature", { "\($0)K" }),
    (Code.focusMode, "Focus mode", Format.named(Format.focusMode)),
    (Code.mode, "Exposure mode", Format.mode),
    (Code.focusArea, "Focus area", Format.named(Format.focusArea)),
    (Code.movieFormat, "File format", Format.named(Format.movieFormat)),
    (Code.frameRate, "Frame rate", Format.named(Format.frameRate)),
    (Code.recordSetting, "Record setting", Format.named(Format.recordSetting)),
    (Code.drive, "Drive", Format.drive),
    (Code.metering, "Metering", Format.named(Format.metering)),
    (Code.fileFormat, "File format", Format.named(Format.fileFormat)),
    (Code.jpegQuality, "JPEG quality", Format.named(Format.jpegQuality)),
    (Code.imageSize, "Image size", Format.named(Format.imageSize)),
    (Code.aspectRatio, "Aspect ratio", Format.named(Format.aspectRatio)),
    (Code.silent, "Silent mode", Format.named(Format.offOn)),
]

private func u16(_ value: UInt16) -> [UInt8] { withUnsafeBytes(of: value.littleEndian, Array.init) }
private func u32(_ value: UInt32) -> [UInt8] { withUnsafeBytes(of: value.littleEndian, Array.init) }
private func u64(_ value: UInt64) -> [UInt8] { withUnsafeBytes(of: value.littleEndian, Array.init) }

private func read16(_ data: [UInt8], _ at: Int) -> UInt16 { UInt16(data[at]) | UInt16(data[at + 1]) << 8 }
private func read32(_ data: [UInt8], _ at: Int) -> UInt32 {
    UInt32(read16(data, at)) | UInt32(read16(data, at + 2)) << 16
}

/// Each entry: code, type, get/set, enabled, default, current, form, then its limits or choices.
private func parseSettings(_ data: [UInt8]) throws -> [UInt16: Setting] {
    func value(_ pos: inout Int, _ kind: UInt16) throws -> Int64 {
        func need(_ bytes: Int) throws {
            guard pos + bytes <= data.count else { throw PtpError(text: "settings list cut short") }
        }
        if kind == 0xFFFF {
            try need(1)
            let length = Int(data[pos])
            pos += 1 + 2 * length
            return 0
        }
        if kind & 0x4000 != 0 {
            guard let size = sizes[kind & 0xFF] else { throw PtpError(text: "unknown list type \(kind)") }
            try need(4)
            pos += 4 + Int(read32(data, pos)) * size
            return 0
        }
        guard let size = sizes[kind] else { throw PtpError(text: "unknown type \(kind)") }
        try need(size)
        var result: UInt64 = 0
        for i in 0..<min(size, 8) { result |= UInt64(data[pos + i]) << (8 * UInt64(i)) }
        pos += size
        if kind & 1 == 1, size < 8, result >> (size * 8 - 1) == 1 { return Int64(result) - (1 << Int64(size * 8)) }
        return Int64(bitPattern: result)
    }
    func list(_ pos: inout Int, _ kind: UInt16) throws -> [Int64] {
        guard pos + 2 <= data.count else { throw PtpError(text: "settings list cut short") }
        let count = Int(read16(data, pos))
        pos += 2
        return try (0..<count).map { _ in try value(&pos, kind) }
    }

    guard data.count >= 8 else { throw PtpError(text: "settings list cut short") }
    let expected = Int(read32(data, 0))
    var found: [UInt16: Setting] = [:]
    var pos = 8
    while pos + 6 <= data.count, found.count < expected {
        let code = read16(data, pos), kind = read16(data, pos + 2)
        pos += 6
        _ = try value(&pos, kind)
        let current = try value(&pos, kind)
        guard pos < data.count else { throw PtpError(text: "settings list cut short") }
        let form = data[pos]
        pos += 1
        var choices: [Int64] = []
        if form == 1 {
            let low = try value(&pos, kind), high = try value(&pos, kind), step = try value(&pos, kind)
            if step > 0, (high - low) / step <= 200 { choices = Array(stride(from: low, through: high, by: Int(step))) }
        } else if form == 2 {
            choices = try list(&pos, kind)
            // Newer Sonys add a second list, the values that can really be set.
            if pos + 2 <= data.count, read16(data, pos) < 0x200 { choices = try list(&pos, kind) }
        }
        found[code] = Setting(kind: kind, value: current, choices: choices)
    }
    guard found.count == expected, pos == data.count else { throw PtpError(text: "settings list read wrong") }
    return found
}

final class Camera: ObservableObject {
    enum Link { case off, connecting, on }

    @Published var link = Link.off
    @Published var frame: UIImage?
    @Published var frameTime = Date()
    @Published var problem = ""
    @Published var message = ""
    @Published var rows: [UInt16: Row] = [:]
    @Published var recording = false
    @Published var recordingSince: Date?
    @Published var recordBusy = false
    @Published var photoMode = false
    @Published var manualFocus = false
    @Published var battery = ""
    @Published var card = ""
    @Published var shotsLeft: Int?
    @Published var timeLapseRunning = false
    @Published var presets = Camera.loadPresets()
    @Published var activeSlot: Int?
    @Published var timeLapsePlan = Camera.savedPlan()
    @Published var timeLapseTaken = 0
    @Published var dialOnMovie = false // as of connecting; the camera doesn't report the dial after that
    @Published var keepTrying = false
    @Published var editingLogin = false
    @Published var loginRefused = false
    @Published var lowBattery = false
    @Published var lowCard = false
    @Published var guideImage: UIImage?
    @Published var histogram: [CGFloat] = []
    @Published var overPercent = 0.0

    private let lock = NSLock()
    private var pending: [() throws -> Void] = []
    private var frameWaiting = false
    private var guides = (histogram: false, zebra: false, peaking: false) // under `lock`
    private var guidesBusy = false // under `lock`
    private var lastGuides = Date.distantPast // under `lock`
    private var timeLapseWasRunning = false
    private var driveBeforeTimeLapse: Int64?
    private var plan = Camera.savedPlan() // the camera thread's copy of `timeLapsePlan`
    private var stopping = false
    private var quiet = false // under `lock`: out of sight, so no pictures
    private var backgroundTask = UIBackgroundTaskIdentifier.invalid // main thread only
    private var tx: UInt32 = 0
    private var settings: [UInt16: Setting] = [:]
    private var events: [UInt8] = []
    private var savedDestination: Int64?
    private var shutterPressed = Date()
    private var shutterHeld = false // main thread only
    private var shutterReleased = Date.distantPast // main thread only
    private var focusSending = false // main thread only
    private var stepSending = false // main thread only
    private var tapBusy = false // main thread only

    private static let folder = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]

    func log(_ text: String) {
        let line = "\(Date().formatted(date: .omitted, time: .standard))  \(text)\n"
        let file = Self.folder.appendingPathComponent("log.txt")
        if let handle = try? FileHandle(forWritingTo: file) {
            handle.seekToEndOfFile()
            handle.write(Data(line.utf8))
            try? handle.close()
        } else {
            try? Data(line.utf8).write(to: file)
        }
    }

    private func tell(_ text: String) {
        log(text)
        DispatchQueue.main.async { self.message = text }
    }

    func connect(address: String, user: String, password: String, knownKey: String) {
        guard link == .off else { return }
        link = .connecting
        stopping = false
        pending = []
        let thread = Thread {
            self.run(address: address, user: user, password: password, knownKey: knownKey)
        }
        thread.stackSize = 1 << 20
        thread.start()
    }

    func disconnect() {
        if shutterHeld { shutterUp() }
        lock.lock()
        stopping = true
        lock.unlock()
    }

    /// Hanging up ends a time-lapse, so leaving the app during one keeps the line open as long as iOS allows.
    func holdConnection() {
        lock.lock()
        quiet = true
        lock.unlock()
        log("Left the app during a time-lapse, staying connected")
        backgroundTask = UIApplication.shared.beginBackgroundTask { [weak self] in
            self?.log("iOS is pausing the app")
            self?.endBackgroundTask()
        }
    }

    func releaseHold() {
        lock.lock()
        quiet = false
        lock.unlock()
        endBackgroundTask()
    }

    private func endBackgroundTask() {
        guard backgroundTask != .invalid else { return }
        UIApplication.shared.endBackgroundTask(backgroundTask)
        backgroundTask = .invalid
    }

    func set(_ code: UInt16, _ value: Int64) {
        queue {
            let rc = try self.setValue(code, value)
            if rc != ok { self.tell("The camera didn't accept that right now.") }
            try self.refresh()
        }
    }

    /// One change at a time, so a fast drag doesn't queue them up. Returns false if one is still on its way.
    func step(_ code: UInt16, by: Int) -> Bool {
        guard !stepSending else { return false }
        stepSending = true
        queue {
            defer { DispatchQueue.main.async { self.stepSending = false } }
            guard let entry = self.settings[code], let at = entry.choices.firstIndex(of: entry.value),
                  entry.choices.indices.contains(at + by) else { return }
            if try self.setValue(code, entry.choices[at + by]) != ok { self.tell("The camera didn't accept that right now.") }
            try self.refresh()
        }
        return true
    }

    /// Photo and video are programs on the camera's mode dial, so the dial has to allow the switch.
    func useMode(photo: Bool) {
        queue {
            guard let mode = self.settings[Code.mode] else { return }
            let current = mode.value & modeMask
            let now = (0x8059...0x805C).contains(current) ? current - 9 : current
            let match = matchingModes.first { (photo ? $0.movie : $0.photo) == now }
            let want = photo ? (match?.photo ?? 1) : (match?.movie ?? 0x8053)
            let dial = photo ? "Turn the camera's mode dial to P, A, S or M." : "Turn the camera's mode dial to Movie."
            guard let choice = mode.choices.first(where: { $0 & modeMask == want }),
                  try self.setValue(Code.mode, choice) == ok else {
                self.tell(dial)
                return
            }
            Thread.sleep(forTimeInterval: 0.5)
            try self.refresh()
            if (self.settings[Code.mode]?.value ?? 0) & modeMask != want { self.tell(dial) } // it says yes, then follows the dial
        }
    }

    func toggleRecord() {
        guard !recordBusy else { return }
        recordBusy = true
        queue {
            defer { DispatchQueue.main.async { self.recordBusy = false } }
            try self.refresh()
            if self.settings[Code.recording]?.value == 1 {
                _ = try self.button(Code.movieButton, up)
                Thread.sleep(forTimeInterval: 0.5)
                try self.refresh()
                if self.settings[Code.recording]?.value == 1 {
                    _ = try self.button(Code.movieButton, down)
                    Thread.sleep(forTimeInterval: 0.08)
                    _ = try self.button(Code.movieButton, up)
                }
                Thread.sleep(forTimeInterval: 1) // the camera refuses the next press while it finishes the file
            } else {
                try self.collectPhotos()
                _ = try self.button(Code.movieButton, down)
                for _ in 0..<8 where self.settings[Code.recording]?.value != 1 {
                    Thread.sleep(forTimeInterval: 0.25)
                    try self.refresh()
                }
                if self.settings[Code.recording]?.value != 1 {
                    _ = try self.button(Code.movieButton, up)
                    self.tell("The camera didn't start recording. Check its card.")
                }
            }
            try self.refresh()
        }
    }

    /// Video and Photo each have their own C1 to C3, so these are numbered 1 to 3 and 11 to 13.
    private func presetKey(_ slot: Int) -> Int { photoMode ? slot + 10 : slot }

    func preset(_ slot: Int) -> Preset? { presets[presetKey(slot)] }

    func savePreset(_ slot: Int) {
        let key = presetKey(slot)
        queue {
            let values = presetCodes.reduce(into: [UInt16: Int64]()) { $0[$1] = self.settings[$1]?.value }
            let parts = (self.onPhotoMode ? [] : [Code.frameRate]) + [Code.shutter, Code.aperture, Code.iso]
            let summary = parts.compactMap { self.text($0) }.joined(separator: " ").replacingOccurrences(of: "ISO ", with: "")
            DispatchQueue.main.async {
                self.presets[key] = Preset(summary: summary, values: values)
                if let data = try? JSONEncoder().encode(self.presets) { UserDefaults.standard.set(data, forKey: "modePresets") }
                self.message = "Saved to C\(slot)."
            }
        }
    }

    /// The camera's own C1 to C3 stay as they are. These live in the app, so nothing on the camera gets overwritten.
    func applyPreset(_ slot: Int) {
        guard let preset = preset(slot) else {
            message = "Press and hold C\(slot) to save your current settings there."
            return
        }
        guard !recording, !timeLapseRunning else {
            message = "Stop first, then switch settings."
            return
        }
        queue {
            if let mode = preset.values[Code.mode], self.settings[Code.mode]?.value != mode {
                _ = try self.setValue(Code.mode, mode)
                Thread.sleep(forTimeInterval: 0.7)
                try self.refresh()
            }
            for code in presetCodes where code != Code.mode {
                guard let value = preset.values[code], let entry = self.settings[code], entry.value != value,
                      entry.choices.isEmpty || entry.choices.contains(value) else { continue }
                _ = try self.setValue(code, value)
                if code == Code.frameRate || code == Code.movieFormat {
                    Thread.sleep(forTimeInterval: 0.3) // the other video settings' choices change with these
                    try self.refresh()
                }
            }
            try self.refresh()
            self.tell("C\(slot): \(preset.summary)")
        }
    }

    func startTimeLapse(intervalSeconds: Int, shots: Int, delaySeconds: Int) {
        queue {
            guard self.settings[Code.timeLapseMode] != nil else {
                self.tell("This camera doesn't offer time-lapse.")
                return
            }
            if let drive = self.settings[Code.drive]?.value, drive != 1 {
                self.driveBeforeTimeLapse = drive
                _ = try self.setValue(Code.drive, 1)
            }
            guard try self.setValue(Code.timeLapseMode, 2) == ok else {
                self.tell("The camera won't start a time-lapse right now.")
                try self.endTimeLapse()
                return
            }
            Thread.sleep(forTimeInterval: 0.3)
            let settled = try self.setValue(Code.timeLapseInterval, Int64(intervalSeconds * 10)) == ok
                && self.setValue(Code.timeLapseShots, Int64(shots)) == ok
                && self.setValue(Code.timeLapseDelay, Int64(max(1, delaySeconds))) == ok
            guard settled else {
                self.tell("The camera didn't accept that plan.")
                try self.endTimeLapse()
                return
            }
            Thread.sleep(forTimeInterval: 0.3)
            let cardShots = self.settings[Code.cardShots].map { Int($0.value) }
            try self.pressShutter()
            try self.refresh()
            let starts = Date().addingTimeInterval(Double(delaySeconds))
            self.remember(TimeLapsePlan(starts: starts, ends: starts.addingTimeInterval(Double(intervalSeconds * shots)), shots: shots,
                                        cardShots: cardShots, drive: self.driveBeforeTimeLapse))
        }
    }

    private func remember(_ new: TimeLapsePlan?) {
        plan = new
        UserDefaults.standard.set(new.flatMap { try? JSONEncoder().encode($0) }, forKey: "timeLapsePlan")
        DispatchQueue.main.async { self.timeLapsePlan = new }
    }

    private static func savedPlan() -> TimeLapsePlan? {
        guard let data = UserDefaults.standard.data(forKey: "timeLapsePlan") else { return nil }
        return try? JSONDecoder().decode(TimeLapsePlan.self, from: data)
    }

    /// The camera doesn't report a count of its own, but its shots left drops by one a photo.
    /// Nil in video mode, where the camera reports no shots left.
    private func photosTaken(_ plan: TimeLapsePlan) -> Int? {
        guard onPhotoMode, let before = plan.cardShots, let left = settings[Code.cardShots]?.value else { return nil }
        return max(0, min(plan.shots, before - Int(left)))
    }

    /// Runs when a connection opens, so a time-lapse that ended while the app was away doesn't look like it's still going.
    private func checkTimeLapse() throws {
        guard let plan, settings[Code.timeLapseStatus]?.value != 1 else { return }
        try endTimeLapse()
        tell(Date() < plan.ends ? "The time-lapse stopped early." : "The time-lapse finished.")
    }

    func stopTimeLapse() {
        queue {
            try self.pressShutter()
            try self.refresh()
        }
    }

    private func pressShutter() throws {
        _ = try button(Code.halfPress, down)
        _ = try button(Code.fullPress, down)
        Thread.sleep(forTimeInterval: 0.12)
        _ = try button(Code.fullPress, up)
        _ = try button(Code.halfPress, up)
    }

    /// Left on, the next ordinary photo would start another one.
    private func endTimeLapse() throws {
        _ = try setValue(Code.timeLapseMode, 1)
        if let drive = driveBeforeTimeLapse ?? plan?.drive { _ = try setValue(Code.drive, drive) }
        driveBeforeTimeLapse = nil
        remember(nil)
    }

    /// Works like the real button: held down, the camera follows its own drive mode, so burst keeps firing.
    /// Returns false for a spare tap, which is ignored because it would only pile up.
    func shutterDown() -> Bool {
        guard !shutterHeld, Date().timeIntervalSince(shutterReleased) > pressGap else { return false }
        shutterHeld = true
        queue {
            self.shutterPressed = Date()
            _ = try self.button(Code.halfPress, down)
            _ = try self.button(Code.fullPress, down)
        }
        return true
    }

    func shutterUp() {
        guard shutterHeld else { return }
        shutterHeld = false
        shutterReleased = Date()
        queue {
            let held = Date().timeIntervalSince(self.shutterPressed)
            if held < 0.12 { Thread.sleep(forTimeInterval: 0.12 - held) } // a tap still has to reach the camera as a press
            _ = try self.button(Code.fullPress, up)
            _ = try self.button(Code.halfPress, up)
        }
    }

    /// One focus move at a time, so a slow link doesn't queue them up. Returns false if one is still on its way.
    func focusStep(_ step: Int) -> Bool {
        guard !focusSending else { return false }
        focusSending = true
        queue {
            defer { DispatchQueue.main.async { self.focusSending = false } }
            _ = try self.send(pressOp, Code.nearFar, withUnsafeBytes(of: Int16(step).littleEndian, Array.init))
        }
        return true
    }

    /// `x` and `y` run from 0 to 1 across the picture. Manual focus drops to autofocus for this one tap, then returns.
    func focus(x: Double, y: Double) {
        guard !tapBusy else { return }
        tapBusy = true
        let point = UInt32(x * 639) << 16 | UInt32(y * 479)
        queue {
            let manual = self.settings[Code.focusMode]?.value == 1, photo = self.onPhotoMode
            if manual {
                _ = try self.setValue(Code.focusMode, continuousFocus)
                Thread.sleep(forTimeInterval: 0.35) // the camera needs a moment to accept touch after leaving manual
            }
            let rc = try self.send(pressOp, Code.touch, u32(point))
            self.log(String(format: "Touch focus at %.2f, %.2f: %04X", x, y, rc))
            if rc != ok { self.tell("The camera didn't take that focus point.") }
            if photo { _ = try self.button(Code.halfPress, down) }
            // Later rather than asleep here, so the picture keeps moving while the lens focuses.
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.3) {
                self.queue {
                    defer { DispatchQueue.main.async { self.tapBusy = false } }
                    if photo { _ = try self.button(Code.halfPress, up) }
                    if manual {
                        _ = try self.button(Code.touchCancel, down)
                        _ = try self.button(Code.touchCancel, up)
                        _ = try self.setValue(Code.focusMode, 1)
                    }
                    try self.refresh()
                }
            }
        }
    }

    private func queue(_ action: @escaping () throws -> Void) {
        lock.lock()
        pending.append(action)
        lock.unlock()
    }

    private func finish(_ problem: String, retry: Bool = false, badLogin: Bool = false) {
        DispatchQueue.main.async {
            self.problem = problem
            self.keepTrying = retry
            self.loginRefused = badLogin
            self.link = .off
            self.recordBusy = false
            self.shutterHeld = false
            self.focusSending = false
            self.tapBusy = false
            self.stepSending = false
        }
    }

    private func findAddress(_ saved: String) -> String? {
        if !saved.isEmpty, cam_reachable(saved, 1000) == 1 { return saved }
        for attempt in 0..<2 {
            // The first try can fail while iOS is still asking to use the local network.
            if attempt > 0 { Thread.sleep(forTimeInterval: 2) }
            var out = [CChar](repeating: 0, count: 32)
            if cam_find("172.20.10.", &out, 32) == 0 { return String(cString: out) }
        }
        return nil
    }

    private func run(address: String, user: String, password: String, knownKey: String) {
        guard let host = findAddress(address) else {
            log("Camera not found")
            finish("Turn on your hotspot, then turn on the camera. This connects by itself.", retry: true)
            return
        }
        log("Connecting to \(host)")
        var found = [CChar](repeating: 0, count: 65)
        let opened = cam_ssh_open(host, user, password, knownKey, &found, 65)
        log("Connection result \(opened)")
        switch opened {
        case 0: break
        case CAM_KEY_CHANGED:
            finish("This isn't the camera you connected to before. If you reset its settings, tap Forget this camera.")
            return
        case CAM_BAD_LOGIN:
            finish("That user or password didn't work. Use the ones on the camera's Access Authen. Info screen.", badLogin: true)
            return
        default:
            finish("The camera wouldn't connect. Check that Remote Shooting is on.", retry: true)
            return
        }
        UserDefaults.standard.set(String(cString: found), forKey: "cameraKey")
        var problem = ""
        do {
            try start()
            DispatchQueue.main.async {
                self.link = .on
                self.problem = ""
            }
            queue { try self.checkTimeLapse() }
            try loop()
        } catch {
            log("Stopped: \(error)")
            lock.lock()
            let leaving = stopping // a hang-up that failed partway is still the hang-up that was asked for
            lock.unlock()
            if !leaving { problem = "Lost the camera. Looking for it again." }
        }
        cam_ssh_close()
        finish(problem, retry: !problem.isEmpty)
    }

    private func start() throws {
        tx = 0
        events = []
        savedDestination = nil
        timeLapseWasRunning = false // `checkTimeLapse` sees to one that ended while disconnected
        let name: [UInt8] = "CamRemote".utf16.flatMap { u16($0) } + [0, 0]
        let guid: [UInt8] = (0..<16).map { _ in UInt8.random(in: 0...255) }
        try sendPacket(0, 1, guid + name + u32(0x00010000))
        let (kind, body) = try readPacket(0)
        guard kind == 2, body.count >= 4 else { throw PtpError(text: "camera refused the start, type \(kind)") }
        try sendPacket(1, 3, Array(body[0..<4]))
        let (eventKind, _) = try readPacket(1)
        guard eventKind == 4 else { throw PtpError(text: "camera refused the event channel, type \(eventKind)") }
        _ = try op(0x1001)
        let (rc, _) = try op(0x1002, [1])
        guard rc == ok || rc == 0x201E else { throw PtpError(text: String(format: "open session said %04X", rc)) }
        for (code, params) in [(UInt16(0x9201), [UInt32(1), 0, 0]), (0x9201, [2, 0, 0]), (0x9202, [0x012C, 0, 0]),
                               (0x9201, [3, 0, 0]), (0x9202, [0x012C, 0, 0])] {
            _ = try op(code, params)
            Thread.sleep(forTimeInterval: 0.3)
        }
        Thread.sleep(forTimeInterval: 1)
        try refresh()
        // Until the app takes over, the mode is wherever the dial sits. A dropped link hands the camera back to the dial.
        let dialIsPhoto = onPhotoMode
        DispatchQueue.main.async { self.dialOnMovie = !dialIsPhoto }
        _ = try setValue(Code.priority, appControl)
        Thread.sleep(forTimeInterval: 0.3)
        try collectPhotos()
        log("Dial control is now \(settings[Code.priority]?.value ?? -1)")
        // Photos stay on the card, so the shutter never waits on a copy to the phone.
        if let entry = settings[Code.saveTo], entry.value != cardOnly, entry.choices.contains(cardOnly) {
            savedDestination = entry.value
            _ = try setValue(Code.saveTo, cardOnly)
        }
        try refresh()
        saveSettingsList()
        _ = try op(0x1008, [liveView])
    }

    private func saveSettingsList() {
        let lines = settings.sorted { $0.key < $1.key }.map { code, entry in
            String(format: "%04X kind %X value %llX choices ", code, entry.kind, entry.value) + entry.choices.map { String(format: "%llX", $0) }.joined(separator: " ")
        }
        try? lines.joined(separator: "\n").write(to: Self.folder.appendingPathComponent("settings.txt"), atomically: true, encoding: .utf8)
    }

    private func loop() throws {
        var lastRefresh = Date()
        var lastMemory = Date()
        while true {
            // A plain thread has no pool of its own, so without this every picture stays in memory until iOS kills the app.
            let done: Bool = try autoreleasepool {
                lock.lock()
                let actions = pending
                pending = []
                let done = stopping, resting = quiet
                lock.unlock()
                for action in actions { try action() } // first, because letting go of the shutter arrives with the hang-up
                if done { return true }
                try pumpEvents()
                if Date().timeIntervalSince(lastRefresh) > 1 {
                    try refresh()
                    lastRefresh = Date()
                    // It won't let go of the dial while recording or during a time-lapse, so ask again once that's over.
                    if settings[Code.priority]?.value != appControl, settings[Code.recording]?.value != 1,
                       settings[Code.timeLapseStatus]?.value != 1 {
                        _ = try setValue(Code.priority, appControl)
                    }
                }
                if Date().timeIntervalSince(lastMemory) > 60 {
                    log("Using \(memoryInMB()) MB")
                    lastMemory = Date()
                }
                if resting {
                    Thread.sleep(forTimeInterval: 0.5)
                    return false
                }
                let (rc, data) = try op(0x1009, [liveView])
                if rc == ok, takeFrameSlot() {
                    if let image = jpeg(data) {
                        DispatchQueue.main.async {
                            self.frame = image
                            self.frameTime = Date()
                            self.freeFrameSlot()
                        }
                        readGuides(image)
                    } else {
                        freeFrameSlot()
                    }
                }
                return false
            }
            if done { break }
        }
        // A recording keeps going: leaving the app shouldn't stop a shot.
        if let destination = savedDestination { _ = try setValue(Code.saveTo, destination) }
        savedDestination = nil
        _ = try op(0x1003)
    }

    /// A picture is dropped if the screen hasn't shown the last one, so a busy screen can't build up a backlog.
    private func takeFrameSlot() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if frameWaiting { return false }
        frameWaiting = true
        return true
    }

    private func freeFrameSlot() {
        lock.lock()
        frameWaiting = false
        lock.unlock()
    }

    func setGuides(histogram: Bool, zebra: Bool, peaking: Bool) {
        lock.lock()
        guides = (histogram, zebra, peaking)
        lock.unlock()
        if !zebra, !peaking { guideImage = nil }
    }

    /// The camera sends a clean picture, so these are worked out here. Off the camera thread, and only about eight times a second.
    private func readGuides(_ image: UIImage) {
        lock.lock()
        let wanted = guides
        let run = (wanted.histogram || wanted.zebra || wanted.peaking) && !guidesBusy && Date().timeIntervalSince(lastGuides) > 0.12
        if run {
            guidesBusy = true
            lastGuides = Date()
        }
        lock.unlock()
        guard run else { return }
        DispatchQueue.global(qos: .userInitiated).async {
            autoreleasepool { self.workOutGuides(image, wanted.zebra, wanted.peaking) }
            self.lock.lock()
            self.guidesBusy = false
            self.lock.unlock()
        }
    }

    private func workOutGuides(_ image: UIImage, _ zebra: Bool, _ peaking: Bool) {
        guard let picture = image.cgImage else { return }
        let width = picture.width, height = picture.height, level = zebraPercent * 255 / 100
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        var overlay = [UInt8](repeating: 0, count: zebra || peaking ? width * height * 4 : 4)
        var bins = [Int32](repeating: 0, count: 256)
        pixels.withUnsafeMutableBytes { bytes in
            let space = CGColorSpaceCreateDeviceRGB()
            let context = CGContext(data: bytes.baseAddress, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                    space: space, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)
            context?.draw(picture, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
        cam_guides(pixels, Int32(width), Int32(height), zebra ? 1 : 0, peaking ? 1 : 0, Int32(level), &bins, &overlay)

        var marks: UIImage?
        if zebra || peaking, let provider = CGDataProvider(data: Data(overlay) as CFData),
           let made = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                              space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                              provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent) {
            marks = UIImage(cgImage: made)
        }
        let peak = max(1, bins[2..<254].max() ?? 1), over = bins[level...].reduce(0) { $0 + Int($1) }
        let bars = bins.map { CGFloat(min(1, (Double($0) / Double(peak)).squareRoot())) }
        let percent = 100 * Double(over) / Double(width * height)
        DispatchQueue.main.async {
            self.histogram = bars
            self.overPercent = percent
            self.lock.lock()
            let wanted = self.guides
            self.lock.unlock()
            self.guideImage = wanted.zebra || wanted.peaking ? marks : nil
        }
    }

    private func memoryInMB() -> Int {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count) }
        }
        return result == KERN_SUCCESS ? Int(info.phys_footprint / 1_048_576) : 0
    }

    private func jpeg(_ data: [UInt8]) -> UIImage? {
        guard let start = data.indices.dropLast().first(where: { data[$0] == 0xFF && data[$0 + 1] == 0xD8 }) else { return nil }
        return UIImage(data: Data(data[start...]))
    }

    /// The camera holds new photos for the app, and refuses to record until they're collected.
    private func collectPhotos() throws {
        for _ in 0..<20 {
            try refresh()
            guard let waiting = settings[Code.pendingPhotos]?.value, waiting & 0x8000 != 0 else { return }
            _ = try op(0x1008, [stillImage])
            let (rc, data) = try op(0x1009, [stillImage])
            log(String(format: "Cleared a waiting photo: %d bytes, %04X", data.count, rc))
            guard rc == ok, !data.isEmpty else { return }
        }
    }

    private func text(_ code: UInt16) -> String? {
        guard let entry = settings[code], let row = shownRows.first(where: { $0.0 == code }) else { return nil }
        return row.2(entry.value)
    }

    private static func loadPresets() -> [Int: Preset] {
        guard let data = UserDefaults.standard.data(forKey: "modePresets") else { return [:] }
        return (try? JSONDecoder().decode([Int: Preset].self, from: data)) ?? [:]
    }

    private var onPhotoMode: Bool { isPhotoMode(settings[Code.mode]?.value ?? 0) }

    private func isPhotoMode(_ value: Int64) -> Bool {
        let mode = value & modeMask
        return mode < 0x8050 || mode == 0x8070 || mode == 0x8089
    }

    /// Whatever the camera works out for itself in P, A or S doesn't count as having moved away from a preset.
    private func matches(_ preset: Preset, _ now: [UInt16: Int64]) -> Bool {
        let mode = (now[Code.mode] ?? 0) & modeMask
        var ignored: Set<UInt16> = []
        if [2, 3, 0x8050, 0x8051].contains(mode) { ignored.insert(Code.shutter) }
        if [2, 4, 0x8050, 0x8052].contains(mode) { ignored.insert(Code.aperture) }
        if now[Code.whiteBalance] != 0x8012 { ignored.insert(Code.colorTemp) }
        return preset.values.allSatisfy { ignored.contains($0) || now[$0] == $1 }
    }

    private func refresh() throws {
        let (rc, data) = try op(0x9209, [0])
        guard rc == ok else { throw PtpError(text: String(format: "reading settings said %04X", rc)) }
        let fresh = try parseSettings(data)
        for (code, entry) in fresh where !entry.choices.isEmpty {
            if let old = settings[code], old.value != entry.value { log(String(format: "%04X went from %llX to %llX", code, old.value, entry.value)) }
        }
        settings.merge(fresh) { $1 }
        settings[Code.exposure]?.choices.sort() // the camera lists these from plus to minus

        let timeLapseRunning = settings[Code.timeLapseStatus]?.value == 1
        if timeLapseWasRunning, !timeLapseRunning { try endTimeLapse() }
        timeLapseWasRunning = timeLapseRunning

        var shown: [UInt16: Row] = [:]
        for (code, label, format) in shownRows {
            guard let entry = settings[code] else { continue }
            let choices = entry.choices.map { Choice(value: $0, text: format($0), group: Format.group(code, $0)) }
            let usable = choices.filter { $0.text != "Other" && (code != Code.mode || isPhotoMode($0.value) == onPhotoMode) }
            shown[code] = Row(code: code, label: label, text: format(entry.value), choices: usable)
        }
        let isRecording = settings[Code.recording]?.value == 1
        let isPhoto = onPhotoMode
        let isManual = settings[Code.focusMode]?.value == 1
        let batteryText = Format.battery(percent: settings[Code.batteryPercent]?.value, level: settings[Code.batteryLevel]?.value)
        let cardText = isPhoto ? (settings[Code.cardShots].map { "\($0.value) shots" } ?? "")
                               : (settings[Code.cardTime].map { Format.duration($0.value) } ?? "")
        let shots = settings[Code.cardShots].map { Int($0.value) }
        let values = presetCodes.reduce(into: [UInt16: Int64]()) { $0[$1] = settings[$1]?.value }
        let batteryPercent = settings[Code.batteryPercent]?.value ?? 100, cardTime = settings[Code.cardTime]?.value ?? 0
        // Non-Sony batteries report no percent, only a rough level.
        let batteryIsLow = (0...15).contains(batteryPercent)
            || (batteryPercent < 0 && [1, 2, 6].contains((settings[Code.batteryLevel]?.value ?? 0) & 0xFFFF))
        let cardIsLow = isPhoto ? (shots ?? 1000) < 50 : (1..<300).contains(cardTime)
        let taken = plan.flatMap(photosTaken)
        DispatchQueue.main.async {
            self.rows = shown
            if let taken { self.timeLapseTaken = taken }
            self.lowBattery = batteryIsLow
            self.lowCard = cardIsLow
            defer { self.activeSlot = (1...3).first { self.preset($0).map { self.matches($0, values) } ?? false } }
            self.shotsLeft = shots
            self.timeLapseRunning = timeLapseRunning
            if isRecording != self.recording { self.recordingSince = isRecording ? Date() : nil }
            self.recording = isRecording
            self.photoMode = isPhoto
            self.manualFocus = isManual
            self.battery = batteryText
            self.card = cardText
        }
    }

    private func setValue(_ code: UInt16, _ value: Int64) throws -> UInt16 {
        guard let kind = settings[code]?.kind, let size = sizes[kind] else { throw PtpError(text: "no such setting") }
        let rc = try send(setValueOp, code, Array(u64(UInt64(bitPattern: value)).prefix(size)))
        log(String(format: "Set %04X to %llX: %04X", code, value, rc))
        return rc
    }

    private func button(_ code: UInt16, _ value: UInt16) throws -> UInt16 {
        let rc = try send(pressOp, code, u16(value))
        log(String(format: "Press %04X %d: %04X", code, value, rc))
        return rc
    }

    private func pumpEvents() throws {
        var buffer = [UInt8](repeating: 0, count: 4096)
        while true {
            let n = cam_ssh_read_some(1, &buffer, 4096)
            if n < 0 { throw PtpError(text: "event channel closed") }
            if n == 0 { break }
            events += buffer[0..<Int(n)]
        }
        while events.count >= 8, events.count >= Int(read32(events, 0)) {
            let size = Int(read32(events, 0))
            guard size >= 8 else { throw PtpError(text: "bad event from the camera") }
            if read32(events, 4) == 13 { try sendPacket(1, 14, []) }
            events.removeFirst(size)
        }
    }

    private func read(_ channel: Int32, _ size: Int) throws -> [UInt8] {
        var buffer = [UInt8](repeating: 0, count: size)
        if size > 0, cam_ssh_read(channel, &buffer, Int32(size), 30000) != Int32(size) {
            throw PtpError(text: "the camera stopped answering")
        }
        return buffer
    }

    private func readPacket(_ channel: Int32) throws -> (UInt32, [UInt8]) {
        let header = try read(channel, 8)
        let size = Int(read32(header, 0))
        guard size >= 8 else { throw PtpError(text: "bad packet from the camera") }
        return (read32(header, 4), try read(channel, size - 8))
    }

    private func sendPacket(_ channel: Int32, _ kind: UInt32, _ body: [UInt8]) throws {
        let packet = u32(UInt32(8 + body.count)) + u32(kind) + body
        guard cam_ssh_write(channel, packet, Int32(packet.count)) == Int32(packet.count) else {
            throw PtpError(text: "the camera stopped answering")
        }
    }

    private func request(_ phase: UInt32, _ code: UInt16, _ params: [UInt32]) throws {
        tx += 1
        try sendPacket(0, 6, u32(phase) + u16(code) + u32(tx) + params.flatMap { u32($0) })
    }

    private func response() throws -> (UInt16, [UInt8]) {
        var data: [UInt8] = []
        while true {
            let (kind, body) = try readPacket(0)
            if kind == 7 { return (read16(body, 0), data) }
            if kind == 10 || kind == 12 { data += body[4...] }
        }
    }

    private func op(_ code: UInt16, _ params: [UInt32] = []) throws -> (UInt16, [UInt8]) {
        try request(1, code, params)
        return try response()
    }

    private func send(_ code: UInt16, _ param: UInt16, _ payload: [UInt8]) throws -> UInt16 {
        try request(2, code, [UInt32(param)])
        try sendPacket(0, 9, u32(tx) + u64(UInt64(payload.count)))
        try sendPacket(0, 12, u32(tx) + payload)
        return try response().0
    }
}
