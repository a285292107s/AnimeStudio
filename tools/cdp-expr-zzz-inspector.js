(() => {
  const text = document.querySelector('#panelBody')?.innerText || '';
  return {
    hasMb: /13\.0 MB/.test(text),
    hasGb: /0\.01 GB/.test(text),
    panel: text.split('\n').filter(Boolean).slice(0, 14),
  };
})()
