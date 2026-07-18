import { render } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import JobRef from './JobRef.svelte';

describe('JobRef', () => {
  it('splits a ULID-backed ref into a muted head and full-ink tail', () => {
    const { container } = render(JobRef, { value: 'job_01kxrwwtf0fe2vygfwffr5c981' });
    expect(container.querySelector('.ref-head')?.textContent).toBe('job_01kxrwwtf0');
    expect(container.querySelector('.ref-tail')?.textContent).toBe('fe2vygfwffr5c981');
    expect(container.textContent).toContain('job_01kxrwwtf0fe2vygfwffr5c981');
  });

  it('renders as a link when href is given', () => {
    const { container } = render(JobRef, { value: 'job_01kxrwwtf0fe2vygfwffr5c981', href: '#/jobs/x' });
    expect(container.querySelector('a.jobref')?.getAttribute('href')).toBe('#/jobs/x');
  });

  it('renders non-ULID values unsplit', () => {
    const { container } = render(JobRef, { value: 'anvil/r-1/always-fails/49' });
    expect(container.querySelector('.ref-head')).toBeNull();
    expect(container.textContent).toContain('anvil/r-1/always-fails/49');
  });
});
