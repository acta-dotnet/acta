// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RESERVED_SYSTEM_NAMESPACE, buildNamespaceDetailsPayload, isSysNamespace, namespaceAdminNeedsReload } from './namespaceAdmin.ts';

test('buildNamespaceDetailsPayload trims input and keeps a real value', () => {
  assert.deepEqual(buildNamespaceDetailsPayload({ ownerTeam: '  Platform  ', description: ' core namespace ' }), {
    ownerTeam: 'Platform',
    description: 'core namespace'
  });
});

test('buildNamespaceDetailsPayload sends null for a blank field, clearing that column', () => {
  assert.deepEqual(buildNamespaceDetailsPayload({ ownerTeam: '', description: '   ' }), { ownerTeam: null, description: null });
});

test('isSysNamespace mirrors the backend reservation: the bare name and the sys. prefix', () => {
  assert.equal(isSysNamespace(RESERVED_SYSTEM_NAMESPACE), true);
  assert.equal(isSysNamespace('sys'), true);
  // IdentifierSyntax.IsReservedSystemName reserves the prefix too, so the guardrail follows it.
  assert.equal(isSysNamespace('sys.recovery'), true);
  // Only the delimited prefix counts - a name that merely starts with the letters is a user row.
  assert.equal(isSysNamespace('system-billing'), false);
  assert.equal(isSysNamespace('billing'), false);
  assert.equal(isSysNamespace(null), false);
  assert.equal(isSysNamespace(undefined), false);
});

test('namespaceAdminNeedsReload is false for applied and alreadyInState - both are successes', () => {
  assert.equal(namespaceAdminNeedsReload('applied'), false);
  assert.equal(namespaceAdminNeedsReload('alreadyInState'), false);
});

test('namespaceAdminNeedsReload is true for notFound and versionConflict - never silently resend', () => {
  assert.equal(namespaceAdminNeedsReload('notFound'), true);
  assert.equal(namespaceAdminNeedsReload('versionConflict'), true);
});
