(async () => {
  const image = document.querySelector('.card img');
  const url = image?.src;

  let status = 'n/a';
  let detail = '';

  try {
    const response = await fetch(url, { cache: 'no-store' });
    status = response.status;
    detail = (await response.text()).slice(0, 120);
  } catch (error) {
    status = 'ERR';
    detail = String(error);
  }

  const css = await fetch('https://app.local/assets/app.css', { cache: 'no-store' })
    .then((r) => r.status)
    .catch((e) => 'ERR ' + e);

  return { url, status, detail, appCssStatus: css, complete: image?.complete, natural: image ? `${image.naturalWidth}x${image.naturalHeight}` : null };
})()
