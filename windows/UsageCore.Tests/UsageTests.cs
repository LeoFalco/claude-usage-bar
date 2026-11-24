// Port of Tests/UsageCoreTests/UsageTests.swift, case for case.
using UsageCore;
using Xunit;

public class UsageTests
{
    [Fact]
    public void PrefersLimitsArray()
    {
        const string json = """
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
        """;
        var usage = Usage.Decode(json);
        Assert.Equal(["Session", "Weekly", "Weekly · Fable"], usage.Rows.Select(r => r.Title));
        Assert.Equal(UsageFormat.ParseIsoDate("2026-11-28T06:00:00Z"), usage.Rows[1].ResetsAt);
        Assert.Equal("23% · 41%", usage.MenuBarTitle);
    }

    [Fact]
    public void FallsBackToLegacyWindows()
    {
        const string json = """
        {"five_hour": {"utilization": 23.4, "resets_at": "2025-10-17T19:00:00.123456+00:00"},
         "seven_day": {"utilization": 41.0, "resets_at": "2025-10-20T08:00:00+00:00"},
         "seven_day_opus": null}
        """;
        var usage = Usage.Decode(json);
        Assert.Equal(["Session 5h", "Weekly"], usage.Rows.Select(r => r.Title));
        Assert.Equal("23% · 41%", usage.MenuBarTitle);
    }

    [Fact]
    public void SkipsLimitWithoutPercent()
    {
        const string json = """{"limits": [{"kind": "weekly_all", "group": "weekly", "percent": null}, {"kind": "x", "group": "weekly", "percent": 7}]}""";
        Assert.Equal("7%", Usage.Decode(json).MenuBarTitle);
    }

    [Fact]
    public void EmptyResponse() => Assert.Equal("–", Usage.Decode("{}").MenuBarTitle);

    [Fact]
    public void RejectsBadDate() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Usage.Decode("""{"five_hour": {"utilization": 1, "resets_at": "soon"}}"""));

    [Fact]
    public void ReadsTokenFromCredentials()
    {
        const string creds = """{"claudeAiOauth":{"accessToken":"sk-ant-oat01-x","refreshToken":"r","expiresAt":1}}""";
        Assert.Equal("sk-ant-oat01-x", UsageFormat.AccessToken(creds));
        Assert.Null(UsageFormat.AccessToken("{}"));
        Assert.Null(UsageFormat.AccessToken("not json"));
        Assert.Null(UsageFormat.AccessToken("[]"));
    }

    [Fact]
    public void CredentialsPath()
    {
        Assert.Equal(Path.Combine("home", ".claude", ".credentials.json"), UsageFormat.CredentialsPath(null, "home"));
        Assert.Equal(Path.Combine("home", ".claude", ".credentials.json"), UsageFormat.CredentialsPath("", "home"));
        Assert.Equal(Path.Combine("cfg", ".credentials.json"), UsageFormat.CredentialsPath("cfg", "home"));
    }

    [Fact]
    public void ResetCountdown()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(0);
        Assert.Equal("resets in 2h 14m", UsageFormat.ResetDescription(now.AddMinutes(134), now));
        Assert.Equal("resets in 3d 5h", UsageFormat.ResetDescription(now.AddDays(3).AddHours(5), now));
        Assert.Equal("resets in 0m", UsageFormat.ResetDescription(now.AddSeconds(-60), now));
        Assert.Equal("", UsageFormat.ResetDescription(null, now));
    }

    [Fact]
    public void Projection()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        UsageRow Weekly(double percent, double daysLeft) => new("weekly", "Weekly", percent, now.AddDays(daysLeft));
        // 2 of 7 days elapsed at 20%: ends near 70%.
        Assert.Equal(new Projection.Lasts(70), Pace.Projection(Weekly(20, 5), now));
        // 2 days elapsed at 50%: 25% a day, hits 100% two days from now.
        Assert.Equal(new Projection.RunsOut(now.AddDays(2)), Pace.Projection(Weekly(50, 5), now));
        Assert.Equal(new Projection.RunsOut(now), Pace.Projection(Weekly(100, 3), now));
        // Too early in the window, unknown group, or no reset date: no guess.
        Assert.Null(Pace.Projection(Weekly(5, 6.9), now));
        Assert.Null(Pace.Projection(new UsageRow("monthly", "M", 10, now.AddDays(1)), now));
        Assert.Null(Pace.Projection(new UsageRow("weekly", "W", 10, null), now));
        // Session window is 5h: 1h elapsed at 10% ends near 50%.
        Assert.Equal(new Projection.Lasts(50), Pace.Projection(new UsageRow("session", "Session", 10, now.AddHours(4)), now));
    }

    [Fact]
    public void Alerts()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var reset = now.AddDays(5);
        var calm = new UsageRow("weekly", "Weekly", 20, reset);
        var fast = new UsageRow("weekly", "Weekly", 50, reset);
        var high = new UsageRow("weekly", "Weekly", 92, reset);
        Assert.Equal(AlertLevel.None, Pace.AlertLevel(calm, now));
        Assert.Equal(AlertLevel.Pace, Pace.AlertLevel(fast, now));
        Assert.Equal(AlertLevel.High, Pace.AlertLevel(high, now));
        Assert.Equal([AlertLevel.Pace, AlertLevel.High], Pace.Alerts([calm, fast, high], now).Select(a => a.Level));
        // Same limit, window and level give the same key; another level does not.
        Assert.Equal(Pace.Alerts([fast], now)[0].Key, Pace.Alerts([fast], now.AddSeconds(60))[0].Key);
        Assert.NotEqual(Pace.Alerts([fast], now)[0].Key, Pace.Alerts([high], now)[0].Key);
    }

    [Fact]
    public void RoundsHalvesUp()
    {
        Assert.Equal(3, UsageFormat.Round(2.5));
        Assert.Equal(23, UsageFormat.Round(23.4));
    }
}
