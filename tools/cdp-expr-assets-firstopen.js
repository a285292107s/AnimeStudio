(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);

  await sleep(1500);
  panel('[data-tab="assets"]').click();

  const started = performance.now();
  let rows = 0;
  for (let attempt = 0; attempt < 60; attempt += 1) {
    await sleep(500);
    rows = document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)').length;
    if (rows > 0) {
      break;
    }
  }

  await sleep(1200);
  return JSON.stringify({
    game: window.HOST?.state?.hostState?.game,
    waitedMs: Math.round(performance.now() - started),
    rows,
    count: panel('#assetCount')?.textContent,
    tabCount: panel('#tabAssetCount')?.textContent,
    status: panel('#statusText')?.textContent,
  });
})()
