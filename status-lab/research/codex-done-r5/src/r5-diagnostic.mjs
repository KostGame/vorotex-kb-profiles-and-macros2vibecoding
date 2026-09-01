import fs from 'node:fs';

const ALLOWED_EVIDENCE_FIELDS = new Set([
  'timestampUtc', 'source', 'event', 'sessionId', 'threadId', 'turnId',
  'terminalStatus', 'previousState', 'currentState', 'reason', 'correlationResult'
]);
const HOOK_EVENTS = new Set(['UserPromptSubmit', 'Stop', 'SessionEnd']);
const TERMINAL_STATUSES = new Set(['completed', 'interrupted', 'failed']);

function text(value) { return typeof value === 'string' ? value : ''; }

export function sanitizeEvent(input) {
  const event = {
    timestampUtc: text(input.timestampUtc),
    source: text(input.source),
    event: text(input.event),
    sessionId: text(input.sessionId),
    threadId: text(input.threadId),
    turnId: text(input.turnId),
    terminalStatus: text(input.terminalStatus),
    previousState: text(input.previousState),
    currentState: text(input.currentState),
    reason: text(input.reason),
    correlationResult: text(input.correlationResult)
  };
  return Object.fromEntries(Object.entries(event).filter(([, value]) => value !== ''));
}

function parseTime(event, index) {
  const parsed = Date.parse(event.timestampUtc);
  return Number.isNaN(parsed) ? [Number.MAX_SAFE_INTEGER, index] : [parsed, index];
}

function same(a, b) {
  return a && b && a === b;
}

export function diagnose(inputEvents) {
  const events = inputEvents
    .map(input => ({ ...sanitizeEvent(input), _sessionThreadId: text(input.sessionThreadId ?? input.threadId) }))
    .filter(event => event.timestampUtc && event.source && event.event)
    .map((event, index) => ({ event, index }))
    .sort((a, b) => parseTime(a.event, a.index)[0] - parseTime(b.event, b.index)[0])
    .map(({ event }) => event);

  const unique = [];
  const seen = new Set();
  for (const event of events) {
    const key = JSON.stringify(event);
    if (!seen.has(key)) { seen.add(key); unique.push(event); }
  }

  const prompts = unique.filter(event => event.source === 'codex_hook' && event.event === 'UserPromptSubmit' && event.sessionId && event.turnId);
  const evidence = [];
  const cases = [];
  for (const prompt of prompts) {
    const sessionEvents = unique.filter(event => event.sessionId === prompt.sessionId && (!event.turnId || event.turnId === prompt.turnId));
    const stops = sessionEvents.filter(event => event.source === 'codex_hook' && event.event === 'Stop');
    const sessionThreadId = prompt._sessionThreadId || '';
    const completions = unique.filter(event => event.source === 'codex_stdio_bridge' && event.event === 'turn_completed' &&
      event.turnId === prompt.turnId && event.threadId);
    const matching = completions.filter(event => event.threadId === prompt.sessionId || event.threadId === sessionThreadId);
    const mismatchCandidate = sessionThreadId === '' && completions.some(event => event.threadId === prompt.sessionId);
    const completion = matching[0];
    const terminalStatus = completion?.terminalStatus || '';
    let currentState = 'RUNNING';
    let reason = '';
    let correlationResult = 'not_correlated';
    if (stops.length) {
      currentState = 'DONE_PENDING_ATTENTION';
      reason = 'codex_stop';
      correlationResult = 'stop_authority';
    } else if (completion && terminalStatus === 'completed' && matching.length === 1 && sessionThreadId) {
      currentState = 'DONE_PENDING_ATTENTION';
      reason = 'codex_turn_completed';
      correlationResult = 'exact_thread_turn';
    } else if (mismatchCandidate) {
      correlationResult = 'candidate_session_id_thread_id_mismatch';
    } else if (completion && !TERMINAL_STATUSES.has(terminalStatus)) {
      correlationResult = 'non_terminal_status';
    } else if (completion && terminalStatus !== 'completed') {
      correlationResult = 'non_success_terminal_status';
    } else if (completions.length) {
      correlationResult = 'identity_or_ambiguity_mismatch';
    }
    const completionTime = completion ? Date.parse(completion.timestampUtc) : NaN;
    const stopTime = stops[0] ? Date.parse(stops[0].timestampUtc) : NaN;
    const chronology = completion && stops[0] ? (completionTime < stopTime ? 'turn_completed_before_stop' : 'stop_before_turn_completed') : 'no_stop';
    const record = sanitizeEvent({
      timestampUtc: completion?.timestampUtc || prompt.timestampUtc,
      source: 'r5_diagnostic', event: 'session_state_changed', sessionId: prompt.sessionId,
      threadId: completion?.threadId || sessionThreadId, turnId: prompt.turnId,
      terminalStatus, previousState: 'RUNNING', currentState, reason, correlationResult
    });
    evidence.push(record);
    cases.push({ sessionId: prompt.sessionId, turnId: prompt.turnId, stopCount: stops.length,
      completionCount: completions.length, chronology, currentState, reason, correlationResult,
      mismatchCandidate, duplicateCompletionCount: Math.max(0, completions.length - 1) });
  }
  return { evidence, cases, duplicateEventsRemoved: events.length - unique.length };
}

export function persistEvidence(filePath, evidence) {
  const safe = evidence.map(sanitizeEvent);
  for (const record of safe) {
    if (Object.keys(record).some(key => !ALLOWED_EVIDENCE_FIELDS.has(key))) throw new Error('forbidden evidence field');
  }
  fs.writeFileSync(filePath, safe.map(record => JSON.stringify(record)).join('\n') + (safe.length ? '\n' : ''), 'utf8');
}

export function evaluatePreflight(input) {
  const checks = {
    repositoryRuntimeCompatible: input.repositoryRuntimeCompatible === true,
    bothHomesCanonical: input.bothHomesCanonical === true,
    hookHealthFlagsEmpty: Array.isArray(input.hookHealthFlags) && input.hookHealthFlags.length === 0,
    stableLoggerExists: input.stableLoggerExists === true,
    productionBridgePathExists: input.productionBridgePathExists === true,
    userCodexCliPathBaselineRecorded: input.userCodexCliPathBaselineRecorded === true,
    machineCodexCliPathMutated: input.machineCodexCliPathMutated === true,
    windowsAppsMutated: input.windowsAppsMutated === true,
    rollbackDeterministic: input.rollbackDeterministic === true
  };
  const blockers = Object.entries(checks).filter(([name, value]) =>
    (name === 'machineCodexCliPathMutated' || name === 'windowsAppsMutated') ? value : !value)
    .map(([name]) => name);
  return { status: blockers.length === 0 ? 'READY' : 'BLOCKED', checks, blockers };
}

export { ALLOWED_EVIDENCE_FIELDS, HOOK_EVENTS };
