(() => {
  document.querySelector('.tab[data-tab="images"]').click();

  const firstImage = document.querySelector('.card img');
  return {
    imageCount: document.querySelector('#imageCount').textContent,
    tabCount: document.querySelector('#tabImageCount').textContent,
    statImages: document.querySelectorAll('#stats dd')[3].textContent,
    cards: document.querySelectorAll('.card').length,
    firstImgSrc: firstImage?.src,
    firstLoaded: firstImage?.complete,
    firstNatural: firstImage ? `${firstImage.naturalWidth}x${firstImage.naturalHeight}` : null,
    log: [...document.querySelectorAll('#logList li')].slice(-3).map((li) => li.textContent),
  };
})()
