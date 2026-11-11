import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import EventDetails from './EventDetails.svelte';

describe('EventDetails', () => {
  it('names the operator who acted beside the message and the details', () => {
    render(EventDetails, { message: 'duplicate order', actor: 'Jože Kovač', details: '{"reason":"manual"}' });
    const button = screen.getByRole('button', { name: /Show full details/ });
    expect(button.textContent).toBe('duplicate order · by Jože Kovač {"reason":"manual"}');
    expect(document.getElementById(button.getAttribute('popovertarget')!)?.textContent).toContain('by Jože Kovač');
  });

  it('names no one for an event without an actor', () => {
    render(EventDetails, { message: 'boom', actor: null, details: null });
    expect(screen.getByRole('button', { name: /Show full details/ }).textContent).toBe('boom');
  });
});
