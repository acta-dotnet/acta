import { afterEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { get } from 'svelte/store';
import {
  DEFAULT_APPEARANCE,
  appearance,
  loadAppearance,
  resetAppearance,
  textSizeRowHeight,
} from './appearance.ts';

const records = new Map<string, string>();

function installStorage(): void {
  records.clear();
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: {
      getItem: (key: string) => records.get(key) ?? null,
      setItem: (key: string, value: string) => records.set(key, value),
    },
  });
}

afterEach(() => {
  Reflect.deleteProperty(globalThis, 'localStorage');
  resetAppearance();
});

test('default settings', () => {
  installStorage();
  assert.deepEqual(loadAppearance(), DEFAULT_APPEARANCE);
});

test('valid persisted settings', () => {
  installStorage();
  records.set('acta-appearance-v1', JSON.stringify({
    version: 1,
    theme: 'paper',
    accent: 'violet',
    textSize: 'large',
  }));
  assert.deepEqual(loadAppearance(), {
    version: 1,
    theme: 'paper',
    accent: 'violet',
    textSize: 'large',
  });
});

for (const [name, value] of [
  ['invalid JSON', '{'],
  ['invalid theme', JSON.stringify({ version: 1, theme: 'dark', accent: 'teal', textSize: 'default' })],
  ['invalid accent', JSON.stringify({ version: 1, theme: 'acta', accent: 'orange', textSize: 'default' })],
  ['invalid text size', JSON.stringify({ version: 1, theme: 'acta', accent: 'teal', textSize: 'compact' })],
] as const) {
  test(name, () => {
    installStorage();
    records.set('acta-appearance-v1', value);
    assert.deepEqual(loadAppearance(), DEFAULT_APPEARANCE);
  });
}

test('storage unavailable', () => {
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: {
      getItem: () => { throw new Error('unavailable'); },
      setItem: () => { throw new Error('unavailable'); },
    },
  });
  assert.deepEqual(loadAppearance(), DEFAULT_APPEARANCE);
  assert.doesNotThrow(() => resetAppearance());
});

test('persisted system theme is honored', () => {
  installStorage();
  records.set('acta-appearance-v1', JSON.stringify({ version: 1, theme: 'system', accent: 'teal', textSize: 'default' }));
  assert.equal(loadAppearance().theme, 'system');
});

test('reset restores System, Teal, and Default atomically', () => {
  installStorage();
  appearance.set({ version: 1, theme: 'paper', accent: 'pink', textSize: 'large' });
  resetAppearance();
  assert.deepEqual(get(appearance), DEFAULT_APPEARANCE);
});

test('text-size row heights match the CSS contract', () => {
  assert.equal(textSizeRowHeight('small'), 38);
  assert.equal(textSizeRowHeight('default'), 42);
  assert.equal(textSizeRowHeight('large'), 48);
});
