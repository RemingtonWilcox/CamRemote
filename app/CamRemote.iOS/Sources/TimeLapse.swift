import SwiftUI

private let intervals = [1, 2, 3, 4, 5, 8, 10, 15, 20, 30, 45, 60]
private let clipLengths = [10, 20, 30, 60]
private let frameRates = [24, 30, 60]
private let maxShots = 9999
private let maxDelay = 5999 // the camera's start timer stops at 99 minutes 59 seconds

private func words(_ total: Int) -> String {
    if total < 60 { return "\(total) s" }
    let hours = total / 3600, minutes = total / 60 % 60, rest = total % 60
    if hours > 0 { return minutes > 0 ? "\(hours) h \(minutes) min" : hours == 1 ? "1 hour" : "\(hours) hours" }
    return rest > 0 ? "\(minutes) min \(rest) s" : "\(minutes) min"
}

/// Say when it runs and how long the clip should be, and this works out the rest before you shoot anything.
struct TimeLapseSheet: View {
    @ObservedObject var camera: Camera
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Time-lapse").font(.title3.weight(.semibold))
                Spacer()
                Button("Done") { dismiss() }
            }
            .padding()
            if let plan = camera.timeLapsePlan, camera.timeLapseRunning {
                TimeLapseProgress(camera: camera, plan: plan)
            } else {
                TimeLapsePlanner(camera: camera)
            }
        }
        .background(Color.page.ignoresSafeArea())
        .presentationDetents([.large])
    }
}

private struct TimeLapseProgress: View {
    @ObservedObject var camera: Camera
    let plan: TimeLapsePlan

    var body: some View {
        VStack(spacing: 18) {
            Spacer()
            TimelineView(.periodic(from: .now, by: 1)) { context in
                let total = plan.ends.timeIntervalSince(plan.starts)
                let done = max(0, min(1, context.date.timeIntervalSince(plan.starts) / total))
                let waiting = context.date < plan.starts
                VStack(spacing: 8) {
                    Text(waiting ? "Starts at \(plan.starts.formatted(date: .omitted, time: .shortened))"
                                 : "\(camera.timeLapseTaken.formatted()) of \(plan.shots.formatted()) photos")
                        .font(.title3.weight(.semibold))
                    ProgressView(value: done).tint(.good)
                    Text("Ends around \(plan.ends.formatted(date: .omitted, time: .shortened))").foregroundColor(.dim)
                }
            }
            Text(camera.dialOnMovie ? "Keep this app open. The dial was on Movie, so closing it could end the time-lapse."
                                    : "You can close the app. The camera keeps going.")
                .font(.footnote)
                .foregroundColor(.dim)
            Spacer()
            Button("Stop time-lapse", action: camera.stopTimeLapse).buttonStyle(Big(color: .rec, height: 54))
        }
        .padding()
    }
}

private struct TimeLapsePlanner: View {
    @ObservedObject var camera: Camera
    @State private var start = Date()
    @State private var end = Date().addingTimeInterval(2 * 3600)
    @State private var step = 4.0 // where in `intervals`
    @State private var fps = 24
    @Environment(\.dismiss) private var dismiss

    private var interval: Int { intervals[Int(step)] }

    private var seconds: Int {
        let span = Int(end.timeIntervalSince(start))
        return span > 0 ? span : span + 24 * 3600 // an end before the start means tomorrow
    }

