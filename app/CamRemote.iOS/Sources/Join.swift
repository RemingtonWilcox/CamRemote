import CryptoKit
import Security
import SwiftUI
import UIKit

private let joinPort = 8787 // the same on every RemoteCam

/// RemoteCam on a computer names each setting as Sony's SDK does, where this app uses the camera's own codes. The values match.
private let computerNames: [String: UInt16] = [
    "ShutterSpeed": Code.shutter, "FNumber": Code.aperture, "IsoSensitivity": Code.iso, "ExposureBiasCompensation": Code.exposure,
    "ExposureProgramMode": Code.mode, "WhiteBalance": Code.whiteBalance, "Colortemp": Code.colorTemp, "FocusMode": Code.focusMode,
    "FocusArea": Code.focusArea, "Movie_File_Format": Code.movieFormat, "Movie_Recording_Setting": Code.recordSetting,
    "Movie_Recording_FrameRateSetting": Code.frameRate, "SQFrameRate": Code.sqFrameRate, "SQRecordingFrameRateSetting": Code.sqPlayRate,
    "SQRecordingSetting": Code.sqRecordSetting, "DriveMode": Code.drive, "MeteringMode": Code.metering, "FileType": Code.fileFormat,
    "StillImageQuality": Code.jpegQuality, "JpegQuality": Code.jpegQuality, "ImageSize": Code.imageSize, "AspectRatio": Code.aspectRatio,
    "SilentMode": Code.silent,
]
private let unreachable = "Can't reach the other device. Check RemoteCam is open on it, on the same Wi-Fi or hotspot as this one."

/// This device as other RemoteCams know it. The id is made once and kept, so an "Allow" there lasts.
enum ThisDevice {
    static let id: String = {
        if let saved = UserDefaults.standard.string(forKey: "deviceId"), saved.count == 32 { return saved }
        var bytes = [UInt8](repeating: 0, count: 16)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        let made = bytes.map { String(format: "%02x", $0) }.joined()
        UserDefaults.standard.set(made, forKey: "deviceId")
        return made
    }()

    static var kind: String { AppDelegate.pad ? "iPad" : "iPhone" }
}

/// What other RemoteCams handed this one when they let it in, kept in the Keychain. Newer ones are by the other
/// device's id; older ones, from when an access code was typed, by its address.
enum JoinTokens {
    private static func query(_ service: String, _ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
    }

    private static func read(_ service: String, _ account: String) -> String {
        var search = query(service, account)
        search[kSecReturnData as String] = true
        var result: AnyObject?
        guard SecItemCopyMatching(search as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return "" }
        return String(decoding: data, as: UTF8.self)
    }

    private static func write(_ service: String, _ account: String, _ token: String) {
        SecItemDelete(query(service, account) as CFDictionary)
        guard !token.isEmpty else { return }
        var item = query(service, account)
        item[kSecValueData as String] = Data(token.utf8)
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }

    static func read(host id: String) -> String { read("CamRemote host", id) }
    static func write(host id: String, _ token: String) { write("CamRemote host", id, token) }
    static func read(address: String) -> String { read("CamRemote join", address) }
    static func forget(address: String) { write("CamRemote join", address, "") }
}

/// Another RemoteCam nearby that has the camera.
struct OtherRemoteCam: Identifiable, Equatable {
    let id: String
    let name: String
    let address: String // like 10.0.0.12:8787

    var icon: String {
        name.localizedCaseInsensitiveContains("iPhone") ? "iphone"
            : name.localizedCaseInsensitiveContains("iPad") ? "ipad" : "desktopcomputer"
    }
}

/// Finds other RemoteCams by asking every address on this Wi-Fi or hotspot who it is, and the addresses of ones
/// joined before, which can include Tailscale ones for when this device is away from home. Nothing to type.
enum Nearby {
    private static let hostsKey = "joinHosts" // [id: [address]], for the ones that let this device in
    static var joining = false // main thread: one join at a time

