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
} else {
  process.stderr.write('fake-child: unsupported test mode\n');
  process.exit(64);
}
