# QingSnap 多选区截图设计

- 状态：第二阶段已实现（2026-10-25）；剪贴板逐格粘贴的本机实测受桌面环境限制，见 6.4.1 与 15.3
- 日期：2026-10-25
- 适用基线：`claude/multi-region-phase2`
- 相关代码：[CaptureOverlayWindow.xaml.cs](../src/QingSnap.App/Views/CaptureOverlayWindow.xaml.cs)、[CaptureCoordinator.cs](../src/QingSnap.App/Services/CaptureCoordinator.cs)、[ClipboardService.cs](../src/QingSnap.App/Services/ClipboardService.cs)、[MatrixLayout.cs](../src/QingSnap.App/Services/MatrixLayout.cs)

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
| 已有选区内部按住 `Ctrl` 拖动 | 强制新建，允许重叠或嵌套 |
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
| `Ctrl` + 拖动 | 强制新建（允许重叠） |
| `Alt` + 角点拖动 | 以中心缩放（沿用现有行为） |
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

#### 6.4.1 Spike 记录（2026-10-25）

本机环境：

| 项 | 结果 |
|---|---|
| 剪贴板 | 不可用：`OpenClipboard(NULL)` 返回 `FALSE`、`GetLastError=5`（拒绝访问），`GetOpenClipboardWindow()=0`；PowerShell / Python / .NET 结果一致 |
| 会话 | WTS 会话为 Active，但前台窗口为 0（锁屏 / 无交互桌面） |
| Excel | **本机未安装 Microsoft Excel**；`Excel.Application` 与 `KET.Application` 都由 WPS 表格 12.1.0.29680 接管 |
| Word | Microsoft Word 16.0（`C:\Program Files\Microsoft Office\Root\Office16`） |
| WPS | Writer / Spreadsheet 12.1.0.29680 |

结论：

- 由于剪贴板不可用，**没有执行真正的“Ctrl+V 粘贴”Spike**，Excel 365/2016、Word、WPS 的剪贴板逐格行为均未实测。
- 退而验证了**同一份 HTML 表格**在 Office 中的导入行为（把 6.4 的表格写成 `.html` 文件后由应用打开；HTML 导入器与 HTML 剪贴板解析路径高度一致）：
  - WPS 表格（`Excel.Application` 实际指向 WPS）：2×2 → 4 张图片，均为浮动图片，锚定 `A1 / B1 / A2 / B2`，行列布局正确；不是“嵌入单元格”。
  - Microsoft Word 16.0：1 个表格 + 4 个内嵌图片，分别位于 `r1c1 / r1c2 / r2c1 / r2c2`，逐格正确（120×80 px 按 90×60 pt 缩放）。
  - WPS Writer：同 Word，1 个表格 + 4 个内嵌图片，行列正确。
- 因此：Word / WPS 文字可得到严格逐格内嵌图；WPS 表格可得到正确的矩阵视觉布局，但图片是浮动图片；Microsoft Excel 的真实行为仍待在有 Excel 的机器上确认。

复现步骤（剪贴板可用时）：

```bash
dotnet run --project tools/QingSnap.ClipboardSpike -- 2 3 skip-excluded
```

然后在 Excel / Word / WPS 中新建文档按 `Ctrl+V`，检查图片数量、行列位置，以及图片是嵌入单元格、内嵌图片还是浮动图片。

无剪贴板时可用离线代理：

```bash
dotnet run --project tools/QingSnap.ClipboardSpike -- html C:\Temp\qingsnap-html-spike
```

生成与 6.4 相同的 HTML 表格，再由 Office 打开该 `.html` 文件检查布局。

兜底（已实现）：无论目标应用是否采纳 HTML，剪贴板始终同时提供首图的 PNG 与 CF_DIB；设置页可开启 CF_HDROP 文件列表；单图输出仍可用；多图文件始终落盘。

## 7. 历史与命名选区

这里的"历史"指现有的**历史选区**（按 `R` 循环的最近 5 个，`CaptureRegionHistory.Capacity = 5`），不是截图记录历史。

