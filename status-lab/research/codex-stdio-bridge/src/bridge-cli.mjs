import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { connectTransparentBridge } from './bridge-core.mjs';

// This entry point intentionally hard-codes the deterministic fake child.
// It is not a launcher for Codex Desktop or a real codex app-server.
const fakeChild = fileURLToPath(new URL('./fake-app-server.mjs', import.meta.url));
const child = spawn(process.execPath, [fakeChild], { stdio: ['pipe', 'pipe', 'pipe'] });
connectTransparentBridge({
  clientInput: process.stdin,
  clientOutput: process.stdout,
  childInput: child.stdin,
  childOutput: child.stdout,
  childStderr: child.stderr,
  stderrOutput: process.stderr,
  telemetrySink: () => {}
});
child.on('exit', (code, signal) => process.exitCode = code ?? (signal ? 1 : 0));
