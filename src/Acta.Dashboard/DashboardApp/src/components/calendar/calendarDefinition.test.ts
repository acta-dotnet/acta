import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  definitionTemplate,
  emptyWeekly,
  minutesOf,
  openMinutes,
  previewDiff,
  weeklyFromJson,
  weeklyProblems,
  weeklyToJson,
  windowKey,
  windowProblem,
  windowSpans
} from './calendarDefinition.ts';

const officeHours =
  '{"kind":"weekly-availability","timeZone":"schedule","weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday"],"windows":[["09:00","12:00"],["13:00","17:00"]]}],"dateOverrides":{"2027-12-25":[]},"excludedRanges":[{"from":"2027-08-01","to":"2027-08-14"}]}';

test('a weekly-availability reads into per-day windows, overrides, and exclusions', () => {
  const model = weeklyFromJson(officeHours);
  assert.ok(model);
  assert.equal(model.timeZone, 'schedule');
  assert.deepEqual(model.days.Monday, [
    { from: '09:00', to: '12:00' },
    { from: '13:00', to: '17:00' }
  ]);
  assert.deepEqual(model.days.Saturday, []);
  assert.deepEqual(model.dateOverrides, [{ date: '2027-12-25', windows: [] }]);
  assert.deepEqual(model.excludedRanges, [{ from: '2027-08-01', to: '2027-08-14' }]);
});

test('the model writes back as grouped weekday rules and round-trips', () => {
  const model = weeklyFromJson(officeHours)!;
  const text = weeklyToJson(model);
  const written = JSON.parse(text);
  assert.deepEqual(written.weekly, [
    { days: ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'], windows: [['09:00', '12:00'], ['13:00', '17:00']] }
  ]);
  assert.deepEqual(written.dateOverrides, { '2027-12-25': [] });
  assert.deepEqual(weeklyFromJson(text), model);
});

test('days whose windows differ get their own rule, in weekday order', () => {
  const model = emptyWeekly();
  model.days.Saturday = [{ from: '10:00', to: '14:00' }];
  const written = JSON.parse(weeklyToJson(model));
  assert.deepEqual(written.weekly.map((rule: { days: string[] }) => rule.days), [
    ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'],
    ['Saturday']
  ]);
});

test("a day named by two rules keeps both rules' windows", () => {
  const model = weeklyFromJson(
    '{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["08:00","10:00"]]},{"days":["Monday"],"windows":[["14:00","16:00"]]}]}'
  );
  assert.deepEqual(model?.days.Monday, [
    { from: '08:00', to: '10:00' },
    { from: '14:00', to: '16:00' }
  ]);
});

test('text that is not a weekly-availability the editor can reproduce stays in the JSON editor', () => {
  assert.equal(weeklyFromJson('{"kind":"union","of":[]}'), null);
  assert.equal(weeklyFromJson('not json'), null);
  assert.equal(weeklyFromJson('{"kind":"weekly-availability","weekly":[{"days":["Funday"],"windows":[]}]}'), null);
  assert.equal(weeklyFromJson('{"kind":"weekly-availability","weekly":[],"extra":1}'), null);
  assert.equal(weeklyFromJson('{"kind":"weekly-availability","weekly":[{"days":["Monday"],"windows":[["09:00"]]}]}'), null);
});

test('window geometry wraps an overnight window onto two spans', () => {
  assert.deepEqual(windowSpans({ from: '09:00', to: '12:00' }), [{ left: 37.5, width: 12.5 }]);
  assert.deepEqual(windowSpans({ from: '22:00', to: '02:00' }), [
    { left: (22 / 24) * 100, width: (2 / 24) * 100 },
    { left: 0, width: (2 / 24) * 100 }
  ]);
  assert.deepEqual(windowSpans({ from: '09:00', to: '09:00' }), []);
  assert.equal(openMinutes([{ from: '22:00', to: '02:00' }, { from: '09:00', to: '10:30' }]), 330);
});

test("window problems use the compiler's words", () => {
  assert.equal(windowProblem({ from: '09:00', to: '17:00' }), null);
  assert.equal(windowProblem({ from: '9am', to: '17:00' }), 'a time is HH:mm or HH:mm:ss.');
  assert.match(windowProblem({ from: '09:00', to: '09:00' }) ?? '', /ends after it starts/);
  assert.equal(minutesOf('23:59:30'), 1439);
  assert.equal(minutesOf('24:00'), null);
});

test('the model reports its problems naming the place the compiler would', () => {
  const model = emptyWeekly();
  model.days.Monday = [{ from: '09:00', to: '09:00' }];
  model.dateOverrides = [{ date: '25-12-2027', windows: [] }];
  model.excludedRanges = [
    { from: '2027-08-14', to: '2027-08-01' },
    { fromUtc: '2027-01-01T00:00:00Z', toUtc: '2026-01-01T00:00:00Z' }
  ];
  const problems = weeklyProblems(model);
  assert.match(problems[0], /^Monday window 1: a window ends after it starts/);
  assert.equal(problems[1], 'dateOverrides[0]: a date is yyyy-MM-dd.');
  assert.equal(problems[2], 'excludedRanges[0]: to must not be before from.');
  assert.equal(problems[3], 'excludedRanges[1]: toUtc must be after fromUtc.');

  const empty = emptyWeekly();
  for (const day of Object.keys(empty.days) as (keyof typeof empty.days)[]) empty.days[day] = [];
  assert.deepEqual(weeklyProblems(empty), ['a weekly-availability needs weekly rules or date overrides.']);
});

test('templates give each role a definition that compiles', () => {
  const availability = JSON.parse(definitionTemplate('availability'));
  assert.equal(availability.kind, 'weekly-availability');
  assert.equal(availability.weekly[0].days.length, 5);
  const occurrence = JSON.parse(definitionTemplate('occurrence'));
  assert.equal(occurrence.kind, 'daily-local-time');
  assert.deepEqual(occurrence.times, ['09:00']);
});

test('the preview diff names what a publication adds and removes', () => {
  const diff = previewDiff(['a', 'b', 'c'], ['b', 'c', 'd']);
  assert.deepEqual([...diff.added], ['a']);
  assert.deepEqual([...diff.removed], ['d']);
  assert.equal(windowKey({ startUtc: '2027-01-04T08:00:00Z', endUtc: null }), '2027-01-04T08:00:00Z/open');
});
