using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QingSnap.App.Models;
using QingSnap.App.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QingSnap.App.Views;

public partial class UninstallWindow : Window
{
    public UninstallWindow(IReadOnlyList<UninstallTarget> targets)
    {
        InitializeComponent();
        var byKey = targets.ToDictionary(target => target.Key, StringComparer.OrdinalIgnoreCase);
        Bind(byKey["history"], HistoryCheck, HistoryPathText);
        Bind(byKey["database"], DatabaseCheck, DatabasePathText);
        Bind(byKey["settings"], SettingsCheck, SettingsPathText);
        Bind(byKey["ocr"], OcrCheck, OcrPathText);
    }

    public UninstallOptions? Options { get; private set; }

    private static void Bind(UninstallTarget target, CheckBox checkBox, TextBlock pathText)
    {
        checkBox.Content = target.Label;
        pathText.Text = target.Exists ? target.Path : $"{target.Path}（不存在）";
        if (!target.CanDelete)
        {
            checkBox.IsEnabled = false;
            checkBox.IsChecked = false;
        }

        if (!string.IsNullOrWhiteSpace(target.Note))
        {
            pathText.Text += $" · {target.Note}";
        }
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        Options = new UninstallOptions(
            HistoryCheck.IsChecked == true,
            DatabaseCheck.IsChecked == true,
            SettingsCheck.IsChecked == true,
            OcrCheck.IsChecked == true);
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        DialogResult = false;
        Close();
    }
}
