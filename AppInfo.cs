using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace EqualTune.AudioLab;

/// <summary>Where EqualTune keeps its data, which version it is, and the links it can open.</summary>
internal static class AppInfo
{
    public const string DonateUrl = "https://buymeacoffee.com/equaltune";   // EqualTune support link (leave empty until set; the button then says it is not set up yet)
    public const string VbAudioUrl = "https://vb-audio.com/Cable/";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>%LOCALAPPDATA%\EqualTune — settings, presets and skins (WebView2 profile) live here.</summary>
    public static string DataRoot
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EqualTune");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>The WebView2 profile folder. On first run it adopts the profile the app used to keep next to the exe, so saved presets and skins are not lost.</summary>
    public static string PrepareWebViewData()
    {
        var target = Path.Combine(DataRoot, "WebView2");
        try
        {
            if (!Directory.Exists(target))
            {
                var exeName = Path.GetFileName(Environment.ProcessPath ?? "EqualTune.AudioLab.exe");
                var old = Path.Combine(AppContext.BaseDirectory, exeName + ".WebView2");
                if (Directory.Exists(old))
                {
                    CopyDirectory(old, target);
                    AppLog.Info("Moved saved settings from " + old + " to " + target);
                }
            }
        }
        catch (Exception ex) { AppLog.Error("Could not move the old settings folder", ex); }
        Directory.CreateDirectory(target);
        return target;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            try { File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true); } catch { /* locked cache files are not needed */ }
        }
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Error("Could not open " + url, ex); }
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { AppLog.Error("Could not open " + path, ex); }
    }
}

/// <summary>Plain-text log in a "logs" folder next to the app (falls back to %LOCALAPPDATA%\EqualTune\logs when that folder is read-only).</summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static string? _folder;

    public static string Folder
    {
        get
        {
            if (_folder is not null) return _folder;
            lock (Gate)
            {
                if (_folder is not null) return _folder;
                string[] candidates =
                [
                    Path.Combine(AppContext.BaseDirectory, "logs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EqualTune", "logs")
                ];
                foreach (var candidate in candidates)
                {
                    try
                    {
                        Directory.CreateDirectory(candidate);
                        var probe = Path.Combine(candidate, ".write-test");
                        File.WriteAllText(probe, "ok");
                        File.Delete(probe);
                        _folder = candidate;
                        break;
                    }
                    catch { /* try the next location */ }
                }
                _folder ??= Path.GetTempPath();
                Prune(_folder);
                return _folder;
            }
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : message + " — " + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = Path.Combine(Folder, $"equaltune-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }

    private static void Prune(string folder)
    {
        try
        {
            foreach (var file in Directory.GetFiles(folder, "equaltune-*.log"))
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-14)) File.Delete(file);
        }
        catch { }
    }
}
