import { defineConfig } from 'vitest/config';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
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
