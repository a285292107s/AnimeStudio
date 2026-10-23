(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  document.querySelector('#assetContainer').value = 'Assets/NapResources';
  document.querySelector('#assetSearch').click();
  await sleep(4000);
  const rows = [...document.querySelectorAll('#assetList .asset-row[data-index]')].slice(0, 5)
    .map((r) => [...r.children].slice(1).map((c) => c.textContent.trim()));
  return JSON.stringify({
    count: document.querySelector('#assetCount')?.textContent,
    rows,
    tab: document.querySelector('#tabAssetCount')?.textContent,
  });
})()
