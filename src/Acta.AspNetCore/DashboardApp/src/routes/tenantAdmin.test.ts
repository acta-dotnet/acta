// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildTenantDetailsPayload, tenantAdminNeedsReload } from './tenantAdmin.ts';

test('buildTenantDetailsPayload trims input and keeps a real value', () => {
  assert.deepEqual(buildTenantDetailsPayload({ displayName: '  Acme Corp  ', description: ' primary tenant ' }), {
    displayName: 'Acme Corp',
    description: 'primary tenant'
  });
});

test('buildTenantDetailsPayload sends null for a blank field, clearing that column', () => {
  assert.deepEqual(buildTenantDetailsPayload({ displayName: '', description: '   ' }), { displayName: null, description: null });
});

test('tenantAdminNeedsReload is false for applied and already-in-state - both are successes', () => {
  assert.equal(tenantAdminNeedsReload('applied'), false);
  assert.equal(tenantAdminNeedsReload('already-in-state'), false);
});

test('tenantAdminNeedsReload is true for not-found and version-conflict - never silently resend', () => {
  assert.equal(tenantAdminNeedsReload('not-found'), true);
  assert.equal(tenantAdminNeedsReload('version-conflict'), true);
});
