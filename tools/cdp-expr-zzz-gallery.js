(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const $ = (sel) => document.querySelector(sel);
  document.querySelector('[data-tab="images"]')?.click();
  await sleep(3000);
  return {
    tabImageCount: $('#tabImageCount')?.textContent,
    galleryStats: $('#imageCount')?.textContent || $('#tabImageCount')?.textContent,
    cards: [...document.querySelectorAll('#imageGrid img, #imageList img')].slice(0, 4).map((img) => img.getAttribute('src')),
    empty: !!document.querySelector('.gallery__empty'),
    logTail: [...document.querySelectorAll('#logList li')].slice(-3).map((li) => li.textContent),
  };
})()
