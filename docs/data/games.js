/* AnimeStudio 说明书 · 游戏注册表数据
 * 事实来源：AnimeStudio/GameManager.cs（LoadGames，49 条内置 + 24 条 UnityCN + 1 条自定义 + 1 条 AFK）
 *          AnimeStudio/UnityCNManager.cs（baseData，只取名字不取密钥）
 *          AnimeStudio/FileReader.cs → PreProcessing 的整文件解密分支
 *          AnimeStudio/GameManager.cs → GameTypes 谓词族
 * 说明：这里只描述「机制」，不包含任何密钥字节。
 */
window.AS_GAMES = {
  meta: {
    total: 75,
    families: 8,
    groups: ['hoyo', 'other', 'unity', 'unitycn']
  },

  families: [
    {
      id: 'plain',
      name: '明文 Unity',
      short: '没有锁',
      keys: '不需要',
      pick: '选 Unity',
      blurb: '最普通的情况：包就是标准的 UnityFS，没有加壳也没有改字段。选 Unity 就能直接读。'
    },
    {
      id: 'unitycn',
      name: 'UnityCN',
      short: '一把 16 字节的钥匙',
      keys: 'CNKeys.json 里的一行，放在 exe 同目录',
      pick: '选 Unity CN（24 款内置）或 UnityCN Custom Key',
      blurb:
        '约 24 款国产 Unity 游戏共用同一套机制：包头的密钥区块用 AES 白化，数据块再做半字节替换。工具自带这些游戏的钥匙，也可以自己往 CNKeys.json 里加一行。'
    },
    {
      id: 'hoyo',
      name: 'HoYoverse 系',
      short: 'mhy / mr0k / blk 三种变体',
      keys: '内置常量，随游戏条目切换',
      pick: '选游戏后在下拉里选版本（Live / Pack / CBT）',
      blurb:
        '原神、崩坏3、星穹铁道、绝区零、未定事件簿各有自己的变体。同一个游戏的不同测试阶段写法也不一样，所以条目按版本拆开。'
    },
    {
      id: 'tencent',
      name: '腾讯 Blb / HYG',
      short: '改过的 AES + 变体 RC4',
      keys: '内置常量',
      pick: '选对应条目',
      blurb:
        '容器格式换成 Blb 或 HYG，用一套改过的 AES（S 盒、行移位、密钥扩展都动过）加变体 RC4，最后再对前 16 字节做一次重排。'
    },
    {
      id: 'endfield',
      name: '鹰角 Endfield',
      short: 'FairGuard + VFS 容器',
      keys: '内置常量（CB1 / CB2 各一套）',
      pick: '选 Arknights Endfield 或其 CBT 条目',
      blurb:
        '明日方舟：终末地走的是另一条路：容器换成 VFS，头部字段被按位旋转混淆，数据块由 FairGuard 式的整数混洗派生密钥。'
    },
    {
      id: 'netease',
      name: '网易',
      short: '从密文里自己找钥匙',
      keys: '不需要外部钥匙，从文件自身前 32 字节派生',
      pick: '选 Net Ease',
      blurb:
        '头扫描 + 自定义 CRC 派生四段密钥，再叠 RC4 与按 0x80 对齐的块异或。没有外部钥匙文件，所以选对条目就够了。'
    },
    {
      id: 'vendor',
      name: '厂商自有整文件加密',
      short: '每个游戏一份代码',
      keys: '内置常量或从文件自身读取',
      pick: '选对应条目',
      blurb:
        '十余家厂商直接在整文件层面做手脚：换假头、按固定偏移异或、重排魔数。工具里每个游戏对应一个独立分支。'
    },
    {
      id: 'variant',
      name: '格式变体',
      short: '不是上锁，是改了结构',
      keys: '不需要钥匙',
      pick: '选对应条目（否则会读出错位的数据）',
      blurb:
        '还有一类游戏根本没加密，而是改了包里的数据结构：非标准压缩变体、特殊包头、网格与材质的字段顺序不同。选错条目会解出「看起来正常但全是错位数字」的模型。'
    }
  ],

  /* g: GUI 选择器里的分组——hoyo（第一组）/ other（第二组）/ unity（第三组）/ unitycn（只在 CLI 或密钥编辑里出现）
     f: 加密家族
     t: CLI --game 要填的名字；d: 工具里显示的版本名；cn: 中文名（确定的才写）
     blk: 这个游戏的包常常「一个文件里塞着几十个包」 */
  games: [
    // ── 第一组：HoYoverse ──
    { t: 'GI', d: 'Live', cn: '原神', g: 'hoyo', f: 'hoyo', blk: true, note: '正式服。容器是 mhy 变体，块级走 mr0k。' },
    { t: 'GI_Pack', d: 'Pack', cn: '原神', g: 'hoyo', f: 'hoyo', blk: true, note: '打包形态，整文件先过一次解密，再当块文件拆。' },
    { t: 'GI_CB1', d: 'CBT 1', cn: '原神', g: 'hoyo', f: 'hoyo', note: '一测，整文件头部与现在不同。' },
    { t: 'GI_CB2', d: 'CBT 2', cn: '原神', g: 'hoyo', f: 'hoyo', note: '二测，头部换成 MT19937 生成的异或垫。' },
    { t: 'GI_CB3', d: 'CBT 3', cn: '原神', g: 'hoyo', f: 'hoyo', note: '三测，与二测同一套头处理。' },
    { t: 'GI_CB3Pre', d: 'CBT 3 Pre', cn: '原神', g: 'hoyo', f: 'hoyo', note: '三测前置版本，mhy 与 blk 叠加。' },
    { t: 'BH3', d: 'Live', cn: '崩坏3', g: 'hoyo', f: 'hoyo', blk: true, note: '正式服，加密头 + mr0k 块。' },
    { t: 'BH3Pre', d: 'Pre', cn: '崩坏3', g: 'hoyo', f: 'hoyo', blk: true, note: '预下载形态。' },
    { t: 'BH3PrePre', d: 'Pre Pre', cn: '崩坏3', g: 'hoyo', f: 'hoyo', note: '更早的预下载形态。' },
    { t: 'SR', d: 'Live', cn: '崩坏：星穹铁道', g: 'hoyo', f: 'hoyo', blk: true, note: '正式服，mr0k + Oodle 压缩，常见 .block 套 ENCR。' },
    { t: 'SR_CB2', d: 'CBT 2', cn: '崩坏：星穹铁道', g: 'hoyo', f: 'hoyo', blk: true, note: '二测，与正式服同一套密钥但包结构更简。' },
    { t: 'ZZZ', d: 'Live', cn: '绝区零', g: 'hoyo', f: 'hoyo', blk: true, note: '正式服，mhy 容器 + Oodle；网格要额外做一次重挂。' },
    { t: 'ZZZ_CB1', d: 'CBT 1', cn: '绝区零', g: 'hoyo', f: 'hoyo', note: '一测，mr0k 路线。' },
    { t: 'ZZZ_CB2', d: 'CBT 2', cn: '绝区零', g: 'hoyo', f: 'hoyo', blk: true, note: '二测，mhy 容器会被当成块文件再拆一层。' },
    { t: 'HNA_CB1', d: 'CBT 1', cn: 'Nexus Anima', g: 'hoyo', f: 'tencent', note: '走腾讯 Blb 容器那套。' },
    { t: 'HYG_CB1', d: 'CBT 1', cn: 'Petit Planet', g: 'hoyo', f: 'tencent', note: 'HYG 容器，大端写法。' },
    { t: 'TOT', d: 'Live', cn: '未定事件簿', g: 'hoyo', f: 'hoyo', blk: true, note: 'mr0k，解完还要再做一次尾部密钥处理。' },

    // ── 第二组：其它厂商 ──
    { t: 'Naraka', d: 'Naraka', cn: '永劫无间', g: 'other', f: 'vendor', blk: true, note: '包头字段整体偏移 -0xCA，包内可能是多个 bundle。' },
    { t: 'EnsembleStars', d: 'Ensemble Stars', cn: '偶像梦幻祭', g: 'other', f: 'vendor', note: '整文件按固定方案重排。' },
    { t: 'OPFP', d: 'OPFP', g: 'other', f: 'vendor', note: '假头 + 按路径派生密钥的块级异或。' },
    { t: 'FakeHeader', d: 'Fake Header', g: 'other', f: 'vendor', note: '文件开头被换成一层假头，先剥掉才看得见 UnityFS。' },
    { t: 'FantasyOfWind', d: 'Fantasy of Wind', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'ShiningNikki', d: 'Shining Nikki', cn: '闪耀暖暖', g: 'other', f: 'vendor', note: '与 Fake Header 共用同一套剥头逻辑。' },
    { t: 'HelixWaltz2', d: 'Helix Waltz 2', cn: '螺旋圆舞曲2', g: 'other', f: 'vendor', note: '整文件级解析。' },
    { t: 'NetEase', d: 'Net Ease', cn: '网易系游戏', g: 'other', f: 'netease', note: '密钥从密文自身派生，不需要钥匙文件。' },
    { t: 'AnchorPanic', d: 'Anchor Panic', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'DreamscapeAlbireo', d: 'Dreamscape Albireo', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'ImaginaryFest', d: 'Imaginary Fest', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'AliceGearAegis', d: 'Alice Gears Aegis', cn: '爱丽丝机甲', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'ProjectSekai', d: 'Project Sekai', cn: '世界计划', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'CodenameJump', d: 'Codename Jump', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'GirlsFrontline', d: 'Girls Frontline', cn: '少女前线', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'Reverse1999', d: 'Reverse: 1999', cn: '重返未来1999', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'ArknightsEndfield', d: 'Arknights Endfield', cn: '明日方舟：终末地', g: 'other', f: 'endfield', note: 'VFS 容器 + FairGuard 块解密，最新一套常量。' },
    { t: 'ArknightsEndfieldCB3', d: 'Arknights Endfield CBT3', cn: '明日方舟：终末地', g: 'other', f: 'endfield', note: '三测，容器头混淆方式与正式服相同。' },
    { t: 'ArknightsEndfieldCB2', d: 'Arknights Endfield CBT2', cn: '明日方舟：终末地', g: 'other', f: 'endfield', blk: true, note: '二测，常量与正式服不同，且常见多包结构。' },
    { t: 'ArknightsEndfieldCB1', d: 'Arknights Endfield CBT1', cn: '明日方舟：终末地', g: 'other', f: 'endfield', note: '一测，最早一套常量。' },
    { t: 'Arknights', d: 'Arknights', cn: '明日方舟', g: 'other', f: 'variant', note: '用非标准 LZ4 变体压缩，不换条目会解压失败。' },
    { t: 'JJKPhantomParade', d: 'JJK Phantom Parade', cn: '咒术回战 幻影游行', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'MuvLuvDimensions', d: 'Muv-Luv Dimensions', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'PartyAnimals', d: 'Party Animals', cn: '动物派对', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'LoveAndDeepspace', d: 'Love and Deepspace', cn: '恋与深空', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'SchoolGirlStrikers', d: 'Schoolgirl Strikers', g: 'other', f: 'vendor', note: '整文件级解密。' },
    { t: 'ExAstris', d: 'ExAstris', cn: '来自星尘', g: 'other', f: 'variant', note: '非标准 LZ4 变体，贴图与网格字段也另有一套读法。' },
    { t: 'PerpetualNovelty', d: 'Perpetual Novelty', g: 'other', f: 'variant', note: '包头的 blocksInfo 有特殊写法。' },
    { t: 'RewindingCadence', d: 'Rewinding Cadence', g: 'other', f: 'variant', note: '没加密，但网格、材质、贴图、动画的字段顺序与标准不同。' },
    { t: 'AzurPromiliaCBT2', d: 'Azur Promilia CBT2', cn: '蓝色星原：旅谣', g: 'other', f: 'unitycn', note: 'UnityCN 系，密钥在代码里固定，多包结构。' },
    { t: 'AFKJourney', d: 'AFK Journey', cn: '剑与远征：启程', g: 'other', f: 'vendor', blk: true, note: '.lpak 里塞多个 UnityFS，还要处理散落的 .jsone / .dxt。' },

    // ── 第三组：Unity 原生 ──
    { t: 'Normal', d: 'Unity', cn: '原生 Unity 游戏', g: 'unity', f: 'plain', note: '没加壳的标准 Unity 包，先用它试水。' },
    { t: 'UnityCN', d: 'Unity CN', cn: 'UnityCN 通用条目（隐藏）', g: 'unitycn', f: 'unitycn', hidden: true, note: 'GUI 的下拉里看不到它，只能用命令行 --game UnityCN 指定，作为没有专门条目的 UnityCN 游戏的兜底。' },
    { t: 'UnityCNCustomKey', d: 'UnityCN Custom Key', cn: '自填密钥的 UnityCN', g: 'unity', f: 'unitycn', note: '密钥自填，适合内置表里没有的 UnityCN 游戏。' },

    // ── UnityCN 内置 24 款（GUI 里归在第二组，密钥在 CNKeys.json）──
    { t: 'PGR_GLB_KR', d: 'Punishing Gray Raven - Global/Korea', cn: '战双帕弥什 国际/韩服', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'PGR_CN_JP_TW', d: 'Punishing Gray Raven - China/Japan/Taiwan', cn: '战双帕弥什 国服/日服/台服', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙，与上一条不同。' },
    { t: 'PGR_CN_JP_TW_OLD', d: 'Punishing Gray Raven - China/Japan/Taiwan Old Key', cn: '战双帕弥什 国服/日服/台服 旧钥匙', g: 'other', f: 'unitycn', hidden: true, note: '老版本专用钥匙。它不在枚举里，条目名会被当成通用 UnityCN 处理，只在 CNKeys.json 里存在。' },
    { t: 'Archeland_KalpaOfUniverse', d: 'Archeland/Kalpa of Universe', cn: 'Archeland', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'Archeland_1114', d: 'Archeland 1.1.14', g: 'other', f: 'unitycn', note: '老版本专用钥匙。' },
    { t: 'NeuralCloud', d: 'Neural Cloud', cn: '少女前线：云图计划', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'NeuralCloudCN', d: 'Neural Cloud CN', g: 'other', f: 'unitycn', note: '国服钥匙与海外服不同。' },
    { t: 'HiganEruthyll', d: 'Higan: Eruthyll', cn: '代号：鸢', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'WhiteCord', d: 'White Chord', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'Mecharashi', d: 'Mecharashi', cn: '钢岚', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'CastlevaniaMoonNightFantasy', d: 'Castlevania: Moon Night Fantasy', cn: '恶魔城：月夜幻想', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'HYSXZY', d: 'Huā Yì Shān Xīn Zhī Yuè', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'DoulaContinent', d: 'Doula Continent', cn: '斗罗大陆', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'BlessGlobal', d: 'Bless Global', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'Starside', d: 'Starside', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'ResonanceSoltice', d: 'Resonance Soltice', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'OblivionOverride', d: 'Oblivion Override', cn: '湮灭线', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'Dawnlands', d: 'Dawnland', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'BB', d: 'BB', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'DynastyLegends2', d: 'Dynasty Legends 2', cn: '三国群英传2', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'EvernightCN', d: 'Evernight CN', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'XintianlongBabu', d: 'Xintianlong Babu', cn: '新天龙八部', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'FrostpunkBeyondTheIce', d: 'Frostpunk: Beyond the Ice', cn: '冰汽时代：冰封之上', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' },
    { t: 'CatFantasy', d: 'Cat Fantasy', cn: '猫咪幻想', g: 'other', f: 'unitycn', note: 'UnityCN，内置钥匙。' }
  ],

  /* 首页「拆一个包试试」用的六个样本，字段面向读者而非程序 */
  profiles: [
    {
      t: 'Normal',
      name: '普通 Unity 游戏',
      files: 'globalgamemanagers、.assets、各种 .bundle',
      lock: '没有锁',
      key: '不需要',
      pick: 'Unity',
      out: '全部类型的资产都能直接预览'
    },
    {
      t: 'GI',
      name: '原神 · 正式服',
      files: '体积很大的 .blk，里面套着 UnityFS',
      lock: '容器头被换过（mhy），数据块再走一层 mr0k',
      key: '跟着条目走，内置',
      pick: 'Genshin Impact → Live',
      out: '一个 .blk 里往往有几十个包，导出时按容器路径分目录最省事'
    },
    {
      t: 'SR',
      name: '星穹铁道 · 正式服',
      files: '.block 里塞满 ENCR，块用 Oodle 压缩',
      lock: 'mr0k 块解密 + Oodle 解压',
      key: '跟着条目走，内置',
      pick: 'Honkai: Star Rail → Live',
      out: '需要工具目录里那 9 个原生 DLL，缺一个就会报找不到模块'
    },
    {
      t: 'ZZZ',
      name: '绝区零 · 正式服',
      files: 'mhy 容器，块用 Oodle 压缩',
      lock: 'mhy 容器 + 块级解密',
      key: '跟着条目走，内置',
      pick: 'Zenless Zone Zero → Live',
      out: '模型要额外做一次网格重挂，工具会自动处理，但导出会比较慢'
    },
    {
      t: 'PGR_GLB_KR',
      name: '战双帕弥什 · 国际服',
      files: '标准 UnityFS，只是数据块被换过',
      lock: 'UnityCN：AES 白化密钥 + 半字节替换',
      key: 'CNKeys.json 里的一行 16 字节',
      pick: 'Punishing Gray Raven - Global/Korea',
      out: '钥匙不对时会直接抛异常，不会给你半成品'
    },
    {
      t: 'ArknightsEndfield',
      name: '明日方舟：终末地',
      files: 'VFS 容器，头部字段被按位旋转过',
      lock: 'VFS 头解混淆 + FairGuard 块解密',
      key: '跟着条目走，内置',
      pick: 'Arknights Endfield',
      out: '三个 CBT 版本的常量各不一样，必须对上'
    }
  ]
};
