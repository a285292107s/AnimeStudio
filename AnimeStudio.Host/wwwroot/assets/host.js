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
  };

  let seq = 0;

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

    if (message.requestId && pending.has(message.requestId)) {
      pending.get(message.requestId)(message);
      pending.delete(message.requestId);
      return;
    }

    switch (message.type) {
      case 'welcome':
        onWelcome(message);
        break;
      case 'settings.changed':
        applySettings(message.settings, message.state);
        break;
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
        break;
      case 'task.done':
        onTaskDone(message);
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
        applySettings(message.settings, message.state);
        APP.state.text = [];
        APP.state.images = [];
        APP.renderTextList();
        APP.renderImageGrid();
        loadText('');
        loadImages('');
        APP.toast(`已切换到 ${message.settings.game}`);
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

    if (message.state?.locales?.length) {
      state.locale = message.state.locales[0];
    }

    // 两个游戏现在都走同一套真实管线，去掉 mock 里的「待验证」标记。
    (APP.state.data.games || []).forEach((game) => {
      if (game.status === 'probe') {
        game.status = 'ready';
      }
    });
    APP.renderGames();

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

  function applySettings(settings, hostState) {
    if (settings) {
      state.settings = settings;
    }
    if (hostState) {
      state.hostState = hostState;
    }

    const current = state.hostState || {};

    // Update the numbers first, then render once, then override the mock labels.
    if (current.textTables?.length) {
      const total = current.textTables.reduce((sum, table) => sum + (table.count || 0), 0);
      APP.state.data.stats.textEntries = total;
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
    } else if (Array.isArray(current.textTables)) {
      APP.state.data.stats.textEntries = 0;
    }

    if (typeof current.images === 'number') {
      APP.state.data.stats.uiSprites = current.images;
    }

    paintChrome();
  }

  function paintChrome() {
    APP.renderRail();
    APP.renderTabs();
    APP.renderLanguageSelect();

    const languageSelect = $('#textLang');
    if (languageSelect && state.locale) {
      languageSelect.value = state.locale;
    }

    $('#gamePath').textContent = state.settings.gameDataPath || '未选择游戏目录（点「更换」）';
    $('#gamePath').title = state.settings.gameDataPath || '';
    $('#outDir').textContent = state.settings.outputDirectory || '未选择输出目录（点击选择）';
    $('#outDir').title = state.settings.outputDirectory || '';

    const ready = Boolean(state.hostState?.mapExists);
    $('#indexChipText').textContent = ready ? '索引就绪' : '未建索引';
    $('#indexChip').classList.toggle('is-busy', !ready);
    $('#indexMeta').textContent = ready
      ? `AssetMap 就绪 · ${state.settings.mapPath || ''}`
      : '还没有索引：点上面的「重建索引」';
    $('#indexMeta').title = state.settings.mapPath || '';

    // 统计来自索引（Host 后台扫描）；结果未到时用 — 占位，不显示 mock 数字。
    const values = document.querySelectorAll('#stats dd');
    const mapStats = state.mapStats;
    const put = (index, text) => {
      if (values[index]) {
        values[index].textContent = text;
      }
    };

    put(0, mapStats ? fmt(mapStats.totalEntries) : '—');
    put(1, mapStats ? fmt(mapStats.sourceFiles) : '—');
    put(4, mapStats ? fmt(mapStats.sprites) : '—');
    put(5, mapStats ? fmt(mapStats.textures) : '—');
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

    APP.state.data.stats.textEntries = response.total || 0;
    paintChrome();
    APP.renderTextList();

    const scroll = $('#textScroll');
    if (scroll) {
      scroll.scrollTop = 0;
    }

    updatePagers();
  }

  async function loadImages(query, offset = 0) {
    const response = await call('images.query', {
      query: query || '',
      offset: Math.max(0, offset),
      limit: IMAGE_PAGE,
    });

    if (!response || response.type !== 'images.page') {
      return;
    }

    state.imageOffset = response.offset || 0;
    state.imageTotal = response.total || 0;

    APP.state.images = (response.items || []).map((item) => ({
      id: item.relativePath,
      name: item.name,
      file: item.url,
      w: item.width,
      h: item.height,
      kb: Math.round((item.bytes || 0) / 1024),
      container: item.relativePath.replace(/\/[^/]+$/, ''),
      kind: 'Sprite',
      block: '',
    }));

    // Keep a name registry so a selection spread over several pages can still be exported.
    (response.items || []).forEach((item) => {
      state.imageNames.set(item.relativePath, item.name);
    });

    APP.state.data.stats.uiSprites = response.total || 0;
    paintChrome();
    $('#imageFilters').style.display = 'none';
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
      imagePager.hidden = !show;
      if (show) {
        const from = state.imageOffset + 1;
        const to = Math.min(state.imageTotal, state.imageOffset + IMAGE_PAGE);
        $('#imagePageInfo').textContent = `${fmt(from)}-${fmt(to)} / ${fmt(state.imageTotal)}`;
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
    APP.setStatus(`${label(message.kind)}进行中…`, true);
    APP.pushLog(`开始${label(message.kind)}`);
    showProgress(`正在${label(message.kind)}…`);
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

    const textElement = $('#indexProgressText');
    if (textElement) {
      textElement.textContent = text;
    }

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

  function onProgress(message) {
    const progress = $('#indexProgress');
    if (progress && progress.hidden) {
      showProgress('进行中…');
    }

    const bar = $('#indexBar');
    const textElement = $('#indexProgressText');
    const detail = shortMessage(message.message);

    if (message.fraction === null || message.fraction === undefined) {
      if (bar) {
        bar.style.width = '100%';
        bar.parentElement.classList.add('is-indeterminate');
      }

      if (textElement) {
        textElement.textContent = detail || '进行中…';
      }
    } else {
      if (bar) {
        bar.style.width = `${Math.round(message.fraction * 100)}%`;
        bar.parentElement.classList.remove('is-indeterminate');
      }

      if (textElement) {
        textElement.textContent = `${Math.round(message.fraction * 100)}%  ${detail}`.trim();
      }
    }

    APP.setStatus(textElement?.textContent || '进行中…', true);
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
    hideProgress();
    APP.setStatus('就绪');
  }

  function onTaskDone(message) {
    endTask();

    const pending = state.pendingExport;
    state.pendingExport = null;
    const suffix = pending?.count ? `（${pending.count} 张所选）` : '';

    APP.toast(`${label(message.kind)}${message.ok ? '完成' + suffix : '失败（exit ' + message.exitCode + '）'}`);
    APP.pushLog(`${label(message.kind)}结束：exit ${message.exitCode}${suffix}`);

    applySettings(null, message.state);

    if (message.kind === 'text') {
      loadText('');
    } else if (message.kind === 'images') {
      loadImages('');
    }
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

    const pageButton = event.target.closest('#textPager [data-page], #imagePager [data-page]');
    if (pageButton) {
      stop(event);
      const isTextPager = pageButton.closest('#textPager') !== null;
      const step = pageButton.dataset.page === 'next' ? 1 : -1;

      if (isTextPager) {
        loadText($('#textSearch').value || '', Math.max(0, state.textOffset + step * TEXT_PAGE));
      } else {
        loadImages($('#imageSearch').value || '', Math.max(0, state.imageOffset + step * IMAGE_PAGE));
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
        runTask('images', { containers: ['spriteoutput/ui'] });
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
        loadImages(value);
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

  webview.postMessage({ type: 'hello' });
})();
