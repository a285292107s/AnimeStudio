# 疑难解答

> 十四种最常见的「不对」，每一个都先给结论，再给原因。

## 打不开

### 加载完一片空白，导出时状态栏说没有可导出资产

九成是游戏选错了，剩下一成是只加载了一个没有依赖的文件。它不看扩展名、只看文件头，解密方式由你选的那个游戏条目决定；条目不对，箱子能认出来，盒子的锁打不开，资产表就是空的，导出时状态栏只回一句 No exportable assets loaded。你可以：Options ▸ Select Game 重选，再用 File ▸ Load folder 整目录重来。

### 双击没反应，或者提示缺 .NET 桌面运行时

缺的是 .NET Desktop Runtime，不是普通的 .NET Runtime；而且只支持 64 位 Windows。两个 exe 都是自宿主程序，运行时按你下的包要 9 或 10 的桌面运行时，x64 版本。你可以：按版本装对应的桌面运行时再双击；32 位系统没有可用的构建。

### 加载大目录跑到一半，内存爆掉

它在加载时把整个目录的资产塞进一张表，内存按数据量的 8.5 倍估算。单个包解压后小于 512MB 会整块放内存，超过就落成一个临时文件（关闭即删），但资产表本身还是全量。你可以：分批加载、先按类型过滤再导出；或者打开 Options ▸ Skip container recovery 减少一步开销（代价是 Container 列没有名字）；真跑不动就用 CLI，它一次只持有一个文件。

## 打开了但不对

### 资产名是乱码，导出的文件也打不开

游戏条目选错，它拿错了钥匙。每个条目自带一套解密参数，字节错位之后名字、尺寸、内容都是垃圾——不是编码问题，改什么系统区域设置都没用。你可以：确认厂商和版本都对上（Live 与 CBT 是两套完全不同的锁），重选之后重新加载。

### 资产数量比游戏体积少得多

通常是你只加载了一个包，而不是全部。一个 .assets 只是清单，贴图实体常在同目录的 .resS 里，手动挑文件时它只能顺着已经读进来的文件找依赖。你可以：用 File ▸ Load folder 整目录加载；目录太大就先看批量那一页，用资产地图精确定位再回捞。

### 模型是灰白的，看不到贴图

这是设计如此，不是导出坏了。3D 预览只把位置、法线、顶点色三样送进显卡，不读 UV、不读材质；导出的 FBX 里也只有几何体，材质会另存在输出目录的 Materials 下，一个材质一份 json。你可以：单独导出 Texture2D 取图，再在 3D 软件里手动挂上。

### 中文路径和超长文件名会不会出问题

中文路径没问题，超长文件名有问题。它全程走 Windows 的 Unicode 接口，中文目录名照常读写；但资产名达到 260 个字符时，导出文件名会被换成随机名，之后就认不出是谁了。你可以：把输出目录放浅一点，导出前留意那些名字特别长的条目。

## 导出后不对

### 导出的贴图全黑，或者只有半张

多半是数据没找齐，不是图坏了。贴图实体常在同目录的 .resS / .resource 里，清单里只写了取货位置；找不到那个文件时，这一项会直接报错跳过，连图都不生成。压缩格式不认识时同样跳过。你可以：整目录加载后再导；还不行就换成 Raw 导出，拿到原始字节自己处理。

### 模型导出了，但 FBX 没有扩展名

这是 CLI 的已知 bug，GUI 不受影响。走命令行导出 Animator 时，它把目录路径直接当成了 FBX 文件名，产物没有 .fbx 后缀。你可以：手动补上 .fbx 就能打开；或者改用 GUI 导出这一类资产。

### 音频导出来没有声音

先看扩展名。是 .wav 说明转换成功，没声音多半是播放器或音量问题；是 .fsb 说明它跳过转换、把原始数据原样写了出来。只有 PCM、Vorbis、ADPCM、MP3、XMA 这几种能转 WAV，其它编码一律落成 .fsb（AAC 是 .m4a），普通播放器不认这两种容器。你可以：用能读 FSB 的工具再转一次。

### 导出的数量比选的少

多数是「文件已经存在」，不是失败。默认不覆盖同名文件，遇到同名直接跳过，结束时那句 skipped 提示把「解不出来」和「已经有了」合并在一起，看不出区别。你可以：清空输出目录重导，或者打开 Options ▸ Allow duplicates，让重名文件自动加序号。

## 程序本身

### 它会修改我的游戏文件吗

