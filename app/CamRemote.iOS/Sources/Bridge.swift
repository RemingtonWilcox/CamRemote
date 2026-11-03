import Combine
import Compression
import Foundation
import Network
import Security
import UIKit

private let port: NWEndpoint.Port = 8787 // the same as CamRemote on a computer
private let cookieName = "camremote"
private let cameraId = "sony"
private let codeLetters = Array("ABCDEFGHJKMNPQRSTUVWXYZ23456789")
private let maxRequest = 64 * 1024
private let pictureWidth: CGFloat = 640
private let pictureQuality: CGFloat = 0.5
private let watchWindow = 5.0 // a computer counts as watching for this long after it asks for a picture
private let frameWait = 3.0

// Where each setting goes on the page, and how it's drawn. Same sections as on a computer.
private let pageRows: [(code: UInt16, section: String, kind: String)] = [
    (Code.shutter, "exposure", "Stepper"), (Code.aperture, "exposure", "Stepper"), (Code.iso, "exposure", "Stepper"),
    (Code.exposure, "exposure", "Stepper"), (Code.mode, "exposure", "Picker"), (Code.whiteBalance, "exposure", "Picker"),
    (Code.colorTemp, "exposure", "Picker"), (Code.focusMode, "focus", "Picker"), (Code.focusArea, "focus", "Picker"),
    (Code.movieFormat, "more", "Picker"), (Code.frameRate, "more", "Picker"), (Code.recordSetting, "more", "Picker"),
    (Code.drive, "more", "Picker"), (Code.metering, "more", "Picker"), (Code.fileFormat, "more", "Picker"),
    (Code.jpegQuality, "more", "Picker"), (Code.imageSize, "more", "Picker"), (Code.aspectRatio, "more", "Picker"),
    (Code.silent, "more", "Picker"),
]

/// How often a picture goes to the computer. Each costs mobile data, so fewer is the default.
enum Pace: Int, CaseIterable, Identifiable {
    case saver = 1, normal = 3, smooth = 8
    var id: Int { rawValue }
    var label: String {
        switch self {
        case .saver: return "1 a second"
        case .normal: return "3 a second"
        case .smooth: return "8 a second"
        }
    }
}

/// The access code and the token a browser keeps afterwards. Both live in the Keychain and are never logged.
private enum Secrets {
    private static func query(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "CamRemote computer control",
         kSecAttrAccount as String: account]
    }

    static func read(_ account: String) -> String {
        var result: AnyObject?
        var search = query(account)
        search[kSecReturnData as String] = true
        guard SecItemCopyMatching(search as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return "" }
        return String(decoding: data, as: UTF8.self)
    }

    static func write(_ account: String, _ value: String) {
        SecItemDelete(query(account) as CFDictionary)
        var item = query(account)
        item[kSecValueData as String] = Data(value.utf8)
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }

    static func random(_ count: Int) -> [UInt8] {
        var bytes = [UInt8](repeating: 0, count: count)
        _ = SecRandomCopyBytes(kSecRandomDefault, count, &bytes)
        return bytes
    }

    /// Made the first time they're needed, and again when someone asks for a new code.
    static func load(fresh: Bool = false) -> (code: String, token: String) {
        var code = read("code"), token = read("token")
        if fresh || code.isEmpty {
            // 248 is the largest multiple of 31 under 256, so every letter is equally likely.
            code = String(random(64).filter { $0 < 248 }.prefix(8).map { codeLetters[Int($0) % codeLetters.count] })
            write("code", code)
        }
        if fresh || token.isEmpty {
            token = random(32).map { String(format: "%02X", $0) }.joined()
            write("token", token)
        }
        return (code, token)
    }
}

private struct Request {
    let method: String
    let path: String
    let query: [String: String]
    let headers: [String: String]
    let body: Data
}

private struct Response {
    var status = 200
    var type = "application/json"
    var body = Data()
    var headers: [String: String] = [:]

    static func json(_ value: Any, status: Int = 200) -> Response {
        Response(status: status, body: (try? JSONSerialization.data(withJSONObject: value)) ?? Data())
    }

    static func message(_ text: String?) -> Response {
        text.map { json(["message": $0], status: 409) } ?? Response()
    }
}

/// Serves CamRemote's phone page from this iPhone, so a computer on Tailscale can watch and run the camera through it.
/// It only runs while the app is open on screen. iOS pauses it otherwise, and nothing here tries to get around that.
final class Bridge: ObservableObject {
    static let shared = Bridge()

