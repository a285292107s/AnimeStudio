# 01 · AnimeStudio 核心管线与加密架构分析

> 范围：`AnimeStudio/`（C# 核心库）+ 必要的跨项目接缝（`AnimeStudio.CLI`、`AnimeStudio.GUI`、`AnimeStudio.Utility`）。
> 方法：纯静态阅读（`read` 分段 + `grep` 符号检索），未构建、未运行、未修改任何仓库源码。本文件是唯一产出。
> 行号均来自当前 clone（`github.com/Escartem/AnimeStudio`，MIT）。LOC 使用 UTF-8 换行切分统计，含空行。
>
> **密钥材料策略**：本文只给出「密码族 / 密钥来源 / 派生布局 / 代码锚点」。**不列出任何密钥字节、十六进制串、S 盒表内容或可复现的种子值**，也不提供可端到端跑通的解密配方。需要落地实现时请直接读源码对应行区间。

---

## 1. 模块清单（Module inventory）

### 1.1 编排层（Orchestration）

| 文件 | LOC | 职责 | 关键公共 API | 耦合 |
|---|---|---|---|---|
| `AnimeStudio/AssetsManager.cs` | 1126 | 唯一入口编排器：文件分派 → 容器解析 → 资源文件缓存 → 对象实例化 → 后处理 | `LoadFiles(string[])` :74/:83、`LoadFolder` :119、`Clear` :802、`ClearLoadedAssets` :781、`AfterBundleLoaded` 回调 :25、`assetsFileList` :30、`FilterData`/`OffsetData` :70/:72 | 强依赖 `ImportHelper`、`BundleFile`、`SerializedFile`、`ObjectReader`、全部 `Classes/*`；被 CLI/GUI 单向调用 |
| `AnimeStudio/AssetsHelper.cs` | 1052 | 离线索引器：CABMap 构建/持久化、AssetMap 构建/流式导出、依赖解析、偏移裁剪加载 | `BuildBoth` :906、`BuildAssetMap` :376、`BuildCABMap` :150、`ProcessDependencies` :136、`ParseAssetMap` :719、`GetMaps` :44 | 内部**自带一个** `AssetsManager`（`Silent=true, SkipProcess=true`）:28；与 `AssetMap`/`ResourceIndex`/`ImportHelper` 耦合 |
| `AnimeStudio/ImportHelper.cs` | 1129 | 预处理/外层解密器集合：split 合并、gzip/brotli、各厂整文件解密（17 个 `Decrypt*` + 2 个 `Parse*` 假头 + 2 个 `Decompress*`，共 21 个入口） | `MergeSplitAssets` :19、`ProcessingSplitFiles` :49、`DecompressGZip` :67、`DecompressBrotli` :82、`DecryptPack` :97、`DecryptMark` :228、`DecryptEnsembleStar` :277、`ParseFakeHeader` :303 …`DecryptSchoolGirlStrikers` :1100 | 直接吃 `CryptoHelper` 常量与 `Mr0k` 配置记录；被 `FileReaderExtensions.PreProcessing` 单点调用 |
| `AnimeStudio/GameManager.cs` | 361 | 游戏注册表 + 厂商配置对象（record 继承链）+ 类型谓词 | `GetGame*` :94-108、`GameManager.Games` :12、`Game` :114、`UnityCNGame` :133、`Mr0k` :143、`Blk` :161、`Mhy` :177、`GameType` :191、`GameTypes.*` :278-359 | 全库耦合中枢：几乎所有分支都写成 `Game.Type.IsXxx()` |
| `AnimeStudio/UnityCNManager.cs` | 107 | UnityCN 密钥库（内置表 + `CNKeys.json` 读写 + 选中项下发） | `GetKeys` :43、`GetDefaultKeys` :52、`SetKeys` :54、`ReadJson` :64、`ResetJson` :85、`SetKey` :94 | 静态类；路径 `CNKeys.json` :39；被 GUI/CLI 调用 |

### 1.2 容器层（Container / bundle）

| 文件 | LOC | 职责 | 关键 API | 耦合 |
|---|---|---|---|---|
| `AnimeStudio/BundleFile.cs` | 922 | UnityFS/UnityWeb/UnityRaw/ENCR 解析 + 块解压 + CAB 切片 | `BundleFile(FileReader, Game)` :124、`ReadFiles` :323、`CreateBlocksStream` :261、内部 `Header/StorageBlock/Node` :51/:78/:94、`ArchiveFlags` :14、`CompressionType` :32 | 读 `UnityCN`、`Mr0kUtils`、`NetEaseUtils`、`FairGuardUtils`、`OPFPUtils`、`WmvUtils`、`OodleHelper`、`SevenZipHelper`、`LZ4` |
| `AnimeStudio/MhyFile.cs` | 396 | HoYo `mhy0/mhy1` 容器（独立块表 + Descramble） | `MhyFile(FileReader, Mhy)` :57 | 依赖 `Mhy` 配置 + `CryptoHelper` 内表 :25 |
| `AnimeStudio/BlbFile.cs` | 263 | `Blb\x03` 容器（腾讯系 Blb3） | `Blb3File(FileReader, string)` :21 | 依赖 `BlbUtils.InitKeys` :24 |
| `AnimeStudio/HygFile.cs` | 243 | 大端 `HYG` 容器 | `HygFile(FileReader, string)` :20 | 依赖 `HygUtils.Decrypt` :33/:66 |
| `AnimeStudio/VFSFile.cs` | 215 | Arknights: Endfield `VFS` 容器 | `VFSFile(FileReader, string, GameType)` :20 | 依赖 `VFSUtils.ReadHeader` :33 |
| `AnimeStudio/WebFile.cs` | 60 | `UnityWebData1.0` 打包 | `WebFile(EndianBinaryReader)` :27 | 无（**注意 :47-56 有致命 bug，见 §5.6**） |
| `AnimeStudio/AFKJourneyUtils.cs` | 604 | `.lpak` 内多 UnityFS chunk 扫描、`.pkg` 条目、`.jsone`/`.dxt` 特殊文件 | `ScanLpakUnityFsChunks` :94、`GetLpakChunkVirtualPath` :126、`ScanPkgEntries` :405、`ExtractPkgEntries` :488 | 被 `AssetsManager.LoadAfkJourneyLpakFile` :212 调用 |

### 1.3 序列化层（SerializedFile / TypeTree）

