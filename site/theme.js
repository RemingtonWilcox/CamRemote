// Runs before the page draws, so it never flashes the wrong colours.
// The choice is kept in this browser only, and only if you press the button.
(function () {
  document.documentElement.classList.add('js');
  var saved = null;
  try { saved = localStorage.getItem('theme'); } catch (e) {}
  if (saved === 'light' || saved === 'dark') document.documentElement.setAttribute('data-theme', saved);
})();
