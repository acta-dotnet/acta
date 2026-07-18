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
    description: 'Signature dark cockpit',
    background: '#0b0f17',
    panel: '#11161f',
    ink: '#eef2f7',
    muted: '#9aa6b6',
    status: {
      ok: { foreground: '#57d9a3', badgeBackground: '#1d3328', onFill: '#0a0a0a' },
      warn: { foreground: '#e6b366', badgeBackground: '#382c18', onFill: '#0a0a0a' },
      bad: { foreground: '#f08a84', badgeBackground: '#3a2120', onFill: '#0a0a0a' },
      held: { foreground: '#c9a7dc', badgeBackground: '#2a2442', onFill: '#0a0a0a' },
    },
    preview: {
      background: '#0b0f17',
      border: '#232c40',
      sidebar: '#11161f',
      content: '#16302b',
    },
  },
  light: {
    label: 'Light',
    description: 'Clean and bright',
    background: '#f3f6fa',
    panel: '#ffffff',
    ink: '#172033',
    muted: '#5d6b80',
    status: {
      ok: { foreground: '#1b794b', badgeBackground: '#e3f2e9', onFill: '#ffffff' },
      warn: { foreground: '#965f0c', badgeBackground: '#f9efdd', onFill: '#ffffff' },
      bad: { foreground: '#b3322e', badgeBackground: '#f9e4e3', onFill: '#ffffff' },
      held: { foreground: '#6c4e84', badgeBackground: '#f1eaf7', onFill: '#ffffff' },
    },
    preview: {
      background: '#f3f6fa',
      border: '#d7e0ea',
      sidebar: '#edf1f6',
      content: '#ffffff',
    },
  },
  paper: {
    label: 'Paper',
    description: 'Warm paper workspace',
    background: '#eee5d3',
    panel: '#fffaf0',
    ink: '#30291f',
    muted: '#6e6456',
    status: {
      ok: { foreground: '#476f50', badgeBackground: '#e5eee1', onFill: '#ffffff' },
      warn: { foreground: '#875b16', badgeBackground: '#f4e8cf', onFill: '#ffffff' },
      bad: { foreground: '#a7413b', badgeBackground: '#f5dfda', onFill: '#ffffff' },
      held: { foreground: '#705b82', badgeBackground: '#ece5f1', onFill: '#ffffff' },
    },
    preview: {
      background: '#eee5d3',
      border: '#d7c9b3',
      sidebar: '#eee5d6',
      content: '#fffaf0',
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
