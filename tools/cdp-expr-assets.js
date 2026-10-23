(async () => {
  const tab = document.querySelector('.tab[data-tab="assets"]');
  if (!tab || tab.hidden) {
    return { error: 'assets tab hidden' };
  }

  tab.click();
  await new Promise((resolve) => setTimeout(resolve, 200));

  const container = document.querySelector('#assetContainer');
  container.value = 'spriteoutput/ui/fonts';
  document.querySelector('#assetSearch').click();

  return { clicked: true, container: container.value };
})()
