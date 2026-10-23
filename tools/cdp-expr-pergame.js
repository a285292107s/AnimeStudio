(() => {
  const s = window.HOST?.state?.settings || {};
  return {
    game: s.game,
    gameLabel: s.gameLabel,
    outputDirectory: s.outputDirectory,
    outputDirectories: s.outputDirectories,
    profiles: s.profiles,
    railOut: document.querySelector('#outDir')?.textContent,
    railHint: document.querySelector('#outHint')?.textContent,
    railTitle: document.querySelector('#outDir')?.title,
    welcomeOutput: document.querySelector('#welcomeOutputPath')?.textContent,
    welcomeLabel: document.querySelector('#welcomeOutputLabel')?.textContent,
    log: [...document.querySelectorAll('#logList li')].slice(-4).map((li) => li.textContent),
  };
})()
