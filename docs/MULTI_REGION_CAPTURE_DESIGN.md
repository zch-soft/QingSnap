# QingSnap 多选区截图设计

- 状态：方案稿（未实现）
- 日期：2026-10-25
- 适用基线：当前 `fix/selection-move-jitter` 分支
- 相关代码：[CaptureOverlayWindow.xaml.cs](../src/QingSnap.App/Views/CaptureOverlayWindow.xaml.cs)、[CaptureCoordinator.cs](../src/QingSnap.App/Services/CaptureCoordinator.cs)、[ClipboardService.cs](../src/QingSnap.App/Services/ClipboardService.cs)

## 1. 背景与目标

当前一次截图只能有一个选区：覆盖层的 `_selection` / `_selectionPixelRegion` 是单值字段，输出是一次 `CroppedBitmap`，下游 `CaptureRegion`、`CaptureResult`、剪贴板私有格式也都只承载一个矩形。

目标是让一次截图可以定义多个区域，支持两种定义方式和两种输出方式：

- **自由多选区**：用户手动框选多个区域。
- **矩阵选区**：先定义一个外框，再指定行、列、行间隔、列间隔，实时预览。
- **单图输出**：所有选区的外接矩形，未选中部分透明，可配置向外扩展。
- **多图输出**：每个选区输出一个文件。
- **命名选区历史**：可命名、可增删改、不限数量，通过菜单调出。

### 非目标（本方案不做）

- 跨屏选区（记录为后续阶段）。
- 多选区下的标注、钉图、OCR、二维码。
- 长截图的多选区（长截图仍只支持单选区）。

## 2. 总体原则

1. **单选区行为零变化**：只有 1 个选区时，标注、钉图、OCR、二维码、智能窗口选择、长截图全部沿用现有路径。
2. **多选区只放行必要能力**：选区增加/删除/调整、输出、历史；其余功能置灰并提示。
3. **覆盖层与下游只传一个契约**：`SelectionSet`，避免下游到处判断数组。
4. **纯计算逻辑可单测**：矩阵布局、透明掩码、文件名生成、HTML 剪贴板构建全部抽成独立类。
5. **先文档、后实现**：本文档评审通过后再进入编码。

## 3. 数据模型

在现有 `CaptureRegion` 之上增加选区集：

```csharp
public enum SelectionMode
{
    Free,
    Matrix
}

public readonly record struct MatrixCell(int Row, int Column);

public sealed record MatrixSpec(
    CaptureRegion Bounds,   // 矩阵外框，物理像素，相对屏幕快照
    int Rows,
    int Columns,
    int GapX,
    int GapY,
    IReadOnlyList<MatrixCell>? ExcludedCells = null);

public sealed record SelectionSet(
    SelectionMode Mode,
    IReadOnlyList<CaptureRegion> Regions,   // 顺序即输出顺序
    MatrixSpec? Matrix = null,
    int Padding = 0);
```

约定：

- `CaptureRegion` 使用物理像素，坐标相对当前屏幕快照；协调器负责加上 `snapshot.Bounds.X/Y` 转成全局坐标（沿用现有做法）。
- `Regions` 的顺序就是输出顺序。自由模式按创建顺序；矩阵模式按行优先（左→右，上→下）。
- `Matrix` 只在矩阵模式存在，用于命名选区重新加载、预览重算和剪贴板布局。
- `ExcludedCells` 使用 1 基行号和列号，仅矩阵模式有效。
- `Padding` 用于单图输出，四边同值。

覆盖层内部另外维护 UI 状态：

```csharp
private readonly List<SelectionItem> _selections = [];
private int _activeSelectionIndex = -1;

private sealed class SelectionItem
{
    public Rect DipRect { get; set; }
    public CaptureRegion PixelRegion { get; set; }
    public SelectionVisual Visual { get; set; }
}
```

`_selection` / `_selectionPixelRegion` 保留，但语义改为**活动选区的镜像**，以便复用现有的尺寸徽标、几何编辑器、拖拽和固定尺寸逻辑。

## 4. 自由多选区

### 4.1 创建