| 文件 | LOC | 职责 | 关键 API | 耦合 |
|---|---|---|---|---|
| `AnimeStudio/SerializedFile.cs` | 431 | `.assets`/CAB 解析：头 → 类型表 → 对象表 → 脚本/外部引用 → TypeTree | ctor :37、`SetVersion` :244、`AddObject` :411、`ObjectsDic` :22、`m_Externals` :33、`IsVersionStripped` :426 | 持有 `AssetsManager`；`Objects`/`ObjectsDic` 是 `PPtr` 的解析目标 |
| `AnimeStudio/SerializedFileHeader.cs` | 29 | 头 DTO | 字段 :10-15 | — |
| `AnimeStudio/SerializedFileFormatVersion.cs` | 88 | 版本枚举 1..22 | `LargeFilesSupport=22` :85、`RefactoredClassId=16` :61 | 分支判据遍布 `SerializedFile` |
| `AnimeStudio/SerializedType.cs` | 35 | 类型条目（classID / stripped / scriptID / TypeTree） | — | `SerializedFile.ReadSerializedType` |
| `AnimeStudio/TypeTree.cs` / `TypeTreeNode.cs` | 15 / 47 | 节点树 + 字符串缓冲 | `m_Nodes` :11 / `m_TypeStrOffset` 等 | `TypeTreeHelper`、`CommonString` |
| `AnimeStudio/TypeTreeHelper.cs` | 340 | 泛型反射式读取：TypeTree → string / OrderedDictionary | `ReadTypeString` :11、`ReadType` :166 | 依赖 `ObjectReader`（`byteStart`/`byteSize` 边界） |
| `AnimeStudio/CommonString.cs` | 123 | TypeTree 常用字符串池（offset 高位标志回落查表） | `StringBuffer` :7 | `SerializedFile.TypeTreeBlobRead` :403 |
| `AnimeStudio/ClassIDType.cs` | 556 | Unity ClassID 枚举，**540 个具名条目**（`UnknownType=-1` 起） | — | `ObjectReader.type`、`TypeFlags`、GUI 过滤 |
| `AnimeStudio/TypeFlags.cs` | 56 | 全局「可解析/可导出」白名单 | `SetTypes` :9、`SetType` :14、`CanParse` :20、`CanExport` :34 | 静态 `Types` 字典；CLI/GUI 从 settings 反序列化注入（CLI `Program.cs:50`） |

### 1.4 对象模型与 I/O

| 文件 | LOC | 职责 | 关键 API | 耦合 |
|---|---|---|---|---|
| `AnimeStudio/ObjectReader.cs` | 110 | 单对象作用域读取器（带 byteSize 越界保护） | ctor :21、`Read` 覆盖 :44、`Reset` :54、`BytesLeft` :60、`ReadVector3/ReadXForm` :65/:77 | **共享 `SerializedFile.reader.BaseStream`**（线程安全根因） |
| `AnimeStudio/Classes/Object.cs` | 150 | 对象基类：`Reset` 到 `byteStart`、xxHash 指纹、TypeTree dump | ctor :32、`GetHash` :53、`ComputeRawHash` :71、`Dump` :103、`ToType` :121、`GetRawData` :139 | 所有 `Classes/*` |
| `AnimeStudio/Classes/PPtr.cs` | 156 | `fileID + pathID` 引用，跨文件解析 | ctor :24、`TryGetAssetsFile` :39、`TryGet` :75、`Set` :111、`IsNull` :153 | 通过 `assetsFileIndexCache` 做 O(1) 文件定位 |
| `AnimeStudio/ObjectInfo.cs` | 31 | 对象表条目（byteStart/byteSize/classID/pathID） | 字段 :7-15 | `SerializedFile` 对象表 |
| `AnimeStudio/EndianBinaryReader.cs` | 379 | 端序可切的基础读取器 | `Position` :21、`Remaining` :28、端序重载 :30-110 | 全库唯一 I/O 原语 |
| `AnimeStudio/FileReader.cs` | 283 | 文件类型嗅探 + 预处理入口 | `CheckFileType` :37、`IsSerializedFile` :137、`PreProcessing` :183 | 依赖 `VFSUtils`、`ImportHelper.*` |
| `AnimeStudio/FileType.cs` / `StreamFile.cs` | 28 / 12 | 15 种容器类型 / CAB 流描述 | :9 / :5 | — |
| `AnimeStudio/XORStream.cs` / `OffsetStream.cs` / `BoundedStream.cs` | 33 / 94 / 120 | 流装饰器：XOR pad / 基址偏移 / 窗口切片 | `XORStream.Read` :18 | `BlkUtils`、AFK lpak |
| `AnimeStudio/OodleHelper.cs` / `SevenZipHelper.cs` | 25 / 50 | P/Invoke `AnimeStudio.Ooz.dll`；LZMA 解码 | `Decompress` :11 / `StreamDecompress` :37 | 原生依赖（部署必须带 Ooz dll） |
| `AnimeStudio/Logger.cs` / `ILogger.cs` / `Progress.cs` | 112 / 74 / 38 | 静态日志门面 / 接口 / 进度 | `Logger.Default`、`Progress.Default` :7 | 全局静态，GUI 替换实现 |

### 1.5 加密（`AnimeStudio/Crypto/`）

| 文件 | LOC | 密码族 | 关键 API |
|---|---|---|---|
| `CryptoHelper.cs` | 97 | **常量表总仓**：按厂商分区（GI / BLB3 / WMV / HYG / CBX / Pack / BH3 / Mr0k / VFS / Mark / TOT / EnsembleStar）:7-94 | 静态 `byte[]` 字段 |
| `AES.cs` | 72 | 标准 AES-128 **解密**（T 表实现，逆 S 盒） | `Decrypt(byte[] m, byte[] keys)` :51 |
| `UnityCN.cs` | 185 | UnityCN 专有：AES-ECB 密钥白化 + 半字节索引替换流密码 | ctor :17、`SetKey` :51/:71、`DecryptBlock` :93、`Entry` :159 |
| `Mr0kUtils.cs` | 76 | HoYo `mr0k` 头/块：AES + 定制 S 盒 + 种子 XOR pad | `Decrypt` :12、`IsMr0k` :73 |
| `BlkUtils.cs` | 67 | GI CB2/CB3、ZZZ CB2 `blk`：MT19937_64 XOR 流 | `Decrypt` :14 |
| `MT19937_64.cs` | 74 | MT19937-64 PRNG | ctor :14、`Int64` :28 |
| `XORShift128.cs` | 55 | xorshift128 + 自定义 `NextDecrypt`（NA/早期米系头） | `InitSeed` :14、`NextDecrypt` :41 |
| `BlbUtils.cs` | 261 | Blb3/HYG/WMV 共用：定制 AES（`BlbAES`）+ 变体 RC4 + 16 字节 Descramble | `InitKeys` :16、`Decrypt` :25、`Descramble` :54、`RC4` :69、`BlbAES` :123 |
| `HygUtils.cs` / `WmvUtils.cs` | 59 / 33 | HYG / WMV 的密钥编排（换表后复用 `BlbUtils`） | `Decrypt` :12 / :11 |
| `NetEaseUtils.cs` | 256 | 网易：头扫描 + 自定义 CRC + RC4 + 0x80 块 XOR 混合 | `DecryptWithHeader` :12、`DecryptWithoutHeader` :18、`CRC` :169、`RC4` :210 |
| `FairGuardUtils.cs` | 295 | 明日方舟: Endfield CB1/CB2：0xA6 掩码 + 整数混洗派生 seed/key | `Decrypt` :37、`CB2Constants` :9、`CB1Constants` :23 |
| `OPFPUtils.cs` | 83 | 路径派生 key 的链式 XOR / 原地置换 | `Decrypt` :13、`IsEncryptionBundle` :39 |
| `VFSUtils.cs` | 606 | Endfield VFS 头字段位旋转混淆 + 定制 AES（`VFSAES`） | `IsValidHeader` :13、`ReadHeader` :44、`VFSAES` :414 |

---

