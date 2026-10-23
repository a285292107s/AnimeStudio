(async () => {
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  document.querySelector('#openSettings').click();
  await sleep(300);
  document.querySelector('#settingsContainerDownload').click();

  let last = '';
  for (let i = 0; i < 200; i += 1) {
    await sleep(1000);
    const state = window.HOST.state.containerIndex || {};
    last = document.querySelector('#settingsContainerIndexState')?.textContent || '';
    if (!state.downloading && !state.loading && i > 3) {
      break;
    }
  }

  const result = {
    state: document.querySelector('#settingsContainerIndexState')?.textContent,
    hint: document.querySelector('#settingsContainerHint')?.textContent,
    container: window.HOST.state.containerIndex,
    status: document.querySelector('#statusText')?.textContent,
  };
  document.querySelector('#settingsClose').click();
  return JSON.stringify(result);
})()
