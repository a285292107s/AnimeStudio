# 界面导览
> 一个窗口，两栏工作区，六个菜单；先认清每块在回答什么，再动手导出。

## 先看整体
AnimeStudio 的图形界面是 `AnimeStudio.GUI.exe`。打开后，标题栏写着 `AnimeStudio v1.1.0`；加载成功后，后面还会追加产品名、Unity 版本和目标平台。

窗口只有左右两栏。左栏约占四成，是一个带三张标签页的工作区：`Scene Hierarchy`、`Asset List`、`Asset Classes`。右栏约占六成，是 `Preview` 和 `Dump` 两页。树和列表不是并排的，它们共用左栏，靠标签切换。窗口最底下，左栏下方是进度条，右栏下方是状态栏。

[清单] 6 组菜单 — File、Options、Export、Filter Type、Maps、About
[清单] 3 张标签页 — Scene Hierarchy、Asset List、Asset Classes
[清单] 6 列 — Name、Container、Type、PathID、Size、SHA256
[提示] 如果你只想把角色立绘存下来：`File → Load folder` 选包目录，`Filter Type` 只勾 `Texture2D` 和 `Sprite`，在 `Asset List` 里选中一张，右栏确认，最后 `Export → Selected assets`。

## 六个菜单
[表格] 菜单 | 管什么 | 新手先用哪几项
File | 打开、解包、清空当前结果 | Load folder、Extract folder、Reset、Abort
Options | 游戏、Unity 版本、导出设置、主题和高级开关 | Select Game、Specify Unity version、Export options、Advanced
Export | 把当前结果导出成文件 | All assets、Selected assets、Filtered assets、Models - Objects → Export selected
Filter Type | 只看某一类资产 | All，以及每次加载后出现的 Texture2D、Sprite、AudioClip 等
Maps | 资产地图、CAB 地图和 Asset Browser 入口 | Build、Open Asset Browser、Asset Index → Load from file
About | 关于窗口 | 点一下即可，没有下拉项

[提示] `Export → All assets` 默认是 `Convert`，会把贴图转成 PNG、音频转成 WAV、模型转成 FBX。`Raw`、`Dump`、`JSON` 是排错用的：分别导出原始字节、字段文本和整对象 JSON。
[提示] `File` 里的 `Load` 是打开来看，`Extract` 是把包里的文件原样解到磁盘；`Reset` 清空当前加载，`Abort` 取消正在进行的加载或导出。
[界面] 顶部菜单条 — File、Options、Export、Filter Type、Maps、About 六组；每组下面有真实子项，例如 File 的 Load folder、Extract folder，Options 的 Export options，Maps 的 Open Asset Browser。

## 左栏的三张标签页
三张标签页回答三个不同的问题，内容都来自同一批已加载的资产：

[清单] `Scene Hierarchy` — 谁挂在谁下面？它把物理文件、SerializedFile 和 GameObject 搭成一棵树。
[清单] `Asset List` — 这包里有哪些能导出的东西？它是扁平清单，负责筛选、搜索、排序和导出。
[清单] `Asset Classes` — 这次加载碰到了哪些 Unity 类型？它按 Unity 版本分组，选中一个类型，右栏会显示它的字段结构。
[提示] 右栏还有 `Dump` 页，显示当前对象的字段转储；排查“为什么读不出来”时才用。

## 场景树怎么读
场景树最多三层。第一层是物理文件，例如某个 `.bundle`；第二层是 `SerializedFile`，也就是包里的 CAB 小盒；第三层是 `GameObject`。有些资产直接来自 `.assets` 文件，没有物理文件这一层，树就会从 SerializedFile 开始。

勾选是父节点向子节点单向传播。你勾上一个父节点，它下面的整棵子树都会被选中；反过来，勾子节点不会自动勾上父节点。导出模型时，程序会从勾选节点开始收集，遇到已勾选的父节点就不再往下重复收集，所以“勾父等于选整棵子树”。

节点底色是浅蓝，表示这个节点自己或它的子节点里带模型。树里只画 GameObject；组件和 Mesh 不会单独成节点，但你在资产列表里右键选中一个 Mesh，可以用 `Go to scene hierarchy` 跳回它所属的 GameObject。

[界面] 场景树 — 第一层 `chapter01_assets_all.bundle`，第二层 `CAB-3f2a9c...`，第三层 `Root`、`Avatar_Girl_Sword`（有模型）、`Weapon_Sword_01`；带勾选框。

