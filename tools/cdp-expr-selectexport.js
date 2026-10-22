(async () => {
  document.querySelector('.tab[data-tab="images"]').click();
  await new Promise((resolve) => setTimeout(resolve, 800));

  const ids = [...document.querySelectorAll('.card')].slice(0, 2).map((card) => card.dataset.id);

  for (const id of ids) {
    document.querySelector(`.card[data-id="${CSS.escape(id)}"]`)?.click();
    await new Promise((resolve) => setTimeout(resolve, 200));
  }

  const selected = document.querySelectorAll('.card.is-selected').length;
  document.querySelector('#imageExport').click();

  return { ids, selected };
})()
