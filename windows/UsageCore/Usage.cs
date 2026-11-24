// Windows port of Sources/UsageCore/Usage.swift. Keep both in step: same rules, same tests.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UsageCore;

/// <summary>One line of the panel: a plan limit and how much of it is used.</summary>
/// <param name="Percent">Percentage used (0-100).</param>
public sealed record UsageRow(string Group, string Title, double Percent, DateTimeOffset? ResetsAt);

/// <summary>Response of <c>GET https://api.anthropic.com/api/oauth/usage</c> (the endpoint behind Claude Code's /usage).</summary>
public sealed record Usage
{
    public sealed record Window
    {
        public double? Utilization { get; init; }
        public DateTimeOffset? ResetsAt { get; init; }
    }

    public sealed record Limit
    {
        public sealed record LimitScope
        {
            public sealed record LimitModel
            {
                public string? DisplayName { get; init; }
            }
            public LimitModel? Model { get; init; }
        }
        public string? Kind { get; init; }
        public string? Group { get; init; }
        public double? Percent { get; init; }
        public DateTimeOffset? ResetsAt { get; init; }
        public LimitScope? Scope { get; init; }
    }

    public Window? FiveHour { get; init; }
    public Window? SevenDay { get; init; }
    public Window? SevenDayOpus { get; init; }
    public Window? SevenDaySonnet { get; init; }
    /// <summary>Newer, generic list of limits; preferred when present.</summary>
    public IReadOnlyList<Limit>? Limits { get; init; }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new IsoDateConverter() },
    };

    public static Usage Decode(string json) =>
        JsonSerializer.Deserialize<Usage>(json, Options) ?? throw new JsonException("Empty response");

    public IReadOnlyList<UsageRow> Rows
    {
        get
        {
            if (Limits is { Count: > 0 })
            {
                return Limits
                    .Where(l => l.Percent is not null)
                    .Select(l =>
                    {
                        var group = l.Group ?? l.Kind ?? "limit";
                        var name = Capitalize(group.Replace('_', ' '));
                        var model = l.Scope?.Model?.DisplayName is { } m ? $" · {m}" : "";
                        return new UsageRow(group, name + model, l.Percent!.Value, l.ResetsAt);
                    })
                    .ToList();
            }
            (string Group, string Title, Window? Window)[] legacy =
            [
                ("session", "Session 5h", FiveHour), ("weekly", "Weekly", SevenDay),
                ("weekly", "Weekly · Opus", SevenDayOpus), ("weekly", "Weekly · Sonnet", SevenDaySonnet),
            ];
            return legacy
                .Where(x => x.Window?.Utilization is not null)
                .Select(x => new UsageRow(x.Group, x.Title, x.Window!.Utilization!.Value, x.Window.ResetsAt))
                .ToList();
        }
    }

    /// <summary>Tray title: the highest usage per limit group, in API order, e.g. "23% · 41%".</summary>
    public string MenuBarTitle
    {
        get
        {
            var top = TopPerGroup(Rows);
            return top.Count == 0 ? "–" : string.Join(" · ", top.Select(t => $"{UsageFormat.Round(t.Percent)}%"));
        }
    }

    /// <summary>The highest usage per limit group, in API order.</summary>
    public static IReadOnlyList<(string Group, double Percent)> TopPerGroup(IEnumerable<UsageRow> rows)
    {
        var order = new List<string>();
        var top = new Dictionary<string, double>();
        foreach (var row in rows)
        {
            if (!top.TryGetValue(row.Group, out var current)) { order.Add(row.Group); current = 0; }
            top[row.Group] = Math.Max(current, row.Percent);
        }
        return order.Select(g => (g, top[g])).ToList();
    }

    /// <summary>Same as Swift's String.capitalized: first letter of each word upper, the rest lower.</summary>
    static string Capitalize(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    sealed class IsoDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var raw = reader.GetString() ?? "";
            return UsageFormat.ParseIsoDate(raw) ?? throw new JsonException($"Bad date: {raw}");
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("o", CultureInfo.InvariantCulture));
    }
}

public static partial class UsageFormat
{
    /// <summary>Rounds like Swift's <c>rounded()</c>: halves away from zero.</summary>
    public static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    [GeneratedRegex(@"\.\d+")]
    private static partial Regex Fraction();

