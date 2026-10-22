(() => {
  document.querySelector('.tab[data-tab="text"]').click();

  return new Promise((resolve) => {
    setTimeout(() => {
      resolve({
        info: document.querySelector('#textPageInfo')?.textContent,
        first: document.querySelector('.row__hash')?.textContent,
        prevDisabled: document.querySelector('#textPager [data-page="prev"]')?.disabled,
        nextDisabled: document.querySelector('#textPager [data-page="next"]')?.disabled,
        rows: document.querySelectorAll('.row').length,
      });
    }, 300);
  });
})()
