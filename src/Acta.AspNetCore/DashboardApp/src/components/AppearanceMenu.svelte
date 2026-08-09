<script lang="ts">
  import { tick } from 'svelte';
  import {
    ACCENTS,
    TEXT_SIZES,
    THEME_CHOICES,
    appearance,
    resetAppearance,
    resolveTheme,
    setAccent,
    setTextSize,
    setTheme,
  } from '../theme/appearance';
  import { accentSwatch } from '../theme/accents';

  let open = $state(false);
  let rootElement: HTMLElement | null = $state(null);
  let triggerElement: HTMLButtonElement | null = $state(null);
  let themeInputs: HTMLInputElement[] = $state([]);

  let selectedAccent = $derived(ACCENTS.find((accent) => accent.id === $appearance.accent));
  let activeTheme = $derived(resolveTheme($appearance.theme));

  async function toggle(): Promise<void> {
    open = !open;
    if (open) {
      await tick();
      themeInputs[THEME_CHOICES.findIndex((theme) => theme.id === $appearance.theme)]?.focus();
    }
  }

  function close(options: { restoreFocus: boolean }): void {
    if (!open) return;
    open = false;
    if (options.restoreFocus) {
      triggerElement?.focus();
    }
  }

  function handleWindowPointerDown(event: PointerEvent): void {
    if (
      open
      && rootElement
      && event.target instanceof Node
      && !rootElement.contains(event.target)
    ) {
      close({ restoreFocus: false });
    }
  }

  function handleWindowKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Escape' && open) {
      event.preventDefault();
      close({ restoreFocus: true });
    }
  }
</script>

<svelte:window
  onpointerdown={handleWindowPointerDown}
  onkeydown={handleWindowKeyDown}
/>