## 2. 主调用链（Open bundle → export）

### 2.1 入口分派

```
调用方（CLI Program / GUI MainForm）
 └─ AssetsManager.LoadFiles(files)                     AssetsManager.cs:74 → :83
     ├─ [AFKJourney] OrderFilesForLoading               :178  (globalgamemanagers→data.unity3d→其它)
     ├─ ImportHelper.MergeSplitAssets(dir)              ImportHelper.cs:19   (写盘副作用!)
     ├─ ImportHelper.ProcessingSplitFiles(files)        ImportHelper.cs:49
     ├─ [ResolveDependencies] AssetsHelper.ProcessDependencies  AssetsHelper.cs:136
     └─ Load(toReadFile)                                AssetsManager.cs:143
         ├─ for each: LoadFile(path)                    :197
         │   ├─ new FileReader(fullName)                FileReader.cs:27
         │   ├─ reader.PreProcessing(Game)              FileReader.cs:183
         │   └─ LoadFile(reader)                        AssetsManager.cs:245
         └─ ReadAssets() + ProcessAssets()（除非 SkipProcess） :171-175 → :814 / :887
```

### 2.2 类型识别（hop 2）

1. `FileReader` 构造即做嗅探：`CheckFileType()` 读 20 字节 `ReadStringToNull(20)` 匹配 `UnityFS/UnityWeb/UnityRaw/UnityArchive/UnityWebData1.0/blk/ENCR`（`FileReader.cs:37-54`）。
2. 字符串签名不中时走字节签名级联：VFS 探测（`:58`）→ gzip（`:68`）→ 0x20 处 brotli（`:74`）→ `IsSerializedFile()`（`:82`，校验 `m_FileSize == stream.Length` 且 `m_DataOffset <= size`，`:166-175`）→ zip（`:89`）→ `mhy0/mhy1`（`:94`）→ `Blb\x02/\x03`（`:99/:104`）→ HYG 7 字节（`:112`）→ Naraka 7 字节（`:117`）→ gunfire 9 字节（`:125`，`Position=0x32`）→ 兜底 `ResourceFile`（`:132`）。
3. `PreProcessing` 做两件事：
   - **整文件厂商解密**：`switch (game.Type)`，21 个 case（`FileType == ResourceFile || !game.Type.IsNormal()` 才触发，`FileReader.cs:186`；分支体 :189-250，其中 `OPFP/FakeHeader/ShiningNikki` 共用 `ParseFakeHeader` :200-203）。
   - **多 bundle 容器降级**：读 `signature/version/unityVersion/unityRevision/size`，若 `size != stream.Length` 则把类型改成 `BlockFile`（`FileReader.cs:252-271`）。这就是「HSR `.block` 里塞 100+ 个 ENCR」的识别点。

### 2.3 容器解析与块解压（hop 3）

**UnityFS 分支**（`AssetsManager.cs:285-287` → `LoadGameBlockFile` :660 → `new BundleFile` :674）：

| hop | 位置 | 动作 |
|---|---|---|
| 3.1 | `BundleFile.cs:124` | 构造，`ReadBundleHeader` :167 |
| 3.2 | `BundleFile.cs:174-204` | `UnityFS` 正常头；BH3 组走「加密头」：读 `uint key` 后 `XORShift128.InitSeed` :186/:191，并硬编码 `version=6` |
| 3.3 | `BundleFile.cs:205-210` | `ENCR`：`version=6` 且 `HasUncompressedDataHash=false` |
| 3.4 | `BundleFile.cs:148-156` | **UnityCN 分支**；`AzurPromiliaCBT2` 走 `UnityCN.SetKey(<字面量>)` :154（硬编码，见 §5.8） |
| 3.5 | `BundleFile.cs:376` `ReadHeader` | `XORShift128.Init` 时逐字段 XOR 解密头（:382-396），否则常规 `size/compressedBlocksInfoSize/uncompressedBlocksInfoSize/flags`（:404-411）；Naraka 额外 `-0xCA`（:413-417） |
| 3.6 | `BundleFile.cs:422` `ReadUnityCN` | 按 Unity 版本决定加密 flag 掩码：≤2020 / 2020.3.34 / 2021.3.2 / 2022.3.1 用 `BlockInfoNeedPaddingAtStart`，之后用 `UnityCNEncryption`（:435-447）；命中则 `new UnityCN(reader)` :454 |
| 3.7 | `BundleFile.cs:458` `ReadBlocksInfoAndDirectory` | `BlocksInfoAtTheEnd`(0x80) 时用 `m_Header.size` 而非 `BaseStream.Length` 定位 trailer（:465-482，为 `.block` 修正）；随后按 `flags & CompressionTypeMask` 解 blocksInfo：None / LZMA(`SevenZipHelper`) / LZ4·LZ4HC(可选 PerpetualNovelty 前置 XOR :516-523) / Lz4Mr0k(`Mr0kUtils.IsMr0k`→`Decrypt` :537-543)；解出 `StorageBlock[]` + `Node[]`（:547-583） |
| 3.8 | `BundleFile.cs:261` `CreateBlocksStream` | 解压目标缓冲：`sum(uncompressedSize)` ≤512MB 用 `GC.AllocateUninitializedArray` + `MemoryStream(publiclyVisible:true)`（:285-287），否则落 `path.temp`（`FileOptions.DeleteOnClose`，:279） |
| 3.9 | `BundleFile.cs:590` `ReadBlocks` | 逐块解压并按厂商分叉：NetEase LZMA 首块（:613-619）、`OodleHSR/OodleMr0k`（:623-671，>1MB 不走 ArrayPool :633-641）、LZ4 家族内嵌 `WmvUtils`(HNACB1 首块 + 后续块与首块 XOR :689-704)、`Mr0kUtils`（:706-710）、**UnityCN 块解密 `(blockInfo.flags & 0x100)`**（:711-720）、`NetEaseUtils`（:721-724）、`FairGuardUtils`（:725-728，以 `0xa6` 出现次数 >5 作为探针）、`OPFPUtils`（:729-732），最后 `LZ4.Instance.Decompress`（:733） |
| 3.10 | `BundleFile.cs:323` `ReadFiles` | 把 `Node[]` 转 `StreamFile[]`：优先对共享缓冲做**零拷贝 `MemoryStream` 切片**（:352-365），超大节点落 `_unpacked/` 文件（:341-351） |
| 3.11 | `AssetsManager.cs:694-712` | 遍历 `file.fileList`：`FileType.AssetsFile` → `LoadAssetsFromMemory`（:700）；否则进 `resourceFileReaders`（:707，`TryAdd` 失败即 `Dispose` 防泄漏） |

**其它容器**（同一 `LoadGameBlockFile` 分派，`AssetsManager.cs:670-688`）：
- `blk` → `BlkUtils.Decrypt(reader, (Blk)Game)` 返回 `XORStream`（`LoadBlockFile` :601-616）
- `mhy` → `MhyFile` :57（`mhy0` 走 `DescrambleHeader`、`mhy1` 走 `DescrambleHeader2`，:92-101）
- `Blb\x03` → `Blb3File` :21；`HYG` → `HygFile` :20；`VFS` → `VFSFile` :20
- `BlockFile`（多 bundle 聚合体）→ `LoadBlockFile` :601，`stream.GetOffsets(path)` 逐个 offset 造 `FileReader`（:631-640），**每个 offset 结束后 `AfterBundleLoaded?.Invoke()` 释放**（:751）
- `.lpak`（AFK Journey）→ `LoadAfkJourneyLpakFile` :212，单 `FileStream` + `BoundedStream` 逐 chunk（:224-233）

