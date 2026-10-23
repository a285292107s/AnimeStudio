(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const load = (url) =>
    new Promise((resolve) => {
      if (!url) {
        resolve('NONE');
        return;
      }
      const img = new Image();
      img.onload = () => resolve(`OK ${img.naturalWidth}x${img.naturalHeight}`);
      img.onerror = () => resolve('ERR');
      setTimeout(() => resolve('TIMEOUT'), 3000);
      img.src = url;
    });

  const srFirst = (window.APP.state.images[0] || {}).thumb || null;
  const srLoad = await load(srFirst);

  document.querySelector('.game[data-game="ZZZ"]')?.click();
  await sleep(2600);

  const settings = window.HOST.state.settings;
  const images = window.APP.state.images || [];

  return {
    srFirst,
    srLoad,
    afterSwitch: {
      game: settings.game,
      outputDirectory: settings.outputDirectory,
      srOutput: settings.outputDirectories?.SR,
      zzzOutput: settings.outputDirectories?.ZZZ,
      imageCount: images.length,
      firstThumb: images[0]?.thumb,
      firstLoad: await load(images[0]?.thumb),
      textCount: document.querySelector('#tabTextCount')?.textContent,
    },
    log: [...document.querySelectorAll('#logList li')].slice(-3).map((li) => li.textContent),
  };
})()
