(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);
  const snapshot = () => ({
    count: panel('#assetCount')?.textContent,
    tabCount: panel('#tabAssetCount')?.textContent,
    rows: document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)').length,
    first: [...document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)')]
      .slice(0, 1)
      .map((row) => row.textContent.trim().replace(/\s+/g, ' ').slice(0, 80)),
  });

  const before = snapshot();

  // 模拟索引重建完成（真实重建要跑几分钟，这里只验证前端收到的 task.done 分支）。
  window.chrome.webview.dispatchEvent(new MessageEvent('message', {
    data: {
      type: 'task.done',
      kind: 'index',
      id: 'probe',
      exitCode: 0,
      ok: true,
      state: window.HOST.state.hostState,
    },
  }));

  const justAfter = snapshot();

  let after = justAfter;
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await sleep(500);
    after = snapshot();
    if (after.rows > 0 && after.count?.startsWith('匹配')) {
      break;
    }
  }

  return JSON.stringify({ before, justAfter, after, logs: [...document.querySelectorAll('#logList li')].slice(-3).map((li) => li.textContent) });
})()
