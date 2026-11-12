import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import VersionChip from './VersionChip.svelte';
import VersionStatus from './VersionStatus.svelte';

const STATUSES = ['draft', 'published', 'superseded', 'archived'];

// The parts of a rendered element, in order, by class, so a status that gains or loses a part fails here.
const parts = (root: Element) => [...root.children].map((child) => child.classList[0]);

describe('version chips and badges', () => {
  it('give every status the same parts in the same order, its mark first', () => {
    for (const status of STATUSES) {
      const chip = render(VersionChip, { version: 12, status, at: '2026-07-14T08:00:00Z', href: '#/calendars/sys/office-hours' });
      const link = chip.getByRole('link', { name: new RegExp(`^v12 ${status}`) });
      expect(parts(link)).toEqual(['version-mark', 'version-num', 'version-status', 'version-when']);
      expect(link.querySelector('.version-mark')?.children).toHaveLength(1);
      chip.unmount();

      const badge = render(VersionStatus, { status, version: 12 });
      const root = badge.container.querySelector('.badge')!;
      expect(parts(root)).toEqual(['version-mark', 'version']);
      expect(root.textContent?.replace(/\s+/g, ' ').trim()).toBe(`v12 ${status}`);
      badge.unmount();
    }
  });
});
