import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import { displayFormatter } from '../format.ts';
import TimeCell from './TimeCell.svelte';

describe('TimeCell', () => {
  it('shows relative time and makes the exact timestamp inspectable', () => {
    const iso = '2026-07-15T06:26:52Z';
    render(TimeCell, { value: iso });
    const button = screen.getByRole('button', { name: /Show exact date and time/ });
    expect(button.textContent).not.toContain('[');
    expect(button.getAttribute('popovertarget')).toBeTruthy();
    expect(document.getElementById(button.getAttribute('popovertarget')!)?.textContent).toContain(displayFormatter.timestamp(iso));
  });

  it('renders a dash for missing values', () => {
    render(TimeCell, { value: null });
    expect(screen.getByText('-')).toBeTruthy();
  });
});
