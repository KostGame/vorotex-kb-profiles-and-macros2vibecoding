const MAX_FIELD_BYTES = 1024;
const MAX_EVENTS = 10000;

export const MAPPING_STATES = Object.freeze({ FOUND: 'Found', UNKNOWN: 'Unknown' });

function boundedText(value) {
  return typeof value === 'string' && value.length > 0 && Buffer.byteLength(value, 'utf8') <= MAX_FIELD_BYTES
    ? value
    : undefined;
}

function unknown(threadId = '') {
  return { state: MAPPING_STATES.UNKNOWN, threadId, sessionId: '', turnId: '', matched: false };
}

/**
 * Maps one persisted unread thread ID through already-safe Status Lab
 * session_state_changed correlation metadata. This is diagnostic-only and
 * intentionally has no reducer, journal, tray, or RGB dependency.
 */
export function mapUnreadThreadToSession(unreadThreadId, events) {
  const threadId = boundedText(unreadThreadId);
  if (!threadId || !Array.isArray(events) || events.length > MAX_EVENTS) return unknown(threadId ?? '');

  const matches = new Map();
  let malformedTargetMatch = false;
  for (const event of events) {
    if (!event || typeof event !== 'object' || Array.isArray(event) ||
        event.source !== 'state_normalizer' || event.event !== 'session_state_changed' ||
        event.plane !== 'per_session' || event.current !== 'DONE_PENDING_ATTENTION' ||
        typeof event.isRehydrated !== 'boolean') continue;
    const sessionId = boundedText(event.sessionId);
    const correlation = event.correlation;
    if (!correlation || typeof correlation !== 'object' || Array.isArray(correlation)) continue;
    const correlationKeys = Object.keys(correlation);
    if (correlationKeys.some((key) => !['threadId', 'turnId', 'rpcIdType', 'rpcId'].includes(key))) continue;
    const correlatedThreadId = boundedText(correlation.threadId);
    if (correlation.threadId === threadId && !correlatedThreadId) malformedTargetMatch = true;
    const turnId = boundedText(correlation.turnId);
    if (correlatedThreadId !== threadId) continue;
    if (!turnId) {
      malformedTargetMatch = true;
      continue;
    }
    if (!sessionId) {
      malformedTargetMatch = true;
      continue;
    }
    if (!matches.has(sessionId)) matches.set(sessionId, new Set());
    matches.get(sessionId).add(turnId);
  }

  if (malformedTargetMatch || matches.size !== 1) return unknown(threadId);
  const [[sessionId, turnIds]] = matches;
  const turnId = turnIds.size === 1 ? [...turnIds][0] : '';
  return { state: MAPPING_STATES.FOUND, threadId, sessionId, turnId, matched: true };
}
