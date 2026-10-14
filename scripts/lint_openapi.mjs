import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';

const toolsDirectory = process.env.CIVICOPS_SPECTRAL_TOOLS_DIR || resolve(process.env.TMPDIR || tmpdir(), 'openapi-lint-tools');
const require = createRequire(resolve(toolsDirectory, 'package.json'));
const { Spectral } = require('@stoplight/spectral-core');
const { oas } = require('@stoplight/spectral-rulesets');
const spectral = new Spectral();
spectral.setRuleset({ extends: [[oas, 'recommended']], rules: {
  // All ten portable extension schemas remain intentionally published for clients.
  'oas3-unused-component': 'off',
} });
const filename = process.argv[2] || 'docs/api/openapi.json';
const document = JSON.parse(readFileSync(filename, 'utf8'));
const results = await spectral.run(document);
for (const result of results) {
  process.stderr.write(`${result.code} ${result.path.join('.')}: ${result.message}\n`);
}
if (results.some(result => result.severity <= 1)) process.exitCode = 1;
else process.stdout.write(`Spectral passed: ${Object.keys(document.paths).length} paths / ${Object.values(document.paths).reduce((sum, path) => sum + Object.keys(path).length, 0)} operations; ${results.length} findings.\n`);