### 2.4 SerializedFile 解析（hop 4）

`new SerializedFile(reader, this)`（`AssetsManager.cs:317` / :384）内部顺序（`SerializedFile.cs:37-242`）：

1. 头 4 字段 `m_MetadataSize/m_FileSize/m_Version/m_DataOffset`（:47-50）
2. `>= Unknown_9` 读 `m_Endianess + m_Reserved[3]`，否则 seek 到 `m_FileSize - m_MetadataSize` 读端序（:52-62）——`m_Reserved` 后面被当 `mhy` classID 混淆标志用（:263）
3. `>= LargeFilesSupport(22)` 重读 64 位 size/offset（:64-71）
4. `m_FileEndianess == 0` → `LittleEndian`（:76-80）
5. `>= Unknown_7` 读 `unityVersion` 并 `SetVersion`（:81-86，正则拆 `buildType` / `int[] version`，:244-254）
6. `>= Unknown_8` 读 `m_TargetPlatform`，非法值回落 `UnknownPlatform`，mhy 系 `NoTarget` 强制 `StandaloneWindows64`（:87-101）
7. `>= HasTypeTreeHashes(13)` 读 `m_EnableTypeTree`（:102-105）
8. 类型表 `m_Types`（:108-114），每项 `ReadSerializedType`（:256）；GI 组且 `m_Reserved` 为真时 `DecodeClassID`（:263-267，`(v ^ 0x23746FBE) - 3`，:418-424）
9. 对象表 `m_Objects`（:122-179）：`byteStart += m_DataOffset`（:149）；`>= RefactoredClassId(16)` 用 `m_Types[typeID]` 反查 classID（:157-162）
10. 脚本表 `m_ScriptTypes`（:181-202）、外部文件 `m_Externals`（:204-223，`fileName = Path.GetFileName(pathName)`，是 `PPtr` 跨文件解析的 key）、`m_RefTypes`（:225-234）、`userInformation`（:236-239）

**TypeTree 两条读法**：`>= Unknown_12 || == Unknown_10` → `TypeTreeBlobRead`（:357，节点定长 + 字符串缓冲，`ReadString` 用 `0x80000000` 高位判定「offset 在 buffer 内」还是「查 `CommonString.StringBuffer`」，:394-408）；旧版本 → 递归 `ReadTypeTree`（:324，`m_Level` 靠递归深度，存在递归爆栈风险）。

版本补丁点：`CheckStrippedVersion`（`AssetsManager.cs:755`）——`AzurPromiliaCBT2` 被 strip 时强制 `"2022.3.62f3"`（:757-760）；AFKJourney 从 `globalgamemanagers` 反查版本（`ResolveUnityVersionHint` :415-458）；两者都没有且 `IsVersionStripped` 就抛异常（:767-770）。

### 2.5 对象实例化（hop 5）

```
AssetsManager.ReadAssets()                                  :814
 └─ foreach assetsFile.m_Objects
     ├─ new ObjectReader(assetsFile.reader, assetsFile, objectInfo, Game)   :830 → ObjectReader.cs:21
     └─ switch (objectReader.type)  → 30 个具名类型 + 兜底 new Object(...)   :833-867
         └─ assetsFile.AddObject(obj)                        :868 → SerializedFile.cs:411
```

`Object` 基类构造即 `reader.Reset()` 到 `byteStart`（`Object.cs:32-45`），并按 `platform == NoTarget` 额外读一个 `m_ObjectHideFlags`（:47-50）。`ReadAssets` 对每个对象 `try/catch` 单点隔离，失败只记日志继续（:870-880）。

`TypeTree` 在此之后才可能被用到（懒解析）：`Object.Dump()`/`ToType()`（`Object.cs:103/:121`）→ `TypeTreeHelper.ReadTypeString/ReadType`（:11/:166），读取完会比对 `reader.Position - byteStart != byteSize` 并告警（`:20-24`），即「TypeTree 与真实字节数不一致」是常态而非异常。

### 2.6 后处理（hop 6）

`ProcessAssets()`（`AssetsManager.cs:887`）：
1. ZZZ 组先建 `separateMeshes`（按 Mesh 名建 `PPtr<Mesh>` 索引，:895-932）
2. 所有 `GameObject.m_Components` 的 `PPtr.TryGet` 回填 `m_Transform / m_MeshRenderer / m_MeshFilter / m_SkinnedMeshRenderer / m_Animator / m_Animation`（:943-983）
3. `SpriteAtlas.m_PackedSprites` 回指父图集（含 variant 处理，:984-1010）
4. ZZZ 组用 `MonoBehaviour("NapLodController")` 的原始字符串反解 `.mesh` 路径，把 `separateMeshes` 挂回 `SkinnedMeshRenderer/MeshFilter`（:1014-1122，纯启发式，源码自注 TODO :897-898）

### 2.7 导出（hop 7，跨项目）

核心库**不含**导出实现，导出在 `AnimeStudio.GUI`（CLI 有一份复制）：
- `Studio.ExportAssets(savePath, List<AssetItem>, ExportType, openAfterExport)` — `AnimeStudio.GUI/Studio.cs:667`，按 `AssetGroupOption`（ByType/ByContainer/BySource）算目录（:680-712），再按 `ExportType` 分派 Raw/Dump/Convert/JSON（:727-753）
- `Exporter.ExportConvertFile(item, path)` — `AnimeStudio.GUI/Exporter.cs:646`，`switch (item.Type)` 到 GameObject/Texture2D/AudioClip/Shader/TextAsset/MonoBehaviour/Font/Mesh…（:648-664）
- `AssetItem`（`ListViewItem` 子类）持有 `public Object Asset` — `AnimeStudio.GUI/Components/AssetItem.cs:7`
- FBX 落地：`ModelConverter` → `ModelExporter.ExportFbx`（`Exporter.cs:609-626`，实现在 `AnimeStudio.Utility/ModelExporter.cs:5`）

> **二次开发提示**：想把核心库变成纯 headless 库，必须把 `Exporter.cs` 的 `switch` 从 GUI 抽到 `AnimeStudio.Utility`，否则 CLI 与 GUI 的导出行为会双份漂移（两份 `Exporter.cs` 已存在：`AnimeStudio.CLI/Exporter.cs` 与 `AnimeStudio.GUI/Exporter.cs`）。

---

## 3. 索引与映射（AssetMap / CABMap）

### 3.1 两层映射，职责不同

