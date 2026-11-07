// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkName, checkTags, NAME_LIMIT, TAG_LIMITS } from './enqueueNames.ts';

test('an empty lane is omitted; a name is trimmed and folded to lowercase', () => {
  assert.deepEqual(checkName(''), { value: null, error: null });
  assert.deepEqual(checkName('  Reports.EU '), { value: 'reports.eu', error: null });
  assert.match(checkName('   ').error ?? '', /at least one character/);
});

test("a name is dotted kebab-case of at most 128 characters, refused in the server's words", () => {
  assert.match(checkName('reports_eu').error ?? '', /not kebab-case or dotted-kebab/);
  assert.match(checkName('1st').error ?? '', /not kebab-case/);
  assert.match(checkName('reports-').error ?? '', /not kebab-case/);
  assert.match(checkName('reports..eu').error ?? '', /not kebab-case/);
  assert.equal(checkName('a'.repeat(NAME_LIMIT)).value, 'a'.repeat(NAME_LIMIT));
  assert.equal(checkName('a'.repeat(NAME_LIMIT + 1)).error, 'Identifier must not exceed 128 characters (got 129).');
});

test('sys and the sys. prefix are reserved, in any case', () => {
  assert.match(checkName('SYS').error ?? '', /reserved/);
  assert.match(checkName('sys.lane').error ?? '', /reserved/);
  assert.equal(checkName('system').value, 'system');
});

test('tag rows fold their names, keep their values, and send an empty value as a presence-only tag', () => {
  const check = checkTags([
    { name: 'Source', value: 'Dashboard' },
    { name: 'review-needed', value: '' }
  ]);
  assert.equal(check.error, null);
  assert.deepEqual(check.tags, [
    { name: 'source', value: 'Dashboard' },
    { name: 'review-needed', value: null }
  ]);
});

test('no rows means no tags member at all', () => {
  assert.deepEqual(checkTags([]), { tags: undefined, rows: [], error: null });
});

test('a row without a name, a duplicate folded name, and a bad value are reported on their rows', () => {
  const check = checkTags([
    { name: '', value: 'x' },
    { name: 'env', value: 'prod' },
    { name: 'ENV', value: '' },
    { name: 'note', value: 'tab\there' }
  ]);
  assert.match(check.rows[0].nameError ?? '', /needs a name/);
  assert.equal(check.rows[1].nameError, null);
  assert.equal(check.rows[2].nameError, "Duplicate normalized tag name 'env'.");
  assert.equal(check.rows[3].valueError, 'Value must not contain control characters.');
  assert.match(check.error ?? '', /needs a name/);
  assert.deepEqual(check.tags, [{ name: 'env', value: 'prod' }]);
});

test('a value is at most 128 characters and the set at most 32 tags', () => {
  const long = checkTags([{ name: 'a', value: 'v'.repeat(TAG_LIMITS.value + 1) }]);
  assert.equal(long.rows[0].valueError, 'Value length 129 exceeds the 128-char limit.');
  const rows = (count: number) => Array.from({ length: count }, (_, i) => ({ name: `t${i}`, value: '' }));
  assert.equal(checkTags(rows(TAG_LIMITS.count + 1)).error, 'A job may carry at most 32 tags.');
  assert.equal(checkTags(rows(TAG_LIMITS.count)).error, null);
});