### 7.1 两层历史

- **最近选区**：保留 5 条，`R` 键快速循环。
- **命名选区**：独立存储，不限数量，支持增删改。
- **最后选区集**：`state.json` 中的 `LastSelectionSet`，`Shift+F1`「重复上次范围」恢复完整多选区。

### 7.2 数据结构与存储

```csharp
public sealed record SelectionPreset(
    Guid Id,
    string Name,
    SelectionSet Selection,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

- 存储：`%LOCALAPPDATA%\QingSnap\selection-presets.json`；便携模式跟随 `AppSettingsService.DataDirectory`。
- 文件带 `version` 字段；写入使用临时文件 + `File.Move(..., overwrite: true)` 原子替换，与现有 `AppStateStore` 相同的容错策略。
- 读取失败时返回空列表，不阻塞启动。

### 7.3 菜单与入口

- 托盘菜单在 `Opening` 时重建，新增 **"命名选区"** 子菜单（靠近"重复上次范围"）：
  - 按名称排序，最多显示 15 个，超出显示"更多…"；
  - 点击直接以该选区集开始截图；
  - 末尾"管理命名选区…"打开管理窗口。
- 覆盖层工具栏 ★ 保存当前选区为命名选区（自动命名，可在管理窗口重命名）；加载通过托盘菜单。

### 7.4 管理窗口

列表显示名称、类型（自由/矩阵）、区域数、外扩边距。

支持：重命名、删除（确认）、用上次选区更新、从上次选区新建；不限条数。

载入时校验区域是否仍在当前屏幕可见范围内；不可见则提示并回退到全屏重新选择。

### 7.5 命名选区的最近使用

命名选区不会自动写入最近 5 条；最近选区在完成截图或保存文件成功后更新。

### 7.6 最近选区的粒度（已决策）

`R` 键的最近选区以单个 `CaptureRegion` 为单位，容量 5。多选区时只把**第一个区域**写入最近选区；完整多选区写入 `LastSelectionSet`，可用 `Shift+F1` 或命名选区恢复。

原因与权衡：

- 最近选区是"快速回到一个矩形范围"的入口，单个矩形模型简单，与现有 `CaptureRegionHistory` 和覆盖层 `R` 流程兼容。
- 完整多选区已有两个可靠入口（重复上次范围、命名选区），不需要扩大最近历史的模型。
- 若改为存储 `SelectionSet`，需要升级 `AppStateStore` 数据结构、`PreviousSelectionRequested` 事件和覆盖层重建流程，收益有限。

**2026-10-25 决策：保持现状。**

截图记录的粒度另行处理：多图输出时每个选区各存一条；单图输出时存一张透明合成图。


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
| `Views/CaptureOverlayWindow.xaml.cs:240` | 鼠标按下 | 新建/激活/移动/`Ctrl` 新建分支 |
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

### 第二阶段：矩阵选区 + 剪贴板图像集（已完成，见 15）

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
- `R` 键最近选区只保存多选区的第一个区域；完整多选区用 `Shift+F1` 或命名选区恢复（2026-10-25 决策，见 7.6）。

## 14. 第一阶段实施记录（2026-10-25）

已完成：

- 自由多选区：连续框选、点击激活、`Ctrl` 强制新建、`Tab` 循环、方向键/WASD 微调、`Delete` 删除、右键菜单、选区列表面板。
- 视觉：统一的多洞遮罩、每选区序号徽标、活动选区保留原有边框和手柄。
- 单图输出：选区并集外接矩形 + 可配置外扩，未选中区域透明；单选区保留标注。
- 多图输出：每个选区一个文件，写入 `历史目录/yyyy-MM/yyyyMMdd-HHmmss/` 组目录，文件名为 `{Prefix}_{序号}`。
- 设置：输出方式（单图/多图）、外扩边距、文件名前缀。
- 命名选区会保存自身的外扩边距，加载时优先使用预设值。
- 透明输出（多选区或配置了外扩边距）强制保存为 PNG；剪贴板同时提供 PNG 流以保留透明通道。
- 命名选区：保存到 `selection-presets.json`；工具栏 ★ 保存，托盘菜单列出，管理窗口支持重命名、删除、用上次选区更新、从上次选区新建。
- 状态：最近一次完整选区集写入 `state.json`，“重复上次范围”可恢复多选区。
- 保存到文件成功后写入最近选区和最后选区集；取消保存或放弃覆盖不会污染历史。
- 多选区下禁用标注、钉图、OCR、二维码、长截图，并在代码层增加守卫。
- 剪贴板：第一阶段复制透明合成图；逐格 HTML 表格留待第二阶段。
- 矩阵文件名工具 `SelectionOutputNaming.MatrixFileName` 已实现并测试，矩阵交互留待第二阶段。
- 自动化测试：179 通过，0 警告。

尚未验证：

- 真实桌面上的多选区拖拽/缩放、混合 DPI、托盘菜单和管理窗口的人工回归。
- Excel / Word / WPS 的 CF_HTML 逐格图片粘贴（第二阶段 Spike）。

已知限制：

- 跨屏选区不支持；加载命名选区时超出当前屏幕的区域会被丢弃并回到全屏选择。
- 多选区输出不包含标注。


## 15. 第二阶段实施记录（2026-10-25）

### 15.1 已完成

**矩阵选区**

- 工具栏新增“自由 / 矩阵”切换，快捷键 `M`；进入矩阵模式时以当前活动选区（多选区时为全部选区的外框）作为基准矩形，没有选区时先拖拽定义基准。
- 矩阵面板：行数、列数、行间隔、列间隔。修改即重算预览；滚轮增减（`Shift` 步进 5）；越界时按可用空间 clamp，输入框标黄并显示实际生效值与单元格尺寸；非法文本标红，内部仍使用上一次合法值预览。
- 布局按 5.2 的整数像素余数分配；行列上限 50，且不超过基准矩形的像素宽高。
- 矩阵整体拖动 / 拖边角缩放 / 方向键与 `WASD` 微调（`Shift` 10px）；矩阵模式下不单独拖拽单元格。
- 单击单元格排除或恢复（被排除单元格显示 × 与虚线），至少保留一格；右键菜单提供“恢复全部单元格 / 重新框选基准矩形 / 切换为自由选区 / 清空矩阵”。
- 切回自由模式时矩阵“炸开”为独立选区，保留当前布局；`Esc` 在拖拽中只取消本次拖拽。
- 矩阵模式下禁用标注、钉图、OCR、二维码、长截图（含 1×1 矩阵），`RequestAction` 与 OCR / 二维码入口均有守卫。
- 命名选区、`Shift+F1`“重复上次范围”会保存和恢复矩阵参数、排除单元格与外扩边距。

**矩阵输出**

- 多图模式：`{Prefix}_{行号}_{列号}.png`（行/列从 1 开始，按最大行列号补零），自由模式仍为 `{Prefix}_{序号}`；被排除的单元格不输出且不重新编号。
- 输出仍写入历史目录下的组目录；同时写入 `.qingsnap-group.json`（版本、GroupId、模式、行列、张数、创建时间）。
- 单图模式仍输出透明合成图；矩阵只保留一个单元格时按该单元格裁剪，而不是按基准框。
- 保存到文件、复制、自动复制使用同一套命名与分组逻辑。

**剪贴板图像集**

- 一次写入多个格式：`PNG`、`CF_DIB`（首图，32bpp 自下而上）、`HTML Format`（CF_HTML，UTF-8 字节偏移、`meta charset=utf-8`）、`QingSnap.CaptureSet.v2`（JSON：模式、行列、间隔、边距、每格行列号/临时路径/区域、创建时间）、可选 `CF_HDROP`。
- 矩阵为 R×C 表格；自由选区默认 N×1，可在设置中选择固定一列或一行。
- 单元格间隔用 `<td>` 内边距表达（`gap/2`，上限 24px）；图片尺寸按 96 DPI 换算为 CSS 像素，高 DPI 截图不会变成两倍大。
- 临时目录 `%TEMP%\QingSnap\Clipboard\{guid}\`；启动时和每次复制前清理 24 小时前的目录，当前目录不会被删除；文件被 Office 占用时清理失败只记录日志。
- HTML 写入失败时自动回退为复制透明合成图；即使 HTML 未被采纳，PNG/DIB 仍可用于单图粘贴。

**历史按组折叠**

- 历史卡片显示组标记（如“矩阵 3×3 · 8 张”），图标模式显示“组 N”。
- 顶部“按组折叠”开关：同一组的文件只保留一张，右键可“显示同组全部”；关闭后恢复完整列表。
- 组信息来源是组目录中的 `.qingsnap-group.json`，不依赖数据库迁移；旧的历史图片不受影响。

**设置**

- 多选区设置区新增“多图复制到表格的排布”（自动 / 一列 / 一行）与“同时提供文件列表（CF_HDROP）”。

### 15.2 已验证

- 自动化测试 210 个全部通过（Debug），其中本阶段新增：矩阵布局与 clamp、矩阵/自由文件名、CF_HTML 头部偏移与转义、CF_DIB / PNG / DROPFILES 字节结构、临时目录清理、组标记读写、历史折叠、矩阵选区与预设的序列化往返。
- 覆盖层集成测试（真实 `CaptureOverlayWindow` 实例、STA 线程）：从 `SelectionSet` 恢复 2×2 矩阵（间隔 10、排除 (1,2)）后，输出 3 张 145×145 图片，顺序为 `(1,1) (2,1) (2,2)`，文件名 `QingSnap_1_1 / 2_1 / 2_2`；`CreateGlobalSelectionSet` 返回带屏幕偏移与排除单元格的矩阵选区；同一窗口再载入自由选区后正确回到自由模式并输出 2 张图。
- Debug / Release 构建 0 警告 0 错误。
- HTML 表格离线代理测试：见 6.4.1（Word 16.0 与 WPS Writer 逐格内嵌；WPS 表格按单元格锚定浮动图片）。

### 15.3 未验证

- **真实剪贴板粘贴**：本次执行环境的剪贴板不可用（`OpenClipboard` 拒绝访问、无前台窗口），无法把 CF_HTML 放入剪贴板，也无法在 Office/WPS 中 `Ctrl+V`。
- **应用级冒烟**：本机已有一个 QingSnap 实例占用单实例互斥量，无法启动本分支构建做整体冒烟；覆盖层 XAML 由集成测试真实实例化验证，历史/设置窗口的新增标记只通过编译与资源键检查。
- **Microsoft Excel 365/2016**：本机未安装，`Excel.Application` 被 WPS 表格接管，Excel 的逐格粘贴行为未知。
- 矩阵交互的人工回归：拖拽移动/缩放、面板输入与滚轮、单元格排除点击、不同 DPI（100%–200%）下的预览与输出。
- 大矩阵（接近 50×50）的 UI 流畅度、HTML 体积与剪贴板超时。
- 剪贴板内容的持久化与剪贴板管理器（PowerToys、输入法等）的兼容性。

### 15.4 已知限制与遗留

- 矩阵模式下不做单独的比例锁定、固定尺寸、吸附对齐；`Tab` 不逐个切换单元格（矩阵只有一个基准框）。
- 矩阵行列上限 50×50；单元格数很多时每个单元格都会创建视觉元素，极端配置下预览可能变慢。
- 剪贴板图像集要求目标应用支持 HTML 图片（`file://` 路径）；不支持时只能得到首图。
- 跨屏仍不支持；多选区/矩阵仍不支持标注、钉图、OCR、二维码、长截图。
- 组标记文件是新增的隐藏 JSON；删除组目录时一并删除，不影响历史扫描。删除组内部分图片后，组标记里的“张数”仍是采集时的数量，不做实时校正。
