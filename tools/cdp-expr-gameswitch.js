(() => {
  const el = document.querySelector('.game[data-game="SR"]');
  if (!el) {
    return { found: false, games: [...document.querySelectorAll('.game')].map((g) => g.dataset.game) };
  }

  const before = window.APP.state.gameId;
  el.click();

  return {
    found: true,
    before,
    after: window.APP.state.gameId,
    active: document.querySelector('.game.is-active')?.dataset.game,
  };
})()
