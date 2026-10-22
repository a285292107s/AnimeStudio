(() => {
  const preset = [...document.querySelectorAll('[data-preset]')].find((button) => button.dataset.preset === 'images');
  if (!preset) {
    return { clicked: false };
  }

  preset.click();
  return { clicked: true };
})()
