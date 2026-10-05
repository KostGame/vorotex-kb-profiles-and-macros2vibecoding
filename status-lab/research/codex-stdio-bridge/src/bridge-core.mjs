import { appendFile as appendFileAsync, mkdir as mkdirAsync, rename as renameAsync, unlink as unlinkAsync, writeFile as writeFileAsync } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import os from 'node:os';
import path from 'node:path';

const REQUEST_FAMILIES = new Map([
  ['item/commandExecution/requestApproval', 'item/commandExecution'],
  ['item/fileChange/requestApproval', 'item/fileChange'],
  ['item/permissions/requestApproval', 'item/permissions']
]);
const PERMISSIONS_REQUEST_FAMILY = 'item/permissions';

const DECISIONS = new Set(['accept', 'acceptForSession', 'decline', 'cancel']);
const MAX_PENDING = 256;
const MAX_SEEN_REQUEST_IDS = 4096;
const MAX_REVIEWER_THREADS = 512;
const MAX_PARTIAL_BYTES = 64 * 1024;
const MAX_FIELD_BYTES = 1024;
export const APPROVAL_SCHEMA_VERSION = 'k15-codex-approval/v1';
export const APPROVAL_REQUEST_SCHEMA_VERSION = 'k15-codex-approval-request/v1';
export const PERMISSIONS_APPROVAL_DIAGNOSTIC_SCHEMA_VERSION = 'k15-codex-permissions-approval-diagnostic/v1';
export const COMPLETION_SCHEMA_VERSION = 'k15-codex-completion/v1';
export const THREAD_STATUS_SCHEMA_VERSION = 'k15-codex-thread-status/v1';
export const THREAD_METADATA_SCHEMA_VERSION = 'k15-codex-thread-metadata/v1';
const COMPLETION_STATUSES = new Set(['completed', 'interrupted', 'failed']);
const THREAD_STATUSES = new Set(['notLoaded', 'idle', 'active', 'systemError']);
const THREAD_FLAGS = new Set(['waitingOnApproval', 'waitingOnUserInput']);
const THREAD_CLASSIFICATIONS = new Set(['user', 'service', 'canary']);
const MAX_AUTHORITY_QUEUE = 64;
const MAX_DIAGNOSTIC_FILE_BYTES = 64 * 1024;
export const BRIDGE_DIAGNOSTICS_SCHEMA_VERSION = 'k15-codex-bridge-diagnostics/v1';
export const BRIDGE_DIAGNOSTIC_REJECTION_REASONS = Object.freeze([
  'invalid_envelope', 'invalid_thread_id', 'invalid_status', 'invalid_active_flags',
  'invalid_emitted_at_ms', 'invalid_classification'
]);

function optionalString(value) {
  return typeof value === 'string' && value.length > 0 && Buffer.byteLength(value, 'utf8') <= MAX_FIELD_BYTES
    ? value
    : undefined;
}

function rpcId(value) {
  if (typeof value === 'string') {
    return value.length > 0 && Buffer.byteLength(value, 'utf8') <= MAX_FIELD_BYTES
      ? { type: 'string', value }
      : undefined;
  }
  if (typeof value === 'number' && Number.isSafeInteger(value)) {
    // JSON numeric IDs use numeric equality. Canonicalize both +0 and -0 to
    // the same identity so a delayed response cannot cross-correlate through
    // alternate zero serialization.
    return { type: 'number', value: value === 0 ? '0' : String(value) };
  }
  return undefined;
}

// Match CodexSourceIdentity.ForHome on the Windows Status Lab host. Restrict
// canonical input to ASCII so JS/.NET invariant casing differences fail closed.
export function codexSourceInstanceId(env = process.env, platform = process.platform, homeDirectory = os.homedir()) {
  if (platform !== 'win32') return undefined;
  let home = typeof env.CODEX_HOME === 'string' && env.CODEX_HOME.length > 0
    ? env.CODEX_HOME
    : path.win32.join(homeDirectory, '.codex');
  home = home.replace(/%([^%]+)%/g, (match, name) => env[name] ?? match);
  if (!path.win32.isAbsolute(home) || /%[^%]+%/.test(home) || /[^\x00-\x7f]/.test(home)) return undefined;
  let canonical = path.win32.resolve(home).replaceAll('/', '\\');
  const root = path.win32.parse(canonical).root;
  if (canonical.toUpperCase() !== root.toUpperCase()) canonical = canonical.replace(/[\\/]+$/, '');
  canonical = canonical.toUpperCase();
  const digest = createHash('sha256').update(`codex-home/v1\0${canonical}`, 'utf8').digest('hex');
  return `local:${digest.slice(0, 32)}`;
}

function pendingKey(family, id, metadata = {}) {
  return JSON.stringify([
    family,
    id.type,
    id.value,
    metadata.threadId ?? '',
    metadata.turnId ?? '',
    metadata.itemId ?? ''
  ]);
}

/**
 * Counts only fixed, sanitized bridge facts. It never receives a JSON string
 * and never exposes a protocol object to its sink. The partial buffer is
 * bounded framing state and is discarded when it exceeds the limit.
 */
