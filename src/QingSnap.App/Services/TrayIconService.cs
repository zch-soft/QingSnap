using System.Drawing;
using System.Windows.Forms;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly CaptureCoordinator _captureCoordinator;
    private readonly AppSettingsService _settingsService;
    private readonly Action _openSettings;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appIcon;
    private readonly ToastNotificationService _toastNotifications = new();
    private bool _disposed;

    public TrayIconService(
        CaptureCoordinator captureCoordinator,
        AppSettingsService settingsService,
        Action openSettings)
    {
        _captureCoordinator = captureCoordinator;
        _settingsService = settingsService;
        _openSettings = openSettings;
        _appIcon = QingSnapTrayIconFactory.Create();
        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9F)
        };
        _menu.Opening += (_, _) => RebuildMenu();
        RebuildMenu();

        var settings = settingsService.Current;
        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = $"QingSnap — {DisplayGesture(settings, HotkeyAction.RegionCapture)} 截图 · {DisplayGesture(settings, HotkeyAction.PinRecentImage)} 贴图",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _captureCoordinator.StartRegionCapture();
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var settings = _settingsService.Current;
        _menu.Items.Add(CreateItem($"区域截图{ShortcutSuffix(settings, HotkeyAction.RegionCapture)}", (_, _) => _captureCoordinator.StartRegionCapture(), true));
        _menu.Items.Add(CreateItem($"长截图（自动滚动）{ShortcutSuffix(settings, HotkeyAction.AutomaticLongCapture)}", (_, _) => _captureCoordinator.StartLongCapture()));
        _menu.Items.Add(CreateItem($"长截图（手动滚动）{ShortcutSuffix(settings, HotkeyAction.ManualLongCapture)}", (_, _) => _captureCoordinator.StartManualLongCapture()));
        _menu.Items.Add(CreateItem($"重复上次范围{ShortcutSuffix(settings, HotkeyAction.RepeatLastRegion)}", (_, _) => _captureCoordinator.RepeatLastCapture()));
        _menu.Items.Add(CreatePresetMenu());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(CreateItem($"循环贴图（最近 5 张）{ShortcutSuffix(settings, HotkeyAction.PinRecentImage)}", (_, _) => _captureCoordinator.PinClipboardImage()));
        _menu.Items.Add(CreateItem($"识别最近截图文字{ShortcutSuffix(settings, HotkeyAction.OcrLatestCapture)}", (_, _) => _captureCoordinator.RecognizeLatestCapture()));
        _menu.Items.Add(CreateItem($"截图记录{ShortcutSuffix(settings, HotkeyAction.OpenHistory)}", (_, _) => _captureCoordinator.OpenHistoryWindow()));
        _menu.Items.Add(CreateItem("打开记录文件夹", (_, _) => _captureCoordinator.OpenHistoryDirectory()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(CreateItem("设置", (_, _) => _openSettings()));
        _menu.Items.Add(CreateItem("导出诊断信息", (_, _) => _captureCoordinator.ExportDiagnostics()));
        _menu.Items.Add(CreateItem("退出 QingSnap", (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));
    }

    private ToolStripMenuItem CreatePresetMenu()
    {
        var presetMenu = new ToolStripMenuItem("命名选区");
        var presets = _captureCoordinator.PresetStore.LoadAll();
        if (presets.Count == 0)
        {
            presetMenu.DropDownItems.Add(new ToolStripMenuItem("（暂无命名选区）")
            {
                Enabled = false
            });
        }
        else
        {
            foreach (var preset in presets.Take(15))
            {
                var count = preset.Selection.Regions is { Count: > 0 } regions
                    ? regions.Count
                    : 0;
                presetMenu.DropDownItems.Add(CreateItem(
                    $"{preset.Name}（{count} 个区域）",
                    (_, _) => _captureCoordinator.StartNamedSelectionCapture(preset)));
            }

            if (presets.Count > 15)
            {
                presetMenu.DropDownItems.Add(CreateItem(
                    $"更多（{presets.Count - 15}）…",
                    (_, _) => _captureCoordinator.OpenSelectionPresetManager()));
            }
        }

        presetMenu.DropDownItems.Add(new ToolStripSeparator());
        presetMenu.DropDownItems.Add(CreateItem(
            "管理命名选区…",
            (_, _) => _captureCoordinator.OpenSelectionPresetManager()));
        return presetMenu;
    }

    public event EventHandler? ExitRequested;

    public void ShowCaptureCompleted(CaptureResult result)
    {
        if (result.ImageCount > 1)
        {
            if (result.CopiedToClipboard)
            {
                _toastNotifications.ShowSuccess($"已保存 {result.ImageCount} 张截图并复制合成图");
            }
            else if (result.CopyRequested)
            {
                _toastNotifications.ShowWarning(
                    $"已保存 {result.ImageCount} 张截图\n剪贴板仍被占用");
            }
            else
            {
                _toastNotifications.ShowSuccess($"已保存 {result.ImageCount} 张截图");
            }

            return;
        }

        if (result.CopiedToClipboard)
        {
            _toastNotifications.ShowSuccess("已复制到剪贴板");
        }
        else if (result.CopyRequested)
        {
            _toastNotifications.ShowWarning("截图已保存\n剪贴板仍被占用");
        }
        else
        {
            _toastNotifications.ShowSuccess("截图已保存");
        }
    }

    public void ShowError(string message) => _toastNotifications.ShowWarning(message);

    public void ShowUpdateAvailable(string version) =>
        _toastNotifications.ShowInformation($"发现 QingSnap {version}\n可在设置中查看并下载");

    public void ShowHotkeyState(bool suspended)
    {
        if (suspended)
        {
            _toastNotifications.ShowWarning("QingSnap 全局快捷键已暂停");
        }
        else
        {
            _toastNotifications.ShowSuccess("QingSnap 全局快捷键已恢复");
        }
    }

    public void ShowDelay(int seconds) => _toastNotifications.ShowCountdown(seconds);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toastNotifications.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _appIcon.Dispose();
    }

    private static ToolStripMenuItem CreateItem(string text, EventHandler onClick, bool isDefault = false)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += onClick;
        if (isDefault)
        {
            item.Font = new Font(item.Font, FontStyle.Bold);
        }

        return item;
    }

    private static string ShortcutSuffix(AppSettings settings, HotkeyAction action)
    {
        var binding = HotkeyCatalog.GetBinding(settings, action);
        return binding.IsEnabled && !string.IsNullOrWhiteSpace(binding.Gesture)
            ? $"    {binding.Gesture}"
            : string.Empty;
    }

    private static string DisplayGesture(AppSettings settings, HotkeyAction action)
    {
        var binding = HotkeyCatalog.GetBinding(settings, action);
        return binding.IsEnabled && !string.IsNullOrWhiteSpace(binding.Gesture)
            ? binding.Gesture
            : "未设置";
    }
}
