# 批量与资产地图
> 一次要导几百个包、要写进脚本、要反复导出同一批东西——这三件事交给命令行和资产地图。

> 说明：本稿是文案底稿，已按组件规范落成 `docs/batch.html`，两处正文一致。

## 什么时候该用命令行

同一个下载里有两个 exe：`AnimeStudio.GUI.exe` 是界面，`AnimeStudio.CLI.exe` 是命令行。两者能力基本一样，区别只在「下一步由谁决定」：界面你点，命令行由参数决定。值得换过去的有三种情况：

[清单] 一次要导几百上千个文件 — 界面上点「导出全部」，中途关窗就白跑。
[清单] 要放进脚本或定时任务 — 没人盯着也能跑完。
[清单] 同一批包换着花样导 — 复制一行，改几个字。

这三种之外先用界面：看清列表、预览贴图、确认游戏选对，命令行都不给你。它也没有「列出内容」的选项，只想清点数量可以用 `--map_op AssetMap --map_type None`，扫一遍只数不写文件。

## 三条能直接复制的命令

三条都只用两个位置参数（输入在前、输出在后）加几个 flag。在解压出来的目录里打开终端，把路径换成你自己的就能跑。

[代码] AnimeStudio.CLI.exe "D:\Games\SR\StreamingAssets" "D:\Out\sr-textures" --game SR --types Texture2D Sprite

输入可以是文件或目录，给目录会连子目录一起扫；输出目录自动创建。`--game SR` 必填，决定用哪套解密规则。`--types Texture2D Sprite` 只要贴图和精灵。另外两个默认值刚好合用：`--export_type Convert` 转成 PNG 这类可读格式，`--group_assets ByType` 按类型分目录。

[代码] AnimeStudio.CLI.exe "D:\Games\ZZZ" "D:\Maps\zzz" --game ZZZ --map_op AssetMap --map_type MessagePack --map_name zzz-assets

这条不导资产，只建索引：扫完目录，在 `D:\Maps\zzz\zzz-assets.map` 里写下每个资产的名字、容器、来源包、PathID、类型、Hash 和偏移。慢，但只需要扫这一次。

[代码] AnimeStudio.CLI.exe "D:\Games\ZZZ" "D:\Out\zzz-ui" --game ZZZ --map_op AssetMap,Load --map_type MessagePack --map_name "D:\Maps\zzz\zzz-assets.map" --names "ui_.*" --types Sprite

这条读上一条的索引，只加载名字匹配 `ui_.*` 的精灵所在的包，再导出。`AssetMap,Load` 就是「用现成的 map」，所以 `--map_name` 要给带 `.map` 的完整路径。

## 17 个参数的默认值

15 个 flag 加两个位置参数，默认值全在 `Components/CommandLine.cs` 里。

必填的三项：

[表格] 参数 | 默认值 | 说明
[表格] `input_path` | 必填 | 第一个位置参数，文件或目录
[表格] `output_path` | 必填 | 第二个位置参数，输出目录，会自动创建
[表格] `--game` | 必填 | `GameType` 枚举名，如 `SR`、`GI`、`ZZZ`。大小写敏感

过滤用的三项：

[表格] `--types` | 空（等于不过滤） | Unity 类名，空格分隔，可写 `类型:标志`
[表格] `--names` | 空 | 资产名正则
[表格] `--containers` | 空 | 容器路径正则

输出相关的两项：

[表格] `--export_type` | `Convert` | `Convert` / `Raw` / `Dump` / `JSON`
[表格] `--group_assets` | `ByType` | `ByType` / `ByContainer` / `BySource` / `None`

地图相关的三项：

[表格] `--map_op` | `None` | `CABMap` / `AssetMap` / `Both` / `Load`，可逗号组合
[表格] `--map_type` | `XML` | `MessagePack` / `XML` / `JSON` / `None`
[表格] `--map_name` | `assets_map` | 地图文件名。Load 模式要给完整路径

其余六项：

[表格] `--silent` | 关 | 只静音核心库的日志，控制台输出照旧
[表格] `--logger_flags` | `Debug Info Warning Error` | 不含 `Verbose`，要看解包细节得自己加
[表格] `--unity_version` | 空 | 留空就自己从文件里读
[表格] `--key` | 0 | MiHoYoBinData 的 XOR 密钥，0 表示不启用。可写十六进制
[表格] `--ai_file` | 空 | 资产索引 JSON，只对原神系游戏生效
[表格] `--dummy_dlls` | 空 | DummyDll 目录，给脚本类资产反序列化用

