import { afterEach, beforeAll, vi } from 'vitest';
import { cleanup } from '@testing-library/svelte';
import { online } from './api.ts';
import { livePaused } from './polling.ts';

beforeAll(() => {
  Object.defineProperty(HTMLElement.prototype, 'offsetParent', {
    configurable: true,
    get() {
      return this.parentElement;
    }
  });
});

afterEach(() => {
  cleanup();
  online.set(true);
  livePaused.set(false);
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});