## 资产列表的 6 列
列的顺序是写死的，从左到右固定为这 6 列：

[表格] 列名 | 人话 | 注意
Name | 资产名 | 没名字的资产会显示成 `Texture2D#123` 这种“类型 + 编号”
Container | 它在包里的路径 | 不是磁盘路径；没有恢复出容器时为空
Type | Unity 类型名 | 常见的有 `Texture2D`、`Sprite`、`AudioClip`、`Mesh`、`TextAsset`
PathID | 包内编号 | 只在同一个 SerializedFile 里唯一，跨包比较会重复
Size | 序列化数据大小，单位字节 | 纹理、音频、视频会把外置流的大小加进来；不等于导出后的文件大小
SHA256 | 内容指纹 | 表头叫 SHA256，这一版实际算的是 64 位 xxHash，显示成 16 位十六进制；适合比对两份原始数据，不要拿它做安全校验

点列头可以排序，再点一次反向。`Size` 和 `PathID` 按数字比，其余按字符串比，所以 `Asset10` 会排在 `Asset9` 前面。

[界面] 资产列表 — 顶部是 Scene Hierarchy / Asset List / Asset Classes 三张标签，Asset List 选中；下面 6 列，5 行真实类型示例：Texture2D、Sprite、AudioClip、Mesh、TextAsset。

## 过滤、搜索与排序
`Filter Type` 菜单除了常驻的 `All`，其余项目是每次加载后重新生成的，只列出这次真正出现过的类型。换一个包，旧类型会消失，这是正常行为，不是丢功能。

左栏有两个搜索框，都是**正则**，不是通配符：

[清单] 场景树搜索 — 输入后按 Enter，在整个树里找；按住 Ctrl 再按 Enter 会勾选结果，Shift 一次展开全部结果，Alt 跳到最顶层父节点。
[清单] 资产列表搜索 — 输入后按 Enter，只在 Name、Container、PathID 三列里找，不搜 Type、Size、SHA256。
[提示] `*` 在正则里不是“任意字符”。要匹配任意字符串用 `.*`，例如 `^UI_.*Icon$`。正则写错时，列表搜索会自己清空，树搜索只会在状态栏报错。

## 右栏：预览面板
右栏有 `Preview` 和 `Dump` 两页。选中资产会自动预览；模型的 `GameObject` 和 `Animator` 预览默认关闭，需要先在 `Options → Advanced` 勾上 `Model preview enabled`，这个开关会保存。单独的 `Mesh` 不受这个开关限制。

[表格] 类型 | 能做什么 | 不能做什么
图片（Texture2D / Sprite） | 解码常见纹理格式，显示尺寸和格式；Ctrl+R/G/B/A 开关通道；右键可 Copy Image | 解不了的格式只在状态栏提示 Unsupported image for preview
音频（AudioClip） | 用 FMOD 在内存里播放，有 Play、Pause、Stop、Loop、进度和音量；能导出 WAV | 不画波形
模型（Mesh / GameObject / Animator） | 用 OpenGL 看几何；左键旋转、右键平移、滚轮缩放；Ctrl+W 线框、Ctrl+S 光照/顶点色、Ctrl+N 原始/重算法线 | 没有 UV、材质、贴图；正交投影；不播放动作
字体（Font） | 在进程内加载字体，按 8 档字号（12–72 pt）显示样张 | 不会安装进 Windows 字体目录
文本（TextAsset / Shader / MonoBehaviour / AnimationClip） | 文本预览；Shader 显示反编译结果；MonoBehaviour 显示 JSON；AnimationClip 显示 YAML | Shader 不编译；MonoBehaviour 类型认不出时会让你选 Assembly 文件夹；旧版动画不支持
视频（VideoClip / MovieTexture） | 只能导出 | 不能预览，状态栏会提示 Only supported export.

[界面] 图片预览 — `Preview` 页，标题是 Texture2D，信息行显示 Width、Height、Format、Filter Mode，底部提示 Ctrl+R/G/B/A 切通道。
[界面] 音频预览 — `Preview` 页，标题是 AudioClip，面板里有 Play、Pause、Stop、Loop、进度条和音量条。
[界面] 模型预览 — `Preview` 页，标题是 Mesh，底部状态栏显示 OpenGL 版本、鼠标操作和 Ctrl+W/S/N 快捷键。

