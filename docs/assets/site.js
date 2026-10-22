/* AnimeStudio 说明书 · 交互
   三件事：滚动进场、手机目录、两个数据小工具（首页的拆包样本 / 游戏表的搜索）。
   没有脚本时页面必须完整可读：所有隐藏状态都挂在 html.js 上。 */
(function () {
  var root = document.documentElement;
  root.classList.add('js');

  /* ---------- 手机目录 ---------- */
  var toggle = document.querySelector('[data-navtoggle]');
  var nav = document.querySelector('[data-topnav]');
  if (toggle && nav) {
    toggle.addEventListener('click', function () {
      var open = nav.classList.toggle('is-open');
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
    });
    nav.addEventListener('click', function (e) {
      if (e.target.tagName === 'A') nav.classList.remove('is-open');
    });
  }

  /* ---------- 滚动进场 ---------- */
  /* 文档页不再逐页手写 reveal：把正文里成块的东西自动挂上 */
  if (document.body.getAttribute('data-theme') === 'paper') {
    var auto = document.querySelectorAll(
      '.pagehead, .prose > h2, .prose > figure, .prose > .callout, .prose > .table-wrap, .prose > .kv, .prose > .steps, .prose > .facts, .prose > .plate, .prose > .ui, .prose > .faq, .faq > details, .pager'
    );
    [].forEach.call(auto, function (el) { el.classList.add('reveal'); });
  }

  var reveals = [].slice.call(document.querySelectorAll('.reveal'));
  if (reveals.length && 'IntersectionObserver' in window) {
    var io = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            entry.target.classList.add('is-in');
            io.unobserve(entry.target);
          }
        });
      },
      { rootMargin: '0px 0px -12% 0px', threshold: 0.08 }
    );
    reveals.forEach(function (el) { io.observe(el); });
  } else {
    reveals.forEach(function (el) { el.classList.add('is-in'); });
  }

  /* ---------- 首页：拆包样本切换 ---------- */
  var picker = document.querySelector('[data-picker]');
  if (picker && window.AS_GAMES && window.AS_GAMES.profiles) {
    var profiles = {};
    window.AS_GAMES.profiles.forEach(function (p) { profiles[p.t] = p; });
    var rows = [].slice.call(picker.querySelectorAll('[data-key]'));
    var panel = document.querySelector('[data-profile]');

    var render = function (key) {
      var p = profiles[key];
      if (!p || !panel) return;
      panel.innerHTML =
        '<div><p class="profile-name">' + esc(p.name) + '</p>' +
        '<h3>你在工具里选</h3><p>' + esc(p.pick) + '</p>' +
        '<p class="profile-out">' + esc(p.out) + '</p></div>' +
        '<dl><div><dt>你会拿到的文件</dt><dd>' + esc(p.files) + '</dd></div>' +
        '<div><dt>它被保护成什么样</dt><dd>' + esc(p.lock) + '</dd></div>' +
        '<div><dt>钥匙从哪来</dt><dd>' + esc(p.key) + '</dd></div></dl>';
      rows.forEach(function (r) {
        r.setAttribute('aria-pressed', r.getAttribute('data-key') === key ? 'true' : 'false');
      });
    };

    rows.forEach(function (row) {
      row.addEventListener('click', function () {
        render(row.getAttribute('data-key'));
      });
    });
    if (rows.length) render(rows[0].getAttribute('data-key'));
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  }

  /* ---------- 游戏表：搜索 + 家族筛选 ---------- */
  var table = document.querySelector('[data-games-table]');
  if (table) {
    var input = document.querySelector('[data-games-search]');
    var family = document.querySelector('[data-games-family]');
    var count = document.querySelector('[data-games-count]');
    var rowsG = [].slice.call(table.querySelectorAll('tbody tr'));

    var apply = function () {
      var q = (input && input.value || '').trim().toLowerCase();
      var fam = family && family.value || '';
      var shown = 0;
      rowsG.forEach(function (tr) {
        var text = tr.getAttribute('data-search') || tr.textContent.toLowerCase();
        var okQ = !q || text.indexOf(q) !== -1;
        var okF = !fam || tr.getAttribute('data-family') === fam;
        var show = okQ && okF;
        tr.hidden = !show;
        if (show) shown++;
      });
      if (count) count.textContent = shown + ' / ' + rowsG.length;
    };

    if (input) input.addEventListener('input', apply);
    if (family) family.addEventListener('change', apply);
    apply();
  }
})();
