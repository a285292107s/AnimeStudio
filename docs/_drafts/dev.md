# 想改代码
> 15 个项目、9 个预编译的原生库、两份几乎一样的导出器。想提 PR 或者自己改一版，先把这张地图看清再动手。

> 说明：本稿是文案底稿，已按组件规范落成 `docs/dev.html`，两处正文一致。

## 仓库是怎么分层的

托管代码的依赖只有一层：从上往下，没有回环。把每个项目的项目引用列出来就是全貌：

[代码] AnimeStudio.GUI        → Utility, AnimeStudio
AnimeStudio.CLI        → Utility, AnimeStudio
AnimeStudio.Utility    → FBXWrapper, PInvoke, AnimeStudio
AnimeStudio.FBXWrapper → PInvoke, AnimeStudio
AnimeStudio.PInvoke    → （无）
AnimeStudio            → （无）
AnimeStudio.Patcher    → （无）

核心项目 `AnimeStudio` 不引用任何项目，是最底下的叶子，包解析、序列化文件、对象模型、贴图解码和资产地图全在里面。`AnimeStudio.Patcher` 完全独立，只跟 exe 文件的字节打交道。8 个 C++ 项目不被任何托管项目引用，它们只往 `AnimeStudio.Libraries` 里吐 DLL，再由三个 csproj 登记进发布包。

## 15 个项目一张表

「产出」只写类型，文件名与项目名相同，比如 `AnimeStudio.Utility.dll`。「随构建」问的是「是否随 solution 构建」。

[表格] 项目 | 语言 | 产出 | 干什么 | 随构建
[表格] AnimeStudio | C# | dll | 核心：容器、序列化、对象、贴图 | 是
[表格] AnimeStudio.Utility | C# | dll | 导出工具：ACL、着色器、音频 | 是
[表格] AnimeStudio.GUI | C#（WinForms） | exe | 图形界面与预览 | 是
[表格] AnimeStudio.CLI | C# | exe | 命令行入口 | 是
[表格] AnimeStudio.Patcher | C# | exe | 改写 exe 内嵌的 dll 路径（构建期） | 是
[表格] AnimeStudio.PInvoke | C# | dll | 加载原生 DLL 的壳 | 是
[表格] AnimeStudio.FBXWrapper | C# | dll | FBX 导出的托管封装 | 是
[表格] AnimeStudio.Ooz | C++ | dll | Oodle 三种格式解压 | 否
[表格] AnimeStudio.HLSLDecompiler | C++ | dll | DXBC 反编译成 HLSL | 是
[表格] AnimeStudio.FBXNative | C++ | dll | FBX 导出本体 | 否
[表格] AnimeStudio.ACL.DB | C++ | dll | ACL 动画解压，DB 变体 | 是
[表格] AnimeStudio.ACL.MHY | C++ | dll | ACL，米哈游变体 | 是
[表格] AnimeStudio.ACL.SR | C++ | dll | ACL，SR 变体 | 是
[表格] AnimeStudio.ACL.ZZZ | C++ | dll | ACL，ZZZ 变体 | 是
[表格] AnimeStudio.ACL.ZZZV2 | C++ | dll | ACL，ZZZ 第二版，带 spdlog | 是

[提示] `.slnx` 里只有 `AnimeStudio.Ooz` 和 `AnimeStudio.FBXNative` 标了 `<Build Project="false" />`，solution 构建不碰它们；5 个 ACL 和 HLSLDecompiler 会跟着编。README 说「原生库都排除在 solution 构建之外」，跟 `.slnx` 的实际标注不一致。

## 构建

[清单] 前置 — Visual Studio 2022，勾上 C++ 桌面工作负载；.NET 9 和 .NET 10 的 SDK 两个都要装。
[清单] `build.ps1` 干的活 — 只编托管代码（CLI 和 GUI 各两个目标框架），再用 Patcher 改写 exe，最后归到 `dist/net9.0-windows/` 和 `dist/net10.0-windows/`。
[清单] 它不干的活 — 不编任何原生项目，不跑测试（仓库里没有测试项目），不签名不压缩。
[清单] 原生 DLL — 9 个，共 7.65 MB，直接提交在 `AnimeStudio.Libraries` 里。它们的登记分散在三个 csproj：核心项目带 Ooz 和 FBXNative，Utility 带 HLSL 和 5 个 ACL，界面工程单独带 FMOD。
[清单] FBX 原生库 — 要 Autodesk FBX SDK 的 2020.3.7 版，头文件目录、一个 Debug 一个 Release 的库目录，全部以绝对路径写死在 `AnimeStudio.FBXNative.vcxproj` 的 4 处。装到别的位置就编不过。