    private static func session(_ wait: TimeInterval) -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.httpShouldSetCookies = false
        config.timeoutIntervalForRequest = wait
        config.httpMaximumConnectionsPerHost = 2
        return URLSession(configuration: config)
    }

    static var saved: [String: [String]] {
        UserDefaults.standard.dictionary(forKey: hostsKey) as? [String: [String]] ?? [:]
    }

    static func remember(_ id: String, _ addresses: [String]) {
        var all = saved
        var kept: [String] = []
        for address in addresses.map(JoinLink.normalize) where !address.isEmpty && !kept.contains(address) { kept.append(address) }
        all[id] = Array(kept.prefix(6))
        UserDefaults.standard.set(all, forKey: hostsKey)
    }

    /// Every other address on this device's Wi-Fi or hotspot. A big network is cut to the 254 addresses around this one.
    static func neighbours() -> [String] {
        var found: [String] = []
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return [] }
        defer { freeifaddrs(list) }
        for entry in sequence(first: first, next: { $0.pointee.ifa_next }) {
            guard let address = entry.pointee.ifa_addr, address.pointee.sa_family == UInt8(AF_INET), let mask = entry.pointee.ifa_netmask,
                  entry.pointee.ifa_flags & UInt32(IFF_UP) != 0, String(cString: entry.pointee.ifa_name).hasPrefix("en") else { continue }
            let ip = address.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            var net = mask.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            guard ip >> 16 != 0xA9FE else { continue } // 169.254, no network yet
            if net < 0xFFFF_FF00 { net = 0xFFFF_FF00 }
            let base = ip & net
            for host in 1..<(~net) where base | host != ip {
                let a = base | host
                found.append("\(a >> 24).\(a >> 16 & 0xFF).\(a >> 8 & 0xFF).\(a & 0xFF):\(joinPort)")
            }
        }
        return found
    }

    /// The RemoteCams that answer and have a camera, nearest first. Learns the addresses of ones this device may join.
    static func find() async -> [OtherRemoteCam] {
        let known = saved
        var tokens: [String: String] = [:]
        for (id, addresses) in known {
            let token = JoinTokens.read(host: id)
            if !token.isEmpty { for address in addresses { tokens[address] = token } }
        }
        let tokenAt = tokens
        let targets = Set(neighbours()).union(known.values.joined())
        let session = session(2)
        defer { session.finishTasksAndInvalidate() }
        var answers: [(OtherRemoteCam, [String])] = []
        await withTaskGroup(of: (OtherRemoteCam, [String])?.self) { group in
            for target in targets { group.addTask { await hello(target, token: tokenAt[target], session: session) } }
            for await answer in group { if let answer { answers.append(answer) } }
        }
        var byId: [String: OtherRemoteCam] = [:]
        for (other, addresses) in answers {
            if !addresses.isEmpty, !JoinTokens.read(host: other.id).isEmpty { remember(other.id, [other.address] + addresses) }
            // The home network beats Tailscale, which only carries it when away.
            if let had = byId[other.id], !had.address.hasPrefix("100.") { continue }
            byId[other.id] = other
        }
        return byId.values.sorted { $0.name < $1.name }
    }

    private static func hello(_ address: String, token: String?, session: URLSession) async -> (OtherRemoteCam, [String])? {
        guard let url = URL(string: "http://\(address)/api/hello") else { return nil }
        var request = URLRequest(url: url)
        if let token { request.setValue("camremote=\(token)", forHTTPHeaderField: "Cookie") } // so it says its other addresses
        guard let (data, response) = try? await session.data(for: request), (response as? HTTPURLResponse)?.statusCode == 200,
              let reply = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              reply["app"] as? String == "egret", reply["camera"] as? Bool == true, // "egret": how every version knows another
              let id = reply["id"] as? String, id != ThisDevice.id else { return nil }
        let name = (reply["name"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return (OtherRemoteCam(id: id, name: name.isEmpty ? "Another device" : String(name.prefix(40)), address: address),
                reply["addresses"] as? [String] ?? [])
    }

    /// The camera's password through HMAC-SHA256 over both devices' ids, as RemoteCam on a computer checks it.
    static func proof(_ password: String, joiner: String, host: String) -> String {
        let code = HMAC<SHA256>.authenticationCode(for: Data("camremote join \(joiner) \(host)".utf8), using: SymmetricKey(data: Data(password.utf8)))
        return code.map { String(format: "%02x", $0) }.joined()
    }

    /// Asks the other device to let this one in. Its person answers there, so this waits for them.
    static func askToJoin(_ other: OtherRemoteCam) async -> (token: String?, problem: String) {
        guard let url = URL(string: "http://\(other.address)/api/pair") else { return (nil, unreachable) }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("1", forHTTPHeaderField: "X-CamRemote")
        // Knowing the camera's password lets it in without anyone tapping Allow there. The password itself isn't sent.
        let proof = Logins.all().prefix(8).map { Nearby.proof($0.login.password, joiner: ThisDevice.id, host: other.id) }
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["id": ThisDevice.id, "name": UIDevice.current.name, "proof": proof])
        let session = session(130)
        defer { session.finishTasksAndInvalidate() }
        for _ in 0..<5 {
            guard let (data, response) = try? await session.data(for: request), let http = response as? HTTPURLResponse else {
                return (nil, unreachable)
            }
            switch http.statusCode {
            case 200:
                let token = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["token"] as? String ?? ""
                return token.isEmpty ? (nil, "\(other.name) couldn't let this \(ThisDevice.kind) in.") : (token, "")
            case 408: continue // nobody has answered yet; the question is still up
            case 409: return (nil, "\(other.name) is asking about another device. Answer that first, then try again.")
            case 429: return (nil, "\(other.name) said no a moment ago. Try again in a few minutes.")
            default: return (nil, "\(other.name) didn't let this \(ThisDevice.kind) in. If nothing came up there, turn on Let my other devices join in its Setup.")
            }
        }
        return (nil, "Nobody answered on \(other.name).")
    }
}

