# 五分钟上手

> 把游戏里的立绘、模型、音乐，变成双击就能打开的文件。

## 你能拿它做什么

一句话：它把 Unity 游戏打好的资源包拆开，把里面的贴图、模型、动作、音频、着色器、文本导成普通文件。

包＝快递箱。一个游戏目录里有几百个箱子，箱子里还有小盒子（Unity 管它叫 CAB），真正的东西在小盒子里。AnimeStudio 的工作是：找到箱子、开锁、拆盒、把东西摆出来，最后按类型放进你指定的文件夹。

[表格] 它导出什么 | 变成什么
[表格] 贴图 / 立绘 | .png .jpg .tga .hdr
[表格] 模型 / 动作 | .fbx（也可单独导 .obj）
[表格] 音频 | .wav，或它原本的压缩格式
[表格] 着色器 / 文本 / 字体 | .shader / .txt / .ttf .otf
[表格] 其它看不懂的对象 | .json（字段全部摊开）、.txt（字段文本）、.dat（原始字节）

[提示] 它不修改游戏本身的资源，除了一个例外：分片包会被合并写回原目录，见疑难解答。

## 下载与运行

[步骤] 1. 选对版本，下载 — 想要新特性用 .NET 10 版（推荐），只求稳用 .NET 9 版。两个包内容一样，只是编译目标不同。
[步骤] 2. 装 .NET Desktop Runtime — 两个包都只编了 Windows x64。没装对应版本的桌面运行时，双击只会闪一下，或者弹「需要 .NET」。
[步骤] 3. 解压到任意目录 — 没有安装程序，解压即用。
[步骤] 4. 双击 AnimeStudio.GUI.exe — GUI 给人用；CLI 给脚本用，同一个压缩包里。

[代码] https://nightly.link/Escartem/AnimeStudio/workflows/build/master/AnimeStudio-net10.zip
[代码] https://nightly.link/Escartem/AnimeStudio/workflows/build/master/AnimeStudio-net9.zip

[提示] 解压后根目录只有两个 exe 和 LICENSE，其余 dll 全在 bin 子目录里。别把 exe 单独拷走——它按相对路径去 bin 找自己的部件，拷走就打不开。

## 第一步 选对游戏

这是新手第一大坑，也是唯一一个「选错就全盘皆错」的步骤。

打开 Options ▸ Select Game，是一个三段的窗口，从上到下三个下拉加一个 Confirm：

[表格] 位置 | 标题 | 你在这里选什么
[表格] 第一段 | Game type | 三个大类：Hoyo game / Encrypted game (pick if unsure) / Other
[表格] 第二段 | Game | 具体游戏。选 Hoyo game 时，这里列 7 个发行商名字
[表格] 第三段 | Game version (Hoyo only) | 版本，比如 Live、CBT 1、CBT 2、CBT 3。只有第一段选 Hoyo game 才亮

全部条目一共 75 个，其中 Hoyo 系 17 个版本、UnityCN 键表 24 条。所以别指望在下拉里「翻一翻就找到」，先确定厂商，再确定版本。

[提示] 三个大类的中文含义和标签对不上，按代码的实际行为操作：第二项 Encrypted game (pick if unsure) 列的是 54 个加密游戏（标签写着「不确定就选这个」，确实如此）；第三项 Other 只列 3 个未加密条目——Unity、Fake Header、UnityCN Custom Key。

为什么必须选对：不同游戏用不同的锁和不同的钥匙。选错时它拿 A 游戏的钥匙去开 B 游戏的箱子，最轻是解不开（列表空白、状态栏报错），最常见是解出一堆名字乱码、尺寸不对、打不开的文件。它不会「自动猜」，也不会在事后提示你选错了。

[提示] 点 Confirm 之后界面几乎没有变化——选择只写进设置，等你下次加载文件时才生效。想确认，开 Options ▸ Debug ▸ Enable console，日志里会有一行 Target Game is XXX。

## 第二步 加载

[步骤] 1. File ▸ Load file — 新手先用这个，挑一个 .bundle 或 CAB 文件试。能出一个立绘，就说明游戏选对了。
[步骤] 2. File ▸ Load folder — 整个目录递归加载。第一次跑完整游戏目录用它，会很慢。
[步骤] 3. 看状态栏 — 加载时状态栏和进度条会走；结束前别急着点别的。

选文件夹时它会先把整个目录扫一遍（包括所有子目录），这就是大目录卡住的原因。加载前它还会估算内存：按数据量的 8.5 倍算，超过你剩余内存就弹警告问你继续不继续——这个数字是作者按不同数据集实测平均出来的，不是保守估计。

[提示] 加载中途想停，用 File ▸ Abort。它只取消正在读的那一步，已经读进来的不会自动清空，而且取消后资产数可能显示 0。别把「0 资产」当成「没有东西」。

## 第三步 浏览与预览

左栏切到 Asset List，是一个 6 列的列表：

