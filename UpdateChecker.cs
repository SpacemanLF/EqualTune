using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace EqualTune.AudioLab;

internal sealed record UpdateInfo(string Version, string Notes, string PageUrl, string? DownloadUrl);

/// <summary>Checks the project's GitHub Releases for a newer version and runs its installer.</summary>
internal static class UpdateChecker
{
    public const string Repo = "SpacemanLF/EqualTune";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EqualTune/" + AppInfo.Version);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Returns the newer release, or null when you are up to date or no release exists yet.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var latest) || !TryParseVersion(AppInfo.Version, out var current) || latest <= current) return null;

        string? download = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.StartsWith("EqualTune-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    download = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        if (notes.Length > 700) notes = notes[..700] + "…";
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : $"https://github.com/{Repo}/releases";
        return new UpdateInfo(latest.ToString(3), notes, page, download);
    }

    public static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        text = text.Trim().TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];
        if (!Version.TryParse(text, out var parsed)) return false;
        version = parsed;
        return true;
    }

    /// <summary>Downloads the installer to a temp folder and starts it. Only https downloads from github.com are accepted.</summary>
    public static async Task RunInstallerAsync(UpdateInfo update)
    {
        if (update.DownloadUrl is null || !Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The release has no installer that EqualTune can download.");

        var folder = Path.Combine(Path.GetTempPath(), "EqualTune-Update");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"EqualTune-Setup-{update.Version}.exe");
        using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(file);
            await source.CopyToAsync(target);
        }
        AppLog.Info("Downloaded the update installer to " + file);
        Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }
}
