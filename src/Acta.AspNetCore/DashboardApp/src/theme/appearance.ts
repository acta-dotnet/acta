import { writable } from 'svelte/store';
import {
  buildAccentTokens,
  MANAGED_ACCENT_TOKENS,
  type AccentId,
} from './accents.ts';
import { THEMES, type ThemeId } from './themes.ts';

export type { AccentId } from './accents.ts';
export type { ThemeId } from './themes.ts';
export { THEMES } from './themes.ts';

/** The theme picker's entries: the OS-following choice first, then the concrete themes. */
export const THEME_CHOICES: ReadonlyArray<{
  id: ThemeChoice;
  label: string;
  description: string;
  preview: (typeof THEMES)[number]['preview'];
}> = [
  {
    id: 'system',
    label: 'System',
    description: 'Follow the OS light/dark setting',
    preview: { background: '#0b0f17', border: '#232c40', sidebar: '#11161f', content: '#f3f6fa' },
  },
  ...THEMES,
];

export type TextSize = 'small' | 'default' | 'large';

/** A concrete theme, or 'system' which follows the OS light/dark preference. */
export type ThemeChoice = ThemeId | 'system';

export interface AppearanceSettings {
  version: 1;
  theme: ThemeChoice;
  accent: AccentId;
  textSize: TextSize;
}

export const DEFAULT_APPEARANCE: AppearanceSettings = {
  version: 1,
  theme: 'system',
  accent: 'teal',
  textSize: 'default',
};

// No swatch here: it depends on the active theme, so the menu resolves it per render via
// accentSwatch(id, theme). Paper renders a warm family, not the cool Radix ramps.
export const ACCENTS: ReadonlyArray<{
  id: AccentId;
  label: string;
}> = [
  { id: 'teal', label: 'Teal' },
  { id: 'blue', label: 'Blue' },
  { id: 'indigo', label: 'Indigo' },
  { id: 'violet', label: 'Violet' },
  { id: 'green', label: 'Green' },
  { id: 'amber', label: 'Amber' },
  { id: 'crimson', label: 'Crimson' },
  { id: 'pink', label: 'Pink' },
];

export const TEXT_SIZES: ReadonlyArray<{
  id: TextSize;
  label: string;
}> = [
  { id: 'small', label: 'Small' },
  { id: 'default', label: 'Default' },
  { id: 'large', label: 'Large' },
];

const STORAGE_KEY = 'acta-appearance-v1';

function safeGet(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function safeSet(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    // The in-memory setting still applies for this browser session.
  }
}

export function isThemeId(value: unknown): value is ThemeId {
  return value === 'acta' || value === 'light' || value === 'paper';
}

export function isThemeChoice(value: unknown): value is ThemeChoice {
  return value === 'system' || isThemeId(value);
}

const prefersDark = typeof matchMedia === 'undefined' ? null : matchMedia('(prefers-color-scheme: dark)');

export function resolveTheme(choice: ThemeChoice): ThemeId {
  return choice === 'system' ? (prefersDark?.matches !== false ? 'acta' : 'light') : choice;
}

export function isAccentId(value: unknown): value is AccentId {
  return ACCENTS.some((accent) => accent.id === value);
}

export function isTextSize(value: unknown): value is TextSize {
  return value === 'small' || value === 'default' || value === 'large';
}

export function loadAppearance(): AppearanceSettings {
  const current = safeGet(STORAGE_KEY);

  if (current) {
    try {
      const parsed: unknown = JSON.parse(current);
      if (
        typeof parsed === 'object'
        && parsed !== null
        && 'version' in parsed
        && parsed.version === 1
        && 'theme' in parsed
        && isThemeChoice(parsed.theme)
        && 'accent' in parsed
        && isAccentId(parsed.accent)
        && 'textSize' in parsed
        && isTextSize(parsed.textSize)
      ) {
        return {
          version: 1,
          theme: parsed.theme,
          accent: parsed.accent,
          textSize: parsed.textSize,
        };
      }
    } catch {
      // Continue to legacy migration.
    }
  }

  const legacyMode = safeGet('acta-theme');
  const legacyPalette = safeGet('acta-palette');

  return {
    version: 1,
    theme: legacyMode === 'light' ? 'light' : legacyMode ? 'acta' : 'system',
    accent: legacyPalette === 'acta'
      ? 'teal'
      : isAccentId(legacyPalette)
        ? legacyPalette
        : 'teal',
    textSize: 'default',
  };
}

const initialAppearance = loadAppearance();

export const appearance = writable<AppearanceSettings>(initialAppearance);

function applyAppearance(settings: AppearanceSettings): void {
  if (typeof document === 'undefined') {
    return;
  }

  const theme = resolveTheme(settings.theme);
  const root = document.documentElement;
  root.dataset.theme = theme;
  root.dataset.accent = settings.accent;
  root.dataset.textSize = settings.textSize;
  root.style.colorScheme = theme === 'acta' ? 'dark' : 'light';

  const accentTokens = buildAccentTokens(settings.accent, theme);
  for (const token of MANAGED_ACCENT_TOKENS) {
    const value = accentTokens[token];
    if (value) {
      root.style.setProperty(token, value);
    } else {
      root.style.removeProperty(token);
    }
  }
}

appearance.subscribe((settings) => {
  applyAppearance(settings);
  safeSet(STORAGE_KEY, JSON.stringify(settings));
});

// Re-emit on OS preference change so a 'system' choice re-resolves (and dependents re-render).
prefersDark?.addEventListener('change', () => {
  appearance.update((current) => ({ ...current }));
});

export function setTheme(theme: ThemeChoice): void {
  appearance.update((current) => ({ ...current, theme }));
}

export function setAccent(accent: AccentId): void {
  appearance.update((current) => ({ ...current, accent }));
}

export function setTextSize(textSize: TextSize): void {
  appearance.update((current) => ({ ...current, textSize }));
}

export function resetAppearance(): void {
  appearance.set(DEFAULT_APPEARANCE);
}

export function textSizeRowHeight(textSize: TextSize): number {
  switch (textSize) {
    case 'small':
      return 38;
    case 'large':
      return 48;
    default:
      return 42;
  }
}
