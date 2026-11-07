import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  archiveBlockedText,
  archiveBlockers,
  calendarControlMessage,
  calendarRowState,
  calendarVerbSucceeded,
  familyState,
  publishImpact,
  publishImpactText
} from './calendarFamily.ts';

const family = { currentVersion: 2, latestVersion: 3 };

test('a shown draft offers publish; any family with a current version offers archive', () => {
  const draft = familyState({ ...family, shownVersion: 3, shownStatus: 'draft' }, true);
  assert.equal(draft.latestVersion, 3);
  assert.deepEqual([draft.shownIsDraft, draft.shownIsCurrent, draft.canPublish, draft.canArchive], [true, false, true, true]);

  const current = familyState({ ...family, shownVersion: 2, shownStatus: 'published' }, true);
  assert.deepEqual([current.shownStatus, current.shownIsCurrent, current.canPublish, current.canArchive], ['published', true, false, true]);
});

test('a read-only host offers nothing, and a family without a current version cannot archive', () => {
  const readOnly = familyState({ ...family, shownVersion: 3, shownStatus: 'draft' }, false);
  assert.deepEqual([readOnly.canPublish, readOnly.canArchive], [false, false]);
  const unpublished = familyState({ currentVersion: null, latestVersion: 1, shownVersion: 1, shownStatus: 'draft' }, true);
  assert.deepEqual([unpublished.canPublish, unpublished.canArchive], [true, false]);
});

const schedules = (...statuses: string[]) => statuses.map((status) => ({ status }));

test('publish impact leaves out orphaned schedules, counts the ones past the preview cap, and the overdue ones', () => {
  assert.deepEqual(publishImpact({ schedules: schedules('active', 'orphaned', 'paused'), schedulesTotal: 150, overdueTotal: 120 }), {
    retimed: 149,
    overdue: 120
  });
  assert.deepEqual(publishImpact(null), { retimed: 0, overdue: 0 });
});

test('the line beside publish names at most the schedules it retimes, and waits for the preview to count them', () => {
  assert.equal(
    publishImpactText({ schedules: schedules('active', 'active', 'orphaned'), schedulesTotal: 3, overdueTotal: 0 }),
    'Retimes up to 2 bound schedules to this version.'
  );
  assert.equal(
    publishImpactText({ schedules: schedules('active'), schedulesTotal: 1, overdueTotal: 0 }),
    'Retimes up to 1 bound schedule to this version.'
  );
  assert.equal(
    publishImpactText({ schedules: schedules('orphaned'), schedulesTotal: 1, overdueTotal: 0 }),
    'No live schedule binds this calendar, so publishing retimes nothing.'
  );
  assert.equal(publishImpactText(undefined), null);
});

test('an archive is blocked by every bound schedule not orphaned, and past the preview cap the count is a floor', () => {
  assert.deepEqual(archiveBlockers({ schedules: schedules('active', 'paused', 'orphaned'), schedulesTotal: 3 }), { count: 2, atLeast: false });
  assert.deepEqual(archiveBlockers({ schedules: schedules('orphaned'), schedulesTotal: 1 }), { count: 0, atLeast: false });
  assert.deepEqual(archiveBlockers({ schedules: schedules('active', 'active'), schedulesTotal: 150 }), { count: 2, atLeast: true });
  assert.equal(archiveBlockers(null), null);

  assert.equal(archiveBlockedText({ count: 14, atLeast: false }), '14 schedules bind this calendar.');
  assert.equal(archiveBlockedText({ count: 1, atLeast: false }), '1 schedule binds this calendar.');
  assert.equal(archiveBlockedText({ count: 100, atLeast: true }), 'At least 100 schedules bind this calendar.');
});

test('a list row flags a bound family with no current version as an error, and a waiting draft as pending', () => {
  const row = { currentVersion: 2, latestVersion: 3, latestStatus: 'draft', dependentCount: 4 };
  assert.deepEqual(calendarRowState(row), { rail: 'due', hint: 'Draft v3 is newer than current v2 and waits to be published.' });
  assert.deepEqual(calendarRowState({ ...row, currentVersion: null, latestVersion: 1, dependentCount: 0 }), {
    rail: 'due',
    hint: 'Draft v1 waits to be published; no version is current yet.'
  });

  const broken = calendarRowState({ ...row, currentVersion: null, dependentCount: 2 });
  assert.equal(broken.rail, 'trouble');
  assert.equal(
    broken.hint,
    'No current version: 2 bound schedules report evaluation errors, and a deploy that binds this calendar is refused. Publish a version to clear both.'
  );
  assert.match(calendarRowState({ ...row, currentVersion: null, latestStatus: 'archived', dependentCount: 1 }).hint ?? '', /^No current version: 1 bound schedule reports/);

  assert.deepEqual(calendarRowState({ ...row, latestVersion: 2, latestStatus: 'published' }), { rail: '', hint: null });
  assert.deepEqual(calendarRowState({ ...row, currentVersion: null, latestStatus: 'archived', dependentCount: 0 }), { rail: '', hint: null });
});

test('catalog verbs word their outcomes with the version and the counts', () => {
  assert.equal(calendarControlMessage('draft', 'applied', { version: 4 }), 'Draft saved as version 4.');
  assert.equal(calendarControlMessage('publish', 'applied', { version: 3, retimed: 1 }), 'Version 3 published; 1 schedule retimed.');
  assert.equal(calendarControlMessage('archive', 'applied', { version: 2 }), 'Calendar archived; version 2 is retired and no version is current.');
  assert.match(calendarControlMessage('draft', 'version-conflict'), /version conflict/);
  assert.equal(calendarControlMessage('draft', 'rejected'), 'Draft rejected: the definition has problems.');
  assert.equal(calendarControlMessage('publish', 'rejected', { reason: 'overdue occurrences' }), 'Publish rejected: overdue occurrences');
  assert.equal(calendarControlMessage('archive', 'rejected', { dependents: 2 }), 'Archive rejected: 2 live schedules still bind this calendar.');
  assert.equal(calendarControlMessage('publish', 'not-found'), "That version is not one of this calendar's.");
  assert.equal(calendarControlMessage('archive', 'not-found'), 'Calendar not found.');
  assert.equal(calendarControlMessage('draft', 'already-in-state', { version: 2 }), 'Version 2 already holds this definition; nothing was saved.');
  assert.equal(calendarControlMessage('publish', 'already-in-state', { version: 2 }), 'Version 2 is already current; nothing was retimed.');
});

test('a verb that finds the family already as asked succeeds, as an applied one does', () => {
  assert.deepEqual(
    ['applied', 'already-in-state', 'rejected', 'version-conflict', 'not-found'].map(calendarVerbSucceeded),
    [true, true, false, false, false]
  );
});