| 操作 | 行为 |
|---|---|
| 空白处按住拖动 | 追加一个新选区，松开后成为活动选区 |
| 空白处单击 | 仅取消"活动"高亮，已有选区全部保留 |
| 悬停窗口/控件（智能选择） | 高亮预览，单击采纳为一个选区，可连续点选 |
| 已有选区内部按住 `Alt` 拖动 | 强制新建，允许重叠或嵌套 |
| 已有选区内部普通拖动 | 移动该选区 |

现有智能预览只在没有任何选区时启用。多选区下改为：只要不在拖拽或标注中，就始终可以预览并点选，这样连续点选多个窗口是最快的多选区入口。

### 4.2 激活

- 单击选区内部 → 激活。
- `Tab` / `Shift+Tab` → 下一个 / 上一个选区。
- 选区列表面板点击 → 激活，也可用于选中被遮挡的选区。
- 视觉区分：
  - 活动选区：现有亮青色边框、8 个缩放手柄、尺寸徽标、序号角标。
  - 非活动选区：1px 半透明边框、序号角标，无手柄。

### 4.3 移动、缩放、指定大小和位置

- 拖动内部 = 移动；拖动边或角 = 缩放，复用现有 `CaptureResizeHandle` 逻辑。
- `Shift` 锁定宽高比（沿用现有行为）。
- `W/A/S/D` 或方向键微调活动选区 1px，`Shift` 为 10px。
- 点击工具栏尺寸区域 → 现有几何编辑器已支持 `X/Y/W/H`，改为作用于活动选区；`Tab` 切换字段，`Enter` 应用。
- 最小尺寸沿用 `MinimumSelectionSize = 2`。
- 选区列表面板显示 `#序号  X,Y  W×H`，支持点击激活和删除，是精确定位与处理重叠选区的主要入口。

建议在第二阶段增加吸附对齐（窗口边缘、选区边缘、等间距辅助线），第一阶段不做。

### 4.4 取消选择

| 目标 | 操作 |
|---|---|
| 删除活动选区 | `Delete` / `Backspace`，删除后自动激活相邻项 |
| 删除指定选区 | 右键选区 → 删除；或列表面板删除按钮 |
| 清空全部 | 工具栏"清空全部"按钮（可选 `Ctrl+Shift+Delete`） |
| 放弃整次截图 | `Esc`（仅在没有拖拽时） |
| 取消正在进行的拖拽/缩放 | 拖拽中按 `Esc` 只取消本次操作，不退出 |

`Esc` 采用两级语义：拖拽/缩放中取消本次操作；空闲时退出截图。不再用 `Esc` 逐级删除选区，避免误删。

### 4.5 视觉与遮罩重构

现有 4 块矩形遮罩无法表达多个洞。改为：

- 一个覆盖整个 `Surface` 的 `Path`，`Data` 使用 `GeometryGroup`（`FillRule="EvenOdd"`）：外框 + 每个选区矩形，自动挖洞。
- 每个选区一个动态生成的 `SelectionVisual`（边框 + 手柄 + 序号），放进新增的 `Canvas x:Name="SelectionLayer"`。
- `UpdateShades`、`UpdateCornerMarks`、`UpdateSizeBadge`、`UpdateToolbarPosition`：
  - 遮罩按全部选区计算；
  - 尺寸徽标、手柄、工具栏按活动选区计算。

建议把单个选区的视觉抽成 `Controls/SelectionVisual.cs`，避免 XAML 里复制 N 份边框和手柄。

### 4.6 键盘与鼠标汇总

| 键位 | 行为 |
|---|---|
| 拖动空白 | 新建选区 |
| 拖动选区内部 | 移动活动选区 |
| 拖动边/角 | 缩放活动选区 |
| `Alt` + 拖动 | 强制新建（允许重叠） |
| `Tab` / `Shift+Tab` | 切换活动选区 |
| `Delete` / `Backspace` | 删除活动选区 |
| `W/A/S/D`、方向键 | 微调活动选区 1px |
| `Shift` + 微调 | 10px |
| `M` | 切换自由/矩阵模式 |
| `R` | 循环最近选区（沿用现有） |
| `Enter` | 输出全部选区 |
| `Ctrl+C` | 复制当前输出 |
| `Ctrl+S` | 保存当前输出 |
| `Esc` | 取消拖拽；空闲时退出截图 |