## 3D 预览的真实限制
先把期待放对：它是“几何检查器”，不是游戏里的渲染器。模型预览只把顶点、法线和顶点色上传给 OpenGL；UV、材质、贴图都没有参与绘制。你看到的是灰模或顶点色模型，不是角色在游戏里的最终样子。

[清单] 投影是正交的，没有近大远小，像工程图。
[清单] 不播放动作。预览 Animator 时传给转换器的是空的动作列表，所以只显示静态姿势。
[清单] 没有 UV、材质、贴图，也没有游戏里的光照和后处理；光照只是按法线做的明暗。
[清单] 左键旋转、右键平移、滚轮缩放；Ctrl+W 在填充、线框、填充加线框之间循环，Ctrl+S 在光照和顶点色之间切换，Ctrl+N 在原始法线和重算法线之间切换。
[提示] 模型预览默认关闭。点模型没反应时，先去 `Options → Advanced → Model preview enabled` 勾上；要确认最终外观，请导出模型和贴图后在 Blender 或 Unity 里组装。

## Export Options
入口在 `Options → Export options`。对话框分左右两组：左边 `Export` 管通用导出，右边 `Fbx` 管模型导出。

[表格] 选项 | 在哪 | 影响什么
Convert Texture2D + Bmp / Png / Jpeg / Tga | 左组 Export | 贴图和立绘的导出格式，默认 Png；关掉 Convert Texture2D 会导出 `.tex` 原始纹理
Convert AudioClip to WAV(PCM) | 左组 Export | 默认开，音频转 WAV；关掉则保留原压缩格式（转换器支持时）
Group exported assets by | 左组 Export | 输出文件夹按 type name、container path、source file name 或 do not group 组织，默认按类型
FBXVersion | 右组 Fbx | 6.1 到 7.5，默认 7.3；老软件打不开就换低版本
FBXFormat | 右组 Fbx | Binary 或 Ascii，默认 Binary
Export skins / Export animations / Export blendshape / Export materials | 右组 Fbx | 决定 FBX 里带不带蒙皮、动作、形态键和材质；Export materials 默认关闭，开了会额外写 Materials 目录下的材质 JSON

[提示] 点 OK 会立即保存；Cancel 放弃本次修改；Reset 会把所有设置恢复默认并关闭对话框。

## 三个已知问题
这一版有几处菜单看起来能用，实际还没接好。不是你的操作问题。

[清单] `Preview enabled`、`Display asset information`、`Filter models only` — 这三个勾选项在 `Options → Advanced` 里，但设计器没有接上它们的 `CheckedChanged`。前两个的勾选状态会被之后新选中的资产读取，所以能影响“下一次选择”，但当前画面不会立刻刷新，重启也不会记住；`Filter models only` 更彻底，筛选时读的是设置文件里的旧值，勾了完全不生效。替代办法：只想看模型，用 `Filter Type` 勾 `GameObject` 和 `Animator`；想控制模型预览，用已经接好的 `Model preview enabled`。
[清单] `Load from GitHub` — 子项里永远只有 `None`，因为负责拉版本列表的 `DropDownOpening` 没有接线。要用 Asset Index，走 `Maps → Asset Index → Load from file`，加载本地文件。
[清单] `Use bundle container name`（Options → Asset loading） — 对普通容器命名无效。代码算出了新名字，但最后仍把原始的 `m_Container.Key` 存进 Container；这个开关实际只影响 `MiHoYoBinData` 的 Container 值。不要指望它改列表里的容器路径，容器信息以 Assets Map / CAB Map 恢复出来的结果为准。

