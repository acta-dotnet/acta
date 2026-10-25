import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RawJson } from '../../lib/rawJson.ts';
import { amendBody, amendOutcomeMessage, inputAmendable } from './inputAmend.ts';

test('input is amendable except while executing', () => {
  assert.equal(inputAmendable('ready'), true);
  assert.equal(inputAmendable('paused'), true);
  assert.equal(inputAmendable('failed'), true);
  assert.equal(inputAmendable('executing'), false);
});

test('amendBody sends the draft text verbatim, every digit kept', () => {
  const draft = '{"id": 9007199254740993}';
  assert.deepEqual(amendBody(draft), { body: { input: new RawJson(draft) } });
});

test('amendBody returns the parse error and no body', () => {
  const result = amendBody('{not json');
  assert.ok('error' in result && result.error.length > 0);
});

test('amendBody blocks a literal null with a clear message and no body', () => {
  const result = amendBody(' null ');
  assert.ok('error' in result && result.error.length > 0);
});

test('applied outcome reports success', () => {
  const message = amendOutcomeMessage('applied', 'ready');
  assert.equal(message.kind, 'ok');
  assert.match(message.text, /amended/);
});

test('a rejection carrying the job status is the in-flight 409, as a warning', () => {
  const message = amendOutcomeMessage('rejected', 'executing');
  assert.equal(message.kind, 'warn');
  assert.match(message.text, /in flight/);
});

test('a rejection without a status is a job with no input to amend', () => {
  const message = amendOutcomeMessage('rejected', null);
  assert.equal(message.kind, 'warn');
  assert.equal(message.text, 'Input rejected: the job has no input to amend.');
});

test('not-found outcome is a bad-kind message', () => {
  const message = amendOutcomeMessage('notFound', null);
  assert.equal(message.kind, 'bad');
});