`Ctrl+C` 在单选区且标注工具激活时仍归标注使用，保持现有优先级；多选区禁用标注后自然回到截图复制。

## 5. 矩阵选区

### 5.1 流程

1. 工具栏切换"矩阵"（快捷键 `M`）。
2. 若已有选区，取活动选区作为基准外框；否则由用户拖拽定义基准矩形。
3. 基准选区旁浮出矩阵面板：行数、列数、行间隔、列间隔（可选外边距）。
4. 修改任一参数立即重算并重绘预览。
5. 矩阵模式下：
   - 整体拖动/缩放基准矩形，行列为保持不变，单元格尺寸实时变化；
   - 禁止单独拖拽某个单元格；
   - 单击单元格可排除/恢复（第二阶段，第一阶段不做）。
6. 切换回自由模式时，矩阵"炸开"为独立选区，保留当前布局。

### 5.2 计算规则

基准外框固定，单元格均分可用空间，使用整数像素并分配余数：

```text
usableW = W - (C - 1) * gapX
cellW   = usableW / C
remW    = usableW % C

x(j)     = bounds.X + j * (cellW + gapX) + min(j, remW)
width(j) = cellW + (j < remW ? 1 : 0)
```

行方向同理。这样总宽严格等于基准宽，最后一行/列不会出现明显窄条。

约束：

- `Rows >= 1`、`Columns >= 1`、`GapX >= 0`、`GapY >= 0`。
- `usableW >= Columns` 且 `usableH >= Rows`，否则自动 clamp 间隔或行列数并提示。
- 建议上限 50×50，超过没有实际意义。
- 排除单元格后，剩余单元格仍按原行列位置计算坐标，输出文件名保留原行号/列号。

计算抽成纯函数：

```csharp
public static IReadOnlyList<CaptureRegion> Calculate(
    CaptureRegion bounds,
    int rows,
    int columns,
    int gapX,
    int gapY,
    IReadOnlyList<MatrixCell>? excluded = null);
```

### 5.3 实时预览

- 参数控件变更即重算，计算量 O(R×C)，上限 2500，同步执行即可，无需节流。
- 数字框支持滚轮增减，`Shift` 步进 5。
- 预览样式：虚线网格、每格半透明高亮、行列编号；基准外框保持活动选区样式。

### 5.4 矩阵面板

```text
┌──────────────────────────────┐
│ 矩阵选区                      │
│ 行数   [ 3 ]   列数   [ 3 ]  │
│ 行间隔 [ 8 ]px 列间隔 [ 8 ]px│
│ 单元格：104 × 56 px           │
└──────────────────────────────┘
```

面板随基准选区定位；参数非法时输入框标红并显示约束提示，内部仍使用 clamp 后的合法值预览。

## 6. 输出

### 6.1 输出方式

新增设置项：

| 设置 | 取值 | 默认 |
|---|---|---|
| `MultiRegionOutputMode` | `SingleImage` / `MultipleFiles` | `SingleImage` |
| `MultiRegionPadding` | 0–200 px | 0 |
| `MultiImageFileNamePrefix` | 字符串 | `QingSnap` |
| `MultiImageClipboardLayout` | `Auto` / `Column` / `Row` | `Auto` |

按钮语义：

- **确认**：按设置输出（单图或多文件）。
- **复制**：始终复制"当前输出"；单图模式复制合成图，多图模式复制图像集。
- **保存**：按设置落盘。
- 开启"截图后自动复制"时，行为与"复制"一致。

### 6.2 单图输出

- 外接矩形 = 所有选区并集 + `Padding`（四边同值），并 clamp 到快照边界。
- 未选中区域透明：
  1. 从原始 `ScreenSnapshot.Image` 裁剪外接矩形；
  2. `CopyPixels` 到 BGRA 缓冲区；
  3. 逐行按选区跨度把非选区像素的四通道全部置 0（Pbgra32 透明黑）。
