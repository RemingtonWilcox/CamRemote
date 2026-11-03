import Foundation
import os
import UIKit

private let between = 0.5
private let bar: Float = 0.25
private let sure: Float = 0.6 // scenery never scores this high, so a spot that has is an animal
private let worthFocus: Float = 0.5
private let sameSpot: Float = 0.015
private let sureSpot: Float = 0.03
private let newSpot: Float = 0.1
private let spotHits = 12 // this many hits on one spot within spotTime makes it scenery
private let startHits = 2
private let startWithin = 3.0
private let spotTime = 40.0
private let spotLasts = 20.0 // no quicker, or a resting animal looks like scenery
private let boxStays = 2.0
private let quietStop = 15.0
private let longestTake = 5 * 60.0
private let rest = 5.0
private let settle = 5.0 // the camera's recording state arrives late
private let sceneryFor = 10 * 60.0
private let sureFor = 5 * 60.0 // an animal can sit a long time scoring low
private let refusedRest = 30.0
private let focusGap = 5.0
private let coreMLMemory = 1_500_000_000 // Core ML needs about 900 MB while it gets the model ready

/// The same rules as BirdTrigger.cs on a computer. Checks the live view twice a second, starts recording after two
/// sightings in three seconds and stops once no animal has been seen for a while. It only ever stops a take it started
/// itself. A confident sighting also focuses there. Sightings that keep landing on one spot without ever being sure
/// are scenery, like splashing water.
///
/// iOS stops the app when it leaves the screen, so this only watches while CamRemote is open.
final class AnimalTrigger: ObservableObject {
    @Published private(set) var on = false
    @Published private(set) var status = ""
    @Published private(set) var box: CGRect? // as 0..1 of the camera's picture, its own way up

    static let model = Bundle.main.path(forResource: "rfdetr_nano", ofType: "onnx")
    static var available: Bool { model != nil }

    private let camera: Camera
    private let work = DispatchQueue(label: "AnimalTrigger", qos: .utility)
    private var timer: Timer?
    private var round = 0 // so a model that finishes loading after Off doesn't count
    private var ready = false
    private var busy = false
    private var lastFrame = Date.distantPast
    private var sightings: [Date] = []
    private var took: Date? // when this take began, if we started it
    private var rolled = false // the camera has shown this take recording
    private var lastAnimal = Date.distantPast
    private var restUntil = Date.distantPast
    private var recent: [(x: Float, y: Float, score: Float, at: Date)] = []
    private var scenery: [(x: Float, y: Float, until: Date)] = []
    private var animals: [(x: Float, y: Float, until: Date)] = []
    private var focusedOn: (x: Float, y: Float)?
    private var focused = Date.distantPast
    private var boxShown = Date.distantPast

    init(camera: Camera) {
        self.camera = camera
    }

    func start() {
        guard !on, let model = Self.model else { return }
        on = true
        round += 1
        ready = false
        sightings = []
        recent = []
        scenery = []
        animals = []
        focusedOn = nil
        status = "Getting the animal detector ready. The first time can take a minute."
        let this = round, cache = Self.cacheFolder()
        work.async {
            let available = os_proc_available_memory() // 0 where it isn't known, like the simulator
            let coreML = available == 0 || available > coreMLMemory
            var error = [CChar](repeating: 0, count: 512)
            let began = Date()
            var opened = animal_open(model, cache, coreML ? 1 : 0, &error, 512) == 0
            if !opened, coreML {
                self.camera.log("Core ML couldn't take the animal model: \(String(cString: error))")
                opened = animal_open(model, nil, 0, &error, 512) == 0
            }
            let how = coreML && opened ? "Core ML" : "the processor"
            self.camera.log(opened ? String(format: "Animal model ready on %@ in %.1f s", how, Date().timeIntervalSince(began))
                                   : "Animal model failed to load: \(String(cString: error))")
            DispatchQueue.main.async {
                guard this == self.round, self.on else { return }
                if opened {
                    self.ready = true
                    self.status = "Watching for animals."
                } else {
                    self.stop()
                    self.status = "The animal detector couldn't start."
                }
            }
        }
        timer = Timer.scheduledTimer(withTimeInterval: between, repeats: true) { [weak self] _ in self?.check() }
    }

    func stop() {
        guard on else { return }
        on = false
        round += 1
        timer?.invalidate()
        timer = nil
        if took != nil { camera.log("Animal trigger turned off during a take; it keeps recording.") }
        took = nil
        box = nil
        status = ""
        work.async { animal_close() }
    }

    /// Leaving the screen stops the app, and a take it started would otherwise run until the card is full.
    /// The camera hangs up right after this, and stopping the take goes out first.
    func pause() {
        guard on else { return }
        if took != nil, camera.recording {
            camera.toggleRecord()
            camera.log("Animal trigger stopped its take because CamRemote left the screen.")
        }
        took = nil
        sightings = []
        box = nil
        status = "Paused. CamRemote only watches while it's open on screen."
    }

    func resume() {
        if on, ready { status = "Watching for animals." }
    }

    private static func cacheFolder() -> String {
        let folder = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("AnimalModel")
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder.path
    }

    private func check() {
        guard on, ready, !busy, camera.link == .on, !camera.photoMode, let frame = camera.frame, camera.frameTime != lastFrame else { return }
        lastFrame = camera.frameTime
        busy = true
        let turn = UserDefaults.standard.integer(forKey: "pictureTurn")
        let this = round
        work.async {
            let found = autoreleasepool { Self.look(frame, turn) }
            DispatchQueue.main.async {
                self.busy = false
                guard this == self.round, let found else { return }
                self.decide(score: found[0], x: found[1], y: found[2], w: found[3], h: found[4], at: Date())
            }
        }
    }

