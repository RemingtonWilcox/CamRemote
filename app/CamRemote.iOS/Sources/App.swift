import Security
import SwiftUI

extension Color {
    static let page = Color(red: 0x10 / 255, green: 0x12 / 255, blue: 0x14 / 255)
    static let panel = Color(red: 0x1B / 255, green: 0x1E / 255, blue: 0x22 / 255)
    static let line = Color(red: 0x30 / 255, green: 0x35 / 255, blue: 0x3B / 255)
    static let dim = Color(red: 0x9A / 255, green: 0x9A / 255, blue: 0x9A / 255)
    static let rec = Color(red: 0xE5 / 255, green: 0x32 / 255, blue: 0x2D / 255)
    static let good = Color(red: 0x4C / 255, green: 0xC2 / 255, blue: 0x6A / 255)
    static let warn = Color(red: 0xE8 / 255, green: 0xB3 / 255, blue: 0x39 / 255)
    static let chosen = Color(red: 0x3A / 255, green: 0x40 / 255, blue: 0x48 / 255)
}

enum Keychain {
    private static let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                               kSecAttrService as String: "CamRemote camera"]

    static var password: String {
        get {
            var result: AnyObject?
            var search = query
            search[kSecReturnData as String] = true
            guard SecItemCopyMatching(search as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return "" }
            return String(decoding: data, as: UTF8.self)
        }
        set {
            SecItemDelete(query as CFDictionary)
            guard !newValue.isEmpty else { return }
            var item = query
            item[kSecValueData as String] = Data(newValue.utf8)
            item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
            SecItemAdd(item as CFDictionary, nil)
        }
    }
}

/// The app stays upright until the full screen view asks to turn.
final class AppDelegate: NSObject, UIApplicationDelegate {
    static var orientations = UIInterfaceOrientationMask.portrait

    func application(_ application: UIApplication, supportedInterfaceOrientationsFor window: UIWindow?) -> UIInterfaceOrientationMask {
        Self.orientations
    }
}

@main
struct CamRemoteApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var camera = Camera()
    @AppStorage("user") private var user = ""
    @AppStorage("address") private var address = ""
    @AppStorage("cameraKey") private var cameraKey = ""
    @State private var password = Keychain.password
    @State private var resume = false
    @Environment(\.scenePhase) private var phase

    var body: some Scene {
        WindowGroup {
            Group {
                if camera.link == .on {
                    ShootingView(camera: camera)
                } else {
                    ConnectView(camera: camera, user: $user, address: $address, password: $password, connect: connect) {
                        cameraKey = ""
                        camera.problem = ""
                    }
                }
            }
            .preferredColorScheme(.dark)
            .onAppear { connectIfReady() }
            .onChange(of: phase) { now in
                // iOS pauses the app in the background, so hand the camera back cleanly and pick up again.
                if now == .background, camera.timeLapseRunning {
                    camera.holdConnection()
                } else if now == .background {
                    resume = camera.link == .on
                    camera.disconnect()
                } else if now == .active {
                    camera.releaseHold()
                    if resume {
                        resume = false
                        connectIfReady()
                    }
                }
            }
            .onChange(of: camera.link) { UIApplication.shared.isIdleTimerDisabled = $0 == .on }
            // Only while the app is open, so turning the camera on is all it takes.
            .onReceive(Timer.publish(every: 4, on: .main, in: .common).autoconnect()) { _ in
                if camera.link == .off, camera.keepTrying, phase == .active { connectIfReady() }
            }
        }
    }

    private func connectIfReady() {
        if !user.isEmpty, !password.isEmpty { connect() }
    }

    private func connect() {
        Keychain.password = password
        camera.connect(address: address.trimmingCharacters(in: .whitespaces), user: user, password: password, knownKey: cameraKey)
    }
}

struct ConnectView: View {
    @ObservedObject var camera: Camera
    @Binding var user: String
    @Binding var address: String
    @Binding var password: String
    let connect: () -> Void
    let forget: () -> Void
    @State private var changingLogin = false

    private var askForLogin: Bool { changingLogin || camera.loginRefused || user.isEmpty || password.isEmpty }

    var body: some View {
        ScrollView {
            VStack(spacing: 12) {
                Text("CamRemote").font(.largeTitle.weight(.semibold)).padding(.top, 72)
                if askForLogin { loginForm } else { waiting }
            }
            .frame(maxWidth: .infinity)
            .padding()
        }
        .background(Color.page.ignoresSafeArea())
    }

    /// With a login saved there's nothing to fill in, so this only says what's going on.
    private var waiting: some View {
        VStack(spacing: 14) {
            if camera.link == .connecting || camera.keepTrying { ProgressView().padding(.top, 24) }
            Text(camera.problem.isEmpty ? (camera.link == .connecting ? "Looking for your camera" : "Not connected") : camera.problem)
                .foregroundColor(.dim)
                .multilineTextAlignment(.center)
            if camera.link == .off, !camera.keepTrying {
                Button("Connect", action: connect).buttonStyle(Big(color: .panel))
            }
            if camera.problem.contains("Forget") {
                Button("Forget this camera", action: forget).foregroundColor(.dim)
            }
            Button("Change login") {
                camera.keepTrying = false // so a half-typed password is never tried or saved
                changingLogin = true
            }
                .font(.footnote)
                .foregroundColor(.dim)
                .padding(.top, 24)
        }
    }

    private var loginForm: some View {
        VStack(spacing: 12) {
            Text("Type these once. They're on the camera's Access Authen. Info screen.")
                .foregroundColor(.dim)
                .multilineTextAlignment(.center)
                .padding(.bottom, 12)
            field("User", text: $user)
            SecureField("Password", text: $password)
                .padding(12)
                .background(Color.panel, in: RoundedRectangle(cornerRadius: 10))
            field("Camera address, only on home Wi-Fi", text: $address)
                .keyboardType(.numbersAndPunctuation)
            Button(camera.link == .connecting ? "Connecting…" : "Connect") {
                changingLogin = false
                camera.loginRefused = false
                connect()
            }
            .buttonStyle(Big(color: .panel))
            .disabled(camera.link == .connecting || user.isEmpty || password.isEmpty)
            if camera.loginRefused {
                Text(camera.problem).foregroundColor(.warn).multilineTextAlignment(.center)
            }
        }
    }

    private func field(_ title: String, text: Binding<String>) -> some View {
        TextField(title, text: text)
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()
            .padding(12)
            .background(Color.panel, in: RoundedRectangle(cornerRadius: 10))
    }
}