- 固定输出 PNG。即使设置里是 JPG/BMP，单图输出也强制 PNG，并在保存时提示。
- 单选区且 `Padding = 0` 时，与现有 `CreateSelectedImage` 的像素结果一致。
- 多选区不渲染标注层，直接使用原始快照；单选区保留现有"渲染 Root 再裁剪"的路径（含标注）。

### 6.3 多图输出与文件命名

- 每个选区独立裁剪，一个选区一个文件。
- **自由多选区**：`{Prefix}_{序号}.png`。
  - 序号从 1 开始，按总张数的位数补零；位数为 1 时不补零。
  - 5 张：`QingSnap_1.png` … `QingSnap_5.png`；12 张：`QingSnap_01.png` … `QingSnap_12.png`。
- **矩阵选区**：`{Prefix}_{行号}_{列号}.png`。
  - 行号、列号从 1 开始，按最大行号/列号的位数补零；位数为 1 时不补零。
  - 3×3：`QingSnap_1_1.png` … `QingSnap_3_3.png`；12×12：`QingSnap_01_01.png` … `QingSnap_12_12.png`。
  - 被排除的单元格不输出文件，但保留原行号/列号，不重新编号。
- 落盘位置：
  - 默认写入历史目录下的组子目录，例如 `<HistoryDirectory>\20261025-163500\QingSnap_1_1.png`，避免重复截图产生同名覆盖；历史扫描本身是递归的，组目录天然可被现有历史发现。
  - `Ctrl+Shift+S` 或菜单"导出全部到文件夹…"可另存到用户选择的目录，文件名仍按上述规则；同名文件存在时提示覆盖/另存。
- 格式沿用设置（PNG/JPG/BMP）；多图是矩形裁剪，不涉及透明。文件名默认使用 `.png`，若设置改为 JPG/BMP，扩展名跟随设置（例如 `QingSnap_1_1.jpg`）。
- 第一阶段每张图独立进入历史；历史窗口按组折叠放第二阶段。

### 6.4 剪贴板

**结论：Windows 剪贴板没有原生的"图像数组"或"矩阵"格式。** 一次只能放一个数据对象，可以同时提供多个格式作为同一对象的不同表示，但不能注册一组位图。

要把多张图按列或矩阵粘贴到 Word/Excel，现实可行的路径是 **HTML 表格（`HTML Format` / CF_HTML）**：

```html
<table style="border-collapse:collapse">
  <tr>
    <td><img src="file:///C:/Temp/QingSnap/Clipboard/xxxx/1_1.png" width="320" height="180"></td>
    <td><img src="file:///C:/Temp/QingSnap/Clipboard/xxxx/1_2.png" width="320" height="180"></td>
  </tr>
  <tr>
    <td><img src="file:///C:/Temp/QingSnap/Clipboard/xxxx/2_1.png" width="320" height="180"></td>
    <td><img src="file:///C:/Temp/QingSnap/Clipboard/xxxx/2_2.png" width="320" height="180"></td>
  </tr>
</table>
```

- **矩阵模式**：输出 R×C 表格，粘贴后保持矩阵形状；单元格间隔用 `<td>` 的内边距或空列/空行表达。
- **自由多选区**：默认 N×1（一列），可通过 `MultiImageClipboardLayout` 改为一行。
- 图片引用使用**本地临时 PNG 文件 + `file://` URL**，不使用 base64 data URI。Office 的 HTML 导入对 data URI 支持不稳定，且 base64 会让 HTML 体积随图片数量快速膨胀。
- 临时目录：`%TEMP%\QingSnap\Clipboard\{guid}\`；启动时清理 24 小时前的目录。粘贴完成前不能删除。
- 同时在剪贴板中放入以下格式做兜底：

| 格式 | 内容 | 用途 |
|---|---|---|
| `PNG` / `CF_DIB` | 首张图或合成图 | 只支持单图的应用仍可粘贴 |
| `HTML Format`（CF_HTML） | 表格 + 图片 | Word / Excel / WPS 还原行列 |
| `QingSnap.CaptureSet.v2` | JSON：模式、区域、矩阵、图片路径 | QingSnap 自身完整还原 |
| `CF_HDROP`（可选） | 多文件列表 | 资源管理器、邮件 |

- CF_HTML 需要正确的 `Version / StartHTML / EndHTML / StartFragment / EndFragment` 字节偏移（UTF-8），抽成 `ClipboardHtmlBuilder` 并单测。

**需要实测确认：**

- Excel 365 / 2016：图片是"单元格内嵌图"还是"覆盖在单元格上的浮动图片"。新版 Excel 有 Place in Cell，旧版通常按浮动图片处理；视觉矩阵大概率成立，但"严格每格一张内嵌图"不保证。
- Word：HTML 表格一般能还原为带图表格。
- WPS Office、LibreOffice 的 HTML 粘贴行为。
- HTML 剪贴板的大小/超时限制，以及 `file://` 路径的安全提示。

