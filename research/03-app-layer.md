# 03 · 应用层深挖：GUI / CLI / Exporter / Patcher

> 范围：`AnimeStudio.GUI`、`AnimeStudio.CLI`、`AnimeStudio.Patcher`，以及三者与 `AnimeStudio`（core）/`AnimeStudio.Utility` 的接缝。核心解包管线（BundleFile → SerializedFile → Object）见 `research/01-core-pipeline.md`，本文不重复。
> 所有行号基于当前 clone（`Directory.Build.props` 版本 1.1.0）。方法：grep 定位 + 局部 read；GUI vs CLI 的重复度用「非空行多重集交集」实测，不是估算。

---

## 0. 规模与依赖（先看数）

| 项目 | .cs 文件 | 代码行 | 关键外部依赖 | TFM / RID |
| --- | --- | --- | --- | --- |
| `AnimeStudio.GUI` | 24 | 11,946 | OpenTK 4.9.4、OpenTK.GLControl 4.0.2、Newtonsoft.Json 13.0.4、原生 `AnimeStudio.Libraries/AnimeStudio.FMOD.dll` | `net9.0-windows;net10.0-windows`，`win-x64` |
| `AnimeStudio.CLI` | 6 | 1,781 | System.CommandLine 2.0.9、System.Configuration.ConfigurationManager 10.0.9、Newtonsoft.Json | 同上 |
| `AnimeStudio.Patcher` | 1 | 213 | 无 | `net10.0` |

出处：`AnimeStudio.GUI/AnimeStudio.GUI.csproj:5,10,43-54`、`AnimeStudio.CLI/AnimeStudio.CLI.csproj:4,7,11-15`、`AnimeStudio.Patcher/AnimeStudio.Patcher.csproj:5`、`Directory.Build.props:2-6`。

单文件占比（GUI 的痛点在两个巨型文件）：

| 文件 | 行 | 占 GUI 代码 |
| --- | --- | --- |
| `AnimeStudio.GUI/MainForm.cs` | 3,426 | 29% |
| `AnimeStudio.GUI/MainForm.Designer.cs` | 1,734 | 15% |
| `AnimeStudio.GUI/Studio.cs` | 1,108 | 9% |
| `AnimeStudio.GUI/AssetBrowser.cs` + `.Designer.cs` | 705 + 393 | 9% |
| `AnimeStudio.GUI/Exporter.cs` | 705 | 6% |

依赖拓扑（应用层只向下；core 无任何项目引用，是整个栈的叶子）：

```
GUI ──┬──> AnimeStudio.Utility ──┬──> AnimeStudio.FBXWrapper ──┬──> AnimeStudio (core, 仅 NuGet 依赖)
      │                          │                             └──> AnimeStudio.PInvoke
      │                          └──> AnimeStudio.PInvoke
      └──> AnimeStudio (core)
CLI ──┘（同样的两条引用）
Patcher (独立，零项目引用)
```

证据：`AnimeStudio.Utility/AnimeStudio.Utility.csproj:21-23`、`AnimeStudio.FBXWrapper/AnimeStudio.FBXWrapper.csproj:14-15`、`AnimeStudio.GUI/AnimeStudio.GUI.csproj:15-16`、`AnimeStudio.CLI/AnimeStudio.CLI.csproj:18-19`、`AnimeStudio/AnimeStudio.csproj:12-19`（只有 PackageReference）、`AnimeStudio.slnx:24-31`（Patcher 被放在 `/Misc/` 文件夹，不参与 core 依赖图）。

> 对 fork 的含义：GUI/CLI 都能拿到 `AnimeStudio.FBXWrapper`（经由 Utility 传递），所以 §4.3 的 `AnimeStudio.Application` 只要引用 `AnimeStudio.Utility` 就同时拥有 FBX 与 Skia 能力；反过来说，**任何「只给 CLI 瘦身」的尝试都得先拆开 Utility 里对 FBX/ACL/Skia 的硬引用**。

**一句话结论**：`GUI = CLI + WinForms/OpenTK/FMOD 表现层`，但两者是靠**复制粘贴**实现的，不是靠分层。这是本文最重要的一条，见 §4。

---

## 1. GUI 形状

### 1.1 启动与装配链（严格按执行顺序）

| # | 位置 | 做什么 | 备注 |
| --- | --- | --- | --- |
| 1 | `AnimeStudio.GUI/Program.cs:15-21` | `SetHighDpiMode(SystemAware)` → `EnableVisualStyles` → `Application.Run(new MainForm())` | 无 DI、无 `appsettings.json`、无单实例互斥 |
| 2 | `MainForm.cs:95` | `Thread.CurrentThread.CurrentCulture = en-US` | 全进程强制，见 §8.6 |
| 3 | `MainForm.cs:96` | `InitializeComponent()`（designer 生成，159 个控件实例化） | |
| 4 | `MainForm.cs:97` → `106-140` | `ApplyTheme()`：`Application.SetColorMode(Dark/Classic)`，`#if NET9_0_OR_GREATER` + `#pragma warning disable WFO5001` | 仅改两处前景色/网格线，不是真正的主题系统 |
| 5 | `MainForm.cs:99` | `Text = $"AnimeStudio v{Application.ProductVersion}"` | 版本来自 `Directory.Build.props` |
| 6 | `MainForm.cs:100` → `189-207` | `InitializeExportOptions()`：把 `Properties.Settings.Default` 灌进菜单勾选态，并把 `MiHoYoBinData.Key/Encrypted`、`AssetsHelper.Minimal`、`SkipContainer`、`assetsManager.ResolveDependencies` 推到静态全局 | 设置 → 全局静态的单向同步点 |
| 7 | `MainForm.cs:101` → `244-248` | `InitializeProgressBar()`：`Progress.Default = new Progress<int>(SetProgressBarValue)`；`Studio.StatusStripUpdate = StatusStripUpdate` | 两个回调钩子都挂在实例方法上 |
| 8 | `MainForm.cs:102` → `209-242` | `InitializeLogger()`：`ConsoleHelper.AllocConsole()`，按 `enableConsole` 决定 `Logger.Default = ConsoleLogger | GUILogger`；动态生成 `loggedEventsMenuItem` 子项（跳过 `None`/`All`，`[1..^1]`） | 每个勾选项动态挂 `LoggerEventMenuItem_Click` |
| 9 | `MainForm.cs:103` → `250-322` | `InitalizeOptions()`：`selectedGame` → `Studio.Game`（越界/非法时落回索引 0）；`TypeFlags.SetTypes(types)`（JSON 坏了就 `Settings.Reset()` 再试一次，`274-282`）；UnityCN key；读 `./Maps/Z3-AssetIndex-Eleiyas.json` 填 `Studio.Paths` 与 `AssetsHelper.Paths`；恢复上次 CAB map（`309-321`） | 注释里的 Eleiyas map 是外部数据依赖 |
| 10 | `MainForm.cs:104` → `2873-2896` | `FMODinit()`：`Factory.System_Create` → 版本检查（旧版直接 `Application.Exit()`）→ `system.init(2, NORMAL)` → master sound group 音量 | 原生依赖，CLI 没有 |

### 1.2 控件清单（designer 里的真实形状）

`MainForm.Designer.cs` 共 1,734 行、94 个 `ToolStripMenuItem` 字段、159 处 `new System.Windows.Forms.*`。除菜单外只有 20 个「真控件」：

| 控件 | 声明 | 角色 | 关键事件（设计器接线处） |
| --- | --- | --- | --- |
| `menuStrip1` | `:1571`, Items `:221` | 6 个顶层菜单：File / Options / Export / Filter Type / Misc / About | — |
| `splitContainer1` | `:1573`, Dock Fill `:963` | 左（场景树）/右（列表+预览）分栏 | — |
| `tabControl1` | `:1574` | 3 页：`Scene Hierarchy`(:1006) / `Asset List`(:1042) / `Asset Classes`(:1111) | `.Selected += tabPageSelected` `:996` → 切页时把焦点丢给搜索框(`MainForm.cs:839-850`) |
| `sceneTreeView` | `:1614`，类型 `GOHierarchy` | 层级树 | `AfterCheck` `:1020`；`GOHierarchy` 只做一件事：吞掉 `WM_LBUTTONDBLCLK (0x203)`，防止双击展开时误触发父节点折叠（`Components/GOHierarchy.cs:11-16`） |
| `assetListView` | `:1581` | 6 列 Details 视图 + **虚拟模式** | `ColumnClick` `:1059`、`ItemSelectionChanged` `:1060`、`RetrieveVirtualItem` `:1061`、`MouseClick` `:1062` |
| `classesListView` | `:1616` | 2 列（Name/ID）的类型树浏览 | `ItemSelectionChanged` `:1126` |
| `treeSearch` / `listSearch` | `:1577` / `:1578` | 两个正则搜索框 | `treeSearch.TextChanged/KeyDown` `:1031-1032`；`listSearch.KeyPress` `:1102` |
| `previewPanel` | `:1588` | 图片预览容器（`BackgroundImage` 贴 `DirectBitmap.Bitmap`） | `Resize` `:1198` → `preview_Resize`（只在 GL 可见时改 viewport） |
| `glControl` | `:1621`，`OpenTK.GLControl.GLControl` | 3D 预览（与 previewPanel 同区域互斥显示） | `Load/Paint/MouseDown/MouseMove/MouseUp/MouseWheel` `:1371-1376` |
| `textPreviewBox` | `:1595` | 文本预览（shader/text/mono/json/animation） | — |
| `fontPreviewBox` | `:1596`（`RichTextBox`） | 字体预览：用 `PrivateFontCollection.AddMemoryFont` 把字体装进进程，再按 7 档字号分段渲染 | `MainForm.cs:1431-1479` |
| `classTextBox` | `:1619` | TypeTree 结构文本 | — |
| `dumpTextBox` | `:1644` | 第二个 `tabControl2` 的 Dump 页 | — |
| `FMODpanel` | `:1597` | 音频面板，含 3 Button + 2 TrackBar + 3 Label + 1 CheckBox（`:1598-1606`） | `FMOD*_Click`、`Scroll`、`MouseDown/Up`、`ValueChanged` `:1282-1341` |
| `tabControl2` | `:1641` | 预览区两页：`Preview`(:1179) / `Dump`(:1414) | `SelectedIndexChanged` `:1170` |
| `progressBar1` + `progressbarPanel` | `:1589` / `:1592` | 底部进度条（Dock Bottom） | — |
| `statusStrip1` / `toolStripStatusLabel1` | `:1590-1591` | 唯一的状态通道 | — |
| `assetInfoLabel` | `:1594` | 预览叠加信息（尺寸/格式/通道） | 可见性由 `displayInfo` 设置控制，但**接线断了**，见 §9 债务 D4 |
| `timer` | `:1607` | WinForms Timer，驱动 FMOD 播放进度 | `Tick` `:1454` |
| `contextMenuStrip1` | `:1622` | 列表右键：复制单元格 / 导出所选 / 在资源管理器定位 / 导出 Animator+Clip | `:1489-1520` |
| `previewContextMenuStrip` | `:1730` | 预览右键：复制图像（PNG + Bitmap 双格式入剪贴板，`MainForm.cs:1884-1897`） | `:1482` |
| `specifyUnityVersion` | `:1651` | 手动指定 Unity 版本（ToolStripTextBox） | 每次 Load 前读一次（`MainForm.cs:393,418,441`） |
| `specifyTheme` / `specifyAIVersion` / `MapNameComboBox` | `:1682` / `:1686` / `:1662` | 主题选择 / 从 GitHub 拉 AssetIndex 版本 / CAB map 名 | `:421`、`MainForm.cs:2335`、`MainForm.cs:309` 手接（注意：designer 没接，代码里补接） |
| `specifyGame` | `:1653` | **死字段**：只声明，从未实例化/加入任何容器 | 参见 §9 债务 D10 |