[提示] `--names` 和 `--containers` 允许只给一个文件名：程序逐行读它，每行当一条正则。手写一份清单存成 txt 传进去，比在命令行里堆一长串正则干净。空行跳过，写错的正则静默忽略。

## 三个必须知道的坑

[步骤] 1. `--types` 用空格，不用逗号 — 仓库 README 里那条示例写的是 `--types Texture2D,Sprite`，这是错的。逗号让整串变成一个词，程序拿它去匹配类型名，失败后只打一行 `has invalid format, skipping...` 就跳过，不报错、不退出。等所有类型都被跳过，过滤器就空了，结果是把整个包全导出来：你想只要贴图，得到的是一整个目录。

[步骤] 2. `--game` 不给就是报错 — 它必填，还挂着校验器比对候选名单：不给，拿到的是错误信息加用法说明；给错，是 `Invalid game. Supported games: ...` 加一长串名单。README 说「不带参数运行会列出完整参数」，实际你看到的是缺必填项的报错。名单里是枚举名（`GI`、`SR`、`ZZZ`），不是界面下拉里的「Live」「CBT 2」——那是另一个字段。界面选「Live」，命令行要写 `--game SR`。

[步骤] 3. 退出码永远是 0 — 程序入口把命令行组件的返回值丢掉，执行函数没有返回值，还把异常全吞进 `catch` 里打印。所以失败时进程照样以 0 退出：`%ERRORLEVEL%`、`$LASTEXITCODE`、CI 的红灯都指望不上。判成败只能看产物：目录里有没有新文件、日志里有没有 `Error`、文件数量和预期对不对得上。

## 一个文件一个文件地干

扫完目录，它先按路径字符串长度从小到大排一遍——不是按文件大小——再逐个文件走三步：加载、建资产表、导出，然后清空处理下一个。

所以内存峰值只跟「最大的那一个文件」有关。界面版反过来：一次把所有文件读进内存、建一棵全局树再导出，内存跟整个目录成正比。经验上差一个数量级——几百 MB 的目录，界面版峰值按 GB 算，命令行通常几十到几百 MB。界面一打开就吃满内存的目录，命令行往往能跑完。

代价是没有进度条，也没法先看结果再决定。

## 资产地图：先建索引再回捞

比喻是图书馆的索引卡：不建索引，每次找书都要翻整个书库；建了索引，卡片告诉你去哪个书架哪一排。

每次导出都要解包、读对象头、算资产名，整包扫一遍几分钟到几十分钟。建 map 就是把这趟活儿一次做完，之后只读卡片。值得建的三种情况：

[清单] 整包扫描 — 先把整个安装目录索引成一张表，再决定要哪一些。
[清单] 反复导出 — 今天要贴图、明天要动作，建一次能用很多次。
[清单] 版本对比 — 两份 map 一比，就知道这次更新加了什么、删了什么。

`.map`（MessagePack）每条存七样：名字、容器、来源包、PathID、类型、Hash、偏移。`.xml` 只存前五样，没有 Hash 也没有偏移——要比版本差异、要用偏移回捞，就得用 `.map` 或 `.json`。

CAB map 是另一份东西，落在工作目录的 `Maps\<名字>.bin`，不是输出目录。它按 CAB 名记两件事：这个 CAB 在哪个包的哪个字节偏移、依赖哪些别的 CAB。有了偏移，加载时只从需要的位置解开，不必整包读完，所以超大包先建 CAB map 再导提升明显。

