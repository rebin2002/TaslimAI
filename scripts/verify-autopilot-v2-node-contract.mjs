#!/usr/bin/env node
// Offline Node.js parity check for the Taslim Autopilot v2 golden vectors.
// Synthetic test key only. This is NOT the deployed Bridge signer or API receiver.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash, createHmac, timingSafeEqual } from 'node:crypto';

const metadataPattern = /^[A-Za-z0-9][A-Za-z0-9._:-]{0,199}$/;
const timestampPattern = /^[1-9][0-9]{0,11}$/;
const signaturePattern = /^sha256=[a-f0-9]{64}$/;

function strictBase64(value) {
  assert.equal(typeof value, 'string', 'base64 input must be text');
  assert.match(value, /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/);
  const bytes = Buffer.from(value, 'base64');
  assert.equal(bytes.toString('base64'), value, 'noncanonical base64 rejected');
  return bytes;
}

function canonicalFrame(vector, body) {
  assert.equal(typeof vector.timestamp, 'string');
  assert.match(vector.timestamp, timestampPattern);
  for (const key of ['source', 'eventType', 'externalEventId']) {
    assert.equal(typeof vector[key], 'string');
    assert.match(vector[key], metadataPattern, key);
  }
  assert.ok(Buffer.isBuffer(body) && body.length >= 1 && body.length <= 65536);
  const digest = createHash('sha256').update(body).digest('hex');
  return Buffer.from([
    'taslim-autopilot-v2',
    vector.timestamp,
    String(Buffer.byteLength(vector.source, 'utf8')) + ':' + vector.source,
    String(Buffer.byteLength(vector.eventType, 'utf8')) + ':' + vector.eventType,
    String(Buffer.byteLength(vector.externalEventId, 'utf8')) + ':' + vector.externalEventId,
    digest,
    '',
  ].join('\n'), 'utf8');
}

function verifyVector(vector, secret) {
  assert.match(vector.expectedSignature, signaturePattern);
  const body = strictBase64(vector.bodyBase64);
  const frame = canonicalFrame(vector, body);
  assert.deepEqual(frame, strictBase64(vector.signedFrameBase64), vector.name + ' signed-frame mismatch');
  const mac = 'sha256=' + createHmac('sha256', secret).update(frame).digest('hex');
  assert.ok(timingSafeEqual(Buffer.from(mac), Buffer.from(vector.expectedSignature)), vector.name + ' MAC mismatch');
  return { body, frame, mac };
}

const file = process.argv[2];
assert.ok(file && process.argv.length === 3, 'usage: node verify-autopilot-v2-node-contract.mjs vectors.json');
const fixture = JSON.parse(readFileSync(file, 'utf8'));
assert.equal(fixture.schema, 'taslim.autopilot.signed-completion-v2.vectors.v1');
assert.equal(typeof fixture.syntheticSecretUtf8, 'string');
const key = Buffer.from(fixture.syntheticSecretUtf8, 'utf8');
assert.ok(key.length >= 32);
assert.ok(Array.isArray(fixture.vectors) && fixture.vectors.length === 12);
const names = new Set();
const signatures = new Set();
const signedFrames = new Set();

for (const vector of fixture.vectors) {
  assert.ok(vector && typeof vector.name === 'string' && !names.has(vector.name), 'duplicate vector name');
  names.add(vector.name);
  const verified = verifyVector(vector, key);
  assert.ok(!signatures.has(verified.mac), 'different vectors must not reuse signatures');
  assert.ok(!signedFrames.has(verified.frame.toString('base64')), 'different vectors must not reuse signed frames');
  signatures.add(verified.mac);
  signedFrames.add(verified.frame.toString('base64'));

  // Independent fail-closed regressions: changed body, identity or signed frame
  // cannot pass an unchanged signature, even for semantically equivalent JSON.
  const tampered = { ...vector, source: vector.source + '-tampered' };
  assert.throws(() => verifyVector(tampered, key), /signed-frame mismatch/);
  const mutatedBody = Buffer.concat([verified.body, Buffer.from(' ')]);
  const mutatedFrame = canonicalFrame(vector, mutatedBody);
  assert.ok(!timingSafeEqual(
    createHmac('sha256', key).update(mutatedFrame).digest(),
    createHmac('sha256', key).update(verified.frame).digest()
  ), vector.name + ' raw-body mutation');
}
assert.ok(names.has('unchanged'));
assert.ok(names.has('raw_unicode'));
assert.ok(names.has('raw_unicode_escaped'));
for (const bad of ['', '0', '01800000000', '9'.repeat(13)]) {
  assert.throws(() => canonicalFrame({ ...fixture.vectors[0], timestamp: bad }, Buffer.from('a')));
}
for (const bad of ['bad metadata', 'a'.repeat(201), 'bad\nheader', 'métadata']) {
  assert.throws(() => canonicalFrame({ ...fixture.vectors[0], source: bad }, Buffer.from('a')));
}
for (const bad of [Buffer.alloc(0), Buffer.alloc(65537)]) {
  assert.throws(() => canonicalFrame(fixture.vectors[0], bad));
}
console.log('PASS: Node.js independently reproduced 12 raw-byte v2 signing vectors and fail-closed negative cases (offline; no Bridge/API invocation)');