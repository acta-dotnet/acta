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
  build: {
    manifest: true,
    outDir: 'dist',
    emptyOutDir: true
  }
});