### 1.3 菜单树（含匿名 `toolStripMenuItemN` 的语义映射）

`menuStrip1` 顶层（`Designer:221`）：

```
File      : Load File / Load Folder / ─ / Extract File / Extract Folder / ─ /
            Reset / Abort / ─ / Exit                              (:230)
Options   : Game ▸ / Game Select / Unity Version ▸ / Edit UnityCN keys / ─ /
            Asset loading ▸ / Resolve dependencies / Allow duplicates /
            Use bundle container name / Skip container / ─ /
            General(标题) / Export options / App Theme ▸ / ─ /
            Misc.(标题) / Advanced ▸ / Debug ▸                   (:301)
Export    : General(标题) / All | Selected | Filtered assets / ─ /
            Format specific(标题) / Raw ▸ / Dump ▸ / JSON ▸ / ─ /
            Assets structure(标题) / Asset list to XML ▸ / Scene hierarchy / ─ /
            Models(标题) / Include AnimationClips / Merge(only selected) /
            Objects: Export all | Export selected / Nodes: Export selected   (:546)
Filter Type : All + 动态附加的类型项（运行时 PopulateTypeFilters 追加）
Misc      : Asset Map / Build Asset Map / Asset Map type ▸ / Asset Browser / ─ /
            CAB Map 组 / MapNameComboBox / Build CAB map / Load CAB map / Clear map / ─ /
            Asset Map and CAB Map 组 / 名称 TextBox / Build Both / ─ /
            Asset Index / Load from GitHub ▸ / Load AI file          (:802)
About
```

`Options ▸ Advanced`（`:437`）= `Show hidden assets` / `Preview enabled` / `Model preview enabled` / `Filter models only` / `Display asset information`。
`Options ▸ Debug`（`:488`）= `Show error messages` / `Export class structures` / `Enable console` / `Clear console` / `Enable file logging` / `Logged events ▸`。

**菜单里三个「看起来能点、其实没接线」的开关（实测 grep）**：

| 菜单项 | designer 声明 | 处理器 | 是否接线 |
| --- | --- | --- | --- |
| `Preview enabled` | `CheckOnClick=true`（`:451-459`），无 `CheckedChanged` | `MainForm.cs:744` | **否** → 勾了只改内存里的 `Checked`，设置值不落盘、当前会话预览开关行为不变 |
| `Display asset information` | `:476-484`，无 `CheckedChanged` | `MainForm.cs:805` | **否** |
| `Filter models only` | `:469-474`，无 `CheckedChanged` | `MainForm.cs:2474` | **否** → `FilterAssetList` 读的是 `Properties.Settings.Default.modelsOnly`（`MainForm.cs:2210`），所以本次会话不生效 |
| `Load from GitHub`（`toolStripMenuItem19`） | 子项 `specifyAIVersion` 只有 `"None"`（`:922`） | `MainForm.cs:2318` `DropDownOpening` | **否**（全仓无 `+= toolStripMenuItem19_DropDownOpening`）→ 版本列表永远只有 `None`，`AIVersionManager.FetchVersions/FetchAI` 在本快照内是**不可达代码** |

**12 个导出菜单项 = ExportFilter(3) × ExportType(4)**，全部在 `MainForm.cs` 里一行一个中转方法：

| ExportType | All | Selected | Filtered |
| --- | --- | --- | --- |
| `Convert`（默认，格式转换） | `:2085` | `:2090` | `:2095` |
| `Raw`（原始字节 → `.dat`） | `:2100` | `:2105` | `:2110` |
| `Dump`（对象字段文本） | `:2115` | `:2120` | `:2125` |
| `JSON`（整对象 Newtonsoft 序列化） | `:2129` | `:2134` | `:2139` |
| 资产清单（仅 XML） | `:2144` | `:2149` | `:2154` |

加 4 条模型专用入口：`ExportSplitObjects`(:2159，每个根节点一目录)、`ExportObjects*`(:1957)、`ExportMergeObjects`(:1995)、`ExportNodes`(:2041)，以及 `modelsObjectsExportSelected_Click:3371` / `modelsNodesExportSelected_Click:3393` 这两个「按勾选项组合转发」的复用入口（它们只是把 4 条路径重新拼一遍，`modelsIncludeAnimationClips` / `modelsMerge` 当开关）。

### 1.4 资产列表：虚拟 ListView 的隐式契约

- `assetListView.VirtualMode = true`（`Designer:1058`），`RetrieveVirtualItem` 直接返回 `visibleAssets[e.ItemIndex]`（`MainForm.cs:831-837`）——**列表只持有索引，不做拷贝**，所以任何对 `visibleAssets` 的重排都必须在 UI 线程且先 `SelectedIndices.Clear()`（`FilterAssetList:2189-2246`、`assetListView_ColumnClick:998-1039` 都遵守了）。
- 列顺序硬编码为 `Name / Container / Type / PathID / Size / SHA256`（`Designer:1048,1066-1090`），而子项由 `AssetItem.SetSubItems()` 一次性追加 5 个（`Components/AssetItem.cs:49-59`）。**这两处顺序是隐式契约**：`assetListView_ColumnClick` 用 `SubItems[sortColumn]` 直接索引（`MainForm.cs:1033`），`FilterAssetList` 用 `SubItems[1]`/`SubItems[3]` 做正则匹配（`MainForm.cs:2241-2242`），`UpdateContainers` 直接写 `SubItems[1]`（`MainForm.cs:2398`）。加一列/换顺序就会静默错位。
- `assetListView_ColumnClick` 对 `sortColumn==4`(Size)、`==3`(PathID) 走数值比较，其余走 `string.CompareTo`（**文化相关**比较，`:1011-1037`）——字符串列没有自然排序（"Asset10" < "Asset9"），且排序结果随当前区域变化（虽然进程被强制 en-US，见 §7.4）。
- `filterTypeToolStripMenuItem` 的类型项是**每次加载重建**的：`ResetForm` 删掉 `DropDownItems[1..]`（`MainForm.cs:1838-1842`），`PopulateTypeFilters` 再按当前 `exportableAssets` 里出现过的 `ClassIDType` 追加（`:612-628`）。

### 1.5 场景树（三层节点 + 勾选语义）

`Studio.BuildAssetData()`（`Studio.cs:308-624`）一次性构建出 `List<TreeNode>`，层级是 **物理文件(可选) → SerializedFile → GameObjectTreeNode**：

1. 按 `AssetsFile.originalPath` 分组（`:507`），有 `originalPath` 才建根节点（`:510`），否则该 assets 文件节点提升为根（`:599-609`）。
2. 每个 `GameObject` 建 `GameObjectTreeNode`（`Components/GameObjectTreeNode.cs:9-17`，`HasModel()` 的节点染 `LightBlue` 背景），并通过 `m_Transform.m_Father` 找父节点（`Studio.cs:577-591`）——**子节点先于父节点出现时用字典兜底**（`:583-587`），最终由 `parentNode.Nodes.Add(currentNode)` 挂接（`:593`）。
3. 组件级归属：`m_Components` 里的对象、以及 `MeshFilter.m_Mesh` / `SkinnedMeshRenderer.m_Mesh` 都回填 `AssetItem.TreeNode`（`Studio.cs:543-573`），实现「列表 ↔ 树」双向跳转（`goToSceneHierarchyToolStripMenuItem_Click:2071-2083`）。
4. 勾选语义是**父 → 子单向传播**：`sceneTreeView_AfterCheck` 只把 `e.Node.Checked` 写进直接子节点（`MainForm.cs:935-941`），不递归、不向上传播。`GetSelectedParentNode` 收集「被勾选的 GameObjectTreeNode」，遇到勾选节点就**不再往下递归**（`Studio.cs:1051-1064`），所以勾父即等于选整棵子树。

### 1.6 预览管线（类型 → 方法 → 后端）

入口 `PreviewAsset`（`MainForm.cs:1104-1177`），先判虚拟资产（`:1110-1114`），再按运行期类型 switch。前置清场在 `selectAsset`（`:1041-1056`）：图片复位为占位图、`textPreviewBox/fontPreviewBox/FMODpanel/glControl` 全部隐藏、`FMODreset()`。

| 资产类型 | 方法 | 后端 | 静态门槛 | 失败表现 |
| --- | --- | --- | --- | --- |
| `Texture2D` | `PreviewTexture2D:1216-1272` | `Texture2DExtensions.ConvertToImage(flip:true)` → **SkiaSharp `SKBitmap`**（`AnimeStudio/Extensions/Texture2DExtensions.cs:38`）→ `ConvertToBytes()`（`ImageExtensions.cs:45`）→ GDI+ `DirectBitmap`（`Bitmap(Format32bppArgb)` 钉住托管数组，`DirectBitmap.cs:10-17`） | `enablePreview` | 状态栏 "Unsupported image for preview" |
| `Sprite` | `PreviewSprite:1748-1762` | 同上 | `enablePreview` | 同上 |
| `AudioClip` | `PreviewAudioClip:1274-1398` | **FMOD 原生**：`system.createSound(bytes, OPENMEMORY|loopMode)` → 取第 0 个 subsound → `playSound(paused=true)` 预热，UI 按钮再真播 | `enablePreview` | 状态栏 FMOD 错误串 |
| `Shader` | `PreviewShader:1400-1410` | 文本（`Shader.Convert()`，>0xFFFFFFF 字节直接拒） | `enablePreview` | "Serialized Shader can't be read" |
| `TextAsset` | `PreviewTextAsset:1412-1417` | 文本（UTF-8 解码 + `\n`→`\r\n` + 去 `\0`） | `enablePreview` | — |
| `MonoBehaviour` | `PreviewMonoBehaviour:1419-1429` | 文本（`ToType()`；失败则弹「选 Assembly 文件夹」`Studio.cs:1066-1082`） | `enablePreview` | 对话框 |
| `Font` | `PreviewFont:1431-1479` | `AddFontMemResourceEx` + `PrivateFontCollection`，7 档字号分段 | `enablePreview` | "Unsupported font for preview" |
| `Mesh` | `PreviewMesh:1481-1629` | **OpenGL**（顶点/索引/法线/顶点色 → VAO） | `enablePreview`（不受 `enableModelPreview` 约束） | "Unable to preview this mesh" |
| `GameObject` | `PreviewGameObject:1631-1645` | OpenGL，先跑 `ModelConverter` | **`enableModelPreview`**（`:1118` 的 `when`） | "Unable to preview this model" |
| `Animator` | `PreviewAnimator:1646-1660` | OpenGL，同上 | **`enableModelPreview`**（`:1149`） | 同上 |
| `AnimationClip` | `PreviewAnimationClip:1662-1668` | 文本（`.anim` YAML） | `enablePreview` | "Legacy animation is not supported" |
| `VideoClip` / `MovieTexture` | — | 不支持 | — | 状态栏 "Only supported export."（`:1142-1145`） |
| `MiHoYoBinData` | `PreviewText(m.AsString):1156-1159` | 文本 | — | — |
| `NapAssetBundleIndexAsset` | `PreviewText(DumpAsset(...)):1160-1162` | 文本 | — | — |
| 其他 | `asset.Dump()` 兜底 `:1163-1170` | 文本 | — | 无输出 |