<div class="appearance-menu" bind:this={rootElement}>
  <button
    type="button"
    class="trigger"
    bind:this={triggerElement}
    aria-haspopup="dialog"
    aria-controls="appearance-popover"
    aria-expanded={open}
    onclick={toggle}
  >
    <span class="trigger-copy">Appearance</span>
    <!-- The accent swatch trails the label so it cannot push the text off the sidebar's shared
         left margin: "Appearance" starts exactly where the nav items do. -->
    <span
      class="trigger-dot"
      style:background={selectedAccent ? accentSwatch(selectedAccent.id, activeTheme) : undefined}
      aria-hidden="true"
    ></span>
  </button>

  {#if open}
    <div
      id="appearance-popover"
      class="popover"
      role="dialog"
      aria-modal="false"
      aria-labelledby="appearance-title"
    >
      <div class="popover-title" id="appearance-title">Appearance</div>

      <fieldset class="theme-options">
        <legend>Theme</legend>
        <div class="theme-grid">
          {#each THEME_CHOICES as theme, index}
            <label class="theme-card" class:wide={theme.id === 'system'} class:selected={$appearance.theme === theme.id}>
              <input
                bind:this={themeInputs[index]}
                type="radio"
                name="appearance-theme"
                value={theme.id}
                checked={$appearance.theme === theme.id}
                onchange={() => setTheme(theme.id)}
              />
              <span
                class="theme-preview"
                style:--preview-bg={theme.preview.background}
                style:--preview-border={theme.preview.border}
                style:--preview-sidebar={theme.preview.sidebar}
                style:--preview-content={theme.preview.content}
                aria-hidden="true"
              >
                <span></span><span></span><span></span>
              </span>
              <span class="theme-label">{theme.label}</span>
              <span class="theme-description">{theme.description}</span>
            </label>
          {/each}
        </div>
      </fieldset>

      <fieldset class="accent-options">
        <legend>Accent</legend>
        <div class="accent-grid">
          {#each ACCENTS as accent}
            <label
              class="accent-choice"
              class:selected={$appearance.accent === accent.id}
              title={accent.label}
            >
              <input
                type="radio"
                name="appearance-accent"
                value={accent.id}
                checked={$appearance.accent === accent.id}
                onchange={() => setAccent(accent.id)}
              />
              <span class="accent-swatch" style:background={accentSwatch(accent.id, activeTheme)} aria-hidden="true">
                {#if $appearance.accent === accent.id}<span class="check">✓</span>{/if}
              </span>
              <span>{accent.label}</span>
            </label>
          {/each}
        </div>
      </fieldset>

      <fieldset class="text-size-options">
        <legend>Text size</legend>
        <div class="text-size-grid">
          {#each TEXT_SIZES as size}
            <label class="text-size-choice" class:selected={$appearance.textSize === size.id}>
              <input
                type="radio"
                name="appearance-text-size"
                value={size.id}
                checked={$appearance.textSize === size.id}
                onchange={() => setTextSize(size.id)}
              />
              <span class="size-preview size-{size.id}" aria-hidden="true">A</span>
              <span>{size.label}</span>
            </label>
          {/each}
        </div>
      </fieldset>

      <button type="button" class="restore" onclick={resetAppearance}>Restore defaults</button>
    </div>
  {/if}
</div>

<style>
  .appearance-menu { position: relative; }
  .trigger {
    display: inline-flex;
    align-items: center;
    gap: 9px;
    text-align: left;
  }
  .trigger-dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--line) 70%, transparent);
    flex: none;
    margin-left: auto;
  }
  .trigger-copy { min-width: 0; }

  .popover {
    position: absolute;
    left: 0;
    bottom: calc(100% + 8px);
    z-index: 20;
    width: 332px;
    max-width: calc(100vw - 28px);
    max-height: calc(100dvh - 96px);
    overflow-y: auto;
    overscroll-behavior: contain;
    padding: 14px;
    display: flex;
    flex-direction: column;
    gap: 14px;
    background: var(--panel);
    border: 1px solid var(--line);
    border-radius: var(--radius-panel);
    box-shadow: 0 8px 30px var(--shadow);
  }
  .popover-title {
    color: var(--muted);
    font-size: var(--text-xs);
    font-weight: 700;
    letter-spacing: 0.02em;
  }
  fieldset { min-width: 0; margin: 0; padding: 0; border: 0; }
  legend {
    margin-bottom: 7px;
    padding: 0;
    color: var(--ink);
    font-size: var(--text-sm);
    font-weight: 600;
  }
  label { cursor: pointer; }
  label:has(input:focus-visible) { outline: 2px solid var(--accent); outline-offset: 2px; }
  input[type='radio'] {
    position: absolute;
    width: 1px;
    height: 1px;
    margin: -1px;
    opacity: 0;
    pointer-events: none;
  }

  .theme-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 7px; }
  /* System is a policy over the three looks, not a fourth look: a slim full-width row on top,
     with the three theme cards back on one line below it. */
  .theme-card.wide {
    grid-column: 1 / -1;
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 7px 10px;
  }
  .theme-card.wide .theme-preview { width: 72px; height: 26px; flex: none; }
  .theme-card.wide .theme-description { margin-left: auto; text-align: right; }
  .theme-card {
    min-width: 0;
    padding: 7px;
    display: grid;
    gap: 4px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
  }
  .theme-card:hover,
  .theme-card.selected { border-color: var(--accent); }
  .theme-card.selected { box-shadow: inset 0 0 0 1px var(--accent); }
  .theme-preview {
    height: 38px;
    padding: 5px;
    display: grid;
    grid-template-columns: 25% 1fr;
    grid-template-rows: repeat(2, 1fr);
    gap: 3px;
    background: var(--preview-bg);
    border: 1px solid var(--preview-border);
    overflow: hidden;
  }
  .theme-preview span { display: block; }
  .theme-preview span:first-child { grid-row: 1 / -1; background: var(--preview-sidebar); }
  .theme-preview span:not(:first-child) { background: var(--preview-content); }
  .theme-label { font-size: var(--text-sm); font-weight: 600; }
  .theme-description { color: var(--muted); font-size: var(--text-2xs); line-height: 1.2; }

  .accent-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 6px; }
  .accent-choice {
    min-width: 0;
    padding: 5px 2px;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 4px;
    color: var(--muted);
    font-size: var(--text-2xs);
    border: 1px solid transparent;
    border-radius: var(--radius-control);
  }
  .accent-choice:hover { background: var(--panel-subtle); color: var(--ink); }
  .accent-swatch {
    width: 24px;
    height: 24px;
    display: grid;
    place-items: center;
    border-radius: 50%;
    /* Defines the swatch against the popover it sits on. A fixed black at 14% disappeared
       entirely on the dark theme, leaving darker swatches edgeless. */
    box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--ink) 22%, transparent);
  }
  .accent-choice.selected .accent-swatch {
    outline: 2px solid var(--panel);
    box-shadow: 0 0 0 3px var(--accent);
  }
  .check {
    width: 16px;
    height: 16px;
    display: grid;
    place-items: center;
    border-radius: 50%;
    background: rgba(0, 0, 0, 0.62);
    color: #fff;
    font-size: 12px;
    font-weight: 800;
    line-height: 1;
  }

  .text-size-grid {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    overflow: hidden;
  }
  .text-size-choice {
    min-height: 45px;
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 6px;
    color: var(--muted);
    font-size: var(--text-sm);
  }
  .text-size-choice + .text-size-choice { border-left: 1px solid var(--line); }
  .text-size-choice:hover { color: var(--accent); }
  .text-size-choice.selected { background: var(--nav-active-bg); color: var(--accent); font-weight: 600; }
  .size-preview { line-height: 1; }
  .size-small { font-size: 12px; }
  .size-default { font-size: 16px; }
  .size-large { font-size: 20px; }

  .restore { width: 100%; }

  @media (max-width: 800px) {
    .popover {
      position: fixed;
      left: 14px;
      right: 14px;
      bottom: 14px;
      width: auto;
      max-width: none;
      max-height: calc(100dvh - 83px);
    }
  }
</style>
