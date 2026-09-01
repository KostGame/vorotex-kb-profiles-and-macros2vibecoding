import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'live');
const runner = path.join(root, 'K15-CODEX-DONE-R5-LIVE.ps1');
const diagnose = path.join(root, 'r5-live-diagnose.mjs');
const ps = process.env.PWSH ?? 'pwsh.exe';
const hook = (event, extra = {}) => ({ source: 'codex_hook', event, timestampUtc: '2026-01-01T00:00:00Z', sessionId: 'S', turnId: 'T', ...extra });
const completion = () => ({ schemaVersion: 'k15-codex-completion/v1', source: 'codex_stdio_bridge', event: 'turn_completed', timestampUtc: '2026-01-01T00:00:02Z', threadId: 'S', turnId: 'T', status: 'completed' });
const done = (reason = 'codex_turn_completed') => ({ source: 'state_normalizer', event: 'session_state_changed', plane: 'per_session', sessionId: 'S', previous: 'RUNNING', current: 'DONE_PENDING_ATTENTION', reason, sourceTimestampUtc: '2026-01-01T00:00:03Z', isRehydrated: false, correlation: { threadId: 'S', turnId: 'T', rpcIdType: '', rpcId: '' } });

test('live package has one launcher per phase and no live execution in tests', () => {
  for (const file of ['000-RUN-R5-PREPARE.cmd','010-RUN-R5-ARM.cmd','020-RUN-R5-VERIFY-DISABLE.cmd','099-RUN-R5-ROLLBACK.cmd']) assert.equal(fs.existsSync(path.join(root, file)), true);
  assert.match(fs.readFileSync(path.join(root, 'README.md'), 'utf8'), /never run the live canary/i);
  assert.doesNotMatch(fs.readFileSync(runner, 'utf8'), /Start-Process\s+\$?m?\.childPath/i);
});

test('diagnostic CLI accepts exact no-Stop production completion', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'r5-live-test-')); const file = path.join(dir, 'events.jsonl');
  try {
    fs.writeFileSync(file, [hook('UserPromptSubmit'), completion(), done()].map(JSON.stringify).join('\n') + '\n');
    const result = spawnSync(process.execPath, [diagnose, file], { encoding: 'utf8' });
    assert.equal(result.status, 0); assert.equal(result.stdout.trim(), 'NO_STOP_LIVE_DONE_ACCEPTED');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('diagnostic CLI preserves Stop-authored classification and ignores forbidden fields', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'r5-live-test-')); const file = path.join(dir, 'events.jsonl');
  try {
    const stop = { ...hook('Stop'), prompt: 'secret', command: 'secret' };
    fs.writeFileSync(file, [hook('UserPromptSubmit'), stop, completion(), done('codex_stop')].map(JSON.stringify).join('\n') + '\n');
    const result = spawnSync(process.execPath, [diagnose, file], { encoding: 'utf8' });
    assert.equal(result.stdout.trim(), 'STOP_AUTHORED_DONE');
    assert.doesNotMatch(fs.readFileSync(runner, 'utf8'), /tool_input|rawProtocol|chatContent/); // runner allowlist never writes these fields
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('runner is explicit about all four modes and protected surfaces', () => {
  const source = fs.readFileSync(runner, 'utf8');
  for (const mode of ['PREPARE','ARM','VERIFY_DISABLE','ROLLBACK']) assert.match(source, new RegExp(`Mode -eq '${mode}'`));
  for (const forbidden of ['Machine','hooks.json']) assert.match(source, new RegExp(forbidden));
  assert.match(fs.readFileSync(path.join(root, 'README.md'), 'utf8'), /WindowsApps/);
  assert.match(source, /1048576/); assert.match(source, /r5-diagnostic|r5-live-diagnose/);
});
