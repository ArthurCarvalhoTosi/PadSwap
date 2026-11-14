using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace PadSwap;

/// <summary>Notification area icon: shows whether the remap is active and lets the user quit.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PadSwap";

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startWithWindowsItem;
    private readonly Icon _activeIcon = CreateIcon(Color.FromArgb(46, 160, 67));
    private readonly Icon _idleIcon = CreateIcon(Color.FromArgb(110, 118, 129));
    private readonly Icon _errorIcon = CreateIcon(Color.FromArgb(207, 34, 46));
    private readonly Remapper _remapper = new();
    private readonly SynchronizationContext _uiContext;
    private SettingsForm? _settingsForm;

    public TrayContext(bool openSettings)
    {
        _statusItem = new ToolStripMenuItem("Iniciando...") { Enabled = false };
        _startWithWindowsItem = new ToolStripMenuItem("Iniciar com o Windows", null, OnToggleStartWithWindows)
        {
            Checked = IsStartWithWindowsEnabled(),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configurar...", null, (_, _) => ShowSettings());
        menu.Items.Add(_startWithWindowsItem);
        menu.Items.Add("Abrir log", null, (_, _) => OpenLog());
        menu.Items.Add("Sair (controle volta ao normal)", null, (_, _) => Exit());

        // Creating the first control installs the WinForms synchronization context.
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _notifyIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "PadSwap",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowSettings();

        _remapper.StateChanged += (state, text) => _uiContext.Post(_ => ShowState(state, text), null);
        _remapper.Start();
        if (openSettings) ShowSettings();
    }

    private void ShowState(RemapperState state, string text)
    {
        _statusItem.Text = text;
        _notifyIcon.Text = $"PadSwap - {text}";
        _notifyIcon.Icon = state switch
        {
            RemapperState.Active => _activeIcon,
            RemapperState.Error => _errorIcon,
            _ => _idleIcon,
        };
    }

    private void ShowSettings()
    {
        if (_settingsForm == null || _settingsForm.IsDisposed)
            _settingsForm = new SettingsForm(_remapper, _activeIcon);
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void OnToggleStartWithWindows(object? sender, EventArgs e)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (_startWithWindowsItem.Checked)
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        else
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
        _startWithWindowsItem.Checked = IsStartWithWindowsEnabled();
    }

    private static bool IsStartWithWindowsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValueName) != null;
    }

    private static void OpenLog()
    {
        if (File.Exists(Log.FilePath))
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    private void Exit()
    {
        _notifyIcon.Visible = false;
        _remapper.Stop();
        ExitThread();
    }

    private static Icon CreateIcon(Color background)
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(background))
        using (var font = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(brush, 1, 1, 30, 30);
            graphics.DrawString("AB", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
