# catalog — AnimeStudio 导出结果 → wiki 结构化数据

`catalog.py` 不碰解密。它只负责把 sidecar（`AnimeStudio.CLI.exe`）吐出来的文件，
整理成站点能直接消费的结构：**内容寻址的图片库 + 逐条清单 + 多语言文本表 + 版本差异**。

```
AnimeStudio.CLI.exe   →   导出目录   →   catalog.py   →   site/
（解密 / 解容器 / 解压）      （原始文件）     （编目 / 去重 / 合并）    （JSON + 图片）
```

## 1. 先跑 sidecar

```powershell
# 星穹铁道：--game SR
# 绝区零：  --game ZZZ
AnimeStudio.CLI.exe "D:\Games\StarRail\StarRail_Data" "out\hsr-2.5.0" `
    --game SR `
    --types Texture2D Sprite TextAsset `
    --export_type convert `
    --group_assets ByContainer `
    --silent 2> "out\hsr-2.5.0.log"
```

几条硬性注意（都来自源码核对，不是猜的）：

| 事项 | 说明 |
|---|---|
| `--types` 用**空格**分隔 | 写成逗号会被解析成空集并静默跳过（README 里的写法是错的） |
| 退出码恒为 0 | 必须靠 `logstat` 解析日志判断成败 |
| 二进制容器要 Oodle | HSR 用 `OodleHSR`、ZZZ 用 `OodleMr0k`，缺 `AnimeStudio.Ooz.dll` 会直接抛 `DllNotFoundException` |
| `.block` 聚合体 | HSR 单个 `.block` 里上百个 ENCR，注意内存；导出日志不要放进 `--dump` 目录 |
| 取 MonoBehaviour 要 typetree | 加 `--dummy_dlls <目录>`，dummy dll 必须与游戏版本对齐 |
| Windows x64 only | 原生 dll 全是 x64 PE，不能跨平台 |

## 2. 编目

```powershell
# 扫描导出目录，算哈希、读尺寸、复制成内容寻址结构
python tools/catalog.py ingest `
    --dump out\hsr-2.5.0 --out out\catalog\2.5.0 `
    --game hsr --version 2.5.0 --phash

# 解析日志（有错就返回非零，CI 才有意义）
python tools/catalog.py logstat --log out\hsr-2.5.0.log --out out\catalog\2.5.0\logstat.json

# 建库
python tools/catalog.py build `
    --manifest out\catalog\2.5.0\manifest.jsonl `
    --db out\catalog\hsr-2.5.0.db --game hsr --version 2.5.0

# 多语言文本表
python tools/catalog.py textmap `
    --map en=TextMapEN.json --map zh=TextMapCHS.json --map ja=TextMapJP.json `
    --out out\catalog\text
```

## 3. 版本对比

```powershell
python tools/catalog.py diff --old out\catalog\hsr-2.4.0.db --new out\catalog\hsr-2.5.0.db `
    --out out\catalog\diff-2.4.0-2.5.0.json
# {"added": 1284, "removed": 37, "changed": 512}
```

主键用 `(game, version, kind, name)`，内容用 `sha256` 判断变没变。
名字会变、内容不会，所以「这张图换了没有」是一次索引查询。

## 3.5 TextMap：文本表怎么用

游戏里的文案不是明文存在每个 UI 上，而是「32 位哈希 → 本地化字符串」一张表。
资产只存哈希，运行时查表。所以：

- **一份表 = 一种语言**，同一个哈希在 CN/EN/JP/KR 里都指向同一句话的不同译文。
- **key 是内容哈希**：文案一改，key 就变。这意味着不能按 key 做「这条改了」的 diff，
  只能按「哪个实体引用了哪个 key」做 diff（见 `diff` 与 `refs.jsonl`）。
- TextMap 覆盖的是**可本地化字符串**；烤进图片的标题、运行时拼出来的句子不在里面。