**预览格式结论**：图片 = 任意能被 `Texture2DConverter` 解出的纹理格式（DXT/BC/ETC/ASTC/RGBA…），屏幕预览走 32bpp BGRA；模型 = 只渲染**顶点色/法线**，没有 UV、没有材质、没有贴图（`CreateVAO` 只上传 position/normal/color，`MainForm.cs:3220-3242`）；音频 = 内存 + FMOD 解码；shader = 反编译后文本，不做实时编译。

### 1.7 OpenGL 预览内部（OpenTK 4 GLControl）

- 资源：3 个 program，GLSL 源码作为**字符串资源**内嵌在 `Properties/Resources.resx`（`vs`/`fs`/`fsColor`/`fsBlack`，见 `Properties/Resources.Designer.cs`），通过 `ResourceManager.GetObject(filename)` 取（`MainForm.cs:3170-3178`）。`InitOpenTK` 在 `glControl.Load` 里只跑一次（`:3260-3264`），建 `pgmID`(光照)/`pgmColorID`(顶点色)/`pgmBlackID`(线框) 三个程序并缓存 attrib/uniform location（`:3143-3168`）。
- 数据装配：`CreateVAO` 每次预览重建 VAO，先 `GL.DeleteVertexArray(vao)` 再建 VBO(position/normal/color + 3 个 uniform matrix) 与 EBO（`:3220-3242`）。注意 `CreateVBO(..., Matrix4, ...)` **不建 buffer，只 `UniformMatrix4`**（`:3204-3208`），命名有误导性；且 VBO/EBO 句柄从不显式删除 → 每切一次模型泄漏一批 GL 对象。
- 顶点法线自算：`PreviewMesh:1557-1583` 与 `PreviewModel:1708-1731` 各写了一遍「叉积累加 + 归一化 + 零退化置 (0,1,0)」，`Ctrl+N` 在「自算/原始」之间切换（`MainForm.cs:666-671` + `CreateVAO:3226-3234`）。
- 绘制：`glControl_Paint` 先 `MakeCurrent`，深浅测试 + `Lequal`，再按 `wireFrameMode`(0 填/1 线/2 填+线) 与 `shadeMode`(0 光照/1 顶点色) 组合绘制（`:3266-3298`）；线框用 `PolygonOffsetLine` 反向偏移避免 z-fighting（`:3285-3293`）。
- 交互：左键旋转（`viewMatrixData *= RotationX/Y`）、右键平移（`*= Translation`）、滚轮缩放（`*= Scale(1+delta/1000)`），全部**累乘到 viewMatrix 上**（`:3300-3358`）——没有累积误差校正，长时间旋转会漂移；`ChangeGLSize` 用 `Matrix4.CreateScale` 造正交投影（`:3244-3258`），所以预览是**正交而非透视**。
- 键盘：`Ctrl+W/S/N` 只作用于 GL 页（`MainForm.cs:650-674`），`Ctrl+R/G/B/A` 只作用于图片页（切通道，`675-708`）。

### 1.8 音频预览（FMOD 直调）

`AnimeStudio.Utility/FMOD Studio API/` 里是**手抄的 FMOD C# 绑定**（`fmod.cs` 3,865 行、`fmod_dsp.cs` 1,617 行），通过 `AnimeStudio.Libraries/AnimeStudio.FMOD.dll` 分发（`GUI.csproj:43-48`）。生命周期：`FMODinit`（构造时）→ `FMODplay/pause/stop` 调 `channel.*` → `timer_Tick` 每 tick 读 `getPosition` 并把进度条/标签同步（`MainForm.cs:3093-3128`，注意 `FMODprogressBar.Value = (int)(ms * 1000 / FMODlenms)`，`FMODlenms==0` 时会除零）→ `ERRCHECK` 统一 `FMODreset()` 并在状态栏报错（`:3130-3139`）。

### 1.9 线程 / 异步模型（这是 GUI 最大的结构性问题）

- **没有 `BackgroundWorker`、没有 `Parallel.*`、没有 `ConfigureAwait`、没有 `SemaphoreSlim`**（全仓 grep 为 0）。并发模型只有两种：`await Task.Run(...)` 与裸 `Task.Run`。
- **20 个 `async void`**：`MainForm.cs` 17 个（`:331,387,410,429,448,464,481,2248,2318,2335,2528,2551,2606,2774,2795,2814`）+ `AssetBrowser.cs` 3 个（`:43,213,560`）。全部是事件处理器，异常只能落到 `SynchronizationContext` 的未捕获异常，没有 try/catch 兜底也没有重入保护——防重入靠**手写禁用菜单项**：`InvokeUpdate(item,false) … InvokeUpdate(item,true)`（`MainForm.cs:2412-2422`）。
- 典型加载链（`LoadPaths:387-408`）：`SizeWarning` → `ResetForm` → `assetsManager.SpecifyUnityVersion/Game` → `await Task.Run(LoadFolder|LoadFiles)` → `BuildAssetStructures`，其中又 `await Task.Run(BuildAssetData)` + `await Task.Run(BuildClassStructure)`（`:499-500`）。UI 线程只做「装配 TreeNode/ListView」。
- 跨线程 UI 更新的两个模式：
  1. `StatusStripUpdate` / `SetProgressBarValue`：`if (InvokeRequired) { var r = BeginInvoke(...); r.AsyncWaitHandle.WaitOne(); }`（`MainForm.cs:1782-1809`）——**同步等待 UI 线程**。因为 `Progress<int>` 与回调本身都在 UI 线程构造，正常情况下 `InvokeRequired==false`；但只要有人从后台线程直接调，就会阻塞直到 UI 泵一次消息（在 UI 线程被阻塞时即死锁）。
  2. `_parent.Invoke(...)` 从 `AssetBrowser` 反向操作主窗体（`AssetBrowser.cs:209-210,221`）。
- 取消：唯一机制是 `abortStripMenuItem_Click` 里取消两个 `CancellationTokenSource`（`MainForm.cs:2767-2772`），而 GUI 的检查点密布在 `BuildAssetData` 的每个对象循环里（`Studio.cs:327,450,470,491,518,631`）——粒度是「每个 Object」，取消延迟可达数秒。取消后返回**空集合**而不是抛 `OperationCanceledException`（`Studio.cs:330`），于是 UI 会显示 0 资产且不解释。
- 进度：`Progress` 是**全局单例**（`Progress.cs:4-36`），`Report` 只接受单调递增值（`Progress.cs:28-35`），没有任何「进度域」概念。因此 GUI 里并发两件事（例如 AssetBrowser 边导出边构建）会互相污染进度条。

### 1.10 主题

`GuiColorTheme {System, Dark, Light}`（`Studio.cs:18-23`）→ 存 `guiTheme`（`Properties/Settings.Designer.cs:500`）→ `ApplyTheme`（`MainForm.cs:106-140`）。实现只有三件事：`Application.SetColorMode()`、`assetListView.GridLines`、`assetInfoLabel.ForeColor`。`System` 档读注册表 `HKCU\...\Themes\Personalize\AppsUseLightTheme`（`:142-155`）。切换时会明确告知「需重启」（`:176-183`），且 `suppress` 首次触发用 `themeIsFirstLaunch`（`:48,159-163`）——因为 `InitalizeOptions` 会 `specifyTheme.SelectedIndex = guiTheme` 触发一次 handler。

---

## 2. CLI 形状

### 2.1 System.CommandLine 装配

`CommandLine.Init`（`Components/CommandLine.cs:13-18`）：

```csharp
var rootCommand = RegisterOptions();
var parseResult = CommandLineParser.Parse(rootCommand, args, new ParserConfiguration());
parseResult.Invoke(new InvocationConfiguration());     // ← 返回值 int 被丢弃
```

`RegisterOptions`（`:19-45`）往 `RootCommand` 加 **15 个 Option + 2 个 Argument**，随后 `rootCommand.SetAction(parseResult => Program.Run(optionsBinder.GetOptions(parseResult)))`（`:42`）。`OptionsBinder` 是手写的「注册 + 校验 + 绑定」三合一（`:68-330`），`Options` 是纯 DTO（`:47-66`）。

> **退出码问题（重要）**：`Invoke` 返回的 int 被丢掉，而 `SetAction` 的 lambda 返回 `void`，`Program.Run` 也是 `void` 并吞掉所有异常（`Program.cs:202-205`）。结论：**CLI 的进程退出码永远是 0**（除非解析阶段有错误，此时 System.CommandLine 自己 `Environment.Exit`）。任何 CI 都无法靠退出码判断导出失败。

### 2.2 完整 flag 清单（从代码提取，不信 README）

