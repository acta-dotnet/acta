import assert from 'node:assert/strict';
import test from 'node:test';
import { parseRouteHash } from './router.ts';
import { routes } from './routes.ts';

test('worker detail route parses a positive worker id', () => {
  assert.deepEqual(parseRouteHash('#/workers/42?ns=billing'), { name: 'worker-detail', workerId: 42 });
});

test('invalid worker and unknown routes resolve to not found', () => {
  assert.deepEqual(parseRouteHash('#/workers/nope'), { name: 'not-found' });
  assert.deepEqual(parseRouteHash('#/does-not-exist'), { name: 'not-found' });
});

test('worker links preserve namespace scope', () => {
  assert.equal(routes.worker(42, { namespace: 'billing' }), '#/workers/42?ns=billing');
});