顺带一个反直觉的地方：`Load` 位在这两个分支里含义相反——`AssetMap,Load` 是读图，`CABMap,Load` 是建图；单独写 `--map_op CABMap` 才是去读 `Maps\` 下的旧图。

## Asset Browser 能干什么

Asset Browser 是专门读 map 的工具（菜单的 Load AssetMap），只认 `.map` 和 `.json`，不认 `.xml`。

界面里这块工具栏和表格重画成了一张示意图，控件名与列名都来自真实代码：工具栏是 Load AssetMap / Clear / Load Selected / Export Selected / Relocate source / Search / Select Types / Load second map，表格列是 Name / Container / Source / PathID / Type / Hash，底部下拉是 Assets Only in First Map。

它能做的五件事：

[清单] 按属性过滤 — 名字、容器、来源、PathID、类型、Hash 各一格，填的是正则，回车就筛。
[清单] 按类型多选 — Select Types，一次勾好几种类型。
[清单] 直接回捞 — Load Selected 只加载选中的，Export Selected 只导出选中的。
[清单] relocate — 把 map 里记的源路径整体平移到另一次安装目录。
[清单] 两张 map 对比 — Load second map 之后，在三种视角里选一种。

[表格] 下拉项 | 你看到的是 | 实际含义
[表格] Assets Only in First Map | 第一张有、第二张没有 | 新版删掉的
[表格] Assets Only in Second Map | 第二张有、第一张没有 | 新版新增的
[表格] Assets with Differences | 两边都有但 Hash 不同 | 内容改过的

relocate 只比文件名、不比子目录，不同目录里的同名包会串；跑完会告诉你多少个没搬过去。

## 对比版本的真实局限

[提示] 超过 16 MB 的对象没有真 Hash — 建图时，大于等于 16 MiB 的条目会把 Hash 写成 `size:1a2b3c` 这样的字符串（十六进制字节数），而不是内容哈希——上百 MB 的着色器和网格只为索引身份算一遍不划算。后果很实在：同一个大对象在两版之间只要大小没变，就永远显示「没改过」，只能靠大小、偏移或手动核对。

另一个边界是「同一个资产」怎么认：用名字、容器、PathID 三元组。重新导入让 PathID 变了，就会被算成「删一条加一条」，而不是「改过」。

## 界面和命令行的结果不一样

[提示] 同一个包，两边产物不是逐文件对得上的 — 已知的十几处差异里有这三条：同名文件去重，界面从 `(1)` 开始编号，命令行从 `(0)` 开始；`ByContainer` 遇到空容器，界面回退到输出根目录，命令行回退到类型子目录；动作导出的 FBX，命令行那条路径不拼 `.fbx` 后缀，产物没有扩展名。要做长期流水线就固定用一边，别混着比。

## 依据

- 15 个 flag 的注册、默认值、必填标记：`AnimeStudio.CLI/Components/CommandLine.cs:90-170`、`:205-209`、`:121`
- `--game` 校验与大小写敏感：`AnimeStudio.CLI/Components/CommandLine.cs:194-201` + `AnimeStudio/GameManager.cs:110`；枚举名与显示名是两个字段：`AnimeStudio/GameManager.cs:121-127`，界面显示 DisplayName：`AnimeStudio.GUI/GameSelector.cs:56,59`
- 正则文件当过滤器：`AnimeStudio.CLI/Components/CommandLine.cs:269-307`
- `--types` 解析失败静默跳过：`AnimeStudio.CLI/Program.cs:52-97`（错误文案 `:92`）
- 退出码恒 0：`AnimeStudio.CLI/Components/CommandLine.cs:13-18`（丢弃 `Invoke` 返回值）+ `AnimeStudio.CLI/Program.cs:16,202-205`（`void Run` + 全捕异常）；CLI 项目内无 `Environment.Exit`
- 按路径长度排序、逐文件流式：`AnimeStudio.CLI/Program.cs:144`、`:183-193`
- `--map_op` 五条分支：`AnimeStudio.CLI/Program.cs:147-174`
- map 七个字段与 XML 缺字段：`AnimeStudio/AssetMap.cs:54-87`、`AnimeStudio/AssetsHelper.cs:455-468`
- 16 MiB Hash 退化：`AnimeStudio/AssetsHelper.cs:671-678`
- CAB map 落 `Maps/*.bin`、含偏移与依赖：`AnimeStudio/AssetsHelper.cs:20,282-306`；`Both` 同时建两份：`:906-928`
- map 回捞过滤：`AnimeStudio/AssetsHelper.cs:719-741`
- 三元组同一性：`AnimeStudio/AssetEntryComparer.cs:11-15`
- Asset Browser 只认 `.map`/`.json`：`AnimeStudio/ResourceMap.cs:33-58`、`AnimeStudio.GUI/AssetBrowser.cs:47`
- 三种对比视角：`AnimeStudio.GUI/AssetBrowser.cs:88-111` + `AssetBrowser.Designer.cs:347`
- relocate 按文件名：`AnimeStudio.GUI/AssetBrowser.cs:526-553`
- 去重序号 (0) vs (1)：`AnimeStudio.CLI/Exporter.cs` 的 `TryExportFile`（`i = 0`）对照 `AnimeStudio.GUI/Exporter.cs`（`i = 1`）
- README 与代码不一致：`README.md:33`、`README.md:36`
- 其余差异背景：`research/03-app-layer.md` §4.2
