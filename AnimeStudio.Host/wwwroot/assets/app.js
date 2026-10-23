/* AnimeStudio · Host UI
   纯原生 JS，无框架。所有数据来自 mock/data.json（真实探测产物），
   接入宿主后由 WebView2 消息通道替换数据来源。 */

(() => {
  'use strict';

  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));
  const nf = new Intl.NumberFormat('zh-CN');
  const fmt = (n) => nf.format(n);

  const state = {
    data: null,
    tab: 'images',
    gameId: 'SR',
    imageQuery: '',
    imageFilter: 'all',
    images: [],
    selectedImages: new Set(),
    preview: null,
    panelHidden: false,
    indexing: false,
  };

  /* ---------- 启动 ---------- */

  async function boot() {
    try {
      const response = await fetch('mock/data.json', { cache: 'no-store' });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      state.data = await response.json();
    } catch (error) {
      document.querySelector('.main').innerHTML =
        '<div class="empty"><b>演示数据未生成</b>' +
        '<span>运行 tools/make-host-mock.ps1 生成 mock 数据；<br>' +
        '或用 AnimeStudio.Host.exe 直接连真实数据</span></div>';
      $('#statusText').textContent = '缺少 mock 数据';
      return;
    }

    state.images = state.data.images.slice();

    renderGames();
    renderRail();
    renderTabs();
    renderImageGrid();
    renderInspector();
    bindEvents();
    attachHostBridge();
    pushLog(window.chrome?.webview ? '就绪' : '就绪：预览数据（mock）', 'info');
    setStatus('就绪');
    window.dispatchEvent(new Event('app:ready'));
  }

  /* ---------- 顶栏 / 左栏 ---------- */

  function renderGames() {
    const host = $('#games');
    host.innerHTML = state.data.games
      .map((game) => `
        <button class="game ${game.id === state.gameId ? 'is-active' : ''}" data-game="${game.id}">
          ${game.name}
          <span class="game__state ${game.status === 'probe' ? 'is-probe' : ''}">
            ${game.status === 'probe' ? '待验证' : game.short}
          </span>
        </button>`)
      .join('');
  }

  function bindGameSwitch() {
    $('#games').addEventListener('click', (event) => {
      const button = event.target.closest('.game');
      if (!button) return;
      const id = button.dataset.game;
      if (id === state.gameId) return;

      const game = state.data.games.find((g) => g.id === id);
      if (game.status === 'probe') {
        toast('绝区零索引尚未就绪：先在设置里选好目录并建索引');
        pushLog('绝区零：需要先建索引', 'warn');
        return;
      }

      state.gameId = id;
      renderGames();
      toast(`已切换到 ${game.name}`);
      pushLog(`切换游戏：${game.name}`);
    });
  }

  function renderRail() {
    const { game, stats } = state.data;
    $('#gamePath').textContent = game.path;
    $('#gamePath').title = game.path;
    $('#indexMeta').textContent =
      `v${game.version} · 索引 ${(stats.indexBytes / 1024 / 1024 / 1024).toFixed(2)} GB`;
    $('#indexChipText').textContent = `索引就绪 v${game.version}`;
    $('#outGameLabel').textContent = `当前游戏：${game.name}`;
    $('#outDir').textContent = `mock/${game.id.toLowerCase()}-out`;

    const statItems = [
      ['资产总数', stats.assets],
      ['Block 数', stats.blocks],
      ['UI 精灵', stats.uiSprites],
      ['Sprite 总数', stats.sprites],
      ['Texture2D', stats.textures],
    ];

    $('#stats').innerHTML = statItems
      .map(([label, value]) => `
        <div class="stat">
          <dt>${label}</dt>
          <dd>${fmt(value)}</dd>
        </div>`)
      .join('');

    $('#presets').innerHTML = [
      { name: 'UI 图库 · spriteoutput/ui', count: stats.uiSprites, action: 'images' },
      { name: '关键：只选我勾中的', count: null, action: 'selection' },
    ]
      .map((preset) => `
        <li class="preset">
          <div class="preset__main">
            <span class="preset__name">${preset.name}</span>
            <span class="preset__count">${preset.count === null ? '手动选择' : fmt(preset.count) + ' 条'}</span>
          </div>
          <button class="btn btn--xs" data-preset="${preset.action}">提取</button>
        </li>`)
      .join('');
  }

  function bindPresets() {
    $('#presets').addEventListener('click', (event) => {
      const button = event.target.closest('[data-preset]');
      if (!button) return;
      const action = button.dataset.preset;
      if (action === 'selection') {
        toast('切到 UI 图库，勾选你要的图片');
        switchTab('images');
        return;
      }
      switchTab('images');
      simulateExport(state.images.length, 'images');
    });
  }

  function renderTabs() {
    const imageCount = $('#tabImageCount');
    if (imageCount) {
      imageCount.textContent = fmt(state.data.stats.uiSprites);
    }
  }

  /* ---------- 图库 ---------- */

  function currentImages() {
    const q = state.imageQuery.trim().toLowerCase();
    return state.images.filter((image) => {
      const inFilter = state.imageFilter === 'all' || image.container.includes(state.imageFilter);
      const inQuery = !q || image.name.toLowerCase().includes(q);
      return inFilter && inQuery;
    });
  }

  function renderImageGrid() {
    const grid = $('#imageGrid');
    const rows = currentImages();

    renderChips();

    if (!rows.length) {
      grid.innerHTML = '<div class="empty"><b>没有匹配的图片</b><span>换个关键词或筛选条件</span></div>';
      $('#imageCount').textContent = '0 张';
      return;
    }

    grid.innerHTML = rows
      .map((image) => {
        const selected = state.selectedImages.has(image.id);
        return `
          <article class="card ${selected ? 'is-selected' : ''}" data-id="${image.id}" tabindex="0">
            <div class="card__thumb">
              <img src="${image.thumb || image.file}" alt="${escapeAttr(image.name)}" loading="lazy">
            </div>
            <div class="card__meta">
              <span class="card__name" title="${escapeAttr(image.name)}">${escapeHtml(image.name)}</span>
              <span class="card__dim">${image.w}×${image.h}</span>
            </div>
            <span class="card__check" aria-hidden="true">
              <svg viewBox="0 0 12 12"><path d="M2 6.4L4.6 9 10 3.4"/></svg>
            </span>
          </article>`;
      })
      .join('');

    const selectedCount = state.selectedImages.size;
    $('#imageCount').textContent = `${fmt(rows.length)} 张` + (selectedCount ? ` / 已选 ${selectedCount}` : '');
  }

  function renderChips() {
    const options = [{ id: 'all', label: '全部' }, ...state.data.containers.map((entry) => ({
      id: entry.id.replace('spriteoutput/', '').replace('ui/', ''),
      label: entry.id,
      match: entry.id,
    }))];

    $('#imageFilters').innerHTML = options
      .map((option) => {
        const active = state.imageFilter === (option.match || 'all') ||
          (state.imageFilter === 'all' && option.id === 'all');
        return `<button class="filter ${active ? 'is-active' : ''}" data-filter="${option.match || 'all'}">${option.label}</button>`;
      })
      .join('');
  }

  /* ---------- 详情面板 ---------- */

  function inspectorIndexRows() {
    const info = state.data.indexInfo;
    const rows = [
      metaRow('游戏', state.data.game?.name || '—'),
      metaRow('数据版本', info?.version || state.data.game?.version || '—'),
    ];

    const bytes = info?.mapBytes ?? state.data.stats.indexBytes;
    rows.push(metaRow('地图大小', bytes ? (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB' : '—'));
    rows.push(metaRow('地图路径', info?.mapPath || '—'));
    return rows.join('');
  }

  function renderInspector() {
    const body = $('#panelBody');
    const preview = state.preview;

    if (!preview) {
      body.innerHTML = `
        <div class="panel__empty">
          选中左侧任意一条<br>查看详情与导出选项
        </div>
        <div class="panel__section">
          <h3 class="label">索引信息 <span>INDEX</span></h3>
          <dl class="meta">
            ${inspectorIndexRows()}
          </dl>
        </div>`;
      return;
    }

    if (preview.kind === 'image') {
      const image = preview.item;
      body.innerHTML = `
        <div class="insp__thumb"><img src="${image.file}" alt="${escapeAttr(image.name)}"></div>
        <h3 class="insp__title">${escapeHtml(image.name)}</h3>
        <p class="insp__sub">${image.kind || 'Sprite'} · ${image.w} × ${image.h} px${image.kb ? ' · ' + image.kb + ' KB' : ''}</p>
        <dl class="meta">
          ${metaRow('容器', image.container || '—')}
          ${metaRow('来源 block', image.block ? image.block.split('/').pop() : '—')}
          ${metaRow('完整路径', (image.container ? image.container + '/' : '') + image.name)}
        </dl>
        <div class="insp__actions">
          <button class="btn btn--xs" data-act="select-image">${state.selectedImages.has(image.id) ? '取消勾选' : '勾选此图'}</button>
          <button class="btn btn--xs" data-act="export-image">导出 PNG</button>
          <button class="btn btn--xs btn--ghost" data-act="copy-path">复制路径</button>
        </div>`;
    }

    body.onclick = (event) => {
      const button = event.target.closest('[data-act]');
      if (!button) return;
      const action = button.dataset.act;
      const image = state.preview?.item;

      switch (action) {
        case 'select-image':
          toggleImage(image.id);
          break;
        case 'export-image':
          simulateExport(1, 'images');
          break;
        case 'copy-path':
          copy(`${image.container}/${image.name}`, '路径已复制');
          break;
        default:
          break;
      }
    };
  }

  function metaRow(label, value) {
    return `<div class="meta__row"><dt>${label}</dt><dd>${escapeHtml(String(value))}</dd></div>`;
  }

  /* ---------- 选中 ---------- */

  function toggleImage(id) {
    if (state.selectedImages.has(id)) {
      state.selectedImages.delete(id);
    } else {
      state.selectedImages.add(id);
    }

    state.preview = { kind: 'image', item: state.images.find((image) => image.id === id) };
    renderImageGrid();
    renderInspector();
  }

  /* ---------- 交互 ---------- */

  function bindEvents() {
    bindGameSwitch();
    bindPresets();
    $$('.tab').forEach((tab) => tab.addEventListener('click', () => switchTab(tab.dataset.tab)));

    $('#imageSearch').addEventListener('input', debounce((event) => {
      state.imageQuery = event.target.value;
      renderImageGrid();
    }, 120));

    $('#imageFilters').addEventListener('click', (event) => {
      const button = event.target.closest('[data-filter]');
      if (!button) return;
      state.imageFilter = button.dataset.filter;
      renderImageGrid();
    });

    $('#imageGrid').addEventListener('click', (event) => {
      const card = event.target.closest('.card');
      if (!card) return;
      toggleImage(card.dataset.id);
    });

    $('#buildIndex').addEventListener('click', simulateIndex);
    $('#pickPath').addEventListener('click', () => toast('原型阶段：目录选择接入宿主后可用'));
    $('#togglePanel').addEventListener('click', () => {
      state.panelHidden = !state.panelHidden;
      document.querySelector('.app').classList.toggle('is-panel-hidden', state.panelHidden);
    });
    $('#panelClose').addEventListener('click', () => {
      state.panelHidden = true;
      document.querySelector('.app').classList.add('is-panel-hidden');
    });

    $('#imageExport').addEventListener('click', () => {
      const count = state.selectedImages.size;
      if (!count) {
        toast('还没有勾选图片：点卡片可以勾选');
        return;
      }
      simulateExport(count, 'images');
    });
    $('#imageClear').addEventListener('click', () => {
      state.selectedImages.clear();
      renderImageGrid();
      toast('已清空选择');
    });

    $('#logClear').addEventListener('click', () => { $('#logList').innerHTML = ''; });

    $('#groupMode').addEventListener('change', (event) => {
      pushLog(`导出分组：${event.target.options[event.target.selectedIndex].text}`);
    });

    document.addEventListener('keydown', (event) => {
      if (event.key === '/' && document.activeElement.tagName !== 'INPUT') {
        event.preventDefault();
        $('#imageSearch').focus();
      }
      if (event.key === 'Escape') {
        if (document.activeElement.tagName === 'INPUT') {
          document.activeElement.value = '';
          document.activeElement.dispatchEvent(new Event('input'));
          document.activeElement.blur();
        } else {
          state.preview = null;
          renderInspector();
        }
      }
      if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
        event.preventDefault();
        $('#imageExport').click();
      }
    });
  }

  function switchTab(tab) {
    state.tab = tab;
    $$('.tab').forEach((el) => {
      const active = el.dataset.tab === tab;
      el.classList.toggle('is-active', active);
      el.setAttribute('aria-selected', String(active));
    });
    $$('.pane').forEach((pane) => {
      const active = pane.dataset.pane === tab;
      pane.classList.toggle('is-active', active);
      pane.hidden = !active;
    });

    if (tab === 'images') renderImageGrid();
  }

  /* ---------- 索引 / 导出模拟 ---------- */

  function simulateIndex() {
    if (state.indexing) return;
    state.indexing = true;

    const button = $('#buildIndex');
    const progress = $('#indexProgress');
    const bar = $('#indexBar');
    const text = $('#indexProgressText');
    button.disabled = true;
    progress.hidden = false;
    $('#indexChipText').textContent = '正在建索引…';
    $('#indexChip').classList.add('is-busy');
    setStatus('扫描 block…', true);

    const steps = [
      { at: 8, label: '枚举 block 文件…' },
      { at: 26, label: '读取资产目录…' },
      { at: 52, label: '写入 AssetMap…' },
      { at: 78, label: '校验索引…' },
      { at: 96, label: '收尾…' },
    ];

    let current = 0;
    const timer = setInterval(() => {
      current = Math.min(100, current + Math.random() * 7 + 2);
      bar.style.width = `${current}%`;
      const step = steps.filter((entry) => current >= entry.at).pop();
      text.textContent = `${step ? step.label : ''} ${current.toFixed(0)}%`;
      setStatus(`${step ? step.label : '建索引'} ${current.toFixed(0)}%`, true);

      if (current >= 100) {
        clearInterval(timer);
        state.indexing = false;
        button.disabled = false;
        setTimeout(() => { progress.hidden = true; bar.style.width = '0%'; }, 600);
        $('#indexChip').classList.remove('is-busy');
        $('#indexChipText').textContent = `索引就绪 v${state.data.game.version}`;
        setStatus('索引完成');
        toast(`索引完成：${fmt(state.data.stats.assets)} 条资产 / ${fmt(state.data.stats.blocks)} 个 block`);
        pushLog(`索引完成：${fmt(state.data.stats.assets)} 条资产，${fmt(state.data.stats.blocks)} 个 block`);
      }
    }, 130);
  }

  function simulateExport(count, kind) {
    if (state.indexing) {
      toast('索引进行中，稍等');
      return;
    }

    const label = 'PNG 图片';
    setStatus(`正在导出 ${fmt(count)} 条${label}…`, true);

    let done = 0;
    const total = count;
    const timer = setInterval(() => {
      done = Math.min(total, done + Math.ceil(total / 8) + 1);
      setStatus(`导出 ${fmt(done)} / ${fmt(total)} ${label}`, true);
      if (done >= total) {
        clearInterval(timer);
        setStatus('就绪');
        toast(`导出完成：${fmt(total)} 条${label} → out/`);
        pushLog(`导出 ${fmt(total)} 条${label}（分组：${$('#groupMode').value}）`);
      }
    }, 160);
  }

  /* ---------- 工具 ---------- */

  function pushLog(message, level = 'info') {
    const list = $('#logList');
    const time = new Date().toLocaleTimeString('zh-CN', { hour12: false });
    const item = document.createElement('li');
    item.className = level === 'warn' ? 'is-warn' : level === 'error' ? 'is-error' : '';
    item.innerHTML = `<time>${time}</time><span class="lvl">${level.toUpperCase()}</span><span>${escapeHtml(message)}</span>`;
    list.appendChild(item);
    list.scrollTop = list.scrollHeight;
  }

  function setStatus(message, busy = false) {
    $('#statusText').textContent = message;
    $('#statusSpinner').hidden = !busy;
  }

  let toastTimer = null;
  function toast(message) {
    const el = $('#toast');
    el.innerHTML = `<span>${escapeHtml(message)}</span>`;
    el.hidden = false;
    requestAnimationFrame(() => el.classList.add('is-visible'));
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
      el.classList.remove('is-visible');
      setTimeout(() => { el.hidden = true; }, 220);
    }, 2600);
  }

  function copy(text, message) {
    navigator.clipboard?.writeText(text).then(
      () => toast(message),
      () => toast('复制失败：浏览器未授权'));
  }

  function showEmpty(selector, message) {
    const el = $(selector);
    if (el) el.innerHTML = `<div class="empty"><b>${message}</b><span>换个关键词试试</span></div>`;
  }

  function debounce(fn, wait) {
    let timer = null;
    return (...args) => {
      clearTimeout(timer);
      timer = setTimeout(() => fn(...args), wait);
    };
  }

  function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, (char) => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[char]));
  }

  const escapeAttr = escapeHtml;

  function attachHostBridge() {
    const webview = window.chrome?.webview;
    if (!webview) {
      $('#modeChip').textContent = '浏览器预览 · mock';
      return;
    }

    $('#modeChip').textContent = 'WebView2 宿主';
    webview.addEventListener('message', (event) => {
      const message = event.data;
      if (message?.type === 'pong') {
        pushLog(`宿主已连接：${message.host} v${message.version}`);
        setStatus('宿主已连接');
      }
    });
    webview.postMessage({ type: 'ping' });
  }

  /* ---------- 对外接口（真实数据模式下由 host.js 接管） ---------- */

  window.APP = {
    state,
    renderGames,
    renderRail,
    renderTabs,
    renderImageGrid,
    renderInspector,
    switchTab,
    toast,
    pushLog,
    setStatus,
  };

  boot();
})();
