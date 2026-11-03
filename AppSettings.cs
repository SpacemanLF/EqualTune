using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace EqualTune.AudioLab;

/// <summary>App-level options (tray, startup, first run). Saved in %LOCALAPPDATA%\EqualTune\settings.json.</summary>
internal sealed class AppSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoRoute { get; set; }
    public bool CheckUpdates { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public bool TrayTipShown { get; set; }

    private static string FilePath => Path.Combine(AppInfo.DataRoot, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) { AppLog.Error("Could not read settings.json", ex); }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { AppLog.Error("Could not save settings.json", ex); }
    }

    /// <summary>Adds or removes EqualTune from the current user's Windows startup list.</summary>
    public void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key is null) return;
            if (StartWithWindows && Environment.ProcessPath is { } exe)
                key.SetValue("EqualTune", $"\"{exe}\"" + (StartMinimized ? " --minimized" : ""));
            else
                key.DeleteValue("EqualTune", false);
        }
        catch (Exception ex) { AppLog.Error("Could not update the Windows startup entry", ex); }
    }
}