| 映射 | 载体 | 语义 | 生产者 | 消费者 |
|---|---|---|---|---|
| **CABMap** | `Maps/<name>.bin`（`BinaryWriter` 裸格式） | CAB 文件名 → (相对路径, 容器内 offset, 依赖 CAB 名列表) | `AssetsHelper.BuildCABMap` :150 → `DumpCABMap` :282 | `LoadCABMap` :308/:329 → `ProcessFiles` :120 → `AssetsManager.OffsetData` :72 |
| **AssetMap** | `<name>.map`（MessagePack）/`.xml`/`.json` | 资产条目列表：(Name, Container, Source, PathID, Type, Hash, Offset) | `BuildAssetMap` :376 / `BuildAssetMapStreaming` :409 / `BuildBoth` :906 | `ParseAssetMap` :719、`ResourceMap.FromFile` :27 |

### 3.2 CABMap 二进制布局

`DumpCABMap`（`AssetsHelper.cs:282-306`）写：`BaseFolder` → `count` → 每条 `cab → (Path, Offset(int64), depCount, dep...)`；`ParseCABMap`（:351-374）逆序读回。**没有任何版本号/魔数**——格式一改，旧 `.bin` 静默错读（`ParseCABMap` 外层 `try/catch` 只 `Logger.Warning`，:342-346）。

条目来自 `BuildCABMap(file, ref collision)`（:256-280）：`Path = Path.GetRelativePath(BaseFolder, file)`，`Offset = assetsFile.offset`（来自 `LoadAssetsFromMemory(..., originalOffset)`，`AssetsManager.cs:386`），`Dependencies = assetsFile.m_Externals.Select(fileName)`；重复 CAB 名只计 `collision` 并丢弃后者（:273-277）。

### 3.3 偏移裁剪加载（依赖解析）

`ProcessFiles`（:120-134）→ `FindCAB`（:112，`CABMap.AsParallel()` 按相对路径反查，**O(n) 线性扫描**）→ `AddCABOffsetsFast`（:84-110，BFS 展开依赖闭包）→ 写 `Offsets[fullPath]`。随后 `AssetsManager.LoadFile(reader)` 把 `FilterData.Items` 命中的 offset 灌进 `OffsetData[fileName]`（`AssetsManager.cs:247-278`），`LoadBlockFile` 只对这些 offset 造 reader（:623-647）。这就是「只解压必要 chunk」的机制。

### 3.4 AssetMap 序列化契约

- `[MessagePackObject] AssetMap { GameType, AssetEntries }` — `AssetMap.cs:35-43`，`[Key(0)]/[Key(1)]`
- `AssetEntry` — `AssetMap.cs:45-117`，`[Key(0..6)] = Name/Container/Source/PathID/Type/Hash/Offset`；`Container`/`Source` 写入时经 `StringCache.Get` 驻留（:60-71），`Name`/`Hash` 故意不驻留（:53/:79 注释：基本唯一，驻留只会涨内存）
- `AssetEntry.Matches(Dictionary<string,Regex>)` :104 提供字段级正则过滤（`"SHA256Hash"` 是 `Hash` 的别名，:99）
- 流式写盘：先逐条 `MessagePackSerializer.Serialize(tempEntries, asset)` 到临时文件，收尾再手工拼 `[GameType, count]` 头 + `CopyTo`（`AssetsHelper.cs:479-505`）。**注意**：拼装时写的是**未压缩** MessagePack 数组头，而读侧 `ParseAssetMap` 用 `WithCompression(Lz4BlockArray)`（:728）——MessagePack 的 Lz4 读取器能容忍未压缩 payload，这是隐式契约，改动序列化选项会破功。
- 小游戏/Big-map 差异：GI 子组或请求 JSON 时才全量驻留内存以便 `UpdateContainers` 回写容器名（:386-392 / :809-836，依赖 `ResourceIndex.GetContainer` :75）；其余游戏一律流式（:395）。

### 3.5 去重/差分

- `AssetEntryComparer`（`AssetEntryComparer.cs:9-25`）以 `(Name, Container, PathID)` 判等——可用于两次构建结果比对，但**仓库内没有任何调用点**（grep 无命中）。
- 实际「差分」能力只有 `AssetEntry.Hash`：`Object.GetHash()` 走 64KB 分块 xxHash64（`Object.cs:71-101`），超过 16MB 的对象退化为 `size:<hex>` 占位（`AssetsHelper.cs:674-677`）——目的是让大 shader/mesh 不吞内存，代价是**跨版本差分对大对象失效**。
- 结论：**没有内建 map diff 工具**。二次开发若要「版本增量包」，需自行实现 `AssetEntryComparer` 之上的集合差，并注意 `Hash` 的 size 退化与 `Offset` 变动噪声。

### 3.6 路径索引（GI 专用）

`AssetIndex`（`AssetIndex.cs:5`，pascal/camel 混排的 JSON DTO：`Types/SubAssets/Dependencies/PreloadBlocks/Assets(BlockInfo{Language,Id,Offset})/SortList`）→ `ResourceIndex.FromFile`（:13，`JsonConvert.PopulateObject` 到单例）→ `BuildBundleMap`（:47，`bundleId → (pathHashLast → Name)`）→ `GetContainer(id, last)`（:75）。仅 GI 生效（`AssetsHelper.UpdateContainers` :811 判 `IsGISubGroup()`）。

---

## 4. 加密架构

### 4.1 分层：三层各管一段

```
L1 整文件        ImportHelper.Decrypt*      ← FileReaderExtensions.PreProcessing (FileReader.cs:189)
L2 容器头/blocksInfo  BundleFile.ReadHeader / ReadUnityCN / ReadBlocksInfoAndDirectory
L3 数据块        BundleFile.ReadBlocks 内联各厂商 Utils，由 StorageBlock.flags & 0x3F / & 0x100 驱动
```

设计特征：**L1 返回「另一个 FileReader」**（新 stream），**L2 原地改 header 字段**，**L3 原地 `Span<byte>` 改写**。因此 L1 必须整文件读入内存，L3 可以按块流式。

### 4.2 厂商 × 密码族对照

