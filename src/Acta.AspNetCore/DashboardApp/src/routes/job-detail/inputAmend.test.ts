import { test } from 'node:test';
import assert from 'node:assert/strict';
import { amendBody, amendOutcomeMessage, inputAmendable } from './inputAmend.ts';

test('input is amendable except while dispatched or executing', () => {
  assert.equal(inputAmendable('ready'), true);
  assert.equal(inputAmendable('paused'), true);
  assert.equal(inputAmendable('failed'), true);
  assert.equal(inputAmendable('dispatched'), false);
  assert.equal(inputAmendable('executing'), false);
});

test('amendBody json format parses the draft into an input body', () => {
  assert.deepEqual(amendBody('{"a":1}', 'json'), { body: { input: { a: 1 } } });
});

test('amendBody json format returns the parse error and no body', () => {
  const result = amendBody('{not json', 'json');
  assert.ok('error' in result && result.error.length > 0);
});

test('amendBody json format blocks a literal null with an honest message and no body', () => {
  const result = amendBody('null', 'json');
  assert.ok('error' in result && result.error.length > 0);
});

test('amendBody text format sends the raw text verbatim, never a quoted json string', () => {
  assert.deepEqual(amendBody('order-42', 'text'), { body: { text: 'order-42' } });
});

test('applied outcome reports success', () => {
  const message = amendOutcomeMessage('applied');
  assert.equal(message.kind, 'ok');
  assert.match(message.text, /amended/);
});

test('rejected outcome surfaces the in-flight server message as a warning', () => {
  const message = amendOutcomeMessage('rejected', 'Input rejected: the job is in flight.');
  assert.equal(message.kind, 'warn');
  assert.equal(message.text, 'Input rejected: the job is in flight.');
});

test('rejected outcome falls back to a default when the server sends no message', () => {
  const message = amendOutcomeMessage('rejected', null);
  assert.equal(message.kind, 'warn');
  assert.match(message.text, /in flight/);
});

test('not-found outcome is a bad-kind message', () => {
  const message = amendOutcomeMessage('notFound');
  assert.equal(message.kind, 'bad');
});
