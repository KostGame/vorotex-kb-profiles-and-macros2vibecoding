#!/usr/bin/env node
import { probeStateFile } from './unread-probe.mjs';
import { parseUnreadProbeArgs } from './unread-probe-cli-args.mjs';

const args = process.argv.slice(2);
const parsed = parseUnreadProbeArgs(args);
if (!parsed) {
  process.stdout.write(JSON.stringify(await probeStateFile('', { threadId: '', host: '' })) + '\n');
  process.exitCode = 2;
} else {
  process.stdout.write(JSON.stringify(await probeStateFile(parsed.statePath, parsed)) + '\n');
}
