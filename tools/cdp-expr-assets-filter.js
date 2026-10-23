(async () => {
  document.querySelector('.tab[data-tab="assets"]').click();
  await new Promise((resolve) => setTimeout(resolve, 200));

  const type = document.querySelector('#assetType');
  type.value = 'Sprite';

  const container = document.querySelector('#assetContainer');
  container.value = 'spriteoutput/ui/fonts/sdffonts/text3d';

  document.querySelector('#assetSearch').click();
  return { container: container.value, type: type.value };
})()