| 厂商/游戏 | 层 | 密码族 | 密钥来源 | 锚点 |
|---|---|---|---|---|
| UnityCN（PGR / Neural Cloud / Archeland / Cat Fantasy …约 24 款） | L2+L3 | AES-128-**ECB** 单块白化密钥 + 运行时派生的**半字节索引替换流密码**（见 4.3） | `CNKeys.json`（16 字节 hex）或内置表 | `UnityCN.cs:17/51/71/93`；`UnityCNManager.cs:11/39` |
| HoYo GI/BH3/SR/ZZZ/TOT | L2+L3 | `mr0k`：AES-128 两轮（expansion key + 定制 S 盒映射）+ 种子 XOR pad；`mhy0/mhy1`：定制 AES + RC4 + Descramble | 内嵌常量 + `CryptoHelper` 分区表 + `Mr0k`/`Mhy` record 字段 | `Mr0kUtils.cs:12-71`；`MhyFile.cs:57`；`GameManager.cs:143/177` |
| GI CB2/CB3、ZZZ CB2（`blk`） | L2 | **MT19937_64** 生成 0x1000 XOR pad，从 `DataOffset=0x2A` 起覆盖 | `Blk` record（ExpansionKey/SBox/InitVector/InitSeed） | `BlkUtils.cs:14-65`；`XORStream.cs:18` |
| 腾讯系 Blb3 / HYG / WMV / HNA | L2 | **定制 AES**（`BlbAES`，非标准 S 盒/ShiftRow/KeyExpansion）+ **变体 RC4**（含 `xor/sub/add` 三分支）+ 16 字节 Descramble | `BlbUtils.InitKeys` 静态注入，表在 `CryptoHelper` BLB3/WMV/HYG 分区 | `BlbUtils.cs:16/25/54/69/123`；`BlbFile.cs:24`；`HygUtils.cs:12`；`WmvUtils.cs:11` |
| NetEase | L3 | 头扫描（`0xEE 0xDD`）+ 自定义多项式 CRC 派生 4 个 key part + RC4 + 0x80 对齐块 XOR 混合 | 从密文自身前 0x20 字节派生（无外部密钥） | `NetEaseUtils.cs:12/18/89/169/210` |
| Arknights: Endfield CB1/CB2 | L3 | `0xA6` 掩码 + 整数混洗（XOR/SUB/ADD 常量表，CB1/CB2 各一套）派生 seed 与 key | 全部常量内嵌（`CB1Constants`/`CB2Constants`） | `FairGuardUtils.cs:9/23/37` |
| OPFP | L3 | 路径派生 key（路径长度/文件名长度）+ 链式 XOR（v0）/ 原地置换（v1） | **路径字符串**（`BundleResources/` 之后的相对路径前缀匹配） | `OPFPUtils.cs:13/39/66` |
| Endfield VFS | L2 | 头字段**位旋转 + XOR 解混淆**（`RotateRight/Left` + 常量）+ 定制 AES（`VFSAES`/`VFSCB3AESSBox`） | 内嵌常量 | `VFSUtils.cs:13/44/97-120/414` |
| EnsembleStars / Mark / Naraka / FakeHeader / PerpetualNovelty / AzurPromilia / AFK `.jsone` | L1 | 字节级 XOR / 多表混合 / 固定偏移 XOR 段 / 魔数重定位 | 内嵌常量 / 固定偏移 | `ImportHelper.cs:277/228/303`；`BundleFile.cs:212-216/408-417/516-523/152-156` |

### 4.3 UnityCN 密钥选择与派生布局

**密钥库（谁提供 16 字节 hex）**
- 内置默认表 `UnityCNManager.baseData`（`UnityCNManager.cs:11-37`）：`List<List<string>>`，每行 `[枚举名, 展示名, hexKey]`，约 24 条。**本文不复制其中任何一行。**
- 持久化：`CNKeys.json`，位于 `AppDomain.CurrentDomain.BaseDirectory`（即 exe 旁），常量 `UnityCNKeysPath` :39；读 `ReadJson` :64（空/解析失败即 `ResetJson` 重写默认表，:71-82/:85-92），写 `SetKeys` :54（`WriteIndented=true`）。
- 装配：`GameManager.LoadGames` 把 `CNKeys.json` 每行转成 `UnityCNGame(type, new UnityCN.Entry(name, key))`（`GameManager.cs:78-89`），`enumName` 解析失败回落 `GameType.UnityCN`（:86）；额外追加 `UnityCNCustomKey`（:91）。
- 选中：GUI `MainForm.cs:286-296`（含 `Properties.Settings.Default.lastUnityCNKey` 的用户自填 key 分支）、GUI `MainForm.cs:2498-2521`、CLI `Program.cs:37-41`。
- 下发：`UnityCNManager.SetKey(entry)` → `UnityCN.SetKey(entry)` → **静态 `ICryptoTransform Encryptor`**（`UnityCN.cs:12`，`CipherMode.ECB`，:51-69/:71-91）。`Entry.Validate()` 只校验长度 16 字节（:170-180）。

**头部密钥区块布局（`UnityCN.cs:17-49`）** —— 只描述结构，不给值：
1. `ReadUInt32()` 跳过魔数
2. `infoBytes[0x10]` + `infoKey[0x10]`，跳 1 字节
3. `signatureBytes[0x10]` + `signatureKey[0x10]`，跳 1 字节
4. `DecryptKey(signatureKey, signatureBytes)`：`AES-ECB-Encrypt(signatureKey)` 后与 `signatureBytes` 逐字节 XOR（:102-110）；结果必须是 `"#$unity3dchina!@"` 或 `"#$manjuuunity*!@"`（:9-10/:33-36），否则抛异常 —— **这是快速判断「是否 UnityCN 且 AES 密钥选对」的唯一探针**
5. `DecryptKey(infoKey, infoBytes)` → 转半字节数组（`ToUInt4Array`）→ 前 0x10 半字节填 `Index[0x10]`，后 0x10 半字节以 `idx = (i%4*4)+(i/4)` 转置填 `Sub[0x10]`（:40-47）

**块解密（`DecryptBlock` :93-100 → `Decrypt` :122-157 → `DecryptByte` :112-120）**：本质是一个「长度编码 + 半字节替换」的流式解码器——每轮读 1 字节得 `high/low` 半字节，`high==0xF` 时继续累加（0xFF 继续），然后 `offset += high`，再吐 2 字节，`low==0xF` 时再消耗一段。`DecryptByte` 用 `Sub` 四段 + `Index` 双半字节查表做反替换。**它只解「被加密的块」，未加密块直通**，判定条件是 `StorageBlockFlags` 的 `0x100` 位（`BundleFile.cs:711/716`）。

**新增一个 UnityCN 系游戏的成本**：`CNKeys.json` 追加一行 + `GameType` 枚举多一个成员（或复用 `UnityCNCustomKey` 让用户填）即可，无需改代码。

### 4.4 新厂商密码如何插进来（四条挂载点）

| 若加密发生在… | 插入点 | 需要改的 3 处 |
|---|---|---|
| 整个文件（自定义头/魔数） | `FileReader.CheckFileType`（`FileReader.cs:37`）+ `PreProcessing` switch（`:189`） | ① `ImportHelper.DecryptXxx(FileReader) → FileReader` ② `FileType` 枚举（`FileType.cs:9`）③ `GameType` + `IsXxx()` 谓词 |
| 容器头 / blocksInfo | `BundleFile.ReadHeader`（:376）/`ReadBlocksInfoAndDirectory`（:458） | ① header 字段解密 ② `ArchiveFlags` 新 flag（:14） ③ `Game` record 携带密钥字段 |
| 数据块 | `BundleFile.ReadBlocks` switch（:603） | ① `CompressionType` 新枚举值（:32） ② `XxxUtils.Decrypt(Span<byte>, ...)` ③ `CryptoHelper` 新分区常量 |
| 容器格式整体不同（mhy/blb/hyg/vfs 式） | `AssetsManager.LoadGameBlockFile` switch（:670） | ① `XxxFile` 类（暴露 `m_Header` + `fileList`）② `FileType` + 嗅探 ③ `GameManager` 注册 |

**统一约定（必须遵守才能被管线接住）**：容器类必须暴露 `public BundleFile.Header m_Header` 与 `public List<StreamFile> fileList`（`AssetsManager.cs:693-700` 依赖这两个成员，且用 `dynamic` 访问 —— 拼错成员名只会在运行时炸）。

---

## 5. 扩展点与「尖锐边缘」

### 5.1 God class 与耦合半径

