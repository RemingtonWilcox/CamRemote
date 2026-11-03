import SwiftUI

/// Sends the log to CamRemote's developer when someone taps Send log, and only then. It goes to a small
/// Cloudflare service (tools/log-receiver) that keeps it for 90 days.
enum LogSender {
    private static let address = URL(string: "https://camremote-logs.wilcoxremington.workers.dev/log")!
    private static let maxChars = 600_000 // the newest part; the service takes up to 1 MB

    /// A reference to quote when talking about it, or else what went wrong.
    static func send(note: String, app: String) async -> (reference: String?, problem: String?) {
        let file = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("log.txt")
        guard let all = try? String(contentsOf: file, encoding: .utf8), !all.isEmpty else { return (nil, "There's no log yet.") }
        var request = URLRequest(url: address, timeoutInterval: 30)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: [
            "app": app, "note": note.trimmingCharacters(in: .whitespacesAndNewlines), "log": String(all.suffix(maxChars)),
        ])
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let answer = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            if (response as? HTTPURLResponse)?.statusCode == 200, let id = answer?["id"] as? String { return (id, nil) }
            return (nil, answer?["error"] as? String ?? "It didn't go through. Try again later.")
        } catch {
            return (nil, "It couldn't be sent. Check that the phone is online, then try again.")
        }
    }
}

/// What went wrong, and a button that sends the log. Nothing is sent before it's tapped.
struct SendLogSection: View {
    let camera: Camera
    @State private var note = ""
    @State private var result = "It sends what the app did, with your camera's model and its address on your network. No passwords, photos or video."
    @State private var sending = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("SOMETHING NOT WORKING?").font(.caption.weight(.semibold)).tracking(1).foregroundColor(.dim).padding(.top, 8)
            TextField("What happened? (optional)", text: $note, axis: .vertical)
                .lineLimit(2...5)
                .padding(10)
                .background(Color.panel, in: RoundedRectangle(cornerRadius: 10))
            Button(sending ? "Sending…" : "Send log to the developer") { send() }
                .buttonStyle(Big(color: .panel))
                .disabled(sending)
            Text(result).font(.footnote).foregroundColor(.dim).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func send() {
        sending = true
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
        let app = "Egret for iPhone \(version), iOS \(UIDevice.current.systemVersion)"
        Task { @MainActor in
            let (reference, problem) = await LogSender.send(note: note, app: app)
            if let reference {
                camera.log("Sent the log to the developer, reference \(reference).")
                result = "Sent, thank you. Its reference is \(reference)."
                note = ""
            } else {
                result = problem ?? ""
            }
            sending = false
        }
    }
}
