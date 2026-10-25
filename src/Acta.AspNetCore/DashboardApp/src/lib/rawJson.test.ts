// Run with Node's built-in test runner (see "npm test").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { jsonError, prettyJson, RawJson, stringifyBody } from './rawJson.ts';

const big = '{"orderId":9007199254740993,"amount":0.1000000000000000055}';

test('stringifyBody splices a RawJson member verbatim beside the other fields', () => {
  assert.equal(stringifyBody({ input: new RawJson(big), reasonMessage: 'fix' }), `{"reasonMessage":"fix","input":${big}}`);
});

test('stringifyBody sends a RawJson body as its own text', () => {
  assert.equal(stringifyBody(new RawJson('12345678901234567890')), '12345678901234567890');
});

test('stringifyBody splices into an object with no other present field', () => {
  assert.equal(stringifyBody({ input: new RawJson('[1]'), text: undefined }), '{"input":[1]}');
});

test('stringifyBody falls back to JSON.stringify without a RawJson', () => {
  assert.equal(stringifyBody({ a: 1, b: [true] }), '{"a":1,"b":[true]}');
  assert.equal(stringifyBody(null), 'null');
});

test('prettyJson indents like JSON.stringify(value, null, 2) and keeps every digit', () => {
  assert.equal(prettyJson(big), '{\n  "orderId": 9007199254740993,\n  "amount": 0.1000000000000000055\n}');
  const plain = '{"a":[1,{"b":"x, y: {z}"}],"c":{},"d":[]}';
  assert.equal(prettyJson(plain), JSON.stringify(JSON.parse(plain), null, 2));
});

test('prettyJson leaves escaped quotes inside strings alone', () => {
  assert.equal(prettyJson('{"q":"say \\"hi\\", ok"}'), '{\n  "q": "say \\"hi\\", ok"\n}');
});

test('prettyJson re-indents already formatted text', () => {
  assert.equal(prettyJson('{\n    "a" : 1 ,\n "b":2}'), '{\n  "a": 1,\n  "b": 2\n}');
});

test('jsonError is null for one JSON value and a message otherwise', () => {
  assert.equal(jsonError(big), null);
  assert.equal(jsonError('"text"'), null);
  assert.notEqual(jsonError('{broken'), null);
  assert.notEqual(jsonError('hello'), null);
});
