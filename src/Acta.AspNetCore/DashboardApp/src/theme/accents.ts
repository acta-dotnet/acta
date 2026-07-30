import { THEME_METADATA, type ThemeId } from './themes.ts';

export type AccentId =
  | 'teal'
  | 'blue'
  | 'indigo'
  | 'violet'
  | 'green'
  | 'amber'
  | 'crimson'
  | 'pink';

type Scale = readonly [
  string,
  string,
  string,
  string,
  string,
  string,
  string,
  string,
  string,
  string,
  string,
  string,
];

type AccentSurface = 'dark' | 'light';
type ScalePair = { light: Scale; dark: Scale };

const scale = (...colors: string[]) => colors as unknown as Scale;

const indigo: ScalePair = {
  light: scale('#fdfdfe', '#f7f9ff', '#edf2fe', '#e1e9ff', '#d2deff', '#c1d0ff', '#abbdf9', '#8da4ef', '#3e63dd', '#3358d4', '#3a5bc7', '#1f2d5c'),
  dark: scale('#11131f', '#141726', '#182449', '#1d2e62', '#253974', '#304384', '#3a4f97', '#435db1', '#3e63dd', '#5472e4', '#9eb1ff', '#d6e1ff'),
};

const ACCENT_SCALES: Record<AccentId, ScalePair> = {
  blue: {
    light: scale('#fbfdff', '#f4faff', '#e6f4fe', '#d5efff', '#c2e5ff', '#acd8fc', '#8ec8f6', '#5eb1ef', '#0090ff', '#0588f0', '#0d74ce', '#113264'),
    dark: scale('#0d1520', '#111927', '#0d2847', '#003362', '#004074', '#104d87', '#205d9e', '#2870bd', '#0090ff', '#3b9eff', '#70b8ff', '#c2e6ff'),
  },
  indigo,
  violet: {
    light: scale('#fdfcfe', '#faf8ff', '#f4f0fe', '#ebe4ff', '#e1d9ff', '#d4cafe', '#c2b5f5', '#aa99ec', '#6e56cf', '#654dc4', '#6550b9', '#2f265f'),
    dark: scale('#14121f', '#1b1525', '#291f43', '#33255b', '#3c2e69', '#473876', '#56468b', '#6958ad', '#6e56cf', '#7d66d9', '#baa7ff', '#e2ddfe'),
  },
  teal: {
    light: scale('#fafefd', '#f3fbf9', '#e0f8f3', '#ccf3ea', '#b8eae0', '#a1ded2', '#83cdc1', '#53b9ab', '#12a594', '#0d9b8a', '#008573', '#0d3d38'),
    dark: scale('#0d1514', '#111c1b', '#0d2d2a', '#023b37', '#084843', '#145750', '#1c6961', '#207e73', '#12a594', '#0eb39e', '#0bd8b6', '#adf0dd'),
  },
  green: {
    light: scale('#fbfefc', '#f4fbf6', '#e6f6eb', '#d6f1df', '#c4e8d1', '#adddc0', '#8eceaa', '#5bb98b', '#30a46c', '#2b9a66', '#218358', '#193b2d'),
    dark: scale('#0e1512', '#121b17', '#132d21', '#113b29', '#174933', '#20573e', '#28684a', '#2f7c57', '#30a46c', '#33b074', '#3dd68c', '#b1f1cb'),
  },
  amber: {
    light: scale('#fefdfb', '#fefbe9', '#fff7c2', '#ffee9c', '#fbe577', '#f3d673', '#e9c162', '#e2a336', '#ffc53d', '#ffba18', '#ab6400', '#4f3422'),
    dark: scale('#16120c', '#1d180f', '#302008', '#3f2700', '#4d3000', '#5c3d05', '#714f19', '#8f6424', '#ffc53d', '#ffd60a', '#ffca16', '#ffe7b3'),
  },
  crimson: {
    light: scale('#fffcfd', '#fef7f9', '#ffe9f0', '#fedce7', '#facedd', '#f3bed1', '#eaacc3', '#e093b2', '#e93d82', '#df3478', '#cb1d63', '#621639'),
    dark: scale('#191114', '#201318', '#381525', '#4d122f', '#5c1839', '#6d2545', '#873356', '#b0436e', '#e93d82', '#ee518a', '#ff92ad', '#fdd3e8'),
  },
  pink: {
    light: scale('#fffcfe', '#fef7fb', '#fee9f5', '#fbdcef', '#f6cee7', '#efbfdd', '#e7acd0', '#dd93c2', '#d6409f', '#cf3897', '#c2298a', '#651249'),
    dark: scale('#191117', '#21121d', '#37172f', '#4b143d', '#591c47', '#692955', '#833869', '#a84885', '#d6409f', '#de51a8', '#ff8dcc', '#fdd1ea'),
  },
};

/**
 * Paper runs its own accent family. The eight Radix ramps are cool light scales, and on a warm
 * ground even their muted steps read as wrong rather than as colour - magenta on beige has no
 * good answer. So each accent id maps to the warm equivalent of that hue instead, and four of
 * them are simply paper's own status colours, which is what keeps the family coherent: teal is
 * the moss of --ok, amber the ochre of --warn, crimson the seal red of --bad, violet the plum of
 * --held. Every value sits at or below 0.12 relative luminance so it clears 4.5:1 against both
 * paper surfaces (#f4ecd9 and the deeper #ebe0c8), which accents.test.ts verifies.
 */
