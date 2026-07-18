import { fireEvent, render, screen } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { resetAppearance } from '../theme/appearance.ts';
import AppearanceMenu from './AppearanceMenu.svelte';

describe('AppearanceMenu', () => {
  beforeEach(() => resetAppearance());
  afterEach(() => resetAppearance());

  it('opens as a nonmodal dialog and focuses the selected theme', async () => {
    const user = userEvent.setup();
    render(AppearanceMenu);

    const trigger = screen.getByRole('button', { name: /Appearance Acta/ });
    expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');
    await user.click(trigger);

    const dialog = screen.getByRole('dialog', { name: 'Appearance' });
    expect(dialog.getAttribute('aria-modal')).toBe('false');
    expect(document.activeElement).toBe(screen.getByRole('radio', { name: /^Acta/ }));
  });

  it('changes every setting and restores all defaults atomically', async () => {
    const user = userEvent.setup();
    render(AppearanceMenu);
    await user.click(screen.getByRole('button', { name: /Appearance Acta/ }));

    await user.click(screen.getByRole('radio', { name: /^Paper/ }));
    await user.click(screen.getByRole('radio', { name: 'Violet' }));
    await user.click(screen.getByRole('radio', { name: 'Large' }));

    expect(document.documentElement.dataset).toMatchObject({
      theme: 'paper',
      accent: 'violet',
      textSize: 'large',
    });

    await user.click(screen.getByRole('button', { name: 'Restore defaults' }));
    expect(document.documentElement.dataset).toMatchObject({
      theme: 'acta',
      accent: 'teal',
      textSize: 'default',
    });
  });

  it('closes on Escape and returns focus to the trigger', async () => {
    const user = userEvent.setup();
    render(AppearanceMenu);
    const trigger = screen.getByRole('button', { name: /Appearance Acta/ });
    await user.click(trigger);

    await user.keyboard('{Escape}');

    expect(screen.queryByRole('dialog', { name: 'Appearance' })).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('closes on an outside pointer without stealing focus', async () => {
    const user = userEvent.setup();
    render(AppearanceMenu);
    const trigger = screen.getByRole('button', { name: /Appearance Acta/ });
    await user.click(trigger);

    const outside = document.createElement('button');
    outside.textContent = 'Another dashboard control';
    document.body.append(outside);
    outside.focus();
    await fireEvent.pointerDown(outside);

    expect(screen.queryByRole('dialog', { name: 'Appearance' })).toBeNull();
    expect(document.activeElement).toBe(outside);
    expect(document.activeElement).not.toBe(trigger);
    outside.remove();
  });
});