| Flag | 类型 | 默认 | 校验 / 特殊解析 | 消费点 |
| --- | --- | --- | --- | --- |
| `--silent` | `bool` | `false` | — | `Program.cs:122` → `assetsManager.Silent`（**只静默 core**，不动 `Logger.Silent`） |
| `--logger_flags` | `LoggerEvent[]`，`AllowMultipleArgumentsPerToken` | `Debug,Info,Warning,Error`（**不含 Verbose**） | `FilterValidator`（当正则校验，见 §2.3） | `Program.cs:45` `Aggregate((e,x)=>e|=x)` |
| `--types` | `string[]`，多参 | 无 | `FilterValidator` | `Program.cs:52-115`（`Type:Flag` 语法） |
| `--names` | `Regex[]`，多参 | 无 | `ParseRegexFilter`：参数可以是「文件路径」，逐行当正则 | `Program.cs:163,188` → `BuildAssetData` |
| `--containers` | `Regex[]`，多参 | 无 | 同上 | 同上 |
| `--game` | `string`，**Required** | 无 | 必须在 `GameManager.GetGameNames()` 内；挂 `CompletionSources` | `Program.cs:20`、`:37-41` UnityCN key |
| `--map_op` | `MapOpType`（Flags） | `None` | — | `Program.cs:147-174` |
| `--map_type` | `ExportListType`（Flags） | `XML` | — | `Program.cs:163,167,172` |
| `--map_name` | `string` | `"assets_map"` | — | 同上传给 `AssetsHelper.*` |
| `--unity_version` | `string` | 无 | — | `Program.cs:48,124`（`AssetsHelper.SetUnityVersion` / `SpecifyUnityVersion`） |
| `--group_assets` | `AssetGroupOption` | `ByType` | — | `Program.cs:189` |
| `--export_type` | `ExportType` | `Convert` | — | `Program.cs:189` |
| `--key` | `byte` | 无 | `ParseKey`：`0x` 前缀按十六进制，否则十进制 | `Program.cs:127-131` → `MiHoYoBinData.Key/Encrypted` |
| `--ai_file` | `FileInfo` | 无 | `LegalPathValidator` | `Program.cs:133-136`（仅 `Game.Type.IsGISubGroup()`） |
| `--dummy_dlls` | `DirectoryInfo` | 无 | `LegalPathValidator` | `Program.cs:138-141` → `assemblyLoader.Load` |
| `input_path` | `FileInfo`（位置参数） | 无 | `AcceptLegalFilePathsOnly()`；运行时既接受文件也接受目录（`Program.cs:144` 用 `FileAttributes.Directory` 判断） | |
| `output_path` | `DirectoryInfo`（位置参数） | 无 | `AcceptLegalFilePathsOnly()` | `Program.cs:125` `o.Output.Create()` |

`MapOpType` 是 Flags（`Studio.cs:14-23`）：`None=0, Load=1, CABMap=2, AssetMap=4, Both=8, All=Both|Load`。因此 `--map_op CABMap,Load` / `--map_op AssetMap,Load` / `--map_op All` 都是合法组合，语义在 `Program.cs:147-174` 里靠 `HasFlag` 分支出 **5 条互不相同的执行路径**。

### 2.3 三个「不写在帮助里」的参数语义

1. `--types Type:Flag`：`Shader:Parse`、`Sprite:Both`、`Texture2D:Export`（`HelpName` 已暗示，`:104`）。实现是 `split(':')` + `Enum.Parse(TypeFlag, param[1], true)`（`Program.cs:66-86`），解析失败只打一条 `Logger.Error("... invalid format, skipping...")` 并 `continue`（`:90-94`）——**静默降级**，拼错类型名等于没过滤。另外给了 `GameObject`/`Animator` 时会自动补 `Texture2D`(parse=true)/`Material`/互相的 parse 标志（`:99-114`），且 `--group_assets ByContainer` 会强制 `AssetBundle` parse=true（`:117-120`）。
2. `--names` / `--containers` 支持「文件当过滤器列表」：单个参数且是已存在文件时，逐行读正则（`CommandLine.cs:269-290`），空行跳过、非法正则**静默忽略**（`:286-288`）。这一条对批量导出很实用，但 README 完全没写。
3. `--logger_flags` 的校验器 `FilterValidator` 是把值当**正则**去 `Regex.Match("", val)` 试一遍（`CommandLine.cs:225-251`），**并不校验枚举名**；因此拼错枚举名不会得到「无效的 LoggerEvent」提示，只会落到 System.CommandLine 的绑定错误上，而 `Init`（`:16-17`）既不检查 `parseResult.Errors` 也不看 `Invoke` 返回值，用户侧表现就是不明的空跑或未处理异常。另一个必踩点：`Program.cs:45` 的 `LoggerFlags.Aggregate((e,x)=>e|=x)` 在数组为空时抛 `InvalidOperationException`（只写 `--logger_flags` 不带值即可触发）。

### 2.4 设置优先级：`flag > App.config > 代码默认`（但 20+ 项无法用 flag 覆盖）

CLI 的设置来自 `AnimeStudio.CLI/App.config` 的 `<appSettings>`（25 个 key，`:4-28`），经手写门面 `AppSettings.Get<T>(key, default)` 读取（`Settings.cs:6-31`），**与 GUI 的 `Properties.Settings.Default` 是两套完全独立的存储**。其中 `exportAll`(:4) 与 `openAfterExport`(:5) 是**没人读的死键**（`Settings.cs` 里没有对应属性）；反之 `collectAnimations` 在 `Settings.cs:47` 有属性（默认 `true`）但 App.config 里**没有这个 key** —— 又一个「看起来可配、其实配不到」的陷阱。

| 优先级 | 载体 | 例子 |
| --- | --- | --- |
| 1 | CLI flag | `--unity_version` → `AssetsHelper.SetUnityVersion`(`Program.cs:48`)、`--key` → `MiHoYoBinData.Key`(`:127`)、`--types` → `TypeFlags`(`:86`)、`--group_assets`/`--export_type`(`:189`)、`--silent`(`:122`) |
| 2 | `App.config` appSettings | `convertTexture`、`convertType`、`exportMaterials`、`collectAnimations`、`uvs`、`texs`、`types`、`minimalAssetMap`、`allowDuplicates`、`restoreExtensionName`、`enableFileLogging`、`scrapeMonos`、全部 FBX 选项（`App.config:4-28`） |
| 3 | `Settings.cs` 里的字面默认（`AppSettings.Get(key, default)`，`:39-62`） | 与 App.config 值一致，仅在配置缺项/坏值时兜底（坏值时打印 `Invalid value at "key"...`，`Settings.cs:24-28`） |

**不能被 flag 覆盖的配置项（fork 时最想改的就是这些）**：`convertTexture`、`convertType`、`convertAudio`、`allowDuplicates`、`restoreExtensionName`、`eulerFilter`/`filterPrecision`/`exportAllNodes`/`exportSkins`/`exportAnimations`/`exportBlendShape`/`castToBone`/`boneSize`/`scaleFactor`/`fbxVersion`/`fbxFormat`（整套 FBX）、`collectAnimations`、`exportMaterials`、`uvs`、`texs`、`scrapeMonos`。也就是说：**想「纯命令行+无配置文件」跑到不同输出格式，必须先把 `convertType` 提升成 flag**（现状只能改 `App.config` 或用 GUI）。

### 2.5 执行流（CLI 是流式，GUI 是全量）

```
Program.Run (Program.cs:16)
 ├─ 读 ./Maps/Z3-AssetIndex-Eleiyas.json（可选）        :23-28
 ├─ 校验 game，UnityCN key                             :30-41
 ├─ Logger / TypeFlags / AssetsHelper.Minimal           :44-50
 ├─ --types 解析 → classTypeFilter[]                    :52-115
 ├─ assetsManager.Silent/Game/SpecifyUnityVersion；建输出目录 :122-125
 ├─ --key / --ai_file / --dummy_dlls                    :127-141
 ├─ 扫文件（目录递归，按文件长度升序！）                 :143-145
 ├─ map 分支 5 条（CABMap/AssetMap/Both × Load/Build）   :147-173
 └─ 默认导出分支：逐文件 LoadFiles → BuildAssetData → ExportAssets → Clear :174-194
```

关键细节：`Directory.GetFiles(...).OrderBy(x => x.Length)`（`Program.cs:144`）——**按文件大小排序**，注释没说为什么，实际效果是小文件先出结果（先给用户反馈）且内存峰值低；这是 CLI 唯一「隐式调度策略」。

`foreach (var file in fileList)` 里的三步循环（`Program.cs:183-193`）意味着 **CLI 每次只持有一个文件的资产表**，`exportableAssets.Clear()` + `assetsManager.Clear()` 后立刻释放；GUI 则是「全部文件 → 一个全局 `exportableAssets` + 一棵全局树」。两者内存量级不同（10× 以上），这是两条不可忽略的架构差异。

### 2.6 与 README 的偏差（fork 前必须纠正的文档债）

| README 说法 | 代码事实 |
| --- | --- |
| `--types Texture2D,Sprite`（`README.md:33`） | 逗号会被当成一个 token → `Enum.Parse("Texture2D,Sprite")` 失败 → 静默跳过（`Program.cs:90-94`）。**正确写法是空格分隔**：`--types Texture2D Sprite`（`AllowMultipleArgumentsPerToken=true`，`CommandLine.cs:97,103,109,115`） |
| "Run it without arguments for the full list"（`README.md:36`） | `--game`、`input_path`、`output_path` 都 Required（`CommandLine.cs:121,156-163`），裸跑得到的是**错误信息 + usage**，不是完整列表 |
| 未提及 | `--ai_file`、`--dummy_dlls`、`--map_name`、`--unity_version`、`--key`、`--logger_flags`、`--map_type MessagePack` 全部缺失；README 只列了 8 个 flag，实际 15 个 |

---

## 3. 导出管线（ExportType × 资产类型的完整矩阵）

### 3.1 `ExportType` 的语义

`ExportType { Convert, Raw, Dump, JSON }`（`AnimeStudio/ExportType.cs:3-9`），四者共享同一个外层循环（GUI `Studio.cs:727-753` / CLI `Studio.cs:430-456`），差异只在分发函数：

| ExportType | 分发函数 | 输出 | 备注 |
| --- | --- | --- | --- |
| `Convert` | `ExportConvertFile`（GUI `Exporter.cs:646-685` / CLI `:565-602`） | 按类型转成可读格式 | **默认值**（`CommandLine.cs:207`） |
| `Raw` | `ExportRawFile` → `item.Asset.GetRawData()` + `.dat`（GUI `:438-444` / CLI `:390-396`） | 原始字节 | 所有类型统一 `.dat`，不猜扩展名 |
| `Dump` | `ExportDumpFile` → `asset.Dump()` + `.txt`（GUI `:628-644` / CLI `:552-563`） | 字段级文本 | GUI 有 MonoBehaviour TypeTree 回退（`:633-637`），**CLI 没有** |
| `JSON` | `ExportJSONFile` → Newtonsoft 序列化整个 Object（GUI `:687-697` / CLI `:604-614`） | `.json` | 与实际资产文件不同名不同内容，适合做 diff |

### 3.2 `Convert` 的类型分派表

