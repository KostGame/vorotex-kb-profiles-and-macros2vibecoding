import assert from 'node:assert/strict';
import test from 'node:test';
import { probeStateText, UNREAD_PROBE_SOURCE } from '../src/unread-probe.mjs';
import { parseUnreadProbeArgs } from '../src/unread-probe-cli-args.mjs';
import { mapUnreadThreadToSession } from '../src/unread-session-mapping.mjs';

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

test('missing host partition fails closed as Unknown', () => {
  const actual = probe(state({ remote: ['thread-A'] }));
  assert.equal(actual.state, 'Unknown');
  assert.equal(actual.matched, false);
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

test('CLI requires exactly one occurrence of each selector', () => {
  const exact = ['--state-path', 'state.json', '--host', 'local', '--thread-id', 'thread-A'];
  assert.deepEqual(parseUnreadProbeArgs(exact), { statePath: 'state.json', host: 'local', threadId: 'thread-A' });
  assert.equal(parseUnreadProbeArgs(['--state-path', 'state.json', '--host', 'local', '--host', 'remote', '--thread-id', 'thread-A']), undefined);
  assert.equal(parseUnreadProbeArgs(['--state-path', 'state.json', '--host', 'local', '--thread-id', 'thread-A', '--thread-id', 'thread-B']), undefined);
  assert.equal(parseUnreadProbeArgs(['--state-path', 'state-a.json', '--state-path', 'state-b.json', '--host', 'local', '--thread-id', 'thread-A']), undefined);
  assert.equal(parseUnreadProbeArgs([...exact, '--unknown', 'value']), undefined);
});

const doneEvent = (sessionId, threadId, turnId, extra = {}) => ({
  source: 'state_normalizer', event: 'session_state_changed', plane: 'per_session',
  sessionId, current: 'DONE_PENDING_ATTENTION', isRehydrated: false,
  correlation: { threadId, turnId, rpcIdType: '', rpcId: '' }, ...extra
});

test('exact DONE session correlation maps persisted thread to one session', () => {
  assert.deepEqual(mapUnreadThreadToSession('thread-A', [
    doneEvent('session-A', 'thread-A', 'turn-A'),
    doneEvent('session-B', 'thread-B', 'turn-B')
  ]), { state: 'Found', threadId: 'thread-A', sessionId: 'session-A', turnId: 'turn-A', matched: true });
});

test('missing or ambiguous mappings fail closed without cross-session fallback', () => {
  assert.equal(mapUnreadThreadToSession('thread-missing', [doneEvent('session-A', 'thread-A', 'turn-A')]).state, 'Unknown');
  assert.equal(mapUnreadThreadToSession('session-A', [doneEvent('session-A', 'thread-A', 'turn-A')]).state, 'Unknown');
  assert.equal(mapUnreadThreadToSession('thread-A', [
    doneEvent('session-A', 'thread-A', 'turn-A'), doneEvent('session-B', 'thread-A', 'turn-B')
  ]).state, 'Unknown');
});

test('mapping ignores non-DONE, malformed, oversized, and privacy-content fields', () => {
  const actual = mapUnreadThreadToSession('thread-A', [
    doneEvent('session-A', 'thread-A', 'turn-A', { current: 'RUNNING', prompt: 'PRIVATE' }),
    doneEvent('session-A', 'thread-A', 'turn-A', { response: 'PRIVATE' })
  ]);
  assert.equal(actual.state, 'Found');
  assert.equal(actual.sessionId, 'session-A');
  assert.equal(JSON.stringify(actual).includes('PRIVATE'), false);
});

test('a malformed target correlation fails closed', () => {
  assert.equal(mapUnreadThreadToSession('thread-A', [
    doneEvent('session-A', 'thread-A', '')
  ]).state, 'Unknown');
  assert.equal(mapUnreadThreadToSession('thread-A', [
    doneEvent('x'.repeat(1025), 'thread-A', 'turn-A')
  ]).state, 'Unknown');
});

test('duplicate evidence is safe and multiple turns do not select by ordering', () => {
  const actual = mapUnreadThreadToSession('thread-A', [
    doneEvent('session-A', 'thread-A', 'turn-A'),
    doneEvent('session-A', 'thread-A', 'turn-A'),
    doneEvent('session-A', 'thread-A', 'turn-B')
  ]);
  assert.deepEqual(actual, { state: 'Found', threadId: 'thread-A', sessionId: 'session-A', turnId: '', matched: true });
});
