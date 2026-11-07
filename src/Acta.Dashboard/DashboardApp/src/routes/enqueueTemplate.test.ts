// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RawJson } from '../lib/rawJson.ts';
import { cloneInputState, enqueueInputFields, inputContractLabel, templateSeed } from './enqueueTemplate.ts';

const addNumbers = {
  jobNamespace: 'testjobs',
  jobName: 'add-numbers',
  inputTypeName: 'TestJobs.AddNumbers',
  inputFormatName: 'json',
  templateJson: '{"left":0,"right":0}'
};

test('inputContractLabel: names the input type and its wire format', () => {
  assert.equal(inputContractLabel(addNumbers), 'Input: TestJobs.AddNumbers (json)');
});

test('inputContractLabel: a job this host does not know has no label', () => {
  assert.equal(inputContractLabel(null), null);
});

test('templateSeed: an untouched editor with no clone prefill takes the template, indented', () => {
  assert.equal(templateSeed(addNumbers, { edited: false, clonePrefilled: false }), '{\n  "left": 0,\n  "right": 0\n}');
});

test('templateSeed: a scalar template seeds as is', () => {
  assert.equal(templateSeed({ ...addNumbers, templateJson: '""' }, { edited: false, clonePrefilled: false }), '""');
});

test('templateSeed: an edited editor is never overwritten', () => {
  assert.equal(templateSeed(addNumbers, { edited: true, clonePrefilled: false }), undefined);
});

test('templateSeed: a clone prefill is never overwritten', () => {
  assert.equal(templateSeed(addNumbers, { edited: false, clonePrefilled: true }), undefined);
});

test('templateSeed: no template leaves the editor alone', () => {
  assert.equal(templateSeed(null, { edited: false, clonePrefilled: false }), undefined);
  assert.equal(templateSeed({ ...addNumbers, templateJson: null }, { edited: false, clonePrefilled: false }), undefined);
});

test('cloneInputState: a json clone seeds the editor with its text, every digit kept', () => {
  assert.equal(cloneInputState('{"id":9007199254740993}'), '{\n  "id": 9007199254740993\n}');
});

test('cloneInputState: no json input, or stored text that is not one JSON value, leaves the form untouched', () => {
  assert.equal(cloneInputState(null), null);
  assert.equal(cloneInputState(undefined), null);
  assert.equal(cloneInputState('{broken'), null);
});

test('enqueueInputFields: disabled input sends nothing', () => {
  assert.deepEqual(enqueueInputFields(false, '{"a":1}'), { fields: {} });
});

test('enqueueInputFields: the editor text is sent verbatim in the payload envelope', () => {
  assert.deepEqual(enqueueInputFields(true, '{"a": 12345678901234567890}'), {
    fields: { input: new RawJson('{"json":{"a": 12345678901234567890}}') }
  });
});

test('enqueueInputFields: text that is not JSON is blocked with the parse error', () => {
  const result = enqueueInputFields(true, '{broken');
  assert.ok('error' in result && result.error.length > 0);
});

test('enqueueInputFields: a literal null is blocked with a clear message', () => {
  const result = enqueueInputFields(true, 'null');
  assert.ok('error' in result && result.error.length > 0);
});
