(async () => {
  const read = () => ({
    game: window.HOST.state.settings.game,
    rail: document.querySelector('#outDir')?.textContent,
    railTitle: document.querySelector('#outDir')?.title,
    railLabel: document.querySelector('#outGameLabel')?.textContent,
    stateOutput: window.HOST.state.settings.outputDirectory,
    stateOutputs: window.HOST.state.settings.outputDirectories,
  });

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const sr = read();

  document.querySelector('.game[data-game="ZZZ"]')?.click();
  await sleep(2600);
  const zzz = read();

  document.querySelector('.game[data-game="SR"]')?.click();
  await sleep(2400);

  document.querySelector('#openSettings')?.click();
  await sleep(500);
  const form = {
    SR: document.querySelector('#settingsOutputSR')?.value,
    ZZZ: document.querySelector('#settingsOutputZZZ')?.value,
  };

  return { sr, zzz, form };
})()
