import fs from 'node:fs';

const ALLOWED_EVIDENCE_FIELDS = new Set(['timestampUtc', 'source', 'event', 'sessionId', 'threadId', 'turnId', 'terminalStatus', 'previousState', 'currentState', 'reason', 'correlationResult']);
const HOOK_EVENTS = new Set(['UserPromptSubmit', 'Stop', 'SessionEnd']);
const TERMINAL_STATUSES = new Set(['completed', 'interrupted', 'failed']);
const RESULTS = Object.freeze({ ACCEPTED: 'NO_STOP_LIVE_DONE_ACCEPTED', STOP: 'STOP_AUTHORED_DONE', NO_PRODUCTION_DONE: 'COMPLETION_PRESENT_BUT_NO_PRODUCTION_DONE', CANDIDATE: 'CORRELATION_FIX_CANDIDATE', IDENTITY: 'IDENTITY_MISMATCH', AMBIGUOUS: 'AMBIGUOUS_CORRELATION', NON_SUCCESS: 'NON_SUCCESS_TERMINAL_STATUS', REHYDRATED: 'REHYDRATED_DONE_NOT_LIVE_ACCEPTANCE', NO_COMPLETION: 'NO_COMPLETION' });
const text = value => typeof value === 'string' ? value : '';

export function sanitizeEvent(input) {
  const safe = { timestampUtc: text(input.timestampUtc), source: text(input.source), event: text(input.event), sessionId: text(input.sessionId), threadId: text(input.threadId), turnId: text(input.turnId), terminalStatus: text(input.terminalStatus), previousState: text(input.previousState), currentState: text(input.currentState), reason: text(input.reason), correlationResult: text(input.correlationResult) };
  return Object.fromEntries(Object.entries(safe).filter(([, value]) => value !== ''));
}

// Read the real Status Lab per-session shape. Nested correlation is transient.
export function adaptProductionSessionEvent(input) {
  if (!input || input.source !== 'state_normalizer' || input.event !== 'session_state_changed' || input.plane !== 'per_session' || !text(input.sessionId) || !text(input.previous) || !text(input.current) || !text(input.reason) || typeof input.isRehydrated !== 'boolean' || !text(input.sourceTimestampUtc) || !input.correlation || typeof input.correlation !== 'object') return null;
  const correlation = input.correlation;
  if (Object.keys(correlation).some(key => !['threadId', 'turnId', 'rpcIdType', 'rpcId'].includes(key))) return null;
  const threadId = text(correlation.threadId); const turnId = text(correlation.turnId);
  if (!threadId && !turnId) return null;
  return { timestampUtc: input.sourceTimestampUtc, source: 'state_normalizer', event: 'session_state_changed', sessionId: text(input.sessionId), threadId, turnId, previousState: text(input.previous), currentState: text(input.current), reason: text(input.reason), isRehydrated: input.isRehydrated };
}

function normalizeInput(input) { return adaptProductionSessionEvent(input) ?? { ...sanitizeEvent(input), _sessionThreadId: text(input.sessionThreadId ?? input.threadId) }; }
function time(event, index) { const parsed = Date.parse(event.timestampUtc); return Number.isNaN(parsed) ? [Number.MAX_SAFE_INTEGER, index] : [parsed, index]; }

