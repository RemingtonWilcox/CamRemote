import Foundation

/// Turns raw camera values into the labels the camera shows. Matches Format.cs in the desktop app.
enum Format {
    static func fNumber(_ v: Int64) -> String {
        if v == 0 || v == 0xFFFE { return "--" }
        if v == 0xFFFD { return "Close" }
        if v == 0xFFFF { return "" }
        return v % 100 == 0 ? "F\(v / 100)" : String(format: "F%g", Double(v) / 100)
    }

    static func iso(_ v: Int64) -> String {
        let value = v & 0xFFFFFF
        return value == 0xFFFFFF ? "ISO AUTO" : "ISO \(value)"
    }

    static func shutter(_ v: Int64) -> String {
        if v == 0 { return "Bulb" }
        if v == 0xFFFFFFFF { return "--" }
        let num = (v >> 16) & 0xFFFF, den = v & 0xFFFF
        if den == 0 { return "--" }
        if num == 1 { return "1/\(den)" }
        if num % den == 0 { return "\(num / den)\"" }
        return String(format: "%.1f\"", Double(num) / Double(den))
    }

    static func exposure(_ v: Int64) -> String {
        v == 0 ? "±0" : String(format: "%+.1f", Double(v) / 1000)
    }

    static func named(_ table: [Int64: String]) -> (Int64) -> String {
        { table[$0] ?? "Other" }
    }

    static let whiteBalance: [Int64: String] = [
        0x0002: "Auto", 0x0004: "Daylight", 0x0005: "Fluorescent", 0x0006: "Incandescent", 0x0007: "Flash",
        0x8001: "Warm white", 0x8002: "Cool white", 0x8003: "Day white", 0x8004: "Fluor. daylight",
        0x8010: "Cloudy", 0x8011: "Shade", 0x8012: "Color temp", 0x8020: "Custom 1", 0x8021: "Custom 2", 0x8022: "Custom 3",
        0x8030: "Underwater",
    ]

    static let focusMode: [Int64: String] = [1: "Manual", 2: "Single", 0x8004: "Continuous", 0x8005: "Auto", 0x8006: "Direct manual"]

    // Over this connection the camera numbers these differently from Sony's SDK, so they aren't the desktop app's tables.
    static let movieFormat: [Int64: String] = [0xB: "XAVC HS 4K", 0x8: "XAVC S 4K", 0x9: "XAVC S HD", 0xE: "XAVC S-I 4K", 0xF: "XAVC S-I HD"]

    private static let driveNames: [Int64: String] = [
        0x1: "Single", 0x8010: "Burst Hi+", 0x2: "Burst Hi", 0x8015: "Burst Mid", 0x8012: "Burst Lo",
        0x8005: "Timer 2s", 0x8003: "Timer 5s", 0x8004: "Timer 10s", 0x800E: "Timer 2s ×3", 0x800F: "Timer 2s ×5",
        0x800C: "Timer 5s ×3", 0x800D: "Timer 5s ×5", 0x8008: "Timer 10s ×3", 0x8009: "Timer 10s ×5",
        0x8018: "WB bracket Lo", 0x8028: "WB bracket Hi", 0x8019: "DRO bracket Lo", 0x8029: "DRO bracket Hi",
    ]

    /// The low 16 bits are the mode. Brackets pack in the shot count and the EV step, like 0x8537 for 5 shots, 0.3 EV, continuous.
    static func drive(_ v: Int64) -> String {
        let low = v & 0xFFFF
        if let name = driveNames[low] { return name }
        let shots = (low >> 8) & 0xFF, code = low & 0xFF
        let count: String
        switch shots {
        case 0xC2: count = code & 8 != 0 ? "×2 −" : "×2 +"
        case 0x83, 0x85, 0x87, 0x89: count = "×\(shots & 0xF)"
        default: return "Other"
        }
        let small: [Int64: String] = [3: "0.3", 5: "0.5", 7: "0.7"]
        let large: [Int64: String] = [1: "1.0", 4: "1.3", 6: "1.5", 8: "1.7", 2: "2.0", 5: "2.3", 7: "2.5", 9: "2.7", 3: "3.0"]
        guard let step = (code & 6 == 6 ? small : large)[code >> 4] else { return "Other" }
        return "BRK \(code & 1 == 1 ? "C" : "S") \(step) EV \(count)"
    }

    static let focusArea: [Int64: String] = [
        0x1: "Wide", 0x2: "Zone", 0x3: "Center", 0x107: "Spot XL", 0x103: "Spot L", 0x102: "Spot M", 0x101: "Spot S", 0x104: "Expand spot",
        0x201: "Track wide", 0x202: "Track zone", 0x203: "Track center", 0x20A: "Track XL", 0x206: "Track L", 0x205: "Track M",
        0x204: "Track S", 0x207: "Track expand",
    ]

