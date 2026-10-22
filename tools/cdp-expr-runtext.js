(() => {
  const preset = [...document.querySelectorAll('[data-preset]')].find((button) => button.dataset.preset === 'text');
  if (!preset) {
    return { clicked: false };
  }

  preset.click();
  return { clicked: true, preset: preset.dataset.preset };
})()
