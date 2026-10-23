(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const rows = () =>
    [...document.querySelectorAll('#settingsOutputs .outputs__row')].map((row) => ({
      game: row.dataset.game,
      name: row.querySelector('.outputs__name')?.textContent?.trim(),
      current: row.classList.contains('is-current'),
      badgeHidden: row.querySelector('[data-current]')?.hidden,
      input: row.querySelector('input')?.value,
    }));
  const snapshot = (label) => ({
    label,
    game: window.HOST.state.settings.game,
    outputDirectory: window.HOST.state.settings.outputDirectory,
    outputs: { ...(window.HOST.state.settings.outputDirectories || {}) },
    railOut: document.querySelector('#outDir')?.textContent,
  });

  document.querySelector('#openSettings')?.click();
  await sleep(500);
  const opened = { label: 'settings opened (SR)', rows: rows() };

  const zzzRow = document.querySelector('#settingsOutputs .outputs__row[data-game="ZZZ"]');
  const zzzInput = zzzRow.querySelector('input');
  const tempPath = 'C:\\Users\\28529\\AppData\\Local\\Temp\\as-userflow\\zzz-dual';
  zzzInput.value = tempPath;
  document.querySelector('#settingsSave')?.click();
  await sleep(2200);
  const afterSave = snapshot('after save ZZZ row');

  document.querySelector('.game[data-game="ZZZ"]')?.click();
  await sleep(2400);
  const onZzz = snapshot('switched ZZZ');
  document.querySelector('#openSettings')?.click();
  await sleep(400);
  const zzzDialog = { label: 'settings on ZZZ', rows: rows() };

  document.querySelector('.game[data-game="SR"]')?.click();
  await sleep(2400);
  const backSr = snapshot('switched back SR');
  document.querySelector('#openSettings')?.click();
  await sleep(400);
  const srDialog = { label: 'settings on SR', rows: rows() };

  // Restore the user's original ZZZ directory.
  const restoreInput = document.querySelector('#settingsOutputs .outputs__row[data-game="ZZZ"] input');
  restoreInput.value = 'C:\\Users\\28529\\Desktop\\mihoyoTextures';
  document.querySelector('#settingsSave')?.click();
  await sleep(2000);
  const restored = snapshot('restored ZZZ path');

  return { tempPath, opened, afterSave, onZzz, zzzDialog, backSr, srDialog, restored };
})()
