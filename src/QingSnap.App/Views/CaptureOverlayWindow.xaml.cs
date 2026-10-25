using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using QingSnap.App.Controls;
using QingSnap.App.Infrastructure;
using QingSnap.App.Models;
using QingSnap.App.Services;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using WpfToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using MediaColor = System.Windows.Media.Color;
using SelectionMode = QingSnap.App.Models.SelectionMode;
using WpfCursors = System.Windows.Input.Cursors;
using WpfRect = System.Windows.Rect;

namespace QingSnap.App.Views;

public partial class CaptureOverlayWindow : Window
{
    private const double HitPadding = 9;
    private const double MinimumSelectionSize = 2;

    private readonly ScreenSnapshot _snapshot;
    private readonly DrawingRectangle? _initialLocalRegion;
    private readonly SelectionSet? _initialSelectionSet;
    private readonly IReadOnlyList<DrawingRectangle?> _recallLocalRegions;
    private readonly bool _showActionToolbar;
    private readonly AppSettings _settings;
    private readonly OcrService? _ocrService;
    private readonly QrCodeService? _qrCodeService;
    private readonly ClipboardService? _clipboardService;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>>? _loadTagsAsync;
    private readonly SelectionPresetStore? _presetStore;
    private readonly Rectangle[] _cornerMarks;
    private readonly CaptureAnnotationController _annotationController;
    private readonly DispatcherTimer _adjustmentBadgeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(850)
    };
    private readonly DispatcherTimer _ocrPrefetchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(180)
    };
    private CancellationTokenSource? _ocrPrefetchCancellation;
    private CancellationTokenSource? _qrCodeCancellation;
    private Task<OcrRecognitionResult>? _prefetchedOcrTask;
    private BitmapSource? _prefetchedOcrImage;
    private bool _creatingOcrPrefetch;
    private Point _dragStart;
    private WpfRect _selection = WpfRect.Empty;
    private WpfRect _selectionAtDragStart;
    private DrawingRectangle? _selectionPixelRegion;
    private DrawingRectangle? _selectionPixelAtDragStart;
    private DragMode _dragMode;
    private bool _isAnnotating;
    private WpfRect _smartPreview = WpfRect.Empty;
    private WpfRect _pendingSmartSelection = WpfRect.Empty;
    private bool _dragMoved;
    private MediaColor _currentPixelColor;
    private CaptureAnnotationTool _arrowTool = CaptureAnnotationTool.Arrow;
    private CaptureAnnotationTool _regionTool = CaptureAnnotationTool.Rectangle;
    private int _recallIndex;
    private CaptureAspectRatioMode _aspectRatioMode = CaptureAspectRatioMode.Free;
    private double? _lockedAspectRatio;
    private DrawingSize? _lockedSelectionSize;
    private bool _updatingGeometryFields;
    private bool _geometryWidthIsPrimary = true;
    private readonly HashSet<string> _selectedQuickTags = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _availableQuickTags = [];
    private bool _quickTagsLoaded;
    private readonly List<SelectionItem> _selections = [];
    private int _activeSelectionIndex = -1;
    private bool _dragCreatedSelection;
    private int? _selectionPaddingOverride;
    private SelectionMode _selectionMode = SelectionMode.Free;
    private MatrixState? _matrix;
    private MatrixState? _matrixAtDragStart;
    private MatrixCell? _matrixDragCell;
    private bool _matrixDragMoved;
    private bool _updatingMatrixFields;
    private readonly List<MatrixCell> _matrixCellOrder = [];

    private SelectionItem? ActiveSelection =>
        _activeSelectionIndex >= 0 && _activeSelectionIndex < _selections.Count
            ? _selections[_activeSelectionIndex]
            : null;

    private sealed class SelectionItem
    {
        public WpfRect Rect { get; set; }
        public DrawingRectangle Pixels { get; set; }
        public string? Label { get; set; }
        public Border Frame { get; init; } = null!;
        public Border Badge { get; init; } = null!;
        public TextBlock BadgeText { get; init; } = null!;
    }

    private sealed class MatrixState
    {
        public DrawingRectangle Bounds { get; set; }
        public int Rows { get; set; } = MatrixLayout.DefaultRows;
        public int Columns { get; set; } = MatrixLayout.DefaultColumns;
        public int GapX { get; set; } = MatrixLayout.DefaultGap;
        public int GapY { get; set; } = MatrixLayout.DefaultGap;
        public HashSet<MatrixCell> Excluded { get; } = [];

        public MatrixState Clone()
        {
            var clone = new MatrixState
            {
                Bounds = Bounds,
                Rows = Rows,
                Columns = Columns,
                GapX = GapX,
                GapY = GapY
            };
            clone.Excluded.UnionWith(Excluded);
            return clone;
        }

        public IReadOnlyList<MatrixCell> ExcludedCells => Excluded
            .OrderBy(cell => cell.Row)
            .ThenBy(cell => cell.Column)
            .ToArray();

        public MatrixSpec ToSpec(CaptureRegion bounds) => new(
            bounds,
            Rows,
            Columns,
            GapX,
            GapY,
            ExcludedCells);
    }

    public CaptureOverlayWindow(
        ScreenSnapshot snapshot,
        DrawingRectangle? initialLocalRegion = null,
        string? confirmationHint = null,
        bool showActionToolbar = true,
        IReadOnlyList<DrawingRectangle?>? recallLocalRegions = null,
        int recallIndex = -1,
        AppSettings? settings = null,
        OcrService? ocrService = null,
        QrCodeService? qrCodeService = null,
        ClipboardService? clipboardService = null,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? loadTagsAsync = null,
        SelectionPresetStore? presetStore = null,
        SelectionSet? initialSelectionSet = null)
    {
        _snapshot = snapshot;
        _initialLocalRegion = initialLocalRegion;
        _initialSelectionSet = initialSelectionSet;
        _recallLocalRegions = recallLocalRegions ?? [];
        _recallIndex = recallIndex >= 0 && recallIndex < _recallLocalRegions.Count
            ? recallIndex
            : -1;
        _showActionToolbar = showActionToolbar;
        _settings = settings ?? new AppSettings();
        _ocrService = ocrService;
        _qrCodeService = qrCodeService;
        _clipboardService = clipboardService;
        _loadTagsAsync = loadTagsAsync;
        _presetStore = presetStore;
        InitializeComponent();
        _annotationController = new CaptureAnnotationController(AnnotationLayer, snapshot, _settings);
        _annotationController.Changed += (_, _) =>
        {
            UpdateAnnotationButtons();
            UpdateStyleButtons();
            InvalidateOcrPrefetch();
        };
        UpdateStyleButtons();
        UpdateSelectionModeUi();
        _adjustmentBadgeTimer.Tick += (_, _) =>
        {
            _adjustmentBadgeTimer.Stop();
            AdjustmentBadge.Visibility = Visibility.Collapsed;
        };
        _ocrPrefetchTimer.Tick += OnOcrPrefetchTimerTick;
        QrCodeHotspotLayer.ResultInvoked += OnQrCodeHotspotInvoked;

        BackgroundImage.Source = snapshot.Image;
        CloseCaptureButton.Visibility = UsesCloseButton && _showActionToolbar
            ? Visibility.Visible
            : Visibility.Collapsed;
        QuickTagButton.Visibility = _showActionToolbar && _settings.ShowQuickCaptureTags
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(confirmationHint))
        {
            HintActionText.Text = $"R 循环历史选区  ·  {confirmationHint}";
        }
        else if (_showActionToolbar)
        {
            HintActionText.Text = UsesCloseButton
                ? "拖动框选多个区域 · Tab 切换 · Delete 删除 · ENTER 输出 · ESC / × 关闭"
                : "拖动框选多个区域 · Tab 切换 · Delete 删除 · ENTER 输出 · ESC 退出";
        }
        else if (UsesCloseButton)
        {
            HintActionText.Text = "R 循环历史选区  ·  双击 / ENTER 复制  ·  ESC / × 关闭";
        }
        _cornerMarks =
        [
            TopLeftHorizontal,
            TopLeftVertical,
            TopRightHorizontal,
            TopRightVertical,
            BottomLeftHorizontal,
            BottomLeftVertical,
            BottomRightHorizontal,
            BottomRightVertical
        ];

        foreach (var mark in _cornerMarks)
        {
            mark.Visibility = Visibility.Collapsed;
        }

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _adjustmentBadgeTimer.Stop();
            CancelOcrPrefetch();
            CancelQrCodeRecognition(clearResults: false);
            ResourceDiagnostics.Sample("CaptureClosed");
        };
    }

    public event EventHandler<DrawingRectangle>? SelectionConfirmed;
    public event EventHandler? SelectionCancelled;
    public event EventHandler<CaptureOverlayActionEventArgs>? ActionRequested;
    public event EventHandler<PreviousSelectionRequestedEventArgs>? PreviousSelectionRequested;

    public IReadOnlyList<string> SelectedTags => _selectedQuickTags
        .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public void CloseAfterPinPresented()
    {
        if (IsVisible)
        {
            IsHitTestVisible = false;
        }

        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.ImmAssociateContextEx(handle, nint.Zero, 0);
        var bounds = _snapshot.Bounds;
        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HwndTopmost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InputMethod.SetIsInputMethodEnabled(this, false);
        Activate();
        Focus();
        Keyboard.Focus(this);
        ConfigureNativeIme(false);
        UpdateHintPosition();
        ShowFullShade();

        if (_initialSelectionSet is { HasSelection: true } initialSelectionSet)
        {
            LoadSelectionSet(initialSelectionSet, useSetPadding: true);
        }
        else if (_initialLocalRegion is { Width: > 0, Height: > 0 } initialRegion)
        {
            LoadLocalSelection(initialRegion);
        }
    }

    private void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject focusTarget)
        {
            var enableIme = focusTarget is WpfTextBox;
            InputMethod.SetIsInputMethodEnabled(focusTarget, enableIme);
            ConfigureNativeIme(enableIme);
            Dispatcher.BeginInvoke(
                () => ConfigureNativeIme(Keyboard.FocusedElement is WpfTextBox),
                DispatcherPriority.Input);
        }
    }

    private void ConfigureNativeIme(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero)
        {
            NativeMethods.ImmAssociateContextEx(
                handle,
                nint.Zero,
                enabled ? NativeMethods.IaceDefault : 0);
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (QuickTagPopup.IsOpen)
        {
            QuickTagPopup.IsOpen = false;
        }

        if (CaptureToolbar.IsMouseOver || MatrixPanel.IsMouseOver)
        {
            return;
        }

        var point = ClampPoint(e.GetPosition(Surface));
        var modifiers = Keyboard.Modifiers;
        var captureHit = HitTest(point);
        var isCaptureResizeHandle = captureHit is not (
            DragMode.None or DragMode.Create or DragMode.Move);
        var annotationAvailable = _showActionToolbar &&
                                  _selectionMode == SelectionMode.Free &&
                                  _selections.Count == 1;
        if (annotationAvailable &&
            _annotationController.ActiveTool != CaptureAnnotationTool.None &&
            _selection.Contains(point) &&
            !(_annotationController.ActiveTool == CaptureAnnotationTool.Select && isCaptureResizeHandle))
        {
            if (_annotationController.ActiveTool == CaptureAnnotationTool.Select &&
                !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
                e.ClickCount >= 2 &&
                _annotationController.SelectAt(point) &&
                BeginEditSelectedAnnotation())
            {
                e.Handled = true;
                return;
            }

            _annotationController.Begin(
                point,
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            _isAnnotating = _annotationController.IsDrawing;
            if (_isAnnotating)
            {
                Surface.CaptureMouse();
            }

            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2 && FindSelectionAt(point) >= 0)
        {
            if (_selectionMode == SelectionMode.Matrix)
            {
                e.Handled = true;
                return;
            }

            ConfirmSelection();
            e.Handled = true;
            return;
        }

        if (_selectionMode == SelectionMode.Matrix)
        {
            BeginMatrixDrag(point);
            e.Handled = true;
            return;
        }

        _dragStart = point;
        _selectionAtDragStart = _selection;
        _selectionPixelAtDragStart = GetLocalRegion();
        _dragMoved = false;
        _pendingSmartSelection = WpfRect.Empty;
        _dragCreatedSelection = false;

        var hitIndex = FindSelectionAt(point);
        // Ctrl keeps Alt free for the existing "resize from center" gesture.
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            BeginNewSelection(new WpfRect(point, point));
            _dragCreatedSelection = true;
            _dragMode = DragMode.Create;
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (hitIndex >= 0)
        {
            if (hitIndex != _activeSelectionIndex)
            {
                ActivateSelection(hitIndex);
            }

            _selectionAtDragStart = _selection;
            _selectionPixelAtDragStart = GetLocalRegion();
            _dragMode = HitTest(point);
            if (_dragMode == DragMode.Create)
            {
                BeginNewSelection(new WpfRect(point, point));
                _dragCreatedSelection = true;
            }

            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (!_smartPreview.IsEmpty && _smartPreview.Contains(point))
        {
            // Adopt the preview immediately so a normal click cannot collapse into a
            // one-pixel drag if Windows delivers a synthetic move between down/up.
            // A deliberate drag still switches back to free-form selection below.
            _pendingSmartSelection = _smartPreview;
            BeginNewSelection(_smartPreview);
            _dragCreatedSelection = true;
            _dragMode = DragMode.Create;
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        BeginNewSelection(new WpfRect(point, point));
        _dragCreatedSelection = true;
        _dragMode = DragMode.Create;
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pointer = ClampPoint(e.GetPosition(Surface));
        UpdateMagnifier(pointer);
        if (_selectionMode == SelectionMode.Matrix)
        {
            if (CaptureToolbar.IsMouseOver || MatrixPanel.IsMouseOver)
            {
                ClearSmartPreview();
                Surface.Cursor = WpfCursors.Arrow;
                return;
            }

            if (_dragMode != DragMode.None)
            {
                ApplyMatrixDrag(pointer);
                e.Handled = true;
                return;
            }

            ClearSmartPreview();
            Surface.Cursor = _matrix is null ? WpfCursors.Cross : CursorFor(HitTest(pointer));
            return;
        }

        if (_dragMode == DragMode.None &&
            !CaptureToolbar.IsMouseOver &&
            !QuickTagPopup.IsOpen &&
            !SelectionListPopup.IsOpen &&
            _settings.SmartWindowSelection &&
            _annotationController.ActiveTool == CaptureAnnotationTool.None &&
            !_isAnnotating)
        {
            if (FindSelectionAt(pointer) < 0)
            {
                UpdateSmartPreview(pointer);
            }
            else
            {
                ClearSmartPreview();
            }
        }

        if (_isAnnotating)
        {
            _annotationController.Update(ClampPoint(e.GetPosition(Surface)));
            Surface.Cursor = _annotationController.ActiveTool == CaptureAnnotationTool.Select
                ? _annotationController.GetSelectionCursorAt(pointer)
                : CursorForAnnotationTool(_annotationController.ActiveTool);
            e.Handled = true;
            return;
        }

        if (CaptureToolbar.IsMouseOver && _dragMode == DragMode.None)
        {
            ClearSmartPreview();
            Surface.Cursor = WpfCursors.Arrow;
            return;
        }

        var point = pointer;
        if (_dragMode == DragMode.None)
        {
            Surface.Cursor = CursorForPointer(point);
            return;
        }

        if (_dragMode == DragMode.Create && !_pendingSmartSelection.IsEmpty)
        {
            if (e.LeftButton != MouseButtonState.Pressed || (point - _dragStart).Length < 8)
            {
                return;
            }

            _dragMoved = true;
            _pendingSmartSelection = WpfRect.Empty;
            SetSelection(new WpfRect(_dragStart, _dragStart));
        }

        ApplyDrag(point);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectionMode == SelectionMode.Matrix &&
            HandleMatrixMouseUp(e, ClampPoint(e.GetPosition(Surface))))
        {
            e.Handled = true;
            return;
        }

        if (_isAnnotating)
        {
            _annotationController.End(ClampPoint(e.GetPosition(Surface)));
            _isAnnotating = false;
            Surface.ReleaseMouseCapture();
            Surface.Cursor = CursorForPointer(ClampPoint(e.GetPosition(Surface)));
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.None)
        {
            return;
        }

        if (!_pendingSmartSelection.IsEmpty && !_dragMoved)
        {
            _pendingSmartSelection = WpfRect.Empty;
            _selectionPixelAtDragStart = null;
            _dragMode = DragMode.None;
            _dragCreatedSelection = false;
            Surface.ReleaseMouseCapture();
            UpdateSelectionVisuals();
            UpdateSizeBadge(_selection);
            UpdateToolbarPosition(_selection);
            UpdateMultiSelectionUi();
            Surface.Cursor = CursorFor(HitTest(ClampPoint(e.GetPosition(Surface))));
            e.Handled = true;
            return;
        }

        ApplyDrag(ClampPoint(e.GetPosition(Surface)));
        _pendingSmartSelection = WpfRect.Empty;
        _selectionPixelAtDragStart = null;
        _dragMode = DragMode.None;
        _dragCreatedSelection = false;
        Surface.ReleaseMouseCapture();

        if (ActiveSelection is { } active &&
            (active.Rect.Width < MinimumSelectionSize ||
             active.Rect.Height < MinimumSelectionSize))
        {
            RemoveActiveSelection();
        }
        else if (ActiveSelection is not null)
        {
            UpdateSelectionVisuals();
            UpdateSizeBadge(_selection);
            UpdateToolbarPosition(_selection);
            UpdateMultiSelectionUi();
        }

        if (_selections.Count == 0)
        {
            ShowFullShade();
        }

        Surface.Cursor = CursorFor(HitTest(ClampPoint(e.GetPosition(Surface))));
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is WpfTextBox)
        {
            return;
        }

        if (QuickTagPopup.IsOpen && ResolveShortcutKey(e) == Key.Escape)
        {
            QuickTagPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        var key = ResolveShortcutKey(e);
        var modifiers = Keyboard.Modifiers;
        if (_showActionToolbar && key == Key.M && modifiers == ModifierKeys.None)
        {
            SetSelectionMode(_selectionMode == SelectionMode.Free
                ? SelectionMode.Matrix
                : SelectionMode.Free);
            e.Handled = true;
            return;
        }

        if (_showActionToolbar &&
            _selectionMode == SelectionMode.Matrix &&
            _matrix is not null &&
            key is Key.Left or Key.Right or Key.Up or Key.Down &&
            modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var (offsetX, offsetY) = key switch
            {
                Key.Left => (-1, 0),
                Key.Right => (1, 0),
                Key.Up => (0, -1),
                _ => (0, 1)
            };
            var pixelDistance = modifiers == ModifierKeys.Shift ? 10 : 1;
            NudgeMatrix(offsetX * pixelDistance, offsetY * pixelDistance);
            e.Handled = true;
            return;
        }

        if (_showActionToolbar &&
            _selections.Count > 1 &&
            key is Key.Left or Key.Right or Key.Up or Key.Down &&
            modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var (offsetX, offsetY) = key switch
            {
                Key.Left => (-1, 0),
                Key.Right => (1, 0),
                Key.Up => (0, -1),
                _ => (0, 1)
            };
            var pixelDistance = modifiers == ModifierKeys.Shift ? 10 : 1;
            NudgeActiveSelection(offsetX * pixelDistance, offsetY * pixelDistance);
            e.Handled = true;
            return;
        }

        if (key is Key.W or Key.A or Key.S or Key.D &&
            modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var offset = key switch
            {
                Key.W => (X: 0, Y: -1),
                Key.A => (X: -1, Y: 0),
                Key.S => (X: 0, Y: 1),
                Key.D => (X: 1, Y: 0),
                _ => (X: 0, Y: 0)
            };
            if (_showActionToolbar &&
                _annotationController.ActiveTool == CaptureAnnotationTool.Select &&
                _annotationController.HasSelection)
            {
                var pixelDistance = modifiers == ModifierKeys.Shift ? 10 : 1;
                var deltaX = ScreenPixelMovement.ToDip(
                    offset.X * pixelDistance,
                    Surface.ActualWidth,
                    _snapshot.Bounds.Width);
                var deltaY = ScreenPixelMovement.ToDip(
                    offset.Y * pixelDistance,
                    Surface.ActualHeight,
                    _snapshot.Bounds.Height);
                _annotationController.NudgeSelection(deltaX, deltaY);
                e.Handled = true;
                return;
            }

            if (_showActionToolbar &&
                _selectionMode == SelectionMode.Matrix &&
                _matrix is not null)
            {
                var pixelDistance = modifiers == ModifierKeys.Shift ? 10 : 1;
                NudgeMatrix(offset.X * pixelDistance, offset.Y * pixelDistance);
                e.Handled = true;
                return;
            }

            if (_showActionToolbar && _selections.Count > 1)
            {
                var pixelDistance = modifiers == ModifierKeys.Shift ? 10 : 1;
                NudgeActiveSelection(offset.X * pixelDistance, offset.Y * pixelDistance);
                e.Handled = true;
                return;
            }

            if (_lockedSelectionSize is not null && !_selection.IsEmpty)
            {
                var pixelDistance = modifiers == ModifierKeys.Shift ? 10 : 1;
                NudgeFixedSelection(offset.X * pixelDistance, offset.Y * pixelDistance);
                e.Handled = true;
                return;
            }

            if (modifiers == ModifierKeys.None)
            {
                NudgeCrosshair(offset.X, offset.Y);
                e.Handled = true;
                return;
            }
        }

        if (_showActionToolbar &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            ((key == Key.Y && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ||
             (key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))))
        {
            _annotationController.Redo();
            e.Handled = true;
            return;
        }

        if (_showActionToolbar &&
            key == Key.Z &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _annotationController.Undo();
            e.Handled = true;
            return;
        }

        if (_showActionToolbar &&
            key == Key.F2 &&
            Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_annotationController.ActiveTool == CaptureAnnotationTool.Select)
            {
                BeginEditSelectedAnnotation();
            }
            else
            {
                ActivateAnnotationTool(CaptureAnnotationTool.Select, SelectButton, supportsColor: false);
            }

            e.Handled = true;
            return;
        }

        if (_showActionToolbar && _annotationController.ActiveTool == CaptureAnnotationTool.Select)
        {
            if (key == Key.Delete)
            {
                _annotationController.DeleteSelected();
                e.Handled = true;
                return;
            }

            if (key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _annotationController.CopySelected();
                e.Handled = true;
                return;
            }

            if (key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                _annotationController.PasteSelected();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
                key is Key.OemOpenBrackets or Key.Oem6)
            {
                var toFrontOrBack = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                var moved = key == Key.Oem6
                    ? toFrontOrBack
                        ? _annotationController.BringSelectedToFront()
                        : _annotationController.BringSelectedForward()
                    : toFrontOrBack
                        ? _annotationController.SendSelectedToBack()
                        : _annotationController.SendSelectedBackward();
                if (moved)
                {
                    ShowAdjustmentBadge(Mouse.GetPosition(Root),
                        key == Key.Oem6 ? "图层已上移" : "图层已下移");
                }
                e.Handled = true;
                return;
            }
        }

        if (_showActionToolbar &&
            _selectionMode == SelectionMode.Free &&
            _selections.Count > 1 &&
            GeometryEditor.Visibility != Visibility.Visible &&
            key == Key.Tab)
        {
            CycleActiveSelection(
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (_showActionToolbar &&
            _selectionMode == SelectionMode.Matrix &&
            key is Key.Delete or Key.Back)
        {
            ClearMatrix();
            ShowFullShade();
            e.Handled = true;
            return;
        }

        if (_showActionToolbar &&
            _selections.Count > 0 &&
            key is Key.Delete or Key.Back)
        {
            RemoveActiveSelection();
            e.Handled = true;
            return;
        }

        if (key == Key.R && Keyboard.Modifiers == ModifierKeys.None)
        {
            RecallPreviousSelection();
            e.Handled = true;
            return;
        }

        if (key == Key.I && Keyboard.Modifiers == ModifierKeys.None && _settings.ShowMagnifier)
        {
            var colorText = $"#{_currentPixelColor.R:X2}{_currentPixelColor.G:X2}{_currentPixelColor.B:X2}";
            _ = CopyColorAsync(colorText);

            e.Handled = true;
            return;
        }

        if (_showActionToolbar && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (key == Key.O)
            {
                RequestAction(CaptureOverlayAction.Ocr);
                e.Handled = true;
                return;
            }

            if (key == Key.C)
            {
                RequestAction(CaptureOverlayAction.Copy);
                e.Handled = true;
                return;
            }

            if (key == Key.S)
            {
                RequestAction(CaptureOverlayAction.Save);
                e.Handled = true;
                return;
            }
        }

        if (key == Key.Escape)
        {
            if (_dragMode != DragMode.None)
            {
                CancelActiveDrag();
                e.Handled = true;
                return;
            }

            if (_annotationController.ActiveTool != CaptureAnnotationTool.None)
            {
                SetAnnotationTool(CaptureAnnotationTool.None);
                e.Handled = true;
                return;
            }

            CancelSelection();
            e.Handled = true;
            return;
        }

        if (key == Key.Enter && _selections.Count > 0)
        {
            ConfirmSelection();
            e.Handled = true;
        }
    }

    private async Task CopyColorAsync(string colorText)
    {
        try
        {
            if (_clipboardService is not null)
            {
                await _clipboardService.CopyTextAsync(colorText);
            }
            else
            {
                ClipboardService.CopyTextWithRetry(colorText);
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("Clipboard", $"Color copy failed: {exception.Message}");
        }
    }

    private void NudgeCrosshair(int offsetX, int offsetY)
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var x = Math.Clamp(
            cursor.X + offsetX,
            _snapshot.Bounds.Left,
            _snapshot.Bounds.Right - 1);
        var y = Math.Clamp(
            cursor.Y + offsetY,
            _snapshot.Bounds.Top,
            _snapshot.Bounds.Bottom - 1);
        NativeMethods.SetCursorPos(x, y);
    }

    private void NudgeFixedSelection(int offsetX, int offsetY)
    {
        if (GetLocalRegion() is not { } current)
        {
            return;
        }

        var moved = CaptureFixedSizeConstraint.Move(
            current,
            offsetX,
            offsetY,
            new DrawingRectangle(0, 0, _snapshot.Bounds.Width, _snapshot.Bounds.Height));
        SetSelectionFromPixels(moved);
    }

    private static Key ResolveShortcutKey(System.Windows.Input.KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // The window-level right-click gesture cancels capture, but toolbar controls
        // must receive the gesture first so their own context menus can open.
        if (CaptureToolbar.IsMouseOver || MatrixPanel.IsMouseOver)
        {
            return;
        }

        if (_annotationController.ActiveTool == CaptureAnnotationTool.Select)
        {
            var point = ClampPoint(e.GetPosition(Surface));
            if (_annotationController.SelectAt(point, preserveExistingSelection: true) &&
                FindResource("AnnotationContextMenu") is ContextMenu menu)
            {
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    item.IsEnabled = item.Tag?.ToString() switch
                    {
                        "EditText" => _annotationController.CanEditSelectedText,
                        "EditNumber" => _annotationController.CanEditSelectedNumber,
                        _ => true
                    };
                }
                menu.PlacementTarget = Surface;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
                e.Handled = true;
                return;
            }
        }

        if (_annotationController.ActiveTool != CaptureAnnotationTool.None)
        {
            SetAnnotationTool(CaptureAnnotationTool.None);
            e.Handled = true;
            return;
        }

        if (_selectionMode == SelectionMode.Matrix)
        {
            if (_matrix is not null &&
                _selection.Contains(ClampPoint(e.GetPosition(Surface))))
            {
                ShowMatrixContextMenu();
            }
            else
            {
                CancelSelection();
            }

            e.Handled = true;
            return;
        }

        var selectionPoint = ClampPoint(e.GetPosition(Surface));
        var selectionIndex = FindSelectionAt(selectionPoint);
        if (selectionIndex >= 0)
        {
            ActivateSelection(selectionIndex);
            ShowSelectionContextMenu();
            e.Handled = true;
            return;
        }

        CancelSelection();
        e.Handled = true;
    }

    private void ShowSelectionContextMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = Surface,
            Placement = PlacementMode.MousePoint
        };
        var delete = new MenuItem { Header = $"删除选区 #{_activeSelectionIndex + 1}" };
        delete.Click += (_, _) => RemoveActiveSelection();
        var duplicate = new MenuItem { Header = "复制选区" };
        duplicate.Click += (_, _) => DuplicateActiveSelection();
        var next = new MenuItem { Header = "下一个选区" };
        next.Click += (_, _) => CycleActiveSelection(1);
        menu.Items.Add(delete);
        menu.Items.Add(duplicate);
        menu.Items.Add(new Separator());
        menu.Items.Add(next);
        menu.IsOpen = true;
    }

    private void DuplicateActiveSelection()
    {
        if (ActiveSelection is not { } item)
        {
            return;
        }

        var moved = CaptureFixedSizeConstraint.Move(item.Pixels, 10, 10, CapturePixelBounds);
        BeginNewSelection(PixelsToDip(moved));
    }

    private void RecallPreviousSelection()
    {
        var nextIndex = CaptureRegionHistory.NextIndex(_recallIndex, _recallLocalRegions.Count);
        if (nextIndex < 0)
        {
            PreviousSelectionRequested?.Invoke(this, new PreviousSelectionRequestedEventArgs(-1));
            return;
        }

        _recallIndex = nextIndex;
        if (_recallLocalRegions[nextIndex] is not { Width: > 0, Height: > 0 } previousRegion)
        {
            PreviousSelectionRequested?.Invoke(this, new PreviousSelectionRequestedEventArgs(nextIndex));
            return;
        }

        SetAnnotationTool(CaptureAnnotationTool.None);
        _annotationController.Reset();
        LoadLocalSelection(previousRegion);
        ShowAdjustmentBadge(Mouse.GetPosition(Root), $"历史选区 {nextIndex + 1}/{_recallLocalRegions.Count}");
    }

    private void LoadLocalSelection(DrawingRectangle region)
    {
        _lockedSelectionSize = null;
        UpdateFixedSizeUi();
        SetAspectRatioMode(CaptureAspectRatioMode.Free, adjustSelection: false);
        _selectionPaddingOverride = null;
        LoadSelectionSet(SelectionSet.Single(CaptureRegion.FromRectangle(region)));
    }

    internal void LoadSelectionSet(SelectionSet set, bool useSetPadding = false)
    {
        if (useSetPadding)
        {
            _selectionPaddingOverride = Math.Clamp(set.Padding, 0, 200);
        }

        _selectionMode = SelectionMode.Free;
        _matrix = null;
        _matrixAtDragStart = null;
        _matrixDragCell = null;
        MatrixLayer.Children.Clear();
        MatrixPanel.Visibility = Visibility.Collapsed;
        ClearSelections();
        _annotationController.Reset();

        if (set.Mode == SelectionMode.Matrix && set.Matrix is { IsValid: true } spec)
        {
            var matrixBounds = ClipToScreen(spec.Bounds.ToRectangle());
            if (matrixBounds.Width > 1 && matrixBounds.Height > 1)
            {
                _selectionMode = SelectionMode.Matrix;
                _matrix = new MatrixState
                {
                    Bounds = matrixBounds,
                    Rows = spec.Rows,
                    Columns = spec.Columns,
                    GapX = spec.GapX,
                    GapY = spec.GapY
                };
                if (spec.ExcludedCells is { Count: > 0 } excluded)
                {
                    _matrix.Excluded.UnionWith(excluded.Where(cell =>
                        cell.Row >= 1 && cell.Column >= 1 &&
                        cell.Row <= spec.Rows && cell.Column <= spec.Columns));
                }

                NormalizeMatrix();
                if (_matrix is not null)
                {
                    RebuildMatrixCells();
                    WriteMatrixFields();
                    UpdateSelectionModeUi();
                    UpdateMultiSelectionUi();
                    HintBadge.Visibility = Visibility.Collapsed;
                    return;
                }
            }
        }

        foreach (var region in set.Regions.Where(region => region.IsValid))
        {
            var clipped = ClipToScreen(region.ToRectangle());
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                continue;
            }

            _selections.Add(CreateSelectionItem(PixelsToDip(clipped), clipped));
        }

        UpdateSelectionModeUi();
        if (_selections.Count == 0)
        {
            ShowFullShade();
            return;
        }

        _activeSelectionIndex = 0;
        var active = _selections[0];
        _selection = active.Rect;
        _selectionPixelRegion = active.Pixels;
        SetSelectionCore(active.Rect, active.Pixels);
        UpdateMultiSelectionUi();
    }

    private DrawingRectangle ClipToScreen(DrawingRectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            return DrawingRectangle.Empty;
        }

        var left = Math.Clamp(region.Left, 0, Math.Max(0, _snapshot.Bounds.Width - 1));
        var top = Math.Clamp(region.Top, 0, Math.Max(0, _snapshot.Bounds.Height - 1));
        var right = Math.Clamp(region.Right, left + 1, _snapshot.Bounds.Width);
        var bottom = Math.Clamp(region.Bottom, top + 1, _snapshot.Bounds.Height);
        return right > left && bottom > top
            ? new DrawingRectangle(left, top, right - left, bottom - top)
            : DrawingRectangle.Empty;
    }

    private WpfRect PixelsToDip(DrawingRectangle pixels)
    {
        var toDipX = Surface.ActualWidth / Math.Max(1, _snapshot.Bounds.Width);
        var toDipY = Surface.ActualHeight / Math.Max(1, _snapshot.Bounds.Height);
        return new WpfRect(
            pixels.X * toDipX,
            pixels.Y * toDipY,
            pixels.Width * toDipX,
            pixels.Height * toDipY);
    }

    private void ApplyDrag(Point point)
    {
        switch (_dragMode)
        {
            case DragMode.Create:
                if (_lockedSelectionSize is not null)
                {
                    return;
                }

                if (_lockedAspectRatio is { } createRatio)
                {
                    SetSelectionFromPixels(CaptureAspectRatioConstraint.Create(
                        ToPhysicalPoint(_dragStart),
                        ToPhysicalPoint(point),
                        createRatio,
                        _snapshot.Bounds.Width,
                        _snapshot.Bounds.Height));
                }
                else
                {
                    SetSelection(new WpfRect(_dragStart, point));
                }
                return;
            case DragMode.Move:
                MoveSelection(point);
                return;
            case DragMode.None:
                return;
        }

        ResizeSelection(point);
    }

    private void MoveSelection(Point point)
    {
        if (_selectionPixelAtDragStart is not { Width: > 0, Height: > 0 } pixelStart)
        {
            return;
        }

        // 以物理像素为单位平移：DIP 坐标按浮点平移后，DIP→像素的 floor/ceil 取整会随
        // 位置变化来回切换，导致移动过程中选区宽高出现 1 像素抖动。
        var physicalStart = ToPhysicalPoint(_dragStart);
        var physicalCurrent = ToPhysicalPoint(point);
        SetSelectionFromPixels(CaptureFixedSizeConstraint.Move(
            pixelStart,
            physicalCurrent.X - physicalStart.X,
            physicalCurrent.Y - physicalStart.Y,
            new DrawingRectangle(0, 0, _snapshot.Bounds.Width, _snapshot.Bounds.Height)));
    }

    private void ResizeSelection(Point point)
    {
        if (_lockedSelectionSize is not null)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var aspectRatio = _lockedAspectRatio;
        if (aspectRatio is null &&
            IsCornerDrag(_dragMode) &&
            modifiers.HasFlag(ModifierKeys.Shift) &&
            _selectionPixelAtDragStart is { Width: > 0, Height: > 0 } temporarySource)
        {
            aspectRatio = (double)temporarySource.Width / temporarySource.Height;
        }

        if (_selectionPixelAtDragStart is { Width: > 0, Height: > 0 } pixelStart &&
            TryMapResizeHandle(_dragMode, out var resizeHandle) &&
            IsCornerDrag(_dragMode) &&
            modifiers.HasFlag(ModifierKeys.Alt))
        {
            var centered = CaptureAspectRatioConstraint.ResizeFromCenter(
                pixelStart,
                ToPhysicalPoint(point),
                resizeHandle,
                aspectRatio,
                new DrawingRectangle(0, 0, _snapshot.Bounds.Width, _snapshot.Bounds.Height));
            SetSelectionFromPixels(centered);
            return;
        }

        if (aspectRatio is { } ratio &&
            _selectionPixelAtDragStart is { Width: > 0, Height: > 0 } constrainedStart &&
            TryMapResizeHandle(_dragMode, out var constrainedHandle))
        {
            var constrained = CaptureAspectRatioConstraint.Resize(
                constrainedStart,
                ToPhysicalPoint(point),
                constrainedHandle,
                ratio,
                _snapshot.Bounds.Width,
                _snapshot.Bounds.Height);
            SetSelectionFromPixels(constrained);
            return;
        }

        var left = _selectionAtDragStart.Left;
        var top = _selectionAtDragStart.Top;
        var right = _selectionAtDragStart.Right;
        var bottom = _selectionAtDragStart.Bottom;

        if (_dragMode is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft)
        {
            left = Math.Clamp(point.X, 0, right - MinimumSelectionSize);
        }

        if (_dragMode is DragMode.Right or DragMode.TopRight or DragMode.BottomRight)
        {
            right = Math.Clamp(point.X, left + MinimumSelectionSize, Surface.ActualWidth);
        }

        if (_dragMode is DragMode.Top or DragMode.TopLeft or DragMode.TopRight)
        {
            top = Math.Clamp(point.Y, 0, bottom - MinimumSelectionSize);
        }

        if (_dragMode is DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight)
        {
            bottom = Math.Clamp(point.Y, top + MinimumSelectionSize, Surface.ActualHeight);
        }

        SetSelection(new WpfRect(left, top, right - left, bottom - top));
    }

    private DragMode HitTest(Point point) => HitTestRect(_selection, point);

    private DragMode HitTestRect(WpfRect rect, Point point)
    {
        if (rect.IsEmpty)
        {
            return DragMode.Create;
        }

        if (_lockedSelectionSize is not null)
        {
            var moveBounds = rect;
            moveBounds.Inflate(HitPadding, HitPadding);
            return moveBounds.Contains(point) ? DragMode.Move : DragMode.None;
        }

        var nearLeft = Math.Abs(point.X - rect.Left) <= HitPadding;
        var nearRight = Math.Abs(point.X - rect.Right) <= HitPadding;
        var nearTop = Math.Abs(point.Y - rect.Top) <= HitPadding;
        var nearBottom = Math.Abs(point.Y - rect.Bottom) <= HitPadding;
        var withinX = point.X >= rect.Left - HitPadding && point.X <= rect.Right + HitPadding;
        var withinY = point.Y >= rect.Top - HitPadding && point.Y <= rect.Bottom + HitPadding;

        if (nearLeft && nearTop)
        {
            return DragMode.TopLeft;
        }

        if (nearRight && nearTop)
        {
            return DragMode.TopRight;
        }

        if (nearLeft && nearBottom)
        {
            return DragMode.BottomLeft;
        }

        if (nearRight && nearBottom)
        {
            return DragMode.BottomRight;
        }

        if (nearLeft && withinY)
        {
            return DragMode.Left;
        }

        if (nearRight && withinY)
        {
            return DragMode.Right;
        }

        if (nearTop && withinX)
        {
            return DragMode.Top;
        }

        if (nearBottom && withinX)
        {
            return DragMode.Bottom;
        }

        return rect.Contains(point) ? DragMode.Move : DragMode.Create;
    }

    private void SetSelection(WpfRect rect) => SetSelectionCore(rect, ToPhysicalRectangle(rect));

    private void SetSelectionFromPixels(DrawingRectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            SetSelection(new WpfRect(_dragStart, _dragStart));
            return;
        }

        var left = Math.Clamp(rect.Left, 0, _snapshot.Bounds.Width - 1);
        var top = Math.Clamp(rect.Top, 0, _snapshot.Bounds.Height - 1);
        var right = Math.Clamp(rect.Right, left + 1, _snapshot.Bounds.Width);
        var bottom = Math.Clamp(rect.Bottom, top + 1, _snapshot.Bounds.Height);
        var pixels = new DrawingRectangle(left, top, right - left, bottom - top);
        var toDipX = Surface.ActualWidth / Math.Max(1, _snapshot.Bounds.Width);
        var toDipY = Surface.ActualHeight / Math.Max(1, _snapshot.Bounds.Height);
        SetSelectionCore(
            new WpfRect(
                pixels.X * toDipX,
                pixels.Y * toDipY,
                pixels.Width * toDipX,
                pixels.Height * toDipY),
            pixels);
    }

    private void SetSelectionCore(WpfRect rect, DrawingRectangle pixelRegion)
    {
        CancelQrCodeRecognition(clearResults: true);
        _smartPreview = WpfRect.Empty;
        SmartPreviewBorder.Visibility = Visibility.Collapsed;

        var item = ActiveSelection;
        if (item is null)
        {
            item = CreateSelectionItem(rect, pixelRegion);
            _selections.Add(item);
            _activeSelectionIndex = _selections.Count - 1;
        }
        else
        {
            item.Rect = rect;
            item.Pixels = pixelRegion;
        }

        _selection = rect;
        _selectionPixelRegion = pixelRegion;

        var single = _selectionMode == SelectionMode.Free && _selections.Count == 1;
        _annotationController.SetBounds(
            single ? rect : WpfRect.Empty,
            Surface.ActualWidth,
            Surface.ActualHeight);

        SelectionBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, rect.Left);
        Canvas.SetTop(SelectionBorder, rect.Top);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;

        UpdateSelectionVisuals();
        UpdateCornerMarks(rect);
        UpdateSizeBadge(rect);
        UpdateToolbarPosition(rect);
        UpdateMultiSelectionUi();
        HintBadge.Visibility = Visibility.Collapsed;
        if (single)
        {
            InvalidateOcrPrefetch();
        }
        else
        {
            CancelOcrPrefetch();
        }
    }

    private SelectionItem CreateSelectionItem(WpfRect rect, DrawingRectangle pixelRegion)
    {
        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0xB3, 0x76, 0xDF, 0xEE)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(MediaColor.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(1),
            IsHitTestVisible = false
        };
        var badgeText = new TextBlock
        {
            FontFamily = FindResource("DataFont") as System.Windows.Media.FontFamily ?? new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0x76, 0xDF, 0xEE))
        };
        var badge = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromArgb(0xEE, 0x11, 0x1A, 0x22)),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x66, 0x76, 0xDF, 0xEE)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 4, 1),
            Child = badgeText,
            IsHitTestVisible = false
        };
        SelectionLayer.Children.Add(frame);
        SelectionLayer.Children.Add(badge);
        System.Windows.Controls.Panel.SetZIndex(frame, 10);
        System.Windows.Controls.Panel.SetZIndex(badge, 20);
        return new SelectionItem
        {
            Rect = rect,
            Pixels = pixelRegion,
            Frame = frame,
            Badge = badge,
            BadgeText = badgeText
        };
    }

    private void BeginNewSelection(WpfRect rect)
    {
        if (!_showActionToolbar)
        {
            ClearSelections();
        }

        var pixels = ToPhysicalRectangle(rect);
        var item = CreateSelectionItem(rect, pixels);
        _selections.Add(item);
        _activeSelectionIndex = _selections.Count - 1;
        _selection = rect;
        _selectionPixelRegion = pixels;
        SetSelectionCore(rect, pixels);
        UpdateMultiSelectionUi();
    }

    private void ActivateSelection(int index)
    {
        if (index < 0 || index >= _selections.Count)
        {
            return;
        }

        _activeSelectionIndex = index;
        var item = _selections[index];
        SetSelectionCore(item.Rect, item.Pixels);
    }

    private void RemoveActiveSelection()
    {
        if (ActiveSelection is not { } item)
        {
            return;
        }

        var removedIndex = _activeSelectionIndex;
        SelectionLayer.Children.Remove(item.Frame);
        SelectionLayer.Children.Remove(item.Badge);
        _selections.RemoveAt(removedIndex);

        if (_selections.Count == 0)
        {
            ShowFullShade();
            return;
        }

        _activeSelectionIndex = Math.Clamp(removedIndex, 0, _selections.Count - 1);
        var next = _selections[_activeSelectionIndex];
        _selection = next.Rect;
        _selectionPixelRegion = next.Pixels;
        SetSelectionCore(next.Rect, next.Pixels);
        UpdateMultiSelectionUi();
    }

    private void ClearSelections()
    {
        foreach (var item in _selections)
        {
            SelectionLayer.Children.Remove(item.Frame);
            SelectionLayer.Children.Remove(item.Badge);
        }

        _selections.Clear();
        _matrixCellOrder.Clear();
        _activeSelectionIndex = -1;
        _selection = WpfRect.Empty;
        _selectionPixelRegion = null;
    }

    private void OnFreeModeClick(object sender, RoutedEventArgs e) =>
        SetSelectionMode(SelectionMode.Free);

    private void OnMatrixModeClick(object sender, RoutedEventArgs e) =>
        SetSelectionMode(SelectionMode.Matrix);

    private void SetSelectionMode(SelectionMode mode)
    {
        if (_selectionMode == mode)
        {
            return;
        }

        if (_dragMode != DragMode.None)
        {
            CancelActiveDrag();
        }

        if (mode == SelectionMode.Matrix)
        {
            EnterMatrixMode();
        }
        else
        {
            ExplodeMatrixToFree();
        }

        UpdateSelectionModeUi();
        UpdateMultiSelectionUi();
    }

    private void EnterMatrixMode()
    {
        var baseBounds = GetSelectionUnionPixels();
        var hadMultiple = _selections.Count > 1;
        var previousCount = _selections.Count;
        _selectionMode = SelectionMode.Matrix;
        _matrix = null;
        _matrixAtDragStart = null;
        _matrixDragCell = null;
        MatrixLayer.Children.Clear();
        QuickTagPopup.IsOpen = false;
        SelectionListPopup.IsOpen = false;
        ClearSmartPreview();
        CancelQrCodeRecognition(clearResults: true);
        SetAnnotationTool(CaptureAnnotationTool.None);
        _annotationController.SetBounds(WpfRect.Empty, Surface.ActualWidth, Surface.ActualHeight);
        ClearSelections();
        _lockedSelectionSize = null;
        _lockedAspectRatio = null;
        _aspectRatioMode = CaptureAspectRatioMode.Free;
        UpdateFixedSizeUi();
        UpdateAspectRatioButtons();

        if (baseBounds is not { Width: > 1, Height: > 1 } bounds)
        {
            MatrixPanel.Visibility = Visibility.Collapsed;
            ShowAdjustmentBadge(Mouse.GetPosition(Root), "矩阵模式：在画面上拖拽定义基准矩形");
            return;
        }

        _matrix = new MatrixState { Bounds = ClipToScreen(bounds) };
        NormalizeMatrix();
        RebuildMatrixCells();
        WriteMatrixFields();
        ShowAdjustmentBadge(
            Mouse.GetPosition(Root),
            hadMultiple
                ? $"矩阵模式：已用 {previousCount} 个选区的外框作为基准"
                : "矩阵模式：拖动基准框移动，拖边角缩放；右键单元格排除/恢复");
    }

    private void ExplodeMatrixToFree()
    {
        _selectionMode = SelectionMode.Free;
        _matrix = null;
        _matrixAtDragStart = null;
        _matrixDragCell = null;
        MatrixLayer.Children.Clear();
        MatrixPanel.Visibility = Visibility.Collapsed;
        foreach (var item in _selections)
        {
            item.Label = null;
        }

        _matrixCellOrder.Clear();
        if (_selections.Count == 0)
        {
            _annotationController.SetBounds(WpfRect.Empty, Surface.ActualWidth, Surface.ActualHeight);
            return;
        }

        _activeSelectionIndex = 0;
        var first = _selections[0];
        _selection = first.Rect;
        _selectionPixelRegion = first.Pixels;
        SetSelectionCore(first.Rect, first.Pixels);
        ShowAdjustmentBadge(Mouse.GetPosition(Root), $"已切换为自由模式：{_selections.Count} 个选区");
    }

    private DrawingRectangle? GetSelectionUnionPixels()
    {
        var valid = _selections
            .Select(item => item.Pixels)
            .Where(pixels => pixels.Width > 0 && pixels.Height > 0)
            .ToArray();
        if (valid.Length == 0)
        {
            return null;
        }

        var left = valid.Min(region => region.Left);
        var top = valid.Min(region => region.Top);
        var right = valid.Max(region => region.Right);
        var bottom = valid.Max(region => region.Bottom);
        return new DrawingRectangle(left, top, right - left, bottom - top);
    }

    private void ClearMatrix()
    {
        _matrix = null;
        _matrixAtDragStart = null;
        _matrixDragCell = null;
        MatrixLayer.Children.Clear();
        MatrixPanel.Visibility = Visibility.Collapsed;
        ClearSelections();
        _annotationController.SetBounds(WpfRect.Empty, Surface.ActualWidth, Surface.ActualHeight);
    }

    private void NormalizeMatrix()
    {
        if (_matrix is null)
        {
            return;
        }

        _matrix.Bounds = ClipToScreen(_matrix.Bounds);
        if (_matrix.Bounds.Width <= 0 || _matrix.Bounds.Height <= 0)
        {
            return;
        }

        var parameters = MatrixLayout.Normalize(
            CaptureRegion.FromRectangle(_matrix.Bounds),
            _matrix.Rows,
            _matrix.Columns,
            _matrix.GapX,
            _matrix.GapY);
        _matrix.Rows = parameters.Rows;
        _matrix.Columns = parameters.Columns;
        _matrix.GapX = parameters.GapX;
        _matrix.GapY = parameters.GapY;
        _matrix.Excluded.RemoveWhere(cell =>
            cell.Row < 1 || cell.Column < 1 ||
            cell.Row > _matrix.Rows || cell.Column > _matrix.Columns);
    }

    private void RebuildMatrixCells()
    {
        if (_matrix is null)
        {
            ClearSelections();
            MatrixLayer.Children.Clear();
            return;
        }

        var cells = MatrixLayout.CalculateCells(
            CaptureRegion.FromRectangle(_matrix.Bounds),
            _matrix.Rows,
            _matrix.Columns,
            _matrix.GapX,
            _matrix.GapY,
            _matrix.ExcludedCells);
        foreach (var item in _selections)
        {
            SelectionLayer.Children.Remove(item.Frame);
            SelectionLayer.Children.Remove(item.Badge);
        }

        _selections.Clear();
        _matrixCellOrder.Clear();
        foreach (var entry in cells)
        {
            var rect = PixelsToDip(entry.Region.ToRectangle());
            var item = CreateSelectionItem(rect, entry.Region.ToRectangle());
            item.Label = $"{entry.Cell.Row},{entry.Cell.Column}";
            _selections.Add(item);
            _matrixCellOrder.Add(entry.Cell);
        }

        _activeSelectionIndex = -1;
        UpdateMatrixUi();
    }

    private void UpdateMatrixUi()
    {
        if (_matrix is null)
        {
            return;
        }

        _selection = PixelsToDip(_matrix.Bounds);
        _selectionPixelRegion = _matrix.Bounds;
        _annotationController.SetBounds(WpfRect.Empty, Surface.ActualWidth, Surface.ActualHeight);
        SelectionBorder.Visibility = Visibility.Visible;
        SetPosition(SelectionBorder, _selection.Left, _selection.Top);
        SelectionBorder.Width = Math.Max(0, _selection.Width);
        SelectionBorder.Height = Math.Max(0, _selection.Height);
        HintBadge.Visibility = Visibility.Collapsed;
        UpdateSelectionVisuals();
        UpdateCornerMarks(_selection);
        UpdateMatrixSizeBadge();
        UpdateToolbarPosition(_selection);
        UpdateMatrixPanelPosition();
        UpdateMultiSelectionUi();
        CancelOcrPrefetch();
    }

    private void UpdateMatrixLayer()
    {
        MatrixLayer.Children.Clear();
        if (_selectionMode != SelectionMode.Matrix || _matrix is null || _selection.IsEmpty)
        {
            return;
        }

        var bounds = _selection;
        var allCells = MatrixLayout.CalculateCells(
            CaptureRegion.FromRectangle(_matrix.Bounds),
            _matrix.Rows,
            _matrix.Columns,
            _matrix.GapX,
            _matrix.GapY);
        var accent = new SolidColorBrush(MediaColor.FromArgb(0x88, 0x76, 0xDF, 0xEE));
        var rowsLabelBounds = bounds.Left >= 18;
        var columnsLabelBounds = bounds.Top >= 18;
        var labelFont = FindResource("DataFont") as System.Windows.Media.FontFamily
                        ?? new System.Windows.Media.FontFamily("Consolas");
        foreach (var entry in allCells)
        {
            var rect = PixelsToDip(entry.Region.ToRectangle());
            if (_matrix.Excluded.Contains(entry.Cell))
            {
                var overlay = new Rectangle
                {
                    Width = Math.Max(0, rect.Width),
                    Height = Math.Max(0, rect.Height),
                    Fill = new SolidColorBrush(MediaColor.FromArgb(0xD8, 0x0B, 0x11, 0x18)),
                    Stroke = new SolidColorBrush(MediaColor.FromArgb(0x99, 0xFF, 0x71, 0x80)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection([3, 2]),
                    IsHitTestVisible = false
                };
                SetPosition(overlay, rect.Left, rect.Top);
                MatrixLayer.Children.Add(overlay);
                if (rect.Width >= 18 && rect.Height >= 18)
                {
                    var cross = new TextBlock
                    {
                        Text = "×",
                        FontFamily = labelFont,
                        FontSize = 14,
                        Foreground = new SolidColorBrush(MediaColor.FromArgb(0xCC, 0xFF, 0x71, 0x80)),
                        IsHitTestVisible = false
                    };
                    SetPosition(cross, rect.Left + (rect.Width / 2) - 5, rect.Top + (rect.Height / 2) - 10);
                    MatrixLayer.Children.Add(cross);
                }
            }

            if (rowsLabelBounds && entry.Cell.Column == 1 && rect.Height >= 16)
            {
                var label = new TextBlock
                {
                    Text = entry.Cell.Row.ToString(),
                    FontFamily = labelFont,
                    FontSize = 10,
                    Foreground = accent,
                    IsHitTestVisible = false
                };
                SetPosition(label, bounds.Left - 16, rect.Top + 2);
                MatrixLayer.Children.Add(label);
            }

            if (columnsLabelBounds && entry.Cell.Row == 1 && rect.Width >= 16)
            {
                var label = new TextBlock
                {
                    Text = entry.Cell.Column.ToString(),
                    FontFamily = labelFont,
                    FontSize = 10,
                    Foreground = accent,
                    IsHitTestVisible = false
                };
                SetPosition(label, rect.Left + 2, bounds.Top - 16);
                MatrixLayer.Children.Add(label);
            }
        }

        var frame = new Rectangle
        {
            Width = Math.Max(0, bounds.Width),
            Height = Math.Max(0, bounds.Height),
            Stroke = accent,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection([4, 3]),
            Fill = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
        SetPosition(frame, bounds.Left, bounds.Top);
        MatrixLayer.Children.Add(frame);
    }

    private void UpdateMatrixSizeBadge()
    {
        if (_matrix is null)
        {
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var total = _matrix.Rows * _matrix.Columns;
        var included = _selections.Count;
        var firstCell = _selections.FirstOrDefault()?.Pixels;
        var cellText = firstCell is { Width: > 0, Height: > 0 } cell
            ? $"{cell.Width} × {cell.Height} px"
            : "—";
        SizeText.Text = $"矩阵 {_matrix.Rows}×{_matrix.Columns} · {included}/{total} 格";
        ToolbarSizeText.Text = $"矩阵 {_matrix.Rows}×{_matrix.Columns} · 单元格 {cellText}";
        MatrixSummaryText.Text = $"{_matrix.Rows} × {_matrix.Columns} · {included}/{total}";
        PositionSizeBadge(_selection);
    }

    private void PositionSizeBadge(WpfRect rect)
    {
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var badgeWidth = SizeBadge.DesiredSize.Width;
        var badgeHeight = SizeBadge.DesiredSize.Height;
        var left = Math.Min(rect.Right - badgeWidth, Surface.ActualWidth - badgeWidth - 8);
        left = Math.Max(8, left);
        var top = rect.Bottom + 9;
        if (top + badgeHeight > Surface.ActualHeight - 8)
        {
            top = Math.Max(8, rect.Bottom - badgeHeight - 9);
        }

        SetPosition(SizeBadge, left, top);
    }

    private void UpdateMatrixPanelPosition()
    {
        var visible = _showActionToolbar &&
                      _selectionMode == SelectionMode.Matrix &&
                      _matrix is not null &&
                      !_selection.IsEmpty;
        MatrixPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            return;
        }

        MatrixPanel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = MatrixPanel.DesiredSize.Width;
        var height = MatrixPanel.DesiredSize.Height;
        var left = Math.Clamp(
            _selection.Left,
            10,
            Math.Max(10, Surface.ActualWidth - width - 10));
        var toolbarTop = Canvas.GetTop(CaptureToolbar);
        var toolbarHeight = CaptureToolbar.ActualHeight > 0
            ? CaptureToolbar.ActualHeight
            : CaptureToolbar.DesiredSize.Height;
        var top = double.IsFinite(toolbarTop)
            ? toolbarTop + toolbarHeight + 6
            : _selection.Bottom + 10;
        if (top + height > Surface.ActualHeight - 10)
        {
            top = _selection.Top - height - 10;
        }

        if (top < 10)
        {
            top = _selection.Bottom + 10;
        }

        top = Math.Clamp(top, 10, Math.Max(10, Surface.ActualHeight - height - 10));
        SetPosition(MatrixPanel, left, top);
    }

    private void UpdateSelectionModeUi()
    {
        var free = _selectionMode == SelectionMode.Free;
        ApplyModeButton(FreeModeButton, free);
        ApplyModeButton(MatrixModeButton, !free);
        SavePresetButton.ToolTip = free
            ? "保存当前选区为命名选区"
            : "保存当前矩阵选区为命名选区";
    }

    private static void ApplyModeButton(WpfButton button, bool active)
    {
        button.Background = active
            ? new SolidColorBrush(MediaColor.FromRgb(29, 58, 68))
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = new SolidColorBrush(active
            ? MediaColor.FromRgb(118, 223, 238)
            : MediaColor.FromRgb(184, 200, 208));
    }

    private void BeginMatrixDrag(Point point)
    {
        _dragStart = point;
        _selectionAtDragStart = _selection;
        _selectionPixelAtDragStart = GetLocalRegion();
        _dragMoved = false;
        _pendingSmartSelection = WpfRect.Empty;
        _dragCreatedSelection = false;
        _matrixDragMoved = false;
        _matrixDragCell = null;
        _matrixAtDragStart = null;

        if (_matrix is null)
        {
            BeginNewSelection(new WpfRect(point, point));
            _dragCreatedSelection = true;
            _dragMode = DragMode.Create;
            Surface.CaptureMouse();
            return;
        }

        var hit = HitTestRect(_selection, point);
        if (hit == DragMode.Create)
        {
            ClearMatrix();
            BeginNewSelection(new WpfRect(point, point));
            _dragCreatedSelection = true;
            _dragMode = DragMode.Create;
            Surface.CaptureMouse();
            return;
        }

        _matrixAtDragStart = _matrix.Clone();
        _matrixDragCell = FindMatrixCellAt(point);
        _dragMode = hit;
        Surface.CaptureMouse();
    }

    private bool HandleMatrixMouseUp(MouseButtonEventArgs e, Point point)
    {
        if (_dragMode == DragMode.None)
        {
            return false;
        }

        if (_matrix is null)
        {
            ApplyDrag(point);
            _dragMode = DragMode.None;
            _dragCreatedSelection = false;
            _selectionPixelAtDragStart = null;
            Surface.ReleaseMouseCapture();
            if (ActiveSelection is not { } created ||
                created.Rect.Width < MinimumSelectionSize ||
                created.Rect.Height < MinimumSelectionSize)
            {
                ShowFullShade();
                return true;
            }

            AdoptActiveSelectionAsMatrix();
            return true;
        }

        ApplyMatrixDrag(point);
        var toggleCell = !_matrixDragMoved && e.ClickCount == 1
            ? _matrixDragCell
            : null;
        _dragMode = DragMode.None;
        _dragCreatedSelection = false;
        _selectionPixelAtDragStart = null;
        _matrixAtDragStart = null;
        _matrixDragCell = null;
        _matrixDragMoved = false;
        Surface.ReleaseMouseCapture();
        if (toggleCell is { } cell)
        {
            ToggleMatrixCell(cell);
        }
        else
        {
            UpdateMatrixUi();
        }

        Surface.Cursor = CursorFor(HitTest(point));
        return true;
    }

    private void ApplyMatrixDrag(Point point)
    {
        if (_matrix is null)
        {
            return;
        }

        var start = _matrixAtDragStart ?? _matrix;
        if (_dragMode == DragMode.Move)
        {
            var physicalStart = ToPhysicalPoint(_dragStart);
            var physicalCurrent = ToPhysicalPoint(point);
            var deltaX = physicalCurrent.X - physicalStart.X;
            var deltaY = physicalCurrent.Y - physicalStart.Y;
            if (deltaX == 0 && deltaY == 0)
            {
                return;
            }

            _matrixDragMoved = true;
            SetMatrixBounds(CaptureFixedSizeConstraint.Move(
                start.Bounds,
                deltaX,
                deltaY,
                CapturePixelBounds));
            return;
        }

        _matrixDragMoved = true;
        SetMatrixBounds(ResizeMatrixBounds(start.Bounds, ToPhysicalPoint(point)));
    }

    private DrawingRectangle ResizeMatrixBounds(
        DrawingRectangle start,
        System.Drawing.Point cursor)
    {
        var left = start.Left;
        var top = start.Top;
        var right = start.Right;
        var bottom = start.Bottom;
        if (_dragMode is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft)
        {
            left = Math.Clamp(cursor.X, 0, right - (int)MinimumSelectionSize);
        }

        if (_dragMode is DragMode.Right or DragMode.TopRight or DragMode.BottomRight)
        {
            right = Math.Clamp(cursor.X, left + (int)MinimumSelectionSize, CapturePixelBounds.Width);
        }

        if (_dragMode is DragMode.Top or DragMode.TopLeft or DragMode.TopRight)
        {
            top = Math.Clamp(cursor.Y, 0, bottom - (int)MinimumSelectionSize);
        }

        if (_dragMode is DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight)
        {
            bottom = Math.Clamp(cursor.Y, top + (int)MinimumSelectionSize, CapturePixelBounds.Height);
        }

        return new DrawingRectangle(left, top, right - left, bottom - top);
    }

    private void SetMatrixBounds(DrawingRectangle bounds)
    {
        if (_matrix is null)
        {
            return;
        }

        var clipped = ClipToScreen(bounds);
        if (clipped.Width <= 0 || clipped.Height <= 0)
        {
            return;
        }

        _matrix.Bounds = clipped;
        NormalizeMatrix();
        RebuildMatrixCells();
        WriteMatrixFields();
    }

    private void AdoptActiveSelectionAsMatrix()
    {
        if (ActiveSelection is not { } created)
        {
            return;
        }

        var bounds = created.Pixels;
        ClearSelections();
        _matrix = new MatrixState { Bounds = ClipToScreen(bounds) };
        NormalizeMatrix();
        RebuildMatrixCells();
        WriteMatrixFields();
        UpdateSelectionModeUi();
        ShowAdjustmentBadge(
            Mouse.GetPosition(Root),
            $"矩阵基准 {_matrix.Bounds.Width} × {_matrix.Bounds.Height} px · 可拖动或调整行列");
    }

    private MatrixCell? FindMatrixCellAt(Point point)
    {
        if (_matrix is null)
        {
            return null;
        }

        foreach (var entry in MatrixLayout.CalculateCells(
                     CaptureRegion.FromRectangle(_matrix.Bounds),
                     _matrix.Rows,
                     _matrix.Columns,
                     _matrix.GapX,
                     _matrix.GapY))
        {
            var rect = PixelsToDip(entry.Region.ToRectangle());
            if (rect.Contains(point))
            {
                return entry.Cell;
            }
        }

        return null;
    }

    private void ToggleMatrixCell(MatrixCell cell)
    {
        if (_matrix is null)
        {
            return;
        }

        if (_matrix.Excluded.Remove(cell))
        {
            RebuildMatrixCells();
            WriteMatrixFields();
            ShowAdjustmentBadge(
                Mouse.GetPosition(Root),
                $"已恢复第 {cell.Row} 行第 {cell.Column} 列");
            return;
        }

        if (_matrix.Excluded.Count + 1 >= _matrix.Rows * _matrix.Columns)
        {
            ShowAdjustmentBadge(Mouse.GetPosition(Root), "至少需要保留一个单元格");
            return;
        }

        _matrix.Excluded.Add(cell);
        RebuildMatrixCells();
        WriteMatrixFields();
        ShowAdjustmentBadge(
            Mouse.GetPosition(Root),
            $"已排除第 {cell.Row} 行第 {cell.Column} 列");
    }

    private void NudgeMatrix(int offsetX, int offsetY)
    {
        if (_matrix is null)
        {
            return;
        }

        SetMatrixBounds(CaptureFixedSizeConstraint.Move(
            _matrix.Bounds,
            offsetX,
            offsetY,
            CapturePixelBounds));
    }

    private void WriteMatrixFields()
    {
        if (_matrix is null)
        {
            return;
        }

        _updatingMatrixFields = true;
        MatrixRowsBox.Text = _matrix.Rows.ToString();
        MatrixColumnsBox.Text = _matrix.Columns.ToString();
        MatrixRowGapBox.Text = _matrix.GapX.ToString();
        MatrixColumnGapBox.Text = _matrix.GapY.ToString();
        _updatingMatrixFields = false;
        SetMatrixFieldState(MatrixRowsBox, MatrixFieldState.Normal);
        SetMatrixFieldState(MatrixColumnsBox, MatrixFieldState.Normal);
        SetMatrixFieldState(MatrixRowGapBox, MatrixFieldState.Normal);
        SetMatrixFieldState(MatrixColumnGapBox, MatrixFieldState.Normal);
        UpdateMatrixHint();
    }

    private enum MatrixFieldState
    {
        Normal,
        Warning,
        Invalid
    }

    private void SetMatrixFieldState(WpfTextBox box, MatrixFieldState state)
    {
        box.BorderBrush = new SolidColorBrush(state switch
        {
            MatrixFieldState.Invalid => MediaColor.FromRgb(255, 113, 128),
            MatrixFieldState.Warning => MediaColor.FromRgb(255, 190, 92),
            _ => MediaColor.FromRgb(0x53, 0x6A, 0x76)
        });
        box.BorderThickness = new Thickness(1);
    }

    private static bool TryReadMatrixValue(WpfTextBox box, int fallback, out int value, out bool invalid)
    {
        var text = box.Text.Trim();
        if (text.Length == 0)
        {
            value = fallback;
            invalid = false;
            return true;
        }

        if (int.TryParse(text, out value))
        {
            invalid = false;
            return true;
        }

        value = fallback;
        invalid = true;
        return false;
    }

    private void OnMatrixParameterChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingMatrixFields || _matrix is null)
        {
            return;
        }

        TryReadMatrixValue(MatrixRowsBox, _matrix.Rows, out var rows, out var rowsInvalid);
        TryReadMatrixValue(MatrixColumnsBox, _matrix.Columns, out var columns, out var columnsInvalid);
        TryReadMatrixValue(MatrixRowGapBox, _matrix.GapY, out var gapY, out var rowGapInvalid);
        TryReadMatrixValue(MatrixColumnGapBox, _matrix.GapX, out var gapX, out var columnGapInvalid);

        var parameters = MatrixLayout.Normalize(
            CaptureRegion.FromRectangle(_matrix.Bounds),
            rows,
            columns,
            gapX,
            gapY);
        var adjusted = parameters.Rows != rows ||
                       parameters.Columns != columns ||
                       parameters.GapX != gapX ||
                       parameters.GapY != gapY;
        _matrix.Rows = parameters.Rows;
        _matrix.Columns = parameters.Columns;
        _matrix.GapX = parameters.GapX;
        _matrix.GapY = parameters.GapY;
        _matrix.Excluded.RemoveWhere(cell =>
            cell.Row > _matrix.Rows || cell.Column > _matrix.Columns);
        RebuildMatrixCells();

        SetMatrixFieldState(MatrixRowsBox, rowsInvalid
            ? MatrixFieldState.Invalid
            : parameters.Rows != rows ? MatrixFieldState.Warning : MatrixFieldState.Normal);
        SetMatrixFieldState(MatrixColumnsBox, columnsInvalid
            ? MatrixFieldState.Invalid
            : parameters.Columns != columns ? MatrixFieldState.Warning : MatrixFieldState.Normal);
        SetMatrixFieldState(MatrixRowGapBox, rowGapInvalid
            ? MatrixFieldState.Invalid
            : parameters.GapY != gapY ? MatrixFieldState.Warning : MatrixFieldState.Normal);
        SetMatrixFieldState(MatrixColumnGapBox, columnGapInvalid
            ? MatrixFieldState.Invalid
            : parameters.GapX != gapX ? MatrixFieldState.Warning : MatrixFieldState.Normal);
        UpdateMatrixHint(adjusted);
    }

    private void UpdateMatrixHint(bool adjusted = false)
    {
        if (_matrix is null)
        {
            return;
        }

        var cells = MatrixLayout.Calculate(
            CaptureRegion.FromRectangle(_matrix.Bounds),
            _matrix.Rows,
            _matrix.Columns,
            _matrix.GapX,
            _matrix.GapY);
        var cellSize = cells.Count > 0 ? $"{cells[0].Width} × {cells[0].Height} px" : "放不下";
        MatrixHintText.Text = adjusted
            ? $"已按可用空间调整：{_matrix.Rows} 行 × {_matrix.Columns} 列，间隔 {_matrix.GapX} × {_matrix.GapY} px，单元格 {cellSize}"
            : $"单元格 {cellSize} · 单击单元格可排除或恢复";
        MatrixHintText.Foreground = new SolidColorBrush(adjusted
            ? MediaColor.FromRgb(255, 190, 92)
            : MediaColor.FromRgb(118, 144, 156));
    }

    private void OnMatrixParameterLostFocus(object sender, RoutedEventArgs e) => WriteMatrixFields();

    private void OnMatrixParameterKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            WriteMatrixFields();
            Focus();
            Keyboard.Focus(this);
            e.Handled = true;
        }
    }

    private void OnMatrixParameterWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not WpfTextBox box || _matrix is null)
        {
            return;
        }

        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
        if (!int.TryParse(box.Text, out var current))
        {
            current = box == MatrixRowsBox
                ? _matrix.Rows
                : box == MatrixColumnsBox
                    ? _matrix.Columns
                    : box == MatrixRowGapBox
                        ? _matrix.GapY
                        : _matrix.GapX;
        }

        var next = Math.Max(0, current + (e.Delta > 0 ? step : -step));
        box.Text = next.ToString();
        box.CaretIndex = box.Text.Length;
        e.Handled = true;
    }

    private void ShowMatrixContextMenu()
    {
        var menu = new ContextMenu
        {
            PlacementTarget = Surface,
            Placement = PlacementMode.MousePoint
        };
        if (_matrix is { Excluded.Count: > 0 })
        {
            var restore = new MenuItem { Header = $"恢复全部单元格（{_matrix.Excluded.Count} 个）" };
            restore.Click += (_, _) =>
            {
                _matrix?.Excluded.Clear();
                RebuildMatrixCells();
                WriteMatrixFields();
            };
            menu.Items.Add(restore);
            menu.Items.Add(new Separator());
        }

        var redraw = new MenuItem { Header = "重新框选基准矩形" };
        redraw.Click += (_, _) =>
        {
            ClearMatrix();
            ShowFullShade();
            ShowAdjustmentBadge(Mouse.GetPosition(Root), "请在画面上拖拽定义新的基准矩形");
        };
        var toFree = new MenuItem { Header = "切换为自由选区" };
        toFree.Click += (_, _) => SetSelectionMode(SelectionMode.Free);
        var clear = new MenuItem { Header = "清空矩阵" };
        clear.Click += (_, _) => ShowFullShade();
        menu.Items.Add(redraw);
        menu.Items.Add(toFree);
        menu.Items.Add(new Separator());
        menu.Items.Add(clear);
        menu.IsOpen = true;
    }

    private void UpdateSelectionVisuals()
    {
        for (var index = 0; index < _selections.Count; index++)
        {
            var item = _selections[index];
            var active = index == _activeSelectionIndex;
            item.Frame.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            SetPosition(item.Frame, item.Rect.Left, item.Rect.Top);
            item.Frame.Width = Math.Max(0, item.Rect.Width);
            item.Frame.Height = Math.Max(0, item.Rect.Height);
            item.BadgeText.Text = item.Label ?? (index + 1).ToString();
            SetPosition(item.Badge, item.Rect.Left + 5, item.Rect.Top + 5);
            item.Badge.Visibility = item.Rect.Width >= 26 && item.Rect.Height >= 20
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        UpdateMatrixLayer();
        UpdateShadeMask();
    }

    private void UpdateShadeMask()
    {
        var width = Math.Max(0, Surface.ActualWidth);
        var height = Math.Max(0, Surface.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            ShadeMask.Data = null;
            return;
        }

        Geometry? holes = null;
        foreach (var item in _selections)
        {
            if (item.Rect.IsEmpty)
            {
                continue;
            }

            var geometry = new RectangleGeometry(item.Rect);
            geometry.Freeze();
            holes = holes is null
                ? (Geometry)geometry
                : Geometry.Combine(holes, geometry, GeometryCombineMode.Union, null);
        }

        if (!_smartPreview.IsEmpty)
        {
            var preview = new RectangleGeometry(_smartPreview);
            preview.Freeze();
            holes = holes is null
                ? (Geometry)preview
                : Geometry.Combine(holes, preview, GeometryCombineMode.Union, null);
        }

        var full = new RectangleGeometry(new WpfRect(0, 0, width, height));
        full.Freeze();
        var mask = holes is null
            ? (Geometry)full
            : Geometry.Combine(full, holes, GeometryCombineMode.Exclude, null);
        mask.Freeze();
        ShadeMask.Data = mask;
    }

    private int FindSelectionAt(Point point)
    {
        if (_activeSelectionIndex >= 0 && _activeSelectionIndex < _selections.Count)
        {
            var activeBounds = _selections[_activeSelectionIndex].Rect;
            activeBounds.Inflate(HitPadding, HitPadding);
            if (activeBounds.Contains(point))
            {
                return _activeSelectionIndex;
            }
        }

        for (var index = _selections.Count - 1; index >= 0; index--)
        {
            if (index == _activeSelectionIndex)
            {
                continue;
            }

            var bounds = _selections[index].Rect;
            bounds.Inflate(HitPadding, HitPadding);
            if (bounds.Contains(point))
            {
                return index;
            }
        }

        return -1;
    }

    private void NudgeActiveSelection(int offsetX, int offsetY)
    {
        if (GetLocalRegion() is not { } current)
        {
            return;
        }

        var moved = CaptureFixedSizeConstraint.Move(current, offsetX, offsetY, CapturePixelBounds);
        SetSelectionFromPixels(moved);
    }

    private void CycleActiveSelection(int direction)
    {
        if (_selections.Count <= 1)
        {
            return;
        }

        var next = (_activeSelectionIndex + direction + _selections.Count) % _selections.Count;
        ActivateSelection(next);
        ShowAdjustmentBadge(Mouse.GetPosition(Root), $"选区 {next + 1}/{_selections.Count}");
    }

    private void CancelActiveDrag()
    {
        if (_dragMode == DragMode.None)
        {
            return;
        }

        if (_selectionMode == SelectionMode.Matrix && _matrixAtDragStart is { } matrixSnapshot)
        {
            _matrix = matrixSnapshot;
            _matrixAtDragStart = null;
            _matrixDragCell = null;
            _matrixDragMoved = false;
            _dragMode = DragMode.None;
            _dragCreatedSelection = false;
            _pendingSmartSelection = WpfRect.Empty;
            _selectionPixelAtDragStart = null;
            Surface.ReleaseMouseCapture();
            RebuildMatrixCells();
            WriteMatrixFields();
            return;
        }

        if (_dragCreatedSelection)
        {
            RemoveActiveSelection();
        }
        else if (ActiveSelection is { } active && _selectionPixelAtDragStart is { } originalPixels)
        {
            active.Rect = _selectionAtDragStart;
            active.Pixels = originalPixels;
            _selection = active.Rect;
            _selectionPixelRegion = active.Pixels;
            SetSelectionCore(active.Rect, active.Pixels);
        }

        _dragMode = DragMode.None;
        _dragCreatedSelection = false;
        _pendingSmartSelection = WpfRect.Empty;
        _selectionPixelAtDragStart = null;
        Surface.ReleaseMouseCapture();

        if (_selections.Count == 0)
        {
            ShowFullShade();
        }
        else
        {
            UpdateSelectionVisuals();
            UpdateSizeBadge(_selection);
            UpdateToolbarPosition(_selection);
            UpdateMultiSelectionUi();
        }
    }

    private void UpdateMultiSelectionUi()
    {
        var multi = _selectionMode == SelectionMode.Matrix || _selections.Count > 1;
        var freeMode = _selectionMode == SelectionMode.Free;
        WpfButton[] annotationButtons =
        [
            PenButton,
            LineButton,
            ArrowButton,
            RectangleButton,
            EllipseButton,
            TextButton,
            NumberButton,
            SelectButton
        ];
        foreach (var button in annotationButtons)
        {
            button.IsEnabled = !multi;
        }

        OcrButton.IsEnabled = !multi;
        PinButton.IsEnabled = !multi;
        LongCaptureButton.IsEnabled = !multi;
        SavePresetButton.IsEnabled = _selections.Count > 0;
        SavePresetButton.Visibility = _showActionToolbar && _selections.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectionListButton.IsEnabled = _selections.Count > 0;
        SelectionListButton.Visibility = _showActionToolbar && freeMode && _selections.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (SelectionListPopup.IsOpen)
        {
            if (freeMode)
            {
                Dispatcher.BeginInvoke(new Action(RebuildSelectionList));
            }
            else
            {
                SelectionListPopup.IsOpen = false;
            }
        }

        UpdateSelectionModeUi();
        if (multi && _annotationController.ActiveTool != CaptureAnnotationTool.None)
        {
            SetAnnotationTool(CaptureAnnotationTool.None);
            _annotationController.SetBounds(WpfRect.Empty, Surface.ActualWidth, Surface.ActualHeight);
        }
    }

    private void UpdateSmartPreview(Point point)
    {
        var scaleX = _snapshot.Bounds.Width / Math.Max(1, Surface.ActualWidth);
        var scaleY = _snapshot.Bounds.Height / Math.Max(1, Surface.ActualHeight);
        var target = NativeMethods.FindWindowAtPointExcludingProcess(
            new NativeMethods.NativePoint(
                _snapshot.Bounds.X + (int)Math.Round(point.X * scaleX),
                _snapshot.Bounds.Y + (int)Math.Round(point.Y * scaleY)),
            (uint)Environment.ProcessId);

        if (target == nint.Zero || !NativeMethods.GetWindowRect(target, out var windowRect))
        {
            ClearSmartPreview();
            return;
        }

        NativeMethods.GetWindowThreadProcessId(target, out var processId);
        if (processId == Environment.ProcessId)
        {
            ClearSmartPreview();
            return;
        }

        var leftPx = Math.Max(windowRect.Left, _snapshot.Bounds.Left);
        var topPx = Math.Max(windowRect.Top, _snapshot.Bounds.Top);
        var rightPx = Math.Min(windowRect.Right, _snapshot.Bounds.Right);
        var bottomPx = Math.Min(windowRect.Bottom, _snapshot.Bounds.Bottom);
        if (rightPx - leftPx < 20 || bottomPx - topPx < 20)
        {
            ClearSmartPreview();
            return;
        }

        var toDipX = Surface.ActualWidth / Math.Max(1, _snapshot.Bounds.Width);
        var toDipY = Surface.ActualHeight / Math.Max(1, _snapshot.Bounds.Height);
        var preview = new WpfRect(
            (leftPx - _snapshot.Bounds.X) * toDipX,
            (topPx - _snapshot.Bounds.Y) * toDipY,
            (rightPx - leftPx) * toDipX,
            (bottomPx - topPx) * toDipY);
        if (_smartPreview == preview && SmartPreviewBorder.Visibility == Visibility.Visible)
        {
            return;
        }

        _smartPreview = preview;
        ShowSmartPreview(preview);
    }

    private void ShowSmartPreview(WpfRect rect)
    {
        _smartPreview = rect;
        SmartPreviewBorder.Visibility = Visibility.Visible;
        SetPosition(SmartPreviewBorder, rect.Left, rect.Top);
        SmartPreviewBorder.Width = rect.Width;
        SmartPreviewBorder.Height = rect.Height;
        UpdateShadeMask();
        UpdateSizeBadge(rect, CaptureRegion.FromRectangle(ToPhysicalRectangle(rect)), isPreview: true);
        CaptureToolbar.Visibility = Visibility.Collapsed;
        QuickTagButton.Visibility = Visibility.Collapsed;
        QuickTagPopup.IsOpen = false;
        SelectionListPopup.IsOpen = false;
        HintBadge.Visibility = Visibility.Collapsed;
        foreach (var mark in _cornerMarks)
        {
            mark.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearSmartPreview()
    {
        if (_smartPreview.IsEmpty)
        {
            return;
        }

        _smartPreview = WpfRect.Empty;
        SmartPreviewBorder.Visibility = Visibility.Collapsed;
        UpdateShadeMask();
        if (ActiveSelection is not null)
        {
            UpdateToolbarPosition(_selection);
            UpdateSizeBadge(_selection);
            UpdateCornerMarks(_selection);
            HintBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            SizeBadge.Visibility = Visibility.Collapsed;
            HintBadge.Visibility = Visibility.Visible;
        }
    }

    private void UpdateMagnifier(Point point)
    {
        if (!_settings.ShowMagnifier || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
        {
            MagnifierOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        var pixelX = MagnifierPixelGrid.MapPointerToPixel(
            point.X,
            Surface.ActualWidth,
            _snapshot.Image.PixelWidth);
        var pixelY = MagnifierPixelGrid.MapPointerToPixel(
            point.Y,
            Surface.ActualHeight,
            _snapshot.Image.PixelHeight);
        const int cropWidth = MagnifierPixelGrid.Columns;
        const int cropHeight = MagnifierPixelGrid.Rows;
        var cropX = MagnifierPixelGrid.GetCropOrigin(pixelX, _snapshot.Image.PixelWidth, cropWidth);
        var cropY = MagnifierPixelGrid.GetCropOrigin(pixelY, _snapshot.Image.PixelHeight, cropHeight);
        var width = Math.Min(cropWidth, _snapshot.Image.PixelWidth - cropX);
        var height = Math.Min(cropHeight, _snapshot.Image.PixelHeight - cropY);
        MagnifierImage.Source = new CroppedBitmap(_snapshot.Image, new Int32Rect(cropX, cropY, width, height));

        // The reticle now sits on the top/left boundary of the sampled pixel. With
        // the even 28 x 12 grid this is the exact centre during normal use, while
        // remaining accurate when the crop is pushed against a screen edge.
        var displayWidth = MagnifierImageHost.ActualWidth > 0 ? MagnifierImageHost.ActualWidth : 280;
        var displayHeight = MagnifierImageHost.ActualHeight > 0 ? MagnifierImageHost.ActualHeight : 120;
        var crosshairX = MagnifierPixelGrid.GetBoundaryOffset(pixelX, cropX, width, displayWidth);
        var crosshairY = MagnifierPixelGrid.GetBoundaryOffset(pixelY, cropY, height, displayHeight);
        MagnifierVerticalCrosshair.Margin = new Thickness(crosshairX - 0.5, 0, 0, 0);
        MagnifierHorizontalCrosshair.Margin = new Thickness(0, crosshairY - 0.5, 0, 0);

        BitmapSource source = _snapshot.Image.Format == PixelFormats.Bgra32
            ? _snapshot.Image
            : new FormatConvertedBitmap(_snapshot.Image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(pixelX, pixelY, 1, 1), pixel, 4, 0);
        _currentPixelColor = MediaColor.FromRgb(pixel[2], pixel[1], pixel[0]);
        MagnifierCoordinateText.Text = $"{_snapshot.Bounds.X + pixelX}, {_snapshot.Bounds.Y + pixelY}";
        MagnifierColorText.Text = $"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";

        var left = point.X + 18;
        var top = point.Y + 18;
        if (left + MagnifierOverlay.Width > Surface.ActualWidth)
        {
            left = point.X - MagnifierOverlay.Width - 18;
        }

        if (top + MagnifierOverlay.Height > Surface.ActualHeight)
        {
            top = point.Y - MagnifierOverlay.Height - 18;
        }

        MagnifierOverlay.Margin = new Thickness(Math.Max(0, left), Math.Max(0, top), 0, 0);
        MagnifierOverlay.Visibility = Visibility.Visible;
    }

    private void ShowFullShade()
    {
        _annotationController.Reset();
        _annotationController.SetBounds(WpfRect.Empty, Surface.ActualWidth, Surface.ActualHeight);
        ClearMatrix();
        _smartPreview = WpfRect.Empty;
        _lockedSelectionSize = null;
        UpdateFixedSizeUi();
        SelectionBorder.Visibility = Visibility.Collapsed;
        SmartPreviewBorder.Visibility = Visibility.Collapsed;
        SizeBadge.Visibility = Visibility.Collapsed;
        CaptureToolbar.Visibility = Visibility.Collapsed;
        QuickTagButton.Visibility = Visibility.Collapsed;
        QuickTagPopup.IsOpen = false;
        SelectionListPopup.IsOpen = false;
        HintBadge.Visibility = Visibility.Visible;
        if (_selectionMode == SelectionMode.Matrix)
        {
            HintActionText.Text = "矩阵模式 · 拖拽定义基准矩形 · M 切换自由模式 · ESC 退出";
        }
        else
        {
            HintActionText.Text = _showActionToolbar
                ? UsesCloseButton
                    ? "拖动框选多个区域 · Tab 切换 · Delete 删除 · ENTER 输出 · ESC / × 关闭"
                    : "拖动框选多个区域 · Tab 切换 · Delete 删除 · ENTER 输出 · ESC 退出"
                : "R 循环历史选区  ·  双击 / ENTER 复制  ·  ESC / × 关闭";
        }

        foreach (var mark in _cornerMarks)
        {
            mark.Visibility = Visibility.Collapsed;
        }

        UpdateShadeMask();
        UpdateMultiSelectionUi();
    }

    private void UpdateCornerMarks(WpfRect rect)
    {
        foreach (var mark in _cornerMarks)
        {
            mark.Visibility = _lockedSelectionSize is null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_lockedSelectionSize is not null)
        {
            return;
        }

        SetPosition(TopLeftHorizontal, rect.Left - 1, rect.Top - 1);
        SetPosition(TopLeftVertical, rect.Left - 1, rect.Top - 1);
        SetPosition(TopRightHorizontal, rect.Right - 15, rect.Top - 1);
        SetPosition(TopRightVertical, rect.Right - 2, rect.Top - 1);
        SetPosition(BottomLeftHorizontal, rect.Left - 1, rect.Bottom - 2);
        SetPosition(BottomLeftVertical, rect.Left - 1, rect.Bottom - 15);
        SetPosition(BottomRightHorizontal, rect.Right - 15, rect.Bottom - 2);
        SetPosition(BottomRightVertical, rect.Right - 2, rect.Bottom - 15);
    }

    private void UpdateSizeBadge(
        WpfRect rect,
        CaptureRegion? physicalOverride = null,
        bool isPreview = false)
    {
        if (_selectionMode == SelectionMode.Matrix && !isPreview)
        {
            UpdateMatrixSizeBadge();
            return;
        }

        var physical = physicalOverride
            ?? (_selectionPixelRegion is { } pixels
                ? CaptureRegion.FromRectangle(pixels)
                : CaptureRegion.FromRectangle(ToPhysicalRectangle(rect)));
        var physicalWidth = physical.Width;
        var physicalHeight = physical.Height;
        var count = _selections.Count;
        var activeNumber = _activeSelectionIndex + 1;
        SizeText.Text = isPreview
            ? $"{physicalWidth} × {physicalHeight} px"
            : count > 1
                ? $"#{activeNumber}/{count} · {physicalWidth} × {physicalHeight} px"
                : $"{physicalWidth} × {physicalHeight} px";
        var constraintLabel = _lockedSelectionSize is not null
            ? _aspectRatioMode == CaptureAspectRatioMode.Free
                ? "固定"
                : $"固定 · {AspectRatioLabel(_aspectRatioMode)}"
            : _aspectRatioMode == CaptureAspectRatioMode.Free
                ? null
                : AspectRatioLabel(_aspectRatioMode);
        ToolbarSizeText.Text = isPreview
            ? $"{physicalWidth} × {physicalHeight}"
            : count > 1
                ? $"{count} 个选区 · {physicalWidth} × {physicalHeight}"
                : constraintLabel is null
                    ? $"{physicalWidth} × {physicalHeight}"
                    : $"{physicalWidth} × {physicalHeight}  ·  {constraintLabel}";
        PositionSizeBadge(rect);
    }

    private void UpdateHintPosition()
    {
        HintBadge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(HintBadge, Math.Max(12, (Surface.ActualWidth - HintBadge.DesiredSize.Width) / 2));
        Canvas.SetTop(HintBadge, 22);
    }

    private void UpdateToolbarPosition(WpfRect rect)
    {
        if (!_showActionToolbar)
        {
            CaptureToolbar.Visibility = Visibility.Collapsed;
            QuickTagButton.Visibility = Visibility.Collapsed;
            SavePresetButton.Visibility = Visibility.Collapsed;
            SelectionListButton.Visibility = Visibility.Collapsed;
            MatrixPanel.Visibility = Visibility.Collapsed;
            SelectionListPopup.IsOpen = false;
            QuickTagPopup.IsOpen = false;
            return;
        }

        QuickTagButton.Visibility = _settings.ShowQuickCaptureTags
            ? Visibility.Visible
            : Visibility.Collapsed;
        SavePresetButton.Visibility = _selections.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectionListButton.Visibility =
            _selections.Count > 0 && _selectionMode == SelectionMode.Free
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (!_settings.ShowQuickCaptureTags)
        {
            QuickTagPopup.IsOpen = false;
        }

        CaptureToolbar.Visibility = Visibility.Visible;
        CaptureToolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarWidth = CaptureToolbar.DesiredSize.Width;
        var toolbarHeight = CaptureToolbar.DesiredSize.Height;
        var left = Math.Clamp(
            rect.Right - toolbarWidth,
            10,
            Math.Max(10, Surface.ActualWidth - toolbarWidth - 10));
        var top = rect.Bottom + 10;
        if (top + toolbarHeight > Surface.ActualHeight - 10)
        {
            top = rect.Top - toolbarHeight - 10;
        }

        top = Math.Clamp(top, 10, Math.Max(10, Surface.ActualHeight - toolbarHeight - 10));
        SetPosition(CaptureToolbar, left, top);
        UpdateMatrixPanelPosition();
    }

    private void ConfirmSelection()
    {
        if (_selections.Count == 0)
        {
            return;
        }

        if (_showActionToolbar)
        {
            RequestAction(CaptureOverlayAction.Confirm);
            return;
        }

        if (GetLocalRegion() is { } localRegion)
        {
            SelectionConfirmed?.Invoke(this, localRegion);
        }
    }

    private void RequestAction(CaptureOverlayAction action)
    {
        var output = CreateSelectionOutput();
        if (output is null)
        {
            return;
        }

        if ((!output.IsSingle || output.Mode == SelectionMode.Matrix) &&
            (action is CaptureOverlayAction.Ocr or
                CaptureOverlayAction.QrCode or
                CaptureOverlayAction.Pin or
                CaptureOverlayAction.AutomaticLongCapture))
        {
            return;
        }

        QuickTagPopup.IsOpen = false;
        var usePrefetch = output.IsSingle &&
                          (action is CaptureOverlayAction.Ocr or
                              CaptureOverlayAction.Pin or
                              CaptureOverlayAction.Copy or
                              CaptureOverlayAction.Confirm) &&
                          _prefetchedOcrImage is not null &&
                          _prefetchedOcrTask is not null;
        var image = usePrefetch ? _prefetchedOcrImage! : output.PrimaryImage;
        var prefetchedOcr = usePrefetch ? _prefetchedOcrTask : null;
        if (usePrefetch)
        {
            _ocrPrefetchTimer.Stop();
            var handedOffCancellation = _ocrPrefetchCancellation;
            _ocrPrefetchCancellation = null;
            if (handedOffCancellation is not null && prefetchedOcr is not null)
            {
                _ = prefetchedOcr.ContinueWith(
                    _ => handedOffCancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        else
        {
            CancelOcrPrefetch();
        }

        ActionRequested?.Invoke(
            this,
            new CaptureOverlayActionEventArgs(
                action,
                output,
                prefetchedOcr,
                SelectedTags));
    }

    private void InvalidateOcrPrefetch()
    {
        if (_creatingOcrPrefetch ||
            _selectionMode != SelectionMode.Free ||
            _selections.Count != 1 ||
            _ocrService?.IsOcrAvailable != true ||
            !_showActionToolbar)
        {
            return;
        }

        CancelOcrPrefetch();
        if (!_selection.IsEmpty)
        {
            _ocrPrefetchTimer.Start();
        }
    }

    private void CancelOcrPrefetch()
    {
        _ocrPrefetchTimer.Stop();
        _ocrPrefetchCancellation?.Cancel();
        _ocrPrefetchCancellation?.Dispose();
        _ocrPrefetchCancellation = null;
        _prefetchedOcrTask = null;
        _prefetchedOcrImage = null;
    }

    private void OnOcrPrefetchTimerTick(object? sender, EventArgs e)
    {
        _ocrPrefetchTimer.Stop();
        if (_ocrService?.IsOcrAvailable != true ||
            _selections.Count != 1 ||
            _selection.IsEmpty ||
            !IsVisible)
        {
            return;
        }

        try
        {
            _creatingOcrPrefetch = true;
            _prefetchedOcrImage = CreateSelectedImage();
            _ocrPrefetchCancellation = new CancellationTokenSource();
            _prefetchedOcrTask = _ocrService.RecognizeAsync(
                _prefetchedOcrImage,
                _ocrPrefetchCancellation.Token,
                progress: null,
                includeWordBoxes: true);
            _ = ObserveOcrPrefetchAsync(_prefetchedOcrTask);
        }
        finally
        {
            _creatingOcrPrefetch = false;
        }
    }

    private static async Task ObserveOcrPrefetchAsync(Task<OcrRecognitionResult> task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Warning("OCR", $"截图 OCR 预取失败：{exception.Message}");
        }
    }

    public BitmapSource CreateSelectedImage()
    {
        var localRegion = GetLocalRegion() ??
                          throw new InvalidOperationException("还没有可输出的截图选区。");
        _annotationController.CommitText();

        UIElement[] hiddenElements =
        [
            ShadeMask,
            SelectionLayer,
            SmartPreviewBorder,
            SelectionBorder,
            SizeBadge,
            HintBadge,
            CaptureToolbar,
            QuickTagButton,
            SavePresetButton,
            AdjustmentBadge,
            MagnifierOverlay,
            GeometryEditor,
            .. _cornerMarks
        ];
        var visibility = hiddenElements.Select(element => element.Visibility).ToArray();
        _annotationController.SetSelectionAdornersVisible(false);
        try
        {
            foreach (var element in hiddenElements)
            {
                element.Visibility = Visibility.Collapsed;
            }

            Root.UpdateLayout();
            var scaleX = _snapshot.Image.PixelWidth / Math.Max(1, Root.ActualWidth);
            var scaleY = _snapshot.Image.PixelHeight / Math.Max(1, Root.ActualHeight);
            var rendered = new RenderTargetBitmap(
                _snapshot.Image.PixelWidth,
                _snapshot.Image.PixelHeight,
                96 * scaleX,
                96 * scaleY,
                PixelFormats.Pbgra32);
            rendered.Render(Root);

            var cropped = new CroppedBitmap(
                rendered,
                new Int32Rect(
                    localRegion.X,
                    localRegion.Y,
                    localRegion.Width,
                    localRegion.Height));
            cropped.Freeze();
            return cropped;
        }
        finally
        {
            for (var index = 0; index < hiddenElements.Length; index++)
            {
                hiddenElements[index].Visibility = visibility[index];
            }
            _annotationController.SetSelectionAdornersVisible(true);
        }
    }

    private DrawingRectangle? GetLocalRegion()
    {
        if (_selection.IsEmpty)
        {
            return null;
        }

        return _selectionPixelRegion ?? ToPhysicalRectangle(_selection);
    }

    public CaptureSelectionOutput? CreateSelectionOutput()
    {
        var matrixMode = _selectionMode == SelectionMode.Matrix && _matrix is not null;
        var valid = _selections
            .Select((item, index) => (Item: item, Index: index))
            .Where(entry => entry.Item.Pixels.Width > 0 && entry.Item.Pixels.Height > 0)
            .ToArray();
        var regions = valid
            .Select(entry => CaptureRegion.FromRectangle(entry.Item.Pixels))
            .ToArray();
        if (regions.Length == 0)
        {
            return null;
        }

        var cells = matrixMode
            ? valid.Select(entry => _matrixCellOrder[entry.Index]).ToArray()
            : null;
        var matrix = matrixMode && _matrix is not null
            ? _matrix.ToSpec(CaptureRegion.FromRectangle(_matrix.Bounds))
            : null;
        var mode = matrixMode ? SelectionMode.Matrix : SelectionMode.Free;

        if (!matrixMode && regions.Length == 1 && _activeSelectionIndex != 0)
        {
            ActivateSelection(0);
        }

        var padding = EffectivePadding;
        if (regions.Length == 1)
        {
            var image = matrixMode
                ? SelectionImageComposer.Crop(_snapshot.Image, regions[0])
                : CreateSelectedImage();
            var box = SelectionImageComposer.GetBoundingBox(regions, padding, CapturePixelBounds);
            var left = box.IsValid ? regions[0].X - box.X : padding;
            var top = box.IsValid ? regions[0].Y - box.Y : padding;
            var right = box.IsValid
                ? box.X + box.Width - (regions[0].X + regions[0].Width)
                : padding;
            var bottom = box.IsValid
                ? box.Y + box.Height - (regions[0].Y + regions[0].Height)
                : padding;
            var composite = SelectionImageComposer.AddTransparentPadding(
                image,
                left,
                top,
                right,
                bottom);
            return new CaptureSelectionOutput(
                [image],
                regions,
                composite,
                box.IsValid ? box : null,
                padding,
                mode,
                matrix,
                cells);
        }

        var images = regions
            .Select(region => SelectionImageComposer.Crop(_snapshot.Image, region))
            .ToArray();
        var multiComposite = SelectionImageComposer.CreateTransparentComposite(
            _snapshot.Image,
            regions,
            padding);
        var multiBox = SelectionImageComposer.GetBoundingBox(regions, padding, CapturePixelBounds);
        return new CaptureSelectionOutput(
            images,
            regions,
            multiComposite,
            multiBox.IsValid ? multiBox : null,
            padding,
            mode,
            matrix,
            cells);
    }

    public SelectionSet? CreateGlobalSelectionSet()
    {
        if (_selectionMode == SelectionMode.Matrix && _matrix is not null)
        {
            var globalBounds = new CaptureRegion(
                _snapshot.Bounds.X + _matrix.Bounds.X,
                _snapshot.Bounds.Y + _matrix.Bounds.Y,
                _matrix.Bounds.Width,
                _matrix.Bounds.Height);
            var matrixRegions = _selections
                .Where(item => item.Pixels.Width > 0 && item.Pixels.Height > 0)
                .Select(item => new CaptureRegion(
                    _snapshot.Bounds.X + item.Pixels.X,
                    _snapshot.Bounds.Y + item.Pixels.Y,
                    item.Pixels.Width,
                    item.Pixels.Height))
                .ToArray();
            return matrixRegions.Length == 0
                ? null
                : new SelectionSet(
                    SelectionMode.Matrix,
                    matrixRegions,
                    _matrix.ToSpec(globalBounds),
                    EffectivePadding);
        }

        var regions = _selections
            .Where(item => item.Pixels.Width > 0 && item.Pixels.Height > 0)
            .Select(item => new CaptureRegion(
                _snapshot.Bounds.X + item.Pixels.X,
                _snapshot.Bounds.Y + item.Pixels.Y,
                item.Pixels.Width,
                item.Pixels.Height))
            .ToArray();
        return regions.Length == 0
            ? null
            : new SelectionSet(
                SelectionMode.Free,
                regions,
                Padding: EffectivePadding);
    }

    private DrawingRectangle ToPhysicalRectangle(WpfRect rect)
    {
        var scaleX = _snapshot.Bounds.Width / Math.Max(1, Surface.ActualWidth);
        var scaleY = _snapshot.Bounds.Height / Math.Max(1, Surface.ActualHeight);
        var left = Math.Clamp((int)Math.Floor(rect.Left * scaleX), 0, _snapshot.Bounds.Width - 1);
        var top = Math.Clamp((int)Math.Floor(rect.Top * scaleY), 0, _snapshot.Bounds.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(rect.Right * scaleX), left + 1, _snapshot.Bounds.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom * scaleY), top + 1, _snapshot.Bounds.Height);
        return new DrawingRectangle(left, top, right - left, bottom - top);
    }

    private System.Drawing.Point ToPhysicalPoint(Point point)
    {
        var scaleX = _snapshot.Bounds.Width / Math.Max(1, Surface.ActualWidth);
        var scaleY = _snapshot.Bounds.Height / Math.Max(1, Surface.ActualHeight);
        return new System.Drawing.Point(
            Math.Clamp((int)Math.Round(point.X * scaleX), 0, _snapshot.Bounds.Width),
            Math.Clamp((int)Math.Round(point.Y * scaleY), 0, _snapshot.Bounds.Height));
    }

    private void SetAspectRatioMode(CaptureAspectRatioMode mode, bool adjustSelection)
    {
        var current = GetLocalRegion() ?? DrawingRectangle.Empty;
        var ratio = CaptureAspectRatioConstraint.RatioFor(mode, current);
        if (mode == CaptureAspectRatioMode.Current && ratio is null)
        {
            return;
        }

        _aspectRatioMode = mode;
        _lockedAspectRatio = ratio;
        if (adjustSelection && _lockedSelectionSize is null &&
            ratio is { } lockedRatio && !current.IsEmpty &&
            mode != CaptureAspectRatioMode.Current)
        {
            SetSelectionFromPixels(CaptureAspectRatioConstraint.FitCentered(
                current,
                lockedRatio,
                _snapshot.Bounds.Width,
                _snapshot.Bounds.Height));
        }
        else if (!_selection.IsEmpty)
        {
            UpdateSizeBadge(_selection);
            UpdateToolbarPosition(_selection);
        }

        UpdateAspectRatioButtons();
    }

    private static bool IsCornerDrag(DragMode mode) => mode is
        DragMode.TopLeft or DragMode.TopRight or DragMode.BottomLeft or DragMode.BottomRight;

    private static bool TryMapResizeHandle(DragMode mode, out CaptureResizeHandle handle)
    {
        switch (mode)
        {
            case DragMode.Left:
                handle = CaptureResizeHandle.Left;
                return true;
            case DragMode.Right:
                handle = CaptureResizeHandle.Right;
                return true;
            case DragMode.Top:
                handle = CaptureResizeHandle.Top;
                return true;
            case DragMode.Bottom:
                handle = CaptureResizeHandle.Bottom;
                return true;
            case DragMode.TopLeft:
                handle = CaptureResizeHandle.TopLeft;
                return true;
            case DragMode.TopRight:
                handle = CaptureResizeHandle.TopRight;
                return true;
            case DragMode.BottomLeft:
                handle = CaptureResizeHandle.BottomLeft;
                return true;
            case DragMode.BottomRight:
                handle = CaptureResizeHandle.BottomRight;
                return true;
            default:
                handle = default;
                return false;
        }
    }

    private static string AspectRatioLabel(CaptureAspectRatioMode mode) => mode switch
    {
        CaptureAspectRatioMode.Square => "1:1",
        CaptureAspectRatioMode.FourThree => "4:3",
        CaptureAspectRatioMode.ThreeTwo => "3:2",
        CaptureAspectRatioMode.SixteenNine => "16:9",
        CaptureAspectRatioMode.NineSixteen => "9:16",
        CaptureAspectRatioMode.Current => "当前比例",
        _ => "自由"
    };

    private void OnAutomaticLongCaptureClick(object sender, RoutedEventArgs e) =>
        RequestAction(CaptureOverlayAction.AutomaticLongCapture);

    private void OnPenClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Pen, sender, supportsColor: true);

    private void OnLineClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Line, sender, supportsColor: true);

    private void OnArrowClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(_arrowTool);
        ShowArrowToolPopup();
    }

    private void OnRectangleClick(object sender, RoutedEventArgs e)
    {
        SetAnnotationTool(_regionTool);
        ShowRegionToolPopup();
    }

    private void OnEllipseClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Ellipse, sender, supportsColor: true);

    private void OnTextClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Text, sender, supportsColor: true);

    private void OnNumberClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Number, sender, supportsColor: true);

    private void OnSelectClick(object sender, RoutedEventArgs e) =>
        ActivateAnnotationTool(CaptureAnnotationTool.Select, sender, supportsColor: false);

    private void ActivateAnnotationTool(CaptureAnnotationTool tool, object sender, bool supportsColor)
    {
        SetAnnotationTool(tool);
        RegionToolPopup.IsOpen = false;
        ArrowToolPopup.IsOpen = false;
        if (supportsColor && sender is WpfButton button)
        {
            ShowAnnotationPalette(button);
        }
        else
        {
            AnnotationPalettePopup.IsOpen = false;
        }
    }

    private void ShowAnnotationPalette(WpfButton placementTarget)
    {
        UpdatePaletteSelection();
        AnnotationPalettePopup.PlacementTarget = placementTarget;
        AnnotationPalettePopup.IsOpen = true;
    }

    private void ShowRegionToolPopup()
    {
        AnnotationPalettePopup.IsOpen = false;
        ArrowToolPopup.IsOpen = false;
        UpdateRegionToolPopup();
        RegionToolPopup.PlacementTarget = RectangleButton;
        RegionToolPopup.IsOpen = true;
    }

    private void ShowArrowToolPopup()
    {
        AnnotationPalettePopup.IsOpen = false;
        RegionToolPopup.IsOpen = false;
        UpdateArrowToolPopup();
        ArrowToolPopup.PlacementTarget = ArrowButton;
        ArrowToolPopup.IsOpen = true;
    }

    private void OnArrowModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string toolName } ||
            !Enum.TryParse(toolName, out CaptureAnnotationTool tool) ||
            tool is not (CaptureAnnotationTool.Arrow or
                         CaptureAnnotationTool.DoubleArrow))
        {
            return;
        }

        _arrowTool = tool;
        UpdateArrowToolButton();
        SetAnnotationTool(tool);
        UpdateArrowToolPopup();
    }

    private void UpdateArrowToolButton()
    {
        ArrowToolIcon.Kind = _arrowTool switch
        {
            CaptureAnnotationTool.DoubleArrow => QingSnapIconKind.DoubleArrow,
            _ => QingSnapIconKind.Arrow
        };
        ArrowButton.ToolTip = _arrowTool switch
        {
            CaptureAnnotationTool.DoubleArrow => "箭头工具：双头箭头",
            _ => "箭头工具：单头箭头"
        };
    }

    private void UpdateArrowToolPopup()
    {
        var inactive = new SolidColorBrush(Colors.Transparent);
        var active = new SolidColorBrush(MediaColor.FromRgb(49, 71, 84));
        foreach (var button in ArrowModePanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           Enum.TryParse(value, out CaptureAnnotationTool tool) &&
                           tool == _arrowTool;
            button.Background = selected ? active : inactive;
        }

        UpdatePaletteSelection();
    }

    private void OnRegionModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string toolName } ||
            !Enum.TryParse(toolName, out CaptureAnnotationTool tool) ||
            tool is not (CaptureAnnotationTool.Rectangle or
                         CaptureAnnotationTool.Mosaic or
                         CaptureAnnotationTool.Highlight or
                         CaptureAnnotationTool.Blur))
        {
            return;
        }

        _regionTool = tool;
        UpdateRegionToolButton();
        SetAnnotationTool(tool);
        UpdateRegionToolPopup();
    }

    private void UpdateRegionToolButton()
    {
        RegionToolIcon.Kind = _regionTool switch
        {
            CaptureAnnotationTool.Mosaic => QingSnapIconKind.Mosaic,
            CaptureAnnotationTool.Highlight => QingSnapIconKind.Highlight,
            CaptureAnnotationTool.Blur => QingSnapIconKind.Blur,
            _ => QingSnapIconKind.Rectangle
        };
        RectangleButton.ToolTip = _regionTool switch
        {
            CaptureAnnotationTool.Mosaic => "区域工具：马赛克",
            CaptureAnnotationTool.Highlight => "区域工具：高亮",
            CaptureAnnotationTool.Blur => "区域工具：模糊",
            _ => "区域工具：矩形"
        };
    }

    private void UpdateRegionToolPopup()
    {
        var inactive = new SolidColorBrush(Colors.Transparent);
        var active = new SolidColorBrush(MediaColor.FromRgb(49, 71, 84));
        foreach (var button in RegionModePanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           Enum.TryParse(value, out CaptureAnnotationTool tool) &&
                           tool == _regionTool;
            button.Background = selected ? active : inactive;
        }

        var usesColor = _regionTool is CaptureAnnotationTool.Rectangle or CaptureAnnotationTool.Highlight;
        RegionColorDivider.Visibility = usesColor ? Visibility.Visible : Visibility.Collapsed;
        RegionColorPanel.Visibility = usesColor ? Visibility.Visible : Visibility.Collapsed;
        UpdatePaletteSelection();
    }

    private void OnPaletteColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string colorValue })
        {
            return;
        }

        var color = (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(colorValue);
        _annotationController.SetColor(color);
        UpdatePaletteSelection();
        AnnotationPalettePopup.IsOpen = false;
        RegionToolPopup.IsOpen = false;
        ArrowToolPopup.IsOpen = false;
        Focus();
    }

    private void UpdatePaletteSelection()
    {
        foreach (var button in AnnotationPalettePanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value) == _annotationController.CurrentColor;
            button.BorderBrush = new SolidColorBrush(
                selected ? Colors.White : MediaColor.FromArgb(102, 83, 106, 118));
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        foreach (var button in RegionColorPanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value) == _annotationController.CurrentColor;
            button.BorderBrush = new SolidColorBrush(
                selected ? Colors.White : MediaColor.FromArgb(102, 83, 106, 118));
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        foreach (var button in ArrowColorPanel.Children.OfType<WpfButton>())
        {
            var selected = button.Tag is string value &&
                           (MediaColor)System.Windows.Media.ColorConverter.ConvertFromString(value) == _annotationController.CurrentColor;
            button.BorderBrush = new SolidColorBrush(
                selected ? Colors.White : MediaColor.FromArgb(102, 83, 106, 118));
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CaptureToolbar.IsMouseOver ||
            MatrixPanel.IsMouseOver ||
            AnnotationPalettePopup.Child is UIElement { IsMouseOver: true } ||
            RegionToolPopup.Child is UIElement { IsMouseOver: true } ||
            ArrowToolPopup.Child is UIElement { IsMouseOver: true } ||
            GeometryEditor.IsMouseOver)
        {
            return;
        }

        var point = ClampPoint(e.GetPosition(Surface));
        var result = _annotationController.AdjustAnnotationAt(point, e.Delta);
        if (result is null)
        {
            return;
        }

        ShowAdjustmentBadge(point, result);
        e.Handled = true;
    }

    private void ShowAdjustmentBadge(Point point, string text)
    {
        AdjustmentText.Text = text;
        AdjustmentBadge.Visibility = Visibility.Visible;
        AdjustmentBadge.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = Math.Clamp(
            point.X + 16,
            8,
            Math.Max(8, Root.ActualWidth - AdjustmentBadge.DesiredSize.Width - 8));
        var top = Math.Clamp(
            point.Y - AdjustmentBadge.DesiredSize.Height - 12,
            8,
            Math.Max(8, Root.ActualHeight - AdjustmentBadge.DesiredSize.Height - 8));
        AdjustmentBadge.Margin = new Thickness(left, top, 0, 0);
        _adjustmentBadgeTimer.Stop();
        _adjustmentBadgeTimer.Start();
    }

    private void OnColorClick(object sender, RoutedEventArgs e)
    {
        _annotationController.CycleColor();
        UpdateStyleButtons();
    }

    private void OnEditAnnotationTextClick(object sender, RoutedEventArgs e)
    {
        _annotationController.BeginEditSelectedText();
    }

    private void OnEditAnnotationNumberClick(object sender, RoutedEventArgs e) =>
        BeginEditSelectedNumber();

    private bool BeginEditSelectedAnnotation() =>
        _annotationController.CanEditSelectedNumber
            ? BeginEditSelectedNumber()
            : _annotationController.BeginEditSelectedText();

    private bool BeginEditSelectedNumber()
    {
        if (!_annotationController.TryGetSelectedNumber(out var currentValue))
        {
            return false;
        }

        var dialog = new NumberAnnotationEditWindow(currentValue)
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && _annotationController.SetSelectedNumber(dialog.Value))
        {
            ShowAdjustmentBadge(Mouse.GetPosition(Root), $"序号已改为 {dialog.Value}");
        }

        Activate();
        return true;
    }

    private void OnBringAnnotationToFrontClick(object sender, RoutedEventArgs e) =>
        ApplyLayerCommand(_annotationController.BringSelectedToFront(), "已置于顶层");

    private void OnBringAnnotationForwardClick(object sender, RoutedEventArgs e) =>
        ApplyLayerCommand(_annotationController.BringSelectedForward(), "已上移一层");

    private void OnSendAnnotationBackwardClick(object sender, RoutedEventArgs e) =>
        ApplyLayerCommand(_annotationController.SendSelectedBackward(), "已下移一层");

    private void OnSendAnnotationToBackClick(object sender, RoutedEventArgs e) =>
        ApplyLayerCommand(_annotationController.SendSelectedToBack(), "已置于底层");

    private void OnCopyAnnotationClick(object sender, RoutedEventArgs e)
    {
        _annotationController.CopySelected();
        _annotationController.PasteSelected();
        ShowAdjustmentBadge(Mouse.GetPosition(Root), "已复制标注");
    }

    private void OnDeleteAnnotationClick(object sender, RoutedEventArgs e)
    {
        _annotationController.DeleteSelected();
        ShowAdjustmentBadge(Mouse.GetPosition(Root), "已删除标注");
    }

    private void ApplyLayerCommand(bool changed, string message)
    {
        if (changed)
        {
            ShowAdjustmentBadge(Mouse.GetPosition(Root), message);
        }
    }

    private void OnThicknessClick(object sender, RoutedEventArgs e)
    {
        _annotationController.CycleThickness();
        UpdateStyleButtons();
    }

    private void OnFontSizeClick(object sender, RoutedEventArgs e)
    {
        _annotationController.CycleFontSize();
        UpdateStyleButtons();
    }

    private void UpdateStyleButtons()
    {
        ColorButton.Foreground = new SolidColorBrush(_annotationController.CurrentColor);
        ColorButton.ToolTip = $"切换标注颜色  #{_annotationController.CurrentColor.R:X2}{_annotationController.CurrentColor.G:X2}{_annotationController.CurrentColor.B:X2}";
        ThicknessButton.ToolTip = $"切换线条粗细  {_annotationController.CurrentThickness:0.#} px";
        FontSizeButton.ToolTip = $"切换文字大小  {_annotationController.CurrentFontSize:0}";
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => _annotationController.Undo();

    private void OnRedoClick(object sender, RoutedEventArgs e) => _annotationController.Redo();

    private void OnUndoRedoMenuOpened(object sender, RoutedEventArgs e)
    {
        UndoMenuItem.IsEnabled = _annotationController.CanUndo;
        RedoMenuItem.IsEnabled = _annotationController.CanRedo;
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _annotationController.Clear();

    private void OnOcrClick(object sender, RoutedEventArgs e) => RequestAction(CaptureOverlayAction.Ocr);

    private async void OnQrCodeClick(object sender, RoutedEventArgs e)
    {
        if (_qrCodeService is null || _clipboardService is null ||
            _selectionMode != SelectionMode.Free ||
            _selections.Count != 1 ||
            GetLocalRegion() is not { } localRegion || _selection.IsEmpty)
        {
            ShowAdjustmentBadge(Mouse.GetPosition(Root), "二维码识别暂不可用");
            return;
        }

        CancelQrCodeRecognition(clearResults: true);
        var cancellation = new CancellationTokenSource();
        _qrCodeCancellation = cancellation;
        var image = CreateSelectedImage();
        ShowAdjustmentBadge(Mouse.GetPosition(Root), "正在识别二维码…");
        try
        {
            var results = await _qrCodeService.RecognizeAsync(image, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_qrCodeCancellation, cancellation))
            {
                return;
            }

            if (results.Count == 0)
            {
                ShowAdjustmentBadge(Mouse.GetPosition(Root), "未检测到二维码");
                return;
            }

            QrCodeHotspotLayer.Width = _selection.Width;
            QrCodeHotspotLayer.Height = _selection.Height;
            Canvas.SetLeft(QrCodeHotspotLayer, _selection.Left);
            Canvas.SetTop(QrCodeHotspotLayer, _selection.Top);
            QrCodeHotspotLayer.ShowResults(
                results,
                image.PixelWidth,
                image.PixelHeight,
                Stretch.Fill);
            ShowAdjustmentBadge(
                Mouse.GetPosition(Root),
                $"已找到 {results.Count:N0} 个二维码 · 悬停查看，单击使用");
            DiagnosticLog.Info("QrCode", $"截图选区二维码热点已显示：{results.Count:N0} 个结果，区域 {localRegion.Width}x{localRegion.Height}。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("QrCode", exception, "截图选区二维码识别失败。");
            ShowAdjustmentBadge(Mouse.GetPosition(Root), "二维码识别失败，请换一张清晰图片重试");
        }
        finally
        {
            if (ReferenceEquals(_qrCodeCancellation, cancellation))
            {
                _qrCodeCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async void OnQrCodeHotspotInvoked(QrCodeResult result)
    {
        if (_clipboardService is null)
        {
            return;
        }

        try
        {
            var message = await QrCodeInteractionService.InvokeAsync(result, _clipboardService);
            if (result.IsUrl)
            {
                SelectionCancelled?.Invoke(this, EventArgs.Empty);
                return;
            }

            ShowAdjustmentBadge(Mouse.GetPosition(Root), message);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("QrCode", exception, "执行截图选区二维码热点操作失败。");
            ShowAdjustmentBadge(Mouse.GetPosition(Root), result.IsUrl ? "无法打开此链接" : "复制二维码内容失败");
        }
    }

    private void CancelQrCodeRecognition(bool clearResults)
    {
        _qrCodeCancellation?.Cancel();
        _qrCodeCancellation?.Dispose();
        _qrCodeCancellation = null;
        if (clearResults)
        {
            QrCodeHotspotLayer.ClearResults();
        }
    }

    private void OnOcrMenuClick(object sender, RoutedEventArgs e)
    {
        if (OcrButton.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = OcrButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void OnPinClick(object sender, RoutedEventArgs e) => RequestAction(CaptureOverlayAction.Pin);

    private async void OnQuickTagClick(object sender, RoutedEventArgs e)
    {
        if (!_settings.ShowQuickCaptureTags)
        {
            return;
        }

        if (QuickTagPopup.IsOpen)
        {
            QuickTagPopup.IsOpen = false;
            return;
        }

        SelectionListPopup.IsOpen = false;
        QuickTagPopup.PlacementTarget = QuickTagButton;
        QuickTagCreatePanel.Visibility = Visibility.Collapsed;
        QuickTagStatusText.Text = _quickTagsLoaded ? string.Empty : "正在读取已有标签…";
        QuickTagStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(127, 147, 157));
        QuickTagStatusText.Visibility = _quickTagsLoaded ? Visibility.Collapsed : Visibility.Visible;
        RenderQuickTagButtons();
        QuickTagPopup.IsOpen = true;

        if (_quickTagsLoaded || _loadTagsAsync is null)
        {
            _quickTagsLoaded = true;
            QuickTagStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            _availableQuickTags = (await _loadTagsAsync(CancellationToken.None))
                .Select(HistoryMetadataStore.NormalizeTagName)
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            _quickTagsLoaded = true;
            QuickTagStatusText.Visibility = Visibility.Collapsed;
            RenderQuickTagButtons();
        }
        catch (Exception exception)
        {
            _quickTagsLoaded = true;
            QuickTagStatusText.Text = "已有标签暂时无法读取，仍可添加新标签。";
            QuickTagStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 145, 137));
            QuickTagStatusText.Visibility = Visibility.Visible;
            DiagnosticLog.Error("HistoryTags", exception, "截图时读取快速标签失败。");
        }
    }

    private void RenderQuickTagButtons()
    {
        QuickTagItemsPanel.Children.Clear();
        foreach (var tag in _availableQuickTags
                     .Concat(_selectedQuickTags)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase))
        {
            var chip = new WpfToggleButton
            {
                Content = tag,
                Tag = tag,
                IsChecked = _selectedQuickTags.Contains(tag),
                Style = (Style)FindResource("QuickTagChip"),
                ToolTip = _selectedQuickTags.Contains(tag) ? "点击移除本次标签" : "点击添加到本次截图"
            };
            chip.Click += OnQuickTagChipClick;
            QuickTagItemsPanel.Children.Add(chip);
        }

        var addButton = new WpfToggleButton
        {
            Content = "＋ 添加标签",
            Style = (Style)FindResource("QuickTagChip"),
            Foreground = new SolidColorBrush(MediaColor.FromRgb(118, 223, 238)),
            ToolTip = "创建并选中一个新标签"
        };
        addButton.Click += OnShowQuickTagCreateClick;
        QuickTagItemsPanel.Children.Add(addButton);
        UpdateQuickTagSummary();
    }

    private void OnQuickTagChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfToggleButton { Tag: string tag } chip)
        {
            return;
        }

        if (chip.IsChecked == true)
        {
            _selectedQuickTags.Add(tag);
            chip.ToolTip = "点击移除本次标签";
        }
        else
        {
            _selectedQuickTags.Remove(tag);
            chip.ToolTip = "点击添加到本次截图";
        }

        UpdateQuickTagSummary();
    }

    private void OnShowQuickTagCreateClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfToggleButton toggle)
        {
            toggle.IsChecked = false;
        }

        QuickTagStatusText.Visibility = Visibility.Collapsed;
        QuickTagCreatePanel.Visibility = Visibility.Visible;
        QuickTagTextBox.Clear();
        QuickTagTextBox.Focus();
        Keyboard.Focus(QuickTagTextBox);
    }

    private void OnAddQuickTagConfirmClick(object sender, RoutedEventArgs e) => AddQuickTagFromTextBox();

    private void OnQuickTagTextBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddQuickTagFromTextBox();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            QuickTagCreatePanel.Visibility = Visibility.Collapsed;
            QuickTagButton.Focus();
            e.Handled = true;
        }
    }

    private void AddQuickTagFromTextBox()
    {
        var tag = HistoryMetadataStore.NormalizeTagName(QuickTagTextBox.Text);
        if (tag.Length == 0)
        {
            QuickTagStatusText.Text = "请输入标签名称。";
            QuickTagStatusText.Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 145, 137));
            QuickTagStatusText.Visibility = Visibility.Visible;
            QuickTagTextBox.Focus();
            return;
        }

        _selectedQuickTags.Add(tag);
        _availableQuickTags = _availableQuickTags
            .Append(tag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        QuickTagCreatePanel.Visibility = Visibility.Collapsed;
        QuickTagStatusText.Visibility = Visibility.Collapsed;
        RenderQuickTagButtons();
    }

    private void UpdateQuickTagSummary()
    {
        var count = _selectedQuickTags.Count;
        QuickTagSummaryText.Text = count == 0 ? "未选择" : $"已选 {count}";
        QuickTagCountText.Text = count > 99 ? "99+" : count.ToString();
        QuickTagCountBadge.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        QuickTagButton.ToolTip = count == 0
            ? "为本次截图添加标签"
            : $"本次截图标签：{string.Join("、", SelectedTags)}";
    }

    private void OnQuickTagPopupClosed(object? sender, EventArgs e)
    {
        QuickTagCreatePanel.Visibility = Visibility.Collapsed;
        ConfigureNativeIme(false);
        Focus();
        Keyboard.Focus(this);
    }

    private void OnCaptureToolbarPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (QuickTagPopup.IsOpen && !QuickTagButton.IsMouseOver)
        {
            QuickTagPopup.IsOpen = false;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => RequestAction(CaptureOverlayAction.Copy);

    private void OnSaveClick(object sender, RoutedEventArgs e) => RequestAction(CaptureOverlayAction.Save);

    private void OnConfirmClick(object sender, RoutedEventArgs e) => ConfirmSelection();

    private void OnCloseCaptureClick(object sender, RoutedEventArgs e) => CancelSelection();

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        if (_presetStore is null || CreateGlobalSelectionSet() is not { } selection)
        {
            return;
        }

        var name = $"选区 {_presetStore.LoadAll().Count + 1}";
        var preset = _presetStore.Add(name, selection);
        ShowAdjustmentBadge(
            Mouse.GetPosition(Root),
            $"已保存命名选区：{preset.Name}（可在管理窗口重命名）");
    }

    private void OnSelectionListClick(object sender, RoutedEventArgs e)
    {
        if (_selections.Count == 0)
        {
            return;
        }

        if (SelectionListPopup.IsOpen)
        {
            SelectionListPopup.IsOpen = false;
            return;
        }

        QuickTagPopup.IsOpen = false;
        RebuildSelectionList();
        SelectionListPopup.PlacementTarget = SelectionListButton;
        SelectionListPopup.IsOpen = true;
    }

    private void RebuildSelectionList()
    {
        SelectionListPanel.Children.Clear();
        SelectionListSummaryText.Text = $"{_selections.Count} 个";
        for (var index = 0; index < _selections.Count; index++)
        {
            var item = _selections[index];
            var active = index == _activeSelectionIndex;
            var row = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(0, 0, 0, 4)
            };
            var delete = new WpfButton
            {
                Content = "×",
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "删除该选区",
                Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 113, 128)),
                Background = new SolidColorBrush(MediaColor.FromRgb(22, 36, 46)),
                BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x55, 0x76, 0xDF, 0xEE)),
                BorderThickness = new Thickness(1),
                Cursor = WpfCursors.Hand
            };
            delete.Click += (_, _) => RemoveSelectionAt(index);
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);

            var activate = new Border
            {
                Background = new SolidColorBrush(active
                    ? MediaColor.FromRgb(44, 76, 88)
                    : MediaColor.FromRgb(16, 29, 37)),
                BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x55, 0x76, 0xDF, 0xEE)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 5, 8, 5),
                Cursor = WpfCursors.Hand,
                Child = new TextBlock
                {
                    FontFamily = FindResource("DataFont") as System.Windows.Media.FontFamily
                        ?? new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(active
                        ? MediaColor.FromRgb(118, 223, 238)
                        : MediaColor.FromRgb(197, 210, 216)),
                    Text = $"#{index + 1}   {item.Pixels.Width} × {item.Pixels.Height}   ({item.Pixels.X}, {item.Pixels.Y})"
                }
            };
            activate.MouseLeftButtonDown += (_, args) =>
            {
                ActivateSelection(index);
                args.Handled = true;
            };
            row.Children.Add(activate);
            SelectionListPanel.Children.Add(row);
        }
    }

    private void RemoveSelectionAt(int index)
    {
        if (index < 0 || index >= _selections.Count)
        {
            return;
        }

        _activeSelectionIndex = index;
        RemoveActiveSelection();
    }

    private void OnToolbarSizeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var region = GetLocalRegion();
        if (region is null)
        {
            return;
        }

        UpdateGeometryFields(region.Value);
        UpdateAspectRatioButtons();
        UpdateFixedSizeUi();
        GeometryEditor.Visibility = Visibility.Visible;
        MagnifierOverlay.Visibility = Visibility.Collapsed;
        GeometryXBox.Focus();
        e.Handled = true;
    }

    private void OnAspectRatioClick(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string value } ||
            !Enum.TryParse<CaptureAspectRatioMode>(value, out var mode))
        {
            return;
        }

        SetAspectRatioMode(mode, adjustSelection: true);
        if (GetLocalRegion() is { } region)
        {
            UpdateGeometryFields(region);
        }
        UpdateFixedSizeUi(_lockedSelectionSize is not null
            ? "固定尺寸优先；比例将在修改 W/H 或解锁后继续生效"
            : null);
    }

    private void OnGeometryDimensionTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingGeometryFields)
        {
            return;
        }

        if (_lockedAspectRatio is { } ratio)
        {
            if (sender == GeometryWidthBox &&
                int.TryParse(GeometryWidthBox.Text, out var width) && width > 0)
            {
                _geometryWidthIsPrimary = true;
                _updatingGeometryFields = true;
                GeometryHeightBox.Text = Math.Max(
                    1,
                    (int)Math.Round(width / ratio, MidpointRounding.AwayFromZero)).ToString();
                _updatingGeometryFields = false;
            }
            else if (sender == GeometryHeightBox &&
                     int.TryParse(GeometryHeightBox.Text, out var height) && height > 0)
            {
                _geometryWidthIsPrimary = false;
                _updatingGeometryFields = true;
                GeometryWidthBox.Text = Math.Max(
                    1,
                    (int)Math.Round(height * ratio, MidpointRounding.AwayFromZero)).ToString();
                _updatingGeometryFields = false;
            }
        }

        if (_lockedSelectionSize is not null)
        {
            UpdateLockedSizeFromGeometryFields();
        }
    }

    private void OnFixedSizeLockClick(object sender, RoutedEventArgs e)
    {
        if (_lockedSelectionSize is not null)
        {
            _lockedSelectionSize = null;
            UpdateFixedSizeUi("尺寸已解锁，可以重新拖动边和角缩放");
            if (!_selection.IsEmpty)
            {
                UpdateCornerMarks(_selection);
                UpdateSizeBadge(_selection);
            }
            return;
        }

        if (!int.TryParse(GeometryWidthBox.Text, out var width) || width <= 0 ||
            !int.TryParse(GeometryHeightBox.Text, out var height) || height <= 0)
        {
            UpdateFixedSizeUi("请输入有效的宽度和高度", warning: true);
            return;
        }

        var current = GetLocalRegion() ?? DrawingRectangle.Empty;
        var x = int.TryParse(GeometryXBox.Text, out var inputX) ? inputX : current.X;
        var y = int.TryParse(GeometryYBox.Text, out var inputY) ? inputY : current.Y;
        ApplyFixedSize(new System.Drawing.Point(x, y), new DrawingSize(width, height));
    }

    private void OnGeometryCancelClick(object sender, RoutedEventArgs e)
    {
        GeometryEditor.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void OnGeometryApplyClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(GeometryXBox.Text, out var x) ||
            !int.TryParse(GeometryYBox.Text, out var y) ||
            !int.TryParse(GeometryWidthBox.Text, out var width) ||
            !int.TryParse(GeometryHeightBox.Text, out var height) ||
            width <= 0 || height <= 0)
        {
            return;
        }

        x = Math.Clamp(x, 0, _snapshot.Bounds.Width - 1);
        y = Math.Clamp(y, 0, _snapshot.Bounds.Height - 1);
        if (_lockedSelectionSize is not null)
        {
            var requestedSize = new DrawingSize(width, height);
            var placed = CaptureFixedSizeConstraint.Place(
                new System.Drawing.Point(x, y),
                requestedSize,
                CapturePixelBounds);
            _lockedSelectionSize = placed.Size;
            SetSelectionFromPixels(placed);
            GeometryEditor.Visibility = Visibility.Collapsed;
            Focus();
            return;
        }

        var maxWidth = _snapshot.Bounds.Width - x;
        var maxHeight = _snapshot.Bounds.Height - y;
        if (_lockedAspectRatio is { } ratio)
        {
            var constrained = CaptureAspectRatioConstraint.ConstrainSize(
                width,
                height,
                ratio,
                _geometryWidthIsPrimary,
                maxWidth,
                maxHeight);
            width = constrained.Width;
            height = constrained.Height;
        }
        else
        {
            width = Math.Clamp(width, 1, maxWidth);
            height = Math.Clamp(height, 1, maxHeight);
        }

        SetSelectionFromPixels(new DrawingRectangle(x, y, width, height));
        GeometryEditor.Visibility = Visibility.Collapsed;
        Focus();
    }

    private void UpdateGeometryFields(DrawingRectangle region)
    {
        _updatingGeometryFields = true;
        GeometryXBox.Text = region.X.ToString();
        GeometryYBox.Text = region.Y.ToString();
        GeometryWidthBox.Text = region.Width.ToString();
        GeometryHeightBox.Text = region.Height.ToString();
        _updatingGeometryFields = false;
        _geometryWidthIsPrimary = true;
    }

    private void UpdateLockedSizeFromGeometryFields()
    {
        if (!int.TryParse(GeometryWidthBox.Text, out var width) || width <= 0 ||
            !int.TryParse(GeometryHeightBox.Text, out var height) || height <= 0 ||
            GetLocalRegion() is not { } current)
        {
            return;
        }

        ApplyFixedSize(current.Location, new DrawingSize(width, height), preserveDimensionEditing: true);
    }

    private void ApplyFixedSize(
        System.Drawing.Point location,
        DrawingSize requestedSize,
        bool preserveDimensionEditing = false)
    {
        var limitedSize = CaptureFixedSizeConstraint.LimitSize(requestedSize, CapturePixelBounds);
        var placed = CaptureFixedSizeConstraint.Place(location, limitedSize, CapturePixelBounds);
        _lockedSelectionSize = limitedSize;
        SetSelectionFromPixels(placed);
        if (preserveDimensionEditing)
        {
            _updatingGeometryFields = true;
            GeometryXBox.Text = placed.X.ToString();
            GeometryYBox.Text = placed.Y.ToString();
            if (limitedSize != requestedSize)
            {
                GeometryWidthBox.Text = limitedSize.Width.ToString();
                GeometryHeightBox.Text = limitedSize.Height.ToString();
            }
            _updatingGeometryFields = false;
        }
        else
        {
            UpdateGeometryFields(placed);
        }
        if (limitedSize != requestedSize)
        {
            UpdateFixedSizeUi(
                $"尺寸超过当前截图边界，已限制为 {limitedSize.Width} × {limitedSize.Height}",
                warning: true);
        }
        else
        {
            UpdateFixedSizeUi($"已锁定 {limitedSize.Width} × {limitedSize.Height} px；选区现在只能移动");
        }
    }

    private void UpdateFixedSizeUi(string? message = null, bool warning = false)
    {
        if (FixedSizeLockButton is null || FixedSizeLockIcon is null || GeometryHintText is null)
        {
            return;
        }

        var locked = _lockedSelectionSize is not null;
        FixedSizeLockIcon.Kind = locked ? QingSnapIconKind.Lock : QingSnapIconKind.Unlock;
        FixedSizeLockButton.ToolTip = locked
            ? $"已锁定 {_lockedSelectionSize!.Value.Width} × {_lockedSelectionSize.Value.Height} px；点击解锁"
            : "锁定当前宽度和高度";
        FixedSizeLockButton.Background = locked
            ? new SolidColorBrush(MediaColor.FromRgb(44, 76, 88))
            : System.Windows.Media.Brushes.Transparent;
        FixedSizeLockButton.Foreground = locked
            ? new SolidColorBrush(MediaColor.FromRgb(118, 223, 238))
            : new SolidColorBrush(MediaColor.FromRgb(220, 231, 236));
        GeometryHintText.Foreground = warning
            ? new SolidColorBrush(MediaColor.FromRgb(255, 190, 92))
            : new SolidColorBrush(MediaColor.FromRgb(118, 161, 177));
        GeometryHintText.Text = message ?? (locked
            ? $"已锁定 {_lockedSelectionSize!.Value.Width} × {_lockedSelectionSize.Value.Height} px；选区只能移动"
            : "Shift + 角点临时锁定比例；Alt + 角点以中心缩放；两者可组合");
    }

    private DrawingRectangle CapturePixelBounds =>
        new(0, 0, _snapshot.Bounds.Width, _snapshot.Bounds.Height);

    private int EffectivePadding =>
        Math.Clamp(_selectionPaddingOverride ?? _settings.MultiRegionPadding, 0, 200);

    private void UpdateAspectRatioButtons()
    {
        WpfButton[] buttons =
        [
            AspectFreeButton,
            AspectSquareButton,
            AspectFourThreeButton,
            AspectThreeTwoButton,
            AspectSixteenNineButton,
            AspectNineSixteenButton,
            AspectCurrentButton
        ];
        foreach (var button in buttons)
        {
            var active = button.Tag is string value &&
                         Enum.TryParse<CaptureAspectRatioMode>(value, out var mode) &&
                         mode == _aspectRatioMode;
            button.Background = active
                ? new SolidColorBrush(MediaColor.FromRgb(44, 76, 88))
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = active
                ? new SolidColorBrush(MediaColor.FromRgb(118, 223, 238))
                : new SolidColorBrush(MediaColor.FromRgb(197, 210, 216));
        }
    }

    private void CancelSelection() => SelectionCancelled?.Invoke(this, EventArgs.Empty);

    private bool UsesCloseButton => string.Equals(
        _settings.CloseInteraction,
        "Button",
        StringComparison.OrdinalIgnoreCase);

    private void SetAnnotationTool(CaptureAnnotationTool tool)
    {
        _annotationController.CommitText();
        if (tool != CaptureAnnotationTool.Select)
        {
            _annotationController.ClearSelection();
        }
        _annotationController.ActiveTool = tool;
        if (tool == CaptureAnnotationTool.None)
        {
            AnnotationPalettePopup.IsOpen = false;
            RegionToolPopup.IsOpen = false;
            ArrowToolPopup.IsOpen = false;
        }
        Surface.Cursor = tool == CaptureAnnotationTool.None
            ? WpfCursors.Cross
            : CursorForAnnotationTool(tool);
        UpdateAnnotationButtons();
    }

    private void UpdateAnnotationButtons()
    {
        var inactive = new SolidColorBrush(Colors.Transparent);
        var active = new SolidColorBrush(MediaColor.FromRgb(69, 42, 48));
        (WpfButton Button, CaptureAnnotationTool Tool)[] buttons =
        [
            (PenButton, CaptureAnnotationTool.Pen),
            (EllipseButton, CaptureAnnotationTool.Ellipse),
            (TextButton, CaptureAnnotationTool.Text),
            (NumberButton, CaptureAnnotationTool.Number),
            (SelectButton, CaptureAnnotationTool.Select)
        ];
        foreach (var (button, tool) in buttons)
        {
            button.Background = _annotationController.ActiveTool == tool ? active : inactive;
        }

        LineButton.Background = _annotationController.ActiveTool == CaptureAnnotationTool.Line ? active : inactive;
        ArrowButton.Background = _annotationController.ActiveTool is
            CaptureAnnotationTool.Arrow or CaptureAnnotationTool.DoubleArrow ? active : inactive;
        RectangleButton.Background = _annotationController.ActiveTool is
            CaptureAnnotationTool.Rectangle or CaptureAnnotationTool.Mosaic or
            CaptureAnnotationTool.Highlight or CaptureAnnotationTool.Blur
            ? active
            : inactive;

        UndoButton.IsEnabled = _annotationController.CanUndo;
        ClearButton.IsEnabled = _annotationController.HasAnnotations;
    }

    private Point ClampPoint(Point point) => new(
        Math.Clamp(point.X, 0, Surface.ActualWidth),
        Math.Clamp(point.Y, 0, Surface.ActualHeight));

    private System.Windows.Input.Cursor CursorForPointer(Point point)
    {
        var captureHit = HitTest(point);
        if (_annotationController.ActiveTool == CaptureAnnotationTool.Select &&
            captureHit is not (DragMode.None or DragMode.Create or DragMode.Move))
        {
            return CursorFor(captureHit);
        }

        if (_annotationController.ActiveTool != CaptureAnnotationTool.None && _selection.Contains(point))
        {
            if (_annotationController.ActiveTool == CaptureAnnotationTool.Select)
            {
                return _annotationController.GetSelectionCursorAt(point);
            }

            return CursorForAnnotationTool(_annotationController.ActiveTool);
        }

        return CursorFor(captureHit);
    }

    private static System.Windows.Input.Cursor CursorForAnnotationTool(CaptureAnnotationTool tool) =>
        tool == CaptureAnnotationTool.Select ? WpfCursors.SizeAll : WpfCursors.Cross;

    private static System.Windows.Input.Cursor CursorFor(DragMode mode) => mode switch
    {
        DragMode.Move => WpfCursors.SizeAll,
        DragMode.Left or DragMode.Right => WpfCursors.SizeWE,
        DragMode.Top or DragMode.Bottom => WpfCursors.SizeNS,
        DragMode.TopLeft or DragMode.BottomRight => WpfCursors.SizeNWSE,
        DragMode.TopRight or DragMode.BottomLeft => WpfCursors.SizeNESW,
        _ => WpfCursors.Cross
    };

    private static void SetPosition(FrameworkElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }

    private enum DragMode
    {
        None,
        Create,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
}

public sealed class PreviousSelectionRequestedEventArgs(int historyIndex) : EventArgs
{
    public int HistoryIndex { get; } = historyIndex;
}
