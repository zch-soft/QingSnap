# 截图智能窗口选择设计说明

本文记录 QingSnap 截图覆盖层里“智能窗口选择”的实现思路、规则、参数和踩过的坑，方便以后改这块逻辑时快速理解。

相关代码：

- `src/QingSnap.App/Services/UiAutomationWindowProbe.cs` —— UIA 元素识别规则
- `src/QingSnap.App/Views/CaptureOverlayWindow.xaml.cs` —— 快速预览、节流、吸附/保持、点击校正
- `src/QingSnap.App/Infrastructure/NativeMethods.cs` —— 窗口命中、cloaked/透明窗口过滤、覆盖层穿透
- `tests/QingSnap.Tests/UiAutomationWindowProbeTests.cs` —— 工具栏/区块形状规则测试
- `tests/QingSnap.Tests/CaptureOverlayMatrixTests.cs` —— 覆盖层蒙版、选区列表、命中测试等集成测试

## 1. 目标行为

鼠标悬停时给出一个“可点击采纳”的高亮预览；点击后按该预览创建选区。期望的粒度是“有意义的窗口/控件”：

- 鼠标下面是**子窗口**（例如 HwndHost 内容区、工具栏子窗口）→ 选该子窗口；
- 鼠标下面是**工具栏** → 选整条工具栏；
- 鼠标下面是停靠面板里的**内容区块**（例如 Diagnostic Tools 的上下两部分、Solution Explorer 的树）→ 选该区块；
- 鼠标下面是面板**标题栏、标签栏或空白** → 选整个停靠面板；
- 其他情况 → 选 HWND 命中的普通窗口。

## 2. 两条路径

智能预览由“快路径 + 慢路径”组成：

1. **快路径（每次鼠标移动）**
   - 用 `NativeMethods.FindWindowAtPointExcludingProcess` 做 HWND 命中，立即显示窗口内容子区域。
   - 结果只依赖窗口层级，速度快，但无法识别没有独立 HWND 的 VS 停靠面板/工具栏。
2. **慢路径（节流后的 UIA 校正）**
   - 每 400ms 最多执行一次 UIA 元素识别，把预览升级为工具栏/区块/整个面板。
   - 结果在鼠标停留或缓慢移动时保持；不会每次 MouseMove 都覆盖回快路径。

## 3. 覆盖层侧流程

入口：`CaptureOverlayWindow.UpdateSmartPreview(Point point)`（仅在 `SmartWindowSelection` 开启、未拖拽、未标注、无弹窗、鼠标不在工具栏上时调用）。

1. **坐标换算**：DIP → 快照物理像素。
2. **快路径命中**：
   - `FindWindowAtPointExcludingProcess` 会临时给覆盖层加 `WS_EX_TRANSPARENT`（覆盖层是分层窗口），让系统命中测试穿透覆盖层；
   - 排除本进程窗口；跳过 DWM cloaked 窗口、`WS_EX_TRANSPARENT` 窗口、常亮 alpha 为 0 的 layered 窗口（Click to Do、输入法/显卡全屏透明 HUD 等）。
3. 计算 `root = GetAncestor(target, GA_ROOT)`，并记录：
   - `_smartPreviewFallbackWindow = root`（UIA 校正时从这个真实窗口向下扫描）；
   - `_smartPreviewOverChildWindow = target != root`（鼠标下面是否有子窗口）。
4. **保持规则（sticky）**：如果当前预览已经是一个 UIA 元素覆盖，并且鼠标仍在它内部：
   - 不在子窗口上 → 保持；
   - 在子窗口上，但当前覆盖是**工具栏/区块** → 保持；
   - 在子窗口上，当前覆盖是**整个面板** → 让子窗口获胜（内容区优先于整个面板）。
5. 应用快路径窗口矩形，立即显示预览。
6. 启动 `_smartPreviewProbeTimer`（120ms，只在未启动时启动，不做防抖重置）。
7. 定时器触发：重新检查状态后调用 `TryRefineSmartPreviewToElement(pointer)`。
8. `GetPreviewOverrideAtPoint(root, x, y, allowDockPane: !overChild)` 执行 UIA 识别（内部 400ms 节流）。
9. 命中结果按 `PreviewOverride(Bounds, IsDockPane)` 应用：
   - `IsDockPane=false`：工具栏/区块；
   - `IsDockPane=true`：整个停靠面板。
