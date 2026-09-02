import fs from 'node:fs';
import { diagnose } from '../src/r5-diagnostic.mjs';
const input = process.argv[2];
if (!input) process.exit(2);
// '-' is the privacy-preserving owner-runner path: transient data is sent over
// stdin and is never written as a raw journal slice on disk.
const payload = input === '-' ? fs.readFileSync(0, 'utf8') : fs.readFileSync(input, 'utf8');
const events = payload.split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
const result = diagnose(events);
const classifications = result.cases.map(c => c.result);
const classification = classifications.length === 1 ? classifications[0] : classifications.length ? 'AMBIGUOUS_CORRELATION' : 'NO_COMPLETION';
if (process.argv.includes('--json')) console.log(JSON.stringify({ classification, evidence: result.evidence }));
else console.log(classification);
