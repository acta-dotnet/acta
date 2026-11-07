// Day and night. The visitor's stored choice wins, then the system's preference; the "day · night" words the script
// adds to the end of the footer each set their own theme and remember it. Loaded in <head>, so the first paint is
// already the right theme.
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
  let buttons = [];
  const apply = (theme) => {
    root.dataset.theme = theme;
    for (const button of buttons) {
      button.setAttribute("aria-pressed", String(button.dataset.theme === theme));
    }
  };
  apply(stored === "light" || stored === "dark" ? stored : system());
  matchMedia("(prefers-color-scheme: light)").addEventListener("change", () => {
    if (stored === null) apply(system());
  });

  addEventListener("DOMContentLoaded", () => {
    const footer = document.querySelector("footer");
    if (!footer) return;
    const group = document.createElement("span");
    group.className = "theme";
    group.setAttribute("role", "group");
    group.setAttribute("aria-label", "Theme");
    const word = (theme, label) => {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.theme = theme;
      button.textContent = label;
      button.title = `Switch to ${label}`;
      button.addEventListener("click", () => {
        stored = theme;
        apply(theme);
        try {
          localStorage.setItem(key, theme);
        } catch {
          // A private window keeps the choice for this page only.
        }
      });
      return button;
    };
    const dot = document.createElement("span");
    dot.className = "dot";
    dot.setAttribute("aria-hidden", "true");
    dot.textContent = "·";
    buttons = [word("light", "day"), word("dark", "night")];
    group.append(buttons[0], dot, buttons[1]);
    const sep = document.createElement("span");
    sep.className = "sep";
    sep.setAttribute("aria-hidden", "true");
    sep.textContent = "·";
    footer.append(sep, group);
    apply(root.dataset.theme);
  });
})();
