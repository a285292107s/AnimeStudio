(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);
  const snapshot = () => ({
    active: [...document.querySelectorAll('.tab.is-active')].map((tab) => tab.dataset.tab),
    count: panel('#assetCount')?.textContent,
    tabCount: panel('#tabAssetCount')?.textContent,
    rows: document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)').length,
  });

  // 切回图库后再收到「重建完成」：资源浏览不在前台，只应该被标脏。
  panel('[data-tab="images"]').click();
  await sleep(200);

  window.chrome.webview.dispatchEvent(new MessageEvent('message', {
    data: {
      type: 'task.done',
      kind: 'index',
      id: 'probe2',
      exitCode: 0,
      ok: true,
      state: window.HOST.state.hostState,
    },
  }));

  await sleep(300);
  const hidden = snapshot();

  // 打开资源浏览标签，应该自动补一次查询。
  panel('[data-tab="assets"]').click();

  let opened = snapshot();
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await sleep(500);
    opened = snapshot();
    if (opened.rows > 0) {
      break;
    }
  }

  return JSON.stringify({ hidden, opened });
})()
