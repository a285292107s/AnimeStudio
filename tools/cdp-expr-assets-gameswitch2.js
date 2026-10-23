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
      .map((row) => row.textContent.trim().replace(/\s+/g, ' ').slice(0, 80)),
  });

  panel('#assetContainer').value = '';
  panel('#assetName').value = 'Avatar';
  panel('[data-tab="assets"]').click();

  let before = snapshot();
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await sleep(500);
    before = snapshot();
    if (before.rows > 0) {
      break;
    }
  }

  panel('.game[data-game="SR"]').click();

  let after = snapshot();
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await sleep(500);
    after = snapshot();
    if (after.rows > 0 && after.game === 'SR') {
      break;
    }
  }

  return JSON.stringify({ before, after, status: panel('#statusText')?.textContent });
})()
