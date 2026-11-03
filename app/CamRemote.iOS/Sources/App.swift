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

struct Login: Codable {
    var user: String
    var password: String
}

struct SavedCamera: Codable, Identifiable {
    var key: String // the camera's own SSH key, which it shows before any password is sent
    var name: String
    var login: Login
    var id: String { key }
}

/// Each camera's login, found by the camera's key, so the right one is used without asking.
/// They live in the Keychain and iCloud Keychain carries them to your other iPhone or iPad.
enum Logins {
    private static let service = "CamRemote cameras"
    private static var base: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
         kSecAttrSynchronizable as String: kSecAttrSynchronizableAny]
    }

    static func all() -> [SavedCamera] {
        var search = base
        search[kSecReturnData as String] = true
        search[kSecMatchLimit as String] = kSecMatchLimitAll
        var result: AnyObject?
        guard SecItemCopyMatching(search as CFDictionary, &result) == errSecSuccess, let items = result as? [Data] else { return [] }
        return items.compactMap { try? JSONDecoder().decode(SavedCamera.self, from: $0) }.sorted { $0.name < $1.name }
    }

    static func login(for key: String) -> Login? {
        all().first { $0.key == key }?.login
    }

    static func save(_ camera: SavedCamera) {
        forget(camera.key)
        var item = base
        item[kSecAttrAccount as String] = camera.key
        item[kSecAttrSynchronizable as String] = true
        item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlocked
        item[kSecValueData as String] = try? JSONEncoder().encode(camera)
        SecItemAdd(item as CFDictionary, nil)
    }

    static func forget(_ key: String) {
        var item = base
        item[kSecAttrAccount as String] = key
        SecItemDelete(item as CFDictionary)
    }

    /// Up to 0.1 the app kept one login, and only on this device.
    static func moveOldLogin() {
        let old: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "CamRemote camera"]
        var search = old
        search[kSecReturnData as String] = true
        var result: AnyObject?
        let defaults = UserDefaults.standard
        if SecItemCopyMatching(search as CFDictionary, &result) == errSecSuccess, let data = result as? Data,
           let key = defaults.string(forKey: "cameraKey"), !key.isEmpty, let user = defaults.string(forKey: "user"), !user.isEmpty {
            save(SavedCamera(key: key, name: "Camera", login: Login(user: user, password: String(decoding: data, as: UTF8.self))))
        }
        SecItemDelete(old as CFDictionary)
        defaults.removeObject(forKey: "cameraKey")
        defaults.removeObject(forKey: "user")
    }
}

/// On a phone the app stays upright until the full screen view asks to turn. An iPad turns freely.
final class AppDelegate: NSObject, UIApplicationDelegate {
    static let pad = UIDevice.current.userInterfaceIdiom == .pad
    static var orientations: UIInterfaceOrientationMask = pad ? .all : .portrait

    func application(_ application: UIApplication, supportedInterfaceOrientationsFor window: UIWindow?) -> UIInterfaceOrientationMask {
        Self.orientations
    }

    #if DEBUG
    /// Test builds only: `devicectl device notification post --name io.github.camremote.screenshot` saves the screen
    /// to Documents/screenshot.png, to copy off with `devicectl device copy from`. There's no other way to see an iPad's screen from a Mac.
    func application(_ application: UIApplication, didFinishLaunchingWithOptions options: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        CFNotificationCenterAddObserver(CFNotificationCenterGetDarwinNotifyCenter(), nil, { _, _, _, _, _ in
            DispatchQueue.main.async { AppDelegate.saveScreen() }
        }, "io.github.camremote.screenshot" as CFString, nil, .deliverImmediately)
        return true
    }

    private static func saveScreen() {
        guard let window = (UIApplication.shared.connectedScenes.first as? UIWindowScene)?.keyWindow else { return }
        let picture = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            window.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
        }
        let file = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("screenshot.png")
        try? picture.pngData()?.write(to: file)
    }
    #endif
}

@main
struct CamRemoteApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var camera: Camera
    @StateObject private var animals: AnimalTrigger
    @AppStorage("address") private var address = ""
    @State private var user = "" // only while typing a camera's login; it's saved with that camera
    @State private var password = ""
    @State private var resume = false
    @State private var launched = false
    @Environment(\.scenePhase) private var phase

    init() {
        Logins.moveOldLogin()
        let camera = Camera()
        _camera = StateObject(wrappedValue: camera)
        _animals = StateObject(wrappedValue: AnimalTrigger(camera: camera))
    }

    var body: some Scene {
        WindowGroup {
            Group {
                if camera.link == .on {
                    ShootingView(camera: camera, animals: animals)
                } else {
                    ConnectView(camera: camera, user: $user, address: $address, password: $password, connect: connectIfReady)
                }
            }
            .preferredColorScheme(.dark)
            .onAppear {
                // This also runs each time the connect screen comes back, and Disconnect has to stay disconnected.
                guard !launched else { return }
                launched = true
                // The animal trigger keeps it awake even while the camera is away, so it can reconnect and carry on.
                let animals = animals
                Bridge.shared.alsoAwake = { animals.on }
                Bridge.shared.attach(camera)
                connectIfReady()
            }
            .onChange(of: phase) { now in
                // iOS pauses the app in the background, so hand the camera back cleanly and pick up again.
                if now == .background { animals.pause() }
                if now == .background, camera.timeLapseRunning {
                    camera.holdConnection()
                } else if now == .background {
                    resume = camera.link != .off
                    camera.disconnect()
                } else if now == .active {
                    camera.releaseHold()
                    animals.resume()
                    resumeIfNeeded(now)
                }
            }
            .onChange(of: camera.link) { link in
                if link == .on { password = "" } // it's saved with the camera now
                Bridge.shared.keepAwake() // on while connected, while a computer may be watching or while watching for animals
                resumeIfNeeded(phase) // a quick trip out of the app can come back before the old connection has closed
            }
            .onChange(of: animals.on) { _ in Bridge.shared.keepAwake() }
            // Only while the app is open, so turning the camera on is all it takes.
            .onReceive(Timer.publish(every: 4, on: .main, in: .common).autoconnect()) { _ in
                if camera.link == .off, camera.keepTrying, !camera.editingLogin, phase == .active { connectIfReady() }
            }
        }
    }

    /// Takes the phase as an argument because inside `onChange` the `phase` property still holds the old value.
    private func resumeIfNeeded(_ current: ScenePhase) {
        guard resume, current == .active, camera.link == .off else { return }
        resume = false
        connectIfReady()
    }

    /// A saved camera connects with its own login. One typed in is kept for retries until it has worked.
    private func connectIfReady() {
        let typed = user.isEmpty || password.isEmpty ? nil : Login(user: user, password: password)
        guard typed != nil || !camera.savedCameras.isEmpty else { return }
        camera.connect(address: address.trimmingCharacters(in: .whitespaces), typed: typed)
    }
}

