using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32;
using UsageCore;

namespace ClaudeUsageBar;

sealed class UsageException(string message, double? retryAfter = null) : Exception(message)
{
    public double? RetryAfter { get; } = retryAfter;
}

static class UsageApi
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<Usage> FetchAsync()
    {
        var token = ReadToken() ?? throw new UsageException("Not logged in: run claude and /login");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        using var response = await Http.SendAsync(request);
        // shortcut: no token refresh of our own; Claude Code refreshes it on its next run. Upgrade if expired tokens become common.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UsageException("Token expired: open Claude Code once to refresh it");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter;
            var wait = retry?.Delta?.TotalSeconds
                ?? (retry?.Date is { } date ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds) : 60);
            throw new UsageException($"Rate limited, retrying in {(int)wait}s", wait);
        }
        if (response.StatusCode != HttpStatusCode.OK)
            throw new UsageException($"API returned HTTP {(int)response.StatusCode}");
        return Usage.Decode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>On Windows Claude Code keeps its OAuth credentials in a plain file,
    /// %USERPROFILE%\.claude\.credentials.json (or under CLAUDE_CONFIG_DIR).</summary>
    static string? ReadToken()
    {
        var path = UsageFormat.CredentialsPath(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        try { return UsageFormat.AccessToken(File.ReadAllText(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>What the app remembers between runs, in %LOCALAPPDATA%\ClaudeUsageBar\state.json.</summary>
sealed class AppState
{
    public bool DidSetUpLoginItem { get; set; }
    public HashSet<string> SentAlerts { get; set; } = [];

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageBar", "state.json");

    public static AppState Load()
    {
        try { return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Open at login through the per-user Run key, no admin rights needed.</summary>
static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "ClaudeUsageBar";
    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(Name, Command);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    /// <summary>Points an enabled entry at the current exe, in case the app was moved or reinstalled elsewhere.</summary>
    public static void Repair()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(Name) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            key.SetValue(Name, Command);
    }
}

static class WindowsTheme
{
    const string Personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Apps theme (Settings › Personalization › Colors › app mode).</summary>
    public static bool AppsDark => Registry.GetValue(Personalize, "AppsUseLightTheme", 1) is 0;

    /// <summary>Taskbar theme, which can differ from the apps theme.</summary>
    public static bool TaskbarDark => Registry.GetValue(Personalize, "SystemUsesLightTheme", 0) is 0;
}
