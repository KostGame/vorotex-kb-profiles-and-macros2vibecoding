#!/usr/bin/env node
import { probeStateFile } from './unread-probe.mjs';

const args = process.argv.slice(2);
function valueFor(name) {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
}

const filePath = valueFor('--state-path');
const threadId = valueFor('--thread-id');
const host = valueFor('--host');
const allowed = new Set(['--state-path', '--thread-id', '--host']);
if (!filePath || !threadId || !host || args.some((arg, index) => arg.startsWith('--') && (!allowed.has(arg) || !args[index + 1]))) {
  process.stdout.write(JSON.stringify(await probeStateFile('', { threadId: threadId ?? '', host: host ?? '' })) + '\n');
  process.exitCode = 2;
} else {
  process.stdout.write(JSON.stringify(await probeStateFile(filePath, { threadId, host })) + '\n');
}
