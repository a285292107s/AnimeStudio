(() => {
  const chips = [...document.querySelectorAll('#imageFilters .filter')];
  const target = chips.find((chip) => chip.textContent.includes('gridfight')) || chips[1];
  if (!target) {
    return { clicked: false };
  }

  const label = target.textContent.trim();
  target.click();
  return { clicked: true, label };
})()
