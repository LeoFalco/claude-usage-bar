import Foundation

/// One line of the menu: a plan limit and how much of it is used.
public struct UsageRow: Equatable {
    public let group: String
    public let title: String
    /// Percentage used (0-100).
    public let percent: Double
    public let resetsAt: Date?
}

/// Response of `GET https://api.anthropic.com/api/oauth/usage` (the endpoint behind Claude Code's /usage).
public struct Usage: Decodable, Equatable {
    public struct Window: Decodable, Equatable {
        public let utilization: Double?
        public let resetsAt: Date?
    }

    public struct Limit: Decodable, Equatable {
        public struct Scope: Decodable, Equatable {
            public struct Model: Decodable, Equatable { public let displayName: String? }
            public let model: Model?
        }
        public let kind: String?
        public let group: String?
        public let percent: Double?
        public let resetsAt: Date?
        public let scope: Scope?
    }

    public let fiveHour: Window?
    public let sevenDay: Window?
    public let sevenDayOpus: Window?
    public let sevenDaySonnet: Window?
    /// Newer, generic list of limits; preferred when present.
    public let limits: [Limit]?

    public static func decode(_ data: Data) throws -> Usage {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        decoder.dateDecodingStrategy = .custom { d in
            let raw = try d.singleValueContainer().decode(String.self)
            guard let date = parseISODate(raw) else {
                throw DecodingError.dataCorrupted(.init(codingPath: d.codingPath, debugDescription: "Bad date: \(raw)"))
            }
            return date
        }
        return try decoder.decode(Usage.self, from: data)
    }

    public var rows: [UsageRow] {
        if let limits, !limits.isEmpty {
            return limits.compactMap { limit in
                guard let percent = limit.percent else { return nil }
                let group = limit.group ?? limit.kind ?? "limit"
                let name = group.replacingOccurrences(of: "_", with: " ").capitalized
                let model = limit.scope?.model?.displayName.map { " · \($0)" } ?? ""
                return UsageRow(group: group, title: name + model, percent: percent, resetsAt: limit.resetsAt)
            }
        }
        let legacy: [(String, String, Window?)] = [
            ("session", "Session 5h", fiveHour), ("weekly", "Weekly", sevenDay),
            ("weekly", "Weekly · Opus", sevenDayOpus), ("weekly", "Weekly · Sonnet", sevenDaySonnet),
        ]
        return legacy.compactMap { group, title, window in
            window?.utilization.map { UsageRow(group: group, title: title, percent: $0, resetsAt: window?.resetsAt) }
        }
    }

    /// Menu bar title: the highest usage per limit group, in API order, e.g. "23% · 41%".
    public var menuBarTitle: String {
        var order: [String] = []
        var top: [String: Double] = [:]
        for row in rows {
            if top[row.group] == nil { order.append(row.group) }
            top[row.group] = max(top[row.group] ?? 0, row.percent)
        }
        return order.isEmpty ? "–" : order.map { "\(Int(top[$0]!.rounded()))%" }.joined(separator: " · ")
    }
}

/// The API sends microsecond precision ("2025-10-17T19:00:00.123456+00:00"), which
/// ISO8601DateFormatter does not accept, so the fraction is dropped.
public func parseISODate(_ raw: String) -> Date? {
    let trimmed = raw.replacingOccurrences(of: #"\.\d+"#, with: "", options: .regularExpression)
    return ISO8601DateFormatter().date(from: trimmed)
}

/// Pulls the OAuth access token out of the JSON Claude Code stores in the Keychain
/// (or in ~/.claude/.credentials.json).
public func accessToken(fromCredentials data: Data) -> String? {
    struct Creds: Decodable {
        struct OAuth: Decodable { let accessToken: String }
        let claudeAiOauth: OAuth?
    }
    return (try? JSONDecoder().decode(Creds.self, from: data))?.claudeAiOauth?.accessToken
}

/// "resets in 2h 14m" style countdown.
public func resetDescription(_ date: Date?, now: Date = Date()) -> String {
    guard let date else { return "" }
    let minutes = max(0, Int(date.timeIntervalSince(now) / 60))
    let (d, h, m) = (minutes / 1440, minutes % 1440 / 60, minutes % 60)
    if d > 0 { return "resets in \(d)d \(h)h" }
    if h > 0 { return "resets in \(h)h \(m)m" }
    return "resets in \(m)m"
}

/// Where a limit ends up if usage keeps the pace it has had since the window started.
public enum Projection: Equatable {
    /// The limit lasts until reset, ending near this percentage.
    case lasts(percentAtReset: Double)
    /// The limit hits 100% at this date, before the reset.
    case runsOut(at: Date)
}

/// Length of each limit window, by group.
func windowLength(group: String) -> TimeInterval? {
    switch group {
    case "session": return 5 * 3600
    case "weekly": return 7 * 86400
    default: return nil
    }
}

/// Linear projection from the average pace since the window started.
/// Returns nil for unknown windows or too early in the window to tell.
public func projection(for row: UsageRow, now: Date = Date()) -> Projection? {
    guard let resetsAt = row.resetsAt, let length = windowLength(group: row.group) else { return nil }
    let elapsed = length - resetsAt.timeIntervalSince(now)
    // shortcut: linear average since window start, ignores daily rhythm; upgrade to a recent-window rate if it misleads.
    guard elapsed >= length * 0.05, elapsed <= length else { return nil }
    if row.percent >= 100 { return .runsOut(at: now) }
    let projected = row.percent * length / elapsed
    if projected <= 100 { return .lasts(percentAtReset: projected) }
    return .runsOut(at: now.addingTimeInterval((100 - row.percent) * elapsed / row.percent))
}

/// How worrying a limit is. Higher is worse.
public enum AlertLevel: Int, Comparable {
    case none, pace, high
    public static func < (a: AlertLevel, b: AlertLevel) -> Bool { a.rawValue < b.rawValue }
}

/// `high` from 90% used; `pace` when the current pace runs out before the reset.
public func alertLevel(for row: UsageRow, now: Date = Date()) -> AlertLevel {
    if row.percent >= 90 { return .high }
    if case .runsOut? = projection(for: row, now: now) { return .pace }
    return .none
}

/// One notification-worthy event. `key` is stable for a limit, its window and the level,
/// so the same alert is sent once per window.
public struct UsageAlert: Equatable {
    public let key: String
    public let level: AlertLevel
    public let row: UsageRow
}

public func alerts(for rows: [UsageRow], now: Date = Date()) -> [UsageAlert] {
    rows.compactMap { row in
        let level = alertLevel(for: row, now: now)
        guard level != .none else { return nil }
        let window = row.resetsAt.map { String(Int($0.timeIntervalSince1970 / 60)) } ?? "none"
        return UsageAlert(key: "\(row.title)|\(window)|\(level.rawValue)", level: level, row: row)
    }
}
