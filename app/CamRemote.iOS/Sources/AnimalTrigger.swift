import Foundation
import os
import UIKit

private let between = 0.5
private let bar: Float = 0.25
private let follow = 3.0 // a sighting this recent gets the closer look
private let quickCheck = 0.4 // slower than this, there's no time for two looks
private let sure: Float = 0.6 // scenery can score this high once, but not twice running, so a spot that has is an animal
private let worthFocus: Float = 0.5
private let sceneryArea: Float = 0.04 // splashing water lands on a few spots this close together, so they're one piece of scenery
private let sureSpot: Float = 0.03
private let stillSpot: Float = 0.006 // a rock's box comes back this close every time; a live animal's moves
private let stillHits = 6
private let stillLasts = 20.0 // the same box this long is a thing, even if it once looked sure
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

/// Something automatic recording can look for, as the COCO numbers the model uses. The same three as on a computer
/// (`RecordSubject` in BirdTrigger.cs). `startAt` is the score a sighting needs to start a take: the model calls seals
/// and distant penguins people at about 0.3 to 0.4, while a real person scores far higher.
struct RecordSubject: Equatable {
    let name: String
    let plural: String
    let first: Int32
    let last: Int32
    var startAt: Float = bar

    static let all = [
        RecordSubject(name: "Animal", plural: "animals", first: 16, last: 25),
        RecordSubject(name: "Bird", plural: "birds", first: 16, last: 16),
        RecordSubject(name: "Person", plural: "people", first: 1, last: 1, startAt: 0.5),
    ]

    static func named(_ name: String?) -> RecordSubject { all.first { $0.name == name } ?? all[0] }
}

/// The same rules as BirdTrigger.cs on a computer. Checks the live view twice a second, starts recording after two
/// sightings in three seconds and stops once no animal has been seen for a while. It only ever stops a take it started
/// itself. A confident sighting also focuses there. Sightings that keep landing in one small area without being sure
/// twice running are scenery, like splashing water. While it's following something, each check also looks closely
/// around the last sighting, which finds small birds the whole picture misses.
///
/// iOS stops the app when it leaves the screen, so this only watches while CamRemote is open.
final class AnimalTrigger: ObservableObject {
    @Published private(set) var on = false
    @Published private(set) var status = ""
    @Published private(set) var box: CGRect? // as 0..1 of the camera's picture, its own way up
    @Published private(set) var subject = RecordSubject.named(UserDefaults.standard.string(forKey: "recordSubject"))

    static let model = Bundle.main.path(forResource: "rfdetr_small", ofType: "onnx")
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
    private var recent: [(x: Float, y: Float, w: Float, h: Float, score: Float, at: Date)] = []
    private var scenery: [(x: Float, y: Float, until: Date)] = []
    private var animals: [(x: Float, y: Float, until: Date)] = []
    private var lastSure: (x: Float, y: Float)? // the last check, if it was a confident one
    // Only touched on `work`.
    private var following: (x: Float, y: Float, w: Float, h: Float, at: Date)?
    private var slow = false
    private var focusedOn: (x: Float, y: Float)?
    private var focused = Date.distantPast
    private var boxShown = Date.distantPast

    init(camera: Camera) {
        self.camera = camera
    }

    private var watching: String { "Watching for \(subject.plural)." }