const PAPER_ACCENTS: Record<AccentId, string> = {
  teal: '#3f6147',     // moss
  green: '#4a6b2f',    // olive
  amber: '#7d5310',    // ochre
  crimson: '#a03a2f',  // seal red
  pink: '#8c4a52',     // clay rose
  violet: '#655279',   // plum
  indigo: '#4a5570',   // slate indigo
  blue: '#3c5a6b',     // slate teal
};

export const MANAGED_ACCENT_TOKENS = [
  '--accent',
  '--accent-solid',
  '--on-accent',
  '--nav-active-bg',
  '--badge-run-bg',
  '--grid',
  '--glow',
] as const;

export type AccentToken = (typeof MANAGED_ACCENT_TOKENS)[number];
export type AccentTokens = Record<AccentToken, string>;

export function accentSurface(theme: ThemeId): AccentSurface {
  return theme === 'acta' ? 'dark' : 'light';
}

/** The picker swatch, so what it shows is what that theme will actually render. */
export function accentSwatch(accent: AccentId, theme?: ThemeId): string {
  return theme === 'paper' ? PAPER_ACCENTS[accent] : ACCENT_SCALES[accent].dark[8];
}

function rgba(hex: string, alpha: number): string {
  const value = Number.parseInt(hex.slice(1), 16);
  return `rgba(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255}, ${alpha})`;
}

export function relativeLuminance(hex: string): number {
  const value = Number.parseInt(hex.slice(1), 16);
  const channels = [
    (value >> 16) & 255,
    (value >> 8) & 255,
    value & 255,
  ].map((channel) => {
    const srgb = channel / 255;
    return srgb <= 0.03928
      ? srgb / 12.92
      : ((srgb + 0.055) / 1.055) ** 2.4;
  });

  return (
    0.2126 * channels[0]
    + 0.7152 * channels[1]
    + 0.0722 * channels[2]
  );
}

export function contrastRatio(first: string, second: string): number {
  const firstLum = relativeLuminance(first);
  const secondLum = relativeLuminance(second);
  const lighter = Math.max(firstLum, secondLum);
  const darker = Math.min(firstLum, secondLum);
  return (lighter + 0.05) / (darker + 0.05);
}

function readableForeground(background: string): string {
  const white = '#ffffff';
  const dark = '#0a0a0a';
  return contrastRatio(background, white) >= contrastRatio(background, dark)
    ? white
    : dark;
}

function contrastSafeSolid(accentScale: Scale): { solid: string; foreground: string } {
  // Step 9 is the Radix solid-control step. Adjacent steps are considered if a future scale ever
  // makes neither supported foreground meet WCAG AA.
  for (const index of [8, 9, 7, 10, 6, 11]) {
    const solid = accentScale[index];
    const foreground = readableForeground(solid);
    if (contrastRatio(solid, foreground) >= 4.5) {
      return { solid, foreground };
    }
  }

  // Black or white always supplies at least 4.5:1 for an opaque sRGB color; this is defensive.
  const solid = accentScale[8];
  return { solid, foreground: readableForeground(solid) };
}

function readableAccentText(accentScale: Scale, theme: ThemeId): string {
  const { background, panel } = THEME_METADATA[theme];

  // Prefer the Radix text step, then its stronger text step. The remaining candidates are a
  // defensive fallback for future scales whose text steps do not clear both theme surfaces.
  for (const index of [10, 11, 9, 8]) {
    const candidate = accentScale[index];
    if (
      contrastRatio(candidate, background) >= 4.5
      && contrastRatio(candidate, panel) >= 4.5
    ) {
      return candidate;
    }
  }

  return accentScale[11];
}

export function buildAccentTokens(accent: AccentId, theme: ThemeId): AccentTokens {
  if (theme === 'acta' && accent === 'teal') {
    return {
      '--accent': '#64d8c7',
      '--accent-solid': '#077a70',
      '--on-accent': '#ffffff',
      '--nav-active-bg': '#16302b',
      '--badge-run-bg': '#13302b',
      '--grid': 'rgba(100, 216, 199, 0.05)',
      '--glow': 'rgba(100, 216, 199, 0.1)',
    };
  }

  if (theme === 'paper') {
    const solid = PAPER_ACCENTS[accent];
    // The tints stay translucent so they composite over whatever the row already is - paper,
    // zebra banding, or the deeper chrome tone - instead of pinning one opaque colour.
    return {
      '--accent': solid,
      '--accent-solid': solid,
      '--on-accent': readableForeground(solid),
      '--nav-active-bg': rgba(solid, 0.13),
      '--badge-run-bg': rgba(solid, 0.16),
      '--grid': rgba(solid, 0.05),
      '--glow': rgba(solid, 0.1),
    };
  }

  const surface = accentSurface(theme);
  const accentScale = ACCENT_SCALES[accent][surface];
  const { solid, foreground } = contrastSafeSolid(accentScale);
  const tint = surface === 'dark' ? accentScale[7] : accentScale[8];

  return {
    '--accent': readableAccentText(accentScale, theme),
    '--accent-solid': solid,
    '--on-accent': foreground,
    '--nav-active-bg': accentScale[2],
    '--badge-run-bg': accentScale[2],
    '--grid': rgba(tint, 0.05),
    '--glow': rgba(tint, 0.1),
  };
}
