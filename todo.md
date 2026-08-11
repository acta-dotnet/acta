- Migrate `ScopeSelector` onto the shared `Dropdown` (`components/Dropdown.svelte`). Blocked on a
  visual check, not on design: the scope trigger is styled by GLOBAL selectors keyed to its wrapper
  (`.side-scope .scope .trigger` in `styles.css`), and the listbox insets `left/right: 12px` where
  Dropdown uses `0`, so the extraction is a CSS change that needs a browser to confirm. `ZonePicker`
  stays its own component on purpose: it lazy-loads the tzdb and does zone-specific matching.
