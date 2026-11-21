import Foundation
import XCTest
@testable import UsageCore

final class UsageTests: XCTestCase {
    func testPrefersLimitsArray() throws {
        let json = """
        {
          "five_hour": null,
          "seven_day": {"utilization": 3.0, "resets_at": "2026-11-28T06:00:00.366758+00:00"},
          "extra_usage": {"is_enabled": false},
          "limits": [
            {"kind": "session", "group": "session", "percent": 23, "resets_at": "2026-11-21T15:00:00+00:00", "scope": null, "is_active": true},
            {"kind": "weekly_all", "group": "weekly", "percent": 3, "resets_at": "2026-11-28T06:00:00.366758+00:00", "scope": null, "is_active": true},
            {"kind": "weekly_scoped", "group": "weekly", "percent": 41, "resets_at": "2026-11-28T06:00:00+00:00",
             "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}, "is_active": false}
          ]
        }
        """
        let usage = try Usage.decode(Data(json.utf8))
        XCTAssertEqual(usage.rows.map(\.title), ["Session", "Weekly", "Weekly · Fable"])
        XCTAssertEqual(usage.rows[1].resetsAt, parseISODate("2026-11-28T06:00:00Z"))
        XCTAssertEqual(usage.menuBarTitle, "23% · 41%")
    }

    func testFallsBackToLegacyWindows() throws {
        let json = """
        {"five_hour": {"utilization": 23.4, "resets_at": "2025-10-17T19:00:00.123456+00:00"},
         "seven_day": {"utilization": 41.0, "resets_at": "2025-10-20T08:00:00+00:00"},
         "seven_day_opus": null}
        """
        let usage = try Usage.decode(Data(json.utf8))
        XCTAssertEqual(usage.rows.map(\.title), ["Session 5h", "Weekly"])
        XCTAssertEqual(usage.menuBarTitle, "23% · 41%")
    }

    func testSkipsLimitWithoutPercent() throws {
        let json = #"{"limits": [{"kind": "weekly_all", "group": "weekly", "percent": null}, {"kind": "x", "group": "weekly", "percent": 7}]}"#
        XCTAssertEqual(try Usage.decode(Data(json.utf8)).menuBarTitle, "7%")
    }

    func testEmptyResponse() throws {
        XCTAssertEqual(try Usage.decode(Data("{}".utf8)).menuBarTitle, "–")
    }

    func testReadsTokenFromCredentials() {
        let creds = #"{"claudeAiOauth":{"accessToken":"sk-ant-oat01-x","refreshToken":"r","expiresAt":1}}"#
        XCTAssertEqual(accessToken(fromCredentials: Data(creds.utf8)), "sk-ant-oat01-x")
        XCTAssertNil(accessToken(fromCredentials: Data("{}".utf8)))
        XCTAssertNil(accessToken(fromCredentials: Data("not json".utf8)))
    }

    func testResetCountdown() {
        let now = Date(timeIntervalSince1970: 0)
        XCTAssertEqual(resetDescription(now.addingTimeInterval(134 * 60), now: now), "resets in 2h 14m")
        XCTAssertEqual(resetDescription(now.addingTimeInterval(3 * 86400 + 5 * 3600), now: now), "resets in 3d 5h")
        XCTAssertEqual(resetDescription(now.addingTimeInterval(-60), now: now), "resets in 0m")
        XCTAssertEqual(resetDescription(nil, now: now), "")
    }

    func testProjection() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        let day: TimeInterval = 86400
        func weekly(_ percent: Double, daysLeft: Double) -> UsageRow {
            UsageRow(group: "weekly", title: "Weekly", percent: percent, resetsAt: now.addingTimeInterval(daysLeft * day))
        }
        // 2 of 7 days elapsed at 20%: ends near 70%.
        XCTAssertEqual(projection(for: weekly(20, daysLeft: 5), now: now), .lasts(percentAtReset: 70))
        // 2 days elapsed at 50%: 25% a day, hits 100% two days from now.
        XCTAssertEqual(projection(for: weekly(50, daysLeft: 5), now: now), .runsOut(at: now.addingTimeInterval(2 * day)))
        XCTAssertEqual(projection(for: weekly(100, daysLeft: 3), now: now), .runsOut(at: now))
        // Too early in the window, unknown group, or no reset date: no guess.
        XCTAssertNil(projection(for: weekly(5, daysLeft: 6.9), now: now))
        XCTAssertNil(projection(for: UsageRow(group: "monthly", title: "M", percent: 10, resetsAt: now.addingTimeInterval(day)), now: now))
        XCTAssertNil(projection(for: UsageRow(group: "weekly", title: "W", percent: 10, resetsAt: nil), now: now))
        // Session window is 5h: 1h elapsed at 10% ends near 50%.
        let session = UsageRow(group: "session", title: "Session", percent: 10, resetsAt: now.addingTimeInterval(4 * 3600))
        XCTAssertEqual(projection(for: session, now: now), .lasts(percentAtReset: 50))
    }

    func testAlerts() {
        let now = Date(timeIntervalSince1970: 1_000_000)
        let reset = now.addingTimeInterval(5 * 86400)
        let calm = UsageRow(group: "weekly", title: "Weekly", percent: 20, resetsAt: reset)
        let fast = UsageRow(group: "weekly", title: "Weekly", percent: 50, resetsAt: reset)
        let high = UsageRow(group: "weekly", title: "Weekly", percent: 92, resetsAt: reset)
        XCTAssertEqual(alertLevel(for: calm, now: now), .none)
        XCTAssertEqual(alertLevel(for: fast, now: now), .pace)
        XCTAssertEqual(alertLevel(for: high, now: now), .high)
        XCTAssertEqual(alerts(for: [calm, fast, high], now: now).map(\.level), [.pace, .high])
        // Same limit, window and level give the same key; another level does not.
        XCTAssertEqual(alerts(for: [fast], now: now).first?.key, alerts(for: [fast], now: now.addingTimeInterval(60)).first?.key)
        XCTAssertNotEqual(alerts(for: [fast], now: now).first?.key, alerts(for: [high], now: now).first?.key)
    }
}
