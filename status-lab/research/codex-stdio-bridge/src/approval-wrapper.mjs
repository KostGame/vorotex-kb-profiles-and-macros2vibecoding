import { fileURLToPath } from 'node:url';
import path from 'node:path';
import {
  ApprovalObserver,
  createSanitizedJsonlSink
} from './bridge-core.mjs';
import { runTransparentWrapper } from './transparent-wrapper.mjs';

export const APPROVAL_SINK_PATH_ENV = 'CODEX_BRIDGE_APPROVAL_SINK_PATH';
export const APPROVAL_WRAPPER_PATH = fileURLToPath(import.meta.url);
export const APPROVAL_CONFIG_ERROR_EXIT_CODE = 2;

function writeDiagnostic(stream, message) {
  try {
    stream.write(message + '\n');
  } catch {
    // A closed Desktop-side stderr must not turn an optional observer failure
    // into a transport failure.
  }
}

function pauseInput(stream) {
  if (stream && typeof stream.pause === 'function') stream.pause();
}

function optionalAbsoluteSinkPath(value) {
  if (value === undefined || value === '') return undefined;
  if (typeof value !== 'string' || !path.isAbsolute(value)) {
    throw new Error('approval sink path must be absolute');
  }
  return value;
}

/**
 * Opt-in Phase C entry point. The transparent wrapper remains the transport;
 * this module only installs bounded data listeners and an optional sanitized
 * side-channel sink. The protocol method/decision shape is fixture-only until
 * the separately authorized owner canary proves it against live Desktop.
 */
export async function runApprovalWrapper(options = {}) {
  const {
    env = process.env,
    stdin = process.stdin,
    stderr = process.stderr,
    telemetrySink
  } = options;

  let sinkPath;
  try {
    sinkPath = optionalAbsoluteSinkPath(env[APPROVAL_SINK_PATH_ENV]);
  } catch {
    pauseInput(stdin);
    writeDiagnostic(stderr, 'codex bridge: invalid approval sink configuration');
    return APPROVAL_CONFIG_ERROR_EXIT_CODE;
  }

  const observer = new ApprovalObserver({
    telemetrySink: telemetrySink ?? createSanitizedJsonlSink(sinkPath)
  });

  return runTransparentWrapper({
    ...options,
    env,
    stdin,
    stderr,
    wrapperPath: options.wrapperPath ?? APPROVAL_WRAPPER_PATH,
    onClientChunk: (chunk) => observer.observeClientChunk(chunk),
    onServerChunk: (chunk) => observer.observeServerChunk(chunk)
  });
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(APPROVAL_WRAPPER_PATH)) {
  process.exitCode = await runApprovalWrapper();
}
