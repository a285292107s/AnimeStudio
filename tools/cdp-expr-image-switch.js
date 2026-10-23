(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const result = {};

  // Switch back to the text tab: changing the output directory here must not scan the gallery.
  document.querySelector('.tab[data-tab="text"]')?.click();
  await sleep(300);

  const settings = JSON.parse(JSON.stringify(window.HOST.state.settings));
  settings.outputDirectories = {
    ...(settings.outputDirectories || {}),
    SR: String.raw`C:\Users\28529\AppData\Local\Temp\as-img-big-b`,
  };

  const saveStarted = performance.now();
  window.HOST.call('settings.save', { settings });
  const saveDeadline = saveStarted + 15000;
  while (performance.now() < saveDeadline) {
    if ((window.HOST.state.settings.outputDirectory || '').toLowerCase().endsWith('as-img-big-b')) {
      break;
    }

    await sleep(50);
  }

  result.saveApplyMs = Math.round(performance.now() - saveStarted);
  await sleep(700);
  result.newOutput = window.HOST.state.settings.outputDirectory;
  result.activeTab = document.querySelector('.tab.is-active')?.dataset.tab;
  result.dirtyAfterSwitch = window.HOST.state.imagesDirty;
  result.cardsAfterSwitch = document.querySelectorAll('#imageGrid .card').length;
  result.scanLogAfterSwitch = [...document.querySelectorAll('#logList li')]
    .some((li) => li.textContent.includes('扫描图库'));

  // Opening the gallery now triggers the background scan for the new folder.
  const loadStarted = performance.now();
  document.querySelector('.tab[data-tab="images"]').click();
  const loadDeadline = loadStarted + 180000;
  while (performance.now() < loadDeadline) {
    if (!window.HOST.state.imagesDirty && !window.HOST.state.imagesLoading) {
      break;
    }

    await sleep(100);
  }

  result.loadMs = Math.round(performance.now() - loadStarted);
  await sleep(1200);
  result.total = window.HOST.state.imageTotal;
  result.cards = document.querySelectorAll('#imageGrid .card').length;
  result.countText = document.querySelector('#imageCount')?.textContent;
  return result;
})()