[提示] 改托管代码时 `dotnet build AnimeStudio.GUI` 就够了，产物里原生 DLL 会自动被拷过去。打包那一步只在要出安装包时才需要。

这一节的四个数字在页面上排成一条数字条：15 个项目（7 个托管、8 个原生）、9 个预编译原生 DLL、7.65 MB、2 个项目不随 solution 构建。

## 为什么要全都塞进 bin

发布产物长这样：根目录只有两个 exe 加一份 LICENSE，其余托管 dll、依赖清单和原生 DLL 全在 `bin/`。

[代码] dist/net10.0-windows/
├─ AnimeStudio.GUI.exe
├─ AnimeStudio.CLI.exe
├─ LICENSE
└─ bin/      托管 dll、deps.json、runtimeconfig.json、9 个原生 DLL

原因是 .NET 的 exe 只是个启动器：它把自己二进制里硬编码了「入口 dll 叫什么名字」，默认去同目录找同名 dll。想让 exe 待在根、dll 待在 `bin/`，就得把内嵌的这串路径改成 `bin\X.dll`。`AnimeStudio.Patcher` 就干这一件事：找到那串字节的起点，把 1024 字节的槽位清零，写进新路径。

好处很实在：两个 exe 共享同一个 `bin/`，公共依赖只存一份，体积几乎减半；用户桌面只有两个图标加一个文件夹。

代价是耦合：`DllLoader` 必须先看 exe 目录下有没有 `bin/`，有就切进去再找原生 DLL。想改发布布局，`build.ps1` 和 `DllLoader` 得一起改，只改一边就是运行期「找不到 DLL」。

## 加一个游戏要动哪四处

[步骤] 1. 枚举里加成员 — 在 `GameType` 里加一个名字。这个名字就是命令行 `--game` 的取值，也是内部到处比较的那个标识。
[步骤] 2. 谓词里加分支 — 同一文件下方那串 `IsXxx(this GameType)`，是核心库判断「这套规则跟谁像」用的。再加进 `IsBlockFile`、各个 `IsXxxGroup` 这类组合，解析分支才认得它。
[步骤] 3. `LoadGames` 里注册 — 一行 `Games.Add(...)`，把枚举、显示名、分类挂上去。显示名是界面下拉框里看到的那串字，跟命令行用的枚举名是两个字段。
[步骤] 4. 需要密钥就换 record — 只改了文件头就用 `Game`；要扩展密钥、初始化向量就用 `Blk`；魔改 AES 用 `Mr0k`；UnityCN 系用 `UnityCNGame`，名字和密钥从 `CNKeys.json` 读。

这四处只让程序「认得」这个游戏。如果它的资源布局跟已有的都不同，还得回到下一节的容器与加密去加分支。

## 加格式、加容器、加加密

加一种导出格式，比如 glTF：

[清单] `ExportType` 枚举加成员 — `AnimeStudio/ExportType.cs`。
[清单] 分发 switch 改两份 — `AnimeStudio.GUI/Exporter.cs` 和 `AnimeStudio.CLI/Exporter.cs`，这两份是同构的。
[清单] 外层调度的 switch 也两份 — 两个 `Studio.cs`。
[清单] 界面菜单 — 每个新格式要加三个菜单项和三个中转方法，否则界面上根本点不到。

加一种容器：

[清单] `FileType` 枚举加成员 — `AnimeStudio/FileType.cs`。
[清单] 魔数识别加一支 — `FileReader.CheckFileType`，一条按签名长度分组的 if 链。
[清单] 加载分派加 case — `AssetsManager.LoadFile`，块文件还要加在 `LoadGameBlockFile`。

加一种块加密：

[清单] 写解密函数 — 放 `ImportHelper` 或 `Crypto/` 下的工具类里，一个静态方法进、一个 `FileReader` 出。
[清单] 预处理链加一支 — `FileReaderExtensions.PreProcessing` 里按 `GameType` 分派的 switch，一个游戏一个 case。
[清单] 加密只包住块数据时还要去 `BundleFile` — 解块的位置已经堆了 Mr0k、UnityCN、NetEase、FairGuard 一串分支。

## 结构性问题

准备 fork 的话，这几件事早晚会撞上。

