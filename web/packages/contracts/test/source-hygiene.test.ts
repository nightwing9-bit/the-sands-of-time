// Validates: forbidden-change rules for task 4.1 (no promiscuous `any`, no hand-written copy of a generated DTO)
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { test } from 'node:test';

const srcDir = new URL('../src/', import.meta.url);
const sources = readdirSync(srcDir)
  .filter((name) => name.endsWith('.ts'))
  .map((name) => ({ name, text: readFileSync(new URL(name, srcDir), 'utf8') }));

/** Removes comments and string literals so only code is scanned. */
function codeOnly(text: string): string {
  return text
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/\/\/[^\n]*/g, '')
    .replace(/'(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*"|`(?:[^`\\]|\\.)*`/g, "''");
}

test('hand-written package source never uses the any type', () => {
  assert.ok(sources.length >= 10);
  for (const { name, text } of sources) {
    const hits = [...codeOnly(text).matchAll(/(?::|<|\bas|\|)\s*any\b|\bany\s*\[\]|<any[,>]/g)];
    assert.equal(hits.length, 0, `${name} uses any`);
  }
});

test('the only generated files are under src/generated and carry the do-not-edit banner', () => {
  const generated = readdirSync(new URL('generated/', srcDir)).sort();
  assert.deepEqual(generated, ['contract-version.ts', 'hosts-api.ts']);
  for (const name of generated) {
    assert.match(readFileSync(new URL(`generated/${name}`, srcDir), 'utf8'), /^\/\/ GENERATED FILE - DO NOT EDIT\./);
  }
});

test('hand-written code derives wire DTOs from the generated types instead of redeclaring them', () => {
  const generatedNames = ['StaffSessionResponse', 'GuestSessionResponse', 'ActorResponse', 'StaffLoginRequest', 'MfaEnrollment', 'MfaVerifiedResponse', 'ContractVersions', 'ApiProblem'];
  for (const { name, text } of sources) {
    for (const dto of generatedNames) {
      const redeclared = new RegExp(`\\b(?:interface|class)\\s+${dto}\\b|\\btype\\s+${dto}\\s*=\\s*\\{`).test(codeOnly(text));
      assert.equal(redeclared, false, `${name} redeclares ${dto}`);
    }
  }
});