| 文件 | LOC | 症状 |
|---|---|---|
| `ImportHelper.cs` | 1129 | 20 个厂商解密器平铺在一个 static class，无策略接口、无注册表；每个都自己 `reader.Dispose()` 并 new 新 reader（所有权约定隐式） |
| `AssetsManager.cs` | 1126 | 一个 `switch` 覆盖 7 种容器 + `dynamic` 分派（:668）+ 后处理业务逻辑（ZZZ 网格挂载，:1014-1122） |
| `AssetsHelper.cs` | 1052 | 索引构建 + 内存管理（`GC.Collect` :216/:249-252）+ 序列化格式 + 依赖解析挤在一起 |
| `BundleFile.cs` | 922 | 6 个厂商块解密内联在 `ReadBlocks` 的 `switch` 里（:603-740），新增厂商必然改这里 |

### 5.2 静态可变全局状态（并发第一杀手）

| 状态 | 位置 | 风险 |
|---|---|---|
| `AssetsHelper.assetsManager`（单例 `AssetsManager`） | `AssetsHelper.cs:28` | 索引构建全局串行；`Game`/`SpecifyUnityVersion` 被就地改写（:159/:382/:915） |
| `CABMap` / `Offsets` / `BaseFolder` | `AssetsHelper.cs:26-27/25` | 静态字典，无锁 |
| `UnityCN.Encryptor` | `UnityCN.cs:12` | **进程级单密钥**：同一进程内不可能同时解两个不同 UnityCN 游戏 |
| `BlbUtils.BlbRC4Key/BlbSBox/...` | `BlbUtils.cs:10-14` | `InitKeys` 每次解析文件都覆写（`BlbFile.cs:24`、`HygUtils.cs:40`、`WmvUtils.cs:13`）→ 多线程解 blb/hyg/wmv **必然串味** |
| `XORShift128.Init` / 静态种子 | `XORShift128.cs:12` | 头解密后置 false（`BundleFile.cs:396`），非重入 |
| `TypeFlags.Types` | `TypeFlags.cs:7` | 全局开关，`SetTypes` 与读并发无保护 |
| `Logger.Silent` / `Progress.Silent` / `Progress.preValue` | `AssetsManager.cs:87-88`、`Progress.cs:8` | 进度百分比单调递增（`value > preValue`，:30），多任务会互相吞进度 |
| `StringCache._cache` | `AssetMap.cs:10` | 无锁 `HashSet`；靠调用方在 bundle 边界 `Clear()`（`AssetsHelper.cs:212/244`） |

### 5.3 线程安全：对象解析天然不可并行

`SerializedFile` 只有一个 `FileReader`，`ObjectReader` 直接把它的 `BaseStream` 传给基类（`ObjectReader.cs:21`），`Object` 构造只做 `Position = byteStart`（`Object.cs:35`）。**任何两个对象不能并发读**。`ReadAssets` 的顺序 `foreach`（`AssetsManager.cs:821-884`）不是风格选择，而是约束。要并行化必须改成「每对象一次 `File.Open` + `BoundedStream`」或做 per-thread reader 池。

### 5.4 内存策略：三层补丁叠在一起

1. **解压缓冲**：`sum <= 512MB` 内存池化数组，否则 `DeleteOnClose` 临时文件（`BundleFile.cs:269-287`）
2. **CAB 切片零拷贝**：`MemoryStream(shared.Array, offset, size, writable:false, publiclyVisible:true)`（`BundleFile.cs:352-365`），代价是**整块解压缓冲被所有 CAB 视图 pin 住**
3. **大对象哈希退化**：`>= 16MB` 只记 `size:<hex>`（`AssetsHelper.cs:674-677`）
4. **手工 GC**：每 bundle 后 `GC.Collect(Optimized, blocking:false)`（`AssetsHelper.cs:216`）、每 32 文件一次（:249-252）
5. **ArrayPool 阈值**：>1MB 不走 `ArrayPool.Shared`，避免池长期驻留 100MB+ 缓冲（`BundleFile.cs:633-641`）

这套组合是「在 OOM 边缘反复调参」的产物（源码注释直接点名 HSR `.block` / 文件 ~3580 的 OOM，`AssetsHelper.cs:193-195`、`BundleFile.cs:626`）。二次开发若换并发模型，必须先拆掉 `GC.Collect` 与共享缓冲零拷贝这两处耦合。

### 5.5 错误处理：静默降级为主

- 容器/文件级失败：`catch → Logger.Error → return`（`AssetsManager.cs:363-367/491-498/592-599/651-654/738-746`），**不抛给调用方**，上层只看到 `assetsFileList` 少了几项
- 对象级失败：逐对象 catch 并把 `Assets/Path/Type/PathID` 打进日志（`AssetsManager.cs:870-880`；同样逻辑复制在 `AssetsHelper.cs:653-663`）
- 类型不匹配：`InvalidCastException` 被专门捕获并提示「期望 `Mr0k`/`Mhy`，实际是 X」（`AssetsManager.cs:714-737`）——即 `(Mr0k)Game` 这类硬转换是主要失败面
- 真正的硬失败只有三处：`CheckStrippedVersion` 版本缺失（`AssetsManager.cs:767-770`）、`CreateBlocksStream` 块尺寸非法（`BundleFile.cs:270-278`）、`ReadBlocksInfoAndDirectory` 尺寸越界（:474-478）

### 5.6 已确认的代码缺陷（二次开发前必看）

| 位置 | 问题 |
|---|---|
| `WebFile.cs:47-56` | `fileList = new List<StreamFile>()` 后直接 `fileList[i] = file` —— 对**空 list 索引赋值**，任何含条目的 `UnityWebData1.0` 都会抛 `ArgumentOutOfRangeException`。整个 WebFile 路径实际不可用（外层 catch 兜住，表现为「静默不解包」） |
| `XORStream.cs:24` | `for (int i = offset; i < count; i++)` 上界应为 `offset + read`。当 `offset != 0` 时会少 XOR，`blk` 容器经 `OffsetStream` 组合后存在踩中风险 |
| `BundleFile.cs:152-156` | `AzurPromiliaCBT2` 的 key 以**字面量硬编码在逻辑里**（本文不复制该值），换版本必须改源码重编译 |
| `BundleFile.cs:711-732` | 6 个厂商判断是**顺序 if 链**而非 else-if；理论上一个游戏命中多个条件会叠加解密。当前游戏矩阵下互斥，但新增厂商极易踩 |
| `AssetsHelper.cs:112-118` | `FindCAB` 每次调用 `CABMap.AsParallel().Where(...)` 全表扫描；`ProcessFiles` 内层循环调用它 → O(n²)，大目录索引构建的主要耗时点 |
| `ImportHelper.cs:19-47` | `MergeSplitAssets` 把合并结果**写回用户资产目录**（`File.Create(destFull)`），不是临时目录。只读挂载/受限权限场景会直接失败 |
| `SerializedFile.cs:324-355` | `ReadTypeTree` 是递归实现且深度由文件内容驱动，恶意/损坏文件可致 `StackOverflow`（不可捕获） |

### 5.7 显式扩展点（设计上留了口子）

