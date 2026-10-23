(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);
  const rows = () => [...document.querySelectorAll('#assetList .asset-row[data-index]')];

  panel('#assetClearSelection').click();
  rows()[0].click();
  rows()[2].click();

  const chosen = [...window.HOST.state.assetSelected.values()].map((entry) => `${entry.type}:${entry.name}`);
  const before = {
    selected: panel('#assetSelectedCount')?.textContent,
    log: document.querySelectorAll('#logList li').length,
  };

  panel('#assetExportSelected').click();

  let running = true;
  for (let attempt = 0; attempt < 240; attempt += 1) {
    await sleep(500);
    running = window.HOST.state.taskRunning;
    if (!running) {
      break;
    }
  }

  await sleep(2000);
  return JSON.stringify({
    chosen,
    before,
    running,
    selectedAfter: panel('#assetSelectedCount')?.textContent,
    status: panel('#statusText')?.textContent,
    tail: [...document.querySelectorAll('#logList li')].slice(-6).map((li) => li.textContent),
  });
})()
