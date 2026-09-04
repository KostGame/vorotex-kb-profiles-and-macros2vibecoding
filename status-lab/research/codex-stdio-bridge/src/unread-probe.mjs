import { open, stat } from 'node:fs/promises';

export const UNREAD_PROBE_SOURCE = 'electron-persisted-atom-state/unread-thread-ids-by-host-v1';
export const UNREAD_STATES = Object.freeze({
  UNAVAILABLE: 'Unavailable',
  UNKNOWN: 'Unknown',
  HAS_UNREAD: 'HasUnread',
  NO_UNREAD: 'NoUnread'
});

const MAX_STATE_BYTES = 16 * 1024 * 1024;
const MAX_FIELD_BYTES = 1024;
const MAX_HOST_BYTES = 256;
const MAX_UNREAD_IDS = 10000;

function boundedText(value, maxBytes = MAX_FIELD_BYTES) {
  return typeof value === 'string' && value.length > 0 && Buffer.byteLength(value, 'utf8') <= maxBytes
    ? value
    : undefined;
}

function result({ timestampUtc, host, threadId, state, unreadCount, matched }) {
  return {
    timestampUtc,
    probeSource: UNREAD_PROBE_SOURCE,
    host,
    threadId,
    state,
    unreadCount,
    matched
  };
}

function unknown(timestampUtc, host, threadId) {
  return result({ timestampUtc, host, threadId, state: UNREAD_STATES.UNKNOWN, unreadCount: 0, matched: false });
}

function unavailable(timestampUtc, host, threadId) {
  return result({ timestampUtc, host, threadId, state: UNREAD_STATES.UNAVAILABLE, unreadCount: 0, matched: false });
}

function skipString(text, start) {
  let escaped = false;
  for (let i = start + 1; i < text.length; i += 1) {
    const code = text.charCodeAt(i);
    if (escaped) { escaped = false; continue; }
    if (code === 0x5c) { escaped = true; continue; }
    if (code === 0x22) return i + 1;
    if (code < 0x20) return -1;
  }
  return -1;
}

function skipValue(text, start) {
  const first = text[start];
  if (first === '"') return skipString(text, start);
  if (first === '{' || first === '[') {
    const close = first === '{' ? '}' : ']';
    let depth = 0;
    let inString = false;
    let escaped = false;
    for (let i = start; i < text.length; i += 1) {
      const ch = text[i];
      if (inString) {
        if (escaped) escaped = false;
        else if (ch === '\\') escaped = true;
        else if (ch === '"') inString = false;
        continue;
      }
      if (ch === '"') { inString = true; continue; }
      if (ch === first) depth += 1;
      else if (ch === close && --depth === 0) return i + 1;
    }
    return -1;
  }
  const match = text.slice(start).match(/^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/);
  return match ? start + match[0].length : -1;
}

function skipWhitespace(text, start) {
  let i = start;
  while (i < text.length && /\s/.test(text[i])) i += 1;
  return i;
}

function findObjectPropertyValue(text, objectStart, expectedKey) {
  if (text[objectStart] !== '{') return { malformed: true };
  let i = skipWhitespace(text, objectStart + 1);
  let found;
  while (i < text.length && text[i] !== '}') {
    if (text[i] !== '"') return { malformed: true };
    const keyEnd = skipString(text, i);
    if (keyEnd < 0) return { malformed: true };
    let key;
    try { key = JSON.parse(text.slice(i, keyEnd)); } catch { return { malformed: true }; }
    i = skipWhitespace(text, keyEnd);
    if (text[i] !== ':') return { malformed: true };
    const valueStart = skipWhitespace(text, i + 1);
    const valueEnd = skipValue(text, valueStart);
    if (valueEnd < 0) return { malformed: true };
    if (key === expectedKey) {
      if (found) return { malformed: true };
      found = { start: valueStart, end: valueEnd };
    }
    i = skipWhitespace(text, valueEnd);
    if (text[i] === ',') i = skipWhitespace(text, i + 1);
    else if (text[i] !== '}') return { malformed: true };
  }
  if (text[i] !== '}') return { malformed: true };
  return { found, end: i + 1 };
}

function decodeValue(text, span) {
  if (!span) return undefined;
  try { return JSON.parse(text.slice(span.start, span.end)); } catch { return undefined; }
}

function validateIds(value, targetThreadId) {
  if (!Array.isArray(value) || value.length > MAX_UNREAD_IDS) return undefined;
  const ids = new Set();
  for (const id of value) {
    if (!boundedText(id) || ids.has(id)) continue;
    ids.add(id);
  }
  if (value.some((id) => typeof id !== 'string' || !boundedText(id))) return undefined;
  return { count: ids.size, matched: ids.has(targetThreadId) };
}

export function probeStateText(text, { threadId, host, now = () => new Date() } = {}) {
  const timestampUtc = now().toISOString();
  const safeThreadId = boundedText(threadId);
  const safeHost = boundedText(host, MAX_HOST_BYTES);
  if (typeof text !== 'string' || Buffer.byteLength(text, 'utf8') > MAX_STATE_BYTES || !safeThreadId || !safeHost) {
    return unknown(timestampUtc, safeHost ?? '', safeThreadId ?? '');
  }

  const root = findObjectPropertyValue(text, 0, 'electron-persisted-atom-state');
  if (root.malformed || root.end !== skipWhitespace(text, root.end)) return unknown(timestampUtc, safeHost, safeThreadId);
  if (!root.found) return unavailable(timestampUtc, safeHost, safeThreadId);
  const atom = findObjectPropertyValue(text, root.found.start, 'unread-thread-ids-by-host-v1');
  if (atom.malformed) return unknown(timestampUtc, safeHost, safeThreadId);
  if (!atom.found) return unavailable(timestampUtc, safeHost, safeThreadId);
  const atomText = text.slice(atom.found.start, atom.found.end);
  const hostSpan = findObjectPropertyValue(atomText, 0, safeHost);
  if (hostSpan.malformed) return unknown(timestampUtc, safeHost, safeThreadId);
  if (!hostSpan.found) return result({ timestampUtc, host: safeHost, threadId: safeThreadId, state: UNREAD_STATES.NO_UNREAD, unreadCount: 0, matched: false });
  const ids = validateIds(decodeValue(text.slice(atom.found.start, atom.found.end), hostSpan.found), safeThreadId);
  if (!ids) return unknown(timestampUtc, safeHost, safeThreadId);
  return result({ timestampUtc, host: safeHost, threadId: safeThreadId, state: ids.matched ? UNREAD_STATES.HAS_UNREAD : UNREAD_STATES.NO_UNREAD, unreadCount: ids.count, matched: ids.matched });
}

export async function probeStateFile(filePath, options = {}) {
  const timestampUtc = (options.now ?? (() => new Date()))().toISOString();
  const safeThreadId = boundedText(options.threadId) ?? '';
  const safeHost = boundedText(options.host, MAX_HOST_BYTES) ?? '';
  if (typeof filePath !== 'string' || !filePath || !safeThreadId || !safeHost) return unknown(timestampUtc, safeHost, safeThreadId);
  try {
    const info = await stat(filePath);
    if (!info.isFile() || info.size > MAX_STATE_BYTES) return unknown(timestampUtc, safeHost, safeThreadId);
    const handle = await open(filePath, 'r');
    try {
      const buffer = Buffer.alloc(info.size);
      await handle.read(buffer, 0, info.size, 0);
      return probeStateText(buffer.toString('utf8'), options);
    } finally { await handle.close(); }
  } catch { return unavailable(timestampUtc, safeHost, safeThreadId); }
}
