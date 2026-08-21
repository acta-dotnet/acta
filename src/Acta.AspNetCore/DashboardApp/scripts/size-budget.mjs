// Fails the build when the dashboard bundle outgrows its budget.
//
// The built dist is embedded into Acta.AspNetCore at pack time, so bundle size is package size:
// every byte here is a byte in the NuGet package and in the assembly every host loads. That is also
// why the dashboard ships as ONE JS file and one CSS file, on purpose. Route-level code splitting is
// the usual answer to a 400 kB bundle, and it does not apply here:
//
//   * It cannot shrink the package. Split or not, every chunk is embedded in the assembly; the
//     download is off a localhost-served operator tool, not a CDN. Splitting moves bytes between
//     requests, it does not remove them.
//   * It adds a failure mode we would rather not own. The dist is embedded, so a redeploy swaps the
//     whole assembly under a session that is already open; the loaded shell then asks for chunk
//     names that no longer exist and navigation breaks until someone reloads. One file cannot
//     half-exist.
//
// So the lever that matters is total size, and this script is the ratchet on it. Run after
// `npm run build`; it reads dist/.vite/manifest.json rather than guessing hashed filenames.
//
// BASELINE is what the bundle measured when this gate went in. BUDGET is that plus ~10% headroom.
// When this fails, the fix is not to raise BUDGET reflexively: find what grew (`npx vite build
// --sourcemap` and attribute the map back to sources) and decide whether it belongs. If it does,
// re-measure, move both constants together, and say why in the commit. Growth should be a decision,
// not an accident.

import { gzipSync } from 'node:zlib';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

/** Bundle bytes measured on 2026-08-21, the build this gate was set from. */
const BASELINE = { raw: 450_804, gzip: 133_130 };

/** BASELINE + ~10%. Over this, the build fails. */
const BUDGET = { raw: 496_000, gzip: 146_000 };

const distDir = join(dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const manifestPath = join(distDir, '.vite', 'manifest.json');

let manifest;
try {
  manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
} catch {
  console.error(`No build manifest at ${manifestPath}. Run \`npm run build\` first.`);
  process.exit(1);
}

// Every emitted script and stylesheet, found through the manifest so a second chunk appearing would
// be counted rather than quietly missed. index.html is a manifest key, not an asset, so it is not
// part of the byte count; it is a few hundred bytes and it is not what anyone would regress.
const files = [...new Set(Object.values(manifest).flatMap((node) => [node.file, ...(node.css ?? [])]))]
  .filter((file) => file && file !== 'index.html')
  .sort();

if (files.length === 0) {
  console.error(`No assets listed in ${manifestPath}; the build output is not what this script expects.`);
  process.exit(1);
}

const measured = { raw: 0, gzip: 0 };
const rows = [];
for (const file of files) {
  const bytes = readFileSync(join(distDir, file));
  const gzip = gzipSync(bytes).length;
  measured.raw += bytes.length;
  measured.gzip += gzip;
  rows.push({ file, raw: bytes.length, gzip });
}

const kb = (bytes) => (bytes / 1000).toFixed(2).padStart(8) + ' kB';
const delta = (now, then) => {
  const percent = ((now - then) / then) * 100;
  return `${percent >= 0 ? '+' : ''}${percent.toFixed(1)}% vs baseline`;
};

console.log('Dashboard bundle (every emitted script and stylesheet):\n');
for (const row of rows) console.log(`  ${row.file.padEnd(34)} ${kb(row.raw)}  gzip ${kb(row.gzip)}`);
console.log(`  ${''.padEnd(34)} ${'--------'.padStart(8)}       ${'--------'.padStart(8)}`);
console.log(`  ${'total'.padEnd(34)} ${kb(measured.raw)}  gzip ${kb(measured.gzip)}`);
console.log(`  ${'budget'.padEnd(34)} ${kb(BUDGET.raw)}  gzip ${kb(BUDGET.gzip)}`);
console.log(`\n  raw  ${delta(measured.raw, BASELINE.raw)}\n  gzip ${delta(measured.gzip, BASELINE.gzip)}`);

const over = [];
if (measured.raw > BUDGET.raw) over.push(`raw ${kb(measured.raw).trim()} exceeds the ${kb(BUDGET.raw).trim()} budget`);
if (measured.gzip > BUDGET.gzip) over.push(`gzip ${kb(measured.gzip).trim()} exceeds the ${kb(BUDGET.gzip).trim()} budget`);

if (over.length > 0) {
  console.error(`\nDashboard bundle is over budget:\n  ${over.join('\n  ')}`);
  console.error('\nFind what grew before raising the constants in scripts/size-budget.mjs, and say why.');
  process.exit(1);
}

console.log('\nWithin budget.');