| 扩展点 | 位置 | 用法 |
|---|---|---|
| `ILogger` 接口 | `ILogger.cs:21`（`DummyLogger` :26 / `ConsoleLogger` :31 / `FileLogger` :39） | `Logger.Default = ...` 接管日志；`Logger.Flags` 做分级过滤（CLI `Program.cs:45`） |
| `Progress.Default` | `Progress.cs:7` | GUI 换成 `ProgressBar` 适配器 |
| `AssetsManager.AfterBundleLoaded` | `AssetsManager.cs:25` | 每个 bundle 结束的回调 —— 索引器用它做 flush（`AssetsHelper.cs:190-217`）。**这是核心库暴露的最重要生命周期钩子** |
| `AssetsManager.ResourceFileCount` | `AssetsManager.cs:28` | 判断是否需要 flush |
| `AssetsManager.SkipProcess` / `Silent` / `SpecifyUnityVersion` / `ResolveDependencies` | `:16-19` | 索引模式（`SkipProcess=true`）与交互模式的唯一开关 |
| `TypeFlags.SetTypes` | `TypeFlags.cs:9` | 运行时裁剪「解析/导出」白名单；CLI 从 `Settings.Default.types` 反序列化注入（`Program.cs:50`） |
| `Game` record 继承链 | `GameManager.cs:114-189` | 新增厂商 = 新增 record + 在 `LoadGames` 注册一行（:27-92） |
| `AssetMap` 的 `[Key(n)]` | `AssetMap.cs:38-87` | 数据契约。**新增字段只能追加新 Key，且会破坏旧 `.map`，需版本头**（当前无版本字段） |
| `IImported` + `ModelConverter` + `ModelExporter` | `AnimeStudio/IImported.cs:7`、`AnimeStudio.Utility/ModelExporter.cs:5` | 模型导出后端可替换（FBX 之外的格式从这里接） |

### 5.8 部署期硬依赖

- `AnimeStudio.Ooz.dll`（native，`OodleHelper.cs:8` P/Invoke，`CallingConvention.StdCall`）—— 缺失则所有 Oodle 块（HSR/ZZZ 2.0）直接抛 `DllNotFoundException`
- `CNKeys.json` 与 exe 同目录（`UnityCNManager.cs:39`）—— 首次运行会**自动写盘**（`ResetJson` :85-92），只读安装目录会失败
- `Maps/` 目录（`AssetsHelper.cs:20/46`）
- `Properties.Settings` 在 GUI/CLI 中承载大量行为开关（`exportAllNodes`、`fbxVersion`、`minimalAssetMap`…），核心库本身不读 settings —— 迁移到无 GUI 服务时必须重建配置层

---

## 6. 最重要的 10 个集成接缝（file:line）

| # | 接缝 | 为什么关键 |
|---|---|---|
| 1 | `AnimeStudio/AssetsManager.cs:245` `LoadFile(FileReader)` 的 `switch (reader.FileType)` | 所有容器类型的唯一分派点；加新格式从这里进 |
| 2 | `AnimeStudio/FileReader.cs:183` `PreProcessing(this FileReader, Game)` | 唯一「整文件级」解密/降级入口；20 个厂商分支在此 switch |
| 3 | `AnimeStudio/AssetsManager.cs:660` `LoadGameBlockFile` + `:674` `new BundleFile` + `:694` `foreach (file.fileList)` | 容器对象契约（`m_Header` + `fileList`）在此被 `dynamic` 消费；CAB/资源文件的分流也在 :698-711 |
| 4 | `AnimeStudio/BundleFile.cs:590` `ReadBlocks` 的 `switch (compressionType)`（厂商块解密链 :603-740） | 数据块级解密/解压的唯一现场；`0x100` flag 判定在 :711 |
| 5 | `AnimeStudio/SerializedFile.cs:37` 构造 + `:108` 类型表 + `:122` 对象表 + `:204` externals | `.assets` 元数据全部在此解析；`m_Externals` 决定 `PPtr` 能否跨文件解析 |
| 6 | `AnimeStudio/ObjectReader.cs:21` + `AnimeStudio/Classes/Object.cs:32` | 对象读取作用域与 `byteStart/byteSize` 边界；**共享 stream，禁止并发** |
| 7 | `AnimeStudio/Classes/PPtr.cs:75` `TryGet` + `:39` `TryGetAssetsFile` | 全库引用解析唯一实现，靠 `assetsFileIndexCache`（`AssetsManager.cs:32`）做 O(1) 定位；索引缓存不变量一破，引用全断 |
| 8 | `AnimeStudio/TypeTreeHelper.cs:11/:166` + `AnimeStudio/Classes/Object.cs:103` `Dump` | 泛型反射读取（MonoBehaviour/未知类型导出）的唯一通孔；`SerializedType.m_Type` 为 null 时全失效 |
| 9 | `AnimeStudio/AssetsHelper.cs:523` `BuildAssetMap(file, batch, ...)` + `:409` `BuildAssetMapStreaming` | 资产发现/命名/容器归并/Hash 策略的唯一实现，`AssetEntry` 字段语义在此定型 |
| 10 | `AnimeStudio/UnityCN.cs:71` `SetKey(string)` + `:93` `DecryptBlock` + `AnimeStudio/UnityCNManager.cs:39` `CNKeys.json` 路径 | UnityCN 生态（约 24 款游戏）的完整密钥选择→解密链路；静态单密钥是硬约束 |
| 11* | `AnimeStudio.GUI/Exporter.cs:646` `ExportConvertFile` 的 `switch (item.Type)` | 实际导出分发点（核心库无导出）；CLI/GUI 双份实现的漂移源 |
| 12* | `AnimeStudio/GameManager.cs:278` `GameTypes` 谓词族 + `:114-189` record 继承链 | 所有厂商分支的判据来源；新增游戏必须同时扩枚举、谓词、record 与 `LoadGames` 注册 |

\* 备选，若报告需要控制在 10 条内可合并 11/12。

---

## 7. 一页速查（给二次开发者的最小行动清单）

1. **接新游戏**：`GameType` 加枚举（`GameManager.cs:191`）→ `GameTypes.IsXxx()`（:278）→ `LoadGames` 注册（:27-92）→ 若需要密钥就 `record Xxx : Game`（仿 :143-189）。
2. **接新容器**：`FileType` + 魔数嗅探（`FileReader.cs:37`）+ `XxxFile` 类（暴露 `m_Header`/`fileList`）+ `LoadGameBlockFile` switch（`AssetsManager.cs:670`）。
3. **接新块加密**：`CompressionType` 值（`BundleFile.cs:32`）+ `XxxUtils.Decrypt(Span<byte>, Game)` + `ReadBlocks` case（:603）。
4. **批量索引**：走 `AssetsHelper.BuildBoth`（:906），**不要**自己循环 `LoadFolder`（会重复 `MergeSplitAssets` 全目录扫描，见 :180-182 注释）。
5. **内存吃紧**：先看 `BundleFile.cs:261` 的 512MB 阈值与 `AssetsHelper.cs:177` 的 per-bundle flush 是否被绕过；`AfterBundleLoaded`（`AssetsManager.cs:25`）没被赋值就等于关闭了流式回收。
6. **要并发**：从 `ObjectReader`（`ObjectReader.cs:21`）与 `BlbUtils` 静态密钥（`BlbUtils.cs:16`）两处开始重写，别在 `ReadAssets` 上加 `Parallel.For`。
