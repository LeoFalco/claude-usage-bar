// Draws the app icon and writes Resources/AppIcon.icns.
// Run from the repo root: swift scripts/make-icon.swift
import AppKit

let size: CGFloat = 1024
let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
    // Rounded square on Apple's icon grid: 824pt body inside the 1024 canvas.
    let body = NSRect(x: 100, y: 100, width: 824, height: 824)
    let shape = NSBezierPath(roundedRect: body, xRadius: 185, yRadius: 185)
    NSGradient(starting: NSColor(red: 0.91, green: 0.56, blue: 0.43, alpha: 1),
               ending: NSColor(red: 0.76, green: 0.35, blue: 0.23, alpha: 1))!.draw(in: shape, angle: -90)

    // Gauge: a 240° track with the used part filled in white.
    let center = NSPoint(x: 512, y: 470)
    let radius: CGFloat = 270
    func arc(from start: CGFloat, to end: CGFloat, color: NSColor) {
        let path = NSBezierPath()
        path.appendArc(withCenter: center, radius: radius, startAngle: start, endAngle: end, clockwise: true)
        path.lineWidth = 76
        path.lineCapStyle = .round
        color.setStroke()
        path.stroke()
    }
    arc(from: 210, to: -30, color: NSColor.white.withAlphaComponent(0.3))
    arc(from: 210, to: 54, color: .white)

    // Needle pointing at the end of the filled part.
    let tip = NSPoint(x: center.x + cos(54 * .pi / 180) * (radius - 90), y: center.y + sin(54 * .pi / 180) * (radius - 90))
    let needle = NSBezierPath()
    needle.move(to: center)
    needle.line(to: tip)
    needle.lineWidth = 40
    needle.lineCapStyle = .round
    NSColor.white.setStroke()
    needle.stroke()
    NSColor.white.setFill()
    NSBezierPath(ovalIn: NSRect(x: center.x - 52, y: center.y - 52, width: 104, height: 104)).fill()
    return true
}

let fm = FileManager.default
let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("AppIcon.iconset")
try? fm.removeItem(at: iconset)
try! fm.createDirectory(at: iconset, withIntermediateDirectories: true)
for base in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let px = base * scale
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                                   samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                   bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        image.draw(in: NSRect(x: 0, y: 0, width: px, height: px))
        NSGraphicsContext.restoreGraphicsState()
        let name = scale == 1 ? "icon_\(base)x\(base).png" : "icon_\(base)x\(base)@2x.png"
        try! rep.representation(using: .png, properties: [:])!.write(to: iconset.appendingPathComponent(name))
    }
}
let task = Process()
task.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
task.arguments = ["-c", "icns", iconset.path, "-o", "Resources/AppIcon.icns"]
try! task.run()
task.waitUntilExit()
precondition(task.terminationStatus == 0, "iconutil failed")
try! fm.copyItem(at: iconset.appendingPathComponent("icon_256x256@2x.png"), to: URL(fileURLWithPath: "docs/icon.png"))
print("Wrote Resources/AppIcon.icns and docs/icon.png")