export function diagnose(inputEvents) {
  const ordered = inputEvents.map(normalizeInput).filter(event => event.timestampUtc && event.source && event.event).map((event, index) => ({ event, index })).sort((a, b) => time(a.event, a.index)[0] - time(b.event, b.index)[0]).map(({ event }) => event);
  const unique = []; const seen = new Set();
  for (const event of ordered) { const key = JSON.stringify(event); if (!seen.has(key)) { seen.add(key); unique.push(event); } }
  const evidence = unique.map(event => sanitizeEvent(event)); const cases = [];
  for (const prompt of unique.filter(event => event.source === 'codex_hook' && event.event === 'UserPromptSubmit' && event.sessionId && event.turnId)) {
    const sameTurn = event => event.turnId === prompt.turnId;
    const stops = unique.filter(event => event.source === 'codex_hook' && event.event === 'Stop' && event.sessionId === prompt.sessionId && sameTurn(event));
    const completions = unique.filter(event => event.source === 'codex_stdio_bridge' && event.event === 'turn_completed' && sameTurn(event) && event.threadId);
    const states = unique.filter(event => event.source === 'state_normalizer' && event.event === 'session_state_changed' && event.sessionId === prompt.sessionId && sameTurn(event));
    const done = states.filter(event => event.currentState === 'DONE_PENDING_ATTENTION');
    const liveCompletionDone = done.filter(event => !event.isRehydrated && event.previousState === 'RUNNING' && event.currentState === 'DONE_PENDING_ATTENTION' && event.reason === 'codex_turn_completed');
    const liveStopDone = done.filter(event => !event.isRehydrated && event.reason === 'codex_stop');
    const exact = completions.filter(completion => liveCompletionDone.some(state => state.threadId === completion.threadId && state.turnId === completion.turnId));
    const sessionThreadId = states.find(event => event.threadId)?.threadId ?? prompt._sessionThreadId ?? '';
    const sessionIdMatches = completions.filter(event => event.threadId === prompt.sessionId);
    const liveRunningWithEmptyThread = states.some(event => !event.isRehydrated && event.currentState === 'RUNNING' && event.reason === 'codex_user_prompt_submit' && event.threadId === '');
    const ambiguous = completions.length > 1 || liveCompletionDone.length > 1;
    let result = RESULTS.NO_COMPLETION;
    if (liveStopDone.length && stops.length) result = RESULTS.STOP;
    else if (done.some(event => event.isRehydrated) && !exact.length) result = RESULTS.REHYDRATED;
    else if (completions.some(event => !TERMINAL_STATUSES.has(event.terminalStatus) || event.terminalStatus !== 'completed')) result = RESULTS.NON_SUCCESS;
    else if (ambiguous) result = RESULTS.AMBIGUOUS;
    else if (exact.length === 1 && stops.length === 0) result = RESULTS.ACCEPTED;
    else if (sessionIdMatches.length === 1 && liveRunningWithEmptyThread && !sessionThreadId && !liveCompletionDone.length) result = RESULTS.CANDIDATE;
    else if (completions.length && sessionThreadId && completions.some(event => event.threadId !== sessionThreadId)) result = RESULTS.IDENTITY;
    else if (completions.length && !liveCompletionDone.length) result = RESULTS.NO_PRODUCTION_DONE;
    else if (stops.length && !liveStopDone.length) result = RESULTS.NO_PRODUCTION_DONE;
    cases.push({ sessionId: prompt.sessionId, turnId: prompt.turnId, result, stopObserved: stops.length > 0, completionObserved: completions.length > 0, productionDone: result === RESULTS.ACCEPTED || result === RESULTS.STOP, completionIgnored: completions.length > 0 && exact.length === 0, completionCount: completions.length, doneTransitionCount: done.length, chronology: completions.length && stops.length ? (Date.parse(completions[0].timestampUtc) < Date.parse(stops[0].timestampUtc) ? 'turn_completed_before_stop' : 'stop_before_turn_completed') : stops.length ? 'stop_without_completion' : 'no_stop', sessionCorrelationThreadId: sessionThreadId });
  }
  return { evidence, cases, duplicateEventsRemoved: ordered.length - unique.length };
}

export function persistEvidence(filePath, evidence) {
  const safe = evidence.map(sanitizeEvent);
  if (safe.some(record => Object.keys(record).some(key => !ALLOWED_EVIDENCE_FIELDS.has(key)))) throw new Error('forbidden evidence field');
  fs.writeFileSync(filePath, safe.map(record => JSON.stringify(record)).join('\n') + (safe.length ? '\n' : ''), 'utf8');
}

export function evaluatePreflight(input) {
  const checks = { repositoryRuntimeCompatible: input.repositoryRuntimeCompatible === true, bothHomesCanonical: input.bothHomesCanonical === true, hookHealthFlagsEmpty: Array.isArray(input.hookHealthFlags) && input.hookHealthFlags.length === 0, stableLoggerExists: input.stableLoggerExists === true, productionBridgePathExists: input.productionBridgePathExists === true, userCodexCliPathBaselineRecorded: input.userCodexCliPathBaselineRecorded === true, machineCodexCliPathMutated: input.machineCodexCliPathMutated === true, windowsAppsMutated: input.windowsAppsMutated === true, rollbackDeterministic: input.rollbackDeterministic === true };
  const blockers = Object.entries(checks).filter(([name, value]) => name === 'machineCodexCliPathMutated' || name === 'windowsAppsMutated' ? value : !value).map(([name]) => name);
  return { status: blockers.length ? 'BLOCKED' : 'READY', checks, blockers };
}

export { ALLOWED_EVIDENCE_FIELDS, HOOK_EVENTS, RESULTS };
