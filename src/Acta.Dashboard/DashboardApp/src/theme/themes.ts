export type ThemeId = 'acta' | 'light' | 'paper';

export type StatusId = 'ok' | 'warn' | 'bad' | 'held';

interface StatusColors {
  foreground: string;
  badgeBackground: string;
  onFill: string;
}

interface ThemeMetadata {
  label: string;
  description: string;
  background: string;
  panel: string;
  ink: string;
  muted: string;
  status: Record<StatusId, StatusColors>;
  preview: {
    background: string;
    border: string;
    sidebar: string;
    content: string;
  };
}

/**
 * Canonical color metadata used whenever TypeScript needs to reason about a theme. The matching CSS
 * blocks remain complete so the dashboard has safe colors before its JavaScript module executes.
 */
export const THEME_METADATA: Record<ThemeId, ThemeMetadata> = {
  acta: {
    label: 'Acta',
    description: 'Midnight navy and soft mint',
    background: '#0e1625',
    panel: '#19263a',
    ink: '#f2f5fc',
    muted: '#b4bfd1',
    status: {
      ok: { foreground: '#63e6be', badgeBackground: '#183b35', onFill: '#0a0a0a' },
      warn: { foreground: '#f0c27b', badgeBackground: '#3d3223', onFill: '#0a0a0a' },
      bad: { foreground: '#ffaaa5', badgeBackground: '#422c35', onFill: '#0a0a0a' },
      held: { foreground: '#ceb9ed', badgeBackground: '#342e49', onFill: '#0a0a0a' },
    },
    preview: {
      background: '#0e1625',
      border: '#2b3a50',
      sidebar: '#19263a',
      content: '#19263a',
    },
  },
  light: {
    label: 'Light',
    description: 'Cool white and quiet pastels',
    background: '#f7f9fc',
    panel: '#ffffff',
    ink: '#172033',
    muted: '#45536b',
    status: {
      ok: { foreground: '#176143', badgeBackground: '#e9f7f0', onFill: '#ffffff' },
      warn: { foreground: '#855209', badgeBackground: '#fcf2e0', onFill: '#ffffff' },
      bad: { foreground: '#b3322e', badgeBackground: '#fcebea', onFill: '#ffffff' },
      held: { foreground: '#66417f', badgeBackground: '#f3edf9', onFill: '#ffffff' },
    },
    preview: {
      background: '#f7f9fc',
      border: '#dfe5ee',
      sidebar: '#ffffff',
      content: '#ffffff',
    },
  },
  paper: {
    label: 'Paper',
    description: 'Sepia sheets and serif text, easy on the eyes at night',
    background: '#eee5d2',
    panel: '#f3ebdb',
    ink: '#3a2e22',
    muted: '#66574a',
    status: {
      ok: { foreground: '#315d43', badgeBackground: '#d7d5c1', onFill: '#f3ebdb' },
      warn: { foreground: '#7d5310', badgeBackground: '#e4d8c1', onFill: '#f3ebdb' },
      bad: { foreground: '#a03a2f', badgeBackground: '#e7d6c3', onFill: '#f3ebdb' },
      held: { foreground: '#655279', badgeBackground: '#ded3c7', onFill: '#f3ebdb' },
    },
    preview: {
      background: '#eee5d2',
      border: '#d3c4a6',
      sidebar: '#eee5d2',
      content: '#f3ebdb',
    },
  },
};

export const THEMES: ReadonlyArray<{
  id: ThemeId;
  label: string;
  description: string;
  preview: ThemeMetadata['preview'];
}> = (Object.entries(THEME_METADATA) as Array<[ThemeId, ThemeMetadata]>).map(
  ([id, theme]) => ({
    id,
    label: theme.label,
    description: theme.description,
    preview: theme.preview,
  }),
);
