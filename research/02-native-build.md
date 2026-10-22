# 02 · 原生库与构建体系（Native / Build / Release）

> 分析对象：`C:\Users\28529\Desktop\AnimeStudio`（github.com/Escartem/AnimeStudio，HEAD `e616e3f`）
> 范围：`AnimeStudio.Ooz`（含 libpoe / simde）、`AnimeStudio.ACL.{DB,MHY,SR,ZZZ,ZZZV2}`、`AnimeStudio.HLSLDecompiler`、`AnimeStudio.FBXNative`、`AnimeStudio.FBXWrapper`、`AnimeStudio.PInvoke`、`AnimeStudio.Libraries`、`AnimeStudio.Utility` 的第三方部分、`build.ps1` / `AnimeStudio.slnx` / `Directory.Build.props` / `.github/workflows/build.yml`。
> 所有行号为该 commit 下的真实行号；未运行任何构建、未安装任何 SDK。

---

## 0. TL;DR（10 条）

1. **仓库里没有任何 C++/CLI（`/clr`）**。全仓 `grep -E 'clr|CLRSupport|CompileAsManaged'` 在 `*.vcxproj/*.props/*.targets/*.csproj` 中命中 **0** 次。8 个原生项目全部是**纯 native DLL + `extern "C"` 导出 + C# `DllImport`**。
2. 原生导出统一走两个手写宏：ACL/FBX 用 `AS_API(ret)` → `extern "C" __declspec(dllexport) ret __stdcall`（`AnimeStudio.ACL/AnimeStudio.ACL.DB/dllmain.h:53`、`AnimeStudio.FBXNative/dllexport.h:53`）；Ooz 用 `OOZ_DLL_PUBLIC`（`AnimeStudio.Ooz/kraken.cpp:28`）；HLSL 直接用 `extern "C" __declspec(dllexport)`（`AnimeStudio.HLSLDecompiler/api.cpp:29`）。
3. **原生库不由 solution 全量构建，也完全不在 CI 里构建**。`build.ps1` 只 `dotnet build` 三个托管项目（`build.ps1:4,58,63`）；CI 里 `microsoft/setup-msbuild`（`build.yml:20`）是死代码。原生 DLL 以**预编译二进制提交进 git**：`AnimeStudio.Libraries/` 共 **9 个 DLL / 7.65 MB**，占工作树 25%。
4. **交接点唯一**：所有 `vcxproj` 的 `OutDir` 都写死为 `$(SolutionDir)\AnimeStudio.Libraries\`（例：`AnimeStudio.Ooz/AnimeStudio.Ooz.vcxproj:53,59`），托管侧再用 `ContentWithTargetPath` 把它们摊平复制到输出目录（`AnimeStudio/AnimeStudio.csproj:29-36`、`AnimeStudio.Utility/AnimeStudio.Utility.csproj:27-55`、`AnimeStudio.GUI/AnimeStudio.GUI.csproj:44-47`）。
5. 9 个二进制全部 **PE32+ / machine 0x8664（x64）**，且 CRT 静态链接（导入表只有 `KERNEL32.dll`，FBXNative 多 `ADVAPI32`）→ 无 VC++ Redistributable 依赖，也**天然不可能跨到 x86/ARM64**。
6. `AnimeStudio.FBXNative.dll` 把 FBX SDK **静态**链入（`libfbxsdk-mt.lib;libxml2-mt.lib;zlib-mt.lib`，`AnimeStudio.FBXNative/AnimeStudio.FBXNative.vcxproj:78,100`），但 include/lib 路径是**硬编码绝对路径** `C:\Program Files\Autodesk\FBX\FBX SDK\2020.3.7\...`（同文件 `:71,79,92,101`，4 处）→ fork 最大的单点阻塞。
7. ACL 有**五份互相独立的 ACL 源码树拷贝**（每份 0.78–4.62 MB，合计 297k 行）：DB/ZZZV2 = ACL 2.1.x，ZZZ = ACL 2.0.99，MHY/SR = ACL 1.x（`acl/core/ansi_allocator.h` + `acl/algorithm/uniformly_sampled/decoder.h`，`AnimeStudio.ACL.MHY/dllmain.cpp:25-27`）。同一套 `DecompressTracks` 符号在 3 个 DLL 里重复出现，靠文件名而非符号隔离。
8. ACL 的**调用约定在纸面上是错的**：native 是 `__stdcall`（`dllmain.h:46`），C# 全部声明 `CallingConvention.Cdecl`（`AnimeStudio.Utility/ACL/ACL.cs:37,82,229,235,241`）。x64 只有一种约定所以能跑，这等于把「x86 支持」在 ABI 层钉死为不可能。
9. 发布产物结构是 `dist/<tfm>/` 放两个 exe + `LICENSE`，其余全部塞进 `dist/<tfm>/bin/`，靠 **Patcher 改写 apphost 内嵌路径**（`build.ps1:60,65` 传 `-d bin`；`AnimeStudio.Patcher/Program.cs:36-41`）实现「exe 当启动器」。`DllLoader` 反向依赖这个约定（`AnimeStudio.PInvoke/DllLoader.cs:32-35`）。
10. 两处**可验证的接线缺陷**：① `ACLLibs.ACL`（MHY 非-ZZZ 路径）用 `archSpecific: true` 去 `bin\x64\` 找 DLL（`ACL.cs:19`），但打包流程从不创建 `x64/` 目录；② `AnimeStudio.FMOD.dll` 只挂在 GUI 的 csproj 上（`GUI.csproj:44-47`），而 CLI 也调用 `AudioClipConverter`（`AnimeStudio.CLI/Exporter.cs:50`），单独 `dotnet build AnimeStudio.CLI` 会缺这个二进制。

---

## 1. 量化基线

### 1.1 仓库规模

| 指标 | 数值 | 备注 |
|---|---|---|
| git 跟踪文件数 | **1,533** | `git ls-files \| wc` |
| 工作树体积（不含 `.git`） | **30.59 MB** | 1,533 个文件 |
| `.git` pack | **5.83 MiB**（1,455 objects） | 单 pack，无不合理膨胀 |
| 提交进 git 的二进制 | **9 个 DLL = 7.65 MB** | `git ls-files \| grep '\.dll$'` |
| 二进制 / 工作树占比 | **25.0 %** | 7.65 / 30.59 |
| `.cs` | 265 个 / **63,764 行** | |
| `.cpp` | 45 个 | 原生实现文件很少，逻辑都在 `.h` 模板里 |
| `.h`+`.hpp` | 1,075 个 | 绝大部分是 vendored ACL/simde/spdlog |
| `*.vcxproj` | **8** | 5 ACL + Ooz + HLSL + FBXNative |
| `*.csproj` | **7** | |
| `*.slnx` 项目节点 | **15** | 见 §2 |

### 1.2 各项目体积（工作树，不含 `.git`）

| 目录 | 文件数 | 体积 | 说明 |
|---|---:|---:|---|
| `AnimeStudio.ACL` | 899 | **14.58 MB** | 5 份 ACL 树拷贝（DB 3.64 / ZZZV2 4.62 / ZZZ 3.47 / SR 2.06 / MHY 0.78） |
| `AnimeStudio.Libraries` | 9 | **7.65 MB** | 纯预编译 DLL |
| `AnimeStudio.Ooz` | 286 | **4.60 MB** | 其中 simde ≈ 100.8k 行、libpoe ≈ 2.5k 行；自有代码 ≈ **15.0k 行** |
| `AnimeStudio`（核心托管） | 186 | 1.52 MB | 含 7zip / Brotli / LZ4 / YAML / Math 的 vendored 源码 |
| `AnimeStudio.Utility` | 45 | 0.86 MB | 其中 `FMOD Studio API/fmod.cs` 230 KB、`CSspv/SpirV.Core.Grammar.cs` 189 KB（生成代码） |
| `AnimeStudio.GUI` | 37 | 0.70 MB | |
| `AnimeStudio.HLSLDecompiler` | 20 | 0.44 MB | 11.6k 行，其中 `DecompileHLSL.cpp` 258 KB |
| `AnimeStudio.CLI` | 9 | 0.09 MB | |
| `AnimeStudio.FBXNative` | 19 | 0.05 MB | 1.3k 行，**最小但最难构建** |
| `AnimeStudio.FBXWrapper` | 7 | 0.04 MB | 托管包装 |
| `AnimeStudio.Patcher` / `AnimeStudio.PInvoke` | 2 / 2 | 0.01 MB | |
| **合计** | **1,533** | **30.59 MB** | |

> 体积结论：「源码 + 二进制同一个仓库」模式下，**26 % 的仓内容是 9 个 DLL，48 % 是 5 份重复的 ACL 头文件树**。真正的自有原生代码不到 30k 行。

---

## 2. 项目矩阵

`AnimeStudio.slnx` 只有 32 行，15 个项目。配置文件层面声明了 3 个平台：`Any CPU / x64 / x86`（`AnimeStudio.slnx:2-6`）——但 8 个 vcxproj **只定义了 `Debug|x64` 和 `Release|x64`**（例：`AnimeStudio.Ooz.vcxproj:4-11`）。选 x86 直接无配置可用。

| # | 项目 | 语言 / 工具集 | 输出 DLL | 托管互操作机制 | 是否随 solution 构建 | 为什么 |
|---|---|---|---|---|---|---|
| 1 | `AnimeStudio.Ooz` | C++17，v143，`DynamicLibrary` | `AnimeStudio.Ooz.dll` (198 KB) | `extern "C"` + `DllImport`（**StdCall**） | **否**：`<Build Project="false"/>`（`slnx:20-22`） | 上游 ooz 工程的 MSVC 裁剪版；需 `/MT` + simde；且另有一套 CMake 构建（见 §4.1） |
| 2 | `AnimeStudio.FBXNative` | C++17，v143，`DynamicLibrary` | `AnimeStudio.FBXNative.dll` (4.55 MB) | `extern "C"` `AS_API`（`__stdcall`）+ `DllImport` | **否**：`<Build Project="false"/>`（`slnx:15-17`） | 依赖本机安装的 Autodesk FBX SDK 2020.3.7，CI 机器上没有 |
| 3 | `AnimeStudio.HLSLDecompiler` | C++，v143 | `AnimeStudio.HLSLDecompiler.dll` (506 KB) | `extern "C"` + `__cdecl` + `DllImport` | **是**（`slnx:19`，无 `Build=false`） | 只依赖系统 `d3dcompiler_47.dll`，可无外部 SDK 构建 |
| 4 | `AnimeStudio.ACL.DB` | C++14，v143 | `AnimeStudio.ACL.DB.dll` (144 KB) | `AS_API`（`__stdcall`）+ `DllImport`(Cdecl) | **是**（`slnx:8`） | 头文件-only ACL，自带 `acl/` 树 |
| 5 | `AnimeStudio.ACL.MHY` | C++14，v143 | `AnimeStudio.ACL.MHY.dll` (118 KB) | 同上 | **是**（`slnx:9`） | ACL 1.x |
| 6 | `AnimeStudio.ACL.SR` | C++14，v143 | `AnimeStudio.ACL.SR.dll` (116 KB) | 同上 | **是**（`slnx:10`） | ACL 1.x，另需 16B 对齐输入 |
| 7 | `AnimeStudio.ACL.ZZZ` | C++14，v143 | `AnimeStudio.ACL.ZZZ.dll` (140 KB) | 同上 | **是**（`slnx:11`） | ACL 2.0.99 |
| 8 | `AnimeStudio.ACL.ZZZV2` | C++**20**，v143 | `AnimeStudio.ACL.ZZZV2.dll` (436 KB) | `extern "C"` `ACLNATIVE_API` + `DllImport` | **是**（`slnx:12`） | ACL 2.1.0 + vendored **spdlog**（`ACLLibrary.h:20`） |
| 9 | `AnimeStudio.FBXWrapper` | C# lib | `AnimeStudio.FBXWrapper.dll` | P/Invoke + `DllLoader` | 是（`slnx:18`） | 托管包装 |
| 10 | `AnimeStudio.PInvoke` | C# lib | `AnimeStudio.PInvoke.dll` | 只提供 `DllLoader` | 是（`slnx:26`） | 显式 `LoadLibraryEx` 预加载，让 `DllImport` 后面能按名解析 |
| 11 | `AnimeStudio` | C# lib | `AnimeStudio.dll` | 持有 `OodleHelper`、`CNKeys.json` | 是（`slnx:31`） | 核心资产管线 |
| 12 | `AnimeStudio.Utility` | C# lib | `AnimeStudio.Utility.dll` | 持有 ACL/HLSL/FMOD 的 `DllImport` | 是（`slnx:30`） | |
| 13 | `AnimeStudio.CLI` | C# exe | `AnimeStudio.CLI.exe` | — | 是（`slnx:28`） | |
| 14 | `AnimeStudio.GUI` | C# WinExe | `AnimeStudio.GUI.exe` | — | 是（`slnx:29`） | |
| 15 | `AnimeStudio.Patcher` | C# exe | `AnimeStudio.Patcher.exe` | — | 是（`slnx:25`） | 发布期改写 apphost |

> **文档漂移**：`README.md:57` 写「They are all x64 only and **excluded from the solution build**」，但 `slnx` 只对 `FBXNative`（`:15-17`）和 `Ooz`（`:20-22`）设了 `Build Project="false"`；5 个 ACL 与 `HLSLDecompiler` 其实**在 solution 构建里**。要复现「当前发布二进制」，正确做法是只把这两个排除项当作「必须手动构建」，其余走 solution 也行——但**谁都不能保证和提交的 DLL 一致**（§7 R1）。

---

## 3. 互操作机制（逐层拆解）

### 3.1 原生导出宏（两套几乎逐字相同）

```
AnimeStudio.ACL/AnimeStudio.ACL.DB/dllmain.h:9-56     #define AS_API(ret_type) _EXTERN_C_STMT _AS_EXPORT ret_type _AS_CALL
  :10  #ifdef _AS_DLL      → 由 vcxproj 的 PreprocessorDefinitions 打开
  :14  __declspec(dllexport)
  :36  #define _EXTERN_C_STMT extern "C"
  :46  #define _AS_CALL __stdcall        ← x64 上无害，x86 上是 ABI 杀手
