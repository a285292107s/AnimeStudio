JSON.stringify({
  title: document.title,
  tabs: [...document.querySelectorAll('.tab')].map((tab) => tab.textContent.trim()),
  active: [...document.querySelectorAll('.tab.is-active')].map((tab) => tab.dataset.tab),
  panes: [...document.querySelectorAll('.pane')].map((pane) => ({ pane: pane.dataset.pane, hidden: pane.hidden })),
  textTab: Boolean(document.querySelector('[data-tab="text"]')),
  configPreset: Boolean(document.querySelector('[data-preset="config"]')),
  presets: [...document.querySelectorAll('#presets .preset__name')].map((el) => el.textContent),
  imageCount: document.querySelector('#imageCount')?.textContent,
  status: document.querySelector('#statusText')?.textContent,
  hostImages: window.HOST?.state?.hostState?.images,
})