**兜底策略（若 Excel 实测不支持）：**

1. 单图输出仍可用；
2. 提供"复制为文件列表"；
3. 增加"导出为 xlsx（图片按格插入）"作为重方案，需引入 OOXML 或第三方库，第一阶段不做。

## 7. 历史与命名选区

这里的"历史"指现有的**历史选区**（按 `R` 循环的最近 5 个，`CaptureRegionHistory.Capacity = 5`），不是截图记录历史。

### 7.1 两层历史

- **最近选区**：保留 5 条，`R` 键快速循环，行为不变。
- **命名选区**：独立存储，不限数量，支持增删改。

### 7.2 数据结构与存储

```csharp
public sealed record SelectionPreset(
    Guid Id,
    string Name,
    SelectionMode Mode,
    IReadOnlyList<CaptureRegion> Regions,
    MatrixSpec? Matrix,
    int Padding,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

- 存储：`%LOCALAPPDATA%\QingSnap\selection-presets.json`；便携模式跟随 `AppSettingsService.DataDirectory`。
- 文件带 `version` 字段；写入使用临时文件 + `File.Move(..., overwrite: true)` 原子替换，与现有 `AppStateStore` 相同的容错策略。
- 读取失败时返回空列表，不阻塞启动。

### 7.3 菜单与入口

- 托盘菜单新增 **"命名选区"** 子菜单（靠近"重复上次范围"）：
  - 按名称排序，最多显示 15 个，超出显示"更多…"；
  - 点击直接以该选区集开始截图；
  - 末尾"管理命名选区…"打开管理窗口。
- 托盘菜单当前在构造时一次性创建，需要改为 `ContextMenuStrip.Opening` 时重建，才能反映新增/删除。
- 覆盖层内：
  - "保存当前选区为…"（工具栏菜单）；
  - 下拉加载命名选区。

### 7.4 管理窗口

列表显示：名称、类型（自由/矩阵）、区域数、行列、缩略图、更新时间。

支持：新建、重命名、编辑（打开覆盖层编辑后保存）、复制、删除（确认）、排序；不限条数。

载入时校验区域是否仍在当前屏幕可见范围内；不可见则提示并回退到全屏重新选择。

### 7.5 命名选区的最近使用

保存命名选区后，可同时写入最近选区列表，方便 `R` 键循环；这不改变"最近 5 条"的数量限制。

## 8. 下游简化与兼容

| 位置 | 改动 |
|---|---|
| `CaptureOverlayActionEventArgs` | 增加 `SelectionSet` / `Images`；保留 `Image`、`LocalRegion` 作为首项兼容 |
| `CaptureCoordinator.OpenCaptureOverlay` | 构造时传入最近选区集、命名选区；渲染输出后按数量分支 |
| `CaptureCoordinator.HandleOverlayActionAsync` | OCR / 二维码 / 钉图增加 `Regions.Count == 1` 守卫 |
| `CaptureResult` | 增加 `ImagePaths`、`ImageCount` |
| `ClipboardService` | 增加 `CopyCaptureSetAsync`：临时 PNG + CF_HTML + PNG/DIB + 私有格式 |
| `AppStateStore` | 状态升级到 v2：最近选区集；旧 `LastRegion` / `RecentRegions` 继续可读 |
| `TrayIconService` | 菜单改为 `Opening` 时重建，加入"命名选区"子菜单 |
| `SettingsWindow` | 新增"多选区"设置区 |
| `HistoryMetadata` | 可选：`GroupId`（多图归组）；第一阶段可用组子目录代替 |

### 8.1 单选区路径保持不变

当 `Regions.Count == 1`：

- 标注、钉图、OCR、二维码、智能窗口选择全部可用；
- `CreateSelectedImage` 继续渲染 `Root`（含标注）；
- `CaptureCoordinator` 走现有 `CompleteCaptureAsync`；
- 剪贴板继续使用现有 `CopyCaptureImageAsync`。

多选区时：

- 工具栏的标注、钉图、OCR、二维码按钮置灰并提示；
- 隐藏长截图入口；
- 输出走新的 `SelectionImageComposer`。

### 8.2 跨屏（后续）

当前快照只覆盖一个屏幕。跨屏多选区需要：

- 虚拟屏快照（`SystemInformation.VirtualScreen`）或每个屏幕分别快照；
- 区域跨屏校验；
- 每屏 DPI 换算；
- 多屏快照内存与性能评估。

本方案明确不支持，记录为第三阶段。

## 9. 代码落点

行号基于当前分支，实施时会偏移。

| 文件 | 现状 | 改造 |
|---|---|---|
| `Views/CaptureOverlayWindow.xaml.cs:57` | 单选区字段 | 增加 `_selections`、`_activeSelectionIndex`，`_selection` 变为活动镜像 |
| `Views/CaptureOverlayWindow.xaml.cs:240` | 鼠标按下 | 新建/激活/移动/`Alt` 新建分支 |
| `Views/CaptureOverlayWindow.xaml.cs:409` | 键盘 | 多选区键位、`Esc` 两级语义、模式切换 |
| `Views/CaptureOverlayWindow.xaml.cs:871` | `HitTest` | 遍历所有选区，返回命中项与手柄 |
| `Views/CaptureOverlayWindow.xaml.cs:961` | `SetSelectionCore` | 拆成"更新单个选区"与"更新全部选区视觉" |
| `Views/CaptureOverlayWindow.xaml.cs:1121` | `UpdateShades` | 改为 EvenOdd 几何遮罩 |
| `Views/CaptureOverlayWindow.xaml.cs:1383` | `CreateSelectedImage` | 拆出 `SelectionImageComposer` |
| `Views/CaptureOverlayWindow.xaml.cs:1272` | `RequestAction` | 携带 `SelectionSet` |
| `Views/CaptureOverlayWindow.xaml:308` | 四个 shade | 替换为一个遮罩 Path + SelectionLayer |
| `Services/CaptureCoordinator.cs:319` | 覆盖层入口 | 传入命名/最近选区集 |
| `Services/CaptureCoordinator.cs:477` | 完成截图 | 单图/多图分支 |
| `Services/CaptureCoordinator.cs:518` | 动作处理 | 多选区守卫 |
| `Services/ClipboardService.cs:29` | 复制入口 | 增加图像集复制 |
| `Services/AppStateStore.cs:36` | 最近选区 | 升级为选区集，兼容旧字段 |
| `Services/TrayIconService.cs:23` | 静态菜单 | `Opening` 重建 + 命名选区子菜单 |
| `Models/AppSettings.cs:3` | 设置模型 | 新增多选区字段 |
| `Views/SettingsWindow.xaml:624` | 设置页 | 新增多选区设置区 |

新增文件建议：

- `Models/SelectionSet.cs`
- `Models/SelectionPreset.cs`
- `Services/MatrixLayout.cs`
- `Services/SelectionImageComposer.cs`
- `Services/ClipboardHtmlBuilder.cs`
- `Services/SelectionPresetStore.cs`
- `Views/SelectionPresetManagerWindow.xaml(.cs)`
- `Controls/SelectionVisual.cs`

## 10. 实施阶段

### 第一阶段：自由多选区 + 输出 + 命名选区

- 多选区创建、激活、移动、缩放、删除、列表面板；
- 遮罩与视觉重构；
- 单图透明输出、多图文件输出；
- 设置项；
- 命名选区存储、托盘菜单、管理窗口；
- 多选区下禁用标注/钉图/OCR/二维码；
- 单元测试：选区集合、矩阵布局、透明掩码、文件名、预设存储。

### 第二阶段：矩阵选区 + 剪贴板图像集

- 矩阵面板、实时预览、行列/间隔校验；
- 单元格排除/恢复；
- CF_HTML 剪贴板 + 临时文件生命周期；
- Excel / Word / WPS Spike 与兜底策略；
- 历史按组折叠。

### 第三阶段：后续

- 跨屏多选区；
- 吸附对齐、分布、辅助线；
- 多选区撤销栈；
- 多选区下的逐区域标注（若确实需要）。

## 11. 测试计划

### 11.1 单元测试

- **矩阵布局**
  - 300×300、3×3、无间隔 → 每格 100×100；
  - 300×300、3×3、间隔 10 → 宽 94/93/93，总宽 300；
  - 1×N、N×1；
  - 间隔过大、行列过大时 clamp；
  - 排除单元格后坐标与行号/列号保持正确。
- **选区集合**
  - 添加、删除、激活相邻项、输出顺序、包围盒；
  - 重叠选区、嵌套选区。
- **透明掩码**
  - 两个不相交区域 + padding；
  - 重叠区域；
  - 边界 clamp；
  - 单选区 padding=0 与现有裁剪结果一致。
- **文件名生成**
  - 自由模式 1 位/2 位序号；
  - 矩阵模式 3×3 与 12×12 的补零；
  - 排除单元格不重新编号。
- **CF_HTML**
  - 头部偏移量按 UTF-8 字节计算正确；
  - HTML 转义；
  - `file://` URL 编码；
  - 1×N 与 R×C 表格结构。
