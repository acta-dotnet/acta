import { writable } from 'svelte/store';
import {
  accentSwatch,
  buildAccentTokens,
  MANAGED_ACCENT_TOKENS,
  type AccentId,
} from './accents.ts';
import { THEMES, type ThemeId } from './themes.ts';

export type { AccentId } from './accents.ts';
export type { ThemeId } from './themes.ts';
export { THEMES } from './themes.ts';

export type TextSize = 'small' | 'default' | 'large';

export interface AppearanceSettings {
  version: 1;
  theme: ThemeId;
  accent: AccentId;
  textSize: TextSize;
}

export const DEFAULT_APPEARANCE: AppearanceSettings = {
  version: 1,
  theme: 'acta',
  accent: 'teal',
  textSize: 'default',
};

export const ACCENTS: ReadonlyArray<{
  id: AccentId;
  label: string;
  swatch: string;
}> = [
  { id: 'teal', label: 'Teal', swatch: accentSwatch('teal') },
  { id: 'blue', label: 'Blue', swatch: accentSwatch('blue') },
  { id: 'indigo', label: 'Indigo', swatch: accentSwatch('indigo') },
  { id: 'violet', label: 'Violet', swatch: accentSwatch('violet') },
  { id: 'green', label: 'Green', swatch: accentSwatch('green') },
  { id: 'amber', label: 'Amber', swatch: accentSwatch('amber') },
  { id: 'crimson', label: 'Crimson', swatch: accentSwatch('crimson') },
  { id: 'pink', label: 'Pink', swatch: accentSwatch('pink') },
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
        && isThemeId(parsed.theme)
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
    theme: legacyMode === 'light' ? 'light' : 'acta',
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

  const root = document.documentElement;
  root.dataset.theme = settings.theme;
  root.dataset.accent = settings.accent;
  root.dataset.textSize = settings.textSize;
  root.style.colorScheme = settings.theme === 'acta' ? 'dark' : 'light';

  const accentTokens = buildAccentTokens(settings.accent, settings.theme);
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

export function setTheme(theme: ThemeId): void {
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
      return 34;
    case 'large':
      return 44;
    default:
      return 38;
  }
}