    private var shots: Int { max(2, min(maxShots, seconds / interval)) }
    private var clip: Int { max(1, Int((Double(shots) / Double(fps)).rounded())) }
    private var speed: Int { interval * fps }
    private var delay: Int { max(0, Int(start.timeIntervalSinceNow)) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                result
                card("When") {
                    HStack {
                        DatePicker("From", selection: $start, displayedComponents: .hourAndMinute)
                        DatePicker("to", selection: $end, displayedComponents: .hourAndMinute)
                    }
                }
                card("Clip length") {
                    HStack(spacing: 6) {
                        ForEach(clipLengths, id: \.self) { length in
                            pill(words(length), picked: clip == length) { aim(at: length) }
                        }
                    }
                }
                card("One photo every \(interval) s") {
                    Slider(value: $step, in: 0...Double(intervals.count - 1), step: 1).tint(.good)
                }
                card("Plays at") {
                    HStack(spacing: 6) {
                        ForEach(frameRates, id: \.self) { rate in
                            pill("\(rate) fps", picked: fps == rate) { fps = rate }
                        }
                    }
                }
                ForEach(warnings, id: \.self) { warning in
                    Text(warning).font(.footnote).foregroundColor(.warn)
                }
            }
            .padding(.horizontal)
        }
        .safeAreaInset(edge: .bottom) {
            Button(delay > 30 ? "Start at \(start.formatted(date: .omitted, time: .shortened))" : "Start now") {
                camera.startTimeLapse(intervalSeconds: interval, shots: shots, delaySeconds: delay)
                dismiss()
            }
            .buttonStyle(Big(color: .good.opacity(0.35), height: 54))
            .disabled(delay > maxDelay)
            .padding()
            .background(Color.page)
        }
        .animation(.easeOut(duration: 0.15), value: speed)
    }

    /// Real time against the finished clip, drawn to scale so the difference is obvious at a glance.
    private var result: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("\(speed)× faster").font(.largeTitle.weight(.bold)).monospacedDigit()
            bar(words(seconds), caption: "real time", share: 1, color: .chosen)
            bar(words(clip), caption: "on screen", share: max(0.04, Double(clip) / Double(seconds)), color: .good)
            Text("\(shots.formatted()) photos" + (camera.shotsLeft.map { " of \($0.formatted()) left on the card" } ?? ""))
                .font(.footnote)
                .foregroundColor(.dim)
            Text(goodFor).font(.footnote.weight(.semibold)).foregroundColor(.good)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.panel, in: RoundedRectangle(cornerRadius: 12))
    }

    private func bar(_ value: String, caption: String, share: Double, color: Color) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text(value).font(.subheadline.weight(.semibold))
                Text(caption).font(.caption).foregroundColor(.dim)
            }
            GeometryReader { box in
                RoundedRectangle(cornerRadius: 3).fill(color).frame(width: box.size.width * share)
            }
            .frame(height: 8)
        }
    }

    private func card<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.caption).foregroundColor(.dim)
            content()
        }
        .padding(12)
        .background(Color.panel, in: RoundedRectangle(cornerRadius: 12))
    }

    private func pill(_ title: String, picked: Bool, action: @escaping () -> Void) -> some View {
        Button(title, action: action).buttonStyle(Big(color: picked ? .chosen : .page, height: 40))
    }

    private var goodFor: String {
        switch interval {
        case ...2: return "Good for people, traffic and fast clouds"
        case ...8: return "Good for clouds and changing light"
        case ...20: return "Good for sunsets and slow clouds"
        default: return "Good for shadows, plants and very long days"
        }
    }

    private var warnings: [String] {
        var found: [String] = []
        if camera.dialOnMovie { found.append("Turn the camera's dial to M first. If it's on Movie, the time-lapse ends when the phone disconnects.") }
        if seconds / interval > maxShots { found.append("The camera stops at 9,999 photos, so this ends early.") }
        if let left = camera.shotsLeft, shots > left { found.append("Not enough room on the card.") }
        if delay > maxDelay { found.append("The camera can only wait 99 minutes. Come back closer to the start.") }
        return found
    }

    private func aim(at length: Int) {
        let wanted = Double(seconds) / Double(length * fps)
        let nearest = intervals.indices.min { abs(Double(intervals[$0]) - wanted) < abs(Double(intervals[$1]) - wanted) }
        step = Double(nearest ?? Int(step))
    }
}
