(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);
  const snapshot = () => ({
    game: window.HOST?.state?.hostState?.game,
    count: panel('#assetCount')?.textContent,
    tabCount: panel('#tabAssetCount')?.textContent,
    rows: document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)').length,
    first: [...document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)')]
      .slice(0, 1)
      .map((row) => row.textContent.trim().replace(/\s+/g, ' ').slice(0, 70)),
  });

  // 打开资源浏览立刻触发一次 SR 查询，然后在它返回前切换游戏。
  panel('[data-tab="assets"]').click();
  await sleep(150);
  panel('.game[data-game="ZZZ"]').click();

  let after = snapshot();
  for (let attempt = 0; attempt < 60; attempt += 1) {
    await sleep(500);
    after = snapshot();
    if (after.rows > 0 && after.game === 'ZZZ' && after.tabCount !== '—') {
      break;
    }
  }

  return JSON.stringify({ after, status: panel('#statusText')?.textContent });
})()
