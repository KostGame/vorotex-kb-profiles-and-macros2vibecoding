import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { adaptProductionSessionEvent, diagnose, evaluatePreflight, persistEvidence, ALLOWED_EVIDENCE_FIELDS, RESULTS } from '../src/r5-diagnostic.mjs';

const hook = (event, timestampUtc, extra = {}) => ({ source: 'codex_hook', event, timestampUtc, sessionId: 'S', turnId: 'T', ...extra });
const completion = (status = 'completed', threadId = 'S', timestampUtc = '2026-01-01T00:00:02Z') => ({ source: 'codex_stdio_bridge', event: 'turn_completed', timestampUtc, threadId, turnId: 'T', terminalStatus: status });
const state = (reason = 'codex_turn_completed', isRehydrated = false, threadId = 'S', timestampUtc = '2026-01-01T00:00:03Z', extra = {}) => ({ source: 'state_normalizer', event: 'session_state_changed', plane: 'per_session', sessionId: 'S', previous: 'RUNNING', current: 'DONE_PENDING_ATTENTION', reason, sourceTimestampUtc: timestampUtc, isRehydrated, correlation: { threadId, turnId: 'T', rpcIdType: '', rpcId: '' }, ...extra });
const running = (threadId = '', timestampUtc = '2026-01-01T00:00:01Z') => ({ source: 'state_normalizer', event: 'session_state_changed', plane: 'per_session', sessionId: 'S', previous: 'NORMAL', current: 'RUNNING', reason: 'codex_user_prompt_submit', sourceTimestampUtc: timestampUtc, isRehydrated: false, correlation: { threadId, turnId: 'T', rpcIdType: '', rpcId: '' } });

test('A: real no-Stop production transition is accepted', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion(), state()]);
  assert.equal(result.cases[0].result, RESULTS.ACCEPTED); assert.equal(result.cases[0].productionDone, true);
});
test('B: negative guard rejects completion without any production session event', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z', { threadId: 'S' }), completion()]);
  assert.equal(result.cases[0].result, RESULTS.NO_PRODUCTION_DONE); assert.equal(result.cases[0].productionDone, false);
});
test('B2: live completion reason with the wrong previous state is not acceptance', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion(), state('codex_turn_completed', false, 'S', '2026-01-01T00:00:03Z', { previous: 'NORMAL' })]);
  assert.equal(result.cases[0].result, RESULTS.NO_PRODUCTION_DONE); assert.equal(result.cases[0].productionDone, false);
});
test('C/D: real Stop transition is authoritative in either chronology', () => {
  for (const events of [[hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), hook('Stop', '2026-01-01T00:00:01Z'), state('codex_stop'), completion()], [hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion(), hook('Stop', '2026-01-01T00:00:03Z'), state('codex_stop')]]) assert.equal(diagnose(events).cases[0].result, RESULTS.STOP);
});
test('E: empty observed thread plus session-id completion is only a bounded candidate', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), running(), completion()]);
  assert.equal(result.cases[0].result, RESULTS.CANDIDATE); assert.equal(result.cases[0].productionDone, false);
});
test('F/G: wrong and unrelated identities are not exact correlation', () => {
  assert.equal(diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion('completed', 'S'), state('codex_turn_completed', false, 'OTHER')]).cases[0].result, RESULTS.IDENTITY);
  assert.equal(diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z', { threadId: 'S' }), completion('completed', 'OTHER')]).cases[0].result, RESULTS.IDENTITY);
});
test('H: ambiguous duplicate completion evidence fails closed', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion(), completion('completed', 'S', '2026-01-01T00:00:04Z'), state()]);
  assert.equal(result.cases[0].result, RESULTS.AMBIGUOUS);
});
test('I: non-success statuses never become successful authority', () => {
  for (const status of ['interrupted', 'failed', 'inProgress']) assert.equal(diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion(status), state()]).cases[0].result, RESULTS.NON_SUCCESS);
});
test('J: exact duplicate/replay chronology is deterministic', () => {
  const p = hook('UserPromptSubmit', '2026-01-01T00:00:00Z'); const c = completion(); const s = state(); const result = diagnose([p, c, s, p, c, s]);
  assert.equal(result.duplicateEventsRemoved, 3); assert.equal(result.cases[0].result, RESULTS.ACCEPTED);
});
test('K: actual nested production schema is adapted without raw correlation', () => {
  const adapted = adaptProductionSessionEvent(state()); assert.equal(adapted.currentState, 'DONE_PENDING_ATTENTION'); assert.equal(adapted.threadId, 'S'); assert.equal('correlation' in adapted, false);
});
test('L: rehydrated DONE is not live acceptance', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion(), state('codex_turn_completed', true)]);
  assert.equal(result.cases[0].result, RESULTS.REHYDRATED); assert.equal(result.cases[0].productionDone, false);
});
test('M: bare SessionEnd never creates DONE', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), hook('SessionEnd', '2026-01-01T00:00:02Z')]);
  assert.equal(result.cases[0].result, RESULTS.NO_COMPLETION); assert.equal(result.cases[0].productionDone, false);
});
test('N: persisted evidence is privacy-bounded and preflight fails closed', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'r5-')); const file = path.join(dir, 'evidence.jsonl');
  try {
    const result = diagnose([{ ...hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), prompt: 'secret', tool_input: 'secret' }, completion(), state()]); persistEvidence(file, result.evidence);
    for (const line of fs.readFileSync(file, 'utf8').trim().split('\n')) assert.deepEqual(Object.keys(JSON.parse(line)).filter(key => !ALLOWED_EVIDENCE_FIELDS.has(key)), []);
    const good = evaluatePreflight({ repositoryRuntimeCompatible: true, bothHomesCanonical: true, hookHealthFlags: [], stableLoggerExists: true, productionBridgePathExists: true, userCodexCliPathBaselineRecorded: true, rollbackDeterministic: true });
    assert.equal(good.status, 'READY'); assert.equal(evaluatePreflight({ ...good.checks, hookHealthFlags: ['duplicate Stop'] }).status, 'BLOCKED');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
