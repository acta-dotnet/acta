import { fireEvent, render, screen } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import PayloadView from './PayloadView.svelte';

describe('PayloadView', () => {
  it('pretty-prints a json payload', () => {
    const { container } = render(PayloadView, { json: '{"a":1,"b":[2,3]}' });
    const pre = container.querySelector('pre');
    expect(pre?.textContent).toBe('{\n  "a": 1,\n  "b": [\n    2,\n    3\n  ]\n}');
    expect(screen.getByRole('button', { name: 'Copy JSON' })).toBeTruthy();
  });

  it('shows a json payload with every digit of a number past 2^53', () => {
    const { container } = render(PayloadView, { json: '{"orderId":9007199254740993}' });
    expect(container.querySelector('pre')?.textContent).toBe('{\n  "orderId": 9007199254740993\n}');
  });

  it('shows bytes with the note it is given, and no editor', () => {
    const { container } = render(PayloadView, {
      base64: 'e2Jyb2tlbg==',
      note: 'stored JSON is malformed; shown as bytes',
      editable: true
    });
    expect(container.textContent).toContain('stored JSON is malformed');
    expect(container.textContent).toContain('7 bytes');
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });

  it('shows byte length, hex, and copy-base64 for bytes, and no download of its own', () => {
    // "Aci" -> 0x41 0x63 0x69
    const { container } = render(PayloadView, { base64: 'QWNp' });
    expect(container.textContent).toContain('3 bytes');
    expect(container.querySelector('pre')?.textContent).toBe('41 63 69');
    expect(container.querySelector('a')).toBeNull();
    expect(screen.getByRole('button', { name: 'Copy base64' })).toBeTruthy();
  });

  it('renders a muted placeholder for no payload', () => {
    render(PayloadView, {});
    expect(screen.getByText('No payload')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });

  it('offers no edit affordance unless editable and json', () => {
    render(PayloadView, { json: '{}', editable: false });
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });

  it('offers neither Edit nor a visible Copy on a body that is not current', () => {
    const { container } = render(PayloadView, { json: '{"a":1}', editable: true, current: false });
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
    expect(container.querySelector('.copy-slot')?.classList.contains('waiting')).toBe(true);
  });

  it('saves an edited json payload after successful validation', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();
    render(PayloadView, { json: '{"a":1}', editable: true, onSave });

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    const editor = screen.getByRole('textbox', { name: 'Payload editor' }) as HTMLTextAreaElement;
    expect(editor.value).toBe('{\n  "a": 1\n}');

    await fireEvent.input(editor, { target: { value: '{"b": 2}' } });
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(onSave).toHaveBeenCalledOnce();
    expect(onSave).toHaveBeenCalledWith('{"b": 2}');
    expect(screen.queryByRole('textbox', { name: 'Payload editor' })).toBeNull();
  });

  it('keeps the editor and its draft open when the save answers false', async () => {
    const user = userEvent.setup();
    render(PayloadView, { json: '{"a":1}', editable: true, onSave: () => false });

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    const editor = screen.getByRole('textbox', { name: 'Payload editor' }) as HTMLTextAreaElement;
    await fireEvent.input(editor, { target: { value: '{"b": 2}' } });
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect((screen.getByRole('textbox', { name: 'Payload editor' }) as HTMLTextAreaElement).value).toBe('{"b": 2}');
  });

  it('blocks save and shows an error for invalid json', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();
    render(PayloadView, { json: '{"a":1}', editable: true, onSave });

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    const editor = screen.getByRole('textbox', { name: 'Payload editor' });
    await fireEvent.input(editor, { target: { value: '{not valid' } });

    expect(screen.getByRole('alert')).toBeTruthy();
    const save = screen.getByRole('button', { name: 'Save' }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    await user.click(save);
    expect(onSave).not.toHaveBeenCalled();
  });

  it('offers no edit affordance for a binary payload', () => {
    render(PayloadView, { base64: 'QWNp', editable: true });
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });
});
