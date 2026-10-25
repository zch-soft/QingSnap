using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QingSnap.App.Models;
using QingSnap.App.Services;
using MediaColor = System.Windows.Media.Color;
using SelectionMode = QingSnap.App.Models.SelectionMode;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace QingSnap.App.Views;

public partial class SelectionPresetManagerWindow : Window
{
    private readonly SelectionPresetStore _store;
    private readonly Action<SelectionPreset> _startCapture;
    private readonly Func<SelectionSet?> _getLastSelection;

    public SelectionPresetManagerWindow(
        SelectionPresetStore store,
        Action<SelectionPreset> startCapture,
        Func<SelectionSet?> getLastSelection)
    {
        _store = store;
        _startCapture = startCapture;
        _getLastSelection = getLastSelection;
        InitializeComponent();
        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;
        Rebuild();
    }

    private void OnStoreChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(Rebuild);

    private void Rebuild()
    {
        PresetPanel.Children.Clear();
        var presets = _store.LoadAll();
        if (presets.Count == 0)
        {
            PresetPanel.Children.Add(new TextBlock
            {
                Text = "还没有命名选区。\n先在截图工具栏点击 ★ 保存当前选区，或点击下方“从上次选区新建”。",
                Foreground = new SolidColorBrush(MediaColor.FromRgb(127, 147, 157)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 18, 2, 0)
            });
            return;
        }

        foreach (var preset in presets)
        {
            PresetPanel.Children.Add(CreateRow(preset));
        }
    }

    private UIElement CreateRow(SelectionPreset preset)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(14, 26, 34)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(68, 83, 106)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var nameBox = new WpfTextBox
        {
            Text = preset.Name,
            MinWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "修改名称后按 Enter 或点击其他位置保存"
        };
        nameBox.LostFocus += (_, _) => Rename(preset, nameBox);
        nameBox.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter)
            {
                return;
            }

            Rename(preset, nameBox);
            args.Handled = true;
        };
        Grid.SetColumn(nameBox, 0);
        grid.Children.Add(nameBox);

        var details = new TextBlock
        {
            Text = Describe(preset.Selection),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(127, 147, 157)),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(CreateButton("开始截图", () =>
        {
            _startCapture(preset);
            Close();
        }));
        actions.Children.Add(CreateButton("更新为上次", () => UpdateFromLast(preset)));
        actions.Children.Add(CreateButton("删除", () => Delete(preset)));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        row.Child = grid;
        return row;
    }

    private static string Describe(SelectionSet selection)
    {
        var count = selection.Regions is { Count: > 0 }
            ? selection.Regions.Count
            : 0;
        var mode = selection.Mode == SelectionMode.Matrix && selection.Matrix is { } matrix
            ? $"{matrix.Rows}×{matrix.Columns}"
            : "自由";
        return selection.Padding > 0
            ? $"{mode} · {count} 个区域 · 外扩 {selection.Padding}px"
            : $"{mode} · {count} 个区域";
    }

    private void Rename(SelectionPreset preset, WpfTextBox nameBox)
    {
        var name = nameBox.Text.Trim();
        if (string.Equals(name, preset.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (_store.Rename(preset.Id, name))
        {
            SetStatus("名称已更新");
        }
    }

    private void UpdateFromLast(SelectionPreset preset)
    {
        var selection = _getLastSelection();
        if (selection is not { HasSelection: true })
        {
            SetStatus("还没有可用的上次选区，请先完成一次截图。", warning: true);
            return;
        }

        _store.ReplaceSelection(preset.Id, selection);
        SetStatus($"已用上次选区更新“{preset.Name}”");
    }

    private void Delete(SelectionPreset preset)
    {
        var answer = System.Windows.MessageBox.Show(
            this,
            $"确定删除命名选区“{preset.Name}”？",
            "删除命名选区",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        _store.Delete(preset.Id);
        SetStatus($"已删除“{preset.Name}”");
    }

    private void OnCreateFromLastClick(object sender, RoutedEventArgs e)
    {
        var selection = _getLastSelection();
        if (selection is not { HasSelection: true })
        {
            SetStatus("还没有可用的上次选区，请先完成一次截图。", warning: true);
            return;
        }

        var preset = _store.Add($"选区 {_store.LoadAll().Count + 1}", selection);
        SetStatus($"已新建“{preset.Name}”，可直接修改名称");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string message, bool warning = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(warning
            ? MediaColor.FromRgb(255, 145, 137)
            : MediaColor.FromRgb(118, 223, 238));
    }

    private static WpfButton CreateButton(string text, Action action)
    {
        var button = new WpfButton
        {
            Content = text,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => action();
        return button;
    }
}
