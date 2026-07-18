// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildTenantMetadataPayload, tenantAdminNeedsReload } from './tenantAdmin.ts';

test('buildTenantMetadataPayload trims input and keeps a real value', () => {
  assert.deepEqual(buildTenantMetadataPayload({ displayName: '  Acme Corp  ', description: ' primary tenant ' }), {
    displayName: 'Acme Corp',
    description: 'primary tenant'
  });
});

test('buildTenantMetadataPayload sends null for a blank field, clearing that column', () => {
  assert.deepEqual(buildTenantMetadataPayload({ displayName: '', description: '   ' }), { displayName: null, description: null });
});

test('tenantAdminNeedsReload is false for applied and alreadyInState - both are successes', () => {
  assert.equal(tenantAdminNeedsReload('applied'), false);
  assert.equal(tenantAdminNeedsReload('alreadyInState'), false);
});

test('tenantAdminNeedsReload is true for notFound and versionConflict - never silently resend', () => {
  assert.equal(tenantAdminNeedsReload('notFound'), true);
  assert.equal(tenantAdminNeedsReload('versionConflict'), true);
});