/// Another CamRemote that holds the camera, usually the iPhone whose hotspot the camera is on. It's the same control API
/// a computer uses, so this device runs the camera through it instead of taking the camera over.
final class JoinLink {
    let address: String // like 172.20.10.1:8787
    let name: String // the other device, as it calls itself
    var ids: [UInt16: String] = [:] // main thread: what the other device calls each setting, for changing it there
    private let token: String
    private let session: URLSession
    private var tasks: [Task<Void, Never>] = []

    init(address: String, name: String, token: String) {
        self.address = address
        self.name = name
        self.token = token
        session = Self.session()
    }

    private static func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.httpShouldSetCookies = false
        config.timeoutIntervalForRequest = 6
        return URLSession(configuration: config)
    }

    /// Also takes what Setup shows on the other device, like "Wi-Fi: http://172.20.10.1:8787".
    static func normalize(_ typed: String) -> String {
        var text = typed.trimmingCharacters(in: .whitespaces)
        if let colon = text.range(of: ": "), !text.hasPrefix("http") { text = String(text[colon.upperBound...]) }
        for scheme in ["http://", "https://"] where text.hasPrefix(scheme) { text = String(text.dropFirst(scheme.count)) }
        while text.hasSuffix("/") { text.removeLast() }
        if !text.isEmpty, !text.contains(":") { text += ":8787" }
        return text
    }

    private func request(_ path: String) -> URLRequest {
        var request = URLRequest(url: URL(string: "http://\(address)/api/sony/\(path)")!)
        request.setValue("camremote=\(token)", forHTTPHeaderField: "Cookie")
        request.setValue("1", forHTTPHeaderField: "X-CamRemote")
        return request
    }

    /// Nil when it can't be reached. A 401 means the other device was given a new code.
    func state() async -> (status: Int, state: [String: Any])? {
        guard let (data, response) = try? await session.data(for: request("state")), let http = response as? HTTPURLResponse else { return nil }
        return (http.statusCode, (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:])
    }

    /// Runs on the main thread, like the camera's own controls. A refusal comes back as the other app's message.
    func post(_ path: String, _ body: [String: Any], camera: Camera, done: String? = nil, then: @escaping () -> Void = {}) {
        var request = request(path)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: body)
        let session = session
        Task { @MainActor in
            defer { then() }
            guard let (data, response) = try? await session.data(for: request), let http = response as? HTTPURLResponse else {
                camera.message = unreachable
                return
            }
            if http.statusCode == 404 {
                camera.message = "The other RemoteCam is an older version without that. Update it first."
            } else if http.statusCode != 200 {
                let reply = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
                camera.message = reply?["message"] as? String ?? "The other RemoteCam refused that."
            } else if let done {
                camera.message = done
            }
        }
    }

    /// Settings once a second, and pictures as fast as they come.
    func run(camera: Camera) {
        let session = session
        tasks.append(Task { @MainActor [weak camera] in
            var misses = 0
            while !Task.isCancelled, let camera {
                if let (status, state) = await self.state(), status == 200 {
                    misses = 0
                    camera.applyJoined(state)
                } else {
                    misses += 1
                    if misses == 3 { camera.message = unreachable }
                    if misses >= 15 { return camera.leave(problem: unreachable) }
                }
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
        })
        tasks.append(Task { @MainActor [weak camera] in
            var after = ""
            while !Task.isCancelled, camera != nil {
                guard let (data, response) = try? await session.data(for: self.request("frame?after=\(after)")),
                      let http = response as? HTTPURLResponse else {
                    try? await Task.sleep(nanoseconds: 500_000_000)
                    continue
                }
                guard http.statusCode == 200, let image = UIImage(data: data), let camera else {
                    if http.statusCode != 204 { try? await Task.sleep(nanoseconds: 1_000_000_000) }
                    continue
                }
                after = http.value(forHTTPHeaderField: "X-Frame") ?? ""
                camera.joinedBox = JoinedAuto.box(http.value(forHTTPHeaderField: "X-Animal"))
                camera.frame = image
                camera.frameTime = Date()
                camera.readGuides(image)
            }
        })
    }

    func stop() {
        tasks.forEach { $0.cancel() }
        tasks = []
        session.invalidateAndCancel()
    }
}

