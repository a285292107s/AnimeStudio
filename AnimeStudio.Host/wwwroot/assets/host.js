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
  const TEXT_PAGE = 200;
  const IMAGE_PAGE = 120;

  const pending = new Map();
  const state = {
    settings: {},
    locale: '',
    taskRunning: false,
    timer: null,
    hostState: null,
    mapStats: null,
    textOffset: 0,
    textTotal: 0,
    imageOffset: 0,
    imageTotal: 0,
    imageNames: new Map(),
    pendingExport: null,
    category: 'all',
    wizardDismissed: false,
    hintShown: false,
    taskStartedAt: 0,
    taskKind: '',
    progressSamples: [],
  };

  let seq = 0;

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
      case 'game.switched':
        state.mapStats = null;
        state.locale = '';
        state.category = 'all';
        APP.state.imageFilter = 'all';
        applySettings(message.settings, message.state);
        resetOutputViews();
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

    // Leave the locale empty: the host picks zh-CN when installed, instead of the
    // alphabetically first language (which is "ja" for ZZZ).
    state.locale = '';

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

    if (message.state?.textTables?.length) {
      loadText('');
    } else {
      APP.state.text = [];
      APP.state.data.stats.textEntries = 0;
      APP.renderTextList();
    }

    // Images are scanned lazily by the host on the first query, so always ask.
    loadImages('');

    APP.pushLog(`宿主已连接${message.host?.cliPath ? '：' + message.host.cliPath : '（未找到 CLI）'}`);
    if (!message.host?.cliPath) {
      APP.toast('未找到 AnimeStudio.CLI.exe，任务无法执行');
    }
  }

  /// <summary>Clears text/images read from the previous output and asks for the new ones.</summary>
  function resetOutputViews() {
    APP.state.text = [];
    APP.state.images = [];
    APP.state.data.stats.textEntries = 0;
    APP.state.data.stats.uiSprites = 0;
    state.textOffset = 0;
    state.imageOffset = 0;
    state.textTotal = 0;
    state.imageTotal = 0;
    state.category = 'all';
    APP.state.imageFilter = 'all';
    APP.renderTextList();
    APP.renderImageGrid();
    loadText('');
    loadImages('');
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
    if (current.textTables?.length) {
      APP.state.data.languages = current.textTables
        .map((table) => ({
          code: table.locale,
          folder: table.locale,
          label: table.locale,
          entries: table.count,
        }))
        .sort((left, right) => {
          const rank = (code) => (code === 'zh-CN' ? 0 : code === 'zh-TW' ? 1 : code === 'ja' ? 2 : 3);
          return rank(left.code) - rank(right.code);
        });

      // 「文本条目」应表示当前语言的数量，而不是所有语言相加。
      const activeTable = current.textTables.find((table) => table.locale === state.locale) || current.textTables[0];
      APP.state.data.stats.textEntries = activeTable?.count ?? 0;
    } else if (Array.isArray(current.textTables)) {
      APP.state.data.stats.textEntries = 0;
    }

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

    paintChrome();
  }

  function paintChrome() {
    APP.renderGames();
    APP.renderRail();
    APP.renderTabs();
    APP.renderLanguageSelect();
    APP.renderInspector();

    const languageSelect = $('#textLang');
    if (languageSelect && state.locale) {
      languageSelect.value = state.locale;
    }

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
    put(4, mapStats ? fmt(mapStats.sprites) : '—');
    put(5, mapStats ? fmt(mapStats.textures) : '—');

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
    put('#settingsOutputPath', settings.outputDirectory);
    put('#settingsOutputSR', outputPathFor('SR'));
    put('#settingsOutputZZZ', outputPathFor('ZZZ'));

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

  async function loadText(query, offset = 0) {
    const response = await call('text.query', {
      locale: state.locale,
      query: query || '',
      offset: Math.max(0, offset),
      limit: TEXT_PAGE,
    });

    if (!response || response.type !== 'text.page') {
      return;
    }

    if (response.locales?.length) {
      state.locale = response.locale || response.locales[0];
      const select = $('#textLang');
      if (select) {
        select.value = state.locale;
      }
    }

    state.textOffset = response.offset || 0;
    state.textTotal = response.total || 0;

    APP.state.text = (response.rows || []).map(([hash, text]) => ({
      hash,
      text,
      source: response.locale,
    }));

    // Keep the rail/tab number at the full size of the active language, even while searching.
    const activeTable = (state.hostState?.textTables || []).find((table) => table.locale === response.locale);
    APP.state.data.stats.textEntries = activeTable?.count ?? response.total ?? 0;
    paintChrome();
    APP.renderTextList();

    const scroll = $('#textScroll');
    if (scroll) {
      scroll.scrollTop = 0;
    }

    updatePagers();
  }

  async function loadImages(query, offset = 0, category = 'all') {
    const response = await call('images.query', {
      query: query || '',
      category: category || 'all',
      offset: Math.max(0, offset),
      limit: IMAGE_PAGE,
    });

    if (!response || response.type !== 'images.page') {
      return;
    }

    state.category = category || 'all';
    state.imageOffset = response.offset || 0;
    state.imageTotal = response.total || 0;

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

    // Keep a name registry so a selection spread over several pages can still be exported.
    (response.items || []).forEach((item) => {
      state.imageNames.set(item.relativePath, item.name);
    });

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
  }

  function updatePagers() {
    const textPager = $('#textPager');
    if (textPager) {
      const show = state.textTotal > TEXT_PAGE;
      textPager.hidden = !show;
      if (show) {
        const from = state.textOffset + 1;
        const to = Math.min(state.textTotal, state.textOffset + TEXT_PAGE);
        $('#textPageInfo').textContent = `${fmt(from)}-${fmt(to)} / ${fmt(state.textTotal)}`;
        textPager.querySelector('[data-page="prev"]').disabled = state.textOffset <= 0;
        textPager.querySelector('[data-page="next"]').disabled = state.textOffset + TEXT_PAGE >= state.textTotal;
      }
    }

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
    return kind === 'index' ? '建索引' : kind === 'text' ? '文本提取' : 'UI 图导出';
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

    if (message.kind === 'text') {
      loadText('');
    } else if (message.kind === 'images') {
      loadImages('', 0, state.category);
    }
  }

  function onDiff(message) {
    const text = message.text || {};
    const images = message.images || {};

    if (!message.hasPrevious) {
      const version = message.version ? `（版本 ${message.version}）` : '';
      APP.pushLog(`已记录基线快照${version}，下次导出后可查看变化`);
      APP.toast('已记录基线快照');
      return;
    }

    const parts = [];
    if (text.added || text.removed || text.changed) {
      parts.push(`文本 +${text.added} −${text.removed} ~${text.changed}`);
    }
    if (images.added || images.removed || images.changed) {
      parts.push(`图片 +${images.added} −${images.removed} ~${images.changed}`);
    }

    const summary = parts.length ? parts.join('，') : '与上次快照一致';
    APP.toast(summary);
    APP.pushLog(`版本对比 ${message.previousVersion || '上次'} → ${message.version || '当前'}：${summary}`);

    (text.samples || []).slice(0, 10).forEach((sample) => {
      const tag = sample.newText == null ? '−' : sample.oldText == null ? '+' : '~';
      const value = sample.newText || sample.oldText || '';
      APP.pushLog(`${tag} 文本 ${sample.locale} ${sample.key}${value ? ' = ' + String(value).slice(0, 40) : ''}`);
    });

    (images.samples || []).slice(0, 10).forEach((sample) => {
      const tag = sample.kind === 'added' ? '+' : sample.kind === 'removed' ? '−' : '~';
      APP.pushLog(`${tag} 图片 ${sample.path}`);
    });

    state.lastDiff = message;
  }

  /* ---------- 资源浏览（AssetMap 过滤） ---------- */

  async function searchAssets() {
    const filters = {
      name: $('#assetName')?.value || '',
      container: $('#assetContainer')?.value || '',
      source: $('#assetSource')?.value || '',
      types: [$('#assetType')?.value || 'all'].filter((type) => type !== 'all'),
      limit: 300,
    };

    APP.setStatus('正在扫描 AssetMap…', true);
    const response = await call('assets.query', filters);
    APP.setStatus('就绪');

    if (!response || response.type !== 'assets.page') {
      APP.pushLog(`资源查询失败：${response?.message || '未知错误'}`);
      APP.toast('资源查询失败');
      return;
    }

    renderAssets(response);
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

    if (items.length === 0) {
      list.innerHTML = '<div class="asset-table__empty">没有匹配的条目。容器支持子串，例如 <code>spriteoutput/ui/fonts</code>。</div>';
      return;
    }

    const head = '<div class="asset-row asset-row--head"><span>名称</span><span>容器</span><span>类型</span><span>来源</span><span>PathID</span></div>';
    const rows = items
      .map((item) => {
        const pathId = String(item.pathId);
        return `<div class="asset-row" title="${escapeHtml(item.sourcePath || '')}">
          <span class="asset-row__name">${escapeHtml(item.name || '—')}</span>
          <span class="asset-row__container">${escapeHtml(item.container || '—')}</span>
          <span class="asset-row__type">${escapeHtml(item.type || '')}</span>
          <span class="asset-row__source">${escapeHtml(item.source || '')}</span>
          <span class="asset-row__pathid">${escapeHtml(pathId)}</span>
        </div>`;
      })
      .join('');

    list.innerHTML = head + rows;
    APP.pushLog(`资源查询：匹配 ${fmt(response.total)} 条，显示 ${fmt(items.length)} 条（${response.elapsedMs} ms）`);
  }

  function exportAssetFilter() {
    const name = ($('#assetName')?.value || '').trim();
    const container = ($('#assetContainer')?.value || '').trim();
    const source = ($('#assetSource')?.value || '').trim();
    const type = $('#assetType')?.value || 'all';

    if (source) {
      APP.toast('来源过滤暂不支持导出，请用「名称」或「容器」');
      return;
    }

    if (!name && !container) {
      APP.toast('先输入名称或容器，再导出过滤结果');
      return;
    }

    const payload = { types: type === 'all' ? ['Sprite', 'Texture2D'] : [type] };

    if (name) {
      payload.names = [escapeRegex(name)];
    }

    if (container) {
      payload.containers = [escapeRegex(container)];
    }

    runTask('images', payload);
    APP.pushLog(`按过滤条件导出：${[name && `名称=${name}`, container && `容器=${container}`, type !== 'all' && `类型=${type}`].filter(Boolean).join('，')}`);
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
      searchAssets();
      return;
    }

    if (hit('#assetExport')) {
      stop(event);
      exportAssetFilter();
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

    const pageButton = event.target.closest('#textPager [data-page], #imagePager [data-page]');
    if (pageButton) {
      stop(event);
      const isTextPager = pageButton.closest('#textPager') !== null;
      const step = pageButton.dataset.page === 'next' ? 1 : -1;

      if (isTextPager) {
        loadText($('#textSearch').value || '', Math.max(0, state.textOffset + step * TEXT_PAGE));
      } else {
        loadImages($('#imageSearch').value || '', Math.max(0, state.imageOffset + step * IMAGE_PAGE), state.category);
      }
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
      if (action === 'text') {
        runTask('text');
      } else if (action === 'images') {
        if (APP.state.gameId === 'ZZZ') {
          APP.pushLog('绝区零的容器是数字 ID，没有 spriteoutput/ui 路径；先导出全部 Sprite（44,661 个，耗时较长）');
          runTask('images', { types: ['Sprite'] });
        } else {
          runTask('images', { containers: ['spriteoutput/ui'] });
        }
      } else {
        APP.switchTab('images');
        APP.toast('在图库里勾选后点「导出 PNG」');
      }
      return;
    }

    if (hit('#imageExport')) {
      stop(event);

      const selected = [...APP.state.selectedImages];
      if (selected.length > 0) {
        const names = selected
          .map((id) => state.imageNames.get(id))
          .filter(Boolean)
          .map(escapeRegex);

        if (names.length === 0) {
          APP.toast('选中的图片不在当前列表里，请重新勾选');
          return;
        }

        if (names.length > 400) {
          APP.toast(`一次最多导出 400 张（当前 ${names.length} 张），请先缩小选择`);
          return;
        }

        state.pendingExport = { count: names.length };
        runTask('images', { names: [`^(?:${names.join('|')})$`] });
        APP.pushLog(`导出所选：${names.length} 张`);
      } else {
        state.pendingExport = { count: null };
        runTask('images', { containers: ['spriteoutput/ui'] });
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
    if (event.target.id !== 'textSearch' && event.target.id !== 'imageSearch') {
      return;
    }

    event.stopImmediatePropagation();
    clearTimeout(state.timer);

    const isText = event.target.id === 'textSearch';
    const value = event.target.value;

    state.timer = setTimeout(() => {
      if (isText) {
        loadText(value);
      } else {
        loadImages(value, 0, state.category);
      }
    }, 300);
  }, true);

  document.addEventListener('change', (event) => {
    if (event.target.id === 'textLang') {
      state.locale = event.target.value;
      loadText('');
    }
  }, true);

  function stop(event) {
    event.preventDefault();
    event.stopImmediatePropagation();
  }

  // 调试/自动化入口：CDP 探针可以用它触发真实任务与查询。
  window.HOST = {
    runTask,
    loadText,
    loadImages,
    call,
    state,
    diff: () => call('diff.run'),
    searchAssets,
  };

  webview.postMessage({ type: 'hello' });
})();
