import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobRef from './JobRef.svelte';

describe('JobRef', () => {
  it('shows the tag and the last 8 digits, and inspects the complete reference', () => {
    const value = 'job_019f71ce69e07b85bf41fc7bf0562501';
    const { container } = render(JobRef, { value });
    expect(container.querySelector('.ref-head')?.textContent).toBe('job');
    expect(container.querySelector('.ref-tail')?.textContent).toBe('f0562501');
    const button = container.querySelector('button.jobref');
    expect(button?.textContent).toBe(value);
    const popover = container.querySelector(`[id="${button?.getAttribute('popovertarget')}"]`);
    expect(popover?.getAttribute('popover')).toBe('auto');
    expect(popover?.textContent).toContain(value);
  });

  it('renders as a link that keeps the whole ref in its text and name', () => {
    const value = 'job_019f71ce69e07b85bf41fc7bf0562501';
    const { container } = render(JobRef, { value, href: '#/jobs/x' });
    const link = container.querySelector('a.jobref');
    expect(link?.getAttribute('href')).toBe('#/jobs/x');
    expect(link?.textContent).toBe(value);
    expect(link?.getAttribute('aria-label')).toBe(value);
    expect(container.querySelector('.ref-tail')?.textContent).toBe('f0562501');
  });

  it('renders non-ref values unsplit', () => {
    const { container } = render(JobRef, { value: 'anvil/r-1/always-fails/49' });
    expect(container.querySelector('.ref-head')).toBeNull();
    expect(container.textContent).toContain('anvil/r-1/always-fails/49');
  });
});
