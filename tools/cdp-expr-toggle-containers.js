(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const box = document.querySelector('#assetOnlyContainers');
  box.checked = false;
  box.dispatchEvent(new Event('change', { bubbles: true }));
  await sleep(4000);
  const off = {
    count: document.querySelector('#assetCount')?.textContent,
    rows: [...document.querySelectorAll('#assetList .asset-row[data-index]')].slice(0, 6)
      .map((r) => [...r.children].slice(1).map((c) => c.textContent.trim())),
    empty: document.querySelectorAll('#assetList .asset-row__container.is-empty').length,
  };

  box.checked = true;
  box.dispatchEvent(new Event('change', { bubbles: true }));
  await sleep(4000);
  const on = { count: document.querySelector('#assetCount')?.textContent };

  return JSON.stringify({ off, on });
})()
