(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);
  const rows = () => [...document.querySelectorAll('#assetList .asset-row[data-index]')];
  const state = () => ({
    selected: panel('#assetSelectedCount')?.textContent,
    exportLabel: panel('#assetExportSelected')?.textContent,
    marked: document.querySelectorAll('#assetList .asset-row.is-selected').length,
    selectAll: panel('#assetSelectAll')?.checked,
    indeterminate: panel('#assetSelectAll')?.indeterminate,
  });

  panel('#assetClearSelection').click();
  await sleep(80);

  rows()[0].click();
  await sleep(80);
  const one = state();

  rows()[2].dispatchEvent(new MouseEvent('click', { bubbles: true, shiftKey: true }));
  await sleep(80);
  const range = state();

  panel('#assetSelectAll').click();
  await sleep(120);
  const all = state();

  panel('#assetSelectAll').click();
  await sleep(120);
  const allOff = state();

  panel('#assetClearSelection').click();
  await sleep(80);
  const cleared = state();

  return JSON.stringify({ one, range, all, allOff, cleared });
})()
