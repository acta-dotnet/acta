import { test } from 'node:test';
import assert from 'node:assert/strict';
import { calendarControlMessage, familyState, publishImpact } from './calendarFamily.ts';

const family = {
  currentVersion: 2,
  versions: [
    { version: 3, status: 'draft' },
    { version: 2, status: 'published' },
    { version: 1, status: 'superseded' }
  ]
};

test('a shown draft offers publish; any family with a current version offers archive', () => {
  const draft = familyState({ ...family, shownVersion: 3 }, true);
  assert.equal(draft.latestVersion, 3);
  assert.deepEqual([draft.shownIsDraft, draft.shownIsCurrent, draft.canPublish, draft.canArchive], [true, false, true, true]);

  const current = familyState({ ...family, shownVersion: 2 }, true);
  assert.deepEqual([current.shownStatus, current.shownIsCurrent, current.canPublish, current.canArchive], ['published', true, false, true]);
});

test('a read-only host offers nothing, and a family without a current version cannot archive', () => {
  const readOnly = familyState({ ...family, shownVersion: 3 }, false);
  assert.deepEqual([readOnly.canPublish, readOnly.canArchive], [false, false]);
  const unpublished = familyState({ currentVersion: null, versions: [{ version: 1, status: 'draft' }], shownVersion: 1 }, true);
  assert.deepEqual([unpublished.canPublish, unpublished.canArchive], [true, false]);
});

test('publish impact counts the bound schedules and the overdue ones', () => {
  assert.deepEqual(publishImpact([{ overdue: false }, { overdue: true }, { overdue: true }]), { retimed: 3, overdue: 2 });
  assert.deepEqual(publishImpact(null), { retimed: 0, overdue: 0 });
});

test('catalog verbs word their outcomes with the version and the counts', () => {
  assert.equal(calendarControlMessage('draft', 'applied', { version: 4 }), 'Draft saved as version 4.');
  assert.equal(calendarControlMessage('publish', 'applied', { version: 3, retimed: 1 }), 'Version 3 published; 1 schedule retimed.');
  assert.equal(calendarControlMessage('archive', 'applied', { version: 2 }), 'Version 2 archived; the family has no current version.');
  assert.match(calendarControlMessage('draft', 'versionConflict'), /version conflict/);
  assert.equal(calendarControlMessage('draft', 'rejected'), 'Draft rejected: the definition has problems.');
  assert.equal(calendarControlMessage('publish', 'rejected', { reason: 'overdue occurrences' }), 'Publish rejected: overdue occurrences');
  assert.equal(calendarControlMessage('archive', 'rejected', { dependents: 2 }), 'Archive rejected: 2 live schedules still bind this calendar.');
  assert.equal(calendarControlMessage('publish', 'notFound'), "That version is not one of this calendar's.");
  assert.equal(calendarControlMessage('archive', 'notFound'), 'Calendar not found.');
});
