// Deterministic OpenAPI -> TypeScript step of scripts/contracts/generate.sh.
//   node scripts/generate-ts.mjs <openapi.json> <outDir>
// Writes <outDir>/hosts-api.ts (paths, components, operations) and <outDir>/contract-version.ts (OpenAPI info.version).
// No network, no clock, no machine-specific paths in the output, so the same input always gives the same bytes.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import openapiTS, { astToString } from 'openapi-typescript';

const [input, outDir] = process.argv.slice(2);
if (!input || !outDir) {
  console.error('usage: generate-ts.mjs <openapi.json> <outDir>');
  process.exit(2);
}

const document = JSON.parse(readFileSync(resolve(input), 'utf8'));
const version = document?.info?.version;
if (typeof version !== 'string' || !/^\d+\.\d+\.\d+$/.test(version)) {
  console.error(`OpenAPI info.version must be a semver string, got ${JSON.stringify(version)}`);
  process.exit(1);
}

const banner = (source) =>
  `// GENERATED FILE - DO NOT EDIT.\n// Source: ${source}. Regenerate with scripts/contracts/generate.sh; CI fails when this file is stale.\n`;

const ast = await openapiTS(document, { alphabetize: true, immutable: false });
const types = `${banner('web/packages/contracts/openapi/hosts-api.json')}\n${astToString(ast)}`;
const versionModule =
  `${banner('info.version of web/packages/contracts/openapi/hosts-api.json')}\n` +
  `/** The API contract version this client was generated from (semver). The host reports its own in GET /meta/contracts. */\n` +
  `export const GENERATED_API_CONTRACT_VERSION = ${JSON.stringify(version)};\n`;

mkdirSync(resolve(outDir), { recursive: true });
writeFileSync(join(resolve(outDir), 'hosts-api.ts'), types.endsWith('\n') ? types : `${types}\n`);
writeFileSync(join(resolve(outDir), 'contract-version.ts'), versionModule);