会写，但只写一种：分片包。加载前它会把 xxx.split0、split1… 按顺序拼成一个完整文件，放回这些分片所在的目录，然后才去读这个新文件；同名文件已存在就跳过，也不会删你的分片。你可以：先把游戏目录复制一份再动手，至少备份一次。

### 需要联网吗，杀毒软件报毒正常吗

不需要联网，报毒基本是误报。唯一会联网的菜单项是「从 GitHub 拉 Asset Index」，它在源码里根本没接上事件，点了没有反应；其余功能全在本机跑。而它要读别人的二进制、解密、再写文件，exe 又没做数字签名，构建时还被改写过入口路径，杀软按行为打分就容易拦。你可以：只从项目发布页下载，报毒时加白名单。

### CLI 明明失败了，却返回 0

它的退出码永远是 0，别拿退出码判断成败。命令行入口拿到执行结果后没有把返回值传出去，内部异常也被吞掉，所以无论是导出失败还是路径写错，进程都正常退出。你可以：用输出目录里的文件数量、以及日志内容来判断，或者在外面包一层检查产物。

## 依据

- 游戏条目决定解密与解析：`AnimeStudio/GameManager.cs:27-92`（每个条目各带一套 key/参数）；`AnimeStudio/FileReader.cs:252-275`（按游戏二次判定 BlockFile）；`AnimeStudio/Classes/Texture2D.cs:203-207`（按游戏读不同字段）
- 「没有可导出资产」文案：`AnimeStudio.GUI/MainForm.cs:2274-2277`
- 只认文件头不认扩展名：`AnimeStudio/FileReader.cs:37-135`
- win-x64：`AnimeStudio.GUI/AnimeStudio.GUI.csproj:10`、`AnimeStudio.CLI/AnimeStudio.CLI.csproj:7`；两套下载包与运行时要求 `README.md:15-18`
- 8.5 倍内存估算：`AnimeStudio.GUI/MainForm.cs:375-385`
- 512MB 阈值与临时文件：`AnimeStudio/BundleFile.cs:261-287`
- Skip container recovery 的副作用：`AnimeStudio.GUI/MainForm.Designer.cs:380-387`
- CLI 逐文件循环：`AnimeStudio.CLI/Program.cs:183-193`
- 贴图实体在流文件里、找不到就抛错：`AnimeStudio/Classes/Texture2D.cs:210-219`、`AnimeStudio/ResourceReader.cs:32-59`
- 图片转换失败返回 null（该项被跳过）：`AnimeStudio.GUI/Exporter.cs:158-176`
- 依赖解析默认开：`AnimeStudio.GUI/MainForm.Designer.cs:352-361`；递归加载 `AnimeStudio/AssetsManager.cs:119-141`
- 3D 预览只传位置/法线/顶点色：`AnimeStudio.GUI/MainForm.cs:3220-3244`；模型预览开关 `AnimeStudio.GUI/MainForm.Designer.cs:461-467`
- 材质另存 Materials/：`AnimeStudio.GUI/Exporter.cs:565-574`
- 260 字符换随机名：`AnimeStudio.GUI/Exporter.cs:699-703`
- CLI Animator 无 .fbx 后缀：`AnimeStudio.CLI/Exporter.cs:455-486`
- 音频转换与扩展名：`AnimeStudio.GUI/Exporter.cs:187-210`、`AnimeStudio.Utility/AudioClipConverter.cs:91-150`（扩展名）、`:154-183`（支持范围）
- 默认不覆盖、skipped 文案、Allow duplicates：`AnimeStudio.GUI/Exporter.cs:446-467`、`AnimeStudio.GUI/Studio.cs:763-768`、`AnimeStudio.GUI/MainForm.Designer.cs:363-370`、`Properties/Settings.Designer.cs:440-447`
- 分片合并写回原目录：`AnimeStudio/ImportHelper.cs:19-47`；调用点 `AnimeStudio/AssetsManager.cs:97-102`、`:127`
- 联网代码只有 Asset Index 且未接线：`AnimeStudio/AIVersionManager.cs:15-29`、`AnimeStudio.GUI/MainForm.cs:2318`（Designer 里没有订阅）；其余外链都在 `AnimeStudio.GUI/About.cs`
- CLI 退出码恒 0：`AnimeStudio.CLI/Components/CommandLine.cs:13-18`、`AnimeStudio.CLI/Program.cs:202-205`
- 构建期改写 exe 入口路径（杀软误报背景）：`AnimeStudio.Patcher/Program.cs:11-17`、`build.ps1:52-66`