    /// <summary>The API sends microsecond precision ("2025-10-17T19:00:00.123456+00:00"); the fraction is
    /// dropped, as the macOS app does, so both apps agree on dates and alert keys.</summary>
    public static DateTimeOffset? ParseIsoDate(string raw)
    {
        var trimmed = Fraction().Replace(raw, "");
        return DateTimeOffset.TryParseExact(trimmed, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
    }

    /// <summary>Pulls the OAuth access token out of the JSON Claude Code stores in ~/.claude/.credentials.json.</summary>
    public static string? AccessToken(string credentialsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(credentialsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                && oauth.ValueKind == JsonValueKind.Object
                && oauth.TryGetProperty("accessToken", out var token)
                && token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Where Claude Code keeps its credentials on Windows and Linux: <c>$CLAUDE_CONFIG_DIR</c>
    /// when set, else <c>%USERPROFILE%\.claude</c>.</summary>
    public static string CredentialsPath(string? configDir, string home) =>
        Path.Combine(string.IsNullOrEmpty(configDir) ? Path.Combine(home, ".claude") : configDir, ".credentials.json");

    /// <summary>"resets in 2h 14m" style countdown.</summary>
    public static string ResetDescription(DateTimeOffset? date, DateTimeOffset now)
    {
        if (date is null) return "";
        var minutes = Math.Max(0, (int)((date.Value - now).TotalSeconds / 60));
        var (d, h, m) = (minutes / 1440, minutes % 1440 / 60, minutes % 60);
        if (d > 0) return $"resets in {d}d {h}h";
        if (h > 0) return $"resets in {h}h {m}m";
        return $"resets in {m}m";
    }
}

/// <summary>Where a limit ends up if usage keeps the pace it has had since the window started.</summary>
public abstract record Projection
{
    /// <summary>The limit lasts until reset, ending near this percentage.</summary>
    public sealed record Lasts(double PercentAtReset) : Projection;
    /// <summary>The limit hits 100% at this date, before the reset.</summary>
    public sealed record RunsOut(DateTimeOffset At) : Projection;
}

/// <summary>How worrying a limit is. Higher is worse.</summary>
public enum AlertLevel { None = 0, Pace = 1, High = 2 }

/// <summary>One notification-worthy event. <c>Key</c> is stable for a limit, its window and the level,
/// so the same alert is sent once per window.</summary>
public sealed record UsageAlert(string Key, AlertLevel Level, UsageRow Row);

public static class Pace
{
    /// <summary>Length of each limit window, by group.</summary>
    public static TimeSpan? WindowLength(string group) => group switch
    {
        "session" => TimeSpan.FromHours(5),
        "weekly" => TimeSpan.FromDays(7),
        _ => null,
    };

    /// <summary>Linear projection from the average pace since the window started.
    /// Returns null for unknown windows or too early in the window to tell.</summary>
    public static Projection? Projection(UsageRow row, DateTimeOffset now)
    {
        if (row.ResetsAt is not { } resetsAt || WindowLength(row.Group) is not { } window) return null;
        var length = window.TotalSeconds;
        var elapsed = length - (resetsAt - now).TotalSeconds;
        // shortcut: linear average since window start, ignores daily rhythm; upgrade to a recent-window rate if it misleads.
        if (elapsed < length * 0.05 || elapsed > length) return null;
        if (row.Percent >= 100) return new Projection.RunsOut(now);
        var projected = row.Percent * length / elapsed;
        if (projected <= 100) return new Projection.Lasts(projected);
        return new Projection.RunsOut(now.AddSeconds((100 - row.Percent) * elapsed / row.Percent));
    }

    /// <summary><c>High</c> from 90% used; <c>Pace</c> when the current pace runs out before the reset.</summary>
    public static AlertLevel AlertLevel(UsageRow row, DateTimeOffset now)
    {
        if (row.Percent >= 90) return UsageCore.AlertLevel.High;
        return Projection(row, now) is Projection.RunsOut ? UsageCore.AlertLevel.Pace : UsageCore.AlertLevel.None;
    }

    public static IReadOnlyList<UsageAlert> Alerts(IEnumerable<UsageRow> rows, DateTimeOffset now) =>
        rows.Select(row => (row, level: AlertLevel(row, now)))
            .Where(x => x.level != UsageCore.AlertLevel.None)
            .Select(x =>
            {
                var window = x.row.ResetsAt is { } r ? (r.ToUnixTimeSeconds() / 60).ToString(CultureInfo.InvariantCulture) : "none";
                return new UsageAlert($"{x.row.Title}|{window}|{(int)x.level}", x.level, x.row);
            })
            .ToList();
}