    @Published private(set) var isOn = UserDefaults.standard.bool(forKey: "bridgeOn")
    @Published private(set) var addresses: [String] = []
    @Published private(set) var problem = ""
    @Published private(set) var watching = false
    @Published private(set) var sentBytes: Int64 = 0
    @Published private(set) var paceBytes: Int64 = 0 // since the picture setting last changed, for the hourly figure
    @Published private(set) var watchedSeconds = 0.0 // likewise
    @Published private(set) var code = ""
    @Published var pace = Pace(rawValue: UserDefaults.standard.integer(forKey: "bridgePace")) ?? .saver {
        didSet {
            UserDefaults.standard.set(pace.rawValue, forKey: "bridgePace")
            paceBytes = 0
            watchedSeconds = 0
        }
    }

    private weak var camera: Camera?
    private var listener: NWListener?
    private var clients: [ObjectIdentifier: NWConnection] = [:] // on `queue`
    private let queue = DispatchQueue(label: "bridge")
    private var token = "" // on `queue`
    private var codeOnQueue = "" // on `queue`
    private var nextLogin = Date.distantPast // on `queue`: one wrong code slows every try after it
    private var watchers: [AnyCancellable] = []
    private lazy var page = Bridge.loadPage()
    private lazy var icon = Bundle.main.url(forResource: "phone-icon", withExtension: "png").flatMap { try? Data(contentsOf: $0) } ?? Data()

    // The latest picture for the computer, shared between threads under `lock`.
    private let lock = NSLock()
    private var jpeg: Data?
    private var frameNo: Int64 = 0
    private var lastAsk = Date.distantPast
    private var unsentBytes: Int64 = 0
    private var encoding = false // main thread only
    private var lastEncode = Date.distantPast // main thread only

    /// Called once at launch with the app's camera.
    func attach(_ camera: Camera) {
        guard self.camera == nil else { return }
        self.camera = camera
        camera.$frame.sink { [weak self] in self?.offer($0) }.store(in: &watchers)
        let center = NotificationCenter.default
        center.publisher(for: UIApplication.didEnterBackgroundNotification).sink { [weak self] _ in self?.stop() }.store(in: &watchers)
        center.publisher(for: UIApplication.willEnterForegroundNotification).sink { [weak self] _ in
            if self?.isOn == true { self?.start() }
        }.store(in: &watchers)
        // Counts are published once a second, not once a picture.
        Timer.publish(every: 1, on: .main, in: .common).autoconnect().sink { [weak self] _ in self?.tick() }.store(in: &watchers)
        if isOn { start() }
    }

    func turn(on: Bool) {
        isOn = on
        UserDefaults.standard.set(on, forKey: "bridgeOn")
        if on { start() } else { stop() }
    }

    /// Signs out every browser that used the old one.
    func newCode() {
        let fresh = Secrets.load(fresh: true)
        code = fresh.code
        queue.async {
            self.token = fresh.token
            self.codeOnQueue = fresh.code
            for connection in self.clients.values { connection.cancel() }
            self.clients = [:]
        }
    }

    func refreshAddresses() {
        addresses = Self.findAddresses()
    }

    private func start() {
        guard listener == nil else { return }
        let secrets = Secrets.load()
        code = secrets.code
        queue.async {
            self.token = secrets.token
            self.codeOnQueue = secrets.code
        }
        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        do {
            let made = try NWListener(using: parameters, on: port)
            made.newConnectionHandler = { [weak self] in self?.accept($0) }
            made.stateUpdateHandler = { [weak self] state in
                DispatchQueue.main.async { self?.listenerChanged(state) }
            }
            made.start(queue: queue)
            listener = made
            problem = ""
        } catch {
            problem = "Couldn't start. Turn it off and on again."
        }
        refreshAddresses()
        keepAwake()
    }

    private func stop() {
        listener?.cancel()
        listener = nil
        queue.async {
            for connection in self.clients.values { connection.cancel() }
            self.clients = [:]
        }
        lock.lock()
        lastAsk = .distantPast
        jpeg = nil
        lock.unlock()
        watching = false
        keepAwake()
    }

    private func listenerChanged(_ state: NWListener.State) {
        switch state {
        case .ready:
            problem = ""
            refreshAddresses()
        case .failed:
            listener?.cancel()
            listener = nil
            problem = "Couldn't open port \(port). Turn it off and on again."
        default:
            break
        }
    }

