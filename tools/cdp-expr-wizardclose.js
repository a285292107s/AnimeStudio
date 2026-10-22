(() => {
  const button = document.querySelector('#welcomeClose');
  if (!button) {
    return { clicked: false };
  }

  button.click();
  return { clicked: true };
})()
