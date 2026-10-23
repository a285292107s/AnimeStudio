(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);

  panel('#assetContainer').value = 'spriteoutput/ui';
  panel('[data-tab="assets"]').click();

  const started = performance.now();
  for (let attempt = 0; attempt < 120; attempt += 1) {
    await sleep(1000);
    if (document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)').length > 0) {
      break;
    }
  }

  return JSON.stringify({
    active: [...document.querySelectorAll('.tab.is-active')].map((tab) => tab.dataset.tab),
    waitedMs: Math.round(performance.now() - started),
    count: panel('#assetCount')?.textContent,
    tabCount: panel('#tabAssetCount')?.textContent,
    rows: document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)').length,
    first: [...document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)')]
      .slice(0, 3)
      .map((row) => row.textContent.trim().replace(/\s+/g, ' ').slice(0, 90)),
    status: panel('#statusText')?.textContent,
  });
})()