    /// Anything else that needs the screen on, like the animal trigger. Set once at launch.
    var alsoAwake: () -> Bool = { false }

    /// A computer only sees anything while this screen is on, so it stays on. The one place that decides it, so the app
    /// also calls this when the camera connects or drops, and when the animal trigger is turned on or off.
    func keepAwake() {
        UIApplication.shared.isIdleTimerDisabled = isOn || camera?.link == .on || alsoAwake()
    }

    private func tick() {
        lock.lock()
        let asked = lastAsk, unsent = unsentBytes
        unsentBytes = 0
        lock.unlock()
        let now = Date().timeIntervalSince(asked) < watchWindow
        if now != watching { watching = now }
        if now { watchedSeconds += 1 }
        if unsent > 0 {
            sentBytes += unsent
            paceBytes += unsent
        }
    }

    // MARK: Pictures

    /// Runs on the main thread for every picture the camera sends. Only some are shrunk and kept, and only while a computer is watching.
    private func offer(_ image: UIImage?) {
        guard let image, listener != nil, !encoding, Date().timeIntervalSince(lastEncode) >= 1 / Double(pace.rawValue) - 0.02 else { return }
        lock.lock()
        let wanted = Date().timeIntervalSince(lastAsk) < watchWindow
        lock.unlock()
        guard wanted else { return }
        encoding = true
        lastEncode = Date()
        DispatchQueue.global(qos: .utility).async {
            let data = autoreleasepool { Self.shrink(image) }
            self.lock.lock()
            if let data {
                self.jpeg = data
                self.frameNo += 1
            }
            self.lock.unlock()
            DispatchQueue.main.async { self.encoding = false }
        }
    }

    private static func shrink(_ image: UIImage) -> Data? {
        let size = image.size
        guard size.width > 0 else { return nil }
        let scale = min(1, pictureWidth / size.width)
        let target = CGSize(width: (size.width * scale).rounded(), height: (size.height * scale).rounded())
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        format.opaque = true
        let small = UIGraphicsImageRenderer(size: target, format: format).image { _ in image.draw(in: CGRect(origin: .zero, size: target)) }
        return small.jpegData(compressionQuality: pictureQuality)
    }

    // MARK: Connections

