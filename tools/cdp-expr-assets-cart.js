(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const rows = [...document.querySelectorAll('#assetList .asset-row[data-index]')];
  document.querySelector('#assetClearSelection').click();
  rows[0].click(); rows[1].click(); rows[2].click();
  await sleep(200);
  const cart = {
    title: document.querySelector('.insp__title')?.textContent,
    sub: document.querySelector('.insp__sub')?.textContent,
    items: [...document.querySelectorAll('.insp__list li')].map((li) => li.textContent),
    actions: [...document.querySelectorAll('.insp__actions .btn')].map((b) => b.textContent),
  };

  document.querySelector('[data-act="assets-clear"]')?.click();
  await sleep(200);
  const afterClear = {
    title: document.querySelector('.insp__title')?.textContent,
    empty: Boolean(document.querySelector('.panel__empty')),
    selected: document.querySelector('#assetSelectedCount')?.textContent,
  };

  return JSON.stringify({ cart, afterClear });
})()
