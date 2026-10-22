(() => {
  const button = document.querySelector('#imagePager [data-page="next"]');
  if (!button || button.disabled) {
    return { clicked: false, disabled: true };
  }

  button.click();
  return { clicked: true };
})()
