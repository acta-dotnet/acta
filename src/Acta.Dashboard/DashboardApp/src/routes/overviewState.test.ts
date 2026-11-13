import assert from 'node:assert/strict';
import test from 'node:test';
import { direction, keepSamples, type OverviewSample } from './overviewState.ts';

const sample = (seconds: number, readyCount: number, oldestReadyAgeSeconds = 0): OverviewSample => ({
  atMs: seconds * 1000,
  readyCount,
  oldestReadyAgeSeconds
});
const count = (amount: number) => String(amount);

test('the history keeps the five minutes before the newest sample', () => {
  let samples: OverviewSample[] = [];
  for (let seconds = 0; seconds <= 400; seconds += 10) samples = keepSamples(samples, sample(seconds, seconds));

  assert.equal(samples[0].atMs, 100_000);
  assert.equal(samples.at(-1)?.atMs, 400_000);
});

test('a gap longer than five minutes starts the history over', () => {
  assert.deepEqual(keepSamples([sample(0, 10), sample(10, 12)], sample(311, 500)), [sample(311, 500)]);
});

test('a sample at an instant already kept takes its place', () => {
  assert.deepEqual(keepSamples([sample(0, 10), sample(10, 12)], sample(10, 14)), [sample(0, 10), sample(10, 14)]);
});

test('a card says nothing until its samples span a minute', () => {
  assert.equal(direction([], 'readyCount', count), null);
  assert.equal(direction([sample(0, 6200)], 'readyCount', count), null);
  assert.equal(direction([sample(0, 6200), sample(59, 5000)], 'readyCount', count), null);
});

test('a move under ten is jitter', () => {
  assert.equal(direction([sample(0, 42), sample(120, 51)], 'readyCount', count), null);
  assert.equal(direction([sample(0, 0, 4), sample(120, 0, 13)], 'oldestReadyAgeSeconds', count), null);
});

test('a lower value points down and a higher one up, in the amount and the span they moved by', () => {
  assert.deepEqual(direction([sample(0, 6200), sample(150, 5600), sample(300, 5002)], 'readyCount', count), {
    icon: 'arrow-down',
    text: 'Down 1198 in 5 minutes'
  });
  assert.deepEqual(direction([sample(0, 180), sample(60, 190)], 'readyCount', count), { icon: 'arrow-up', text: 'Up 10 in 1 minute' });
});

test('the span is named as it was, not as the five minutes it could have been', () => {
  const paused = [sample(0, 0, 390), sample(10, 0, 385), sample(200, 0, 250)];

  assert.deepEqual(direction(paused, 'oldestReadyAgeSeconds', (seconds) => seconds + 's'), {
    icon: 'arrow-down',
    text: 'Down 140s in 3 minutes'
  });
});
