(() => {
  const cancel = document.querySelector('#taskCancel');
  if (!cancel || cancel.hidden) {
    return { clicked: false, reason: 'cancel hidden' };
  }

  cancel.click();
  return { clicked: true };
})()
