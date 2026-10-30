// Day and night. The visitor's stored choice wins, then the system's preference; the "day · night" button the script
// adds to the chrome flips it and remembers it. Loaded in <head>, so the first paint is already the right theme.
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
    // Two words in the chrome's own type; the stylesheet lights the one that is on.
    button.innerHTML = '<span class="day">day</span><span class="dot" aria-hidden="true">·</span><span class="night">night</span>';
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