[表格] 列 | 是什么 | 新手怎么用
[表格] Name | 资产名，通常是内部代号 | 找立绘看这个
[表格] Container | 它属于哪个小盒子（CAB 路径） | 批量导出时决定放哪个目录
[表格] Type | 类型：Texture2D、Sprite、Mesh、AudioClip… | 先按它过滤，比看名字快
[表格] PathID | 在这个文件里的编号 | 重名时靠它区分
[表格] Size | 大小 | 判断是不是你要的那张
[表格] SHA256 | 内容指纹 | 只有做比对时才需要

三种过滤手段，按使用顺序：

[清单] Filter Type 菜单 — 按类型筛选。菜单项是加载后按当前文件里实际出现的类型动态生成的，所以每次加载都可能不一样。
[清单] Asset List 上方的搜索框 — 是正则表达式，不是普通搜索。匹配 Name、Container、PathID 三列，不匹配 Size 和 SHA256。
[清单] Scene Hierarchy 页的搜索框 — 同样正则，作用于左边的树。

[提示] 搜索框写普通英文也当正则用。想找名字以 bgm 开头的音频，写 ^bgm 比写 bgm 准。写错了它会报 Invalid Regex 并清空搜索框。

预览在右栏。图片、文本、音频、字体、网格都能点开看；模型类（GameObject / Animator）要先在 Options ▸ Advanced 里勾上 Model preview enabled 才有得看。

[提示] 预览不读贴图，只能看形状和顶点色——它只把位置、法线、顶点色三样送进显卡，没有 UV，也不读材质。这不是你操作错了，想看图就去导出。

## 第四步 导出

Export 菜单里是 3 种范围 × 4 种格式，共 12 个入口。范围三个在菜单最上面，格式分在 Raw / Dump / JSON 三个子菜单里，Convert 就是那三个不带子菜单的。

[表格] 格式 | 产物 | 什么时候用
[表格] Convert（默认） | 按类型转：贴图→图片、模型→FBX、音频→WAV、文本→TXT | 九成情况用这个，目标是「人能直接打开」
[表格] Raw | 一律 .dat 的原始字节 | 想自己写程序处理，或者怀疑 Convert 转坏了
[表格] Dump | 一律 .txt 的字段文本 | 想读对象内部到底存了什么字段
[表格] JSON | 整个对象序列化成 .json | 想做版本对比、喂给别的工具

[表格] 范围 | 导出哪些
[表格] All assets | 当前加载进来的全部资产
[表格] Selected assets | 你在资产列表里选中的那几行
[表格] Filtered assets | 过滤后列表上看到的全部（类型筛选 + 搜索框都算）

[提示] All / Selected / Filtered 说的是「范围」，不是「格式」。想只导立绘：先 Filter Type 只勾 Texture2D 和 Sprite，再 Export ▸ Filtered assets。想只导一张：列表里选中它，再 Selected assets。

点下去先弹一个选文件夹的窗口，选完才开始导。开跑后状态栏逐条显示进度，结束时给一句总结。

[提示] 结束语里如果出现 N assets skipped (not extractable or files already exist)，那个 N 不一定是失败：文件已经存在时它不覆盖，直接跳过。想连号重导，先清空输出目录，或者去 Export Options 里打开允许重名。

## 输出目录怎么排

导出目录的排法由 Export Options 里的一个下拉决定，默认第一种：

[表格] 布局 | 路径长什么样 | 适合谁
[表格] ByType（默认） | 输出目录/Texture2D/、输出目录/AudioClip/… | 想按类型翻，最简单
[表格] ByContainer | 输出目录/它原来的 CAB 路径/ | 想还原游戏里的目录结构
[表格] BySource | 输出目录/包名_export/包内文件名/ | 想追溯每个文件来自哪个包
[表格] None | 全部平铺在输出目录根下 | 只导几件东西时最省事

重名怎么办：默认不覆盖，第二份同名文件直接跳过。打开允许重名后，自动加序号（第 2 份叫 名字 (1).png），序号从 1 开始。另外，资产名超过 260 个字符时会被换成随机文件名——这种文件在列表里还认得出，进了文件夹就认不出了。

## 你会遇到的文件长什么样

它判断文件类型靠读开头几个字节，不看扩展名。所以改扩展名没用，也别因为「没有扩展名」就以为文件坏了。

[表格] 你看到的东西 | 人话
[表格] .bundle，或叫 CAB-xxxx、甚至没扩展名 | 快递箱：Unity 的资源包，开头写着 UnityFS
[表格] .assets、.resource、globalgamemanagers | 箱子里的清单，记着「这个箱子里有什么」。globalgamemanagers 没有扩展名，但它就是这种文件
[表格] 同目录的 .resS 或 .resource | 大件货物的实体：贴图和音频常放在这里，清单里只写了个取货位置
[表格] .block | 一个文件里塞了上百个加锁小包的合集，程序会自己拆开逐个读
[表格] ENCR 开头 | 加了锁的包，锁和钥匙按游戏不同
[表格] .split0、.split1… | 被切成几段的包；程序会先把它们拼回一个完整文件，再读那个
[表格] blk、mhy0/mhy1、Blb02/Blb03、hyg、VFS 系 | 各厂商自己的封箱格式，全看文件头区分
[表格] 以上都不像 | 当成资源流文件（ResourceFile）处理，通常就是贴图/音频的实体数据

