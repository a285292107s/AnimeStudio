(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const zzz = document.querySelector('.game[data-game="ZZZ"]');
  if (zzz) {
    zzz.click();
    await sleep(2500);
  }

  // 资源浏览里搜一下，确认图片条目能列出来
  const name = document.querySelector('#assetName');
  if (name) {
    name.value = '';
    document.querySelector('#assetSearch')?.click();
    await sleep(2500);
  }

  const rows = [...document.querySelectorAll('#assetList .asset-row')].slice(0, 5).map((row) =>
    [...row.querySelectorAll('span')].map((s) => s.textContent).join(' | '));

  return {
    game: document.querySelector('.game.is-active')?.dataset.game,
    chip: document.querySelector('#indexChipText')?.textContent,
    indexMeta: document.querySelector('#indexMeta')?.textContent,
    indexSize: document.querySelector('#indexSize')?.textContent,
    indexFormat: document.querySelector('#indexFormat')?.textContent,
    cacheSize: document.querySelector('#indexCacheSize')?.textContent,
    mapPath: document.querySelector('#indexMapPath')?.textContent,
    stats: document.querySelector('#stats')?.innerText?.replace(/\n/g, ' | '),
    assetCount: document.querySelector('#assetCount')?.textContent,
    tabCount: document.querySelector('#tabAssetCount')?.textContent,
    rows,
    hasTypeSelect: !!document.querySelector('#assetType'),
  };
})()
