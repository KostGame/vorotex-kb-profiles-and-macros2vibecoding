const SELECTORS = new Set(['--state-path', '--host', '--thread-id']);

export function parseUnreadProbeArgs(args) {
  if (!Array.isArray(args)) return undefined;
  const values = new Map();
  for (let index = 0; index < args.length; index += 1) {
    const selector = args[index];
    if (typeof selector !== 'string' || !SELECTORS.has(selector) || values.has(selector)) return undefined;
    const value = args[index + 1];
    if (typeof value !== 'string' || value.length === 0 || value.startsWith('--')) return undefined;
    values.set(selector, value);
    index += 1;
  }
  if (values.size !== SELECTORS.size) return undefined;
  return {
    statePath: values.get('--state-path'),
    host: values.get('--host'),
    threadId: values.get('--thread-id')
  };
}
