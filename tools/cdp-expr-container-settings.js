(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  document.querySelector('#openSettings').click();
  await sleep(400);
  const data = {
    state: document.querySelector('#settingsContainerIndexState')?.textContent,
    input: document.querySelector('#settingsContainerIndex')?.value,
    hint: document.querySelector('#settingsContainerHint')?.textContent,
    pick: document.querySelector('#settingsContainerPick')?.textContent,
    download: document.querySelector('#settingsContainerDownload')?.textContent,
    downloadDisabled: document.querySelector('#settingsContainerDownload')?.disabled,
  };
  document.querySelector('#settingsClose').click();
  return JSON.stringify(data);
})()
