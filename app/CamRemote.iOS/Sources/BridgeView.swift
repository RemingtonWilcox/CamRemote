import SwiftUI

/// The part of Setup that lets a computer elsewhere run the camera through this phone.
struct BridgeSection: View {
    @ObservedObject private var bridge = Bridge.shared
    @State private var codeShown = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("COMPUTER CONTROL").font(.caption.weight(.semibold)).tracking(1).foregroundColor(.dim).padding(.top, 8)
            Toggle("Allow control from a computer or iPad", isOn: Binding(get: { bridge.isOn }, set: { bridge.turn(on: $0) }))
                .tint(.good)
            Text("An iPad on this phone's hotspot, or a computer on your Tailscale network, can watch and run the camera through this phone. "
                 + "It only works while CamRemote stays open on this screen, so the screen stays on. Keep the phone plugged in.")
                .font(.footnote)
                .foregroundColor(.dim)
            if bridge.isOn { details }
        }
        .onReceive(Timer.publish(every: 3, on: .main, in: .common).autoconnect()) { _ in
            if bridge.isOn { bridge.refreshAddresses() } // Tailscale can connect after this opens
        }
    }

    @ViewBuilder private var details: some View {
        if !bridge.problem.isEmpty {
            Text(bridge.problem).font(.footnote).foregroundColor(.warn)
        }
        VStack(alignment: .leading, spacing: 6) {
            if bridge.addresses.contains(where: { $0.hasPrefix("Tailscale") }) {
                Text("On the computer, open:").font(.footnote).foregroundColor(.dim)
            } else {
                Text("Tailscale isn't connected on this phone. Open the Tailscale app and connect.")
                    .font(.footnote)
                    .foregroundColor(.warn)
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
                Text("Access code")
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
            Text("New code signs out every computer using the old one.").font(.caption).foregroundColor(.dim)
        }
        .padding(12)
        .background(Color.panel, in: RoundedRectangle(cornerRadius: 10))

        Text("Pictures sent to the computer").font(.footnote).foregroundColor(.dim).padding(.top, 4)
        Picker("Pictures", selection: $bridge.pace) {
            ForEach(Pace.allCases) { Text($0.label).tag($0) }
        }
        .pickerStyle(.segmented)
        Text(usage).font(.footnote).foregroundColor(.dim)
    }

    /// Measured while a computer watches, since how much a picture costs depends on what's in it.
    private var usage: String {
        let sent = String(format: "%.1f MB", Double(bridge.sentBytes) / 1_000_000)
        guard bridge.watchedSeconds >= 20 else {
            return bridge.watching ? "A computer is watching. \(sent) sent so far." : "Uses mobile data only while a computer is watching."
        }
        let hourly = Double(bridge.paceBytes) / bridge.watchedSeconds * 3600 / 1_000_000
        let lead = bridge.watching ? "A computer is watching." : "No computer is watching now."
        return "\(lead) \(sent) sent so far, about \(Int(hourly.rounded())) MB an hour at this setting."
    }
}

/// A small reminder on screen that a computer can see the camera.
struct BridgeBadge: View {
    @ObservedObject private var bridge = Bridge.shared

    var body: some View {
        if bridge.isOn {
            Label(bridge.watching ? "Computer watching" : "Computer control on", systemImage: "desktopcomputer")
                .font(.caption2.weight(.semibold))
                .foregroundColor(bridge.watching ? .good : .dim)
                .lineLimit(1)
        }
    }
}
