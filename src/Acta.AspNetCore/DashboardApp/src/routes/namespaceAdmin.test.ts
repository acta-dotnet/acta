// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildNamespaceMetadataPayload, isSysNamespace, namespaceAdminNeedsReload } from './namespaceAdmin.ts';

test('buildNamespaceMetadataPayload trims input and keeps a real value', () => {
  assert.deepEqual(buildNamespaceMetadataPayload({ ownerTeam: '  Platform  ', description: ' core namespace ' }), {
    ownerTeam: 'Platform',
    description: 'core namespace'
  });
});

test('buildNamespaceMetadataPayload sends null for a blank field, clearing that column', () => {
  assert.deepEqual(buildNamespaceMetadataPayload({ ownerTeam: '', description: '   ' }), { ownerTeam: null, description: null });
});

test('isSysNamespace is true only for the seeded id 1', () => {
  assert.equal(isSysNamespace(1), true);
  assert.equal(isSysNamespace(2), false);
  assert.equal(isSysNamespace(0), false);
});

test('namespaceAdminNeedsReload is false for applied and alreadyInState - both are successes', () => {
  assert.equal(namespaceAdminNeedsReload('applied'), false);
  assert.equal(namespaceAdminNeedsReload('alreadyInState'), false);
});

test('namespaceAdminNeedsReload is true for notFound and versionConflict - never silently resend', () => {
  assert.equal(namespaceAdminNeedsReload('notFound'), true);
  assert.equal(namespaceAdminNeedsReload('versionConflict'), true);
});
