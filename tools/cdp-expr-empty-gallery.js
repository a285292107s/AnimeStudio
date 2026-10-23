(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  document.querySelector('#viewer').hidden = true;
  document.querySelector('.game[data-game="ZZZ"]').click();
  await sleep(4000);
  document.querySelector('[data-tab="images"]').click();
  await sleep(3000);
  const gallery = {
    count: document.querySelector('#imageCount')?.textContent,
    empty: document.querySelector('#imageGrid .empty')?.innerText?.replace(/\n/g, ' / '),
    cards: document.querySelectorAll('#imageGrid .card').length,
    tab: document.querySelector('#tabImageCount')?.textContent,
    stat: document.querySelectorAll('#stats dd')[2]?.textContent,
  };
  document.querySelector('[data-tab="assets"]').click();
  await sleep(3000);
  const assets = {
    game: window.HOST.state.hostState.game,
    tabCount: document.querySelector('#tabAssetCount')?.textContent,
    count: document.querySelector('#assetCount')?.textContent,
    rows: document.querySelectorAll('#assetList .asset-row[data-index]').length,
  };
  return JSON.stringify({ gallery, assets });
})()
