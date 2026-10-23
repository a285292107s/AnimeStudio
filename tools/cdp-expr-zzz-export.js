(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  document.querySelector('#assetContainer').value = 'BusinessCardDialog';
  document.querySelector('#assetSearch').click();
  await sleep(3000);

  const rows = [...document.querySelectorAll('#assetList .asset-row[data-index]')];
  document.querySelector('#assetClearSelection').click();
  rows[0].click();
  await sleep(200);
  const chosen = [...window.HOST.state.assetSelected.values()];
  document.querySelector('#assetExportSelected').click();

  for (let i = 0; i < 240; i += 1) {
    await sleep(500);
    if (!window.HOST.state.taskRunning) break;
  }
  await sleep(1000);
  return JSON.stringify({
    chosen,
    status: document.querySelector('#statusText')?.textContent,
    tail: [...document.querySelectorAll('#logList li')].slice(-3).map((li) => li.textContent),
  });
})()
