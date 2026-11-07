import { render, screen } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import ActiveFilters from './ActiveFilters.svelte';

describe('ActiveFilters', () => {
  it('reports the active count and removes one filter', async () => {
    const user = userEvent.setup();
    const removeStatus = vi.fn();
    render(ActiveFilters, {
      chips: [
        { label: 'Status', value: 'Failed', onRemove: removeStatus },
        { label: 'Namespace', value: 'billing', onRemove: vi.fn() }
      ]
    });

    expect(screen.getByText('2 filters active')).toBeTruthy();
    await user.click(screen.getByTitle('Remove the Status filter'));
    expect(removeStatus).toHaveBeenCalledOnce();
  });

  it('clears all filters', async () => {
    const user = userEvent.setup();
    const onClearAll = vi.fn();
    render(ActiveFilters, {
      chips: [{ label: 'Status', value: 'Failed', onRemove: vi.fn() }],
      onClearAll
    });

    expect(screen.getByText('1 filter active')).toBeTruthy();
    await user.click(screen.getByRole('button', { name: 'Clear all' }));
    expect(onClearAll).toHaveBeenCalledOnce();
  });
});
