import assert from 'node:assert/strict';
import test from 'node:test';
import { parseRouteHash } from './router.ts';
import { routes } from './routes.ts';

const WORKER_REF = 'wrk_01a00839ac007d0897f301df388fb723';
const ALERT_REF = 'alr_01a0083ad2c8768f88152ef8864d3a8c';

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

test('calendar detail route parses the namespace and name pair, the version staying in the query', () => {
  assert.deepEqual(parseRouteHash('#/calendars/sys/office-hours?ns=billing&v=3'), {
    name: 'calendar-detail',
    calendarNamespace: 'sys',
    calendarName: 'office-hours'
  });
  assert.deepEqual(parseRouteHash('#/calendars'), { name: 'calendars' });
  assert.deepEqual(parseRouteHash('#/calendars/sys'), { name: 'not-found' });
});

test('calendar links keep the scope and carry the version or the new family role', () => {
  assert.equal(routes.calendar('sys', 'office-hours', { namespace: 'billing', version: 3 }), '#/calendars/sys/office-hours?ns=billing&v=3');
  assert.equal(routes.calendar('billing', 'pay-days', { role: 'occurrence' }), '#/calendars/billing/pay-days?role=occurrence');
  assert.equal(routes.calendars({ namespace: 'billing' }), '#/calendars?ns=billing');
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