    /// The best animal in the picture: its score, then its box as centre and size.
    private static func look(_ image: UIImage, _ turn: Int) -> [Float]? {
        guard let picture = image.cgImage else { return nil }
        let size = Int(ANIMAL_SIZE)
        var rgba = [UInt8](repeating: 0, count: size * size * 4)
        let drawn = rgba.withUnsafeMutableBytes { bytes -> Bool in
            guard let context = CGContext(data: bytes.baseAddress, width: size, height: size, bitsPerComponent: 8, bytesPerRow: size * 4,
                                          space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return false }
            context.interpolationQuality = .medium
            context.draw(picture, in: CGRect(x: 0, y: 0, width: size, height: size)) // squashed square, as on a computer
            return true
        }
        var found = [Float](repeating: 0, count: 5)
        guard drawn, animal_look(rgba, Int32(turn), &found) == 0 else { return nil }
        return found
    }

    func decide(score: Float, x: Float, y: Float, w: Float, h: Float, at now: Date) {
        guard on else { return }
        let seen = counts(score, x, y, now)
        if seen { sightings.append(now) }
        sightings.removeAll { now.timeIntervalSince($0) > startWithin }
        act(score, seen, now)
        if seen, score >= worthFocus, took != nil { focus(x, y, now) } // only once recording, so focusing never holds up the record button
        if seen {
            boxShown = now
            box = CGRect(x: CGFloat(x - w / 2), y: CGFloat(y - h / 2), width: CGFloat(w), height: CGFloat(h))
        } else if now.timeIntervalSince(boxShown) > boxStays {
            box = nil
        }
    }

    private func act(_ score: Float, _ seen: Bool, _ now: Date) {
        if let began = took {
            if seen { lastAnimal = now }
            rolled = rolled || camera.recording
            if !camera.recordBusy, now.timeIntervalSince(camera.recordSent) > settle, !camera.recording {
                took = nil // stopped by hand, or the camera never started
                sightings = []
                if rolled {
                    status = "Watching for animals."
                } else {
                    restUntil = now + refusedRest
                    camera.log("The camera didn't start recording for the animal trigger.")
                    status = "The camera won't record. Check its card."
                }
            } else if now.timeIntervalSince(lastAnimal) >= quietStop || now.timeIntervalSince(began) >= longestTake {
                guard !camera.recordBusy else { return } // a press is still on its way, so stop on the next check
                if camera.recording { camera.toggleRecord() }
                took = nil
                sightings = []
                restUntil = now + rest
                focusedOn = nil
                camera.log(String(format: "Animal trigger stopped recording; last sighting %.0f s ago.", now.timeIntervalSince(lastAnimal)))
                status = "Stopped. Watching for animals."
            } else {
                let left = Int((quietStop - now.timeIntervalSince(lastAnimal)).rounded(.up))
                status = seen ? "Animal seen. Recording." : "Recording. Stops in \(left) s if nothing shows up."
            }
            return
        }
        if seen, sightings.count >= startHits, now >= restUntil, camera.link == .on, !camera.recording, !camera.recordBusy,
           !camera.photoMode, !camera.timeLapseRunning {
            camera.toggleRecord()
            took = now
            lastAnimal = now
            rolled = false
            camera.log(String(format: "Animal trigger started recording at score %.2f.", score))
            status = "Animal seen. Recording."
        }
    }

    /// Whether a check saw an animal, as opposed to nothing or a hit on a known piece of scenery.
    private func counts(_ score: Float, _ x: Float, _ y: Float, _ now: Date) -> Bool {
        recent.removeAll { now.timeIntervalSince($0.at) > spotTime }
        if score < bar { return false }

        let same = recent.filter { near($0.x, $0.y, x, y, sameSpot) }
        animals.removeAll { $0.until <= now }
        if score >= sure { animals.append((x, y, now + sureFor)) }
        let isSure = animals.contains { near($0.x, $0.y, x, y, sureSpot) }
        recent.append((x, y, score, now))
        scenery.removeAll { $0.until <= now || (isSure && near($0.x, $0.y, x, y, sureSpot)) }
        if same.count >= spotHits, let first = same.first, now.timeIntervalSince(first.at) >= spotLasts, !isSure, !isScenery(x, y) {
            scenery.append((x, y, now + sceneryFor))
            camera.log(String(format: "Animal trigger is ignoring hits at %.2f, %.2f.", x, y))
        }
        return !isScenery(x, y)
    }

    private func isScenery(_ x: Float, _ y: Float) -> Bool { scenery.contains { near($0.x, $0.y, x, y, sameSpot) } }

    private func near(_ x1: Float, _ y1: Float, _ x2: Float, _ y2: Float, _ by: Float) -> Bool { abs(x1 - x2) < by && abs(y1 - y2) < by }

    /// Focuses once on an animal, and again only if it has moved somewhere new.
    private func focus(_ x: Float, _ y: Float, _ now: Date) {
        if let was = focusedOn, near(was.x, was.y, x, y, newSpot) || now.timeIntervalSince(focused) < focusGap { return }
        focusedOn = (x, y)
        focused = now
        camera.focus(x: Double(x), y: Double(y))
    }
}