    private func accept(_ connection: NWConnection) {
        guard Self.allowed(connection.endpoint) else {
            connection.cancel()
            return
        }
        let key = ObjectIdentifier(connection)
        clients[key] = connection
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled: self?.clients[key] = nil
            default: break
            }
        }
        connection.start(queue: queue)
        receive(connection, Data())
    }

    /// Only Tailscale, home network and hotspot addresses are answered, like CamRemote on a computer.
    private static func allowed(_ endpoint: NWEndpoint) -> Bool {
        guard case let .hostPort(host, _) = endpoint else { return false }
        var bytes: [UInt8]
        switch host {
        case let .ipv4(address): bytes = Array(address.rawValue)
        case let .ipv6(address):
            if let v4 = address.asIPv4 { bytes = Array(v4.rawValue) } else {
                let b = Array(address.rawValue)
                return address == .loopback || b.first.map { $0 & 0xFE == 0xFC } == true || address.isLinkLocal // fd7a:… is Tailscale's
            }
        default: return false
        }
        guard bytes.count == 4 else { return false }
        return bytes[0] == 127 || bytes[0] == 10 || (bytes[0] == 172 && (16...31).contains(bytes[1])) || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 100 && (64...127).contains(bytes[1]))
    }

    private func receive(_ connection: NWConnection, _ buffer: Data) {
        if let (request, rest) = Self.parse(buffer) {
            handle(request) { [weak self] response in
                self?.send(Self.squeeze(response, for: request), on: connection) { self?.receive(connection, rest) }
            }
            return
        }
        guard buffer.count < maxRequest else {
            send(Response(status: 413), on: connection, close: true) {}
            return
        }
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, done, error in
            guard let self, error == nil else {
                connection.cancel()
                return
            }
            if let data, !data.isEmpty {
                self.receive(connection, buffer + data)
            } else if done {
                connection.cancel()
            } else {
                self.receive(connection, buffer)
            }
        }
    }

    private static func parse(_ buffer: Data) -> (Request, Data)? {
        guard let end = buffer.range(of: Data("\r\n\r\n".utf8)) else { return nil }
        let head = String(decoding: buffer[buffer.startIndex..<end.lowerBound], as: UTF8.self)
        var lines = head.components(separatedBy: "\r\n")
        let first = lines.removeFirst().split(separator: " ")
        guard first.count >= 2 else { return nil }
        var headers: [String: String] = [:]
        for line in lines {
            guard let colon = line.firstIndex(of: ":") else { continue }
            headers[line[..<colon].lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        let length = Int(headers["content-length"] ?? "") ?? 0
        guard length >= 0, buffer.distance(from: end.upperBound, to: buffer.endIndex) >= length else { return nil }
        let bodyEnd = buffer.index(end.upperBound, offsetBy: length)
        let target = String(first[1])
        let parts = target.split(separator: "?", maxSplits: 1)
        var query: [String: String] = [:]
        if parts.count == 2 {
            for pair in parts[1].split(separator: "&") {
                let kv = pair.split(separator: "=", maxSplits: 1)
                query[String(kv[0])] = kv.count == 2 ? String(kv[1]).removingPercentEncoding : ""
            }
        }
        let request = Request(method: String(first[0]), path: String(parts.first ?? ""), query: query, headers: headers,
                              body: Data(buffer[end.upperBound..<bodyEnd]))
        return (request, Data(buffer[bodyEnd...]))
    }

    /// The page asks for the camera's settings every second, and as text they shrink to a tenth.
    private static func squeeze(_ response: Response, for request: Request) -> Response {
        guard response.body.count > 512, !response.type.hasPrefix("image/"),
              request.headers["accept-encoding"]?.contains("gzip") == true, let packed = gzip(response.body) else { return response }
        var smaller = response
        smaller.body = packed
        smaller.headers["Content-Encoding"] = "gzip"
        return smaller
    }

    private static let crcTable: [UInt32] = (0..<256).map { n in
        (0..<8).reduce(UInt32(n)) { c, _ in c & 1 == 1 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1 }
    }

    private static func gzip(_ data: Data) -> Data? {
        let capacity = data.count + 1024
        var packed = [UInt8](repeating: 0, count: capacity)
        let count = data.withUnsafeBytes { source in
            compression_encode_buffer(&packed, capacity, source.bindMemory(to: UInt8.self).baseAddress!, data.count, nil, COMPRESSION_ZLIB)
        }
        guard count > 0 else { return nil }
        let crc = ~data.reduce(UInt32.max) { crcTable[Int(($0 ^ UInt32($1)) & 0xFF)] ^ ($0 >> 8) }
        // Apple's zlib option is plain deflate, so it only needs gzip's header and trailer around it.
        return Data([0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 255]) + packed[0..<count] + u32le(crc) + u32le(UInt32(truncatingIfNeeded: data.count))
    }

    private static func u32le(_ value: UInt32) -> [UInt8] { withUnsafeBytes(of: value.littleEndian, Array.init) }

    private func send(_ response: Response, on connection: NWConnection, close: Bool = false, then next: @escaping () -> Void) {
        let reasons = [200: "OK", 204: "No Content", 400: "Bad Request", 401: "Unauthorized", 403: "Forbidden", 404: "Not Found",
                       409: "Conflict", 413: "Payload Too Large"]
        var head = "HTTP/1.1 \(response.status) \(reasons[response.status] ?? "Error")\r\n"
        var headers = response.headers
        headers["Content-Length"] = String(response.body.count)
        headers["Connection"] = close ? "close" : "keep-alive"
        if response.status != 204 { headers["Content-Type"] = response.type }
        if headers["Cache-Control"] == nil { headers["Cache-Control"] = "no-store" }
        for (name, value) in headers { head += "\(name): \(value)\r\n" }
        let bytes = Data((head + "\r\n").utf8) + response.body
        lock.lock()
        unsentBytes += Int64(bytes.count)
        lock.unlock()
        connection.send(content: bytes, completion: .contentProcessed { error in
            if close || error != nil { connection.cancel() } else { next() }
        })
    }

    // MARK: The page's API, the same one CamRemote on a computer answers

    /// Runs on `queue`. Anything that touches the camera hops to the main thread, where the camera's controls live.
    private func handle(_ request: Request, reply: @escaping (Response) -> Void) {
        let path = request.path
        if path.hasPrefix("/api/"), path != "/api/login" {
            guard Self.same(cookie(request), token) else { return reply(Response(status: 401)) }
            // Browsers won't add this header on another site's behalf.
            guard request.method == "GET" || request.headers["x-camremote"] != nil else { return reply(Response(status: 403)) }
        }
        let body = (try? JSONSerialization.jsonObject(with: request.body)) as? [String: Any] ?? [:]
        switch (request.method, path) {
        case ("GET", "/"):
            reply(Response(type: "text/html; charset=utf-8", body: page))
        case ("GET", "/icon.png"):
            reply(Response(type: "image/png", body: icon))
        case ("GET", "/manifest.webmanifest"):
            reply(.json(["name": "CamRemote", "short_name": "CamRemote", "start_url": "/", "display": "standalone",
                         "background_color": "#101214", "theme_color": "#101214",
                         "icons": [["src": "/icon.png", "sizes": "180x180", "type": "image/png"]]]))
        case ("POST", "/api/login"):
            login(body["code"] as? String, reply: reply)
        case ("GET", "/api/cameras"):
            reply(.json([["id": cameraId, "name": "Camera"]]))
        case ("GET", "/api/\(cameraId)/state"):
            reply(.json(DispatchQueue.main.sync { state() }))
        case ("GET", "/api/\(cameraId)/frame"):
            frame(after: Int64(request.query["after"] ?? ""), since: Date(), reply: reply)
        case ("POST", "/api/\(cameraId)/set"):
            let id = body["id"] as? String ?? "", value = body["value"] as? String ?? ""
            reply(.message(DispatchQueue.main.sync { set(id, value) }))
        case ("POST", "/api/\(cameraId)/record"):
            let on = body["on"] as? Bool ?? false
            reply(.message(DispatchQueue.main.sync { record(on) }))
        case ("POST", "/api/\(cameraId)/focus"):
            let x = (body["x"] as? Double) ?? -1, y = (body["y"] as? Double) ?? -1
            reply(.message(DispatchQueue.main.sync { focus(x, y) }))
        case ("POST", "/api/\(cameraId)/focusstep"):
            let step = max(-7, min(7, (body["step"] as? Int) ?? 0))
            reply(.message(DispatchQueue.main.sync { focusStep(step) }))
        default:
            reply(Response(status: 404))
        }
    }

    private func cookie(_ request: Request) -> String? {
        for part in (request.headers["cookie"] ?? "").split(separator: ";") {
            let kv = part.trimmingCharacters(in: .whitespaces).split(separator: "=", maxSplits: 1)
            if kv.count == 2, kv[0] == cookieName { return String(kv[1]) }
        }
        return nil
    }

    private static func same(_ given: String?, _ expected: String) -> Bool {
        guard let given, !expected.isEmpty else { return false }
        let a = Array(given.utf8), b = Array(expected.utf8)
        guard a.count == b.count else { return false }
        return zip(a, b).reduce(0) { $0 | ($1.0 ^ $1.1) } == 0
    }

    private func login(_ code: String?, reply: @escaping (Response) -> Void) {
        guard Date() >= nextLogin, Self.same(code?.trimmingCharacters(in: .whitespaces).uppercased(), codeOnQueue) else {
            nextLogin = Date().addingTimeInterval(0.5) // keeps guessing slow
            queue.asyncAfter(deadline: .now() + 0.5) { reply(Response(status: 401)) }
            return
        }
        reply(Response(headers: ["Set-Cookie": "\(cookieName)=\(token); Path=/; Max-Age=31536000; HttpOnly; SameSite=Strict"]))
    }

    /// Waits briefly for a picture newer than the last one, so the picture paces itself to the connection.
    private func frame(after: Int64?, since: Date, reply: @escaping (Response) -> Void) {
        lock.lock()
        lastAsk = Date()
        let data = jpeg, number = frameNo
        lock.unlock()
        if let data, number != after {
            return reply(Response(type: "image/jpeg", body: data, headers: ["X-Frame": String(number)]))
        }
        guard Date().timeIntervalSince(since) < frameWait else { return reply(Response(status: 204)) }
        queue.asyncAfter(deadline: .now() + 0.05) { self.frame(after: after, since: since, reply: reply) }
    }

    // MARK: The camera, on the main thread

    private func state() -> [String: Any] {
        guard let camera else { return ["link": "disconnected", "rows": []] }
        let connected = camera.link == .on
        func value(_ row: Row) -> Int64? { row.choices.first { $0.text == row.text }?.value }
        let colorTemp = camera.rows[Code.whiteBalance].flatMap(value) == 0x8012
        var rows: [[String: Any]] = []
        for (code, section, kind) in pageRows where code != Code.colorTemp || colorTemp {
            guard let row = camera.rows[code], !row.choices.isEmpty else { continue }
            let value = value(row)
            rows.append(["id": String(code), "label": row.label, "section": section, "kind": kind,
                         "value": value.map { String($0) } ?? "", "text": row.text,
                         "enabled": connected && row.choices.count > 1,
                         "choices": row.choices.map { ["value": String($0.value), "text": $0.text] }])
        }
        let alert = !connected ? "" : camera.lowBattery ? "Camera battery low" : camera.lowCard ? "Card nearly full" : ""
        return [
            "link": connected ? "connected" : camera.link == .connecting || camera.keepTrying ? "connecting" : "disconnected",
            "recording": camera.recording,
            "rotation": UserDefaults.standard.integer(forKey: "pictureTurn"),
            "battery": camera.battery, "card": camera.card, "alert": alert,
            "baseIso": [Int](), "manualFocus": camera.manualFocus, "meter": NSNull(),
            "magnify": 0, "magnifyChoices": [Int](), "tracking": false,
            "zebraLevel": zebraPercent, "peakSensitivity": 1, "peakColor": 0xFF3030,
            "rows": rows,
        ]
    }

    private func set(_ id: String, _ value: String) -> String? {
        guard let camera, camera.link == .on else { return "The camera isn't connected." }
        guard let v = Int64(value) else { return "That value isn't valid." }
        guard let code = UInt16(id), let row = camera.rows[code], row.choices.contains(where: { $0.value == v }) else {
            return "The camera isn't offering that right now."
        }
        camera.set(code, v)
        return nil
    }

    private func record(_ on: Bool) -> String? {
        guard let camera, camera.link == .on else { return "The camera isn't connected." }
        if camera.recording != on { camera.toggleRecord() } // a repeated tap on a slow link can't flip it back
        return nil
    }

    private func focus(_ x: Double, _ y: Double) -> String? {
        guard let camera, camera.link == .on else { return "The camera isn't connected." }
        guard (0...1).contains(x), (0...1).contains(y) else { return "That spot isn't on the picture." }
        camera.focus(x: x, y: y)
        return nil
    }

    private func focusStep(_ step: Int) -> String? {
        guard let camera, camera.link == .on else { return "The camera isn't connected." }
        _ = camera.focusStep(step) // one at a time; the page sends the next
        return nil
    }

    // MARK: Setup

    /// The page CamRemote on a computer serves, with its words about the computer changed to this phone.
    private static func loadPage() -> Data {
        guard let url = Bundle.main.url(forResource: "phone", withExtension: "html"),
              var html = try? String(contentsOf: url, encoding: .utf8) else { return Data("CamRemote".utf8) }
        for (old, new) in [("Enter the phone access code shown in the app on your PC.", "Enter the access code shown in CamRemote on your iPhone, under Setup."),
                           ("Can't reach the PC", "Can't reach the iPhone"), ("The PC refused that.", "The iPhone refused that."),
                           // Kept for the page's script, but this phone has no audio settings or magnifier to offer.
                           ("<h2>AUDIO</h2><div id=\"audio\"></div>", "<div id=\"audio\" class=\"hide\"></div>"),
                           ("<div class=\"lbl\">Focus magnifier</div>", "")] {
            html = html.replacingOccurrences(of: old, with: new)
        }
        return Data(html.utf8)
    }

    /// Tailscale's address first, since that's the one a computer elsewhere uses.
    private static func findAddresses() -> [String] {
        var found: [(Int, String)] = []
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return [] }
        defer { freeifaddrs(list) }
        for entry in sequence(first: first, next: { $0.pointee.ifa_next }) {
            guard let address = entry.pointee.ifa_addr, address.pointee.sa_family == UInt8(AF_INET),
                  entry.pointee.ifa_flags & UInt32(IFF_UP) != 0 else { continue }
            let name = String(cString: entry.pointee.ifa_name)
            var raw = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr }
            var text = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            inet_ntop(AF_INET, &raw, &text, socklen_t(INET_ADDRSTRLEN))
            let ip = String(cString: text)
            let parts = ip.split(separator: ".").compactMap { Int($0) }
            guard parts.count == 4 else { continue }
            if parts[0] == 100, (64...127).contains(parts[1]) {
                found.append((0, "Tailscale: http://\(ip):\(port)"))
            } else if name.hasPrefix("en"), ip != "0.0.0.0", !ip.hasPrefix("169.254.") {
                found.append((1, "Wi-Fi: http://\(ip):\(port)"))
            }
        }
        return found.sorted { $0.0 < $1.0 }.map(\.1)
    }
}
