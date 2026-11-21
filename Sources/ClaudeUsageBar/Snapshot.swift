import AppKit
import SwiftUI
import UsageCore

/// Renders the panel offscreen with sample data into a PNG (`ClaudeUsageBar --snapshot out.png`),
/// so the README screenshot never shows real account data.
@MainActor
func renderSnapshot(to path: String) -> Bool {
    _ = NSApplication.shared
    let now = Date()
    func iso(_ hours: Double) -> String { ISO8601DateFormatter().string(from: now.addingTimeInterval(hours * 3600)) }
    let json = """
    {"limits": [
      {"kind": "session", "group": "session", "percent": 34, "resets_at": "\(iso(2.5))"},
      {"kind": "weekly_all", "group": "weekly", "percent": 58, "resets_at": "\(iso(88))"},
      {"kind": "weekly_scoped", "group": "weekly", "percent": 12, "resets_at": "\(iso(88))",
       "scope": {"model": {"display_name": "Fable"}}}
    ]}
    """
    guard let usage = try? Usage.decode(Data(json.utf8)) else { return false }

    let view = PanelView(model: UsageModel(preview: usage))
        .background(Color(nsColor: .windowBackgroundColor))
        .environment(\.colorScheme, .dark)
    let host = NSHostingView(rootView: view)
    host.appearance = NSAppearance(named: .darkAqua)
    host.frame.size = host.fittingSize
    let window = NSWindow(contentRect: host.frame, styleMask: .borderless, backing: .buffered, defer: false)
    window.appearance = host.appearance
    window.contentView = host
    host.layoutSubtreeIfNeeded()

    // 2x so the image is sharp on Retina screens.
    guard let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: Int(host.bounds.width * 2), pixelsHigh: Int(host.bounds.height * 2),
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
        bytesPerRow: 0, bitsPerPixel: 0
    ) else { return false }
    rep.size = host.bounds.size
    host.cacheDisplay(in: host.bounds, to: rep)
    guard let png = rep.representation(using: .png, properties: [:]) else { return false }
    return (try? png.write(to: URL(fileURLWithPath: path))) != nil
}
