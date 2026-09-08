const mode = process.env.FAKE_CHILD_MODE ?? 'echo';

process.stderr.write('fake-child:' + mode + '\n');

if (mode === 'argv') {
  process.stdout.end(JSON.stringify(process.argv.slice(2)));
} else if (mode === 'echo') {
  process.stdin.pipe(process.stdout);
} else if (mode === 'exit') {
  process.stdin.resume();
  process.stdin.on('end', () => process.exit(Number(process.env.FAKE_CHILD_EXIT_CODE ?? '0')));
} else if (mode === 'signal') {
  process.stdin.resume();
  process.stdin.on('end', () => process.kill(process.pid, 'SIGTERM'));
} else if (mode === 'close-stdout') {
  process.stdin.resume();
  process.stdin.on('end', () => process.stdout.end());
} else if (mode === 'approval') {
  process.stdout.write(JSON.stringify({
    jsonrpc: '2.0',
    id: 1,
    method: 'item/commandExecution/requestApproval',
    params: {
      threadId: 'thread-fixture',
      turnId: 'turn-fixture',
      itemId: 'item-fixture',
      command: 'MUST NOT REACH SIDE CHANNEL'
    }
  }) + '\n');
  process.stdin.pipe(process.stdout);
} else if (mode === 'native') {
  process.stdout.write(JSON.stringify({
    jsonrpc: '2.0', method: 'thread/status/changed',
    params: { threadId: 'thread-native-fixture', status: { type: 'active', activeFlags: ['waitingOnApproval'] } },
    emittedAtMs: 1788854400000
  }) + '\n');
  process.stdin.pipe(process.stdout);
} else if (mode === 'metadata') {
  process.stdout.write(JSON.stringify({
    jsonrpc: '2.0', method: 'thread/started', params: {
      thread: { id: 'thread-metadata-fixture', cwd: 'G:\\Мой диск\\AgentLoop Exchange\\inbox', prompt: 'SECRET', model: 'PRIVATE' }
    }
  }) + '\n');
  process.stdin.pipe(process.stdout);
} else {
  process.stderr.write('fake-child: unsupported test mode\n');
  process.exit(64);
}
