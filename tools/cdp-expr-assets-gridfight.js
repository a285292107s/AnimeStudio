(async () => {
  document.querySelector('.tab[data-tab="assets"]').click();
  await new Promise((resolve) => setTimeout(resolve, 150));

  document.querySelector('#assetType').value = 'Sprite';
  document.querySelector('#assetContainer').value = 'spriteoutput/ui/gridfight';
  document.querySelector('#assetSearch').click();
  return { container: 'spriteoutput/ui/gridfight', type: 'Sprite' };
})()
