import { fileURLToPath } from 'node:url';
import path from 'node:path';
import {
  ApprovalObserver,
  NativeThreadStatusObserver,
  NativeThreadMetadataObserver,
  createSanitizedJsonlSink,
  BridgeDiagnostics,
  createSanitizedDiagnosticsSink
} from './bridge-core.mjs';
import { createNamedPipeAuthoritySink } from './runtime-process-authority.mjs';
import { runTransparentWrapper } from './transparent-wrapper.mjs';

export const APPROVAL_SINK_PATH_ENV = 'CODEX_BRIDGE_APPROVAL_SINK_PATH';
export const DIAGNOSTICS_SINK_PATH_ENV = 'CODEX_BRIDGE_DIAGNOSTICS_SINK_PATH';
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
 * side-channel sink. The observer recognizes the proven live JSON-RPC shape;
 * it does not recognize the superseded fixture-only response method.
 */
export async function runApprovalWrapper(options = {}) {
  const {
    env = process.env,
    stdin = process.stdin,
    stderr = process.stderr,
    telemetrySink,
    authoritySink,
    classificationResolver,
    receiptClock,
    authorityConnector,
    authorityPipePath
  } = options;

  let sinkPath;
  let diagnosticsPath;
  try {
    sinkPath = optionalAbsoluteSinkPath(env[APPROVAL_SINK_PATH_ENV]);
    diagnosticsPath = optionalAbsoluteSinkPath(env[DIAGNOSTICS_SINK_PATH_ENV]);
  } catch {
    pauseInput(stdin);
    writeDiagnostic(stderr, 'codex bridge: invalid approval sink configuration');
    return APPROVAL_CONFIG_ERROR_EXIT_CODE;
  }

  let runtimeBoundary;
  const diagnostics = options.diagnostics ?? new BridgeDiagnostics({
    sink: createSanitizedDiagnosticsSink(diagnosticsPath)
  });
  try {
    runtimeBoundary = authoritySink ? undefined : createNamedPipeAuthoritySink({
      pipePath: authorityPipePath,
      connectPipe: authorityConnector,
      diagnostics
    });
  } catch {
    // Authority is an optional side channel; its configuration must never
    // prevent byte-transparent Codex transport.
    runtimeBoundary = undefined;
  }

  const observer = new ApprovalObserver({
    telemetrySink: telemetrySink ?? createSanitizedJsonlSink(sinkPath)
  });
  const nativeStatusObserver = new NativeThreadStatusObserver({
    authoritySink: authoritySink ?? runtimeBoundary?.sink,
    classificationResolver, receiptClock,
    authorityDegraded: reason => runtimeBoundary?.markDegraded(reason),
    diagnostics
  });
  const nativeThreadMetadataObserver = new NativeThreadMetadataObserver({
    metadataSink: authoritySink ?? runtimeBoundary?.sink
  });

  try {
    return await runTransparentWrapper({
      ...options,
      env, stdin, stderr,
      wrapperPath: options.wrapperPath ?? APPROVAL_WRAPPER_PATH,
      onClientChunk: (chunk) => observer.observeClientChunk(chunk),
      onServerChunk: (chunk) => {
        diagnostics.observeServerChunk(chunk);
        observer.observeServerChunk(chunk);
        nativeStatusObserver.observeServerChunk(chunk);
        nativeThreadMetadataObserver.observeServerChunk(chunk);
      }
    });
  } finally {
    await runtimeBoundary?.close();
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(APPROVAL_WRAPPER_PATH)) {
  process.exitCode = await runApprovalWrapper();
}