export class BridgeDiagnostics {
  #sink; #busy = false; #pending; #partial = Buffer.alloc(0); #snapshot;
  constructor({ sink = () => {}, generation = undefined, clock = () => new Date() } = {}) {
    this.#sink = sink; this.#clock = clock;
    this.#snapshot = { schemaVersion: BRIDGE_DIAGNOSTICS_SCHEMA_VERSION, component: 'codex_stdio_bridge',
      ...(typeof generation === 'string' && /^[A-Za-z0-9._-]{1,128}$/.test(generation) ? { generation } : {}),
      serverStdoutChunks: 0, serverStdoutBytes: 0, completeRecords: 0, jsonParseSuccess: 0, jsonParseFailure: 0,
      threadStatusSeen: 0, threadStartedSeen: 0, nativeStatusAccepted: 0,
      rejectedStatus: Object.fromEntries(BRIDGE_DIAGNOSTIC_REJECTION_REASONS.map(reason => [reason, 0])),
      authorityQueue: { accepted: 0, delivered: 0, overflow: 0, sinkFailures: 0 },
      namedPipe: { connectAttempts: 0, connectSuccesses: 0, connectFailures: 0,
        frameWriteAttempts: 0, frameWriteSuccesses: 0, frameWriteFailures: 0,
        failureReasons: { unavailable: 0, timeout: 0, busy: 0, unknown: 0 },
        writeFailureReasons: { unavailable: 0, remote_close: 0, unknown: 0 } },
      updatedAtUtc: this.#timestamp()
    };
  }
  #clock;
  observeServerChunk(chunk) {
    const input = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    this.#snapshot.serverStdoutChunks += 1; this.#snapshot.serverStdoutBytes += input.length;
    const combined = this.#partial.length === 0 ? input : Buffer.concat([this.#partial, input]);
    let start = 0;
    for (let index = 0; index < combined.length; index += 1) {
      if (combined[index] !== 0x0a) continue;
      const line = combined.subarray(start, index);
      this.#snapshot.completeRecords += 1;
      if (line.length > MAX_PARTIAL_BYTES) this.#snapshot.jsonParseFailure += 1;
      else {
        try {
          const message = JSON.parse(line.toString('utf8'));
          this.#snapshot.jsonParseSuccess += 1;
          if (message?.method === 'thread/status/changed') this.#snapshot.threadStatusSeen += 1;
          if (message?.method === 'thread/started') this.#snapshot.threadStartedSeen += 1;
        } catch { this.#snapshot.jsonParseFailure += 1; }
      }
      start = index + 1;
    }
    const remainder = combined.subarray(start);
    this.#partial = remainder.length > MAX_PARTIAL_BYTES ? Buffer.alloc(0) : Buffer.from(remainder);
    this.#flush();
  }
  recordStatusAccepted() { this.#snapshot.nativeStatusAccepted += 1; this.#flush(); }
  recordStatusRejected(reason) {
    if (BRIDGE_DIAGNOSTIC_REJECTION_REASONS.includes(reason)) this.#snapshot.rejectedStatus[reason] += 1;
    this.#flush();
  }
  recordAuthority(kind) {
    if (Object.hasOwn(this.#snapshot.authorityQueue, kind)) this.#snapshot.authorityQueue[kind] += 1;
    this.#flush();
  }
  recordPipe(kind, reason = 'unknown') {
    if (kind === 'connectAttempts') this.#snapshot.namedPipe.connectAttempts += 1;
    else if (kind === 'connectSuccesses') this.#snapshot.namedPipe.connectSuccesses += 1;
    else if (kind === 'connectFailures') {
      this.#snapshot.namedPipe.connectFailures += 1;
      if (Object.hasOwn(this.#snapshot.namedPipe.failureReasons, reason)) this.#snapshot.namedPipe.failureReasons[reason] += 1;
      else this.#snapshot.namedPipe.failureReasons.unknown += 1;
    }
    else if (kind === 'frameWriteAttempts') this.#snapshot.namedPipe.frameWriteAttempts += 1;
    else if (kind === 'frameWriteSuccesses') this.#snapshot.namedPipe.frameWriteSuccesses += 1;
    else if (kind === 'frameWriteFailures') {
      this.#snapshot.namedPipe.frameWriteFailures += 1;
      if (Object.hasOwn(this.#snapshot.namedPipe.writeFailureReasons, reason)) this.#snapshot.namedPipe.writeFailureReasons[reason] += 1;
      else this.#snapshot.namedPipe.writeFailureReasons.unknown += 1;
    }
    this.#flush();
  }
  snapshot() { return JSON.parse(JSON.stringify(this.#snapshot)); }
  #timestamp() { try { const value = this.#clock?.(); return value instanceof Date && !Number.isNaN(value.getTime()) ? value.toISOString() : undefined; } catch { return undefined; } }
  #flush() {
    const timestamp = this.#timestamp(); if (timestamp) this.#snapshot.updatedAtUtc = timestamp;
    const current = this.snapshot();
    if (this.#busy) { this.#pending = current; return; }
    this.#busy = true;
    try { Promise.resolve(this.#sink(current)).catch(() => {}).finally(() => {
      this.#busy = false;
      if (this.#pending) { this.#pending = undefined; this.#flush(); }
    }); } catch { this.#busy = false; }
  }
}

/**
 * Observes the proven live JSON-RPC approval shape without retaining raw JSON
 * records. Requests use method + top-level id; responses use the same id and
 * result.decision, with no response method.
 * The partial buffer is transient stream framing only; it is bounded and never
 * passed to the telemetry sink or written to disk.
 */
export class ApprovalObserver {
  #pending = new Map();
  #serverPartial = Buffer.alloc(0);
  #clientPartial = Buffer.alloc(0);
  #sink;
  #sinkBusy = false;
  #ownerQueue = [];
  #telemetryQueue = [];
  #ownerCredits = 0;
  #seenRequestIds = new Set();
  #requestIdHistorySaturated = false;
  #reviewers = new Map();
  #reviewerRequests = new Map();
  #turnReviewers = new Map();

  constructor({ telemetrySink = () => {}, sourceInstanceId = codexSourceInstanceId() } = {}) {
    this.#sink = telemetrySink;
    this.#sourceInstanceId = /^local:[0-9a-f]{32}$/.test(sourceInstanceId ?? '') ? sourceInstanceId : undefined;
  }
  #sourceInstanceId;

  observeServerChunk(chunk) {
    this.#observeChunk(chunk, 'server', (message) => {
      this.#observeReviewerServer(message);
      this.#trackRequest(message);
      this.#trackCompletion(message);
    });
  }

  observeClientChunk(chunk) {
    this.#observeChunk(chunk, 'client', (message) => {
      this.#observeReviewerClient(message);
      this.#resolveResponse(message);
    });
  }

  pendingCount() {
    return this.#pending.size;
  }

  #observeChunk(chunk, direction, handle) {
    const input = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    const partial = direction === 'server' ? this.#serverPartial : this.#clientPartial;
    const combined = partial.length === 0 ? input : Buffer.concat([partial, input]);
    let start = 0;

    for (let index = 0; index < combined.length; index += 1) {
      if (combined[index] !== 0x0a) continue;
      this.#parseLine(combined.subarray(start, index), handle);
      start = index + 1;
    }

    const remainder = combined.subarray(start);
    // An oversize/incomplete record is intentionally unobservable, never a
    // transport error. Forwarding is handled separately by pipe().
    const nextPartial = remainder.length > MAX_PARTIAL_BYTES ? Buffer.alloc(0) : Buffer.from(remainder);
    if (direction === 'server') this.#serverPartial = nextPartial;
    else this.#clientPartial = nextPartial;
  }

  #parseLine(line, handle) {
    if (line.length > MAX_PARTIAL_BYTES) return;
    try {
      handle(JSON.parse(line.toString('utf8')));
    } catch {
      // Non-JSON fixture traffic remains transparent and produces no telemetry.
    }
  }

  #trackRequest(message) {
    const family = REQUEST_FAMILIES.get(message?.method);
    if (!family) return;
    const id = rpcId(message.id);
    if (!id) return;
    const params = message.params;
    if (params !== undefined && (params === null || typeof params !== 'object' || Array.isArray(params))) return;

    const metadata = {};
    for (const key of ['threadId', 'turnId', 'itemId']) {
      const value = optionalString(params?.[key]);
      if (value) metadata[key] = value;
    }
    if (family === PERMISSIONS_REQUEST_FAMILY &&
        !['threadId', 'turnId', 'itemId'].every(key => metadata[key])) return;
    const idKey = JSON.stringify([id.type, id.value]);
    // JSON-RPC ids identify server->client requests independently of method
    // metadata. A delayed duplicate response is indistinguishable from a
    // response to a later request reusing the same typed id, so semantic
    // approval authority rejects id reuse for the lifetime of this observer.
    // The history is bounded; once saturated, stop admitting new requests
    // rather than evicting history and reopening a cross-correlation window.
    if (this.#requestIdHistorySaturated || this.#seenRequestIds.has(idKey)) return;
    if (this.#seenRequestIds.size >= MAX_SEEN_REQUEST_IDS) {
      this.#requestIdHistorySaturated = true;
      return;
    }
    this.#seenRequestIds.add(idKey);
    if (this.#pending.size >= MAX_PENDING) return;
    const key = pendingKey(family, id, metadata);
    const request = {
      rpcIdType: id.type,
      rpcId: id.value,
      family,
      idKey,
      ownerWaitAdmitted: false,
      timestampUtc: new Date().toISOString()
    };
    Object.assign(request, metadata);
    this.#pending.set(key, request);
    const turnKey = JSON.stringify([metadata.threadId, metadata.turnId]);
    const reviewer = this.#turnReviewers.has(turnKey) ? this.#turnReviewers.get(turnKey) : this.#reviewers.get(metadata.threadId);
    if (this.#sourceInstanceId &&
        (reviewer === 'user' || reviewer === 'auto_review') &&
        ['threadId', 'turnId', 'itemId'].every(name => metadata[name] && Buffer.byteLength(metadata[name], 'utf8') <= 128) &&
        Buffer.byteLength(id.value, 'utf8') <= 128) {
      const event = {
        schemaVersion: APPROVAL_REQUEST_SCHEMA_VERSION,
        timestampUtc: request.timestampUtc,
        source: 'codex_stdio_bridge',
        event: 'approval_requested',
        requestFamily: family,
        rpcIdType: id.type,
        rpcId: id.value,
        threadId: metadata.threadId,
        turnId: metadata.turnId,
        itemId: metadata.itemId,
        approvalsReviewer: reviewer,
        sourceInstanceId: this.#sourceInstanceId
      };
      if (this.#ownerCredits < MAX_PENDING) {
        request.ownerWaitAdmitted = true;
        this.#ownerCredits += 1;
        this.#emitOwnerEvent(event, false);
      }
    }
  }

  #observeReviewerClient(message) {
    if (!message || typeof message !== 'object' || Array.isArray(message) || typeof message.method !== 'string') return;
    if (!['thread/start', 'thread/resume', 'thread/fork', 'turn/start'].includes(message.method)) return;
    const id = rpcId(message.id); if (!id || !message.params || typeof message.params !== 'object' || Array.isArray(message.params)) return;
    const key = `${id.type}:${id.value}`;
    if (this.#reviewerRequests.size >= MAX_PENDING || this.#reviewerRequests.has(key)) return;
    const requestedReviewer = message.params.approvalsReviewer;
    this.#reviewerRequests.set(key, { method: message.method, threadId: optionalString(message.params.threadId),
      override: message.method === 'turn/start' && Object.hasOwn(message.params, 'approvalsReviewer')
        ? (requestedReviewer === 'user' || requestedReviewer === 'auto_review' ? requestedReviewer : null) : undefined });
  }

  #observeReviewerServer(message) {
    if (!message || typeof message !== 'object' || Array.isArray(message)) return;
    if (message.method === 'thread/settings/updated') {
      const threadId = optionalString(message.params?.threadId), reviewer = message.params?.settings?.approvalsReviewer ?? message.params?.approvalsReviewer;
      if (threadId) this.#rememberThreadReviewer(threadId, reviewer);
      return;
    }
    if (message.method === 'turn/completed') {
      const threadId = optionalString(message.params?.threadId), turnId = optionalString(message.params?.turn?.id);
      if (threadId && turnId) this.#turnReviewers.delete(JSON.stringify([threadId, turnId]));
      return;
    }
    if (Object.hasOwn(message, 'method') || !Object.hasOwn(message, 'id')) return;
    const id = rpcId(message.id); if (!id) return;
    const key = `${id.type}:${id.value}`, req = this.#reviewerRequests.get(key); if (!req) return;
    this.#reviewerRequests.delete(key);
    const result = message.result; if (!result || typeof result !== 'object' || Array.isArray(result)) return;
    const reviewer = result.approvalsReviewer;
    const threadId = req.method === 'turn/start' ? (req.threadId ?? optionalString(result.threadId)) : (optionalString(result.threadId) ?? optionalString(result.thread?.id));
    if (req.method !== 'turn/start' && threadId) this.#rememberThreadReviewer(threadId, reviewer);
    if (req.method === 'turn/start' && threadId) {
      const turnId = optionalString(result.turnId) ?? optionalString(result.turn?.id);
      const turnKey = JSON.stringify([threadId, turnId]);
      if (turnId && (this.#turnReviewers.has(turnKey) || this.#turnReviewers.size < MAX_REVIEWER_THREADS))
        this.#turnReviewers.set(turnKey, req.override !== undefined
          ? (req.override === 'user' || req.override === 'auto_review' ? req.override : null)
          : (reviewer === 'user' || reviewer === 'auto_review' ? reviewer : null));
    }
  }

  #rememberThreadReviewer(threadId, reviewer) {
    if (reviewer !== 'user' && reviewer !== 'auto_review') { this.#reviewers.delete(threadId); return; }
    if (this.#reviewers.has(threadId) || this.#reviewers.size < MAX_REVIEWER_THREADS) this.#reviewers.set(threadId, reviewer);
  }

  #trackCompletion(message) {
    if (!message || typeof message !== 'object' || Array.isArray(message) ||
        message.method !== 'turn/completed' || !message.params ||
        typeof message.params !== 'object' || Array.isArray(message.params)) return;
    const threadId = optionalString(message.params.threadId);
    const turn = message.params.turn;
    const turnId = optionalString(turn?.id);
    const status = optionalString(turn?.status);
    if (!threadId || !turn || typeof turn !== 'object' || Array.isArray(turn) ||
        !turnId || !status || !COMPLETION_STATUSES.has(status)) return;

    this.#emitFailOpen({
      schemaVersion: COMPLETION_SCHEMA_VERSION,
      timestampUtc: new Date().toISOString(),
      source: 'codex_stdio_bridge',
      event: 'turn_completed',
      threadId,
      turnId,
      status
    });
  }

  #resolveResponse(message) {
    if (!message || typeof message !== 'object' || Array.isArray(message) || Object.hasOwn(message, 'method')) return;
    const id = rpcId(message.id);
    if (!id) return;
    const requestEntries = [...this.#pending.entries()]
      .filter(([, request]) => request.rpcIdType === id.type && request.rpcId === id.value);
    const request = requestEntries.length === 1 ? requestEntries[0][1] : undefined;
    if (!request) return;
    if (request.family === PERMISSIONS_REQUEST_FAMILY) {
      this.#resolvePermissionsResponse(message, id, requestEntries[0][0], request);
      return;
    }
    const result = message.result;
    const decision = optionalString(result?.decision);
    if (!result || typeof result !== 'object' || Array.isArray(result) || !decision || !DECISIONS.has(decision)) return;

    this.#pending.delete(requestEntries[0][0]);
    const event = {
      schemaVersion: APPROVAL_SCHEMA_VERSION,
      timestampUtc: new Date().toISOString(),
      source: 'codex_stdio_bridge',
      event: 'approval_resolved',
      rpcIdType: request.rpcIdType,
      rpcId: request.rpcId,
      decision
    };
    if (request.ownerWaitAdmitted) event.sourceInstanceId = this.#sourceInstanceId;
    for (const key of ['threadId', 'turnId', 'itemId']) {
      if (request[key]) event[key] = request[key];
    }
    if (request.ownerWaitAdmitted) this.#emitOwnerEvent(event, true);
    else this.#emitFailOpen(event);
  }

  #resolvePermissionsResponse(message, id, pendingKeyValue, request) {
    const hasResult = Object.hasOwn(message, 'result');
    const hasError = Object.hasOwn(message, 'error');
    if (hasResult === hasError) return;

    // A unique JSON-RPC response terminally consumes this exact request even
    // when the peer reports an error or the result is malformed. Never persist
    // error/result payloads on those paths, but do release bounded owner credit.
    if (hasError) {
      this.#pending.delete(pendingKeyValue);
      if (request.ownerWaitAdmitted) this.#emitOwnerRelease();
      return;
    }

    const result = message.result;
    if (!result || typeof result !== 'object' || Array.isArray(result) ||
        Object.hasOwn(result, 'decision') || !result.permissions || typeof result.permissions !== 'object' ||
        Array.isArray(result.permissions)) {
      this.#pending.delete(pendingKeyValue);
      if (request.ownerWaitAdmitted) this.#emitOwnerRelease();
      return;
    }

    const scope = Object.hasOwn(result, 'scope') ? result.scope : 'turn';
    if (scope !== 'turn' && scope !== 'session') {
      this.#pending.delete(pendingKeyValue);
      if (request.ownerWaitAdmitted) this.#emitOwnerRelease();
      return;
    }
    if (Object.hasOwn(result, 'strictAutoReview') && typeof result.strictAutoReview !== 'boolean') {
      this.#pending.delete(pendingKeyValue);
      if (request.ownerWaitAdmitted) this.#emitOwnerRelease();
      return;
    }
    for (const key of ['threadId', 'turnId', 'itemId']) {
      if ((Object.hasOwn(message, key) && message[key] !== request[key]) ||
          (Object.hasOwn(result, key) && result[key] !== request[key])) {
        this.#pending.delete(pendingKeyValue);
        if (request.ownerWaitAdmitted) this.#emitOwnerRelease();
        return;
      }
    }

    this.#pending.delete(pendingKeyValue);
    const event = {
      schemaVersion: PERMISSIONS_APPROVAL_DIAGNOSTIC_SCHEMA_VERSION,
      source: 'codex_stdio_bridge',
      event: 'permissions_approval_observed',
      requestFamily: PERMISSIONS_REQUEST_FAMILY,
      rpcIdType: id.type,
      rpcId: id.value,
      threadId: request.threadId,
      turnId: request.turnId,
      itemId: request.itemId,
      requestObservedAtUtc: request.timestampUtc,
      responseObservedAtUtc: new Date().toISOString(),
      scope,
      ...(Object.hasOwn(result, 'strictAutoReview') ? { strictAutoReview: result.strictAutoReview } : {})
    };
    if (request.ownerWaitAdmitted) this.#emitOwnerEvent(event, true);
    else this.#emitFailOpen(event);
  }

  #emitFailOpen(event) {
    if (this.#telemetryQueue.length >= MAX_PENDING) return;
    this.#telemetryQueue.push({ event, releasesOwnerCredit: false });
    this.#drainSinkQueue();
  }

  #emitOwnerEvent(event, releasesOwnerCredit) {
    // At most MAX_PENDING credits exist, with at most one request record and
    // one resolution/release record per credit. This lane therefore needs <= 2N slots.
    this.#ownerQueue.push({ event, releasesOwnerCredit });
    this.#drainSinkQueue();
  }

  #emitOwnerRelease() {
    // Internal queue marker: releases one admitted owner credit in-order
    // without exposing malformed/error response payload or inventing telemetry.
    this.#ownerQueue.push({ event: null, releasesOwnerCredit: true });
    this.#drainSinkQueue();
  }

  #drainSinkQueue() {
    if (this.#sinkBusy || (this.#ownerQueue.length === 0 && this.#telemetryQueue.length === 0)) return;
    this.#sinkBusy = true;
    const entry = this.#ownerQueue.length > 0 ? this.#ownerQueue.shift() : this.#telemetryQueue.shift();
    let result;
    try { result = entry.event === null ? undefined : this.#sink(entry.event); } catch { result = undefined; }
    Promise.resolve(result).catch(() => {}).finally(() => {
      if (entry.releasesOwnerCredit) this.#ownerCredits -= 1;
      this.#sinkBusy = false;
      this.#drainSinkQueue();
    });
  }
}

