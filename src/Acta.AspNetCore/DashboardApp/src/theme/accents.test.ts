import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  buildAccentTokens,
  contrastRatio,
  MANAGED_ACCENT_TOKENS,
} from './accents.ts';
import { ACCENTS, THEMES } from './appearance.ts';
import { THEME_METADATA } from './themes.ts';

const neutralTokens = ['--bg', '--panel', '--ink', '--muted', '--line'];
const statusTokens = [
  '--ok',
  '--warn',
  '--bad',
  '--held',
  '--badge-ok-bg',
  '--badge-warn-bg',
  '--badge-bad-bg',
  '--badge-held-bg',
];

test('Acta Teal returns the exact golden values', () => {
  assert.deepEqual(buildAccentTokens('teal', 'acta'), {
    '--accent': '#64d8c7',
    '--accent-solid': '#077a70',
    '--on-accent': '#ffffff',
    '--nav-active-bg': '#16302b',
    '--badge-run-bg': '#13302b',
    '--grid': 'rgba(100, 216, 199, 0.05)',
    '--glow': 'rgba(100, 216, 199, 0.1)',
  });
});

test('accent generation emits no neutral or status tokens', () => {
  for (const theme of THEMES) {
    for (const accent of ACCENTS) {
      const names = Object.keys(buildAccentTokens(accent.id, theme.id));
      for (const token of [...neutralTokens, ...statusTokens]) {
        assert.ok(!names.includes(token), `${theme.id}/${accent.id} emitted ${token}`);
      }
    }
  }
});

test('all accents emit the complete managed accent contract', () => {
  const expected = [...MANAGED_ACCENT_TOKENS].sort();
  for (const theme of THEMES) {
    for (const accent of ACCENTS) {
      const names = Object.keys(buildAccentTokens(accent.id, theme.id)).sort();
      assert.deepEqual(names, expected, `${theme.id}/${accent.id}`);
    }
  }
});

test('solid accent foregrounds meet WCAG AA contrast', () => {
  for (const theme of THEMES) {
    for (const accent of ACCENTS) {
      const tokens = buildAccentTokens(accent.id, theme.id);
      assert.ok(
        contrastRatio(tokens['--accent-solid'], tokens['--on-accent']) >= 4.5,
        `${theme.id}/${accent.id}`,
      );
    }
  }
});

test('accent text and focus colors meet WCAG AA on every theme surface', () => {
  for (const theme of THEMES) {
    const surfaces = THEME_METADATA[theme.id];
    for (const accent of ACCENTS) {
      const accentText = buildAccentTokens(accent.id, theme.id)['--accent'];
      assert.ok(
        contrastRatio(accentText, surfaces.background) >= 4.5,
        `${theme.id}/${accent.id} on background`,
      );
      assert.ok(
        contrastRatio(accentText, surfaces.panel) >= 4.5,
        `${theme.id}/${accent.id} on panel`,
      );
    }
  }
});

test('theme text meets WCAG AA on every theme surface', () => {
  for (const theme of THEMES) {
    const colors = THEME_METADATA[theme.id];
    for (const [name, foreground] of [['ink', colors.ink], ['muted', colors.muted]] as const) {
      assert.ok(
        contrastRatio(foreground, colors.background) >= 4.5,
        `${theme.id}/${name} on background`,
      );
      assert.ok(
        contrastRatio(foreground, colors.panel) >= 4.5,
        `${theme.id}/${name} on panel`,
      );
    }
  }
});

test('status badge and filled-status foregrounds meet WCAG AA', () => {
  for (const theme of THEMES) {
    const colors = THEME_METADATA[theme.id];
    for (const [status, tokens] of Object.entries(colors.status)) {
      assert.ok(
        contrastRatio(tokens.foreground, tokens.badgeBackground) >= 4.5,
        `${theme.id}/${status} badge`,
      );
      assert.ok(
        contrastRatio(tokens.foreground, colors.background) >= 4.5,
        `${theme.id}/${status} on background`,
      );
      assert.ok(
        contrastRatio(tokens.foreground, colors.panel) >= 4.5,
        `${theme.id}/${status} on panel`,
      );
      assert.ok(
        contrastRatio(tokens.foreground, tokens.onFill) >= 4.5,
        `${theme.id}/${status} filled foreground`,
      );
    }
  }
});

test('switching token maps leaves no unmanaged output', () => {
  const managed = new Set<string>(MANAGED_ACCENT_TOKENS);
  for (const theme of THEMES) {
    for (const accent of ACCENTS) {
      for (const token of Object.keys(buildAccentTokens(accent.id, theme.id))) {
        assert.ok(managed.has(token), `${theme.id}/${accent.id} emitted ${token}`);
      }
    }
  }
});

test('Acta uses dark scales while Light and Paper use light scales', () => {
  assert.equal(buildAccentTokens('blue', 'acta')['--accent'], '#70b8ff');
  assert.equal(buildAccentTokens('blue', 'acta')['--nav-active-bg'], '#0d2847');
  assert.equal(buildAccentTokens('blue', 'light')['--accent'], '#113264');
  assert.equal(buildAccentTokens('blue', 'paper')['--accent'], '#113264');
  assert.equal(buildAccentTokens('blue', 'light')['--nav-active-bg'], '#e6f4fe');
  assert.equal(buildAccentTokens('blue', 'paper')['--nav-active-bg'], '#e6f4fe');
});
