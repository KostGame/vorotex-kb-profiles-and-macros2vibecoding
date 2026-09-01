import { diagnose, persistEvidence } from './r5-diagnostic.mjs';

export const JOURNAL_LIMIT = 1024 * 1024;
export const ALLOWED_ENV = ['CODEX_CLI_PATH','CODEX_BRIDGE_NODE_PATH','CODEX_BRIDGE_WRAPPER_PATH','CODEX_BRIDGE_CHILD_PATH','CODEX_BRIDGE_CHILD_SHA256','CODEX_BRIDGE_APPROVAL_SINK_PATH'];
export function boundedDelta(bytes, offset, limit = JOURNAL_LIMIT) {
  if (!Number.isInteger(offset) || offset < 0 || offset > bytes.length) throw new Error('journal rotated below recorded offset');
  const delta = bytes.subarray(offset);
  if (delta.length > limit) throw new Error('journal delta exceeds limit');
  return delta.toString('utf8');
}
export function extractTransient(lines) {
  const out = [];
  for (const line of lines.split(/\r?\n/)) {
    if (!line.trim()) continue;
    try {
      const event = JSON.parse(line);
      const selected = (event.source === 'codex_hook' && ['UserPromptSubmit','Stop','SessionEnd'].includes(event.event)) ||
        (event.source === 'codex_stdio_bridge' && event.event === 'turn_completed') ||
        (event.source === 'state_normalizer' && event.event === 'session_state_changed');
      if (selected) out.push(event);
    } catch { /* truncated/malformed lines are ignored */ }
  }
  return out;
}
export function diagnoseTransient(events) {
  const result = diagnose(events);
  return { ...result, classification: result.cases.length === 1 ? result.cases[0].result : result.cases.length ? 'AMBIGUOUS_CORRELATION' : 'NO_COMPLETION' };
}
export function restoreEnvironment(snapshot, target = new Map()) {
  for (const name of ALLOWED_ENV) {
    const item = snapshot[name] ?? { present: false, value: undefined };
    if (item.present) target.set(name, item.value);
    else target.delete(name);
  }
  return target;
}
export function persistSanitized(file, events) { persistEvidence(file, diagnoseTransient(events).evidence); }
export function chooseDesktopRoute(packageFamily) { if (!packageFamily) throw new Error('package identity required'); return `shell:AppsFolder\\${packageFamily}!App`; }
export function restoreMarker(existed, marker, fsApi) { if (existed) fsApi.write(marker); else fsApi.remove(marker); return fsApi.exists(marker) === existed; }
export function rollbackOutcome({ disableOk, restoreOk, trayOk, loggingOk, stockOk }) { const ok = disableOk && restoreOk && trayOk && loggingOk && stockOk; return { status: ok ? 'PASS' : 'BLOCKED', rollback: ok ? 'PASS' : 'FAIL' }; }