| `ClassIDType` | 处理函数 | 输出 | GUI≠CLI 的差异 |
| --- | --- | --- | --- |
| `GameObject` | `ExportGameObject` | 目录 + `{name}.fbx`（`ModelConverter` → `IImported` → `Fbx.Exporter`） | GUI 多一个 `ExportGameObjectMerge`（`Exporter.cs:580-607`，多 GameObject 合并一个 FBX） |
| `Animator` | `ExportAnimator` | GUI：目录内 `{item.Text}.fbx`（`Exporter.cs:506`）；CLI：**直接把目录路径当 FBX 文件名**（`Exporter.cs:455-486`，没有 `.fbx` 拼接）→ CLI 的 Animator 导出产出的是无扩展名文件 |
| `Texture2D` | `ExportTexture2D` | `.png/.jpg/.bmp/.tga/.hdr`，或 `.tex`（`convertTexture=false`） | GUI 有 HDR 手写 radiance 编码（`:80-157`），CLI 完全没有；GUI 还处理 `RGBAHalf` 半浮点翻转 |
| `Sprite` | `ExportSprite` | 按 `convertType` 输出图片（`GetImage()`） | 相同 |
| `AudioClip` | `ExportAudioClip` | `convertAudio && converter.IsSupport` → `.wav`，否则原压缩扩展名 | 相同（`AudioClipConverter`，`AnimeStudio.Utility/AudioClipConverter.cs`） |
| `VideoClip` | `ExportVideoClip` | 原扩展名，`m_VideoData.WriteData` | 相同（需外部资源 `m_Size>0`） |
| `MovieTexture` | `ExportMovieTexture` | `.ogv` | 相同 |
| `Font` | `ExportFont` | `.ttf`，首 4 字节为 `OTTO` 则 `.otf` | 相同（`79,84,84,79` 魔数，`Exporter.cs:302`） |
| `Mesh` | `ExportMesh` | **单文件 `.obj`**（v/vt/vn/f，X 轴取负） | 相同，逐字符同源；`NaN`→`0`（`:343`） |
| `Shader` | `ExportShader` | `.shader`（`Shader.Convert()`） | 相同 |
| `TextAsset` | `ExportTextAsset` | `.txt`，`restoreExtensionName` 时用 `item.Container` 的扩展名 | 相同 |
| `MonoBehaviour` | `ExportMonoBehaviour` | GUI：`.json`（`ToType`，必要时弹 Assembly 目录）；CLI：`.json` **或** `scrapeMonos` 模式（CLI `Exporter.cs:111-178`，只刮字符串并 `return true`，**不写文件**，末尾由 `Program.cs:195-200` 汇总写 `Maps/*.txt`） | **最大行为分歧之一** |
| `Material` | `ExportJSONFile` | `.json` | 相同 |
| `NapAssetBundleIndexAsset` | `ExportJSONFile` | `.json` | **只有 GUI 有**（`:680-681`），CLI 会掉到 `default` → `.dat` |
| `MiHoYoBinData` | `ExportMiHoYoBinData` | JSON 类型→`.json`；Bytes 类型→`.bin`（`restoreExtensionName` 时用容器扩展名） | 相同 |
| `AnimationClip` | `ExportAnimationClip` | `.anim`（YAML） | 相同 |
| 其他 | `ExportRawFile` | `.dat` | 相同 |

### 3.3 分组选项（`AssetGroupOption`）

`{ByType, ByContainer, BySource, None}`（`AnimeStudio/AssetGroupOption.cs:3-9`），GUI `Studio.cs:679-712` / CLI `Studio.cs:396-425`：

| 值 | 输出路径 | 备注 |
| --- | --- | --- |
| `ByType` | `{out}/{TypeString}/` | 默认 |
| `ByContainer` | `{out}/{Container 目录}`；容器为空回退：**GUI → `{out}/`（根）**、**CLI → `{out}/{TypeString}/`** | 又一处静默分歧；`ByContainer` 要求先把容器名解析出来（`AssetBundle.m_Container` / `ResourceManager.m_Container` / AI map） |
| `BySource` | `{out}/{源文件名}_export/{assetsFileName}/`（GUI 额外处理 `IsVirtual` → `{ExternalPath 文件名}_export/`） | 最稳定但目录最深 |
| `None` | `{out}/` 平铺（`default:` 分支） | 重名冲突全靠 `allowDuplicates` |

GUI 侧还有个耦合：分组值**不是参数而是全局设置**（`Properties.Settings.Default.assetGroupOption`，`Studio.cs:680`；赋值处 `ExportOptions.cs:74`），而 CLI 是显式参数（`Studio.cs:390`）——同一段代码两种签名。

### 3.4 文件名与去重策略（fork 高频踩坑点）

```
TryExportFile(dir, item, ext)                      GUI Exporter.cs:446-467 / CLI :398-419
  fileName = FixFileName(item.Text)
  fullPath = dir/fileName+ext
  if (!File.Exists(fullPath)) { Directory.CreateDirectory(dir); return true; }
  if (allowDuplicates) for (i=1; ; i++)  ← GUI 从 1 开始，CLI 从 0 开始
      → "name (1).png" / CLI "name (0).png"
  return false   ← 既没写文件也没报错，静默计为 skipped
```

`FixFileName`（GUI `:699-703` / CLI `:616-620`）：`length>=260` → `Path.GetRandomFileName()`（**随机名！**266 字符的资产名会变成随机 8.3 名，无法追溯），否则把 `Path.GetInvalidFileNameChars()` 全部替换为 `_`。

### 3.5 模型 / FBX 路径

```
GameObject / Animator
  → ModelConverter.Options { imageFormat, game, collectAnimations, exportMaterials,
                             materials, uvs, texs }        GUI Exporter.cs:546-558
  → new ModelConverter(gameObject|animator, options[, AnimationClip[]])
  → convert.MeshList.Count == 0 → 跳过并记 Info      :560-564
  → exportMaterials == true → 在输出目录下建 Materials/ 并 ExportJSONFile 每个 Material  :565-574
  → ExportFbx(convert, path) → Fbx.ExportOptions { eulerFilter, filterPrecision, exportAllNodes,
        exportSkins, exportAnimations, exportBlendShape, castToBone, boneSize, scaleFactor,
        fbxVersion, fbxFormat }                          :609-626
  → ModelExporter.ExportFbx → Fbx.Exporter.Export → 原生 FBXNative(需 FBX SDK 2020.3.7)
```

`uvs`/`texs` 两个「字符串化的 JSON 字典」是配置里的脏数据典型：每次导出都 `JsonConvert.DeserializeObject` 一遍（`Exporter.cs:553-554`），解析失败直接抛到外层 `catch`，整批导出该项失败。默认值在 `App.config:26-28`（`uvs` 是 8 个 `{"Item1":bool,"Item2":int}` 元组）。

#### 3.5.1 并行导出的硬阻塞（fork 若想提速必看）

`Fbx.Exporter.Export` 在写文件前**切换整个进程的当前目录**，写完再切回来：

```csharp
var currentDir = Directory.GetCurrentDirectory();
Directory.SetCurrentDirectory(dir.FullName);   // ← 进程级副作用
using (var exporter = new FbxExporter(name, imported, exportOptions)) { exporter.Initialize(); exporter.ExportAll(); }
Directory.SetCurrentDirectory(currentDir);
```

（`AnimeStudio.FBXWrapper/Fbx.cs:41-52`）。而 `FbxExporter` 的上下文把 `Directory.GetCurrentDirectory()` 当作基准目录（`AnimeStudio.FBXWrapper/FbxExporter.cs:19`）。后果：

1. **模型导出天然不可并行**——两个线程同时导出会互相把 CWD 切走，输出跑到别的目录（或抛 `DirectoryNotFoundException`）。这就是为什么 GUI/CLI 都老老实实串行（GUI `Studio.cs:677` 的 `foreach`，CLI `Program.cs:183` 的 `foreach`）。
2. `ExportAnimator` 的 CLI 路径 bug 在这里得到解释：`Export(path=目录)` → `name = 目录名`（`Fbx.cs:44`）→ 生成的 FBX 文件名等于目录名、且**无 `.fbx` 后缀**。
3. 想并行化的话，正确改法是给原生层传绝对路径并在 `FbxExporterContext` 里去掉对 CWD 的依赖（这属于 native wrapper 改动，比 app 层重构风险高）。

GUI 的 4 条模型导出入口对应不同装配方式（都在 `GUI/Studio.cs`）：

| 入口 | 语义 | 装配 |
| --- | --- | --- |
| `ExportAssets`(Convert) → `ExportGameObject(item)` | 单个 GameObject 一目录 | `TryExportFolder` |
| `ExportObjectsWithAnimationClip:947-985` | 勾选的每个 GameObject 一子目录，可带 selected AnimationClips | `GetSelectedParentNode` |
| `ExportObjectsMergeWithAnimationClip:987-1010` | 勾选的多个 GameObject 合成一个 FBX | `ExportGameObjectMerge(rootName,…)` |
| `ExportNodesWithAnimationClip:1012-1049` | 以根节点为单位 split，节点名做文件名 | `ExportGameObjectMerge` + `FixFileName` |
| `ExportSplitObjects:829-912` | 「Export all objects (split)」：遍历整棵树，跳过无 Mesh/无 SkinnedMesh 的子树 | 手写 `GetNodes` 迭代器 + 重名 `" ({i})"` |

### 3.6 资产清单与资产地图（两条完全不同的导出）

| 功能 | GUI | CLI | 格式 |
| --- | --- | --- | --- |
| 「Asset list」（当前加载结果的清单） | `Studio.ExportAssetsList:779-827`，**只有 XML**，文件名硬编码 `assets.xml`（`:790`），多写 `Size` 字段 | `Studio.ExportAssetsMap:474-521`，**XML + JSON**，文件名来自 `--map_name`，JSON 用 `StringEnumConverter` | XML 元素：`Asset/Name,Container,Type(@id),PathID,Source[,Size]` |
| 「Asset Map / CAB Map」（对整目录索引） | `AssetsHelper.BuildAssetMap/BuildBoth`，类型由 `assetMapType` 位掩码决定 | `--map_op` + `--map_type`（`None/MessagePack/XML/JSON`） | MessagePack 用 `Lz4BlockArray` 压缩（`AssetsHelper.cs:725-728`），GUI 的 AssetBrowser 能读 `.map` 与 `.json`（`AssetBrowser.cs:47`） |

注意 GUI 侧「Asset list to XML」的三个菜单项（`MainForm.cs:2144-2157`）调用 `ExportAssetsList(Filter, ExportListType.XML)`，但 `ExportAssetsList` 的 `exportListType` 参数在方法体里**没被使用**（`Studio.cs:789-816` 只有 `case ExportListType.XML`），且方法签名里 `ExportListType` 的实参恒为 XML（`MainForm.cs:2305`）。注释还留着 `// XXX: Only exporting as XML for now`（`:2282`）。

---

## 4. GUI vs CLI：复制粘贴的量化与逐项分歧

### 4.1 量化（实测非空行多重集交集）

| 文件对 | GUI 行 / 非空 | CLI 行 / 非空 | 完全相同的非空行 | 占 CLI 比例 |
| --- | --- | --- | --- | --- |
| `Exporter.cs` | 705 / 649 | 622 / 576 | **498** | **86%** |
| `Studio.cs` | 1,108 / 1,036 | 528 / 497 | **311** | **63%** |

方法级对照（`●`=同名同责，`◐`=同名不同实现，`○`=仅一侧存在）：

