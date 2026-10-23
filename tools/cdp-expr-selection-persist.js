(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  document.querySelector('[data-tab="assets"]').click();
  await sleep(2500);
  const rows = [...document.querySelectorAll('#assetList .asset-row[data-index]')];
  document.querySelector('#assetClearSelection').click();
  rows[0].click(); rows[3].click();
  await sleep(150);
  const before = document.querySelector('#assetSelectedCount').textContent;

  document.querySelector('[data-tab="images"]').click();
  await sleep(1200);
  const away = { selected: window.HOST.state.assetSelected.size, panel: document.querySelector('.insp__title')?.textContent };

  document.querySelector('[data-tab="assets"]').click();
  await sleep(800);
  const back = {
    selected: document.querySelector('#assetSelectedCount')?.textContent,
    cart: document.querySelector('.insp__title')?.textContent,
    marked: document.querySelectorAll('#assetList .asset-row.is-selected').length,
  };
  return JSON.stringify({ before, away, back });
})()
