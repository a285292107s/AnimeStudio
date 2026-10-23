(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const $ = (sel) => document.querySelector(sel);

  // 1) 取消「只看有路径的」：哈希容器也要列出来
  const only = $('#assetOnlyContainers');
  only.checked = false;
  $('#assetSearch').click();
  await sleep(2500);
  const allCount = $('#assetCount')?.textContent;
  const hashRow = [...document.querySelectorAll('#assetList .asset-row__container.is-hash')].slice(0, 2).map((el) => el.textContent);

  // 2) 勾回来
  only.checked = true;
  $('#assetSearch').click();
  await sleep(2500);
  const pathCount = $('#assetCount')?.textContent;

  // 3) 选两行导出，验证 map → 回捞 → PNG 这条线
  const rows = [...document.querySelectorAll('#assetList .asset-row:not(.asset-row--head)')].slice(0, 2);
  for (const row of rows) {
    row.querySelector('input[type="checkbox"]')?.click();
    await sleep(120);
  }
  const selected = $('#assetSelectedCount')?.textContent;
  $('#assetExportSelected')?.click();

  // 等任务结束
  for (let i = 0; i < 120; i += 1) {
    await sleep(1000);
    const status = $('#statusText')?.textContent || '';
    if (status.includes('就绪') || status.includes('完成') || status.includes('失败')) {
      break;
    }
  }

  const names = rows.map((row) => row.querySelector('.asset-row__name')?.textContent);
  const logLines = [...document.querySelectorAll('#logList li')].slice(-8).map((li) => li.textContent);

  return {
    allCount,
    hashRow,
    pathCount,
    selected,
    exported: names,
    status: $('#statusText')?.textContent,
    gallery: $('#tabImageCount')?.textContent,
    logLines,
  };
})()