    /// What to look for. A take already running carries on; what counted as scenery starts over.
    func choose(_ new: RecordSubject) {
        guard new != subject else { return }
        subject = new
        UserDefaults.standard.set(new.name, forKey: "recordSubject")
        sightings = []
        recent = []
        scenery = []
        animals = []
        lastSure = nil
        camera.log("Automatic recording now looks for \(new.plural).")
        if on, ready, took == nil { status = watching }
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
        lastSure = nil
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
                    self.status = self.watching
                } else {
                    self.stop()
                    self.status = "The animal detector couldn't start."
                }
            }
        }
        // In the common mode, so it keeps checking while someone scrolls the controls.
        let timer = Timer(timeInterval: between, repeats: true) { [weak self] _ in self?.check() }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
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
    /// The camera hangs up right after this, and stopping the take goes out first. It goes out even if the press
    /// that started the take is still on its way, when the camera doesn't show it recording yet.
    func pause() {
        guard on else { return }
        if took != nil {
            camera.stopRecording()
            camera.log("Animal trigger stopped its take because RemoteCam left the screen.")
        }
        took = nil
        sightings = []
        box = nil
        status = "Paused. RemoteCam only watches while it's open on screen."
    }

    func resume() {
        if on, ready { status = watching }
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
        let this = round, subject = subject
        work.async {
            let found = autoreleasepool { self.lookAround(frame, turn, subject) }
            DispatchQueue.main.async {
                self.busy = false
                guard this == self.round, let found else { return }
                self.decide(score: found[0], x: found[1], y: found[2], w: found[3], h: found[4], at: Date())
            }
        }
    }

    /// On `work`: the whole picture, then, while it's following something, a closer look around the last sighting.
    /// The best match for the subject: its score, then its box as centre and size, 0..1 of the whole picture.
    private func lookAround(_ image: UIImage, _ turn: Int, _ subject: RecordSubject) -> [Float]? {
        guard let picture = image.cgImage else { return nil }
        let began = Date()
        guard var seen = Self.look(picture, turn, subject) else {
            following = nil
            return nil
        }
        let took = Date().timeIntervalSince(began)
        if (took > quickCheck) != slow {
            slow.toggle()
            camera.log(String(format: "An animal check took %.2f s, so the closer look is %@.", took, slow ? "off" : "back on"))
        }
        if let f = following, began.timeIntervalSince(f.at) <= follow, !slow {
            let square = Self.closeLook(picture.width, picture.height, f)
            if let part = picture.cropping(to: square), let close = Self.look(part, turn, subject), close[0] > seen[0] {
                let ax = Float(square.minX) / Float(picture.width), aw = Float(square.width) / Float(picture.width)
                let ay = Float(square.minY) / Float(picture.height), ah = Float(square.height) / Float(picture.height)
                seen = [close[0], ax + close[1] * aw, ay + close[2] * ah, close[3] * aw, close[4] * ah]
            }
        }
        following = seen[0] >= bar ? (seen[1], seen[2], seen[3], seen[4], began) : nil
        return seen
    }

    /// The square of a width by height picture to look at closely around a box (0..1 of the picture, by its centre),
    /// in pixels: four times the box's longer edge, from 256 to 576 pixels, kept inside the picture. As on a computer.
    private static func closeLook(_ width: Int, _ height: Int, _ box: (x: Float, y: Float, w: Float, h: Float, at: Date)) -> CGRect {
        let most = Float(min(576, min(width, height)))
        let side = min(max(4 * max(box.w * Float(width), box.h * Float(height)), min(256, most)), most).rounded(.down)
        let x = min(max(box.x * Float(width) - side / 2, 0), Float(width) - side).rounded(.down)
        let y = min(max(box.y * Float(height) - side / 2, 0), Float(height) - side).rounded(.down)
        return CGRect(x: CGFloat(x), y: CGFloat(y), width: CGFloat(side), height: CGFloat(side))
    }

    /// The best match for the subject in the picture, squashed to a square: its score, then its box as centre and size.
    private static func look(_ picture: CGImage, _ turn: Int, _ subject: RecordSubject) -> [Float]? {
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
        guard drawn, animal_look(rgba, Int32(turn), subject.first, subject.last, &found) == 0 else { return nil }
        return found
    }

    func decide(score: Float, x: Float, y: Float, w: Float, h: Float, at now: Date) {
        guard on else { return }
        let seen = counts(score, x, y, w, h, now)
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
                    status = watching
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
                status = "Stopped. " + watching
            } else {
                let left = Int((quietStop - now.timeIntervalSince(lastAnimal)).rounded(.up))
                status = seen ? "\(subject.name) seen. Recording." : "Recording. Stops in \(left) s if nothing shows up."
            }
            return
        }
        if seen, score >= subject.startAt, sightings.count >= startHits, now >= restUntil, camera.link == .on, !camera.recording, !camera.recordBusy,
           !camera.photoMode, !camera.timeLapseRunning {
            camera.toggleRecord()
            took = now
            lastAnimal = now
            rolled = false
            camera.log(String(format: "Animal trigger started recording for %@ at score %.2f.", subject.name, score))
            status = "\(subject.name) seen. Recording."
        }
    }

    /// Whether a check saw an animal, as opposed to nothing or a hit on a known piece of scenery.
    private func counts(_ score: Float, _ x: Float, _ y: Float, _ w: Float, _ h: Float, _ now: Date) -> Bool {
        recent.removeAll { now.timeIntervalSince($0.at) > spotTime }
        if score < bar {
            lastSure = nil
            return false
        }

        let same = recent.filter { near($0.x, $0.y, x, y, sceneryArea) }
        animals.removeAll { $0.until <= now }
        if score >= sure {
            if let was = lastSure, near(was.x, was.y, x, y, sureSpot) { animals.append((x, y, now + sureFor)) }
            lastSure = (x, y)
        } else {
            lastSure = nil
        }
        let still = recent.filter { near($0.x, $0.y, x, y, stillSpot) && similar($0.w, w) && similar($0.h, h) }
        if still.count >= stillHits, let first = still.first, now.timeIntervalSince(first.at) >= stillLasts {
            animals.removeAll { near($0.x, $0.y, x, y, sureSpot) } // it hasn't moved at all, so it isn't an animal, however sure it looked
        }
        let isSure = animals.contains { near($0.x, $0.y, x, y, sureSpot) }
        recent.append((x, y, w, h, score, now))
        scenery.removeAll { $0.until <= now || (isSure && near($0.x, $0.y, x, y, sceneryArea)) }
        for i in scenery.indices where near(scenery[i].x, scenery[i].y, x, y, sceneryArea) {
            scenery[i].until = now + sceneryFor // scenery that keeps scoring stays scenery, so a suet cake costs one take, not one every ten minutes
        }
        if same.count >= spotHits, let first = same.first, now.timeIntervalSince(first.at) >= spotLasts, !isSure, !isScenery(x, y) {
            scenery.append((x, y, now + sceneryFor))
            camera.log(String(format: "Animal trigger is ignoring hits at %.2f, %.2f.", x, y))
        }
        return !isScenery(x, y)
    }

    private func isScenery(_ x: Float, _ y: Float) -> Bool { scenery.contains { near($0.x, $0.y, x, y, sceneryArea) } }

    private func near(_ x1: Float, _ y1: Float, _ x2: Float, _ y2: Float, _ by: Float) -> Bool { abs(x1 - x2) < by && abs(y1 - y2) < by }

    private func similar(_ a: Float, _ b: Float) -> Bool { abs(a - b) <= 0.15 * max(a, b) }

    /// Focuses once on an animal, and again only if it has moved somewhere new.
    private func focus(_ x: Float, _ y: Float, _ now: Date) {
        if let was = focusedOn, near(was.x, was.y, x, y, newSpot) || now.timeIntervalSince(focused) < focusGap { return }
        focusedOn = (x, y)
        focused = now
        camera.focus(x: Double(x), y: Double(y))
    }
}
