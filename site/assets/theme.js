// Day and night. The visitor's stored choice wins, then the system's preference; the button the script adds to the
// chrome flips it and remembers it. Loaded in <head>, so the first paint is already the right theme.
(() => {
  const key = "acta-theme";
  const root = document.documentElement;
  const system = () => (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark");
  let stored = null;
  try {
    stored = localStorage.getItem(key);
  } catch {
    stored = null;
  }
  const apply = (theme) => {
    root.dataset.theme = theme;
  };
  apply(stored === "light" || stored === "dark" ? stored : system());
  matchMedia("(prefers-color-scheme: light)").addEventListener("change", () => {
    if (stored === null) apply(system());
  });

  addEventListener("DOMContentLoaded", () => {
    const chrome = document.querySelector(".chrome");
    if (!chrome) return;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "theme";
    button.innerHTML =
      '<svg class="sun" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>' +
      '<svg class="moon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>';
    const label = () => {
      const next = root.dataset.theme === "light" ? "night" : "day";
      button.setAttribute("aria-label", `Switch to the ${next} theme`);
      button.title = `Switch to ${next}`;
    };
    button.addEventListener("click", () => {
      stored = root.dataset.theme === "light" ? "dark" : "light";
      apply(stored);
      try {
        localStorage.setItem(key, stored);
      } catch {
        // A private window keeps the choice for this page only.
      }
      label();
    });
    label();
    chrome.appendChild(button);
  });
})();
