import Security
import SwiftUI
import UIKit

private let unreachable = "Can't reach the other Egret. Check it's open on screen with Allow control from a computer or iPad on, and that this device is on the same hotspot or Wi-Fi."

/// What another CamRemote handed this one after its access code was typed, kept in the Keychain by its address.
enum JoinTokens {
    private static func query(_ address: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "CamRemote join", kSecAttrAccount as String: address]
    }

    static func read(_ address: String) -> String {
        var search = query(address)
        search[kSecReturnData as String] = true
        var result: AnyObject?
        guard SecItemCopyMatching(search as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return "" }
        return String(decoding: data, as: UTF8.self)
    }

    static func write(_ address: String, _ token: String) {
        SecItemDelete(query(address) as CFDictionary)
        guard !token.isEmpty else { return }
        var item = query(address)
        item[kSecValueData as String] = Data(token.utf8)
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }
}

/// Another CamRemote that holds the camera, usually the iPhone whose hotspot the camera is on. It's the same control API
/// a computer uses, so this device runs the camera through it instead of taking the camera over.
final class JoinLink {
    let address: String // like 172.20.10.1:8787
    private let token: String
    private let session: URLSession
    private var tasks: [Task<Void, Never>] = []

    init(address: String, token: String) {
        self.address = address
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

    /// The code goes once. What comes back is kept, so next time there's nothing to type.
    static func signIn(address: String, code: String) async -> (token: String?, problem: String) {
        guard let url = URL(string: "http://\(address)/api/login") else { return (nil, "That address isn't right.") }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("1", forHTTPHeaderField: "X-CamRemote")
        request.httpBody = try? JSONSerialization.data(withJSONObject: ["code": code.trimmingCharacters(in: .whitespaces).uppercased()])
        guard let (_, response) = try? await session().data(for: request), let http = response as? HTTPURLResponse else {
            return (nil, unreachable)
        }
        if http.statusCode == 401 { return (nil, "That access code wasn't right. It's in Egret on the other device, under Allow control from a computer or iPad.") }
        let cookie = http.value(forHTTPHeaderField: "Set-Cookie") ?? ""
        let pair = cookie.split(separator: ";").first?.split(separator: "=", maxSplits: 1) ?? []
        guard pair.count == 2, pair[0].trimmingCharacters(in: .whitespaces) == "camremote" else {
            return (nil, "Something answered there, but it isn't Egret.")
        }
        return (String(pair[1]), "")
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
                camera.message = "The other Egret is an older version without that. Update it first."
            } else if http.statusCode != 200 {
                let reply = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
                camera.message = reply?["message"] as? String ?? "The other Egret refused that."
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
    /// Uses the camera through another CamRemote. The code is only needed the first time.
    func joinOther(address typed: String, code: String) {
        let address = JoinLink.normalize(typed)
        guard !address.isEmpty, join == nil, link != .on else { return }
        stopTrying()
        link = .connecting
        stage = "Reaching the other Egret…"
        Task { @MainActor in
            var token = JoinTokens.read(address)
            if !code.isEmpty {
                let signed = await JoinLink.signIn(address: address, code: code)
                guard let fresh = signed.token else { return failJoin(signed.problem) }
                token = fresh
            }
            guard !token.isEmpty else { return failJoin("Type the access code once. It's in Egret on the other device, under Allow control from a computer or iPad.") }
            let other = JoinLink(address: address, token: token)
            guard let (status, state) = await other.state() else { return failJoin(unreachable) }
            guard status == 200 else {
                JoinTokens.write(address, "")
                return failJoin("The other Egret has a new access code. Type it once.")
            }
            JoinTokens.write(address, token)
            UserDefaults.standard.set(address, forKey: "joinAddress")
            join = other
            busyElsewhere = false
            problem = ""
            stage = ""
            applyJoined(state)
            link = .on
            message = "Using the camera through the Egret at \(address.replacingOccurrences(of: ":8787", with: ""))"
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
        activeSlot = nil
        problem = text
        link = .off
    }

    /// The other app's settings, in the same shape it sends a computer. Its rows carry this app's own codes.
    func applyJoined(_ state: [String: Any]) {
        let lost = "The other Egret has lost the camera. It's looking for it."
        if state["link"] as? String != "connected", message != lost { message = lost }
        var shown: [UInt16: Row] = [:]
        for case let item as [String: Any] in state["rows"] as? [Any] ?? [] {
            guard let code = UInt16(item["id"] as? String ?? "") else { continue }
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
        cameraKey = ""
    }
}

/// For when another CamRemote already has the camera: this device runs it through that one.
struct JoinSection: View {
    @ObservedObject var camera: Camera
    @State private var address = UserDefaults.standard.string(forKey: "joinAddress") ?? (AppDelegate.pad ? "172.20.10.1" : "")
    @State private var code = ""
    @State private var open = AppDelegate.pad

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Button { withAnimation { open.toggle() } } label: {
                HStack {
                    Text(AppDelegate.pad ? "Is your iPhone already using the camera?" : "Is another iPhone, iPad or computer using the camera?")
                        .font(.callout.weight(.semibold))
                        .multilineTextAlignment(.leading)
                    Spacer()
                    Image(systemName: open ? "chevron.up" : "chevron.down").foregroundColor(.dim)
                }
            }
            .foregroundColor(.white)
            if open || camera.busyElsewhere {
                Text("Open Egret there and turn on Allow control from a computer or iPad. Its address and access code are shown under it. Your iPhone's is 172.20.10.1 when this device is on its hotspot.")
                    .font(.footnote)
                    .foregroundColor(.dim)
                field("Address", text: $address).keyboardType(.numbersAndPunctuation)
                field("Access code (only the first time)", text: $code)
                Button(camera.link == .connecting ? "Connecting…" : "Use it through that Egret") {
                    camera.joinOther(address: address, code: code)
                    code = ""
                }
                .buttonStyle(Big(color: .panel))
                .disabled(address.isEmpty || camera.link == .connecting)
            }
        }
        .padding(14)
        .background(Color.panel.opacity(0.5), in: RoundedRectangle(cornerRadius: 12))
        // Reopening the app tries the camera first; if the iPhone still has it, this goes back through the iPhone by itself.
        .onChange(of: camera.busyElsewhere) { busy in
            let saved = JoinLink.normalize(address)
            if busy, !saved.isEmpty, !JoinTokens.read(saved).isEmpty { camera.joinOther(address: saved, code: "") }
        }
    }

    private func field(_ title: String, text: Binding<String>) -> some View {
        TextField(title, text: text)
            .autocorrectionDisabled()
            .textInputAutocapitalization(.never)
            .padding(12)
            .background(Color.page, in: RoundedRectangle(cornerRadius: 10))
    }
}