AnimeStudio.FBXNative/dllexport.h:9-56                  同上（逐字复制）
```
- `_AS_DLL` 由各 vcxproj 注入：`ACLDB_EXPORTS` / `ACLMHY_EXPORTS` / `ACLSR_EXPORTS` / `ACLZZZ_EXPORTS` / `ACLZZZV2_EXPORTS` + `_AS_DLL`（例：`AnimeStudio.ACL.DB.vcxproj:61,83`）。
- **没有任何 `.def` 文件**；所有 DLL 链接时加 `/NOIMPLIB /NOEXP`（`AnimeStudio.Ooz.vcxproj:75,99`、`AnimeStudio.FBXNative.vcxproj:81,102`）→ **不生成 .lib**，因此这些库**只能被 P/Invoke 消费，不能被 C++ 侧链接**。这是有意的单向设计。
- 符号名未修饰（x64 无 stdcall 名字修饰），所以 `DllImport` 用裸名即可；`ZZZV2` 需要 `EntryPoint` 显式指定（见 §3.3）。

### 3.2 C# 侧固定三件套

```
① 静态构造函数 PreloadDll       → AnimeStudio.PInvoke/DllLoader.cs:11-25
② 函数级 [DllImport(...)]        → 分散在各包装类
③ 手写 Marshal + 指针/对齐处理   → ACL.cs、ShaderConverter.cs、OodleHelper.cs
```
`DllLoader` 的行为决定了整个分发布局：

| 代码 | 行为 | 后果 |
|---|---|---|
| `DllLoader.cs:29-35` | 若 exe 同级存在 `bin/`，改用 `bin/` | **硬绑定 `build.ps1` 的 `-d bin` 布局** |
| `DllLoader.cs:42-44` | `archSpecific=true` → 再拼 `x64` 或 `x86` 子目录 | 只有 `ACLLibs.ACL` 用默认 `true`（`ACL.cs:19`），其余全部显式 `false` |
| `DllLoader.cs:56` | `LoadLibraryEx(path, 0, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS \| LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR)` | 预加载后，后续 `DllImport("AnimeStudio.ACL.DB")` 按已加载模块名命中 |
| `DllLoader.cs:63` | 失败抛 `DllNotFoundException(Win32Exception)` | 静态构造函数内抛 → 外层表现为 `TypeInitializationException`，**错误信息会指向类型而非 DLL** |

### 3.3 逐库「原生入口 ↔ C# 声明」对照

| DLL | 原生入口（精确签名） | 出处 | C# 声明 | 出处 |
|---|---|---|---|---|
| `AnimeStudio.Ooz` | `int Ooz_Decompress(const uint8_t* src, int src_len, uint8_t* dst, size_t dst_size, int,int,int, uint8_t*, size_t, void*, void*, void*, size_t, int)`（后 10 个参数被实现**忽略**，直接转 `Kraken_Decompress(src,src_len,dst,dst_size)`） | `kraken.cpp:4188-4193`（导出）/ `:4191`（转调） | `static extern int Ooz_Decompress(ref byte, int, ref byte, int, int,int,int, IntPtr,int, IntPtr,IntPtr,IntPtr,IntPtr,int)`, `CallingConvention.StdCall` | `AnimeStudio/OodleHelper.cs:8-9`；调用处 `:16` 传 `fuzzSafe=1, threadPhase=3` |
| `AnimeStudio.HLSLDecompiler` | `int __cdecl Decompile(const void* byteCode, int size, char** shaderText, int* shaderTextSize)`；返回枚举 `0..4` | `api.cpp:29-33`；状态码 `:20-27` | `static extern int Decompile(byte[], int, out IntPtr, out int)`, `Cdecl` | `AnimeStudio.Utility/ShaderConverter.cs:1196-1197` |
| `AnimeStudio.ACL.MHY` / `.SR` | `void __stdcall DecompressClip(void* data, DecompressedClip& clip)` + `void Dispose(DecompressedClip&)` | `MHY/dllmain.cpp:71,107`；`SR/dllmain.cpp:86,125` | `static extern void DecompressClip(byte[]/nint, ref DecompressedClip)` | `ACL.cs:37-41`（MHY，`byte[]`）、`ACL.cs:82-86`（SR，`nint`） |
| `AnimeStudio.ACL.DB` | `void __stdcall DecompressTracks(void* data, void* database, void* streamer, decompressed_clip& out)` | `DB/dllmain.cpp:109,222` | `static extern void DecompressTracks(nint, nint, nint, ref DecompressedClip)` | `ACL.cs:229-233` |
| `AnimeStudio.ACL.ZZZ` | **同名** `DecompressTracks(void*, void*, void*, decompressed_clip&)`（另一份 ACL 树） | `ZZZ/dllmain.cpp:109,222` | 同签名 + `EntryPoint="DecompressTracks"` 显式指定 | `ACL.cs:235-239` |
| `AnimeStudio.ACL.ZZZV2` | `extern "C" void DecompressTracksZZZ(const compressed_tracks*, const compressed_tracks* scalar, const compressed_database*, const uint8_t* bulk_data, DecompressedClip*)` | `ACL.ZZZV2/ACLLibrary.h:45` | `EntryPoint="DecompressTracksZZZ"`，4 个 `nint` + `ref` | `ACL.cs:241-244` |
| `AnimeStudio.FBXNative` | **~60 个** `AS_API(...)` 导出（Context/Mesh/Skin/Anim/Morph/Texture 六组） | `AnimeStudio.FBXNative/api.h:22-161` | 63 个 `[DllImport(FbxDll.DllName)]`（`FbxDll.cs:6` = `"AnimeStudio.FBXNative"`） | `FbxExporterContext.PInvoke.cs:9..217`、`Fbx.PInvoke.cs:9-13` |
| `AnimeStudio.FMOD` | FMOD Ex/Studio 1.7.16 全量 C API（`fmod64.dll` 改名） | 二进制（无源码） | `public const string dll = "AnimeStudio.FMOD"`，`VERSION.number = 0x00010716` | `AnimeStudio.Utility/FMOD Studio API/fmod.cs:20-21` |

**跨边界结构体必须逐字节对齐**（两边都是 4 个 `ptr/int` 交替，x64 下 24 字节）：

```c
// AnimeStudio.ACL.*/dllmain.cpp:99-105（DB/ZZZ）   :61-67（MHY）
struct decompressed_clip { float* values; int values_count; float* times; int times_count; };
```
```csharp
// AnimeStudio.Utility/ACL/ACL.cs:7-13
public struct DecompressedClip { public IntPtr Values; public int ValuesCount; public IntPtr Times; public int TimesCount; }
```

**内存所有权**：`values`/`times` 由 DLL 内部的 `ansi_allocator` 分配（`DB/dllmain.cpp:182-183`），必须由 DLL 自己的 `Dispose` 释放（`:222-225`）——**不能用 `Marshal.FreeHGlobal`**（两套 CRT，库是 `/MT`）。唯一例外是 HLSL：它用 `LocalAlloc`（`api.cpp:74`），所以 C# 侧 `Marshal.FreeHGlobal`（`ShaderConverter.cs:1191`）是对的，`api.cpp:72-73` 有注释专门说明这点。

**对齐要求**：ACL 的 `compressed_tracks` 需要 16 字节对齐，C# 手工做指针对齐——`ACL.cs:56-58`（SR）、`:109-112`（`AlignAndCopyData`）、`:143-149`（DB）。新增解码器必须复制这个模式。

---

## 4. 逐库详解

### 4.1 `AnimeStudio.Ooz` — Oodle Kraken / Mermaid / Leviathan 解压

- **覆盖格式**：`.bun`（PoE bundle）、GGPK（PoE 归档）、以及 Oodle 的 Kraken(8) / Mermaid(9) / Selkie(11) / Leviathan(13)（`:30-35` 枚举在 `compress.h`）。
- **两套构建并存**：
  - MSVC：`AnimeStudio.Ooz.vcxproj` —— 只编 12 个 `.cpp`（`:119-129`），**不包含** `bun.cpp`、`ggpk_vfs.cpp`、`bun_extract_file.cpp`、`fnv/murmur/path_rep/utf/util`，也**不包含 libpoe**。产物只导出 `Ooz_Decompress` 一个符号 → 这是给 AnimeStudio 用的最小解压器。
  - CMake：`AnimeStudio.Ooz/CMakeLists.txt` —— 完整上游工程，建 `libooz`(SHARED, `:37`)、`ooz`(exe, `:51`)、`ooz-validate`(需 libsodium, `:56-62`)、`libbun`(SHARED, `:81`)、`libpoe`(STATIC, `libpoe/CMakeLists.txt:1`)、`bun_extract_file`(exe, `:92`)。UNIX 下还要 `pkg-config libsodium` + `unistring`（`:44-49,73-79`）。
  - 两者输出名不一致：CMake 的 `libooz` 产出 `ooz.dll`，而 `scripts/decompress.py:21` 期望 `oozlib.dll`。**这三套命名（CMake / vcxproj TargetName / Python 脚本）互不一致**，说明 CMake 分支在本仓库里是「留着备查」而非可用的构建路径。
- **simde（vendored，~100.8k 行）**：不是装饰。`compr_entropy.cpp:298-337`、`compr_kraken.cpp:557-670`、`compr_leviathan.cpp:665-1323`、`compr_mermaid.cpp` 全都用 `simde__m128i` / `simde_mm_*` 写 SSE2 代码；vcxproj 通过 `AdditionalIncludeDirectories=./simde`（`:68,91`）+ `EnableEnhancedInstructionSet=StreamingSIMDExtensions2`（`:86`）启用。**结论：换编译器/换架构时 simde 是唯一的移植层**，删不得。
- **libpoe（vendored，~2.5k 行）**：`poe/format/ggpk.{cpp,hpp}`、`poe/util/{murmur2,sha256,utf,random_access_file,install_location}`，外加 `mio` 单头 mmap 库。只被 CMake 的 `bun_extract_file` 使用；**MSVC 构建完全不引用** → 对二次开发是纯体积负担（2.5k 行 + 61 KB 单头）。
- **构建怪癖**：`ToolsVersion="15.0"`（`:2`）但工具集 v143；`SubSystem=Console` 却产出 DLL（`:72,94`）；`WindowsTargetPlatformVersion=10.0`（`:17`）不钉具体 SDK 版本；`ClInclude` 里列了**磁盘上不存在的 `moo.h`**（`:113`，已核实 `Test-Path` = false）——MSBuild 容忍，但 VS 里会显示为缺失文件。
- **许可证警告**：`kraken.cpp:1-17` 是 **GPLv3** 头（Copyright 2016 Powzix），而仓库根 `LICENSE` 是 MIT、`README.md:127` 声称「Everything else is MIT or similar」。`AnimeStudio.Ooz` 其余 40 个文件**没有任何 license 头**（已逐文件核对）。对 fork 的商业化是实质风险。

### 4.2 `AnimeStudio.ACL.*` — 五个 ACL 变体

Unity 的 Animation Compression Library 在多年间换了 3 次磁盘格式，所以这里**把 5 个版本的 ACL 源码树各自拷一份**，各自编成一个小 DLL，用「哪个游戏的 clip 用哪个 DLL」来分派。

| 项目 | ACL 版本 | 代码规模 | 算法 API 世代 | 导出 | 分派条件（C#） |
|---|---|---|---|---|---|
| `ACL.MHY` | 1.x（无 `acl/version.h`） | 74 文件 / 0.78 MB | `acl/algorithm/uniformly_sampled/decoder.h` + `ACLWriter : OutputWriter`（`dllmain.cpp:32-59`） | `DecompressClip` | `MHYACLClip` 且 **非** ZZZ（`ACLExtensions.cs:32`） |
| `ACL.SR` | 1.x | 126 文件 / 2.06 MB | 同上（`SR/dllmain.cpp:86`） | `DecompressClip` | `game.Type.IsSRGroup()`（`ACLExtensions.cs:10-13`） |
| `ACL.ZZZ` | **2.0.99**（`acl/version.h:33-35`） | 175 文件 / 3.47 MB | ACL 2.0 `compressed_tracks` + `database_context` | `DecompressTracks` | `MHYACLClip` 且 `IsZZZ()`（`ACLExtensions.cs:28`） |
| `ACL.DB` | **2.1.99**（`acl/version.h:33-35`） | 206 文件 / 3.64 MB | ACL 2.1 + `debug_database_streamer` | `DecompressTracks` | `GIACLClip`（`ACLExtensions.cs:20`） |
| `ACL.ZZZV2` | **2.1.0**（`acl/version.h:33-35`） | 316 文件 / **4.62 MB**（含 spdlog + rtm） | ACL 2.1 双流（transform + scalar） | `DecompressTracksZZZ` | `ZZZACLClip`（`ACLExtensions.cs:23`） |

**关键实现细节（DB/ZZZ 共用同一份 `dllmain.cpp`，仅 ACL 版本不同）**：

1. 一个 clip 里可能**串联**两段数据：先 `track_type8::qvvf`（transform），偏移 `align_to(get_size(), alignof(compressed_tracks))` 之后可能还有一段 scalar（`DB/dllmain.cpp:141-178`）。
2. 输出布局是**交错 qvvf + 尾部 float1**：`values[0..]` 每 sample 占 `num_tracks*0xA`，每 track = quat(4) + translation(3) + scale(3)；float1 段接在后面（`acl_writer::calculate_qvvf_index` / `calculate_float1f_size`，`:82-86`）。C# 侧在 `ModelExporter`/`AnimationClipConverter` 里按这个 stride 解析。
3. **database bulk data 的偏移是逆向出来的魔数**：`TryGetDatabaseBulkOffset`（`ACL.cs:200-224`）手工解析序列化头——tag `0xAC11DB01` @8、`misc_packed` @14 bit0 = inline、`numChunks0` @16、`numChunks1` @20、`numClips` @28、`mediumSize` @40、`lowSize` @44，然后 `bulkOffset = 64 + 8*(numChunks0+numChunks1+numClips)`（`:218`）。原生侧再把 low bulk 放在 `align_to(medium_size,4)` 之后（`DB/dllmain.cpp:124-127`）。**任何 ACL 版本微调都会静默地把动画解成零/粗量化噪声**，`:151-156` 的注释明确记录了「streamer 传 NULL 会得到 garbage」这个坑。
4. `ZZZV2` 的 C# 调用把 `m_databaseData` **同时当作 `database` 和 `bulk_data` 两个参数**传入（`ACLExtensions.cs:23` 的 4 个实参里第 3、4 个都是 `m_databaseData`/`m_DatabaseData`）——语义上挺可疑，属于「先跑通再优化」的痕迹。
5. `ZZZV2` 的 `DecompressedClip` 是**另一份定义**（`ACLLibrary.h:22-40`），带 `Reset()`/`Dispose()` 成员函数，且 `Dispose()` 用 `delete Values`（**new[] 配 delete，UB**，`:35-38`）；不过 C# 实际调用的是导出函数 `Dispose(DecompressedClip*)`（`ACLLibrary.h:46`），该成员函数似乎是死代码。`ACLLibrary.h:42-43` 还留着未启用的 `static_assert(sizeof(DecompressedClip)==32)`。

**唯一真正在用的路径（按 `ACLExtensions.cs` 反推）**：`SR → ACL.SR`；`GI → ACL.DB`；`ZZZ → ACL.ZZZV2`；`MHY 且 ZZZ → ACL.ZZZ`；`MHY 非 ZZZ → ACL.MHY`。最后这条走的是 `archSpecific:true`，见 §7 R8。

### 4.3 `AnimeStudio.HLSLDecompiler` — DXBC → HLSL

- **来源**：3Dmigoto 1.4.9（`version.h:30-32,42`：`VERSION_MAJOR 1 / MINOR 4 / REVISION 9`，`VER_PRODUCTNAME_STR "3Dmigoto"`）。核心是 `DecompileHLSL.cpp`（258 KB）+ `decode.cpp` / `decodeDX9.cpp` / `reflect.cpp`，继承自 HLSLcc 家族（`include/hlslcc.h`、`internal_includes/tokens.h`）。
- **包装极薄**：只有 `api.cpp`（84 行）是本仓库新增的，注释明确写了边界（`api.cpp:1-6`）：先 `D3DDisassemble` 把字节码反汇编成文本，再把**字节码 + 反汇编文本一起**喂给 `DecompileBinaryHLSL`（因为反汇编里才有 resource binding / signature / buffer layout）。
- **只暴露 1 个符号**：`Decompile`（`api.cpp:29`）。`LogInfo`/`LogDebug` 被 stub 掉（`api.cpp:16-17`）；`DecompilerSettings` 全默认、`ZeroOutput=false`（`:52-59`），即**主动关闭** ZRepair / BackProject / stereo 这些 3Dmigoto 的「改游戏」功能。
- **依赖**：`d3dcompiler.lib`（`HLSLDecompiler.vcxproj:73,97`）→ 运行时只导入系统 `D3DCOMPILER_47.dll`（已核实导入表）。**不需要任何 SDK，最容易重建**。
- **配套**：`AnimeStudio.Utility/ShaderConverter.cs` 里还自带一套 `Vortice.D3DCompiler`（`:17` PackageReference）做 DXBC 编译/反射，以及 `CSspv`(SPIR-V) + `Smolv` 解码器 + `SpirVShaderConverter.cs` 处理 Vulkan/GLSL 侧；两条路径并存。

### 4.4 `AnimeStudio.FBXNative` + `AnimeStudio.FBXWrapper` — FBX 导出

- 原生侧 1.3k 行，分 6 个 `*.cpp`（`FBXNative.vcxproj:106-111`），一个 `AsFbxContext` 覆盖 scene / mesh / skin / anim / morph / texture 六组操作，导出 **~60 个 `AS_API` 函数**（`api.h:22-161`）。
- 托管侧 3 个文件：`Fbx.cs`（静态构造 + `ExportOptions` record，`:11-15,56-69`）、`FbxExporterContext.cs`（24 KB 业务逻辑）、`FbxExporterContext.PInvoke.cs`（63 个 `DllImport`）。**没有用 CppSharp**，全部手写声明——我逐个核对了 63 个 entrypoint 名字，**全部能在 `AnimeStudio.FBXNative.dll` 里找到对应字符串，无拼写漂移**。
- **FBX 版本映射是硬编码数组**：`FBXVersion[] = { FBX_2010_00_COMPATIBLE … FBX_2016_00_COMPATIBLE }`（`api.cpp:64-72`），`versionIndex==0` 走 `pFileFormat=3/4`（二进制/ASCII），否则 `SetFileExportVersion(FBXVersion[versionIndex])`（`:120-137`）。**越界索引没有校验**（`versionIndex` 来自 CLI/GUI 参数）→ 潜在 OOB 读。
- **强耦合的 SDK 路径**（4 处，fork 必改）：
  ```
  AnimeStudio.FBXNative.vcxproj:71   C:\Program Files\Autodesk\FBX\FBX SDK\2020.3.7\include          (Debug)
  AnimeStudio.FBXNative.vcxproj:79   ...\2020.3.7\lib\x64\debug                                        (Debug lib)
  AnimeStudio.FBXNative.vcxproj:92   ...\2020.3.7\include                                              (Release)
  AnimeStudio.FBXNative.vcxproj:101  ...\2020.3.7\lib\x64\release                                      (Release lib)
  ```
  没有 `$(FBX_SDK_DIR)` 之类的环境变量回退，也没有 `Directory.Build.props` 覆盖点。
- **静态链接**：`libfbxsdk-mt.lib;libxml2-mt.lib;zlib-mt.lib`（`:78,100`）+ `/MT`（`:72,93`）+ `IgnoreSpecificDefaultLibraries=LIBCMT`（Debug 下，`:80`）→ 产物 4.55 MB 自包含，运行时**不需要 `libfbxsdk.dll`**（`FbxDll.FbxsdkDllName = "libfbxsdk"` 在 `FbxDll.cs:7` 是**未被使用的遗留常量**，全仓无 `PreloadDll("libfbxsdk")`）。
- **遗留 hack**：`<Target Name="AfterBuild">` 在 `Platform==Win32` 时递归 `MSBuild` 自身并强制 `Platform=x64`（`FBXNative.vcxproj:133-135`）。因为所有 `ProjectConfiguration` 都只有 x64，这个条件永不成立 → dead code，但会成为 VS「加载项」噪音。

### 4.5 `AnimeStudio.FMOD.dll` — 唯一「无源码」的原生二进制

| 事实 | 证据 |
|---|---|
| 是 `fmod64.dll` 改名 | PE 资源 `OriginalFilename = fmod64.dll`，`FileVersion 1.7.16 (build 78421)`，`CompanyName = Firelight Technologies` |
| C# 侧版本常量 | `fmod.cs:20` `number = 0x00010716`；`:21` `dll = "AnimeStudio.FMOD"` |
| 仓库内**没有**任何 FMOD 源码或 vcxproj | `git ls-files \| grep -i fmod` 只返回 4 条：`AnimeStudio.Libraries/AnimeStudio.FMOD.dll` + `AnimeStudio.Utility/FMOD Studio API/{fmod.cs,fmod_dsp.cs,fmod_errors.cs}`（纯 C# 包装） |
| 加载方式 | `fmod.cs:1591` `DllLoader.PreloadDll(VERSION.dll, false)` |
| 挂载位置 | **只有** `AnimeStudio.GUI.csproj:44-47` |
| 许可证 | `README.md:127` 说明是 Firelight 免费非商用许可 |

→ 一个 2016 年的 FMOD 1.7.16（≈9 年前）二进制，无源码、无构建脚本、只在 GUI 工程里接线。**Fork 想做商业分发必须先把这块换掉或买授权**（R10）。

### 4.6 `AnimeStudio.PInvoke` — 加载器

131 行，零依赖（`AnimeStudio.PInvoke.csproj` 无任何 `PackageReference`）。三个职责：
1. `PreloadDll(name, archSpecific)` —— Windows 走 `LoadLibraryExW` + `LOAD_LIBRARY_SEARCH_DEFAULT_DIRS|DLL_LOAD_DIR`（`:56,69-73`），POSIX 走 `dlopen(RTLD_NOW|RTLD_GLOBAL)`（`:100-101,117-122`）。**POSIX 分支是死代码**（`AnimeStudio.csproj:8` 强制 `win-x64`，且所有原生库都是 PE）。
2. `GetDirectedDllDirectory` —— 识别 `bin/` 并可选追加 `x64|x86`（`:27-45`）。
3. 抛 `DllNotFoundException` 时把 Win32 错误码转成可读消息（`:58-64`）。

### 4.7 `AnimeStudio.Utility` 的 vendored 托管依赖（非原生，但影响 fork 判断）

| 目录/文件 | 规模 | 性质 |
|---|---|---|
| `FMOD Studio API/fmod.cs` | 230 KB | Firelight 官方 C# 绑定，含 ~400 个 `[DllImport]` |
| `FMOD Studio API/fmod_dsp.cs` / `fmod_errors.cs` | 93 KB / 13 KB | 同上 |
| `CSspv/*` | `SpirV.Core.Grammar.cs` **189 KB 生成代码** + `Module.cs`/`Disassembler.cs`/`OperandType.cs` | SPIR-V 解析/反汇编（有独立 `LICENSE` 文件） |
| `Smolv/*` | `OpData.cs` 15 KB + `SmolvDecoder.cs` 11 KB | SMOL-V 压缩 SPIR-V 解码 |
| `Unity.CecilTools/*`、`Unity.SerializationLogic/*` | 2.4 KB–24 KB | 来自 UnityPy/AssetStudio 生态的 Cecil 工具 |
| `YAML/*`（Utility）+ `AnimeStudio/YAML/*`（核心） | 26 KB–29 KB + 一批 `Base/` `Utils/` | YamlDotNet 派生（无包引用，源码内联） |
| `AnimeStudio/7zip`、`Brotli`、`LZ4`、`Math` | 45 KB `LzmaEncoder.cs`、243 KB `Brotli/Dictionary.cs`、46 KB `Math/Half.cs` | 全部源码内联，无版本号、无 changelog |

→ 这些目录**没有上游版本号/commit 记录**，`.csproj` 也不引用对应 NuGet 包（`AnimeStudio.csproj:12-19` 只列了 9 个包，不含 7zip/Brotli/LZ4/YAML）。想升级只能整目录替换。

---

## 5. 预编译二进制交接（`AnimeStudio.Libraries`）

### 5.1 清单（全部实测）

| DLL | 大小 | PE | 导入表 | 生产者 | 消费方（csproj:line） | C# 加载点 |
|---|---:|---|---|---|---|---|
| `AnimeStudio.Ooz.dll` | 202,240 | x64 | KERNEL32 | `AnimeStudio.Ooz.vcxproj` | `AnimeStudio.csproj:29-32` | `OodleHelper.cs:8`（**无 PreloadDll**，靠 app 目录解析） |
| `AnimeStudio.FBXNative.dll` | 4,653,056 | x64 | KERNEL32, ADVAPI32 | `AnimeStudio.FBXNative.vcxproj` | `AnimeStudio.csproj:33-36` | `Fbx.cs:14` |
| `AnimeStudio.HLSLDecompiler.dll` | 518,656 | x64 | **D3DCOMPILER_47**, KERNEL32 | `AnimeStudio.HLSLDecompiler.vcxproj` | `AnimeStudio.Utility.csproj:27-30` | `ShaderConverter.cs:1180` |
| `AnimeStudio.ACL.DB.dll` | 147,456 | x64 | KERNEL32 | `AnimeStudio.ACL.DB.vcxproj` | `AnimeStudio.Utility.csproj:35-38` | `ACL.cs:99` |
| `AnimeStudio.ACL.ZZZ.dll` | 143,872 | x64 | KERNEL32 | `AnimeStudio.ACL.ZZZ.vcxproj` | `AnimeStudio.Utility.csproj:39-42` | `ACL.cs:100` |
| `AnimeStudio.ACL.SR.dll` | 118,272 | x64 | KERNEL32 | `AnimeStudio.ACL.SR.vcxproj` | `AnimeStudio.Utility.csproj:43-46` | `ACL.cs:52` |
| `AnimeStudio.ACL.MHY.dll` | 120,320 | x64 | KERNEL32 | `AnimeStudio.ACL.MHY.vcxproj` | `AnimeStudio.Utility.csproj:47-50` | `ACL.cs:19`（`archSpecific:true` ⚠） |
| `AnimeStudio.ACL.ZZZV2.dll` | 445,952 | x64 | KERNEL32 | `AnimeStudio.ACL.ZZZV2.vcxproj` | `AnimeStudio.Utility.csproj:51-54` | `ACL.cs:99-100`（复用 DB 的预加载） |
| `AnimeStudio.FMOD.dll` | 1,669,632 | x64 | WSOCK32, WINMM, MSACM32, KERNEL32, USER32, ADVAPI32, ole32 | **无**（外部二进制） | **`AnimeStudio.GUI.csproj:44-47`** | `fmod.cs:1591` |
| **合计** | **8,019,456 B ≈ 7.65 MB** | 全部 `machine=0x8664`、`PE32+ (0x20B)` | | | | |

> `ZZZV2` 在 `ACL.cs:99-100` 只预加载了 `DB` 和 `ZZZ`；`ZZZV2` 本身靠 `DllImport("AnimeStudio.ACL.ZZZV2")` 的默认解析（从 `bin/` 目录命中）——能用，但和其它库的「先 Preload 再 DllImport」风格不一致。

### 5.2 交接链路（从源码到用户桌面）

```
① 手工/VS 构建 8 个 vcxproj
      OutDir = $(SolutionDir)\AnimeStudio.Libraries\        ← 8 个 vcxproj 全部如此（Ooz:53/59, FBXNative:56/62,
                                                               ACL.DB:48/54, HLSL:47/53, ZZZV2:48/54 …）
      /NOIMPLIB /NOEXP → 只留 .dll，不产 .lib/.exp
                     ↓
② AnimeStudio.Libraries/*.dll  ← 提交进 git（9 个，7.65 MB）
                     ↓
③ 托管 csproj 的 ContentWithTargetPath（CopyToOutputDirectory=PreserveNewest）
      AnimeStudio.csproj:29-36        → Ooz, FBXNative
      AnimeStudio.Utility.csproj:27-55→ HLSL + 5×ACL
      AnimeStudio.GUI.csproj:44-47    → FMOD
                     ↓
④ 每个项目的 bin/Release/<tfm>/ 里，native dll 与托管 dll **平铺同级**
                     ↓
⑤ build.ps1 复制 CLI+GUI 两个输出目录 → dist/<tfm>/bin/，把 exe 移出 bin/
                     ↓
⑥ Patcher 把 exe 内嵌的 "AnimeStudio.CLI.dll" 改写成 "bin\AnimeStudio.CLI.dll"
                     ↓
⑦ 运行时：exe 在 dist/<tfm>/ → 加载 bin\*.dll → bin/ 内的 native dll 被
      DllLoader.PreloadDll 按 bin/ 绝对路径 LoadLibraryEx 预加载 → 后续 DllImport 命中
```

---

## 6. 构建与发布流水线

### 6.1 `build.ps1` 精确步骤（107 行，`$ErrorActionPreference='Stop'`）

| # | 行 | 动作 | 失败处理 |
|---|---|---|---|
| 1 | `:4-7` | `dotnet build AnimeStudio.Patcher -c Release -f net10.0`，然后校验 `AnimeStudio.Patcher\bin\Release\net10.0\AnimeStudio.Patcher.exe` 存在 | `throw`（`:5`）、`throw`（`:7`） |
| 2 | `:40` | `foreach ($tfm in 'net9.0-windows','net10.0-windows')` ← **TFM 矩阵硬编码在脚本里** | — |
| 3 | `:54-55` | **先删除** `bin/<cfg>/<tfm>/AnimeStudio.{CLI,GUI}.exe`，强制 apphost 重新生成 | 注释 `:52-53` 解释原因：避免对已 patch 的 exe 二次 patch 导致 `bin\bin\` 叠加 |
| 4 | `:58-61` | `dotnet build AnimeStudio.CLI -c Release -f $tfm` → `& $patcher $cliExe -d bin` | `throw`（`:59,61`） |
| 5 | `:63-66` | 同上，GUI | `throw`（`:64,66`） |
| 6 | `:69-70` | `Reset-Dir dist/<tfm>`；`New-Item dist/<tfm>/bin` | `Reset-Dir`（`:9-20`）目录被占用时退化为「清空内容」 |
| 7 | `:73-74` | `Copy-Item <cliOut>/* → dist/<tfm>/bin`；GUI 同（**后拷的覆盖先拷的**，两个项目共享一个 bin） | — |
| 8 | `:77-84` | 把 `AnimeStudio.GUI.exe` / `AnimeStudio.CLI.exe` 从 `bin/` `Move-Item` 到 `dist/<tfm>/`；找不到就 `throw "Expected ... after copy"` | `throw` |
| 9 | `:85-89` | `bin/LICENSE` → 上一级；否则从仓库根 `LICENSE` 复制 | — |
| 10 | `:91` | `Remove-EmptyDirectories`（`:22-38`，循环删空目录，卡住则 `Write-Warning` 并 break） | 软失败 |
| 11 | `:93-104` | **二进制自检**：读 exe 原始字节 → UTF8 解码 → 断言不含 `bin\\bin\\`；再断言含 `bin\<dllName>`（否则 `Write-Warning`） | `throw`（`:98`） |
| 12 | `:106` | `Write-Host "Built $outputDir" -ForegroundColor Green` | — |

**脚本不做的事**（重要）：
- **不构建任何原生项目**（没有 `msbuild` 调用）。
- **不跑测试**（仓库无测试项目）。
- **不签名、不压缩、不打 tag**（打包交给 CI）。

### 6.2 为什么是 `-d bin`（exe-patching 技巧）

`AnimeStudio.Patcher` 直接改 **apphost 二进制里内嵌的相对 dll 路径字符串**（源自 dnSpy 的 AppHostPatcher，`Program.cs:1` 注明出处）。

```
用法（Program.cs:13-17）：  apphostpatcher <apphostexe> -d <newsubdir>
                             例：AnimeStudio.CLI.exe -d bin
语义（Program.cs:36-41）：    orig = Path.GetFileName(ChangeExtension(exe,".dll"))   // "AnimeStudio.CLI.dll"
                              new  = "bin" + "\\" + orig                            // "bin\AnimeStudio.CLI.dll"
改写（Program.cs:106-117）：  定位 orig 起始偏移 → 先把 maxPathBytes(=1024, :19) 范围整体清零
                              → 再写入 newPathBytes
```
**为什么必须这么做**（不是为了炫技）：
1. 目标形态是「**单 exe 启动器 + 一个 `bin/` 目录**」，用户桌面只看到 2 个 exe 和 1 个文件夹，而不是 200 多个散落文件。
2. CLI 与 GUI 要**共享同一个 `bin/`**（`build.ps1:73-74` 拷到同一目录），共享 `AnimeStudio.dll`/`AnimeStudio.Utility.dll`/9 个 native dll，发布体积几乎减半。
3. 一旦这么做，`DllLoader` 必须知道去 `bin/` 找库（`DllLoader.cs:32-35` 的 `Path.Exists(localDir/bin)` 判断），形成**双向耦合**：改布局必须同时改 `build.ps1` 和 `DllLoader`。
4. 历史 bug 的化石：早期 patcher 每次运行都在路径前再叠一个 `bin\`，所以现在有**三层防护**——patcher 自身的幂等检查（`Program.cs:86-91`：若已含目标路径直接返回 0）、`FindEmbeddedDllPathStart` 的堆叠恢复（`:98-105,161-199`）、`IsPathStart` 拒绝后缀匹配（`:147-155`），外加 `build.ps1:54-55` 的删 exe 和 `:93-104` 的字节断言。

### 6.3 CI 矩阵（`.github/workflows/build.yml`，56 行）

| 项 | 值 | 行 |
|---|---|---|
| 触发 | push 到 `master`（忽略 `LICENSE`/`README.md`/`build.yml`/`.gitignore`/`.gitattributes` 变更）+ `workflow_dispatch` | `:3-13` |
| Runner | **`windows-latest` 单平台**，无矩阵展开 | `:16` |
| Checkout | `actions/checkout@v3` | `:19` |
| MSBuild | `microsoft/setup-msbuild@v1.1` ← **后续无任何 msbuild 调用，纯死代码** | `:20` |
| .NET | `actions/setup-dotnet@v3`，`9.0.x` + `10.0.x` | `:21-26` |
| Restore | `nuget restore` ← 仓库根**只有 `.slnx`，无 `.sln`**；`nuget.exe` 不认 `.slnx` → 该步骤要么报错要么空跑（`dotnet build` 本身会隐式 restore，所以不致失败） | `:29` |
| 构建 | `pwsh .\build.ps1` | `:37` |
| 产物整理 | `mkdir output-net9\AnimeStudio-net9-<sha>` + `xcopy .\dist\net9.0-windows\*` （net10 同构） | `:41-44` |
| 上传 | `actions/upload-artifact@v4`，两个 artifact：`AnimeStudio-net9` / `AnimeStudio-net10` | `:46-56` |
| 分发 | 用 nightly.link 直链：`README.md:15-16` → `nightly.link/Escartem/AnimeStudio/workflows/build/master/AnimeStudio-net{9,10}.zip` | — |

**结论：CI 只证明「托管代码能编译 + 打包」，不证明「原生库与源码一致」**——它连 `slnx` 都不碰。这是最大的可重现性缺口。

### 6.4 TFM / 平台矩阵

| 项目 | TargetFrameworks | RID | 关键属性 |
|---|---|---|---|
| `AnimeStudio` | `net9.0;net10.0` | `win-x64` | `SelfContained=false`（`csproj:7`） |
| `AnimeStudio.Utility` | `net9.0;net10.0` | — | `AllowUnsafeBlocks=true`（`:10`） |
| `AnimeStudio.FBXWrapper` | `net9.0;net10.0` | — | `AllowUnsafeBlocks=true`（`:5`） |
| `AnimeStudio.PInvoke` | `net9.0;net10.0` | — | `DebugType=embedded`（`:9`） |
| `AnimeStudio.CLI` | **`net9.0-windows;net10.0-windows`** | `win-x64` | `AppendRuntimeIdentifierToOutputPath=false`（`:8`） |
| `AnimeStudio.GUI` | **`net9.0-windows;net10.0-windows`** | `win-x64` | `UseWindowsForms=true`（`:6`） |
| `AnimeStudio.Patcher` | **`net10.0`**（单一） | — | 构建脚本里也硬编码 `-f net10.0`（`build.ps1:4,6`） |

→ 4 种不同的 TFM 组合；`Patcher` 的 `net10.0` 与 `build.ps1` 的字符串是**双重硬编码**，任何一方改动都会漏。

**Windows-x64-only 的三层锁定**：
1. 3 个 csproj 写死 `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`（`AnimeStudio.csproj:8`、`CLI:7`、`GUI:10`）；
2. 8 个 vcxproj 只有 `Debug|x64` / `Release|x64` 配置（`AnimeStudio.Ooz.vcxproj:4-11` 等）；
3. 9 个 DLL 的 PE machine 全为 `0x8664`。

`README.md:18` 明确写「Both builds are Windows x64 only」。**ARM64 Windows / Linux / macOS 移植需要同时改这 3 层 + 删掉 `DllLoader` 的 POSIX 分支或补 native 实现。**

### 6.5 FBX SDK 2020.3.7 的 pin

- 唯一 pin 手段就是**绝对路径字符串**，出现 4 次（`FBXNative.vcxproj:71,79,92,101`）。
- `README.md:44,53` 用散文强调：「You need Visual Studio 2022 with the C++ desktop workload and the .NET 9 and 10 SDKs」+「needs the Autodesk FBX SDK at **exactly version 2020.3.7** installed」。
- 没有 `Directory.Build.props` 变量、没有 `nuget`（FBX SDK 不在 NuGet 上）、没有 `.vsconfig`、没有 vcpkg。
- 后果：**fork 的第一天就必须先下载 Autodesk 的旧版本安装包（需 Autodesk 账号登录）**，否则只能跳过 FBX 导出功能，继续用提交的二进制。

---

## 7. 可重现性风险清单（fork 视角，按杀伤力排序）

| ID | 风险 | 证据 | 缓解 |
|---|---|---|---|
| **R1** | **9 个原生 DLL 是提交的二进制，且 CI 从不重建它们** → 源码与二进制可能已漂移，且无法验证 | 9 DLL = 7.65 MB 在 `git ls-files`；`build.ps1` 无 msbuild；`build.yml:37` 只跑 `build.ps1`；`README.md:48` 自认「native libraries are not built by that script」 | fork 首日就手工重建全部 8 个工程并对比 PE 大小/导入表；把 native 构建加进 CI |
| **R2** | **二进制无溯源信息**：无 commit hash、无构建时间戳；`.rc` 版本恒为 `1.0.0.1`（实测全部 ACL/HLSL = `1.0.0.1`，FBXNative `1.0.0.2`，Ooz `1.0.0.0`） | `Get-Item *.dll` 的 `VersionInfo` | 在 `.rc` 里注入 `$(GitCommit)`，或改用 `SourceLink`/构建清单 |
| **R3** | **FBX SDK 路径写死 4 处绝对路径**，无环境变量回退 | `FBXNative.vcxproj:71,79,92,101` | 改成 `$(FBXSDK_DIR)` 属性 + `Directory.Build.props` 默认值 |
| **R4** | **工具链未钉**：无 `global.json`、无 `.vsconfig`、`WindowsTargetPlatformVersion=10.0`（浮到本机最新 Win10 SDK）、`PlatformToolset=v143` 靠 VS 安装 | `AnimeStudio.Ooz.vcxproj:17,24,30`；`FBXNative.vcxproj:18,25,31` | 加 `global.json`（`sdk.version` + `rollForward: latestPatch`）、`vcpkg` 或 `.vsconfig` |
| **R5** | **原生工程依赖 `$(SolutionDir)` 才能正确输出**：单独 `msbuild AnimeStudio.Ooz.vcxproj` 时 `SolutionDir` 为空 → `OutDir` 变成 `\AnimeStudio.Libraries\`（当前盘根目录），几乎必然写入失败或污染盘根 | `AnimeStudio.Ooz.vcxproj:53,59`（`$(SolutionDir)\AnimeStudio.Libraries\`） | 构建命令必须带 `/p:SolutionDir=<repo>\`，或在 VS 里从 solution 构建 |
| **R6** | **CI 不校验原生、`nuget restore` 找不到 `.sln`、action 版本陈旧**（`checkout@v3`/`setup-msbuild@v1.1`/`setup-dotnet@v3` 均为 Node20 时代，将来会被强制升级） | `build.yml:19,20,22,29`；仓库只有 `AnimeStudio.slnx`（无 `.sln`） | 删 `nuget restore` 与 `setup-msbuild`，或改为 `dotnet restore AnimeStudio.slnx`；升级 action 到 v4/v5 |
| **R7** | **x86 / ARM64 在 ABI 层就被排除**：`__stdcall` + C# `Cdecl` 声明在 x86 上不兼容；vcxproj 无 x86 配置；PE 全 x64 | `dllmain.h:46` vs `ACL.cs:37`；`AnimeStudio.Ooz.vcxproj:4-11` | 若要 32 位：把两边的 calling convention 统一（建议全部 `__cdecl` / `Cdecl`）并新增 x86 配置 |
| **R8** | **`ACLLibs.ACL`（MHY 非-ZZZ 路径）去 `bin\x64\` 找 DLL，但打包流程从不创建 `x64/` 目录** → 极可能 `DllNotFoundException`（且发生在 static ctor，表现为 `TypeInitializationException`） | `ACL.cs:16-19`（`PreloadDll(DLL_NAME)` 默认 `archSpecific=true`）+ `DllLoader.cs:42-44`（拼 `x64`）+ `build.ps1:70-84`（只建 `bin/`，无 `x64/`）；调用方 `ACLExtensions.cs:32` 是活路径；对照其它 4 处都显式传 `archSpecific:false`（`ACL.cs:52,99,100`、`ShaderConverter.cs:1180`、`Fbx.cs:14`） | 改成 `PreloadDll(DLL_NAME, archSpecific: false)` —— **一行修复，建议 fork 首先做** |
| **R9** | **`AnimeStudio.FMOD.dll` 只挂在 GUI 的 csproj**，但 CLI 也调 `AudioClipConverter` → 单独 `dotnet build AnimeStudio.CLI` 后音频导出会 `DllNotFoundException`（打包后的 dist 里因为共享 `bin/` 而侥幸正常） | `GUI.csproj:44-47` vs `AnimeStudio.CLI/Exporter.cs:50` + `Utility/AudioClipConverter.cs:1` | 把该 `ContentWithTargetPath` 移到 `AnimeStudio.Utility.csproj`（和 HLSL/ACL 一致） |
| **R10** | **许可证混合**：仓库声明 MIT（`LICENSE:1-5`），但 `kraken.cpp:1-17` 是 **GPLv3** 头；FMOD 1.7.16 是**免费非商用**许可；FBX SDK 受 Autodesk 条款约束；`AnimeStudio.ACL/LICENSE.txt` 是 MIT（Razmoth/hrothgar/Escartem） | `kraken.cpp:1-17`；`README.md:127`；PE 资源 `CompanyName=Firelight Technologies` | 商用前替换 Ooz 实现 / 购买 FMOD / 遵守 FBX EULA；否则只能保持 MIT 开源分发 |
| **R11** | **大量 vendored 托管源码无版本记录**：7zip、Brotli（243 KB `Dictionary.cs`）、LZ4、YAML、CSspv（189 KB 生成代码）、Smolv、Unity.CecilTools —— 均无 `.csproj` 包引用、无上游 commit | `AnimeStudio/*`、`AnimeStudio.Utility/{CSspv,Smolv,Unity.*,YAML}` | 记录上游 tag/commit 到 `THIRD-PARTY.md`；或换成 NuGet 包 |
| **R12** | **版本号不一致**：`Directory.Build.props:2-6` 只给 `AnimeStudio`/`CLI`/`GUI` 打 `1.1.0`，`Utility`/`FBXWrapper`/`PInvoke` 仍是 `1.0.0`（各自 csproj 内） | `Directory.Build.props:3-5` vs `Utility.csproj:5`、`FBXWrapper.csproj:6`、`PInvoke.csproj:5` | 统一到 `Directory.Build.props`，去掉逐项目 `Version` |
| **R13** | **没有 NuGet 锁文件 / 集中包管理**（但**版本都精确钉住**，无浮动——这点是好消息）；也无 `nuget.config`，restore 依赖全局 feed 配置 | 全仓无 `global.json`/`Directory.Packages.props`/`packages.lock.json`/`nuget.config`（已用 `Get-ChildItem -Include` 核实为空）；18 个 `PackageReference` 全部 `Version="x.y.z"`，无 `*` / `[range]` | 加 `Directory.Packages.props` + `RestorePackagesWithLockFile` |
| **R14** | 零散工程卫生问题：`AnimeStudio.Ooz.vcxproj:113` 引用**不存在的 `moo.h`**；`:2` `ToolsVersion="15.0"` 与现代 VS 混用；`FBXNative.vcxproj:133-135` 的递归 `AfterBuild` 是 dead code；`FbxDll.cs:7` 的 `FbxsdkDllName` 从未使用；`DllLoader.cs:77-128` 的 POSIX 分支永不可达 | 见左 | 清理即可，不影响功能 |

---

## 8. 新增原生解码器的最小配方（给二次开发者）

以「新增一个 ACL 变体 / 新游戏格式」为例，照抄 `AnimeStudio.ACL.ZZZ` 是最省事的（同代 ACL、无外部 SDK）。

### Step 1 — 目录与工程

```
AnimeStudio.<Name>/
  AnimeStudio.<Name>.vcxproj        ← 从 AnimeStudio.ACL.ZZZ.vcxproj 复制
  dllmain.h                         ← 逐字复制（AS_API 宏）
  dllmain.cpp                       ← 你的入口
  acl/ 或 <vendor>/                 ← vendored 头文件树（可选）
```

`vcxproj` 必须改的 6 个字段（对齐现有约定）：

```xml
<PropertyGroup Label="Globals">
  <ProjectGuid>{新 GUID}</ProjectGuid>
  <RootNamespace>AnimeStudio.<Name></RootNamespace>
  <WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>
</PropertyGroup>
<PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'" Label="Configuration">
  <ConfigurationType>DynamicLibrary</ConfigurationType>
  <PlatformToolset>v143</PlatformToolset>
  <WholeProgramOptimization>true</WholeProgramOptimization>
  <CharacterSet>Unicode</CharacterSet>
</PropertyGroup>
<PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'">
  <TargetName>AnimeStudio.<Name></TargetName>
  <OutDir>$(SolutionDir)\AnimeStudio.Libraries\</OutDir>   <!-- ★ 交接路径，别改 -->
  <IntDir>obj\$(Platform)\$(Configuration)\</IntDir>
</PropertyGroup>
<ItemDefinitionGroup Condition="'$(Configuration)|$(Platform)'=='Release|x64'">
  <ClCompile>
    <PreprocessorDefinitions>WIN32;NDEBUG;_AS_DLL;<NAME>_EXPORTS;_WINDOWS;_USRDLL;%(PreprocessorDefinitions)</PreprocessorDefinitions>
    <LanguageStandard>stdcpp14</LanguageStandard>
    <RuntimeLibrary>MultiThreaded</RuntimeLibrary>          <!-- ★ /MT 静态 CRT，与现有 9 个 DLL 一致 -->
    <AdditionalIncludeDirectories>$(ProjectDir);%(AdditionalIncludeDirectories)</AdditionalIncludeDirectories>
  </ClCompile>
  <Link>
    <SubSystem>Console</SubSystem>
    <AdditionalOptions>/NOIMPLIB /NOEXP %(AdditionalOptions)</AdditionalOptions>  <!-- ★ 只产 dll -->
  </Link>
</ItemDefinitionGroup>
```
> 只保留 `Debug|x64` / `Release|x64` 两个 `ProjectConfiguration`（与现状一致，别加 x86/ARM64）。

### Step 2 — 注册进 `AnimeStudio.slnx`

```xml
<Folder Name="/ACL/">
  <Project Path="AnimeStudio.<Name>/AnimeStudio.<Name>.vcxproj" Id="{新 GUID}" />
</Folder>
```
> 若该工程有外部 SDK 依赖（如 FBX），仿照 `slnx:15-17` / `:20-22` 加 `<Build Project="false" />`，让 solution 构建跳过它。

### Step 3 — 原生入口（最小导出面：1 个干活 + 1 个释放）

```cpp
#include "dllmain.h"
#include <acl/core/ansi_allocator.h>
#include <acl/core/compressed_tracks.h>
#include <acl/decompression/decompress.h>

struct DecompressedClip { float* Values; int32_t ValuesCount; float* Times; int32_t TimesCount; };
static acl::ansi_allocator Allocator;

AS_API(void) DecompressTracks(void* data, DecompressedClip& out) {   // ★ 引用参数 = 指针，C# 用 ref
    // … 填充 out.Values / out.Times（用 Allocator 分配）
}

AS_API(void) Dispose(DecompressedClip& out) {                       // ★ 必须由本 DLL 释放
    acl::deallocate_type_array<float>(Allocator, out.Values, out.ValuesCount);
    acl::deallocate_type_array<float>(Allocator, out.Times,  out.TimesCount);
}
```
硬性约束（踩坑点）：
- **不要跨边界暴露 STL 类型**（`std::string`/`std::vector`）——现在全仓都遵守，一旦破例，托管侧无法正确封送。
- **不要返回 C++ 对象/异常**；错误用返回码（参考 `HLSLDecompiler/api.cpp:20-27` 的 `enum DecompileStatus`）。
- 需要 16B 对齐的数据由**调用方**对齐好再传进来（参考 `ACL.cs:56-58`）。
- 若返回字符串，用 `LocalAlloc`（`api.cpp:74`）而非 `new`，否则 C# 侧 `Marshal.FreeHGlobal` 会崩。

### Step 4 — `csproj` 里登记二进制（这是唯一让 DLL 进入发布包的地方）

在 **`AnimeStudio.Utility/AnimeStudio.Utility.csproj`**（如果只有 Utility 用到）追加：

```xml
<ItemGroup>
  <ContentWithTargetPath Include="..\AnimeStudio.Libraries\AnimeStudio.<Name>.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <TargetPath>AnimeStudio.<Name>.dll</TargetPath>   <!-- ★ 摊平到输出根，不带子目录 -->
  </ContentWithTargetPath>
</ItemGroup>
```
> 放 `AnimeStudio.Utility` 而不是 `AnimeStudio` 的关键：`AnimeStudio.csproj:29-36` 放的是「核心资产管线自己用的」（Ooz/FBXNative），`Utility.csproj:27-55` 放的是「导出器用的」（HLSL/ACL）。**别学 FMOD 放进 GUI.csproj**（R9）。

### Step 5 — C# 包装（固定三段式）

```csharp
using System;
using System.Runtime.InteropServices;
using AnimeStudio.PInvoke;

namespace ACLLibs
{
    public static class <Name>ACL
    {
        private const string DLL_NAME = "AnimeStudio.<Name>";   // ★ 不带 .dll，与其它 5 个一致

        static <Name>ACL() => DllLoader.PreloadDll(DLL_NAME, archSpecific: false);  // ★ 一定传 false

        public static void DecompressClip(byte[] data, out float[] values, out float[] times)
        {
            var clip = new DecompressedClip();
            var raw = Marshal.AllocHGlobal(data.Length + 16);
            var aligned = new IntPtr((raw.ToInt64() + 15) & ~15L);   // ★ 16B 对齐
            try { Marshal.Copy(data, 0, aligned, data.Length); DecompressTracks(aligned, ref clip); }
            finally { Marshal.FreeHGlobal(raw); }                    // ★ 只释放自己分配的 raw

            values = new float[clip.ValuesCount]; Marshal.Copy(clip.Values, values, 0, clip.ValuesCount);
            times  = new float[clip.TimesCount];  Marshal.Copy(clip.Times,  times,  0, clip.TimesCount);
            Dispose(ref clip);                                        // ★ 由 native 释放 clip.Values/Times
        }

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void DecompressTracks(nint data, ref DecompressedClip clip);
        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Dispose(ref DecompressedClip clip);
    }
}
```
最后在 `AnimeStudio.Utility/ACL/ACLExtensions.cs` 的 `switch` 里加一个 `case <NewClip> c: <Name>ACL.DecompressClip(...)` 分支（`ACLExtensions.cs:17-39`）。

### Step 6 — 验收清单（缺一不可）

1. `msbuild AnimeStudio.slnx /p:Configuration=Release /p:Platform=x64 /t:AnimeStudio.<Name>` 或 `msbuild AnimeStudio.<Name>\AnimeStudio.<Name>.vcxproj /p:Configuration=Release /p:Platform=x64 /p:SolutionDir=<repo>\`（**别忘了 `SolutionDir`**，R5）。
2. 确认产物**直接落在** `AnimeStudio.Libraries\AnimeStudio.<Name>.dll`。
3. `dumpbin /headers`（或本文附带的 PE 解析）：machine 必须是 `8664`，`machine != 0x14c`。
4. `dumpbin /exports`：符号名必须是**裸名**（无 `?`/`@` 修饰）；导入表应只有 `KERNEL32`（不要引入 `VCRUNTIME140.dll`——说明漏了 `/MT`）。
5. `dotnet build AnimeStudio.CLI -c Release -f net10.0-windows` 后检查 `bin\Release\net10.0-windows\AnimeStudio.<Name>.dll` 存在。
6. `.\build.ps1` 后检查 `dist\net10.0-windows\bin\AnimeStudio.<Name>.dll` 存在，且两个 exe 的字节断言（`build.ps1:93-104`）通过。
7. **把新 DLL 一起 commit 进 `AnimeStudio.Libraries`** —— 否则 CI（只跑 `build.ps1`）产出的包会缺库（R1 的直接后果）。

---

## 9. 需要人工核实 / 无法从静态分析确定的点

1. **发布 zip 里是否存在 `bin\x64\` 目录**（决定 R8 是「真 bug」还是「文档外约定」）。本地无 `dist/`（`.gitignore:1` 忽略），无法观测。
2. **提交的 9 个 DLL 与 HEAD 源码是否同源**。RC 版本号恒为 `1.0.0.x`，无法据此判断（R2）。
3. **`versionIndex` 越界**（`FBXNative/api.cpp:131`）是否有上层校验——需查 `AnimeStudio.GUI`/`CLI` 的 FBX 版本下拉框取值范围（属于 app 层报告范围）。
4. **`ACL.ZZZV2` 的 `DecompressedClip::Dispose()` 用 `delete` 释放 `new[]`**（`ACLLibrary.h:35-38`）是否真的从未被调用——静态看是死代码，但若被 walk 到就是 UB。
5. **`build.ps1:73-74` 的覆盖顺序**（CLI 先、GUI 后）在同一 `bin/` 上是否会导致 `AnimeStudio.CLI.exe` 依赖的某个 DLL 被 GUI 版本覆盖——目前两者共享同一份 Utility/Core 输出，理论上一致，但一旦两个项目的属性（如 `DebugType`、裁剪）分叉就会静默出错。

---

*本文所有结论均可由文中 `file:line` 复核；未执行任何构建、未安装任何 SDK、未修改 `research/02-native-build.md` 以外的任何文件。*
