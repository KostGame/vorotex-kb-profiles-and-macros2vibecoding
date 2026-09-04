import assert from 'node:assert/strict';
import test from 'node:test';
import { probeStateText, UNREAD_PROBE_SOURCE } from '../src/unread-probe.mjs';

const now = () => new Date('2026-09-05T00:00:00.000Z');
const state = (value) => JSON.stringify({
  unrelated: { title: 'PRIVATE MUST NOT EMIT', prompt: 'PRIVATE MUST NOT EMIT' },
  'electron-persisted-atom-state': { 'unread-thread-ids-by-host-v1': value, neighboring: 'PRIVATE MUST NOT EMIT' }
});
const probe = (text, threadId = 'thread-A', host = 'local') => probeStateText(text, { threadId, host, now });

test('present target is HasUnread with bounded metadata', () => {
  const actual = probe(state({ local: ['thread-A', 'thread-A', 'thread-B'], remote: [] }));
  assert.deepEqual(actual, { timestampUtc: '2026-09-05T00:00:00.000Z', probeSource: UNREAD_PROBE_SOURCE, host: 'local', threadId: 'thread-A', state: 'HasUnread', unreadCount: 2, matched: true });
});

test('absent target is NoUnread and host partitions stay isolated', () => {
  const actual = probe(state({ local: ['thread-B'], remote: ['thread-A'] }));
  assert.equal(actual.state, 'NoUnread');
  assert.equal(actual.matched, false);
  assert.equal(probe(state({ local: ['thread-B'], remote: ['thread-A'] }), 'thread-A', 'remote').state, 'HasUnread');
});

test('malformed state fails closed without emitting content', () => {
  const actual = probe('{"electron-persisted-atom-state":{"unread-thread-ids-by-host-v1":{"local":[}}');
  assert.equal(actual.state, 'Unknown');
  assert.deepEqual(Object.keys(actual).sort(), ['host', 'matched', 'probeSource', 'state', 'threadId', 'timestampUtc', 'unreadCount']);
  assert.equal(JSON.stringify(actual).includes('PRIVATE'), false);
});

test('missing atom is Unavailable', () => {
  assert.equal(probe(JSON.stringify({ 'electron-persisted-atom-state': {} })).state, 'Unavailable');
  assert.equal(probe(JSON.stringify({ other: true })).state, 'Unavailable');
});

test('duplicate expected keys and malformed host values fail closed', () => {
  assert.equal(probe('{"electron-persisted-atom-state":{"unread-thread-ids-by-host-v1":{},"unread-thread-ids-by-host-v1":{}}}').state, 'Unknown');
  assert.equal(probe(state({ local: ['thread-A', 7] })).state, 'Unknown');
});

test('oversized and malformed IDs are rejected', () => {
  assert.equal(probe(state({ local: ['thread-A'] }), 'x'.repeat(1025)).state, 'Unknown');
  assert.equal(probe(state({ local: ['x'.repeat(1025)] }), 'thread-A').state, 'Unknown');
});

test('result is diagnostic-only and cannot mutate K15 state', () => {
  const normalizedState = Object.freeze({ state: 'DONE_PENDING_ATTENTION' });
  const before = JSON.stringify(normalizedState);
  probe(state({ local: ['thread-A'] }));
  assert.equal(JSON.stringify(normalizedState), before);
  assert.equal(normalizedState.state, 'DONE_PENDING_ATTENTION');
});