- **命名选区存储**
  - 增删改查、原子写、损坏文件容错、旧版本兼容。

### 11.2 手工测试

- DPI 100% / 125% / 150% / 200% 下创建、移动、缩放、输出。
- 同 DPI 双显示器；混合 DPI 已知限制记录在案。
- 连续创建 5–10 个选区，重叠、嵌套、删除、`Tab` 切换。
- 矩阵 2×2、3×3、1×N、N×1、间隔 0/8/50，实时预览与输出。
- 单图透明输出在 PNG 查看器、Word、Excel 中检查透明区域。
- 多图输出文件名、顺序、组目录。
- 剪贴板粘到 Excel 365 / Excel 2016 / Word / WPS / 记事本。
- 命名选区：保存、重命名、删除、排序、托盘菜单启动、不可见区域回退。
- 单选区回归：标注、钉图、OCR、二维码、长截图、`R` 键。

## 12. 风险与待验证

| 风险 | 影响 | 应对 |
|---|---|---|
| Excel 对 CF_HTML 图片的落格行为不确定 | 剪贴板矩阵可能达不到"每格一张内嵌图" | 先做 Spike；保留单图/文件列表兜底；必要时导出 xlsx |
| CF_HTML 体积/超时限制 | 大矩阵粘贴失败 | 使用临时文件而非 base64；限制矩阵规模并提示 |
| 覆盖层 2600 行重构 | 回归风险 | 先抽纯逻辑与视觉控件；单选区路径保持不变；补单测 |
| 多屏混合 DPI | 选区坐标偏差 | 本阶段不支持跨屏；单屏内沿用现有 DIP/像素换算 |
| 临时文件清理过早 | 稍后粘贴时图片丢失 | 24 小时保留；粘贴前不删除；文档说明 |
| 选区数量过多 | 视觉嘈杂、性能下降 | 建议上限 50；列表管理；性能测试 |

## 13. 已确认的决策

- 矩阵截图文件名：`{Prefix}_{行号}_{列号}.png`，行号/列号从 1 开始。
- 默认输出方式：单图（透明背景）。
- 多图输出默认写入历史目录下的组子目录，支持另存到指定文件夹。
- 自由多选区复制到表格时默认一列（N×1）。
- "命名历史"按"命名选区"实现，不限数量，菜单调出，支持增删改。
- `Esc`：拖拽中取消本次拖拽，空闲时退出截图。
- 矩阵单格排除放在第二阶段。
- 多选区下不提供标注、钉图、OCR、二维码。
- 跨屏不支持，记录为第三阶段。
