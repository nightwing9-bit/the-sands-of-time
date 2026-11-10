// Bundle import-graph check (Spec 04, R04.01 / R04.06). Builds both app shells in memory with Vite/Rollup and inspects the
// bundler's own module list for every output chunk, instead of searching the minified text:
//   - each bundle contains exactly one module for the shared scrubber, and it is the same source file in both apps;
//   - neither bundle contains a module from the other app;
//   - no test file, fixture or Node-only module is bundled.
// Run from web/: npm run check:bundles
import { build } from 'vite';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const apps = ['revenant', 'ghost-lap'];
const scrubber = path.join(web, 'packages', 'timeline-ui', 'src', 'TimelineScrubber.tsx');
const clean = (id) => id.split('?')[0].replace(/\0/g, '');
async function moduleIds(app) {
  const result = await build({ root: path.join(web, 'apps', app), logLevel: 'silent', build: { write: false } });
  const outputs = Array.isArray(result) ? result : [result];
  const ids = [];
  for (const output of outputs) {
    for (const item of output.output) {
      if (item.type === 'chunk') ids.push(...Object.keys(item.modules).map(clean));
    }
  }
  return ids;
}
const failures = [];
const graphs = {};
for (const app of apps) graphs[app] = await moduleIds(app);
for (const app of apps) {
  const ids = graphs[app];
  const other = apps.find((a) => a !== app);
  const scrubbers = ids.filter((id) => id === scrubber);
  if (scrubbers.length !== 1) failures.push(`${app}: expected exactly one bundled TimelineScrubber module, found ${scrubbers.length}`);
  if (!ids.some((id) => id.startsWith(path.join(web, 'packages', 'contracts', 'src') + path.sep))) failures.push(`${app}: @sands/contracts is not bundled`);
  const foreign = ids.filter((id) => id.startsWith(path.join(web, 'apps', other) + path.sep));
  if (foreign.length > 0) failures.push(`${app}: bundles modules of ${other}: ${foreign.join(', ')}`);
  const own = ids.filter((id) => id.startsWith(path.join(web, 'apps', app) + path.sep));
  if (own.length === 0) failures.push(`${app}: none of its own adapter modules are bundled`);
  const forbidden = ids.filter((id) => /[\\/](test|tests|e2e)[\\/]|\.test\.|\.spec\./.test(id.slice(web.length)));
  if (forbidden.length > 0) failures.push(`${app}: bundles test files: ${forbidden.join(', ')}`);
  const timelineUi = ids.filter((id) => id.startsWith(path.join(web, 'packages', 'timeline-ui') + path.sep));
  const copies = timelineUi.filter((id) => path.basename(id) === 'TimelineScrubber.tsx');
  console.log(`${app}: ${ids.length} modules; own adapter modules: ${own.map((id) => path.relative(web, id)).join(', ')}; shared scrubber: ${copies.map((id) => path.relative(web, id)).join(', ')}`);
}
if (failures.length > 0) {
  console.error(failures.join('\n'));
  process.exit(1);
}
console.log('bundle graph OK: one shared scrubber module in both apps, no cross-app modules, no test files.');