```powershell
# 1) 合并多语言表
python tools/catalog.py textmap --map en=TextMapEN.json --map zh=TextMapCHS.json --map ja=TextMapJP.json --out out\catalog\text

# 2) 把导出资产里的哈希字段关联回表
python tools/catalog.py join --dump out\hsr-2.5.0 --text out\catalog\text --out out\catalog\2.5.0\refs.jsonl
```

`join` 的做法是：遍历导出目录里的 JSON，找出所有「落在 TextMap key 集合里」的整数或数字字符串，
连字段路径一起记下来。产出：

```
out\catalog\2.5.0\refs.jsonl      # {"source":..., "field":"$.ItemName.Hash", "hash":"1234"}
out\catalog\2.5.0\join-stats.json # {"refs":3,"distinct_hashes_used":3,"text_keys":3,"coverage":1.0}
```

`coverage` 是「表里有多少 key 真的被用到」。它低于预期通常意味着两件事之一：
导出里还有别的哈希命名（比如 `TextmapID`、`NameHash`），或者导出格式不是 JSON。

**限制**：`join` 只解析 JSON 形式的导出。如果 sidecar 产的是 TypeTree 文本 dump，
需要先写一层转 JSON 的适配器（每个版本一次），否则 `join` 会报 `json_files_parsed=0`。
## 4. 站点产物

```powershell
python tools/catalog.py export --db out\catalog\hsr-2.5.0.db `
    --manifest-dir out\catalog\2.5.0 --out out\site
```

```
out\site\
  assets.json      # [{id,kind,name,container,sha256,w,h,url}]
  versions.json    # {game, version, built_at}
  img\<sha256>.png # 内容寻址：同一张图在任意版本只存一份
out\catalog\text\  # text.<locale>.json  {hash: 文本}
```

## 5. manifest.jsonl 字段

| 字段 | 含义 |
|---|---|
| `game` / `version` | 来源标识 |
| `kind` | `texture` `sprite` `textasset` `monobehaviour` `other` |
| `name` | 资产名（去掉扩展名） |
| `container` | 容器内路径，来自 `--group_assets ByContainer` 的目录结构 |
| `source` | 导出目录里的相对路径，出问题时用来回查 |
| `sha256` / `bytes` | 内容指纹与大小，去重与增量发布的依据 |
| `width` / `height` / `mime` | 图片元数据（读 PNG IHDR，不依赖 Pillow） |
| `blob` | 内容寻址副本的相对路径 |
| `dhash` | 感知哈希，加 `--phash` 时出现；用来发现重新压缩过的同一张图 |

## 6. 自测

仓库里带了一份 `fixtures/`（合成数据，不是游戏素材），可直接验证全链路：

```powershell
python tools/catalog.py ingest --dump fixtures\v1 --out build\v1 --game hsr --version 2.4.0 --phash
python tools/catalog.py build --manifest build\v1\manifest.jsonl --db build\v1.db --game hsr --version 2.4.0
python tools/catalog.py ingest --dump fixtures\v2 --out build\v2 --game hsr --version 2.5.0
python tools/catalog.py build --manifest build\v2\manifest.jsonl --db build\v2.db --game hsr --version 2.5.0
python tools/catalog.py diff --old build\v1.db --new build\v2.db
```

期望：`v1` 报 `duplicate_hashes=1`（两张同内容不同名），diff 报 `added=1 removed=3 changed=1`。

## 7. 还没做的

- **图集切片**：目前按 `Sprite` 对象导出（sidecar 侧 `--types Sprite`）。若某游戏的 UI 图只以 atlas 形式存在，需要在 sidecar 侧按 `m_RD.rect` 切，catalog 这一层不负责。
- **文本与资产的关联**：`asset_text` 表留了位置但还没写入；需要 TextMap hash 与 MonoBehaviour 字段的映射规则，按游戏各写一份。
- **富文本清洗**：`<color>` / `{0}` 占位符原样保留在 `raw` 里，转 wikitext/HTML 是发布层的事。