| 方法 | GUI | CLI | 备注 |
| --- | --- | --- | --- |
| `ExtractFolder` / `ExtractFile(string)` / `ExtractFile(string[])` | ● | ● | GUI 额外挂 `Progress.Report`（`Studio.cs:54,67`）与 `StatusStripUpdate`；GUI 额外支持 `Blb3File`/`VFSFile`（`:90-93,192-197`）与 AFK 松散文件（`:74-77`） |
| `ExtractBundleFile/WebDataFile/BlkFile/BlockFile/MhyFile/StreamFile` | ◐ | ◐ | GUI 版本多 `fileList == null`、`file.stream == null` 防御（`:245-262`）与每步 `StatusStripUpdate`；CLI 用 `Logger.Info` |
| `UpdateContainers` | ◐ | ◐ | GUI 多 ZZZ 分支（`ulong` + `Paths` 查表，`:281-285`）并同步刷新 `SubItems[1]`（`MainForm.cs:2398` 另有一份 `MainForm.UpdateContainers`，**同名三份**） |
| `BuildAssetData` | ●(返回值+树) | ●(void+ref int) | GUI 返回 `(productName, TreeNode[])`，读 `PlayerSettings.productName`（`:364-367`）；CLI 无 |
| `ProcessAssetData` | ● | ● | **第三份在 `AssetBrowser.cs:287-368`** |
| `ExportAssets` | ◐(返回 Task) | ◐(同步) | GUI 包 `Task.Run` + `CultureInfo en-US` + `IsVirtual` 分支 + `OpenFolderInExplorer`；分组值来源不同（§3.3） |
| `ExportAssetsList` vs `ExportAssetsMap` | ◐ | ◐ | 见 §3.6 |
| `MonoBehaviourToTypeTree` | ◐ | ◐ | GUI 会**弹目录选择对话框**（`Studio.cs:1066-1082`），CLI 直接用已加载的 `assemblyLoader`（`:523-526`） |
| `ExportGameObjectMerge` / `ExportVirtual*` / `DumpAsset` / `ExportNodes*` / `ExportSplitObjects` / `GetSelectedParentNode` / `OpenFolderInExplorer` / `BuildClassStructure` | ○ | — | GUI 特有 |
| `ExportAssetsMap`(带名字) / `ExportAssetsMap` JSON 分支 / `ParseKey` … | — | ○ | CLI 特有 |

### 4.2 逐项行为分歧（12 条，fork 时必须选边）

| # | 分歧点 | GUI | CLI | 影响 |
| --- | --- | --- | --- | --- |
| 1 | 「非可导出但 CanExport」的资产是否出现 | 需要 `displayAll` 设置（`Studio.cs:441`） | **无条件**：`if (!exportable && assetItem.Type.CanExport()) exportable = true;`（`Studio.cs:373-378`） | 同一份输入，两个前端的「有多少资产」不同 |
| 2 | MonoBehaviour `Dump` | 有 TypeTree 回退（`Exporter.cs:633-637`） | 无 → 返回 false，计为 skipped（`:552-563`） | CLI 的 `--export_type Dump` 对 mono 基本无效 |
| 3 | Texture HDR | `RGBAHalf` + `enableHDR` → 手写 `.hdr`（`:80-157`） | 无此分支 | 光照贴图类资产只能 GUI 导 |
| 4 | AFK Journey 松散资产（`.dxt`/`.jsone`） | 虚拟资产全链路（`Studio.cs:74-77`、`MainForm.cs:562-610,1179-1200`、`Exporter.cs:14-73`） | **完全不支持** | CLI 无法处理 AFK 目录 |
| 5 | `NapAssetBundleIndexAsset` | `.json` | `.dat`（掉 default） | 输出格式不一致 |
| 6 | Animator FBX 路径 | 目录 + `{Text}.fbx`（`:506`） | 目录路径直接当文件（`CLI :455-486`） | CLI 产物无 `.fbx` 后缀 |
| 7 | 去重序号 | 从 `(1)` 起 | 从 `(0)` 起 | 同输入产物文件名不同，脚本对不上 |
| 8 | `ByContainer` 空容器回退 | 输出根目录 | `{TypeString}/` | 目录结构不一致 |
| 9 | Preload table 越界 | `preloadIndex<0` 警告 + `try/catch`（`Studio.cs:378-399`） | 直接索引，可能抛 `IndexOutOfRange`（`CLI :333-339`） | CLI 对畸形 bundle 更脆 |
| 10 | 依赖解析 | 默认 `ResolveDependencies = true`（设置项，`MainForm.cs:202`） | 默认 `false`，仅 map Load 时置 true（`Studio.cs:29`、`Program.cs:156`） | 同输入可能拿到不同依赖集 |
| 11 | `containerName` 开关 | 算了 `containerName` 却仍存 `m_Container.Key`（`Studio.cs:386-393` ⇒ 「Use bundle container name」对容器**无效**，只对 MiHoYoBinData 生效 `:461`） | 无此开关 | GUI 的开关是半个假开关 |
| 12 | 进度/静默 | `Progress` + 状态栏 + `StatusStripUpdate` 委托 | 无（`Progress.Default` 是空 `Progress<int>`，`Progress.cs:7`；`--silent` 只静默 core） | CLI 全程无进度，长任务像卡死 |

### 4.3 建议的重构形状（给 fork 的「Application 库」）

> 目标不是「消灭重复」，而是把**行为契约**收成一份，把**表现差异**变成参数。

```
AnimeStudio.Application   (net9.0-windows;net10.0-windows，无 WinForms/OpenTK/FMOD 引用)
├─ Abstractions/
│   ├─ IProgressSink        { void Report(int current,int total); void Status(string s); }
│   ├─ ILogSink             { void Write(LoggerEvent,string); }                // 复用 core ILogger
│   └─ IUserPrompt          { string ChooseAssemblyFolder(); bool Confirm(string); }  // GUI 弹窗 / CLI 返回 null
├─ Settings/
│   └─ ExportSettings       (POCO：convertTexture/convertType/Fbx*/groupOption/allowDuplicates/…)
├─ Catalog/
│   ├─ AssetEntry           (Text/Type/Container/PathID/FullSize/Hash/IsVirtual/ExternalPath)
│   ├─ AssetCatalog.Build(AssetsManager, CatalogFilter) → Entries + ProductName
│   └─ AssetNode            (Text/Kind/Children/GameObject)   ← GUI 把 AssetNode 映射成 TreeNode
├─ Export/
│   ├─ ExportRequest        { OutputRoot, GroupOption, ExportType, Filter }
│   ├─ FileNamingPolicy     (allowDuplicates + 起始序号 + 260 字符策略，一处实现)
│   ├─ AssetExporter        (Texture/Audio/Mesh/Model/Shader/Text/Mono/Sprite/Font/Bin/…)
│   └─ ExportPipeline       Run(request, sink)  ← 支持「流式(per-file)」与「全量」两种驱动
└─ Maps/
    └─ AssetListWriter      (XML/JSON/MessagePack，名字由参数给)
```

落地顺序（每步都能单独编译通过）：

1. **先把 `Exporter` 抽出去**（收益最大、风险最低）：以 GUI 版为准（它是 CLI 的超集），把 `TryExportFile/TryExportFolder` 的「起始序号 + 随机名」变成 `FileNamingPolicy` 参数，一次性消掉 §4.2 的 #2/#3/#5/#6/#7。
2. **抽 `AssetCatalog`**：以 CLI 的 `ProcessAssetData` 为骨架（它有 `CanExport` 兜底，语义更完整），`displayAll` 变成 `CatalogFilter.IncludeNonExportable`；GUI 的 `BuildClassStructure`、TreeNode 组装留在 GUI（那是视图）。
3. **抽 `ExportSettings` POCO**：GUI 侧写个 `SettingsAdapter` 从 `Properties.Settings.Default` 映射，CLI 侧从 `App.config` + 新 flag 映射；`JsonConvert.DeserializeObject(uvs/texs)` 移到 POCO 的一次性解析（顺带修掉「每次导出都解析」）。
4. **抽 `IProgressSink/ILogSink`**：GUI 实现走 `Invoke`，CLI 实现走 `Console`（并让 `--silent` 真正静默 `Logger.Silent`）。
5. **预览留在 GUI**（`PreviewAsset` 依赖 `ModelConverter` + OpenTK + FMOD），只把 `ModelConverter.Options` 的构造提取成工厂（现在 GUI 有 5 份几乎相同的 `Options` 初始化：`Exporter.cs:508-517,546-555,583-592`、`MainForm.cs:1633-1642,1648-1657`）。

预计收益：直接删除约 **800 行**完全重复的非空代码，§4.2 的 12 条分歧里有 9 条会变成「一处开关」，CLI 立即获得 HDR、AFK、NapAsset 与正确的 FBX 命名。

---

## 5. 第三份拷贝：`AssetBrowser`（且它换全局委托）

`AssetBrowser`（`AssetBrowser.cs:705` + `Designer:393`）是「先建 map、再按 map 精确回捞」的批量导出器，它**又实现了一遍** `BuildAssetData` / `ProcessAssetData`：

- `BuildAssetData(List<AssetItem>, AssetEntry[])`（`:248-286`）——与 CLI 版几乎同构，只是过滤条件从「正则」变成「`entries.Any(x => x.Container==… && x.Name==… && x.Type==… && x.PathID==…)`」（`:283`），**用四元组精确匹配**，`O(n·m)` 全表扫描。
- `ProcessAssetData`（`:287-368`）——与 CLI `Studio.cs:286-388` 几乎逐行相同（含 `GameObject.HasModel()`、`Texture2D.m_StreamData` 尺寸补偿等），但没有 `TypeFlags` 的兜底分支。
- 它借用主窗体的一切：`_parent.Invoke(_parent.ResetForm)`、`_parent.updateGame(ResourceMap.GetGameType())`、`_parent.LoadPaths(files, paths)`（`:209-210,221`）。
- **最脏的一处**：为了不让核心日志污染主窗体状态栏，它把全局委托换掉再换回：

```csharp
var statusStripUpdate = StatusStripUpdate;   // 备份全局委托
StatusStripUpdate = Logger.Info;             // 换成日志
await Task.Run(async () => { … ExportAssets(…) … });
StatusStripUpdate = statusStripUpdate;       // 恢复
```

（`AssetBrowser.cs:223-225,245`）。这不是线程安全的重入（`StatusStripUpdate` 是 `static Action<string>`，`Studio.cs:40`），只要同时有别的后台任务在写状态栏，就会串台。

功能面（值得保留）：加载/差分两张 map（`secondMapFilter` 三态：仅 A / 仅 B / Hash 变化，`:88-111`）、按属性列做正则过滤（`_filters` 由 `typeof(AssetEntry).GetProperties()` 反射生成，`:78-84`）、`relocateSource_Click`（把 map 里的源路径平移到另一次安装目录，`:526-554`）、类型多选过滤（`filterSelectTypesBtn_Click:605`）。

---

## 6. Patcher：它到底改了什么，为什么需要它

### 6.1 目标发布形态

`build.ps1` 把两个 TFM 分别打成：

