import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { displayFormatter } from '../format.ts';
import TimeCell from './TimeCell.svelte';

describe('TimeCell', () => {
  it('renders relative time plus the row-form absolute without zone suffix', () => {
    const iso = '2026-07-15T06:26:52Z';
    render(TimeCell, { value: iso });
    const absolute = displayFormatter.rowTimestamp(iso);
    expect(screen.getByText(absolute)).toBeTruthy();
    expect(document.body.textContent).not.toContain('[');
    // Full timestamp (with zone) survives as the relative-time tooltip.
    expect(document.querySelector(`[title="${displayFormatter.timestamp(iso)}"]`)).toBeTruthy();
  });

  it('renders the empty text for missing values', () => {
    render(TimeCell, { value: null, emptyText: '—' });
    expect(screen.getByText('—')).toBeTruthy();
  });
});
