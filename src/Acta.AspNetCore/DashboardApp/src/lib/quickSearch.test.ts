import { afterEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { loadRecents, matchPages, parseQuery, pushRecent } from './quickSearch.ts';

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
});

test('blank input recognizes nothing', () => {
  assert.equal(parseQuery(''), null);
  assert.equal(parseQuery('   '), null);
});

test('a pasted job ref folds and jumps', () => {
  assert.deepEqual(parseQuery(' job_01A14F4E954E7BF68AC609F3FCAD8E3D '), {
    kind: 'jobRef',
    ref: 'job_01a14f4e954e7bf68ac609f3fcad8e3d',
  });
});

test('a malformed ref falls through to text', () => {
  assert.equal(parseQuery('job_notahexref')!.kind, 'text');
  assert.equal(parseQuery('job_01k2zk03vf6fh0aeds62dscdvb')!.kind, 'text');
  assert.equal(parseQuery('JOB_01a14f4e954e7bf68ac609f3fcad8e3d')!.kind, 'text');
});

test('worker and alert refs route to their own screens', () => {
  assert.deepEqual(parseQuery('wrk_019f9b3508007e4dbfd13d9d20bc81f4'), { kind: 'workerRef', ref: 'wrk_019f9b3508007e4dbfd13d9d20bc81f4' });
  assert.deepEqual(parseQuery('alr_01a0083ad2c8768f88152ef8864d3a8c'), { kind: 'alertRef', ref: 'alr_01a0083ad2c8768f88152ef8864d3a8c' });
});

test('internal ids accept both spellings', () => {
  assert.deepEqual(parseQuery('id:42'), { kind: 'jobId', id: '42' });
  assert.deepEqual(parseQuery('#42'), { kind: 'jobId', id: '42' });
});

test('reserved prefixes beat the tag token', () => {
  assert.equal(parseQuery('id:42')!.kind, 'jobId');
  assert.deepEqual(parseQuery('corr:ORD-1042'), { kind: 'correlation', key: 'ORD-1042' });
  assert.deepEqual(parseQuery('key:invoice-99'), { kind: 'dedupKey', key: 'invoice-99' });
});

test('a bare name:value token is a tag filter', () => {
  assert.deepEqual(parseQuery('env:prod'), { kind: 'tag', token: 'env:prod' });
});

test('tag: reaches bare tag names and layered values', () => {
  assert.deepEqual(parseQuery('tag:urgent'), { kind: 'tag', token: 'urgent' });
  assert.deepEqual(parseQuery('tag:env:prod'), { kind: 'tag', token: 'env:prod' });
});

test('ns: switches scope and folds the namespace name', () => {
  assert.deepEqual(parseQuery('ns:Billing'), { kind: 'scope', name: 'billing' });
});

test('free text folds for name domains and keeps the raw form', () => {
  assert.deepEqual(parseQuery('MICA'), { kind: 'text', folded: 'mica', raw: 'MICA' });
});

test('page matching covers the alias views and carries the scope', () => {
  const labels = matchPages('recur', 'billing').map((hit) => hit.label);
  assert.deepEqual(labels, ['Recurring jobs']);
  const [hit] = matchPages('recur', 'billing');
  assert.match(hit.href, /view=recurring/);
  assert.match(hit.href, /ns=billing/);
});

test('recents dedupe by href, newest first, capped at eight', () => {
  installStorage();
  for (let i = 0; i < 10; i++) {
    pushRecent({ href: '#/jobs/' + i, label: 'job ' + i }, i);
  }
  pushRecent({ href: '#/jobs/5', label: 'job 5 again' }, 99);
  const recents = loadRecents();
  assert.equal(recents.length, 8);
  assert.equal(recents[0].href, '#/jobs/5');
  assert.equal(recents[0].at, 99);
  assert.equal(recents.filter((item) => item.href === '#/jobs/5').length, 1);
});

test('corrupt or foreign payloads load as empty', () => {
  installStorage();
  records.set('acta-recents-v1', '{');
  assert.deepEqual(loadRecents(), []);
  records.set('acta-recents-v1', JSON.stringify({ version: 2, items: [{ href: 'x', label: 'y', at: 1 }] }));
  assert.deepEqual(loadRecents(), []);
});

test('unavailable storage stays quiet', () => {
  Object.defineProperty(globalThis, 'localStorage', {
    configurable: true,
    value: {
      getItem: () => { throw new Error('unavailable'); },
      setItem: () => { throw new Error('unavailable'); },
    },
  });
  assert.deepEqual(loadRecents(), []);
  assert.doesNotThrow(() => pushRecent({ href: '#/x', label: 'x' }, 1));
});
