import { diagnoseTransient, persistSanitized } from './r5-live-runner-core.mjs';

export async function arm(ctx) {
  if (!ctx.hooksHealthy()) return { status: 'BLOCKED', reason: 'hook health changed before ARM' };
  if (ctx.machineChanged()) return { status: 'BLOCKED', reason: 'machine environment changed' };
  if (ctx.permanent.running) { await ctx.stopExact(ctx.permanent); }
  ctx.canary = await ctx.startTray();
  ctx.state = 'TRAY_STARTED';
  await ctx.enableLogging(); ctx.state = 'LOGGING_ENABLED';
  await ctx.validate(); ctx.state = 'VALIDATED';
  await ctx.enableBridge(); ctx.state = 'BRIDGE_ENABLED';
  ctx.desktop = await ctx.launchDesktop(); ctx.state = 'DESKTOP_STARTED';
  const route = await ctx.waitForRoute();
  if (!route.adapter || !route.child) throw new Error('bounded Desktop route evidence missing');
  ctx.state = 'ARMED';
  return { status: 'PASS', adapter: true, child: true };
}

export async function verifyDisable(ctx) {
  if (!ctx.closed()) return { status: 'BLOCKED', next: 'CLOSE_CODEX_COMPLETELY_AND_RETRY_VERIFY_DISABLE' };
  const delta = ctx.readDelta();
  const events = ctx.extract(delta);
  const diagnostic = diagnoseTransient(events);
  persistSanitized(ctx.chronologyPath, events);
  const cleanup = await cleanupState(ctx);
  return { ...diagnostic, ...cleanup, status: cleanup.status === 'PASS' ? 'PASS' : 'BLOCKED' };
}

export async function rollback(ctx) {
  const failures = [];
  for (const process of [ctx.desktop, ctx.adapter, ctx.child, ctx.canary]) {
    if (process) { try { await ctx.stopExact(process); } catch (e) { failures.push(e); } }
  }
  try { await ctx.disableBridge(); } catch (e) { failures.push(e); }
  try { await ctx.restoreEnv(); } catch (e) { failures.push(e); }
  try { await ctx.restoreLogging(); } catch (e) { failures.push(e); }
  try { await ctx.restoreTray(); } catch (e) { failures.push(e); }
  try { await ctx.restoreStock(); } catch (e) { failures.push(e); }
  return { status: failures.length ? 'BLOCKED' : 'PASS', rollback: failures.length ? 'FAIL' : 'PASS', failures: failures.length };
}

async function cleanupState(ctx) {
  const results = [];
  for (const action of [ctx.disableBridge, ctx.restoreEnv, ctx.restoreLogging, ctx.stopCanary, ctx.restoreTray, ctx.restoreStock]) {
    try { await action(); results.push(true); } catch { results.push(false); }
  }
  return { status: results.every(Boolean) ? 'PASS' : 'BLOCKED', cleanup: results };
}
