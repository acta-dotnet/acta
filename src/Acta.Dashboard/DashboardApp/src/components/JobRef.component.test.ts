import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobRef from './JobRef.svelte';

describe('JobRef', () => {
  it('splits a UUIDv7-backed ref into a muted head and full-ink tail', () => {
    const { container } = render(JobRef, { value: 'job_019f71ce69e07b85bf41fc7bf0562501' });
    expect(container.querySelector('.ref-head')?.textContent).toBe('job_019f71ce69e0');
    expect(container.querySelector('.ref-tail')?.textContent).toBe('7b85bf41fc7bf0562501');
    expect(container.textContent).toContain('job_019f71ce69e07b85bf41fc7bf0562501');
  });

  it('renders as a link when href is given', () => {
    const { container } = render(JobRef, { value: 'job_019f71ce69e07b85bf41fc7bf0562501', href: '#/jobs/x' });
    expect(container.querySelector('a.jobref')?.getAttribute('href')).toBe('#/jobs/x');
  });

  it('shows the tag and the last 8 digits when compact, and keeps the whole ref in the text and the name', () => {
    const value = 'job_019f71ce69e07b85bf41fc7bf0562501';
    const { container } = render(JobRef, { value, href: '#/jobs/x', compact: true });
    expect(container.querySelector('.ref-head')?.textContent).toBe('job_');
    expect(container.querySelector('.ref-tail')?.textContent).toBe('f0562501');
    const link = container.querySelector('a.jobref');
    expect(link?.textContent).toBe(value);
    expect(link?.getAttribute('aria-label')).toBe(value);
  });

  it('renders non-ref values unsplit', () => {
    const { container } = render(JobRef, { value: 'anvil/r-1/always-fails/49' });
    expect(container.querySelector('.ref-head')).toBeNull();
    expect(container.textContent).toContain('anvil/r-1/always-fails/49');
  });
});
