import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { diagnose, evaluatePreflight, persistEvidence, ALLOWED_EVIDENCE_FIELDS } from '../src/r5-diagnostic.mjs';

const base = { source: 'codex_hook', sessionId: 'S', turnId: 'T' };
const hook = (event, timestampUtc, extra = {}) => ({ ...base, event, timestampUtc, sessionThreadId: 'S', ...extra });
const completion = (status = 'completed', timestampUtc = '2026-01-01T00:00:02Z', threadId = 'S') =>
  ({ source: 'codex_stdio_bridge', event: 'turn_completed', timestampUtc, threadId, turnId: 'T', terminalStatus: status });

test('A: completion without Stop is the diagnostic candidate', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion()]);
  assert.equal(result.cases[0].reason, 'codex_turn_completed');
  assert.equal(result.cases[0].currentState, 'DONE_PENDING_ATTENTION');
  assert.equal(result.cases[0].chronology, 'no_stop');
});

test('B/C: Stop remains the authority and chronology is retained', () => {
  for (const events of [
    [hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), hook('Stop', '2026-01-01T00:00:01Z'), completion()],
    [hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion(), hook('Stop', '2026-01-01T00:00:03Z')]
  ]) {
    const result = diagnose(events);
    assert.equal(result.cases[0].reason, 'codex_stop');
    assert.match(result.cases[0].chronology, /stop|no_stop/);
  }
});

test('D: mismatching identity does not correlate', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion('completed', '2026-01-01T00:00:02Z', 'OTHER')]);
  assert.equal(result.cases[0].correlationResult, 'identity_or_ambiguity_mismatch');
  assert.equal(result.cases[0].reason, '');
});

test('E: empty session.ThreadId reports the future fix candidate without applying it', () => {
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z', { sessionThreadId: '' }), completion()]);
  assert.equal(result.cases[0].mismatchCandidate, true);
  assert.equal(result.cases[0].correlationResult, 'candidate_session_id_thread_id_mismatch');
  assert.equal(result.cases[0].reason, '');
});

test('F: interrupted, failed, and inProgress are not successful DONE authority', () => {
  for (const status of ['interrupted', 'failed', 'inProgress']) {
    const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), completion(status)]);
    assert.notEqual(result.cases[0].reason, 'codex_turn_completed');
  }
});

test('G: duplicate completion is deterministic and not a second evidence claim', () => {
  const event = completion();
  const result = diagnose([hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), event, event]);
  assert.equal(result.duplicateEventsRemoved, 1);
  assert.equal(result.evidence.length, 1);
});

test('H: parallel sessions do not cross-correlate', () => {
  const events = [hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), hook('UserPromptSubmit', '2026-01-01T00:00:01Z', { sessionId: 'S2', turnId: 'T2' }), completion()];
  const result = diagnose(events);
  assert.equal(result.cases.find(item => item.sessionId === 'S').reason, 'codex_turn_completed');
  assert.equal(result.cases.find(item => item.sessionId === 'S2').reason, '');
});

test('I: persisted evidence contains only the sanitized allowlist', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'r5-'));
  const file = path.join(dir, 'evidence.jsonl');
  try {
    const result = diagnose([{ ...hook('UserPromptSubmit', '2026-01-01T00:00:00Z'), prompt: 'secret', tool_input: 'secret' }, completion()]);
    persistEvidence(file, result.evidence);
    for (const line of fs.readFileSync(file, 'utf8').trim().split('\n')) {
      const record = JSON.parse(line);
      assert.deepEqual(Object.keys(record).filter(key => !ALLOWED_EVIDENCE_FIELDS.has(key)), []);
      assert.equal(JSON.stringify(record).includes('secret'), false);
    }
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('J: any hook-health failure blocks the future live canary', () => {
  const good = evaluatePreflight({ repositoryRuntimeCompatible: true, bothHomesCanonical: true, hookHealthFlags: [], stableLoggerExists: true, productionBridgePathExists: true, userCodexCliPathBaselineRecorded: true, rollbackDeterministic: true });
  assert.equal(good.status, 'READY');
  const bad = evaluatePreflight({ ...good.checks, hookHealthFlags: ['duplicate Stop'] });
  assert.equal(bad.status, 'BLOCKED');
  assert.ok(bad.blockers.includes('hookHealthFlagsEmpty'));
});
