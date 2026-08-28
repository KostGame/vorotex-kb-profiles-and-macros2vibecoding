import { createHash } from 'node:crypto';
import { createReadStream, realpathSync, statSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

export const CHILD_PATH_ENV = 'CODEX_BRIDGE_CHILD_PATH';
export const CHILD_SHA256_ENV = 'CODEX_BRIDGE_CHILD_SHA256';
export const UNSUPPORTED_ARGS_ENV = 'CODEX_BRIDGE_CHILD_ARGS';
export const CONFIG_ERROR_EXIT_CODE = 2;
export const CHILD_FAILURE_EXIT_CODE = 1;

const WRAPPER_PATH = fileURLToPath(import.meta.url);
const SHA256_PATTERN = /^[0-9a-f]{64}$/i;

export class WrapperConfigurationError extends Error {}

function canonicalPath(value) {
  const resolved = path.resolve(value);
  return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}

function samePath(left, right) {
  return canonicalPath(left) === canonicalPath(right);
}

function requireAbsolutePath(value) {
  if (typeof value !== 'string' || value.length === 0 || !path.isAbsolute(value)) {
    throw new WrapperConfigurationError(CHILD_PATH_ENV + ' must be an absolute path');
  }
  return value;
}

function requireRegularFile(value) {
  try {
    if (!statSync(value).isFile()) throw new Error('not a regular file');
  } catch {
    throw new WrapperConfigurationError('configured child file is unavailable');
  }
}

function realPath(value, label) {
  try {
    return realpathSync(value);
  } catch {
    throw new WrapperConfigurationError(label + ' cannot be resolved');
  }
}

function sha256File(filePath) {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256');
    const input = createReadStream(filePath);
    input.on('error', reject);
    input.on('data', (chunk) => hash.update(chunk));
    input.on('end', () => resolve(hash.digest('hex')));
  });
}

export async function resolveWrapperConfig({ env = process.env, wrapperPath = WRAPPER_PATH } = {}) {
  if (typeof env[UNSUPPORTED_ARGS_ENV] === 'string' && env[UNSUPPORTED_ARGS_ENV].length > 0) {
    throw new WrapperConfigurationError(UNSUPPORTED_ARGS_ENV + ' is unsupported');
  }

  const configuredPath = requireAbsolutePath(env[CHILD_PATH_ENV]);
  const wrapperRealPath = realPath(wrapperPath, 'wrapper');
  const childRealPath = realPath(configuredPath, 'configured child');
  requireRegularFile(childRealPath);

  if (samePath(wrapperRealPath, childRealPath)) {
    throw new WrapperConfigurationError('configured child resolves to the wrapper');
  }

  const expectedSha256 = env[CHILD_SHA256_ENV];
  if (expectedSha256 !== undefined) {
    if (typeof expectedSha256 !== 'string' || !SHA256_PATTERN.test(expectedSha256)) {
      throw new WrapperConfigurationError(CHILD_SHA256_ENV + ' must be a SHA-256 hex pin');
    }
    const actualSha256 = await sha256File(childRealPath);
    if (actualSha256.toLowerCase() !== expectedSha256.toLowerCase()) {
      throw new WrapperConfigurationError('configured child SHA-256 does not match');
    }
  }

  return { childPath: childRealPath };
}

export function normalizeChildExitCode(code, signal) {
  return Number.isInteger(code) ? code : CHILD_FAILURE_EXIT_CODE;
}

function pauseInput(stream) {
  if (stream && typeof stream.pause === 'function') stream.pause();
}

function writeDiagnostic(stream, message) {
  try {
    stream.write(message + '\n');
  } catch {
    // A closed Desktop-side stderr must not turn a bounded failure into a hang.
  }
}

export async function runTransparentWrapper({
  argv = process.argv.slice(2),
  env = process.env,
  wrapperPath = WRAPPER_PATH,
  stdin = process.stdin,
  stdout = process.stdout,
  stderr = process.stderr,
  spawnProcess = spawn
} = {}) {
  let config;
  try {
    config = await resolveWrapperConfig({ env, wrapperPath });
  } catch {
    pauseInput(stdin);
    writeDiagnostic(stderr, 'codex bridge: invalid child configuration');
    return CONFIG_ERROR_EXIT_CODE;
  }

  let child;
  try {
    child = spawnProcess(config.childPath, argv, {
      stdio: ['pipe', 'pipe', 'pipe'],
      env,
      windowsHide: true
    });
  } catch {
    pauseInput(stdin);
    writeDiagnostic(stderr, 'codex bridge: child spawn failed');
    return CHILD_FAILURE_EXIT_CODE;
  }

  let settled = false;
  let resolveRun;
  const completed = new Promise((resolve) => { resolveRun = resolve; });
  const finish = (code) => {
    if (settled) return;
    settled = true;
    resolveRun(code);
  };

  child.once('error', () => {
    if (typeof stdin.unpipe === 'function') stdin.unpipe(child.stdin);
    pauseInput(stdin);
    child.stdin?.destroy();
    child.stdout?.destroy();
    child.stderr?.destroy();
    writeDiagnostic(stderr, 'codex bridge: child spawn failed');
    finish(CHILD_FAILURE_EXIT_CODE);
  });
  child.once('close', (code, signal) => finish(normalizeChildExitCode(code, signal)));

  stdin.pipe(child.stdin);
  child.stdout.pipe(stdout);
  child.stderr.pipe(stderr);
  return completed;
}