```
dist/<tfm>/
├─ AnimeStudio.GUI.exe        ← apphost（根目录）
├─ AnimeStudio.CLI.exe        ← apphost（根目录）
├─ LICENSE
└─ bin/                       ← 所有托管 dll / deps.json / runtimeconfig.json / 原生 dll
```

证据：`build.ps1:69-74`（`New-Item bin` + 把两个 `bin/…/<tfm>` 输出全量 `Copy-Item` 进 `bin`）、`:77-84`（再把两个 `.exe` 从 `bin/` 搬回根）。

### 6.2 为什么必须改写 apphost

.NET apphost（`apphost.exe` 模板生成的 `X.exe`）在**自身二进制里硬编码**了入口托管 dll 的相对路径（默认就是 `X.dll`）。要让 `X.exe` 躺在根目录、dll 躺在 `bin/`，只能把这个内嵌字符串改成 `bin\X.dll` —— 这就是 Patcher 的唯一职责（注释与用法见 `AnimeStudio.Patcher/Program.cs:11-17`，来源标注为 dnSpy 的 AppHostPatcher，`:1`）。

### 6.3 实现要点（213 行，全部是字节级操作）

| 步骤 | 位置 | 说明 |
| --- | --- | --- |
| 参数形态 | `:29-59` | 3 参：`<exe> <origdll> <newpath>` 或 `<exe> -d <subdir>`；2 参：`<exe> <newpath>`（orig 由 exe 名推导，`ChangeExecutableExtension:21-24`）；否则 usage + `return 1` |
| 分隔符 | `:26-27` | 按 exe 是否 `.exe` 决定 `\` 或 `/` |
| 长度上限 | `:19,73-82` | `maxPathBytes = 1024`，orig/new 超长即 `return 1` |
| 路径构造（`-d`） | `:36-41` | `newPath = subdir + sep + Path.GetFileName(ChangeExecutableExtension(exe))` ⇒ `bin\AnimeStudio.GUI.dll` |
| **幂等** | `:87-91` | 若已含 `newPath\0` 直接 `return 0`（`Already patched`） |
| 精确匹配 | `:95` + `:128-155` | 先找 `origPath\0`，并用 `IsPathStart` 拒绝「作为已打补丁路径后缀」的命中（前一个字节不能是 `\` 或 `/`）——注释明确说这是为了修「每次 build 叠一层 `bin\`」的旧 bug（`:93-94`） |
| 旧补丁恢复 | `:99-105,161-199` | 找不到裸名时，向后回溯路径段，定位 `[subdir\]*X.dll\0` 的**起点**（`bin\bin\X.dll` 也能救回来） |
| 覆写 | `:111-117` | 先把 `offset` 起 **1024 字节清零**（防更长的旧路径残留），再写 `newPath + '\0'`，`File.WriteAllBytes` |

### 6.4 build.ps1 侧的配套证据

| 行 | 作用 |
| --- | --- |
| `:4-7` | 先单独 `dotnet build AnimeStudio.Patcher -c Release -f net10.0`，硬编码 patcher 路径 |
| `:54-55` | **先删掉旧 exe**，强制生成全新 apphost（注释解释：旧的叠路径 bug 让「就地重打」不可靠） |
| `:58-61` / `:63-66` | build CLI/GUI 后立刻 `& $patcher $exe -d bin`，失败即 `throw` |
| `:93-104` | 事后断言：读 exe 字节 → UTF-8 解码 → 不得含 `bin\bin\`（否则 `throw "patcher failed"`），并检查含 `bin\X.dll`（否则 warning） |

### 6.5 风险与限制（fork 前要知道）

1. **不是签名/校验问题，是格式假设问题**：patcher 假设 apphost 内嵌路径字段可用 1024 字节且以 `\0` 结尾、且原串是裸 dll 名。若 .NET 未来版本改 apphost 布局（例如换 hostfxr embedding 方案），`GetOffset` 找不到 → 构建期 `throw`（比运行时崩好）。
2. **重命名 exe 后无法用 `-d` 重打**：orig 路径由 exe 名推导（`:21-24,39`）。若发行版把 `AnimeStudio.GUI.exe` 改名，第二次 patch 会找不到 `新名字.dll`，只能改用 3 参形式。
3. **它是纯构建期工具**：`build.ps1:73-74` 只复制 CLI/GUI 输出，Patcher 不进 `dist`；fork 的分支若改 `dist` 布局（例如想彻底平铺、或用 `AppHost` 的 `AssemblyName` 改变入口名），可以整块删掉 patcher 与 `build.ps1:52-66,93-104`。
4. **与本仓库其它 bug 的耦合**：`build.ps1:9-38` 的两个辅助函数（`Reset-Dir`/`Remove-EmptyDirectories`）暴露了 Windows 上「dist 目录被资源管理器/预览进程锁住」的现实问题——这也是为什么 patcher 必须先删 exe 而不是原地重打。

---

## 7. 扩展点（按二次开发的实际需求排序）

> 每条给「改哪里 → 为什么在那里改 → 坑」。

### 7.1 新增一种导出格式（如 glTF / PNG 序列 / CSV）

| 需求 | 锚点 |
| --- | --- |
| 新增 `ExportType` 成员（若要独占一种模式） | `AnimeStudio/ExportType.cs:3-9` |
| 主分发 switch（GUI / CLI 两份，**都要改**） | `AnimeStudio.GUI/Exporter.cs:646-685`、`AnimeStudio.CLI/Exporter.cs:565-602` |
| 外层调度 switch（GUI / CLI 两份） | `AnimeStudio.GUI/Studio.cs:727-753`、`AnimeStudio.CLI/Studio.cs:430-456` |
| GUI 菜单（每个新 ExportType 要加 3 个菜单项 + 3 个中转方法） | `MainForm.Designer.cs:546,593,621,649` + `MainForm.cs:2085-2157` |
| CLI flag（`--export_type` 自动接受新枚举值，无需改代码；但默认值在 `:207`） | `CommandLine.cs:144-147,207` |
| 图片格式（新增 `Webp`） | `AnimeStudio/Extensions/ImageFormat.cs:3-10` + `Extensions/ImageExtensions.cs:19-44`（SkiaSharp 编码 switch） + GUI 单选框 `ExportOptions.Designer.cs:777-780` + `ExportOptions.cs:26-34,79-86`（**用 `c.Text` 匹配枚举名**，控件 Text 必须与枚举成员字面量完全一致） |

坑：`ExportOptions.cs:26-34` 用 `((RadioButton)c).Checked` + `Enum.Parse(typeof(ImageFormat), c.Text)`，一旦加格式忘了加 RadioButton，`convertType` 永远保持原值且无提示；`ImageFormat.Hdr` 没有对应单选框，只能靠 `enableHDR` 间接启用（`Exporter.cs:80-86`）。

### 7.2 新增一种预览类型

| 需求 | 锚点 |
| --- | --- |
| 类型分派 | `MainForm.cs:1104-1177`（`switch (assetItem.Asset)`） |
| 切换资产时的清场清单（漏了会残留上一个预览） | `MainForm.cs:1041-1056`（`selectAsset`） |
| 「Preview enabled」开关的分支 | `MainForm.cs:744-804`（注意此方法**未接线**，见 D4） |
| 复用文本预览 | `PreviewText:1776-1780` |
| 复用图片预览（含通道开关） | `PreviewTexture/DirectBitmap:1764-1774`、通道逻辑 `:1246-1263` |
| 复用 3D 预览 | `PreviewModel:1670-1746`（吃 `ModelConverter`）、`CreateVAO:3220-3242` |
| 新增键盘快捷键 | `AnimeStudioForm_KeyDown:648-709`（GL 区/图片区两套分支） |
| 新增预览面板控件 | designer 里加控件 + 在 `selectAsset` 清场 + 在 `ResetForm` 复位（`MainForm.cs:1811-1846`） |

### 7.3 无头（headless）自动化

现状可复用的最大子集是 CLI：`Program.Run` 之外的所有能力都已经是 `static` 方法，但**入口绑死在 `Options`**：

- 直接复用 `Studio.ExtractFolder/ExtractFile`（`CLI/Studio.cs:38-79`）做「只解包不解析」，需要先设 `Studio.Game`、`Logger.Default`、`AssetsHelper.Minimal`、`TypeFlags`（`Program.cs:43-50` 是必须的最小样板）。
- 需要「只导出某几个资产」时，`BuildAssetData(typeFilters, nameFilters, containerFilters, ref i)`（`CLI/Studio.cs:234-284`）已是纯函数式（输入 `assetsManager` 静态 + 过滤数组），可以直接从自己的 `Main` 调用。
- **不要试图无头驱动 GUI**：`GUI/Studio.BuildAssetData` 直接 `new TreeNode()`（`:510,514`），`MonoBehaviourToTypeTree` 会弹对话框（`:1066-1082`），`Progress.Default` 默认是空实现（`Progress.cs:7`）而 GUI 在构造函数里替换（`MainForm.cs:246`）。⇒ 想做「GUI 能力 + 批处理」，正确路径是 §4.3 的 `AnimeStudio.Application`，而不是 WinForms 宿主。

### 7.4 本地化（i18n）

| 观察 | 证据 |
| --- | --- |
| **没有任何本地化资源**：只有默认 `.resx`，没有 `MainForm.zh-CN.resx` 之类 | `glob **/*.resx` = 7 个，全在 GUI 且都是默认文化 |
| 所有 UI 文本硬编码在 designer（`Text = "…"`） | `MainForm.Designer.cs:458,562,624,685,692,719` 等 100+ 处 |
| 逻辑层也有硬编码文案 | `MainForm.cs:167,176,180,381,2081,2276`；`Studio.cs:464,468` |
| 更糟：**代码里直接混中文注释**（说明上游接受中英混合） | `GUI/Studio.cs:839,842,845,851,857,872` |
| 文化被强制 `en-US`（影响 `ToString()` 与 FBX 数值格式） | `MainForm.cs:95`、`GUI/Studio.cs:671,783`；**CLI 没有设置** → 在 `de-DE`/`fr-FR` 等小数逗号区域，FBX/OBJ 数值输出会带逗号 |

fork 建议：先做一个 `Strings` 静态门面（键 → 文案）替换 designer 里的 `Text` 赋值（designer 可以保留，改在 `InitializeComponent` 后统一 `Localize()`），并**在 CLI 也显式 `CultureInfo.InvariantCulture`**（这是比翻译更紧急的正确性 bug）。

### 7.5 主题 / 外观

- 颜色来源只有三处（`MainForm.cs:106-140`），加新主题 = 在 `GuiColorTheme`（`Studio.cs:18-23`）+ designer 的 `specifyTheme.Items`（`:418`）+ `ApplyTheme` switch 三处同步。
- 深色模式依赖 .NET 9 的 `Application.SetColorMode`（`#if NET9_0_OR_GREATER` + `WFO5001` 抑制，`:108-139`），在 net9 以下直接被编译掉 → 「无主题」而非「优雅降级」。
- 硬编码颜色散落：`GameObjectTreeNode` 的 `LightBlue`（`Components/GameObjectTreeNode.cs:15`）、GL 清屏 `CadetBlue`（`MainForm.cs:3146`）。做真正的主题系统需要把这些收敛成 `Theme` 对象。

### 7.6 新增设置项（两套存储都要改）

| 前端 | 需要改的地方 |
| --- | --- |
| GUI | `Properties/Settings.settings`（设计器）→ 自动生成 `Settings.Designer.cs` → `App.config` 的 `<userSettings>` 默认值（`:8-…`）→ 读取点 → 可选：`ExportOptions.cs` 的控件 ↔ 设置映射 → `ExportOptions.cs:258-269` 的 Reset 会自动覆盖（它遍历 `Settings.Default.Properties`，新项自动纳入） |
| CLI | `App.config` `<appSettings>`（`:4-28`）+ `Settings.cs` 门面（`:39-62`）+ 若需命令行可覆盖，再改 `Components/CommandLine.cs`（Option + `Options` DTO + `GetOptions`） |

坑：GUI 的 `Properties.Settings.Default` 是 **User 作用域 + 版本化 `user.config`**，而 `Directory.Build.props:3-5` 每次发版都改 `AssemblyVersion`，全仓**没有一处调用 `Settings.Default.Upgrade()`**（grep 为 0）⇒ 升级版本后用户设置全部回到默认。

---

## 8. 架构/UX 债清单（按「fork 影响 × 修复成本」排序）

| # | 债 | 证据 | 影响 | 修法 |
| --- | --- | --- | --- | --- |
| **D1** | 业务逻辑双份（Exporter 86%、Studio 63% 重复） | §4.1 | 任何导出改动要写两遍，必然漂移（已漂 12 处） | §4.3 抽 Application 库 |
| **D2** | 全局可变静态状态：`Studio.Game/exportableAssets/visibleAssets/Paths/StatusStripUpdate/SkipContainer`、`AssetsHelper.Paths/Minimal`、`TypeFlags.Types`、`Progress.Default`、`Logger.Default/Flags`、`MiHoYoBinData.Key/Encrypted`、`UnityCNManager` | `GUI/Studio.cs:32-41`、`CLI/Studio.cs:25-36`、`TypeFlags.cs:7`、`Progress.cs:7`、`Logger.cs:13-17` | 无法多会话/多标签；单元测试必须先摆全局；`AssetBrowser` 用换委托的 hack 绕它（`AssetBrowser.cs:223-225`） | 把 `AssetsManager`+`TypeFlags`+`Paths` 收进 `ExportSession` 实例 |
| **D3** | CLI 退出码恒 0 | `CommandLine.cs:16-17`（丢弃 `Invoke` 返回值）、`Program.cs:202-205`（吞异常） | CI/脚本无法判成败 | `Run` 返回 int；`SetAction` lambda 返回它；异常 ⇒ 非 0 |
| **D4** | 死接线：`enablePreview_Check`（`MainForm.cs:744`）、`displayAssetInfo_Check`（`:805`）、`modelsOnly_CheckedChanged`（`:2474`）三个处理器**从未挂到任何事件**（designer 里对应项只设 `CheckOnClick`，`Designer:451-484`）；`toolStripMenuItem19_DropDownOpening`（`:2318`）同样未订阅，导致「Load from GitHub」的 AssetIndex 版本列表恒为 `{None}`，`AIVersionManager` 整条链路不可达 | §1.3 表 | 「Preview enabled」「Display asset information」「Filter models only」在运行期只在下次启动生效（值甚至不落盘）；「Load from GitHub」是纯装饰 | 逐项接线；`AIVersionManager` 要么接上要么删掉（它带网络请求与缓存目录副作用） |
| **D5** | 设置双存储 + 无迁移 | GUI `Properties/Settings.Designer.cs`（42 项）vs CLI `App.config`（25 key）+`Settings.cs`（24 个属性）；无 `Upgrade()`；CLI 侧 2 个死键、1 个缺失键 | 两个前端「同名字段不同默认值/不同存储」；发版丢设置；20+ 项无 flag | 抽 POCO + 显式 migrate + 把 `convertType` 等提升为 flag |
| **D6** | 「Use bundle container name」对容器无效 | `GUI/Studio.cs:386-393`：算出 `containerName` 却 `containers.Add((..., m_Container.Key))`（局部变量未使用） | 该开关只影响 MiHoYoBinData 命名（`:461`），用户预期落空 | 把 `m_Container.Key` 换成 `containerName`（一行） |
| **D7** | `ByContainer` 空容器回退不一致（GUI 根 / CLI 类型目录）；去重序号 `(1)` vs `(0)`；Animator FBX 路径不一致 | §4.2 #6/#7/#8 | 同输入产物不同 → 下游脚本/比对失效 | 由 `FileNamingPolicy` + `ExportPlanner` 统一 |
| **D8** | GUI 的 Asset list 只有 XML 且文件名写死；`exportListType` 形参未用 | `GUI/Studio.cs:779-790`、`MainForm.cs:2282`（`// XXX`） | 用户无法从 GUI 拿到 JSON 清单（CLI 可以） | 复用 CLI 的 `ExportAssetsMap` |
| **D9** | 逻辑层弹 UI：`Studio.MonoBehaviourToTypeTree` 打开文件夹对话框 | `GUI/Studio.cs:1066-1082` | 导出流程可被模态框阻塞（批处理时尤其致命），也让 `Studio` 无法无头复用 | 注入 `IUserPrompt`；CLI 已如此 |
| **D10** | 死代码/半成品 | `Designer:1653` `specifyGame` 未实例化；`CLI/Exporter.cs:98` `var option = new Options();` 未使用；`CLI/Program.cs:178` `var path` 未使用；`MainForm.cs:968` `nodes.TryAdd`（重名节点被**静默丢弃**）、`HasGameObjectNode:982-986`（只检查第一个子节点就 `return`）；`CLI/Studio.cs:316-343` 的容器名解析（含 `Paths` 哈希查表 + `container=null` 分支）与 GUI `Studio.cs:368-405` 不是同一套语义 | 见左 | 阅读成本 + 隐蔽错误（场景层级 dump 会丢子树；CLI 的 `ByContainer` 在缺少 AI map 时会把容器置空） | 逐个清理；`HasGameObjectNode` 应遍历全部子节点 |
| **D11** | 资源生命周期缺失 | `MainForm` 无 `FormClosing/FormClosed`（grep 为 0；`AssetBrowser` 有 `AssetBrowser_FormClosing:514`，主窗体没有）；`Designer:20-26` 只 `components.Dispose()`；FMOD `system/sound/channel`、GL program/VBO/EBO、`imageTexture`、`Logger.File` 均未释放（`MainForm.cs:3220-3242` 每次重建 VAO 只删旧 VAO，不删旧 VBO/EBO） | 长会话内存/句柄缓慢增长；预览切换频繁时 GL 泄漏 | 覆写 `OnFormClosing` 统一 `FMODreset` + 删 GL 对象 + `Progress/StatusStripUpdate` 解绑 |
| **D12** | i18n/文化强制 | §7.4 | 小数逗号区域输出损坏 | 全链路 `InvariantCulture`（数值格式化），UI 文案走资源 |
| **D13** | 进度模型不可组合（全局单例、单调、无域） | `Progress.cs:4-36` | 并发任务互相覆盖；AssetBrowser 与主窗体同时导出时进度条乱跳 | 改成 `IProgressSink` 实例 |
| **D14** | 取消语义：取消返回空集合而非异常；检查点粒度=每 Object | `GUI/Studio.cs:327-331`、`MainForm.cs:2767-2772` | 用户看到「0 资产」而非「已取消」；大数据集取消延迟数秒 | 抛 `OperationCanceledException` + 在循环内每 N 项检查 |
| **D15** | Designer 体积与可读性：`MainForm.Designer.cs` 1,734 行、94 个 `toolStripMenuItemN` | `Designer:1571-1734` | 新加菜单只能靠 Text 猜语义；无法 code review | 给匿名项重命名（VS 设计器支持），或迁到 `.resx` 驱动的手工构建 |

---

## 9. 给二次开发者的一页清单

**先做（1 天内，低风险高收益）**
1. `Exec` 退出码：`CommandLine.cs:16-17` + `Program.cs:16`（改 `int Run`）。—— 让 CI 可用。
2. `containerName` 一行修复：`GUI/Studio.cs:393`。
3. 接线 D4 三个开关（或删掉菜单项）。
4. CLI 显式 `CultureInfo`（`Program.cs:16` 开头）与 GUI 对齐。
5. 把 `--convert-type/--allow-duplicates/--restore-extension` 三个最常用的配置提升为 flag（`CommandLine.cs:144` 附近加 Option，`Program.cs:189` 附近消费）。

**再做（1 周，需要设计）**
6. 抽 `AnimeStudio.Application`（§4.3），按「Exporter → Catalog → Settings → Sink」四步走，每步保留 GUI/CLI 双入口回归：用同一份 bundle 分别跑 GUI「Export all」与 `CLI --export_type convert`，逐文件 hash 比对（修复前先记录基线，因为当前两侧本来就不一致）。
7. `ExportSession` 实例化（替代 D2 的静态状态），先把 `Paths/TypeFlags/Progress/StatusStripUpdate` 搬进去。

**谨慎（会碰到 core）**
8. `AssetCatalog` 的 `CanExport` 兜底语义（`CLI/Studio.cs:373-378` 与 `GUI/Studio.cs:441` 必须合并成一个开关，否则任何统一都会改变某一侧的行为）。
9. 预览层不要抽进 Application（OpenTK/FMOD/`ModelConverter` 的耦合会污染 CLI 的可移植性）。若一定要 3D 预览跨前端，建议单独 `AnimeStudio.Preview` 项目，只暴露 `IMeshSource`。

---

### 附：本文用到的验证命令（可复现）

```powershell
# 规模
(Get-Content AnimeStudio.GUI\MainForm.cs).Count                      # 3426
(Get-Content AnimeStudio.GUI\MainForm.Designer.cs).Count             # 1734
# GUI/CLI 重复度（非空行多重集交集）
#   Exporter.cs: 498 行共同 / CLI 非空 576
#   Studio.cs  : 311 行共同 / CLI 非空 497
# 事件接线（证明 D4）
Select-String AnimeStudio.GUI\MainForm.Designer.cs -Pattern 'enablePreview\.|displayInfo\.|modelsOnly\.'
# 设置迁移缺失
Select-String -Path (Get-ChildItem -Recurse -Filter *.cs).FullName -Pattern '\.Upgrade\(\)'   # 0 hit
# 并发原语缺失
Select-String -Path AnimeStudio.GUI\*.cs,AnimeStudio.CLI\*.cs -Pattern 'BackgroundWorker|Parallel\.|ConfigureAwait'  # 0 hit
```
