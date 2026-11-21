import AppKit
import UserNotifications
import UsageCore

/// Menu bar labels are template images (always monochrome) unless drawn as a plain image,
/// so the colored warning state is rendered by hand.
func menuBarImage(_ text: String, level: AlertLevel) -> NSImage {
    let color: NSColor? = switch level {
    case .none: nil
    case .pace: .systemOrange
    case .high: .systemRed
    }
    let string = NSAttributedString(string: text, attributes: [
        .font: NSFont.menuBarFont(ofSize: 0),
        .foregroundColor: color ?? .black,
    ])
    let size = string.size()
    let image = NSImage(size: NSSize(width: ceil(size.width), height: ceil(size.height)), flipped: false) { rect in
        string.draw(in: rect)
        return true
    }
    image.isTemplate = color == nil
    return image
}

/// Posts each alert once per limit window; sent keys are kept in UserDefaults.
func notify(_ alerts: [UsageAlert]) {
    let defaults = UserDefaults.standard
    var sent = Set(defaults.stringArray(forKey: "sentAlerts") ?? [])
    let fresh = alerts.filter { !sent.contains($0.key) }
    guard !fresh.isEmpty else { return }
    // shortcut: keys are never pruned; a few per week, fine for years.
    sent.formUnion(fresh.map(\.key))
    defaults.set(Array(sent), forKey: "sentAlerts")

    let center = UNUserNotificationCenter.current()
    center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
        guard granted else { return }
        for alert in fresh {
            let content = UNMutableNotificationContent()
            let row = alert.row
            let when = { (d: Date) in d.formatted(.dateTime.weekday().day().month().hour().minute()) }
            switch (alert.level, projection(for: row)) {
            case (.pace, .runsOut(let date)?):
                content.title = "\(row.title): heavy usage"
                content.body = "\(Int(row.percent.rounded()))% used. At this pace it runs out \(when(date))."
            default:
                content.title = "\(row.title): \(Int(row.percent.rounded()))% used"
                content.body = row.resetsAt.map { "Resets \(when($0))." } ?? ""
            }
            content.sound = .default
            center.add(UNNotificationRequest(identifier: alert.key, content: content, trigger: nil))
        }
    }
}