extension Camera {
    /// Uses the camera through another RemoteCam. The first time, the person there taps Allow; after that it just joins.
    func joinOther(_ host: OtherRemoteCam) {
        guard join == nil, link != .on, !Nearby.joining else { return }
        Nearby.joining = true
        stopTrying()
        link = .connecting
        stage = "Connecting to \(host.name)…"
        Task { @MainActor in
            defer { Nearby.joining = false }
            var token = JoinTokens.read(host: host.id)
            if token.isEmpty { token = JoinTokens.read(address: host.address) } // let in with an access code, before
            var other = JoinLink(address: host.address, name: host.name, token: token)
            var answer = token.isEmpty ? nil : await other.state()
            if !token.isEmpty, answer == nil { return failJoin(unreachable) }
            if answer?.status != 200 {
                JoinTokens.write(host: host.id, "")
                JoinTokens.forget(address: host.address)
                stage = "Tap Allow on \(host.name) to let this \(ThisDevice.kind) in."
                let asked = await Nearby.askToJoin(host)
                guard let fresh = asked.token else { return failJoin(asked.problem) }
                token = fresh
                other = JoinLink(address: host.address, name: host.name, token: token)
                answer = await other.state()
            }
            guard let (status, state) = answer else { return failJoin(unreachable) }
            guard status == 200 else { return failJoin("\(host.name) didn't let this \(ThisDevice.kind) in. Try again.") }
            JoinTokens.write(host: host.id, token)
            if Nearby.saved[host.id] == nil { Nearby.remember(host.id, [host.address]) }
            join = other
            busyElsewhere = false
            problem = ""
            stage = ""
            applyJoined(state)
            link = .on
            message = "Using the camera through \(host.name)."
            other.run(camera: self)
        }
    }

