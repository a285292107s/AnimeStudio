(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);
  const cards = () => document.querySelectorAll('#imageGrid .card').length;

  panel('[data-tab="images"]').click();

  let loaded = 0;
  for (let attempt = 0; attempt < 60; attempt += 1) {
    await sleep(500);
    loaded = cards();
    if (loaded > 0) break;
  }

  const list = {
    cards: loaded,
    count: panel('#imageCount')?.textContent,
    pager: panel('#imagePageInfo')?.textContent,
    tabs: [...document.querySelectorAll('.tab')].map((tab) => tab.textContent.trim()),
  };

  panel('#imageGrid .card').click();
  await sleep(400);
  const opened = {
    visible: !panel('#viewer').hidden,
    title: panel('#viewerTitle')?.textContent,
    meta: panel('#viewerMeta')?.textContent,
    index: panel('#viewerIndex')?.textContent,
    transform: panel('#viewerImage')?.style.transform,
  };

  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
  await sleep(300);
  const next = { index: panel('#viewerIndex')?.textContent, title: panel('#viewerTitle')?.textContent };

  document.dispatchEvent(new KeyboardEvent('keydown', { key: '+', bubbles: true }));
  await sleep(250);
  const zoomed = {
    label: panel('#viewerZoomReset')?.textContent,
    transform: panel('#viewerImage')?.style.transform,
  };

  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  await sleep(250);
  const closed = panel('#viewer').hidden;

  // 密度：小图 / 大图各切一次
  panel('#imageDensity [data-size="sm"]').click();
  await sleep(150);
  const small = { cls: panel('#imageGrid').className, cards: cards() };
  panel('#imageDensity [data-size="lg"]').click();
  await sleep(150);
  const large = { cls: panel('#imageGrid').className };

  return JSON.stringify({ list, opened, next, zoomed, closed, small, large });
})()