export class NativeThreadStatusObserver {
  #sink; #journalSink; #classify; #receiptClock; #authorityDegraded; #diagnostics; #sourceInstanceId; #queue = []; #busy = false; #accepted = 0; #delivered = 0; #overflow = 0; #sinkFailures = 0; #partial = Buffer.alloc(0);
  constructor({ authoritySink = () => {}, journalSink, classificationResolver = () => undefined, receiptClock = () => new Date(), authorityDegraded = () => {}, diagnostics, sourceInstanceId } = {}) {
    this.#sink = authoritySink;
    this.#journalSink = journalSink;
    this.#classify = classificationResolver;
    this.#receiptClock = receiptClock;
    this.#authorityDegraded = authorityDegraded;
    this.#diagnostics = diagnostics;
    this.#sourceInstanceId = /^local:[0-9a-f]{32}$/.test(sourceInstanceId ?? '') ? sourceInstanceId : undefined;
  }
  observeServerChunk(chunk) {
    const input = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    const combined = this.#partial.length === 0 ? input : Buffer.concat([this.#partial, input]);
    let start = 0;
    for (let index = 0; index < combined.length; index += 1) {
      if (combined[index] !== 0x0a) continue;
      const line = combined.subarray(start, index).toString('utf8');
      if (Buffer.byteLength(line, 'utf8') > 0 && Buffer.byteLength(line, 'utf8') <= MAX_PARTIAL_BYTES) {
        try { this.#observeMessage(JSON.parse(line)); } catch { /* transparent */ }
      }
      start = index + 1;
    }
    const remainder = combined.subarray(start);
    this.#partial = remainder.length > MAX_PARTIAL_BYTES ? Buffer.alloc(0) : Buffer.from(remainder);
  }
  authorityHealth() {
    return { healthy: this.#overflow === 0 && this.#sinkFailures === 0, queued: this.#queue.length,
      accepted: this.#accepted, delivered: this.#delivered, overflow: this.#overflow, sinkFailures: this.#sinkFailures };
  }
  #observeMessage(message) {
    if (!message || typeof message !== 'object' || Array.isArray(message) || message.method !== 'thread/status/changed') return;
    const params = message.params;
    if (!params || typeof params !== 'object' || Array.isArray(params)) { this.#diagnostics?.recordStatusRejected('invalid_envelope'); return; }
    const threadId = optionalString(params.threadId);
    const statusObject = params.status;
    const status = optionalString(statusObject?.type);
    const hasFlags = statusObject && Object.hasOwn(statusObject, 'activeFlags');
    const flagsValue = statusObject?.activeFlags;
    const hasEmittedAt = Object.hasOwn(message, 'emittedAtMs');
    const emittedAtMs = message.emittedAtMs;
    const timestamp = hasEmittedAt && Number.isSafeInteger(emittedAtMs) && emittedAtMs >= 0 && emittedAtMs <= 8640000000000000
      ? new Date(emittedAtMs).toISOString() : hasEmittedAt ? undefined : this.#receiptTimestamp();
    const classification = optionalString(this.#classify(threadId));
    if (!threadId) { this.#diagnostics?.recordStatusRejected('invalid_thread_id'); return; }
    if (!THREAD_STATUSES.has(status)) { this.#diagnostics?.recordStatusRejected('invalid_status'); return; }
    if (hasFlags && status !== 'active') { this.#diagnostics?.recordStatusRejected('invalid_active_flags'); return; }
    if (status === 'active' && (!hasFlags || !Array.isArray(flagsValue) || flagsValue.length > 8)) { this.#diagnostics?.recordStatusRejected('invalid_active_flags'); return; }
    if (hasEmittedAt && (!Number.isSafeInteger(emittedAtMs) || emittedAtMs < 0 || emittedAtMs > 8640000000000000)) { this.#diagnostics?.recordStatusRejected('invalid_emitted_at_ms'); return; }
    if (!timestamp || Number.isNaN(Date.parse(timestamp))) { this.#diagnostics?.recordStatusRejected('invalid_emitted_at_ms'); return; }
    if (classification !== undefined && !THREAD_CLASSIFICATIONS.has(classification)) { this.#diagnostics?.recordStatusRejected('invalid_classification'); return; }
    const activeFlags = [];
    for (const flag of status === 'active' ? flagsValue : []) {
      if (typeof flag !== 'string' || !THREAD_FLAGS.has(flag) || activeFlags.includes(flag)) { this.#diagnostics?.recordStatusRejected('invalid_active_flags'); return; }
      activeFlags.push(flag);
    }
    activeFlags.sort((left, right) => left === 'waitingOnApproval' ? -1 : right === 'waitingOnApproval' ? 1 : 0);
    const event = { schemaVersion: THREAD_STATUS_SCHEMA_VERSION, source: 'codex_stdio_bridge', event: 'thread_status_changed',
      timestampUtc: timestamp, threadId, status, activeFlags };
    if (classification !== undefined) event.classification = classification;
    if (this.#journalSink && this.#sourceInstanceId) {
      const journalEvent = { ...event, sourceInstanceId: this.#sourceInstanceId };
      try { Promise.resolve(this.#journalSink(journalEvent)).catch(() => {}); } catch { /* optional journal path */ }
    }
    if (this.#queue.length >= MAX_AUTHORITY_QUEUE) {
      this.#overflow += 1;
      this.#diagnostics?.recordAuthority('overflow');
      try { this.#authorityDegraded('NATIVE_AUTHORITY_DEGRADED_OVERFLOW'); } catch { /* optional health path */ }
      return;
    }
    this.#queue.push(event); this.#accepted += 1; this.#diagnostics?.recordStatusAccepted(); this.#diagnostics?.recordAuthority('accepted'); this.#drain();
  }

  #receiptTimestamp() {
    try {
      const value = this.#receiptClock();
      return value instanceof Date && !Number.isNaN(value.getTime()) ? value.toISOString() : undefined;
    } catch { return undefined; }
  }
  #drain() {
    if (this.#busy || this.#queue.length === 0) return;
    this.#busy = true; const event = this.#queue.shift();
    try {
      Promise.resolve(this.#sink(event)).then(() => { this.#delivered += 1; this.#diagnostics?.recordAuthority('delivered'); }, () => {
        this.#sinkFailures += 1;
        this.#diagnostics?.recordAuthority('sinkFailures');
        try { this.#authorityDegraded('NATIVE_AUTHORITY_DEGRADED_SINK_FAILURE'); } catch { /* optional health path */ }
      })
        .finally(() => { this.#busy = false; this.#drain(); });
    } catch {
      this.#sinkFailures += 1;
      this.#diagnostics?.recordAuthority('sinkFailures');
      try { this.#authorityDegraded('NATIVE_AUTHORITY_DEGRADED_SINK_FAILURE'); } catch { /* optional health path */ }
      this.#busy = false; this.#drain();
    }
  }
}

/**
 * Reads only the proven App Server thread/started metadata envelope. This is
 * deliberately a separate stream from thread/status/changed: cwd is
 * presentation metadata and never participates in status ordering/health.
 */
export class NativeThreadMetadataObserver {
  #sink; #partial = Buffer.alloc(0);
  constructor({ metadataSink = () => {} } = {}) { this.#sink = metadataSink; }
  observeServerChunk(chunk) {
    const input = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    const combined = this.#partial.length === 0 ? input : Buffer.concat([this.#partial, input]);
    let start = 0;
    for (let index = 0; index < combined.length; index += 1) {
      if (combined[index] !== 0x0a) continue;
      const line = combined.subarray(start, index).toString('utf8');
      if (Buffer.byteLength(line, 'utf8') > 0 && Buffer.byteLength(line, 'utf8') <= MAX_PARTIAL_BYTES) {
        try { this.#observeMessage(JSON.parse(line)); } catch { /* transparent */ }
      }
      start = index + 1;
    }
    const remainder = combined.subarray(start);
    this.#partial = remainder.length > MAX_PARTIAL_BYTES ? Buffer.alloc(0) : Buffer.from(remainder);
  }
  #observeMessage(message) {
    if (message?.method !== 'thread/started' || !message.params?.thread ||
        typeof message.params.thread !== 'object' || Array.isArray(message.params.thread)) return;
    const thread = message.params.thread;
    const threadId = optionalString(thread.id);
    const workingDirectory = optionalString(thread.cwd);
    if (!threadId || !workingDirectory) return;
    const event = { schemaVersion: THREAD_METADATA_SCHEMA_VERSION, source: 'codex_stdio_bridge',
      event: 'thread_metadata_changed', threadId, workingDirectory };
    try {
      // Metadata is optional presentation data: consume both synchronous and
      // asynchronous delivery failures without creating a retry backlog.
      Promise.resolve(this.#sink(event)).catch(() => {});
    } catch { /* fail-open; never affect transparent transport or status */ }
  }
}

export function createSanitizedJsonlSink(filePath, { appendFile, makeDirectory } = {}) {
  if (typeof filePath !== 'string' || filePath.length === 0) return () => {};

  const append = appendFile ?? appendFileAsync;
  const mkdir = makeDirectory ?? mkdirAsync;
  let directoryReady;

  return (event) => {
    if (event.schemaVersion === THREAD_STATUS_SCHEMA_VERSION && event.event === 'thread_status_changed') {
      if (event.source !== 'codex_stdio_bridge' ||
          !/^local:[0-9a-f]{32}$/.test(event.sourceInstanceId ?? '') ||
          typeof event.timestampUtc !== 'string' || Number.isNaN(Date.parse(event.timestampUtc)) ||
          typeof event.threadId !== 'string' || Buffer.byteLength(event.threadId, 'utf8') > 1024 ||
          !THREAD_STATUSES.has(event.status) || !Array.isArray(event.activeFlags) || event.activeFlags.length > 8 ||
          event.activeFlags.some(flag => !THREAD_FLAGS.has(flag)) || new Set(event.activeFlags).size !== event.activeFlags.length ||
          (event.classification !== undefined && !THREAD_CLASSIFICATIONS.has(event.classification))) return;
      const sanitized = {
        schemaVersion: THREAD_STATUS_SCHEMA_VERSION,
        timestampUtc: event.timestampUtc,
        source: 'codex_stdio_bridge',
        event: 'thread_status_changed',
        sourceInstanceId: event.sourceInstanceId,
        threadId: event.threadId,
        status: event.status,
        activeFlags: [...event.activeFlags]
      };
      if (event.classification !== undefined) sanitized.classification = event.classification;
      const line = JSON.stringify(sanitized) + '\n';
      if (Buffer.byteLength(line, 'utf8') > MAX_PARTIAL_BYTES) return;
      if (!directoryReady) directoryReady = mkdir(path.dirname(filePath), { recursive: true });
      return Promise.resolve(directoryReady).then(() => append(filePath, line, { encoding: 'utf8' }));
    }
    const sanitized = {
      schemaVersion: event.schemaVersion,
      timestampUtc: event.timestampUtc,
      source: event.source,
      event: event.event,
      decision: event.decision,
      rpcIdType: event.rpcIdType,
      rpcId: event.rpcId
    };
    if ((event.schemaVersion === APPROVAL_REQUEST_SCHEMA_VERSION || event.schemaVersion === APPROVAL_SCHEMA_VERSION) &&
        /^local:[0-9a-f]{32}$/.test(event.sourceInstanceId ?? ''))
      sanitized.sourceInstanceId = event.sourceInstanceId;
    if (event.event === 'turn_completed') {
      sanitized.schemaVersion = COMPLETION_SCHEMA_VERSION;
      sanitized.threadId = event.threadId;
      sanitized.turnId = event.turnId;
      sanitized.status = event.status;
    }
    for (const key of ['threadId', 'turnId', 'itemId']) {
      if (event[key]) sanitized[key] = event[key];
    }
    if (event.schemaVersion === APPROVAL_REQUEST_SCHEMA_VERSION && event.event === 'approval_requested' &&
        (event.requestFamily === 'item/commandExecution' || event.requestFamily === 'item/fileChange' ||
         event.requestFamily === PERMISSIONS_REQUEST_FAMILY)) {
      sanitized.requestFamily = event.requestFamily;
      if (event.approvalsReviewer === 'user' || event.approvalsReviewer === 'auto_review') sanitized.approvalsReviewer = event.approvalsReviewer;
    }
    if (event.schemaVersion === PERMISSIONS_APPROVAL_DIAGNOSTIC_SCHEMA_VERSION &&
        event.event === 'permissions_approval_observed') {
      // Persist only schema-defined timing metadata. No raw permission profiles,
      // commands, prompts, paths, reasons, or arbitrary event properties.
      if (event.requestFamily === PERMISSIONS_REQUEST_FAMILY) {
        sanitized.requestFamily = PERMISSIONS_REQUEST_FAMILY;
      }
      for (const key of ['requestObservedAtUtc', 'responseObservedAtUtc']) {
        const value = event[key];
        if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value) &&
            !Number.isNaN(Date.parse(value))) sanitized[key] = value;
      }
      if (event.scope === 'turn' || event.scope === 'session') sanitized.scope = event.scope;
      if (typeof event.strictAutoReview === 'boolean') sanitized.strictAutoReview = event.strictAutoReview;
    }
    const line = JSON.stringify(sanitized) + '\n';
    if (Buffer.byteLength(line, 'utf8') > MAX_PARTIAL_BYTES) return;
    if (!directoryReady) {
      directoryReady = mkdir(path.dirname(filePath), { recursive: true });
    }
    return Promise.resolve(directoryReady).then(() => append(filePath, line, { encoding: 'utf8' }));
  };
}

/** Writes one bounded sanitized snapshot, replacing the previous snapshot atomically. */
export function createSanitizedDiagnosticsSink(filePath, { writeFile, rename, makeDirectory } = {}) {
  if (typeof filePath !== 'string' || filePath.length === 0) return () => {};
  const write = writeFile ?? writeFileAsync; const replace = rename ?? renameAsync; const mkdir = makeDirectory ?? mkdirAsync;
  let directoryReady; let busy = false;
  return snapshot => {
    if (busy) return;
    const line = JSON.stringify(snapshot) + '\n';
    if (Buffer.byteLength(line, 'utf8') > MAX_DIAGNOSTIC_FILE_BYTES) return;
    busy = true;
    const tempPath = filePath + '.tmp';
    if (!directoryReady) directoryReady = mkdir(path.dirname(filePath), { recursive: true });
    return Promise.resolve(directoryReady)
      .then(() => write(tempPath, line, { encoding: 'utf8' }))
      .then(() => replace(tempPath, filePath))
      .catch(() => unlinkAsync(tempPath).catch(() => {}))
      .finally(() => { busy = false; });
  };
}

/**
 * Connects already-created fake-process streams. Node's pipe() is the entire
 * transport path, so its native backpressure and close behavior remain intact.
 * Observers receive the same Buffer chunks but never write to either transport.
 */
export function connectTransparentBridge({ clientInput, clientOutput, childInput, childOutput, childStderr, stderrOutput, telemetrySink, authoritySink, diagnostics }) {
  const observer = new ApprovalObserver({ telemetrySink });
  const nativeObserver = new NativeThreadStatusObserver({ authoritySink, diagnostics });
  const metadataObserver = new NativeThreadMetadataObserver({ metadataSink: authoritySink });
  clientInput.on('data', (chunk) => observer.observeClientChunk(chunk));
  childOutput.on('data', (chunk) => { diagnostics?.observeServerChunk(chunk); observer.observeServerChunk(chunk); nativeObserver.observeServerChunk(chunk); metadataObserver.observeServerChunk(chunk); });
  clientInput.pipe(childInput);
  childOutput.pipe(clientOutput);
  if (childStderr && stderrOutput) childStderr.pipe(stderrOutput);
  observer.nativeStatusObserver = nativeObserver;
  observer.nativeThreadMetadataObserver = metadataObserver;
  return observer;
}
