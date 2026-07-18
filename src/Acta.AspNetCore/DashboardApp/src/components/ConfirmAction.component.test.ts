import { fireEvent, render, screen, waitFor } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import ConfirmAction from './ConfirmAction.svelte';

describe('ConfirmAction', () => {
  it('puts initial focus on the safe action for a dangerous operation', async () => {
    const opener = document.createElement('button');
    document.body.append(opener);
    opener.focus();

    render(ConfirmAction, { title: 'Delete?', danger: true });

    const cancel = screen.getByRole('button', { name: 'Keep as is' });
    await waitFor(() => expect(document.activeElement).toBe(cancel));
  });

  it('cancels on Escape', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();
    render(ConfirmAction, { title: 'Cancel?', onCancel });

    await user.keyboard('{Escape}');

    expect(onCancel).toHaveBeenCalledOnce();
  });

  it('traps forward and reverse focus inside the dialog', async () => {
    render(ConfirmAction, { title: 'Proceed?', danger: true });
    const reason = screen.getByRole('textbox');
    const cancel = screen.getByRole('button', { name: 'Keep as is' });
    const confirm = screen.getByRole('button', { name: 'Confirm' });

    await waitFor(() => expect(document.activeElement).toBe(cancel));
    confirm.focus();
    await fireEvent.keyDown(confirm, { key: 'Tab' });
    expect(document.activeElement).toBe(reason);

    await fireEvent.keyDown(reason, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(confirm);
  });

  it('requires the exact confirmation phrase', async () => {
    const user = userEvent.setup();
    render(ConfirmAction, { title: 'Delete?', confirmPhrase: 'DELETE' });
    const inputs = screen.getAllByRole('textbox');
    const phrase = inputs[1];
    const confirm = screen.getByRole('button', { name: 'Confirm' }) as HTMLButtonElement;

    expect(confirm.disabled).toBe(true);
    await user.type(phrase, 'delete');
    expect(confirm.disabled).toBe(true);
    await user.clear(phrase);
    await user.type(phrase, 'DELETE');
    expect(confirm.disabled).toBe(false);
  });

  it('prevents a double click from submitting twice', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    render(ConfirmAction, { title: 'Proceed?', onConfirm });

    await user.dblClick(screen.getByRole('button', { name: 'Confirm' }));

    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it('can omit a reason field when the operation does not retain one', () => {
    render(ConfirmAction, { title: 'Delete?', showReason: false, confirmPhrase: 'job_42' });

    expect(screen.queryByLabelText(/Reason/)).toBeNull();
    expect(screen.getByLabelText(/Type job_42/)).toBeTruthy();
  });
});