    /// Long lists get headings in the chooser.
    static func group(_ code: UInt16, _ v: Int64) -> String {
        if code == Code.drive { return driveGroup(v) }
        if code == Code.focusArea { return focusArea[v]?.hasPrefix("Track") == true ? "Tracking" : "Fixed" }
        return ""
    }

    private static func driveGroup(_ v: Int64) -> String {
        let name = drive(v)
        if name.hasPrefix("Timer") { return "Self-timer" }
        if name.hasPrefix("BRK C") { return "Exposure bracket, hold the shutter" }
        if name.hasPrefix("BRK S") { return "Exposure bracket, one press a shot" }
        if name.contains("bracket") { return "Other brackets" }
        return "Shooting"
    }

    static let metering: [Int64: String] = [
        0x8001: "Multi", 0x8002: "Center", 0x8003: "Entire screen", 0x8004: "Spot", 0x8005: "Large spot", 0x8006: "Highlight",
    ]

    static let fileFormat: [Int64: String] = [1: "RAW", 2: "RAW+JPEG", 3: "JPEG"]

    static let jpegQuality: [Int64: String] = [1: "Extra fine", 2: "Fine", 3: "Standard"]

    static let imageSize: [Int64: String] = [1: "Large", 2: "Medium", 3: "Small"]

    static let aspectRatio: [Int64: String] = [1: "3:2", 2: "16:9", 3: "4:3", 4: "1:1"]

    static let offOn: [Int64: String] = [1: "Off", 2: "On"]

    private static let modeNames: [Int64: String] = [
        0x8000: "Auto", 2: "Program", 3: "Aperture", 4: "Shutter", 1: "Manual",
        0x8050: "Program", 0x8051: "Aperture", 0x8052: "Shutter", 0x8053: "Manual",
        0x8059: "S&Q Program", 0x805A: "S&Q Aperture", 0x805B: "S&Q Shutter", 0x805C: "S&Q Manual",
    ]

    static func mode(_ v: Int64) -> String { modeNames[v & 0xFFFF] ?? "Other" }

    /// M, A, S or P, as the camera's own screen shows it.
    static func modeLetter(_ name: String) -> String {
        name.replacingOccurrences(of: "Manual", with: "M").replacingOccurrences(of: "Aperture", with: "A")
            .replacingOccurrences(of: "Shutter", with: "S").replacingOccurrences(of: "Program", with: "P")
    }

    static let frameRate: [Int64: String] = [1: "120p", 2: "100p", 3: "60p", 4: "50p", 5: "30p", 6: "25p", 7: "24p"]

    /// These do match the SDK. They start at 0x26.
    static let recordSetting: [Int64: String] = Dictionary(uniqueKeysWithValues: [
        "600M 4:2:2 10-bit", "500M 4:2:2 10-bit", "400M 4:2:0 10-bit", "300M 4:2:2 10-bit", "280M 4:2:2 10-bit", "250M 4:2:2 10-bit",
        "240M 4:2:2 10-bit", "222M 4:2:2 10-bit", "200M 4:2:2 10-bit", "200M 4:2:0 10-bit", "200M 4:2:0 8-bit", "185M 4:2:2 10-bit",
        "150M 4:2:0 10-bit", "150M 4:2:0 8-bit", "140M 4:2:2 10-bit", "111M 4:2:2 10-bit", "100M 4:2:2 10-bit", "100M 4:2:0 10-bit",
        "100M 4:2:0 8-bit", "93M 4:2:2 10-bit", "89M 4:2:2 10-bit", "75M 4:2:0 10-bit", "60M 4:2:0 8-bit", "50M 4:2:2 10-bit",
        "50M 4:2:0 10-bit", "50M 4:2:0 8-bit", "45M 4:2:0 10-bit", "30M 4:2:0 10-bit", "25M 4:2:0 8-bit", "16M 4:2:0 8-bit",
    ].enumerated().map { (Int64(0x26 + $0.offset), $0.element) })

    static func battery(percent: Int64?, level: Int64?) -> String {
        if let percent, (0...100).contains(percent) { return "\(percent)%" }
        guard let level else { return "--" }
        let usb = level & 0x10000 != 0 ? " + USB" : ""
        switch level & 0xFFFF {
        case 1: return "under 10%" + usb
        case 2: return "10–25%" + usb
        case 3: return "25–50%" + usb
        case 4: return "50–75%" + usb
        case 5: return "75–100%" + usb
        case 6: return "10–33%" + usb
        case 7: return "33–66%" + usb
        case 8: return "66–100%" + usb
        default: return usb.isEmpty ? "--" : "USB power"
        }
    }

    static func duration(_ seconds: Int64) -> String {
        if seconds < 0 || seconds >= 0xFFFFFFFF { return "--" }
        let hours = seconds / 3600, minutes = seconds / 60 % 60
        return hours > 0 ? String(format: "%dh %02dm", hours, minutes) : String(format: "%dm %02ds", minutes, seconds % 60)
    }
}