    private func failJoin(_ text: String) {
        link = .off
        stage = ""
        problem = text
    }

    /// Hands back to this device's own connecting. The other CamRemote keeps the camera.
    func leave(problem text: String = "") {
        join?.stop()
        join = nil
        rows = [:]
        frame = nil
        recording = false
        timeLapseRunning = false
        joinedPresets = [:]
        joinedAuto = nil
        joinedBox = nil
        activeSlot = nil
        problem = text
        link = .off
    }

    /// The other app's settings, in the same shape it sends a computer. Its rows carry this app's own codes.
    func applyJoined(_ state: [String: Any]) {
        let lost = "The other RemoteCam has lost the camera. It's looking for it."
        if state["link"] as? String != "connected", message != lost { message = lost }
        var shown: [UInt16: Row] = [:]
        for case let item as [String: Any] in state["rows"] as? [Any] ?? [] {
            let id = item["id"] as? String ?? ""
            guard let code = UInt16(id) ?? computerNames[id] else { continue }
            join?.ids[code] = id
            let choices = (item["choices"] as? [[String: Any]] ?? []).compactMap { choice -> Choice? in
                guard let value = Int64(choice["value"] as? String ?? "") else { return nil }
                return Choice(value: value, text: choice["text"] as? String ?? "", group: Format.group(code, value))
            }
            shown[code] = Row(code: code, label: item["label"] as? String ?? "", text: item["text"] as? String ?? "", choices: choices)
        }
        rows = shown
        let isRecording = state["recording"] as? Bool ?? false
        if isRecording != recording { recordingSince = isRecording ? Date() : nil }
        recording = isRecording
        battery = state["battery"] as? String ?? ""
        card = state["card"] as? String ?? ""
        let alert = state["alert"] as? String ?? ""
        lowBattery = alert.localizedCaseInsensitiveContains("battery")
        lowCard = alert.localizedCaseInsensitiveContains("card")
        manualFocus = state["manualFocus"] as? Bool ?? false
        photoMode = state["photo"] as? Bool ?? false
        slowQuick = state["slowQuick"] as? Bool ?? false
        offersSlowQuick = state["offersSlowQuick"] as? Bool ?? false
        timeLapseRunning = state["timeLapse"] as? Bool ?? false
        var lines: [Int: [String]] = [:]
        for case let item as [String: Any] in state["presets"] as? [Any] ?? [] {
            if let slot = item["slot"] as? Int { lines[slot] = item["lines"] as? [String] ?? [] }
        }
        joinedPresets = lines
        activeSlot = state["activeSlot"] as? Int
        if Date() >= autoSentAt + 2 { joinedAuto = JoinedAuto(state["auto"]) } // a change just sent shows until the other app has it
        cameraKey = ""
    }
}

/// Automatic recording on the RemoteCam that holds the camera. While using its camera, this device shows and changes
/// that one instead of running its own detector, so only one of them decides when to record.
struct JoinedAuto: Equatable {
    var available: Bool
    var on: Bool
    var subject: String
    var subjects: [String]
    var stopAfter: Int
    var stops: [Int]
    var status: String

    /// Nil from a RemoteCam too old to share it.
    init?(_ any: Any?) {
        guard let a = any as? [String: Any] else { return nil }
        available = a["available"] as? Bool ?? false
        on = a["on"] as? Bool ?? false
        subject = a["subject"] as? String ?? ""
        subjects = a["subjects"] as? [String] ?? []
        stopAfter = a["stopAfter"] as? Int ?? 0
        stops = a["stops"] as? [Int] ?? []
        status = a["status"] as? String ?? ""
    }

