(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  document.querySelector('#openSettings')?.click();
  await sleep(400);
  const result = {
    open: !document.querySelector('#settings')?.hidden,
    fields: [...document.querySelectorAll('#settings input, #settings select')].map((el) => el.id).filter(Boolean),
    configRow: Boolean(document.querySelector('#settingsConfigSource')),
    title: document.title,
  };
  document.querySelector('#settingsClose')?.click();
  await sleep(200);
  return result;
})()