[表格] 问题 | 具体表现 | 影响
[表格] 全局可变静态状态 | 游戏、导出表、类型开关、进度、日志全是静态字段 | 不能同时开两个会话；写单元测试得先把全局摆好
[表格] 两份业务代码 | 界面版与命令行版的 `Exporter.cs` 有 86% 非空行逐字相同，`Studio.cs` 是 63% | 改导出逻辑要改两遍，已经漂了十几处
[表格] 5 份 ACL 源码树 | 5 个 ACL 工程各自带一份 `acl/` 源码树，其中 4 个还带 `rtm/` | 上游更新要手工同步五次
[表格] apphost 的假设 | Patcher 假设内嵌路径槽位够 1024 字节、以零结尾，原串是裸 dll 名 | .NET 换掉 apphost 布局，构建期就抛错，比运行期崩好
[表格] 许可证混合 | 仓库是 MIT，但 `kraken.cpp` 带 GPLv3 头，FMOD 是免费非商用，FBX SDK 受 Autodesk 条款约束 | 商业闭源分发之前必须逐项处理

## 一句话结论

[提示] 适合贡献，不适合直接做生意 — 这个项目适合给它提 PR：加游戏、修解析、补导出的 PR 都欢迎，改法就是上面那几处锚点。但它不适合拿去做商业闭源分发：许可证是混的，两份前端代码会持续漂移，发布形态也建立在对 .NET apphost 的字节级假设上。

## 依据

- 15 个项目与两个 `Build=false`：`AnimeStudio.slnx:7-31`
- 分层与项目引用：`AnimeStudio.Utility/AnimeStudio.Utility.csproj`、`AnimeStudio.FBXWrapper/AnimeStudio.FBXWrapper.csproj`、`AnimeStudio.GUI/AnimeStudio.GUI.csproj`、`AnimeStudio.CLI/AnimeStudio.CLI.csproj` 的 `ProjectReference`；核心项目无项目引用：`AnimeStudio/AnimeStudio.csproj`
- `build.ps1` 的步骤与「不做的事」：`build.ps1:4-7,40,58-66,69-104`；TFM 矩阵 `:40`
- 9 个预编译 DLL 与登记位置：`AnimeStudio.Libraries/`（9 个文件，7.65 MB）、`AnimeStudio/AnimeStudio.csproj`（Ooz/FBXNative）、`AnimeStudio.Utility/AnimeStudio.Utility.csproj`（HLSL + 5 ACL）、`AnimeStudio.GUI/AnimeStudio.GUI.csproj`（FMOD）
- FBX SDK 绝对路径 4 处：`AnimeStudio.FBXNative/AnimeStudio.FBXNative.vcxproj:71,79,92,101`
- apphost 改写与 1024 字节槽位：`AnimeStudio.Patcher/Program.cs:19,39-41,106-118`；`bin/` 探测：`AnimeStudio.PInvoke/DllLoader.cs:29-45`；布局由 `build.ps1:70,77-84` 决定
- 加游戏的四处：`AnimeStudio/GameManager.cs:191-…`（`GameType`）、`:278-356`（`GameTypes` 谓词）、`:19-92`（`LoadGames`）、`:114-189`（`Game` / `Blk` / `Mr0k` / `UnityCNGame` record）；CN 密钥 `AnimeStudio/UnityCNManager.cs:40-86`
- 新增导出格式的锚点：`AnimeStudio/ExportType.cs:3-9`、`AnimeStudio.GUI/Exporter.cs` 与 `AnimeStudio.CLI/Exporter.cs` 的 `ExportConvertFile` 分发、两个 `Studio.cs` 的外层 switch
- 新增容器：`AnimeStudio/FileType.cs:9-27`、`AnimeStudio/FileReader.cs:37-134`、`AnimeStudio/AssetsManager.cs:280-307,660-740`
- 新增块加密：`AnimeStudio/FileReader.cs:183-250`、`AnimeStudio/ImportHelper.cs:97-1120`、`AnimeStudio/BundleFile.cs:700-810`
- GUI/CLI 重复度与静态状态清单：`research/03-app-layer.md` §4.1、§8（D1、D2）
- 原生库清单与交接链路、可重现性风险：`research/02-native-build.md` §5.1、§5.2、§7
- 许可证：`LICENSE:1-5`（MIT）、`AnimeStudio.Ooz/kraken.cpp:1-17`（GPLv3 头）、`README.md:127`（FMOD 非商用 + FBX 条款）、`README.md:57`（原生库与 solution 构建的说法）
