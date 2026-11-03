using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace EqualTune.AudioLab;

/// <summary>The notification-area icon and its right-click menu.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    public event Action? OpenRequested, ToggleEqRequested, ExitRequested;

    public TrayIcon()
    {
        Drawing.Icon? icon = null;
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/EqualTune.ico"))?.Stream;
            if (stream is not null) icon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { AppLog.Error("Could not load the tray icon", ex); }

        _icon = new Forms.NotifyIcon { Text = "EqualTune", Icon = icon ?? Drawing.SystemIcons.Application, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open EqualTune", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("EQ on / off", null, (_, _) => ToggleEqRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit EqualTune", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
    }

    public void SetTip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
