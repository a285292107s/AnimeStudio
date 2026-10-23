/* Host 接入层：只有在 WebView2 里运行时才接管数据源。
   浏览器预览（node tools/serve-host.js）保持 app.js 的 mock 数据不变。
   协议见 AnimeStudio.Host/HostService.cs。 */

(() => {
  'use strict';

  const webview = window.chrome?.webview;
  if (!webview || !window.APP) {
    return;
  }

  const APP = window.APP;
  const $ = (selector) => document.querySelector(selector);
  const nf = new Intl.NumberFormat('zh-CN');
  const fmt = (value) => nf.format(value || 0);
  const fmtBytes = (bytes) => {
    if (!bytes || bytes <= 0) {
      return '—';
    }

    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit += 1;
    }

    const digits = value >= 100 ? 0 : value >= 10 ? 1 : 2;
    return `${value.toFixed(digits)} ${units[unit]}`;
  };
  const GAME_NAMES = { SR: '星穹铁道', ZZZ: '绝区零' };
  const nameOfGame = (id) => GAME_NAMES[id] || id || '—';
  const IMAGE_PAGE = 120;

  const pending = new Map();
  const state = {
    settings: {},
    taskRunning: false,
    timer: null,
    hostState: null,
    mapStats: null,
    imageOffset: 0,
    imageTotal: 0,
    pendingExport: null,
    category: 'all',
    wizardDismissed: false,
    hintShown: false,
    taskStartedAt: 0,
    taskKind: '',
    progressSamples: [],
    imagesDirty: true,
    imagesLoading: false,
    outputVerifyTimer: null,
    assetsLoaded: false,
    assetsDirty: true,
    assetsLoading: false,
    assetsQueued: false,
    // ZZZ 容器索引（hash → 路径）状态：{ loaded, count, path, loading, downloading }
    containerIndex: null,
    containerIndexProgress: 0,
    // 资源浏览里勾中的行：key -> { name, type }；key 由 类型+PathID+来源 组成。
    assetSelected: new Map(),
    assetRows: [],
    assetAnchor: -1,
  };

  let seq = 0;

  // Bumped whenever the rows on screen stop belonging to the current map (game switch,
  // output change, rebuild). A query that resolves afterwards is dropped instead of painted.
  let assetGeneration = 0;

  /// <summary>
  /// One source of truth for a game's export directory: the settings dialog rows and the rail
  /// OUTPUT block both read from <c>outputDirectories</c>, falling back to the active game's
  /// resolved <c>outputDirectory</c>.
  /// </summary>
  function outputPathFor(game = state.settings.game) {
    const outputs = state.settings.outputDirectories || {};
    if (outputs[game]) {
      return outputs[game];
    }

    return game === state.settings.game ? (state.settings.outputDirectory || '') : '';
  }

  // app.js loads its shell asynchronously (mock/data.json); a welcome reply can arrive
  // before APP.state.data exists, so hold host messages until the shell is ready.
  let shellReady = Boolean(APP.state.data);
  const earlyMessages = [];
  window.addEventListener('app:ready', () => {
    shellReady = true;
    for (const message of earlyMessages.splice(0)) {
      handle(message);
    }
  });

  function call(type, payload = {}) {
    return new Promise((resolve) => {
      const requestId = ++seq;
      pending.set(requestId, resolve);
      webview.postMessage({ type, requestId, ...payload });
      setTimeout(() => pending.delete(requestId), 120000);
    });
  }

  webview.addEventListener('message', (event) => handle(event.data));

  function handle(message) {
    if (!message || typeof message !== 'object') {
      return;
    }

    if (!shellReady) {
      earlyMessages.push(message);
      return;
    }

    if (message.requestId && pending.has(message.requestId)) {
      pending.get(message.requestId)(message);
      pending.delete(message.requestId);
      return;
    }

    switch (message.type) {
      case 'welcome':
        onWelcome(message);
        break;
      case 'settings.changed': {
        const previousOutput = outputPathFor();
        applySettings(message.settings, message.state);
        if ($('#settings') && !$('#settings').hidden) {
          fillSettingsForm();
        }
        if (previousOutput !== outputPathFor()) {
          resetOutputViews();
          if (document.querySelector('.tab.is-active')?.dataset.tab === 'assets') {
            refreshAssets();
          }
        }
        break;
      }
      case 'task.started':
        onTaskStarted(message);
        break;
      case 'task.progress':
        onProgress(message);
        break;
      case 'log':
        APP.pushLog(`[${message.level}] ${message.message}`);
        if (message.level === 'error') {
          APP.setStatus('出错：见日志');
        }
        if (state.taskRunning && state.taskKind === 'index') {
          trackIndexStage(message.message);
        }
        break;
      case 'task.hint':
        state.hintShown = true;
        APP.pushLog(`提示：${message.message}`);
        APP.toast(message.message);
        break;
      case 'task.done':
        onTaskDone(message);
        break;
      case 'diff.result':
        onDiff(message);
        break;
      case 'task.failed':
        endTask();
        APP.toast(`任务失败：${message.message}`);
        APP.pushLog(`任务失败：${message.message}`);
        break;
      case 'task.cancelled':
        endTask();
        APP.toast('任务已取消');
        APP.pushLog('任务已取消');
        break;
      case 'task.busy':
        APP.toast('已有任务在进行中');
        break;
      case 'images.scanning':
        setImagesDirty();
        APP.setStatus('正在扫描图库目录…', true);
        setGalleryHint('正在加载图库…', '首次扫描输出目录，图片越多耗时越长');
        break;
      case 'map.stats':
        state.mapStats = message;
        paintChrome();
        APP.setStatus('就绪');
        APP.pushLog(
          `索引统计：${fmt(message.totalEntries)} 资产 / ${fmt(message.sourceFiles)} 个文件 / ` +
          `Sprite ${fmt(message.sprites)} / Texture2D ${fmt(message.textures)}（${message.elapsedMs} ms）`);
        break;
      case 'games.detected':
        onGamesDetected(message);
        break;
      case 'games.scan.pending':
        APP.setStatus('正在查找已安装的游戏…', true);
        break;
      case 'games.scan.busy':
        APP.toast('正在查找中…');
        break;
      case 'games.scan.failed':
        APP.setStatus('就绪');
        APP.pushLog(`自动查找失败：${message.message}`);
        APP.toast('自动查找失败，请手动选择目录');
        break;
      case 'output.synced':
        APP.pushLog(`输出目录已移动，自动同步：${message.from} → ${message.to}`);
        APP.toast(`输出目录已移动：已同步到 ${message.to.split('\\').pop() || message.to}`);
        break;
      case 'container.index.pending':
        if (state.containerIndex) {
          state.containerIndex.loading = true;
        }
        paintContainerIndexStatus();
        break;
      case 'container.index': {
        const previousLoaded = Boolean(state.containerIndex?.loaded);
        const previousCount = state.containerIndex?.count || 0;
        state.containerIndex = {
          loaded: Boolean(message.loaded),
          count: message.count || 0,
          path: message.path || '',
          loading: Boolean(message.loading),
          downloading: Boolean(message.downloading),
        };
        paintContainerIndexStatus();

        const changed = state.containerIndex.loaded !== previousLoaded ||
          (state.containerIndex.loaded && state.containerIndex.count !== previousCount);

        if (changed) {
          // 容器从哈希变成了路径（或反过来）：屏幕上的行已经过期。
          resetAssets(message.loaded ? '容器索引已加载，正在刷新…' : '容器索引已清除');
          if (document.querySelector('.tab.is-active')?.dataset.tab === 'assets') {
            refreshAssets();
          }
        }
        break;
      }
      case 'container.index.progress':
        state.containerIndexProgress = message.received || 0;
        paintContainerIndexStatus();
        APP.setStatus(
          `正在下载容器索引 ${fmtBytes(message.received || 0)}` +
            (message.total ? ` / ${fmtBytes(message.total)}` : ''),
          true);
        break;
      case 'container.index.failed':
        if (state.containerIndex) {
          state.containerIndex.downloading = false;
        }
        paintContainerIndexStatus();
        APP.toast(`容器索引下载失败：${message.message}`);
        APP.pushLog(`容器索引下载失败：${message.message}`);
        break;
      case 'container.index.busy':
        APP.toast('容器索引正在下载中，稍等');
        break;
      case 'game.switched':
        state.mapStats = null;
        state.category = 'all';
        APP.state.imageFilter = 'all';
        applySettings(message.settings, message.state);
        resetOutputViews();
        resetAssets();
        if (document.querySelector('.tab.is-active')?.dataset.tab === 'assets') {
          refreshAssets();
        }
        if ($('#settings') && !$('#settings').hidden) {
          fillSettingsForm();
        }
        APP.toast(`已切换到 ${nameOfGame(message.settings.game)}`);
        break;
      case 'map.stats.pending':
        APP.setStatus('正在统计索引…', true);
        break;
      case 'map.stats.failed':
        APP.setStatus('就绪');
        APP.pushLog(`索引统计失败：${message.message}`);
        break;
      default:
        break;
    }
  }

  /* ---------- 启动 ---------- */

  function onWelcome(message) {
    $('#modeChip').textContent = 'WebView2 宿主 · 真实数据';
    state.settings = message.settings || {};

    // 两个游戏现在都走同一套真实管线，去掉 mock 里的「待验证」标记。
    (APP.state.data.games || []).forEach((game) => {
      if (game.status === 'probe') {
        game.status = 'ready';
      }
    });
    APP.renderGames();

    // 资源浏览需要真实 AssetMap，只在宿主里出现。
    const assetsTab = document.querySelector('.tab[data-tab="assets"]');
    if (assetsTab) {
      assetsTab.hidden = false;
    }

    applySettings(message.settings, message.state);

    // The shell boots with mock cards/categories; clear them before the real lazy loads.
    APP.state.images = [];
    APP.state.data.containers = [];
    APP.state.data.stats.uiSprites = 0;
    APP.state.galleryLoaded = false;
    APP.renderImageGrid();

    // Images are scanned lazily: a fresh output folder can hold tens of thousands of PNGs,
    // and scanning it on startup / output change made the window briefly unresponsive.
    setImagesDirty();
    if (document.querySelector('.tab.is-active')?.dataset.tab === 'images') {
      ensureImagesLoaded();
    }

    // 资源浏览是默认页：启动后自动列出前 300 条，省掉一次「搜索」。
    setTimeout(() => {
      if (document.querySelector('.tab.is-active')?.dataset.tab === 'assets' &&
          state.assetsDirty && !state.assetsLoading) {
        refreshAssets();
      }
    }, 600);

    APP.pushLog(`宿主已连接${message.host?.cliPath ? '：' + message.host.cliPath : '（未找到 CLI）'}`);
    if (!message.host?.cliPath) {
      APP.toast('未找到 AnimeStudio.CLI.exe，任务无法执行');
    }

    // A folder renamed while the host was closed is followed at startup.
    (message.outputSyncs || []).forEach((notice) => {
      APP.pushLog(`输出目录已移动，自动同步：${notice.from} → ${notice.to}`);
      APP.toast(`输出目录已移动：已同步到 ${notice.to.split('\\').pop() || notice.to}`);
    });

    // Cheap safety net for renames while the window stays open.
    if (!state.outputVerifyTimer) {
      state.outputVerifyTimer = setInterval(() => {
        if (!document.hidden && !state.taskRunning) {
          call('output.verify');
        }
      }, 20000);
    }
  }

  /// <summary>Clears images read from the previous output and asks for the new ones.</summary>
  function resetOutputViews() {
    APP.state.images = [];
    APP.state.data.stats.uiSprites = 0;
    APP.state.galleryLoaded = false;
    state.imageOffset = 0;
    state.imageTotal = 0;
    state.category = 'all';
    APP.state.imageFilter = 'all';
    APP.state.data.containers = [];
    APP.renderImageGrid();

    // The map lives under the output folder too, so its rows are stale as well.
    resetAssets();

    // Do not scan the new output folder unless the gallery is actually open.
    setImagesDirty();
    if (document.querySelector('.tab.is-active')?.dataset.tab === 'images') {
      ensureImagesLoaded();
    }
  }

  function setImagesDirty() {
    state.imagesDirty = true;
    state.imagesLoading = false;
    const count = $('#imageCount');
    if (count) {
      count.textContent = '—';
    }
  }

  function setGalleryHint(title, detail) {
    const grid = $('#imageGrid');
    if (grid) {
      grid.innerHTML = `<div class="empty"><b>${title}</b><span>${detail}</span></div>`;
    }

    const count = $('#imageCount');
    if (count) {
      count.textContent = '加载中…';
    }
  }

  function ensureImagesLoaded() {
    if (state.imagesLoading) {
      return;
    }

    if (!state.imagesDirty && APP.state.images.length > 0) {
      return;
    }

    if (!APP.state.images.length) {
      setGalleryHint('正在加载图库…', '首次扫描输出目录，图片越多耗时越长');
    }

    loadImages($('#imageSearch')?.value || '', 0, state.category);
  }

  function applySettings(settings, hostState) {
    if (settings) {
      state.settings = settings;
    }
    if (hostState) {
      state.hostState = hostState;
    }

    const current = state.hostState || {};

    // The host is the source of truth for the active game: sync it before rendering tabs.
    if (current.game) {
      APP.state.gameId = current.game;
    }

    // Update the numbers first, then render once, then override the mock labels.
    if (typeof current.images === 'number') {
      APP.state.data.stats.uiSprites = current.images;
    }

    if (current.indexInfo) {
      APP.state.data.indexInfo = current.indexInfo;
      APP.state.data.game = {
        ...(APP.state.data.game || {}),
        name: nameOfGame(current.indexInfo.game),
        version: current.indexInfo.version || '—',
      };
    }

    if (current.containerIndex) {
      state.containerIndex = { ...current.containerIndex };
      paintContainerIndexStatus();
    }

    paintChrome();
  }

  function paintChrome() {
    APP.renderGames();
    APP.renderRail();
    APP.renderTabs();
    APP.renderInspector();

    $('#gamePath').textContent = state.settings.gameDataPath || '未选择游戏目录（点「更换」）';
    $('#gamePath').title = state.settings.gameDataPath || '';
    const gamePathHint = $('#gamePathHint');
    if (gamePathHint) {
      gamePathHint.textContent = `${nameOfGame(state.settings.game)}的 *_Data 安装目录；换游戏时自动切换。`;
    }
    const outputPath = outputPathFor();
    $('#outDir').textContent = outputPath || '未选择输出目录（点击选择）';
    $('#outDir').title = outputPath;
    const outGameLabel = $('#outGameLabel');
    if (outGameLabel) {
      outGameLabel.textContent = `当前游戏：${nameOfGame(state.settings.game)}`;
    }
    const outHint = $('#outHint');
    if (outHint) {
      outHint.textContent = outputPath
        ? '每款游戏独立目录；切换游戏自动同步，可在「设置」里分别修改。'
        : '还没有输出目录：点上面的路径去选择。';
    }

    const ready = Boolean(state.hostState?.mapExists);
    const indexInfo = state.hostState?.indexInfo || {};
    const mapStats = state.mapStats;
    const mapPath = state.settings.mapPath || indexInfo.mapPath || '';
    const mapBytes = mapStats?.mapBytes ?? indexInfo.mapBytes ?? 0;
    const cacheBytes = mapStats?.cacheBytes ?? indexInfo.cacheBytes ?? 0;
    const mapFormat = mapStats?.mapFormat ?? indexInfo.mapFormat ?? '';

    $('#indexChipText').textContent = state.taskRunning && state.taskKind === 'index'
      ? '建索引中…'
      : ready ? '索引就绪' : '未建索引';
    $('#indexChip').classList.toggle('is-busy', state.taskRunning || !ready);
    $('#indexMeta').textContent = state.taskRunning && state.taskKind === 'index'
      ? '正在建立索引…'
      : ready
        ? `索引就绪${mapStats ? ` · ${fmt(mapStats.totalEntries)} 条 / ${fmt(mapStats.sourceFiles)} 个包` : ' · 正在统计…'}`
        : '还没有索引：点下面的「重建索引」';
    $('#indexMeta').title = mapPath;

    const buildButton = $('#buildIndex');
    if (buildButton) {
      buildButton.disabled = state.taskRunning;
    }

    // 明确写清楚是「地图文件」而不是输出目录，避免路径被误读。
    const facts = $('#indexFacts');
    if (facts) {
      facts.hidden = !ready;
    }

    const sizeElement = $('#indexSize');
    if (sizeElement) {
      sizeElement.textContent = mapBytes > 0 ? fmtBytes(mapBytes) : '—';
      sizeElement.title = mapBytes > 0 ? `${fmt(mapBytes)} 字节` : '';
    }

    const formatElement = $('#indexFormat');
    if (formatElement) {
      formatElement.textContent = mapFormat === 'compact'
        ? 'v2 紧凑'
        : mapFormat === 'messagepack'
          ? 'v1 兼容'
          : '—';
      formatElement.hidden = !mapFormat;
    }

    const cacheElement = $('#indexCacheSize');
    if (cacheElement) {
      cacheElement.textContent = cacheBytes > 0 ? fmtBytes(cacheBytes) : '—';
    }

    const cacheNote = document.querySelector('.indexfacts__note');
    if (cacheNote) {
      cacheNote.hidden = cacheBytes <= 0;
    }

    const mapPathElement = $('#indexMapPath');
    const expectedOutput = outputPathFor();
    const expectedMapPath = expectedOutput && state.settings.indexName
      ? `${expectedOutput}\\index\\${state.settings.indexName}.map`
      : '';
    const manual = Boolean(mapPath && expectedMapPath &&
      mapPath.replace(/\//g, '\\').toLowerCase() !== expectedMapPath.replace(/\//g, '\\').toLowerCase());

    if (mapPathElement) {
      mapPathElement.textContent = mapPath || '—';
      mapPathElement.title = mapPath;
      mapPathElement.classList.toggle('is-manual', manual);
    }

    const manualElement = $('#indexManual');
    if (manualElement) {
      manualElement.hidden = !manual;
    }

    // 统计来自索引（Host 后台扫描）；结果未到时用 — 占位，不显示 mock 数字。
    const values = document.querySelectorAll('#stats dd');
    const put = (index, text) => {
      if (values[index]) {
        values[index].textContent = text;
      }
    };

    put(0, mapStats ? fmt(mapStats.totalEntries) : '—');
    put(1, mapStats ? fmt(mapStats.sourceFiles) : '—');
    put(2, APP.state.galleryLoaded ? fmt(APP.state.data.stats.uiSprites) : '—');
    put(3, mapStats ? fmt(mapStats.sprites) : '—');
    put(4, mapStats ? fmt(mapStats.textures) : '—');

    updateWizard();
  }

  /// <summary>First-run guidance: only when something is still missing, and only until dismissed.</summary>
  function fillSettingsForm() {
    const settings = state.settings || {};
    const put = (selector, value) => {
      const input = $(selector);
      if (input) {
        input.value = value || '';
      }
    };

    put('#settingsGamePath', settings.gameDataPath);
    put('#settingsCliPath', settings.cliPath);
    put('#settingsMapPath', settings.mapPath);
    put('#settingsIndexName', settings.indexName);
    // A default-location index has no settings path; show where it actually came from.
    put('#settingsContainerIndex', settings.containerIndexPath || state.containerIndex?.path || '');
    put('#settingsOutputPath', settings.outputDirectory);
    put('#settingsOutputSR', outputPathFor('SR'));
    put('#settingsOutputZZZ', outputPathFor('ZZZ'));
    paintContainerIndexStatus();

    // Mark the game the rest of the dialog (game dir, map, index) belongs to.
    document.querySelectorAll('#settingsOutputs .outputs__row').forEach((row) => {
      const current = row.dataset.game === settings.game;
      row.classList.toggle('is-current', current);
      const badge = row.querySelector('[data-current]');
      if (badge) {
        badge.hidden = !current;
      }
    });

    const label = $('#settingsGameLabel');
    if (label) {
      label.textContent = nameOfGame(settings.game);
    }

    const outputLabel = $('#settingsOutputLabel');
    if (outputLabel) {
      outputLabel.textContent = nameOfGame(settings.game);
    }
  }

  function openSettings() {
    fillSettingsForm();
    const modal = $('#settings');
    if (modal) {
      modal.hidden = false;
    }
  }

  /// <summary>Shows whether the ZZZ hash → path table is loaded, and what the download is doing.</summary>
  function paintContainerIndexStatus() {
    const index = state.containerIndex || {};
    const label = $('#settingsContainerIndexState');
    const hint = $('#settingsContainerHint');
    const download = $('#settingsContainerDownload');

    if (download) {
      download.disabled = Boolean(index.downloading || index.loading);
      download.textContent = index.downloading ? '下载中…' : index.loaded ? '更新' : '下载';
    }

    if (label) {
      label.textContent = index.downloading
        ? `下载中 ${fmtBytes(state.containerIndexProgress)}`
        : index.loading
          ? '正在读取索引…'
          : index.loaded
            ? `已加载 ${fmt(index.count)} 条`
            : '可选 · 让 ZZZ 资源显示路径';
    }

    if (hint) {
      hint.textContent = index.loaded
        ? `当前：${index.path || '—'}。资源浏览里的容器会显示成 Assets/NapResources/… 这样的路径。`
        : '绝区零的容器是 xxhash64，没有索引时资源浏览只能看到数字 ID。索引是 Eleiyas 的 Z3-AssetIndex（约 81 MB），下载后不用重建地图就能显示路径。';
    }
  }

  function closeSettings() {
    const modal = $('#settings');
    if (modal) {
      modal.hidden = true;
    }
  }

  function updateWizard() {
    const overlay = $('#welcome');
    if (!overlay) {
      return;
    }

    const hasOutput = Boolean(outputPathFor());
    const hasMap = Boolean(state.hostState?.mapExists);
    overlay.hidden = state.wizardDismissed || (hasOutput && hasMap);

    const gamePath = $('#welcomeGamePath');
    if (gamePath) {
      gamePath.textContent = state.settings.gameDataPath || '未找到，点「自动查找」或手动选择';
    }

    const outputPath = $('#welcomeOutputPath');
    if (outputPath) {
      outputPath.textContent = outputPathFor() || '未选择';
    }

    const outputLabel = $('#welcomeOutputLabel');
    if (outputLabel) {
      outputLabel.textContent = nameOfGame(state.settings.game);
    }
  }

  /* ---------- 真实数据加载 ---------- */

  /// <summary>
  /// Gallery viewer reached the edge of the loaded page: fetch the neighbouring page and open
  /// its end image, so ←/→ keeps walking through the whole result set instead of stopping.
  /// </summary>
  async function pageTurn(delta) {
    if (state.imagesLoading || state.imageTotal <= 0) {
      return false;
    }

    const nextOffset = state.imageOffset + delta * IMAGE_PAGE;
    if (nextOffset < 0 || nextOffset >= state.imageTotal) {
      return false;
    }

    await loadImages($('#imageSearch')?.value || '', nextOffset, state.category);

    if (state.imageOffset !== nextOffset || APP.state.images.length === 0) {
      return false;
    }

    APP.openViewerAt(delta > 0 ? 0 : APP.state.images.length - 1);
    return true;
  }

  async function loadImages(query, offset = 0, category = 'all') {
    state.imagesLoading = true;
    try {
      const response = await call('images.query', {
        query: query || '',
        category: category || 'all',
        offset: Math.max(0, offset),
        limit: IMAGE_PAGE,
      });

      if (!response || response.type !== 'images.page') {
        if (response?.type === 'images.failed') {
          APP.toast(`图库加载失败：${response.message}`);
          APP.pushLog(`图库加载失败：${response.message}`);
        }

        return;
      }

      state.imagesDirty = false;
      state.category = category || 'all';
      state.imageOffset = response.offset || 0;
      state.imageTotal = response.total || 0;
      APP.state.galleryLoaded = true;

      APP.state.images = (response.items || []).map((item) => ({
        id: item.relativePath,
        name: item.name,
        file: item.url,
        thumb: item.thumbUrl,
        w: item.width,
        h: item.height,
        kb: Math.round((item.bytes || 0) / 1024),
        category: item.category,
        container: item.category,
        kind: 'Sprite',
        block: '',
      }));

      if (response.categories?.length) {
        APP.state.data.containers = response.categories.map((entry) => ({
          id: entry.id,
          label: entry.label,
          count: entry.count,
        }));
      }

      APP.state.imageFilter = state.category;
      APP.state.data.stats.uiSprites = response.available ?? response.total ?? 0;
      paintChrome();
      $('#imageFilters').style.display = '';
      APP.renderImageGrid();
      updatePagers();
      if (!state.taskRunning) {
        APP.setStatus('就绪');
      }
    } finally {
      state.imagesLoading = false;
    }
  }

  function updatePagers() {
    const imagePager = $('#imagePager');
    if (imagePager) {
      const show = state.imageTotal > IMAGE_PAGE;
      if (state.imageTotal > 0) {
        const from = state.imageOffset + 1;
        const to = Math.min(state.imageTotal, state.imageOffset + IMAGE_PAGE);
        $('#imagePageInfo').textContent = `${fmt(from)}-${fmt(to)} / ${fmt(state.imageTotal)}`;
      }

      imagePager.hidden = !show;
      if (show) {
        imagePager.querySelector('[data-page="prev"]').disabled = state.imageOffset <= 0;
        imagePager.querySelector('[data-page="next"]').disabled = state.imageOffset + IMAGE_PAGE >= state.imageTotal;
      }
    }
  }

  /* ---------- 任务 ---------- */

  function runTask(kind, payload = {}) {
    if (state.taskRunning) {
      APP.toast('已有任务在进行中');
      return;
    }

    call('task.run', {
      kind,
      payload: { game: APP.state.gameId, ...payload },
    });
  }

  function label(kind) {
    return kind === 'index' ? '建索引' : 'UI 图导出';
  }

  function onTaskStarted(message) {
    state.taskRunning = true;
    state.hintShown = false;
    state.taskStartedAt = Date.now();
    state.taskKind = message.kind || '';
    state.progressSamples = [];
    APP.setStatus(`${label(message.kind)}进行中…`, true);
    APP.pushLog(`开始${label(message.kind)}`);
    showProgress(`正在${label(message.kind)}…`);
    paintChrome();
    updateAssetSelectionUi();
  }

  function setProgressStage(text) {
    const element = $('#indexProgressStage');
    if (element) {
      element.textContent = text;
    }
  }

  function setProgressPercent(text) {
    const element = $('#indexProgressPercent');
    if (element) {
      element.textContent = text;
    }
  }

  function setProgressText(text) {
    const element = $('#indexProgressText');
    if (element) {
      element.textContent = text || '—';
    }
  }

  function setProgressMeta(text) {
    const element = $('#indexProgressMeta');
    if (element) {
      element.textContent = text;
    }
  }

  function showProgress(text) {
    const progress = $('#indexProgress');
    if (progress) {
      progress.hidden = false;
    }

    const bar = $('#indexBar');
    if (bar) {
      bar.style.width = '0%';
      bar.parentElement.classList.remove('is-indeterminate');
    }

    setProgressStage(text || '准备中…');
    setProgressPercent('0%');
    setProgressText(text || '—');
    setProgressMeta('已用 0 秒');

    const cancel = $('#taskCancel');
    if (cancel) {
      cancel.hidden = false;
    }
  }

  function hideProgress() {
    const progress = $('#indexProgress');
    if (progress) {
      progress.hidden = true;
    }

    const cancel = $('#taskCancel');
    if (cancel) {
      cancel.hidden = true;
    }

    const bar = $('#indexBar');
    if (bar) {
      bar.style.width = '0%';
      bar.parentElement.classList.remove('is-indeterminate');
    }
  }

  /// <summary>Maps the CLI's phase log lines to a readable stage label above the bar.</summary>
  function trackIndexStage(line) {
    const text = String(line || '');
    if (!text || !state.taskRunning) {
      return;
    }

    if (text.includes('Incremental map cache')) {
      setProgressStage('检查增量缓存');
      setProgressText(shortMessage(text));
    } else if (text.includes('Building AssetMap (Compact)')) {
      setProgressStage('扫描资源包');
      const match = text.match(/([\d,]+) files, ([\d,]+) cached, ([\d,]+) to scan/);
      if (match) {
        setProgressText(`复用 ${match[2]} 个包，需要重扫 ${match[3]} 个`);
      }
    } else if (text.includes('Assembling AssetMap')) {
      setProgressStage('写入地图');
      const bar = $('#indexBar');
      if (bar) {
        bar.style.width = '100%';
        bar.parentElement.classList.add('is-indeterminate');
      }

      setProgressPercent('写入中');
      setProgressText('正在按顺序写入地图文件…');
      setProgressMeta(`已用 ${formatDuration(Date.now() - state.taskStartedAt)}`);
    } else if (text.includes('Incremental cache updated')) {
      setProgressStage('更新增量缓存');
      setProgressText(shortMessage(text));
    } else if (text.includes('AssetMap is up to date')) {
      setProgressStage('无需重建');
      setProgressPercent('100%');
      setProgressText('游戏文件没有变化，直接复用现有地图');
      setProgressMeta(`已用 ${formatDuration(Date.now() - state.taskStartedAt)}`);
    } else if (text.includes('Finished building AssetMap')) {
      setProgressStage('完成');
      setProgressPercent('100%');
      setProgressText(shortMessage(text));
    }
  }

  function parseProgressCounts(value) {
    const match = String(value || '').match(/\[(\d+)\s*\/\s*(\d+)\]/);
    return match ? { done: Number(match[1]), total: Number(match[2]) } : null;
  }

  function onProgress(message) {
    const progress = $('#indexProgress');
    if (progress && progress.hidden) {
      showProgress('进行中…');
    }

    const raw = String(message.message || '');
    const clean = raw.replace(/^\[(Info|Debug|Warning|Error)\]\s*/i, '');
    const counts = parseProgressCounts(clean);
    const fileNameMatch = clean.match(/^\[\d+\s*\/\s*\d+\]\s*(.+)$/);
    const detail = fileNameMatch ? `扫描 ${fileNameMatch[1]}` : shortMessage(raw);
    const elapsed = state.taskStartedAt ? Date.now() - state.taskStartedAt : 0;
    const bar = $('#indexBar');

    if (message.fraction === null || message.fraction === undefined) {
      if (bar) {
        bar.style.width = '100%';
        bar.parentElement.classList.add('is-indeterminate');
      }

      setProgressPercent('…');
      setProgressText(detail || '进行中…');
      setProgressMeta(`已用 ${formatDurationShort(elapsed)}`);
    } else {
      if (bar) {
        bar.style.width = `${Math.round(message.fraction * 100)}%`;
        bar.parentElement.classList.remove('is-indeterminate');
      }

      setProgressPercent(`${Math.round(message.fraction * 100)}%`);
      if (detail) {
        setProgressText(detail);
      }

      const meta = [];
      if (counts) {
        meta.push(`${fmt(counts.done)}/${fmt(counts.total)}`);
      }

      meta.push(`已用 ${formatDurationShort(elapsed)}`);
      const remaining = counts ? estimateRemaining(counts) : '';
      if (remaining) {
        meta.push(remaining);
      }

      setProgressMeta(meta.join(' · '));
    }

    APP.setStatus($('#indexProgressPercent')?.textContent || '进行中…', true);
  }

  /// <summary>
  /// ETA from the last ~15 seconds of progress instead of the whole run: HSR bundles differ
  /// wildly in cost, so a linear estimate from the start keeps sliding. Suppressed for the first
  /// 20 seconds, when early (cheap) files would make the estimate look far too optimistic.
  /// </summary>
  function estimateRemaining(counts) {
    if (!counts || !counts.total || !counts.done) {
      return '';
    }

    if (!state.taskStartedAt || Date.now() - state.taskStartedAt < 20000) {
      return '';
    }

    const now = Date.now();
    const samples = state.progressSamples;
    samples.push({ at: now, done: counts.done });
    while (samples.length > 2 && now - samples[0].at > 15000) {
      samples.shift();
    }

    if (samples.length < 3) {
      return '';
    }

    const first = samples[0];
    const seconds = (now - first.at) / 1000;
    if (seconds < 1.5) {
      return '';
    }

    const rate = (counts.done - first.done) / seconds;
    if (rate <= 0) {
      return '';
    }

    const remaining = (counts.total - counts.done) / rate;
    return remaining >= 1 ? `剩 ${formatDurationShort(remaining * 1000)}` : '';
  }

  function formatDuration(milliseconds) {
    const seconds = Math.round(milliseconds / 1000);
    if (seconds < 90) {
      return `${seconds} 秒`;
    }

    return `${Math.round(seconds / 60)} 分钟`;
  }

  /// <summary>Compact "11s / 2m05s" form for the one-line progress meta row.</summary>
  function formatDurationShort(milliseconds) {
    const totalSeconds = Math.max(0, Math.round(milliseconds / 1000));
    if (totalSeconds < 60) {
      return `${totalSeconds}s`;
    }

    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return seconds === 0 ? `${minutes}m` : `${minutes}m${String(seconds).padStart(2, '0')}s`;
  }

  function shortMessage(value) {
    if (!value) {
      return '';
    }

    const text = String(value).replace(/^\[Info\]\s*/, '').trim();
    return text.length > 48 ? `${text.slice(0, 48)}…` : text;
  }

  function endTask() {
    state.taskRunning = false;
    state.taskKind = '';
    hideProgress();
    APP.setStatus('就绪');
    paintChrome();
    updateAssetSelectionUi();
  }

  function onTaskDone(message) {
    endTask();

    const pending = state.pendingExport;
    state.pendingExport = null;
    const suffix = pending?.count ? `（${pending.count} 张所选）` : '';

    APP.toast(`${label(message.kind)}${message.ok ? '完成' + suffix : '失败（exit ' + message.exitCode + '）'}`);
    APP.pushLog(`${label(message.kind)}结束：exit ${message.exitCode}${suffix}`);

    if (!message.ok && !state.hintShown) {
      const exitHints = {
        1: '参数错误：检查游戏目录与输出目录。',
        2: '目录无效：游戏目录要包含 <Game>_Data，输出目录必须可写。',
        3: '建索引失败：确认游戏完整安装，且磁盘空间足够。',
      };
      const exitHint = exitHints[message.exitCode];
      if (exitHint) {
        APP.pushLog(`提示：${exitHint}`);
        APP.toast(exitHint);
      }
    }

    applySettings(null, message.state);

    if (message.kind === 'images') {
      setImagesDirty();
      if (document.querySelector('.tab.is-active')?.dataset.tab === 'images') {
        ensureImagesLoaded();
      }

      // 导出跑完，勾选就没用了；顺手把人往图库引一下。
      if (message.ok) {
        clearAssetSelection();
        if (pending?.fromSelection) {
          APP.toast('导出完成：去「UI 图库」看结果');
        }
      }
    }

    if (message.kind === 'index') {
      // A rebuilt map replaces every row the Asset Browser was showing, and the tab count
      // belongs to the old file too. Re-query when the pane is (or was) in use; otherwise
      // only mark it dirty so opening the tab picks up the new map.
      const assetsVisible = document.querySelector('.tab.is-active')?.dataset.tab === 'assets';
      const wasLoaded = state.assetsLoaded;
      const refreshing = message.ok && (assetsVisible || wasLoaded);

      resetAssets(message.ok
        ? (refreshing ? '索引已更新，正在刷新…' : '索引已更新，打开「资源浏览」时刷新')
        : '重建失败，列表已清空');

      if (refreshing) {
        refreshAssets();
      }
    }
  }

  function onDiff(message) {
    const images = message.images || {};

    if (!message.hasPrevious) {
      APP.pushLog('已记录基线快照，下次导出后可查看变化');
      APP.toast('已记录基线快照');
      return;
    }

    const parts = [];
    if (images.added || images.removed || images.changed) {
      parts.push(`图片 +${images.added} −${images.removed} ~${images.changed}`);
    }

    const summary = parts.length ? parts.join('，') : '与上次快照一致';
    APP.toast(summary);
    APP.pushLog(`版本对比：${summary}`);

    (images.samples || []).slice(0, 10).forEach((sample) => {
      const tag = sample.kind === 'added' ? '+' : sample.kind === 'removed' ? '−' : '~';
      APP.pushLog(`${tag} 图片 ${sample.path}`);
    });

    state.lastDiff = message;
  }

  /* ---------- 资源浏览（AssetMap 过滤） ---------- */

  const ASSET_HINT =
    '<div class="asset-table__empty">输入容器或名称后点「搜索」。容器支持子串匹配，例如 <code>spriteoutput/ui/fonts</code>。</div>';
  const ASSET_NO_MAP =
    '<div class="asset-table__empty">还没有 AssetMap：先在左侧点「重建索引」，完成后这里会自动刷新。</div>';

  const EXPORT_NAME_LIMIT = 400;

  /// <summary>Filters currently typed into the Asset Browser toolbar.</summary>
  function assetFilters() {
    return {
      name: $('#assetName')?.value || '',
      container: $('#assetContainer')?.value || '',
      source: $('#assetSource')?.value || '',
      types: assetTypes(),
      limit: 300,
    };
  }

  /// <summary>「图像」是 Sprite + Texture2D：导出 PNG 只可能是这两类。</summary>
  function assetTypes() {
    const value = $('#assetType')?.value || 'image';
    if (value === 'all') {
      return [];
    }

    return value === 'image' ? ['Sprite', 'Texture2D'] : [value];
  }

  /// <summary>Drops the rows read from the previous map and marks the pane as needing a query.</summary>
  function resetAssets(note) {
    // Bump the generation so a query still in flight for the old map cannot paint over the reset.
    assetGeneration += 1;
    state.assetsLoaded = false;
    state.assetsDirty = true;
    state.assetRows = [];
    state.assetAnchor = -1;
    clearAssetSelection();

    const list = $('#assetList');
    if (list) {
      list.innerHTML = state.hostState?.mapExists ? ASSET_HINT : ASSET_NO_MAP;
    }

    const count = $('#assetCount');
    if (count) {
      count.textContent = note || '—';
    }

    const tabCount = $('#tabAssetCount');
    if (tabCount) {
      tabCount.textContent = '—';
    }
  }

  /// <summary>
  /// Re-reads the map: used by the Search button, by opening the tab, and after a rebuild —
  /// a rebuilt map makes every row (and the tab count) that was on screen wrong.
  /// </summary>
  function refreshAssets() {
    if (state.assetsLoading) {
      // A reset arrived while the old map was still being scanned: redo it once it returns.
      state.assetsQueued = true;
      return;
    }

    if (!state.hostState?.mapExists) {
      resetAssets();
      return;
    }

    searchAssets(assetFilters());
  }

  async function searchAssets(filters = assetFilters()) {
    if (state.assetsLoading) {
      return;
    }

    const generation = assetGeneration;
    state.assetsLoading = true;
    APP.setStatus('正在扫描 AssetMap…', true);
    try {
      const response = await call('assets.query', filters);

      if (generation !== assetGeneration) {
        return;
      }

      if (!response || response.type !== 'assets.page') {
        APP.pushLog(`资源查询失败：${response?.message || '未知错误'}`);
        APP.toast('资源查询失败');
        const count = $('#assetCount');
        if (count) {
          count.textContent = response?.message || '查询失败';
        }
        return;
      }

      state.assetsLoaded = true;
      state.assetsDirty = false;
      renderAssets(response);
    } finally {
      state.assetsLoading = false;
      if (!state.taskRunning) {
        APP.setStatus('就绪');
      }

      if (state.assetsQueued) {
        state.assetsQueued = false;
        refreshAssets();
      }
    }
  }

  function renderAssets(response) {
    const items = response.items || [];
    const tabCount = $('#tabAssetCount');
    if (tabCount) {
      tabCount.textContent = fmt(response.total);
    }

    const count = $('#assetCount');
    if (count) {
      const truncated = response.truncated ? `（仅显示前 ${fmt(items.length)} 条）` : '';
      count.textContent = `匹配 ${fmt(response.total)} / 共 ${fmt(response.available)}${truncated} · ${fmt(response.sources)} 个来源 · ${response.elapsedMs} ms`;
    }

    const list = $('#assetList');
    if (!list) {
      return;
    }

    state.assetRows = items.map((item) => ({
      key: assetKey(item),
      name: item.name || '',
      container: item.container || '',
      type: item.type || '',
      source: item.source || '',
      sourcePath: item.sourcePath || '',
      pathId: String(item.pathId),
    }));
    state.assetAnchor = -1;

    if (state.assetRows.length === 0) {
      list.innerHTML = '<div class="asset-table__empty">没有匹配的条目。容器支持子串，例如 <code>spriteoutput/ui/fonts</code>。</div>';
      updateAssetSelectionUi();
      return;
    }

    const head = `<div class="asset-row asset-row--head">
        <span class="asset-check"><input type="checkbox" id="assetSelectAll" title="全选本页"></span>
        <span>名称</span><span>容器</span><span>类型</span><span>来源</span><span>PathID</span>
      </div>`;

    const rows = state.assetRows
      .map((row, index) => {
        const selected = state.assetSelected.has(row.key);
        return `<div class="asset-row ${selected ? 'is-selected' : ''}" data-index="${index}" title="${escapeHtml(row.sourcePath)}">
          <span class="asset-check"><input type="checkbox" ${selected ? 'checked' : ''} tabindex="-1"></span>
          <span class="asset-row__name">${escapeHtml(row.name || '—')}</span>
          <span class="asset-row__container">${escapeHtml(row.container || '—')}</span>
          <span class="asset-row__type">${escapeHtml(row.type)}</span>
          <span class="asset-row__source">${escapeHtml(row.source)}</span>
          <span class="asset-row__pathid">${escapeHtml(row.pathId)}</span>
        </div>`;
      })
      .join('');

    list.innerHTML = head + rows;
    updateAssetSelectionUi();
    hintContainerIndexGap();
    APP.pushLog(`资源查询：匹配 ${fmt(response.total)} 条，显示 ${fmt(items.length)} 条（${response.elapsedMs} ms）`);
  }

  /// <summary>ZZZ without the hash index shows numbers; point at the one-click download.</summary>
  function hintContainerIndexGap() {
    const hint = $('#assetSelectionHint');
    if (!hint) {
      return;
    }

    const hasHashes = state.assetRows.some((row) => /^\d{10,}$/.test(row.container));
    if (hasHashes && !state.containerIndex?.loaded) {
      hint.textContent = '容器只显示哈希：到「设置」下载绝区零容器索引即可显示路径（不用重建地图）';
      hint.classList.add('is-warn');
    } else {
      hint.textContent = '勾选行 → 导出；导出结果在图库预览';
      hint.classList.remove('is-warn');
    }
  }

  /// <summary>Stable per-entry identity: the same name can live in several bundles.</summary>
  function assetKey(item) {
    return `${item.type}\u0000${item.pathId}\u0000${item.source}\u0000${item.name}`;
  }

  function toggleAssetSelection(index, additive) {
    const row = state.assetRows[index];
    if (!row) {
      return;
    }

    if (additive && state.assetAnchor >= 0 && state.assetAnchor !== index) {
      // Shift+click: select the whole range between the anchor and the clicked row.
      const from = Math.min(state.assetAnchor, index);
      const to = Math.max(state.assetAnchor, index);
      for (let cursor = from; cursor <= to; cursor += 1) {
        const entry = state.assetRows[cursor];
        if (entry) {
          state.assetSelected.set(entry.key, { name: entry.name, type: entry.type });
        }
      }
    } else if (state.assetSelected.has(row.key)) {
      state.assetSelected.delete(row.key);
    } else {
      state.assetSelected.set(row.key, { name: row.name, type: row.type });
    }

    state.assetAnchor = index;
    paintAssetSelection();
  }

  function setPageSelection(selected) {
    for (const row of state.assetRows) {
      if (selected) {
        state.assetSelected.set(row.key, { name: row.name, type: row.type });
      } else {
        state.assetSelected.delete(row.key);
      }
    }

    state.assetAnchor = -1;
    paintAssetSelection();
  }

  function clearAssetSelection() {
    state.assetSelected.clear();
    state.assetAnchor = -1;
    paintAssetSelection();
  }

  /// <summary>Repaints checkboxes/rows without rebuilding the table (cheap for 300 rows).</summary>
  function paintAssetSelection() {
    document.querySelectorAll('#assetList .asset-row[data-index]').forEach((element) => {
      const row = state.assetRows[Number(element.dataset.index)];
      if (!row) {
        return;
      }

      const selected = state.assetSelected.has(row.key);
      element.classList.toggle('is-selected', selected);
      const box = element.querySelector('input[type="checkbox"]');
      if (box) {
        box.checked = selected;
      }
    });

    updateAssetSelectionUi();
  }

  function updateAssetSelectionUi() {
    const selected = state.assetSelected.size;
    const count = $('#assetSelectedCount');
    if (count) {
      count.textContent = fmt(selected);
    }

    const bar = $('#assetBar');
    if (bar) {
      bar.classList.toggle('has-selection', selected > 0);
    }

    const exportButton = $('#assetExportSelected');
    if (exportButton) {
      exportButton.disabled = selected === 0 || state.taskRunning;
      exportButton.textContent = selected > 0 ? `导出所选 PNG（${fmt(selected)}）` : '导出所选 PNG';
    }

    const clearButton = $('#assetClearSelection');
    if (clearButton) {
      clearButton.disabled = selected === 0;
    }

    const selectAll = $('#assetSelectAll');
    if (selectAll) {
      const pageSelected = state.assetRows.filter((row) => state.assetSelected.has(row.key)).length;
      selectAll.checked = pageSelected > 0 && pageSelected === state.assetRows.length;
      selectAll.indeterminate = pageSelected > 0 && pageSelected < state.assetRows.length;
    }

    syncAssetPreview();
  }

  /// <summary>右侧详情面板在资源浏览里当「购物车」用：列出勾中的名称与导出入口。</summary>
  function syncAssetPreview({ reset = false } = {}) {
    if (document.querySelector('.tab.is-active')?.dataset.tab !== 'assets') {
      return;
    }

    if (state.assetSelected.size === 0) {
      if (reset || APP.state.preview?.kind === 'assets') {
        APP.state.preview = null;
        APP.renderInspector();
      }
      return;
    }

    APP.state.preview = {
      kind: 'assets',
      count: state.assetSelected.size,
      names: [...new Set([...state.assetSelected.values()].map((entry) => entry.name))].slice(0, 200),
      limit: EXPORT_NAME_LIMIT,
    };
    APP.renderInspector();
  }

  /// <summary>Selected rows → one CLI run (`--names` regex, names deduped).</summary>
  function exportSelectedAssets() {
    const rows = [...state.assetSelected.entries()];
    const names = [...new Set(rows.map(([, entry]) => entry.name).filter(Boolean))];
    if (names.length === 0) {
      APP.toast('还没有勾选资源');
      return;
    }

    if (names.length > EXPORT_NAME_LIMIT) {
      APP.toast(`一次最多导出 ${EXPORT_NAME_LIMIT} 个名称（当前 ${names.length} 个），请分批导出`);
      return;
    }

    const types = [...new Set(rows.map(([, entry]) => entry.type).filter(Boolean))];
    state.pendingExport = { count: names.length, fromSelection: true };
    runTask('images', {
      types: types.length > 0 ? types : undefined,
      names: [`^(?:${names.map(escapeRegex).join('|')})$`],
    });
    APP.pushLog(`导出所选：${fmt(names.length)} 个名称（${fmt(rows.length)} 行，类型 ${types.join(' / ') || 'Sprite / Texture2D'}）`);
  }

  /// <summary>Current filters → CLI run, for sets too big to tick by hand.</summary>
  function exportMatchingAssets() {
    const filters = assetFilters();
    const name = filters.name.trim();
    const container = filters.container.trim();

    if (filters.source.trim()) {
      APP.toast('来源过滤暂不支持导出，请用「名称」或「容器」');
      return;
    }

    if (!name && !container) {
      APP.toast('先加一个名称或容器条件，避免把整张地图都导出去');
      return;
    }

    const payload = {
      types: filters.types.length > 0 ? filters.types : ['Sprite', 'Texture2D'],
    };

    if (name) {
      payload.names = [escapeRegex(name)];
    }

    if (container) {
      payload.containers = [escapeRegex(container)];
    }

    state.pendingExport = { count: null, fromSelection: false };
    runTask('images', payload);
    APP.pushLog(`导出全部匹配：${[name && `名称≈${name}`, container && `容器≈${container}`, filters.types.join('/')].filter(Boolean).join('，')}`);
  }

  function escapeHtml(value) {
    return String(value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function onGamesDetected(message) {
    applySettings(message.settings, message.state);
    APP.setStatus('就绪');

    const names = { SR: '星穹铁道', ZZZ: '绝区零' };
    const installs = message.installs || [];
    const applied = installs.filter((item) => item.applied);

    installs.forEach((item) => {
      APP.pushLog(`发现 ${names[item.game] || item.game}：${item.gameDataPath}${item.applied ? '（已采用）' : ''}`);
    });

    if (applied.length > 0) {
      APP.toast(`已自动找到 ${applied.map((item) => names[item.game] || item.game).join('、')}`);
    } else if (installs.length === 0) {
      APP.toast('没有自动找到游戏，请点「更换」手动选择目录');
    } else {
      APP.toast('已找到安装，但当前路径是手动指定的，未覆盖');
    }
  }

  function escapeRegex(value) {
    return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  /* ---------- 交互接管 ---------- */

  document.addEventListener('click', (event) => {
    const hit = (selector) => event.target.closest(selector);

    // 图库延迟加载：切到图库标签时才扫描，避免启动/换输出目录时被几万张图卡住。
    const tabHit = event.target.closest('.tab[data-tab]');
    if (tabHit && tabHit.dataset.tab === 'images') {
      setTimeout(() => {
        ensureImagesLoaded();
        if (APP.state.preview?.kind === 'assets') {
          APP.state.preview = null;
          APP.renderInspector();
        }
      }, 0);
    }

    // 资源浏览同样延迟：第一次打开、索引重建后、换游戏/换目录后都重新查一次。
    if (tabHit && tabHit.dataset.tab === 'assets') {
      setTimeout(() => {
        if (state.assetsDirty || !state.assetsLoaded) {
          refreshAssets();
        }
        syncAssetPreview({ reset: true });
      }, 0);
    }

    if (hit('#openSettings')) {
      stop(event);
      openSettings();
      return;
    }

    if (hit('#settingsClose')) {
      stop(event);
      closeSettings();
      return;
    }

    if (hit('#settingsPickGame')) {
      stop(event);
      call('settings.pick', { target: 'game' });
      return;
    }

    if (hit('#settingsContainerPick')) {
      stop(event);
      call('container.index.pick');
      return;
    }

    if (hit('#settingsContainerDownload')) {
      stop(event);
      call('container.index.download');
      APP.pushLog('开始下载 ZZZ 容器索引…');
      return;
    }

    if (hit('#settingsPickOutput')) {
      stop(event);
      call('settings.pick', { target: 'output' });
      return;
    }

    const pickOutput = hit('[data-pick-output]');
    if (pickOutput) {
      stop(event);
      call('settings.pick', { target: 'output', game: pickOutput.dataset.pickOutput });
      return;
    }

    if (hit('#settingsOpenOutput')) {
      stop(event);
      call('open.folder');
      return;
    }

    if (hit('#assetSearch')) {
      stop(event);
      searchAssets(assetFilters());
      return;
    }

    if (hit('#assetReset')) {
      stop(event);
      ['#assetName', '#assetContainer', '#assetSource'].forEach((selector) => {
        const input = $(selector);
        if (input) {
          input.value = '';
        }
      });
      const type = $('#assetType');
      if (type) {
        type.value = 'image';
      }
      searchAssets(assetFilters());
      return;
    }

    if (hit('#assetExportSelected')) {
      stop(event);
      exportSelectedAssets();
      return;
    }

    if (hit('#assetExportAll')) {
      stop(event);
      exportMatchingAssets();
      return;
    }

    if (hit('#assetClearSelection')) {
      stop(event);
      clearAssetSelection();
      return;
    }

    // 整行都能点：勾选比对准小方块容易得多；Shift 连选一段。
    if (event.target.closest('#assetList .asset-row--head')) {
      stop(event);
      const allSelected = state.assetRows.length > 0 &&
        state.assetRows.every((row) => state.assetSelected.has(row.key));
      setPageSelection(!allSelected);
      // 被 preventDefault 的 checkbox 会在事件结束后回滚到点击前的状态，
      // 所以下一拍再写一次 checked / indeterminate。
      setTimeout(updateAssetSelectionUi, 0);
      return;
    }

    const assetRow = event.target.closest('#assetList .asset-row[data-index]');
    if (assetRow) {
      stop(event);
      toggleAssetSelection(Number(assetRow.dataset.index), event.shiftKey);
      return;
    }

    if (hit('#settingsDiff')) {      stop(event);
      closeSettings();
      call('diff.run');
      APP.toast('正在对比上次快照…');
      return;
    }

    if (hit('#settingsSave')) {
      stop(event);
      const outputs = {
        SR: $('#settingsOutputSR')?.value?.trim() || '',
        ZZZ: $('#settingsOutputZZZ')?.value?.trim() || '',
      };
      const single = $('#settingsOutputPath');
      if (single) {
        // Old cached markup: one field for whichever game is active.
        outputs[state.settings.game] = single.value.trim();
      }

      call('settings.save', {
        settings: {
          gameDataPath: $('#settingsGamePath').value,
          cliPath: $('#settingsCliPath').value,
          mapPath: $('#settingsMapPath').value,
          indexName: $('#settingsIndexName').value,
          containerIndexPath: $('#settingsContainerIndex')?.value || '',
          outputDirectories: outputs,
          outputDirectory: outputs[state.settings.game] || '',
        },
      });
      closeSettings();
      APP.toast('设置已保存');
      return;
    }

    if (hit('#welcomeDetect')) {
      stop(event);
      call('games.scan');
      return;
    }

    if (hit('#welcomePickOutput')) {
      stop(event);
      call('settings.pick', { target: 'output' });
      return;
    }

    if (hit('#welcomeBuildIndex')) {
      stop(event);
      state.wizardDismissed = true;
      updateWizard();
      runTask('index');
      return;
    }

    if (hit('#welcomeClose')) {
      stop(event);
      state.wizardDismissed = true;
      updateWizard();
      return;
    }

    const filterChip = event.target.closest('#imageFilters [data-filter]');
    if (filterChip) {
      stop(event);
      APP.state.imageFilter = filterChip.dataset.filter;
      loadImages($('#imageSearch').value || '', 0, filterChip.dataset.filter);
      return;
    }

    const pageButton = event.target.closest('#imagePager [data-page]');
    if (pageButton) {
      stop(event);
      const step = pageButton.dataset.page === 'next' ? 1 : -1;
      loadImages($('#imageSearch').value || '', Math.max(0, state.imageOffset + step * IMAGE_PAGE), state.category);
      return;
    }

    if (hit('#taskCancel')) {
      stop(event);
      call('task.cancel');
      APP.pushLog('已请求取消任务…');
      APP.setStatus('正在取消…', true);
      return;
    }

    if (hit('#buildIndex')) {
      stop(event);
      runTask('index');
      return;
    }

    if (hit('#autoDetect')) {
      stop(event);
      call('games.scan');
      APP.toast('正在查找已安装的游戏…');
      return;
    }

    if (hit('#pickPath')) {
      stop(event);
      call('settings.pick', { target: 'game' });
      return;
    }

    if (hit('#outDir')) {
      stop(event);
      call('settings.pick', { target: 'output' });
      return;
    }

    const preset = hit('[data-preset]');
    if (preset) {
      stop(event);
      const action = preset.dataset.preset;
      if (action === 'images') {
        if (APP.state.gameId === 'ZZZ') {
          APP.pushLog('绝区零的容器是数字 ID，没有 spriteoutput/ui 路径；先导出全部 Sprite（44,661 个，耗时较长）');
          runTask('images', { types: ['Sprite'] });
        } else {
          runTask('images', { containers: ['spriteoutput/ui'] });
        }
      } else {
        APP.switchTab('assets');
        APP.toast('先在资源浏览里筛出想要的资源，勾选后点「导出所选 PNG」');
        $('#assetName')?.focus();
      }
      return;
    }

    const game = hit('.game');
    if (game) {
      const id = game.dataset.game;
      if (id === APP.state.gameId) {
        return;
      }

      // 先同步改掉当前游戏，app.js 的 mock 分支看到相同 id 会直接返回。
      APP.state.gameId = id;
      APP.renderGames();
      call('game.switch', { game: id });
    }
  }, true);

  document.addEventListener('input', (event) => {
    if (event.target.id !== 'imageSearch') {
      return;
    }

    event.stopImmediatePropagation();
    clearTimeout(state.timer);

    const value = event.target.value;
    state.timer = setTimeout(() => {
      loadImages(value, 0, state.category);
    }, 300);
  }, true);

  function stop(event) {
    event.preventDefault();
    event.stopImmediatePropagation();
  }

  // 图库大图翻到本页边界时，由宿主去取相邻页。
  APP.pageTurn = pageTurn;

  // 调试/自动化入口：CDP 探针可以用它触发真实任务与查询。
  window.HOST = {
    runTask,
    loadImages,
    call,
    state,
    diff: () => call('diff.run'),
    searchAssets,
    refreshAssets,
    exportSelectedAssets,
    exportMatchingAssets,
    toggleAssetSelection,
    clearAssetSelection,
    setPageSelection,
  };

  webview.postMessage({ type: 'hello' });
})();