struct ConnectView: View {
    @ObservedObject var camera: Camera
    @Binding var user: String
    @Binding var address: String
    @Binding var password: String
    let connect: () -> Void

    private var askForLogin: Bool { camera.editingLogin || camera.loginRefused || camera.newCamera || camera.savedCameras.isEmpty }

    var body: some View {
        ScrollView {
            VStack(spacing: 12) {
                Text("Egret").font(.largeTitle.weight(.semibold)).padding(.top, 72)
                BridgeBadge()
                status.padding(.top, 12)
                if AppDelegate.pad || camera.busyElsewhere { JoinSection(camera: camera) }
                if askForLogin { loginForm } else { waiting }
            }
            .frame(maxWidth: 480) // an iPad's width would stretch the fields
            .frame(maxWidth: .infinity)
            .padding()
        }
        .background(Color.page.ignoresSafeArea())
        .onChange(of: camera.loginRefused) { if $0 { UINotificationFeedbackGenerator().notificationOccurred(.error) } }
    }

    private var trying: Bool { camera.link == .connecting || camera.keepTrying }

    /// Stays put between tries, so a search that keeps going reads as one search and says why it hasn't found it.
    @ViewBuilder private var status: some View {
        if trying {
            VStack(spacing: 10) {
                HStack(spacing: 10) {
                    ProgressView()
                    Text(camera.link == .connecting && !camera.stage.isEmpty ? camera.stage : "Looking for your camera")
                        .font(.callout.weight(.semibold))
                }
                if !camera.problem.isEmpty {
                    Text(camera.problem).font(.footnote).foregroundColor(.dim).multilineTextAlignment(.center)
                }
                if camera.keepTrying {
                    Button("Stop looking", action: camera.stopTrying).font(.footnote.weight(.semibold)).foregroundColor(.white)
                }
            }
            .padding(14)
            .frame(maxWidth: .infinity)
            .background(Color.panel, in: RoundedRectangle(cornerRadius: 12))
        } else if !camera.problem.isEmpty, !camera.newCamera {
            Label(camera.problem, systemImage: camera.loginRefused ? "xmark.circle.fill" : "exclamationmark.circle")
                .font(.callout)
                .foregroundColor(camera.loginRefused ? .rec : .dim)
                .padding(14)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(Color.panel, in: RoundedRectangle(cornerRadius: 12))
        }
    }

    /// With a camera saved there's nothing to fill in.
    private var waiting: some View {
        VStack(spacing: 14) {
            if !trying {
                if camera.problem.isEmpty { Text("Not connected").foregroundColor(.dim) }
                Button("Connect", action: connect).buttonStyle(Big(color: .panel))
            }
            Button("Type a login") { camera.editingLogin = true } // so a half-typed password is never tried or saved
                .font(.footnote)
                .foregroundColor(.dim)
                .padding(.top, 24)
            BridgeSection().padding(.top, 24) // so it can be turned on or off without a camera
        }
    }

    private var loginForm: some View {
        VStack(spacing: 12) {
            Text(camera.newCamera ? "This camera is new to Egret. Type its login once and it's saved. It's on the camera's Access Authen. Info screen."
                                  : "Type these once for each camera. They're on its Access Authen. Info screen.")
                .foregroundColor(.dim)
                .multilineTextAlignment(.center)
                .padding(.bottom, 12)
            field("User", text: $user)
            SecureField("Password", text: $password)
                .padding(12)
                .background(Color.panel, in: RoundedRectangle(cornerRadius: 10))
            field("Camera address, only on home Wi-Fi", text: $address)
                .keyboardType(.numbersAndPunctuation)
            Button(trying ? "Connecting…" : "Connect") {
                camera.editingLogin = false
                camera.loginRefused = false
                camera.newCamera = false
                connect()
            }
            .buttonStyle(Big(color: .panel))
            .disabled(trying || user.isEmpty || password.isEmpty)
            if !camera.savedCameras.isEmpty, camera.editingLogin {
                Button("Back") {
                    password = "" // or it would be tried on the next camera that turns up
                    camera.editingLogin = false
                }.font(.footnote).foregroundColor(.dim)
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
