(() => ({
  progressHidden: document.querySelector('#indexProgress')?.hidden,
  barWidth: document.querySelector('#indexBar')?.style.width,
  indeterminate: document.querySelector('.progress__bar')?.classList.contains('is-indeterminate'),
  progressText: document.querySelector('#indexProgressText')?.textContent,
  cancelHidden: document.querySelector('#taskCancel')?.hidden,
  status: document.querySelector('#statusText')?.textContent,
  logTail: [...document.querySelectorAll('#logList li')].slice(-4).map((li) => li.textContent),
}))()
