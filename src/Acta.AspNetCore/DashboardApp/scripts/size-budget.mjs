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
// `npm run build`; it reads dist/.vite/manifest.json rather than guessing hashed filenames. It
// checks two things: that the build is still one JS file and one CSS file, and that they fit the
// budget below. The shape check is what keeps the reasoning above from being advice.
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
} catch (error) {
  // Two different problems with two different fixes: nothing built yet, versus a manifest that is
  // there but wrong. Saying "no manifest" for the second sends the reader hunting the wrong thing.
  if (error.code === 'ENOENT') {
    console.error(`No build manifest at ${manifestPath}. Run \`npm run build\` first.`);
  } else {
    console.error(`Could not read the build manifest at ${manifestPath}: ${error.message}`);
  }
  process.exit(1);
}

// Every emitted asset, found through the manifest so a new chunk is counted rather than quietly
// missed. The manifest's keys are source ids (index.html among them); the paths that matter are the
// `file` and `css` values, and for the html entry `file` is already the emitted JS, not the page.
const files = [...new Set(Object.values(manifest).flatMap((node) => [node.file, ...(node.css ?? [])]))]
  .filter(Boolean)
  .sort();

if (files.length === 0) {
  console.error(`No assets listed in ${manifestPath}; the build output is not what this script expects.`);
  process.exit(1);
}

const measured = { raw: 0, gzip: 0 };
const rows = [];
for (const file of files) {
  const bytes = readFileSync(join(distDir, file));
  // zlib's default level, and that is the number the budget is set from. Vite's build log compresses
  // at a different level, so its gzip figures read a little lower; compare against this script, not
  // against the log.
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

// Shape, not just size. A split build can sit comfortably under the byte budget while breaking the
// ruling this gate exists to hold, so the count of emitted scripts and stylesheets is an assertion
// in its own right - otherwise the single-file decision is enforced by a comment and good manners.
const scripts = files.filter((file) => file.endsWith('.js'));
const stylesheets = files.filter((file) => file.endsWith('.css'));
const wrongShape = [];
if (scripts.length !== 1) wrongShape.push(`${scripts.length} JS files, expected 1:\n    ${scripts.join('\n    ')}`);
if (stylesheets.length !== 1) wrongShape.push(`${stylesheets.length} CSS files, expected 1:\n    ${stylesheets.join('\n    ')}`);

if (wrongShape.length > 0) {
  console.error(`\nThe dashboard build is no longer one file:\n  ${wrongShape.join('\n  ')}`);
  console.error('\nThe bundle is embedded in the Acta.AspNetCore assembly, so splitting it does not');
  console.error('reduce what ships and does let a redeploy strand an open session on chunk names the');
  console.error('new assembly no longer has. Read the decision comment above build in vite.config.ts');
  console.error('before changing this - and if the decision is genuinely being reversed, change both.');
  process.exit(1);
}

const over = [];
if (measured.raw > BUDGET.raw) over.push(`raw ${kb(measured.raw).trim()} exceeds the ${kb(BUDGET.raw).trim()} budget`);
if (measured.gzip > BUDGET.gzip) over.push(`gzip ${kb(measured.gzip).trim()} exceeds the ${kb(BUDGET.gzip).trim()} budget`);

if (over.length > 0) {
  console.error(`\nDashboard bundle is over budget:\n  ${over.join('\n  ')}`);
  console.error('\nFind what grew before raising the constants in scripts/size-budget.mjs, and say why.');
  process.exit(1);
}

console.log('\nWithin budget.');
