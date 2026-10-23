(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  document.querySelector('#openSettings').click();
  await sleep(400);
  const data = {
    state: document.querySelector('#settingsContainerIndexState')?.textContent,
    hint: document.querySelector('#settingsContainerHint')?.textContent,
    download: document.querySelector('#settingsContainerDownload')?.textContent,
    inputWidth: document.querySelector('#settingsContainerIndex')?.getBoundingClientRect().width,
    row: document.querySelector('#settingsContainerIndex')?.closest('.field--stack')?.getBoundingClientRect(),
  };
  return JSON.stringify(data);
})()
