// Apply appearance before the first paint, then enhance each theme control group.
(() => {
  const key = "acta-theme";
  const root = document.documentElement;
  const allowed = ["light", "dark"];
  const systemPreference = matchMedia("(prefers-color-scheme: light)");
  const system = () => systemPreference.matches ? "light" : "dark";
  let stored = null;
  try {
    const value = localStorage.getItem(key);
    stored = value === "paper" ? "light" : allowed.includes(value) ? value : null;
    if (value === "paper") localStorage.setItem(key, "light");
  } catch {
    // Appearance still works when browser storage is unavailable.
  }
  const buttons = [];
  const colors = { dark: "#0E1625", light: "#F7F9FC" };
  const apply = (theme) => {
    root.dataset.theme = theme;
    document.querySelector('meta[name="theme-color"]')?.setAttribute("content", colors[theme]);
    for (const button of buttons) {
      button.setAttribute("aria-pressed", String(button.dataset.theme === theme));
    }
  };
  apply(stored ?? system());
  systemPreference.addEventListener("change", () => {
    if (stored === null) apply(system());
  });

  addEventListener("DOMContentLoaded", () => {
    const icons = {
      light: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5"/>',
      dark: '<path d="M20.5 13A8.5 8.5 0 0 1 11 3.5 8.5 8.5 0 1 0 20.5 13Z"/>'
    };
    for (const host of document.querySelectorAll("[data-theme-controls]")) {
      const group = document.createElement("span");
      group.className = "theme";
      group.setAttribute("role", "group");
      group.setAttribute("aria-label", "Theme");
      for (const theme of allowed) {
        const label = theme[0].toUpperCase() + theme.slice(1);
        const button = document.createElement("button");
        button.type = "button";
        button.dataset.theme = theme;
        button.title = `${label} theme`;
        button.setAttribute("aria-label", `${label} theme`);
        button.innerHTML = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${icons[theme]}</svg>`;
        button.addEventListener("click", () => {
          stored = theme;
          apply(theme);
          try {
            localStorage.setItem(key, theme);
          } catch {
            // Keep the selection for this page when storage is unavailable.
          }
        });
        buttons.push(button);
        group.append(button);
      }
      host.append(group);
    }
    apply(root.dataset.theme);
  });
})();
