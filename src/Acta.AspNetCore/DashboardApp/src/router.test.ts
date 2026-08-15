import assert from 'node:assert/strict';
import test from 'node:test';
import { parseRouteHash } from './router.ts';
import { routes } from './routes.ts';

const WORKER_REF = 'wrk_01k2r8h0000080000000000042';
const ALERT_REF = 'alr_01k2r8h0000080000000000043';

test('worker detail route parses a worker ref', () => {
  assert.deepEqual(parseRouteHash(`#/workers/${WORKER_REF}?ns=billing`), { name: 'worker-detail', workerRef: WORKER_REF });
});

test('alert detail route parses an alert ref', () => {
  assert.deepEqual(parseRouteHash(`#/alerts/${ALERT_REF}`), { name: 'alert-detail', alertRef: ALERT_REF });
});

test('definition detail route parses the namespace and name pair', () => {
  assert.deepEqual(parseRouteHash('#/definitions/billing/send-invoice'), {
    name: 'definition-detail',
    defNamespace: 'billing',
    defName: 'send-invoice'
  });
});

test('over-long and unknown routes resolve to not found', () => {
  assert.deepEqual(parseRouteHash('#/workers/a/b'), { name: 'not-found' });
  assert.deepEqual(parseRouteHash('#/definitions/billing'), { name: 'not-found' });
  assert.deepEqual(parseRouteHash('#/does-not-exist'), { name: 'not-found' });
});

test('worker links preserve namespace scope', () => {
  assert.equal(routes.worker(WORKER_REF, { namespace: 'billing' }), `#/workers/${WORKER_REF}?ns=billing`);
});

test('definition links carry the natural key and default their scope to the namespace', () => {
  assert.equal(routes.definition('billing', 'send-invoice'), '#/definitions/billing/send-invoice?ns=billing');
});
