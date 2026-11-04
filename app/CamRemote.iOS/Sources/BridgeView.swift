import SwiftUI

/// The part of Setup that lets another device, like an iPad on this phone's hotspot, use the camera through this phone.
struct BridgeSection: View {
    @ObservedObject private var bridge = Bridge.shared
    @State private var codeShown = false
    @State private var browser = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("OTHER DEVICES").font(.caption.weight(.semibold)).tracking(1).foregroundColor(.dim).padding(.top, 8)
            Toggle("Let my other devices join", isOn: Binding(get: { bridge.isOn }, set: { bridge.turn(on: $0) }))
                .tint(.good)
            Text("Your iPad or computer can use the camera through this iPhone. The first time, you tap Allow here. "
                 + "Keep RemoteCam open on this screen, and the phone plugged in.")
                .font(.footnote)
                .foregroundColor(.dim)
            if bridge.isOn { details }
        }
        .onReceive(Timer.publish(every: 3, on: .main, in: .common).autoconnect()) { _ in
            if bridge.isOn, browser { bridge.refreshAddresses() } // Tailscale can connect after this opens
        }
    }

    @ViewBuilder private var details: some View {
        if !bridge.problem.isEmpty {
            Text(bridge.problem).font(.footnote).foregroundColor(.warn)
        }
        if !bridge.allowedDevices.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                ForEach(bridge.allowedDevices) { device in
                    HStack {
                        Text(device.name)
                        Spacer()
                        Button("Remove") { bridge.remove(device) }.font(.footnote)
                    }
                }
            }
            .padding(12)
            .background(Color.panel, in: RoundedRectangle(cornerRadius: 10))
        }

        Text("Picture to other devices").font(.footnote).foregroundColor(.dim).padding(.top, 4)
        Picker("Pictures", selection: $bridge.pace) {
            ForEach(Pace.allCases) { Text($0.label).tag($0) }
        }
        .pickerStyle(.segmented)
        Text(usage).font(.footnote).foregroundColor(.dim)

        DisclosureGroup("From a web browser", isExpanded: $browser) { browserDetails }
            .font(.footnote)
            .tint(.dim)
            .padding(.top, 4)
    }

    /// For a computer without RemoteCam, which opens the page in a browser instead.
    @ViewBuilder private var browserDetails: some View {
        VStack(alignment: .leading, spacing: 6) {
            if !bridge.addresses.contains(where: { $0.hasPrefix("Tailscale") }) {
                Text("Away from home, this needs Tailscale connected on this phone.").font(.footnote).foregroundColor(.dim)
            }
            ForEach(bridge.addresses, id: \.self) { address in
                let parts = address.components(separatedBy: ": ")
                HStack(alignment: .firstTextBaseline) {
                    Text(parts[0]).font(.footnote).foregroundColor(.dim)
                    Spacer()
                    Text(parts.dropFirst().joined(separator: ": "))
                        .font(.callout.monospaced())
                        .lineLimit(1)
                        .minimumScaleFactor(0.7)
                        .textSelection(.enabled)
                }
            }
            HStack {
                Text("Code")
                Spacer()
                Text(codeShown ? bridge.code : "••••••••").font(.callout.monospaced()).textSelection(.enabled)
            }
            .padding(.top, 4)
            HStack(spacing: 8) {
                Button(codeShown ? "Hide code" : "Show code") { codeShown.toggle() }
                    .buttonStyle(Big(color: .panel, height: 38))
                Button("New code") {
                    bridge.newCode()
                    codeShown = true
                }
                .buttonStyle(Big(color: .panel, height: 38))
            }
            Text("A new code signs out every browser using the old one.").font(.caption).foregroundColor(.dim)
        }
        .padding(12)
        .background(Color.panel, in: RoundedRectangle(cornerRadius: 10))
        .padding(.top, 6)
    }

    /// Measured while another device watches, since how much a picture costs depends on what's in it.
    private var usage: String {
        let sent = String(format: "%.1f MB", Double(bridge.sentBytes) / 1_000_000)
        guard bridge.watchedSeconds >= 20 else {
            return bridge.watching ? "Another device is watching. \(sent) sent so far." : "Uses mobile data only while a device away from here is watching."
        }
        let hourly = Double(bridge.paceBytes) / bridge.watchedSeconds * 3600 / 1_000_000
        let lead = bridge.watching ? "Another device is watching." : "Nothing is watching now."
        return "\(lead) \(sent) sent so far, about \(Int(hourly.rounded())) MB an hour at this setting."
    }
}

/// A small reminder on screen that another device can see the camera.
struct BridgeBadge: View {
    @ObservedObject private var bridge = Bridge.shared

    var body: some View {
        if bridge.isOn {
            Label(bridge.watching ? "Another device watching" : "Other devices can join", systemImage: "rectangle.on.rectangle")
                .font(.caption2.weight(.semibold))
                .foregroundColor(bridge.watching ? .good : .dim)
                .lineLimit(1)
        }
    }
}

/// Asks the person whether a device may use the camera, wherever they are in the app.
struct JoinQuestionAlert: ViewModifier {
    @ObservedObject private var bridge = Bridge.shared

    func body(content: Content) -> some View {
        // Only its buttons close it, and each one answers.
        content.alert(item: Binding(get: { bridge.question }, set: { _ in })) { asked in
            Alert(title: Text("\(asked.name) wants to use the camera"),
                  message: Text("You won't be asked again for it. You can remove it in Setup."),
                  primaryButton: .default(Text("Allow")) { bridge.answer(allow: true) },
                  secondaryButton: .cancel(Text("Don't Allow")) { bridge.answer(allow: false) })
        }
    }
}
