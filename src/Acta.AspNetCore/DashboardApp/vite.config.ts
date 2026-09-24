import { defineConfig } from 'vitest/config';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import license from 'rollup-plugin-license';

// The notices are generated from the modules that actually land in the bundle, never listed by
// hand: a transitive package that survives minification still owes its copyright line. The file is
// embedded with the other dist files and packed at the root of Acta.AspNetCore.
const thirdPartyNotices = license({
  thirdParty: {
    includePrivate: false,
    output: {
      file: 'dist/THIRD-PARTY-NOTICES.txt',
      template(dependencies) {
        const header =
          'Third-party notices for Acta.AspNetCore\n\n' +
          'The operator dashboard embedded in this package bundles the open-source components below.\n' +
          'Acta itself is licensed under the Apache License 2.0; see LICENSE in the repository.\n';
        const sections = dependencies
          .slice()
          .sort((a, b) => (a.name ?? '').localeCompare(b.name ?? ''))
          .map((d) => {
            const lines = [`${d.name ?? 'unknown'} ${d.version ?? ''}`.trim(), `License: ${d.license ?? 'unknown'}`];
            const repository = typeof d.repository === 'string' ? d.repository : d.repository?.url;
            const source = d.homepage ?? repository;
            if (source) lines.push(`Source: ${source}`);
            lines.push('', (d.licenseText ?? '').trim());
            return lines.join('\n');
          });
        return [header, ...sections].join('\n' + '-'.repeat(80) + '\n');
      }
    }
  }
});

export default defineConfig({
  plugins: [svelte(), thirdPartyNotices],
  base: './',
  resolve: {
    conditions: ['browser']
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.component.test.ts'],
    setupFiles: ['./src/test-setup.ts']
  },
  // One JS file and one CSS file, deliberately. dist is embedded into the Acta.AspNetCore assembly,
  // so route-level code splitting cannot shrink what ships - it only spreads the same bytes over
  // more requests, and it lets a redeploy strand an open session on chunk names the new assembly no
  // longer has. Total size is the thing that matters, and scripts/size-budget.mjs holds the line on
  // it in CI. Before adding rollupOptions.output chunking here, read the reasoning written there.
  build: {
    manifest: true,
    outDir: 'dist',
    emptyOutDir: true
  }
});