    /// "x,y,w,h" as centre and size, from the X-Animal header.
    static func box(_ text: String?) -> CGRect? {
        let n = (text ?? "").split(separator: ",").compactMap { Double($0) }
        guard n.count == 4 else { return nil }
        return CGRect(x: n[0] - n[2] / 2, y: n[1] - n[3] / 2, width: n[2], height: n[3])
    }
}

extension Camera {
    /// Changes the other RemoteCam's automatic recording: "auto", "autoSubject" or "autoStop". It shows here straight away.
    func setJoinedAuto(_ id: String, _ value: String) {
        guard let join, var auto = joinedAuto else { return }
        switch id {
        case "auto": auto.on = value == "1"
        case "autoSubject": auto.subject = value
        case "autoStop": auto.stopAfter = Int(value) ?? auto.stopAfter
        default: break
        }
        if !auto.on { joinedBox = nil }
        joinedAuto = auto
        autoSentAt = Date()
        join.post("set", ["id": id, "value": value], camera: self)
    }
}

/// For when another RemoteCam already has the camera: this device finds it and runs the camera through it. It keeps
/// looking nearby while this device looks for the camera, since a camera on home Wi-Fi with a computer is never seen
/// from the hotspot, and only shows itself on an iPhone once it finds something.
struct JoinSection: View {
    @ObservedObject var camera: Camera
    @State private var found: [OtherRemoteCam] = []
    @State private var looking = false
    @State private var looked = false

    private var shown: Bool { AppDelegate.pad || camera.busyElsewhere || !found.isEmpty }

    var body: some View {
        Group {
            if shown { card } else { Color.clear.frame(height: 0) }
        }
        .onAppear { look() }
        .onChange(of: camera.busyElsewhere) { busy in if busy { look() } }
        .onReceive(Timer.publish(every: 8, on: .main, in: .common).autoconnect()) { _ in
            if camera.link != .on, camera.join == nil { look() }
        }
    }

    private var card: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(AppDelegate.pad && found.isEmpty ? "Using your iPhone's camera?" : "The camera is in use on another device")
                .font(.callout.weight(.semibold))
            ForEach(found) { other in
                Button { camera.joinOther(other) } label: {
                    HStack(spacing: 12) {
                        Image(systemName: other.icon).font(.title3).frame(width: 28)
                        Text(other.name).font(.body.weight(.semibold)).lineLimit(1)
                        Spacer()
                        Text(camera.link == .connecting && camera.join == nil && !camera.stage.isEmpty ? "Connecting…" : "Use it here")
                            .font(.callout)
                            .foregroundColor(.good)
                    }
                    .padding(14)
                    .background(Color.page, in: RoundedRectangle(cornerRadius: 10))
                }
                .foregroundColor(.white)
            }
            if found.isEmpty, !looked || looking {
                HStack(spacing: 10) {
                    ProgressView()
                    Text("Looking nearby…").font(.footnote).foregroundColor(.dim)
                }
            } else if found.isEmpty {
                Text(AppDelegate.pad
                     ? "Your iPhone isn't showing up. Open RemoteCam on it and turn on Let my other devices join, in Setup."
                     : "Nothing nearby has it. Open RemoteCam on the other device, on the same Wi-Fi as this one.")
                    .font(.footnote)
                    .foregroundColor(.dim)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
        .background(Color.panel.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
    }

    /// One that has let this device in before is joined straight away.
    private func look() {
        guard !looking else { return }
        looking = true
        Task { @MainActor in
            let nearby = await Nearby.find()
            found = nearby
            looking = false
            looked = true
            if camera.join == nil, camera.link != .on,
               let known = nearby.first(where: { !JoinTokens.read(host: $0.id).isEmpty || !JoinTokens.read(address: $0.address).isEmpty }) {
                camera.joinOther(known)
            }
        }
    }
}