10. **点击采纳**：`OnMouseLeftButtonDown` 里采纳智能预览前会同步再调用一次 `TryRefineSmartPreviewToElement`，保证“移动后立刻点击”也能点中面板/工具栏/区块，而不是尚未校正的内容子窗口。

## 4. UIA 通用规则

全部规则只使用 UIA 的**控制类型、层级和矩形**，不匹配任何应用专有类名，也没有硬编码像素布局。

### 4.1 找停靠面板

`FindPaneContainer`：

- 从真实顶层窗口开始，用 `ControlViewWalker` 按矩形向下找；
- 候选条件是：`ControlType.Pane`，且**直接子元素**里有 `TitleBar` 或 `Tab`；
- 取第一个（最外层）候选作为面板；找不到则把顶层窗口当作扫描根。

这样可以把“带标题/标签页的停靠面板”和普通的 `DockRoot`、内容宿主区分开。

### 4.2 扫描面板子树

`ScanAll`：

- 从面板（或顶层窗口）开始，使用 **`RawViewWalker`** 递归扫描；
- **不按祖先矩形剪枝**：VS 会给出不可靠的父元素矩形（例如某些 `TabItem` 被报告在面板底部），按祖先裁剪会漏掉整棵子树；
- 深度上限 22，访问元素上限 400；
- 只对**自身矩形包含鼠标点**的元素做候选判断。

### 4.3 工具栏

优先级从高到低：

1. `ControlType.ToolBar`：命中即工具栏（取最先遇到的，即外层工具栏）。
2. 形状规则（用于内容内部的工具栏，例如 WebView 里的工具栏条）：
   - 宽度 ≥ 面板宽度的 70%；
   - 高度 ≤ 面板高度的 8%；
   - 顶部位于面板顶部 8% 范围内；
   - 排除 `Text`、`Button`、`TitleBar`、`TabItem`、`Tab`；
   - 取满足条件的**最深/最小面积**元素，避免选到包住工具栏的 header 块。

### 4.4 内容区块

- 元素面积占面板面积的比例在 **35%–90%** 之间；
- 排除 `TitleBar`、`TabItem`；
- 取满足条件的**最深**元素；
- 90% 上限用于排除“整个内容宿主/整个标签组”，让上下分栏（如 Diagnostic Tools 的 swimlanes 与 Summary/Events）能分别命中。

### 4.5 结果优先级

```text
ToolBar 类型 > 形状工具栏 > 内容区块 > 整个停靠面板（仅 allowDockPane=true）
```

`allowDockPane` 由“鼠标下面是否有子窗口”决定：

- 有子窗口（内容/工具栏子窗口）→ `false`，只允许工具栏/区块覆盖，不允许升级成整个面板；
- 没有子窗口（标题栏/标签栏/空白）→ `true`，允许整个面板。

## 5. 关键参数

`UiAutomationWindowProbe`：

| 常量 | 值 | 含义 |
|---|---|---|
| `QueryThrottleMilliseconds` | 400 | UIA 校正最小间隔 |
| `MaxDepth` | 22 | 子树扫描深度上限 |
| `MaxVisitedElements` | 400 | 子树扫描元素上限 |
| `SectionAreaRatio` | 0.35 | 区块最小面积比例 |
| `SectionMaxAreaRatio` | 0.90 | 区块最大面积比例 |
| `ToolbarMinWidthRatio` | 0.70 | 形状工具栏最小宽度比例 |
| `ToolbarMaxHeightRatio` | 0.08 | 形状工具栏最大高度比例 |
| `ToolbarTopRatio` | 0.08 | 形状工具栏允许的顶部范围 |

`CaptureOverlayWindow`：

| 字段 | 值 | 含义 |
|---|---|---|
| `_smartPreviewProbeTimer.Interval` | 120ms | 尝试执行 UIA 校正的节奏；真正节流在探针内部（400ms） |

调参建议：

