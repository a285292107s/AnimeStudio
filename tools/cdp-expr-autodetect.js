(() => {
  const button = document.querySelector('#autoDetect');
  if (!button) {
    return { clicked: false };
  }

  button.click();
  return { clicked: true };
})()
