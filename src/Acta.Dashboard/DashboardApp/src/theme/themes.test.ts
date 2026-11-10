import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { THEMES, THEME_METADATA } from './themes.ts';

const css = readFileSync(new URL('../styles.css', import.meta.url), 'utf8');

function themeCssBlock(theme: string): string {
  const pattern = new RegExp(`:root\\[data-theme='${theme}'\\] \\{([\\s\\S]*?)\\n\\}`);
  const match = css.match(pattern);
  assert.ok(match, `missing complete CSS block for ${theme}`);
  return match[1];
}

test('theme metadata stays aligned with the complete CSS theme definitions', () => {
  for (const theme of THEMES) {
    const block = themeCssBlock(theme.id);
    const colors = THEME_METADATA[theme.id];
    const expected = {
      '--bg': colors.background,
      '--panel': colors.panel,
      '--ink': colors.ink,
      '--muted': colors.muted,
      '--ok': colors.status.ok.foreground,
      '--warn': colors.status.warn.foreground,
      '--bad': colors.status.bad.foreground,
      '--held': colors.status.held.foreground,
      '--badge-ok-bg': colors.status.ok.badgeBackground,
      '--badge-warn-bg': colors.status.warn.badgeBackground,
      '--badge-bad-bg': colors.status.bad.badgeBackground,
      '--badge-held-bg': colors.status.held.badgeBackground,
      '--on-ok': colors.status.ok.onFill,
      '--on-warn': colors.status.warn.onFill,
      '--on-bad': colors.status.bad.onFill,
      '--on-held': colors.status.held.onFill,
    };

    for (const [token, value] of Object.entries(expected)) {
      assert.match(block, new RegExp(`${token}:\\s*${value.replace('#', '\\#')};`), `${theme.id}/${token}`);
    }
  }
});

test('the root CSS contains the golden Acta accent fallback contract', () => {
  for (const declaration of [
    '--accent: #63e6be;',
    '--accent-solid: #63e6be;',
    '--on-accent: #10251e;',
    '--nav-active-bg: #1c3737;',
    '--badge-run-bg: #183b35;',
    '--grid: rgba(99, 230, 190, 0.05);',
    '--glow: rgba(99, 230, 190, 0.1);',
  ]) {
    assert.ok(css.includes(declaration), declaration);
  }
});
