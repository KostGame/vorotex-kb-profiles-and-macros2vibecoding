import fs from 'node:fs';
import { diagnose } from '../src/r5-diagnostic.mjs';
const input = process.argv[2];
if (!input) process.exit(2);
const events = fs.readFileSync(input, 'utf8').split(/\r?\n/).filter(Boolean).map(line => JSON.parse(line));
const result = diagnose(events);
const classifications = result.cases.map(c => c.result);
console.log(classifications.length === 1 ? classifications[0] : classifications.length ? 'AMBIGUOUS_CORRELATION' : 'NO_COMPLETION');