[提示] 一个 .assets 单独拎出来常常导不出图，因为贴图实体在同目录的 .resS 里。第一次跑请用 Load folder，让它把整个目录（含这些流文件）一起读进去。

## 依据

- 三个下拉的标题与选项：`AnimeStudio.GUI/GameSelector.Designer.cs:112`（Custom Key）、`:130`（三个大类字面量）、`:171`（Game version）、`:184`（Game type）、`:197`（Game）、`:209`（Confirm）
- 三段联动与 7 个发行商：`AnimeStudio.GUI/GameSelector.cs:50-64`（三个 case 各填什么）、`:71-90`（版本填法与默认选中第一项 `:76`）、`:98-111`（Confirm 写设置并关闭）
- 75 个游戏条目：`AnimeStudio/GameManager.cs:27-75`（49 条硬编码）、`:78-89`（读 UnityCN 键表，逐条加入）、`:91-92`（Custom Key、AFK Journey）；键表 24 条见 `AnimeStudio/UnityCNManager.cs:11-37`
- 分类归属：`AnimeStudio/GameManager.cs:121`（默认 Other）、`:151`（Mr0k→Hoyo）、`:168`（Blk→Hoyo）；`Unity`/`Fake Header`/`UnityCN Custom Key` 的 Unity 类别见 `:27`、`:49`、`:91`
- 选完游戏界面无反馈：`AnimeStudio.GUI/MainForm.cs:2490-2504`（只写 settings 与日志 Target Game）、`:3414-3418`（弹窗）
- 加载入口与文件夹递归：`AnimeStudio.GUI/MainForm.Designer.cs:239`、`:246`；`AnimeStudio/AssetsManager.cs:119-141`（`Directory.GetFiles(..., AllDirectories)`）
- 内存估算 8.5 倍：`AnimeStudio.GUI/MainForm.cs:375-385`
- Abort 只发取消：`AnimeStudio.GUI/MainForm.cs:2767-2772`
- 6 列列名与顺序：`AnimeStudio.GUI/MainForm.Designer.cs:1048`、`:1066-1091`；子项顺序 `AnimeStudio.GUI/Components/AssetItem.cs:49-59`
- Filter Type 动态生成：`AnimeStudio.GUI/MainForm.cs:612-628`、`:1838-1842`
- 搜索框是正则、只匹配三列：`AnimeStudio.GUI/MainForm.cs:2227-2243`；非法正则提示 `:2235`
- 预览分派与模型预览门槛：`AnimeStudio.GUI/MainForm.cs:1104-1177`、`:1118`；预览只传位置/法线/顶点色 `:3220-3244`
- 12 个导出入口：`AnimeStudio.GUI/MainForm.cs:2085-2139`；菜单结构 `AnimeStudio.GUI/MainForm.Designer.cs:546-673`
- 四种 ExportType：`AnimeStudio/ExportType.cs:3-9`；转换分派 `AnimeStudio.GUI/Exporter.cs:646-685`
- 三种范围与选目录：`AnimeStudio.GUI/MainForm.cs:2248-2278`
- 四种分组路径：`AnimeStudio.GUI/Studio.cs:677-713`；`AssetGroupOption` 定义 `AnimeStudio/AssetGroupOption.cs:3-9`；默认值 ByType 见 `AnimeStudio.GUI/Properties/Settings.Designer.cs:76`
- skipped 文案：`AnimeStudio.GUI/Studio.cs:763-768`
- 重名与 260 字符：`AnimeStudio.GUI/Exporter.cs:446-467`、`:699-703`；默认不重名 `AnimeStudio.GUI/Properties/Settings.Designer.cs:440-447`
- 文件类型嗅探顺序：`AnimeStudio/FileReader.cs:37-135`；BlockFile 二次判定 `:252-275`；类型枚举 `AnimeStudio/FileType.cs:9-26`
- 贴图实体在流文件里：`AnimeStudio/Classes/Texture2D.cs:210-219`、`AnimeStudio/ResourceReader.cs:32-59`
- 两套下载包与 x64 限制：`README.md:15-18`；`AnimeStudio.GUI/AnimeStudio.GUI.csproj:10`、`AnimeStudio.CLI/AnimeStudio.CLI.csproj:7`
- exe 在根、dll 在 bin：`build.ps1:69-84`；改写 apphost 的 patcher `AnimeStudio.Patcher/Program.cs:11-17`
