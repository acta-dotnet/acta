import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RawJson } from '../../lib/rawJson.ts';
import { updateBody, updateOutcomeMessage, inputUpdatable } from './inputUpdate.ts';

test('input is updatable except while executing', () => {
  assert.equal(inputUpdatable('ready'), true);
  assert.equal(inputUpdatable('paused'), true);
  assert.equal(inputUpdatable('failed'), true);
  assert.equal(inputUpdatable('executing'), false);
});

test('updateBody sends the draft text verbatim in the payload envelope, every digit kept', () => {
  const draft = '{"id": 9007199254740993}';
  assert.deepEqual(updateBody(draft), { body: { input: new RawJson(`{"json":${draft}}`) } });
});

test('updateBody returns the parse error and no body', () => {
  const result = updateBody('{not json');
  assert.ok('error' in result && result.error.length > 0);
});

test('updateBody blocks a literal null with a clear message and no body', () => {
  const result = updateBody(' null ');
  assert.ok('error' in result && result.error.length > 0);
});

test('applied outcome reports success', () => {
  const message = updateOutcomeMessage('applied', 'ready');
  assert.equal(message.kind, 'ok');
  assert.match(message.text, /updated/);
});

test('a rejection carrying the job status is the in-flight 409, as a warning', () => {
  const message = updateOutcomeMessage('rejected', 'executing');
  assert.equal(message.kind, 'warn');
  assert.match(message.text, /in flight/);
});

test('a rejection without a status is a job with no input to update', () => {
  const message = updateOutcomeMessage('rejected', null);
  assert.equal(message.kind, 'warn');
  assert.equal(message.text, 'Input rejected: the job has no input to update.');
});

test('a version conflict says the job changed since its input was read and that the draft is kept', () => {
  const message = updateOutcomeMessage('version-conflict', null);
  assert.equal(message.kind, 'warn');
  assert.match(message.text, /changed since its input was read \(version conflict\)\. Your draft is kept/);
});

test('not-found outcome is a bad-kind message', () => {
  const message = updateOutcomeMessage('not-found', null);
  assert.equal(message.kind, 'bad');
});
