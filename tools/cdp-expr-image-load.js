(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const result = {
    startupDirty: window.HOST.state.imagesDirty,
    startupCards: document.querySelectorAll('#imageGrid .card').length,
    startupCount: document.querySelector('#imageCount')?.textContent,
    startupOutput: window.HOST.state.settings.outputDirectory,
  };

  const started = performance.now();
  document.querySelector('.tab[data-tab="images"]').click();
  const deadline = started + 180000;
  while (performance.now() < deadline) {
    if (!window.HOST.state.imagesDirty && !window.HOST.state.imagesLoading) {
      break;
    }

    await sleep(100);
  }

  result.loadMs = Math.round(performance.now() - started);
  await sleep(1200);

  const cards = [...document.querySelectorAll('#imageGrid .card')];
  const images = [...document.querySelectorAll('#imageGrid img')];
  result.dirty = window.HOST.state.imagesDirty;
  result.total = window.HOST.state.imageTotal;
  result.available = window.APP.state.data.stats.uiSprites;
  result.cards = cards.length;
  result.loadedThumbs = images.filter((img) => img.complete && img.naturalWidth > 0).length;
  result.countText = document.querySelector('#imageCount')?.textContent;
  result.pageText = document.querySelector('#imagePageInfo')?.textContent;
  result.status = document.querySelector('#statusText')?.textContent;
  result.categories = [...document.querySelectorAll('#imageFilters [data-filter]')]
    .slice(0, 6)
    .map((el) => el.textContent.trim());
  return result;
})()