## 依据
- `AnimeStudio.GUI/MainForm.Designer.cs:221` — 6 个顶层菜单。
- `AnimeStudio.GUI/MainForm.Designer.cs:230` — File 菜单项。
- `AnimeStudio.GUI/MainForm.Designer.cs:301` — Options 菜单项。
- `AnimeStudio.GUI/MainForm.Designer.cs:546` — Export 菜单项。
- `AnimeStudio.GUI/MainForm.Designer.cs:785` — Filter Type；`MainForm.cs:612-628` 每次加载重建。
- `AnimeStudio.GUI/MainForm.Designer.cs:802-805` — 第六组菜单显示文字是 Maps（字段名 `miscToolStripMenuItem`）。
- `AnimeStudio.GUI/MainForm.Designer.cs:933-938` — About 直接打开窗口。
- `AnimeStudio.GUI/MainForm.Designer.cs:962-979` — splitContainer 左右两栏。
- `AnimeStudio.GUI/MainForm.Designer.cs:983-1006` — 三张标签页及标题。
- `AnimeStudio.GUI/MainForm.Designer.cs:1048,1064-1091` — 资产列表 6 列及顺序。
- `AnimeStudio.GUI/MainForm.Designer.cs:1140-1158` — 左栏底部进度条。
- `AnimeStudio.GUI/MainForm.Designer.cs:1160-1179` — 右栏 Preview；`1407-1414` — Dump 页。
- `AnimeStudio.GUI/MainForm.Designer.cs:1430-1449` — 右栏底部状态栏。
- `AnimeStudio.GUI/MainForm.Designer.cs:451-484` — 三个勾选项没有 CheckedChanged 接线。
- `AnimeStudio.GUI/MainForm.Designer.cs:914-924` — Load from GitHub 只有 None。
- `AnimeStudio.GUI/MainForm.cs:98,521` — 标题栏版本与加载后的标题。
- `AnimeStudio.GUI/MainForm.cs:648-708` — Ctrl+W/S/N 与 Ctrl+R/G/B/A。
- `AnimeStudio.GUI/MainForm.cs:935-941` — 勾选父节点向子节点传播。
- `AnimeStudio.GUI/MainForm.cs:998-1038` — 列头排序；Size/PathID 数值比较。
- `AnimeStudio.GUI/MainForm.cs:1041-1075` — 选中资产与预览入口。
- `AnimeStudio.GUI/MainForm.cs:1104-1177` — 预览类型分发。
- `AnimeStudio.GUI/MainForm.cs:1216-1272` — 图片预览与通道开关。
- `AnimeStudio.GUI/MainForm.cs:1274-1398` — FMOD 音频预览。
- `AnimeStudio.GUI/MainForm.cs:1431-1479` — 字体预览，8 个字号（16/12/18/24/36/48/60/72）。
- `AnimeStudio.GUI/MainForm.cs:1481-1629` — Mesh 预览只用顶点/法线/顶点色。
- `AnimeStudio.GUI/MainForm.cs:1631-1746` — GameObject / Animator 预览；空 AnimationClip 列表。
- `AnimeStudio.GUI/MainForm.cs:2189-2246` — 过滤、列表正则搜索 Name/Container/PathID。
- `AnimeStudio.GUI/MainForm.cs:2474-2487` — modelsOnly 处理器存在但未接线；Model preview 已接线。
- `AnimeStudio.GUI/MainForm.cs:2318-2324` — GitHub 版本列表处理器未接线。
- `AnimeStudio.GUI/Studio.cs:308-624` — 场景树三层结构。
- `AnimeStudio.GUI/Studio.cs:386-393,461` — Use bundle container name 对容器无效。
- `AnimeStudio.GUI/Studio.cs:626-665` — Asset Classes 按 Unity 版本分组。
- `AnimeStudio.GUI/Studio.cs:1051-1064` — 勾父节点等于选整棵子树。
- `AnimeStudio.GUI/Components/AssetItem.cs:22-32,49-59` — 6 列的来源。
- `AnimeStudio/Classes/Object.cs:53-65` — SHA256 列实际是 xxHash64。
- `AnimeStudio.GUI/ExportOptions.cs:21-53,72-113` — 导出选项读写。
- `AnimeStudio.GUI/ExportOptions.Designer.cs:156,525,590,611` — Export / Fbx 分组、FBXFormat、FBXVersion。
- `AnimeStudio.GUI/ExportOptions.Designer.cs:333` — 分组选项。
- `AnimeStudio.GUI/Properties/Settings.settings:26-28,47-52` — 图片默认 Png，FBX 默认 7.3 / Binary。
- `AnimeStudio.GUI/Properties/Settings.settings:95-97` — Model preview 默认关闭。
- `AnimeStudio.GUI/Properties/Settings.settings:8-16` — Preview / Display info 默认开启，导出后打开文件夹默认开启。
- `AnimeStudio.GUI/AssetBrowser.cs:43-74` — Asset Browser 加载 .map / .json。
- `AnimeStudio.GUI/About.cs:12-23` — About 窗口版本与架构。
- `AnimeStudio.GUI/GameSelector.Designer.cs:130` — Select Game 的三类游戏。
- `Directory.Build.props:2-4` — 版本 1.1.0。
