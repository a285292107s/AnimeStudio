(async () => {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const panel = (sel) => document.querySelector(sel);

  window.APP.openViewerAt(119);
  await sleep(300);
  const atEnd = {
    index: panel('#viewerIndex')?.textContent,
    title: panel('#viewerTitle')?.textContent,
    offset: window.HOST.state.imageOffset,
    pager: panel('#imagePageInfo')?.textContent,
  };

  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));

  let moved = atEnd;
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await sleep(500);
    if (window.HOST.state.imageOffset !== atEnd.offset) {
      moved = {
        index: panel('#viewerIndex')?.textContent,
        title: panel('#viewerTitle')?.textContent,
        offset: window.HOST.state.imageOffset,
        pager: panel('#imagePageInfo')?.textContent,
      };
      break;
    }
  }

  // 再往回翻一页
  window.APP.openViewerAt(0);
  await sleep(300);
  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
  await sleep(4000);
  const back = {
    index: panel('#viewerIndex')?.textContent,
    offset: window.HOST.state.imageOffset,
    pager: panel('#imagePageInfo')?.textContent,
  };

  document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
  await sleep(200);

  return JSON.stringify({ atEnd, moved, back });
})()
