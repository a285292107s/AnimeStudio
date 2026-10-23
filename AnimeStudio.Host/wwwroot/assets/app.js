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
    tab: 'assets',
    gameId: 'SR',
    imageQuery: '',
    imageFilter: 'all',
    images: [],
    preview: null,
    panelHidden: false,
    indexing: false,
    gallerySize: 'md',
    galleryLoaded: false,
    // 大图预览：items 是打开时那一页的图片，index 是当前位置，scale 相对「适应窗口」。
    viewer: null,
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
    state.galleryLoaded = true;
    state.gallerySize = readGallerySize();
    applyGallerySize();

    // 资源浏览只在 WebView2 宿主里有真实数据；浏览器预览退回图库。
    if (!window.chrome?.webview) {
      const assetsTab = $('.tab[data-tab="assets"]');
      if (assetsTab) assetsTab.hidden = true;
      switchTab('images');
    }

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
      ['图库图片', stats.uiSprites],
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
      { name: 'UI 图库 · spriteoutput/ui', count: stats.uiSprites, action: 'images', hint: '直接提取' },
      { name: '按筛选结果导出', count: null, action: 'assets', hint: '资源浏览里勾选' },
    ]
      .map((preset) => `
        <li class="preset">
          <div class="preset__main">
            <span class="preset__name">${preset.name}</span>
            <span class="preset__count">${preset.count === null ? preset.hint : fmt(preset.count) + ' 条'}</span>
          </div>
          <button class="btn btn--xs" data-preset="${preset.action}">${preset.action === 'images' ? '提取' : '去筛选'}</button>
        </li>`)
      .join('');
  }

  function bindPresets() {
    $('#presets').addEventListener('click', (event) => {
      const button = event.target.closest('[data-preset]');
      if (!button) return;
      const action = button.dataset.preset;
      if (action === 'assets') {
        switchTab('assets');
        toast('筛出想要的资源，勾选后点「导出所选 PNG」');
        $('#assetName')?.focus();
        return;
      }

      switchTab('images');
      simulateExport(state.images.length, 'images');
    });
  }

  function renderTabs() {
    const imageCount = $('#tabImageCount');
    if (imageCount) {
      imageCount.textContent = state.galleryLoaded ? fmt(state.data.stats.uiSprites) : '—';
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
      const noneAtAll = state.images.length === 0;
      grid.innerHTML = noneAtAll
        ? '<div class="empty"><b>还没有导出过图片</b><span>在「资源浏览」里筛资源、导出 PNG，这里只负责预览结果</span></div>'
        : '<div class="empty"><b>没有匹配的图片</b><span>换个关键词或筛选条件</span></div>';
      $('#imageCount').textContent = '0 张';
      return;
    }

    grid.innerHTML = rows
      .map((image, index) => `
          <article class="card" data-index="${index}" data-id="${escapeAttr(image.id)}" tabindex="0" role="button"
                   aria-label="预览 ${escapeAttr(image.name)}">
            <div class="card__thumb">
              <img src="${image.thumb || image.file}" alt="" loading="lazy" decoding="async">
            </div>
            <div class="card__meta">
              <span class="card__name" title="${escapeAttr(image.name)}">${escapeHtml(image.name)}</span>
              <span class="card__dim">${image.w}×${image.h}</span>
            </div>
          </article>`)
      .join('');

    $('#imageCount').textContent = `${fmt(rows.length)} 张`;

    // 大图打开时换页/换筛选：保持预览对象和当前页对应。
    if (state.viewer) {
      state.viewer.items = rows;
      state.viewer.index = Math.min(state.viewer.index, rows.length - 1);
    }
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
          在「资源浏览」里勾选要导出的资源；<br>在「UI 图库」里点图片看大图
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
      const position = state.viewer ? `${state.viewer.index + 1} / ${state.viewer.items.length}` : '';
      body.innerHTML = `
        <div class="insp__thumb"><img src="${image.file}" alt="${escapeAttr(image.name)}"></div>
        <h3 class="insp__title">${escapeHtml(image.name)}</h3>
        <p class="insp__sub">${image.kind || 'Sprite'} · ${image.w} × ${image.h} px${image.kb ? ' · ' + image.kb + ' KB' : ''}${position ? ' · ' + position : ''}</p>
        <dl class="meta">
          ${metaRow('容器', image.container || '—')}
          ${metaRow('来源 block', image.block ? image.block.split('/').pop() : '—')}
          ${metaRow('完整路径', (image.container ? image.container + '/' : '') + image.name)}
        </dl>
        <div class="insp__actions">
          <button class="btn btn--xs" data-act="view-open">打开大图</button>
          <button class="btn btn--xs btn--ghost" data-act="view-prev">上一张</button>
          <button class="btn btn--xs btn--ghost" data-act="view-next">下一张</button>
          <button class="btn btn--xs btn--ghost" data-act="copy-path">复制路径</button>
        </div>`;
    }

    if (preview.kind === 'assets') {
      // 资源浏览的「购物车」：勾中的行按名称去重后再导出。
      const names = preview.names || [];
      body.innerHTML = `
        <h3 class="insp__title">已选 ${fmt(preview.count || names.length)} 项</h3>
        <p class="insp__sub">导出时按名称去重，单次最多 ${preview.limit || 400} 个</p>
        <ul class="insp__list">
          ${names.map((name) => `<li title="${escapeAttr(name)}">${escapeHtml(name)}</li>`).join('')}
        </ul>
        <div class="insp__actions">
          <button class="btn btn--xs" data-act="assets-export">导出所选 PNG</button>
          <button class="btn btn--xs btn--ghost" data-act="assets-clear">清空选择</button>
        </div>`;
    }

    body.onclick = (event) => {
      const button = event.target.closest('[data-act]');
      if (!button) return;
      const action = button.dataset.act;
      const image = state.preview?.item;

      switch (action) {
        case 'view-open':
          openViewerAtId(image.id);
          break;
        case 'view-prev':
          stepViewer(-1);
          break;
        case 'view-next':
          stepViewer(1);
          break;
        case 'copy-path':
          copy(`${image.container ? image.container + '/' : ''}${image.name}`, '路径已复制');
          break;
        case 'assets-export':
          window.HOST?.exportSelectedAssets?.();
          break;
        case 'assets-clear':
          window.HOST?.clearAssetSelection?.();
          break;
        default:
          break;
      }
    };
  }

  function metaRow(label, value) {
    return `<div class="meta__row"><dt>${label}</dt><dd>${escapeHtml(String(value))}</dd></div>`;
  }

  /* ---------- 大图预览 ---------- */

  function openViewerAt(index) {
    const rows = currentImages();
    if (!rows.length) {
      return;
    }

    const clamped = Math.max(0, Math.min(rows.length - 1, Number.isFinite(index) ? index : 0));
    state.viewer = { items: rows, index: clamped, scale: 1, x: 0, y: 0 };
    $('#viewer').hidden = false;
    document.body.classList.add('is-viewing');
    paintViewer();
  }

  function openViewerAtId(id) {
    const rows = currentImages();
    const index = rows.findIndex((image) => image.id === id);
    openViewerAt(index >= 0 ? index : 0);
  }

  function closeViewer() {
    if (!state.viewer) {
      return;
    }

    state.viewer = null;
    $('#viewer').hidden = true;
    $('#viewerImage').src = '';
    document.body.classList.remove('is-viewing');
  }

  function paintViewer() {
    const viewer = state.viewer;
    if (!viewer) {
      return;
    }

    const image = viewer.items[viewer.index];
    if (!image) {
      return;
    }

    state.preview = { kind: 'image', item: image };
    renderInspector();

    $('#viewerTitle').textContent = image.name;
    $('#viewerMeta').textContent = [
      image.kind || 'Sprite',
      image.w && image.h ? `${image.w} × ${image.h}` : '',
      image.kb ? `${image.kb} KB` : '',
      image.container || '',
    ].filter(Boolean).join(' · ');
    $('#viewerIndex').textContent = `${viewer.index + 1} / ${viewer.items.length}`;

    const img = $('#viewerImage');
    viewer.scale = 1;
    viewer.x = 0;
    viewer.y = 0;
    img.src = image.file;
    img.alt = image.name;
    applyViewerTransform();
  }

  function applyViewerTransform() {
    const viewer = state.viewer;
    const img = $('#viewerImage');
    if (!viewer || !img) {
      return;
    }

    img.style.transform = `translate(${viewer.x}px, ${viewer.y}px) scale(${viewer.scale})`;
    img.classList.toggle('is-zoomed', viewer.scale > 1.001);
    $('#viewerZoomReset').textContent = viewer.scale > 1.001
      ? `${Math.round(viewer.scale * 100)}%`
      : '适应';
  }

  function zoomViewerBy(factor) {
    const viewer = state.viewer;
    if (!viewer) {
      return;
    }

    const next = Math.max(1, Math.min(16, viewer.scale * factor));
    if (Math.abs(next - viewer.scale) < 0.001) {
      return;
    }

    viewer.scale = next;
    if (next === 1) {
      viewer.x = 0;
      viewer.y = 0;
    }
    applyViewerTransform();
  }

  // 1:1：把图片放到原始像素大小（相对于当前「适应」比例换算）。
  function zoomViewerActual() {
    const viewer = state.viewer;
    const img = $('#viewerImage');
    if (!viewer || !img || !img.naturalWidth) {
      return;
    }

    const base = img.getBoundingClientRect().width / viewer.scale;
    if (base <= 0) {
      return;
    }

    viewer.scale = Math.max(1, Math.min(16, img.naturalWidth / base));
    viewer.x = 0;
    viewer.y = 0;
    applyViewerTransform();
  }

  async function stepViewer(delta) {
    const viewer = state.viewer;
    if (!viewer) {
      return;
    }

    const next = viewer.index + delta;
    if (next >= 0 && next < viewer.items.length) {
      viewer.index = next;
      paintViewer();
      return;
    }

    // 页码边界交给宿主：它取上一页/下一页，取到后再打开对应端点的图片。
    if (typeof window.APP?.pageTurn === 'function') {
      const moved = await window.APP.pageTurn(delta);
      if (moved) {
        return;
      }
    }

    toast(delta > 0 ? '已经是最后一张了' : '已经是第一张了');
  }

  /* ---------- 图库缩略图密度 ---------- */

  function readGallerySize() {
    try {
      const saved = localStorage.getItem('anime.gallery.size');
      return ['sm', 'md', 'lg'].includes(saved) ? saved : 'md';
    } catch {
      return 'md';
    }
  }

  function applyGallerySize() {
    const grid = $('#imageGrid');
    if (!grid) {
      return;
    }

    grid.classList.toggle('grid--sm', state.gallerySize === 'sm');
    grid.classList.toggle('grid--lg', state.gallerySize === 'lg');
    $$('#imageDensity [data-size]').forEach((button) => {
      button.classList.toggle('is-active', button.dataset.size === state.gallerySize);
    });
  }

  function setGallerySize(size) {
    state.gallerySize = size;
    applyGallerySize();
    try {
      localStorage.setItem('anime.gallery.size', size);
    } catch {
      /* 隐私模式下写不进去，忽略 */
    }
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
      openViewerAt(Number(card.dataset.index));
    });

    $('#imageDensity').addEventListener('click', (event) => {
      const button = event.target.closest('[data-size]');
      if (!button) return;
      setGallerySize(button.dataset.size);
    });

    $('#viewerClose').addEventListener('click', closeViewer);
    $('#viewerPrev').addEventListener('click', () => stepViewer(-1));
    $('#viewerNext').addEventListener('click', () => stepViewer(1));
    $('#viewerZoomIn').addEventListener('click', () => zoomViewerBy(1.25));
    $('#viewerZoomOut').addEventListener('click', () => zoomViewerBy(1 / 1.25));
    $('#viewerZoomReset').addEventListener('click', () => {
      if (state.viewer && state.viewer.scale > 1.001) {
        state.viewer.scale = 1;
        state.viewer.x = 0;
        state.viewer.y = 0;
        applyViewerTransform();
      } else {
        zoomViewerActual();
      }
    });

    bindViewerGestures();

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

    $('#logClear').addEventListener('click', () => { $('#logList').innerHTML = ''; });

    $('#groupMode').addEventListener('change', (event) => {
      pushLog(`导出分组：${event.target.options[event.target.selectedIndex].text}`);
    });

    document.addEventListener('keydown', (event) => {
      // 大图打开时键盘交给预览：翻页、缩放、关闭。
      if (state.viewer) {
        if (event.key === 'Escape') { event.preventDefault(); closeViewer(); return; }
        if (event.key === 'ArrowLeft') { event.preventDefault(); stepViewer(-1); return; }
        if (event.key === 'ArrowRight') { event.preventDefault(); stepViewer(1); return; }
        if (event.key === '+' || event.key === '=') { event.preventDefault(); zoomViewerBy(1.25); return; }
        if (event.key === '-') { event.preventDefault(); zoomViewerBy(1 / 1.25); return; }
        if (event.key === '0') { event.preventDefault(); zoomViewerActual(); return; }
      }

      if (event.key === '/' && document.activeElement.tagName !== 'INPUT') {
        event.preventDefault();
        if (state.tab === 'images') $('#imageSearch').focus();
        else $('#assetName')?.focus();
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
    });
  }

  function bindViewerGestures() {
    const stage = $('#viewerStage');
    if (!stage) {
      return;
    }

    let drag = null;

    stage.addEventListener('wheel', (event) => {
      if (!state.viewer) return;
      event.preventDefault();
      zoomViewerBy(event.deltaY < 0 ? 1.15 : 1 / 1.15);
    }, { passive: false });

    stage.addEventListener('dblclick', () => {
      if (state.viewer?.scale > 1.001) {
        state.viewer.scale = 1;
        state.viewer.x = 0;
        state.viewer.y = 0;
        applyViewerTransform();
      } else {
        zoomViewerActual();
      }
    });

    stage.addEventListener('pointerdown', (event) => {
      if (!state.viewer || state.viewer.scale <= 1.001) return;
      drag = { x: event.clientX, y: event.clientY, ox: state.viewer.x, oy: state.viewer.y, id: event.pointerId };
      stage.setPointerCapture(event.pointerId);
      stage.classList.add('is-panning');
    });

    stage.addEventListener('pointermove', (event) => {
      if (!drag || !state.viewer) return;
      state.viewer.x = drag.ox + (event.clientX - drag.x);
      state.viewer.y = drag.oy + (event.clientY - drag.y);
      applyViewerTransform();
    });

    const stopDrag = () => {
      if (!drag) return;
      drag = null;
      stage.classList.remove('is-panning');
    };

    stage.addEventListener('pointerup', stopDrag);
    stage.addEventListener('pointercancel', stopDrag);
    stage.addEventListener('pointerleave', stopDrag);
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
    // 图库预览：宿主在翻页边界上接管（pageTurn），其余全是本地操作。
    openViewerAt,
    openViewerAtId,
    closeViewer,
    stepViewer,
    pageTurn: null,
  };

  boot();
})();
