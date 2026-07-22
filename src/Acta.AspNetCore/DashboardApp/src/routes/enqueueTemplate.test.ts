// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cloneInputState, enqueueInputFields, inputContractLabel, templateSeed } from './enqueueTemplate.ts';

const addNumbers = {
  jobNamespace: 'testjobs',
  jobName: 'add-numbers',
  inputTypeName: 'TestJobs.AddNumbers',
  format: 'json',
  template: { left: 0, right: 0 }
};

test('inputContractLabel: names the input type and its wire format', () => {
  assert.equal(inputContractLabel(addNumbers), 'Input: TestJobs.AddNumbers (json)');
});

test('inputContractLabel: a job this host does not know has no label', () => {
  assert.equal(inputContractLabel(null), null);
  assert.equal(inputContractLabel({ ...addNumbers, inputTypeName: null, format: 'none', template: null }), null);
});

test('templateSeed: an untouched editor with no clone prefill takes the template', () => {
  assert.deepEqual(templateSeed(addNumbers, { edited: false, clonePrefilled: false }), { left: 0, right: 0 });
});

test('templateSeed: an edited editor is never overwritten', () => {
  assert.equal(templateSeed(addNumbers, { edited: true, clonePrefilled: false }), undefined);
});

test('templateSeed: a clone prefill is never overwritten', () => {
  assert.equal(templateSeed(addNumbers, { edited: false, clonePrefilled: true }), undefined);
});

test('templateSeed: no template leaves the editor alone', () => {
  assert.equal(templateSeed(null, { edited: false, clonePrefilled: false }), undefined);
  assert.equal(
    templateSeed({ ...addNumbers, template: null }, { edited: false, clonePrefilled: false }),
    undefined
  );
});

test('cloneInputState: a json clone seeds the json editor', () => {
  assert.deepEqual(cloneInputState({ format: 'json', formatId: 1, json: { a: 1 } }), {
    format: 'json',
    json: { a: 1 },
    text: ''
  });
});

test('cloneInputState: a text clone seeds text mode, not a quoted json string', () => {
  assert.deepEqual(cloneInputState({ format: 'text', formatId: 3, text: 'order-42' }), {
    format: 'text',
    json: {},
    text: 'order-42'
  });
});

test('cloneInputState: a binary or none clone leaves the form untouched (out of scope for v1)', () => {
  assert.equal(cloneInputState({ format: 'bytes', formatId: 2, base64: 'AQI=' }), null);
  assert.equal(cloneInputState({ format: 'none', formatId: 0 }), null);
  assert.equal(cloneInputState(null), null);
});

test('cloneInputState: a truncated clone seeds nothing (no body to copy)', () => {
  assert.equal(cloneInputState({ format: 'json', formatId: 1, byteLength: 512 * 1024, truncated: true }), null);
});

const jsonState = { format: 'json' as const, json: { a: 1 }, text: '' };

test('enqueueInputFields: disabled input sends nothing', () => {
  assert.deepEqual(enqueueInputFields(false, jsonState), { fields: {} });
});

test('enqueueInputFields: json sends input only', () => {
  assert.deepEqual(enqueueInputFields(true, jsonState), { fields: { input: { a: 1 } } });
});

test('enqueueInputFields: text sends text only, verbatim', () => {
  assert.deepEqual(enqueueInputFields(true, { format: 'text', json: {}, text: 'order-42' }), { fields: { text: 'order-42' } });
});

test('enqueueInputFields: a literal null json is blocked with an honest message', () => {
  const result = enqueueInputFields(true, { format: 'json', json: null, text: '' });
  assert.ok('error' in result && result.error.length > 0);
});
