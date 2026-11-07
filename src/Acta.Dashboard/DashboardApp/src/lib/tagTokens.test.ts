// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseTagTokens } from './tagTokens.ts';

test('parseTagTokens splits on whitespace and commas and drops blanks', () => {
  assert.deepEqual(parseTagTokens('env:prod  team, region:eu'), ['env:prod', 'team', 'region:eu']);
});

test('parseTagTokens dedupes repeated tokens', () => {
  assert.deepEqual(parseTagTokens('env:prod env:prod team'), ['env:prod', 'team']);
});

test('parseTagTokens returns an empty list for blank input', () => {
  assert.deepEqual(parseTagTokens('   '), []);
});