- 区块切得太碎/太小 → 提高 `SectionAreaRatio`；区块识别不到 → 降低；
- 上下分栏被合并成整体 → 降低 `SectionMaxAreaRatio`；
- 内容内工具栏识别不到 → 放宽 `ToolbarMaxHeightRatio`/`ToolbarTopRatio`；
- 普通控件被误当成工具栏 → 收紧宽度/高度比例，或排除更多控制类型；
- 首次进入面板卡顿明显 → 增大 `QueryThrottleMilliseconds`、降低 `MaxVisitedElements`，或把扫描移到后台线程（当前是同步 UI 线程扫描）。

## 6. 已踩过的坑（历史原因）

1. **全屏隐形窗口抢命中**：Windows 11 Click to Do、输入法/显卡助手会留下全屏、topmost、cloaked 或全透明的 layered 窗口，旧的 `EnumWindows` 会把它们当成“整个桌面”。现在快路径改用 `WindowFromPoint` + 覆盖层临时穿透，并过滤 cloaked / `WS_EX_TRANSPARENT` / alpha=0 的窗口。
2. **VS 停靠面板没有独立 HWND**：Solution Explorer、Diagnostic Tools 的面板是主窗口内的 UIA 元素；只靠 HWND 只能选到内容子窗口或整个 VS 窗口。因此引入 UIA 校正。
3. **调试会话里 `AutomationElement.FromPoint` 不可靠**：在 VS 调试/XAML 诊断附加时，同进程的 `FromPoint` 可能返回宿主窗口或覆盖层自身。最终方案不再用它，而是从真实窗口的 HWND 向下扫描（`_smartPreviewFallbackWindow`）。
4. **VS 父元素矩形不可靠**：部分 `TabItem` 的 UIA 矩形不在其子元素附近，按祖先矩形剪枝会漏掉工具栏/内容。`ScanAll` 因此不剪枝，只判断元素自身矩形。
5. **防抖不触发**：早期用“鼠标停稳 120ms 才校正”，连续移动时计时器不断重启，从未执行；现在改为“未启动才启动”的节流。
6. **校正结果被快路径覆盖**：校正后的一次微小移动会立刻被 HWND 快速预览覆盖回内容子窗口；现在用 sticky 保持，并在点击采纳前同步再校正一次。
7. **VS 专有类名/几何特判已移除**：`ViewPresenter`、`ToolWindowTabGroupContainer`、`ScriptedControl`、`toolbar-container` 以及 Solution Explorer 的固定高度切分都已删除，改为纯 UIA 类型/形状规则，避免绑定某个 VS 版本的布局。

## 7. 已知限制

- 识别依赖目标应用的 UIA provider；某些应用/VS 状态下内容根本不暴露给 UIA（例如部分状态下的 Solution Explorer 树）。此时会回退到整个面板，这是通用规则的预期代价，不再做窗口专用兜底。
- 面板子树扫描是同步的，首次进入新面板可能有约 0.2–0.3 秒停顿；停留/小幅移动时由 sticky 避免重复扫描。
- 形状阈值是启发式的；新应用/新布局可能需要在第 5 节调参。
- WebView/Chrome 内容（如 Diagnostic Tools 的图表区）在 `ControlView`/`RawView` 中的暴露程度随版本可能变化；如果工具栏/分栏识别不到，优先检查该元素在 UIA 树中的类型和矩形。
- 覆盖层必须是分层窗口（`AllowsTransparency=True`），否则 `WS_EX_TRANSPARENT` 不能让 `WindowFromPoint` 穿透它。

## 8. 验证方式

1. 单元测试：

   ```bash
   dotnet test tests/QingSnap.Tests/QingSnap.Tests.csproj
   ```

   `UiAutomationWindowProbeTests` 覆盖工具栏/区块的形状规则；`CaptureOverlayMatrixTests` 覆盖覆盖层蒙版、工具栏位置、选区列表、cloaked 窗口和穿透命中。

2. 人工验证（Visual Studio）：

   - 打开 Solution Explorer / Diagnostic Tools；
   - 按 F1，分别悬停标题栏、工具栏、内容区、上下分栏；
   - 确认预览粒度符合第 1 节；
   - 移动后立即点击，确认点击结果与预览一致。

3. 排查手段：如需临时观察，可在 `UiAutomationWindowProbe` / `TryRefineSmartPreviewToElement` 里加 `DiagnosticLog`，日志在 `%LOCALAPPDATA%\QingSnap\Logs\QingSnap.log`（portable 模式也不会放到 Data 目录）。正式代码中不保留这些日志。
