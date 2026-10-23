(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const result = {
    activeTab: document.querySelector('.tab.is-active')?.dataset.tab,
    oldTotal: window.HOST.state.imageTotal,
    oldOutput: window.HOST.state.settings.outputDirectory,
  };

  const settings = JSON.parse(JSON.stringify(window.HOST.state.settings));
  settings.outputDirectories = {
    ...(settings.outputDirectories || {}),
    SR: String.raw`C:\Users\28529\AppData\Local\Temp\as-img-big-a`,
  };

  const saveStarted = performance.now();
  window.HOST.call('settings.save', { settings });
  const saveDeadline = saveStarted + 15000;
  while (performance.now() < saveDeadline) {
    if ((window.HOST.state.settings.outputDirectory || '').toLowerCase().endsWith('as-img-big-a')) {
      break;
    }

    await sleep(50);
  }

  result.saveApplyMs = Math.round(performance.now() - saveStarted);

  // The gallery is open, so settings.changed should kick off a background rescan.
  const loadStarted = performance.now();
  const loadDeadline = loadStarted + 180000;
  while (performance.now() < loadDeadline) {
    if (!window.HOST.state.imagesDirty && !window.HOST.state.imagesLoading) {
      break;
    }

    await sleep(100);
  }

  result.loadMs = Math.round(performance.now() - loadStarted);
  result.newOutput = window.HOST.state.settings.outputDirectory;
  result.total = window.HOST.state.imageTotal;
  result.cards = document.querySelectorAll('#imageGrid .card').length;
  await sleep(800);
  result.status = document.querySelector('#statusText')?.textContent;
  return result;
})()
