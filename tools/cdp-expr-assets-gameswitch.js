(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);
  const snapshot = () => ({
    game: window.HOST?.state?.hostState?.game,
    count: panel('#assetCount')?.textContent,
    tabCount: panel('#tabAssetCount')?.textContent,
    rows: document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)').length,
    first: [...document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)')]
      .slice(0, 2)
      .map((row) => row.textContent.trim().replace(/\s+/g, ' ').slice(0, 90)),
  });

  // 空过滤：ZZZ 的容器是数字 ID，spriteoutput/ui 在那边匹配不到东西。
  panel('#assetContainer').value = '';
  const before = snapshot();

  panel('.game[data-game="ZZZ"]').click();

  let after;
  for (let attempt = 0; attempt < 60; attempt += 1) {
    await sleep(1000);
    after = snapshot();
    if (after.tabCount && after.tabCount !== '—' && after.tabCount !== before.tabCount) {
      break;
    }
  }

  return JSON.stringify({ before, after, status: panel('#statusText')?.textContent });
})()
