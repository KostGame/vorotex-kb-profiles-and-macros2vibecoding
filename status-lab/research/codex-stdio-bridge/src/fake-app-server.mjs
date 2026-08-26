// Deterministic fake child only. It echoes stdin to stdout and keeps stderr separate.
process.stdin.pipe(process.stdout);
process.stderr.write('fake-app-server: started\n');
