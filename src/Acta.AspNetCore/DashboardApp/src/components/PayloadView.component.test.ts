import { fireEvent, render, screen } from '@testing-library/svelte';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import PayloadView from './PayloadView.svelte';

describe('PayloadView', () => {
  it('pretty-prints a json payload', () => {
    const { container } = render(PayloadView, { payload: { formatName: 'json', formatId: 1, json: { a: 1, b: [2, 3] } } });
    const pre = container.querySelector('pre');
    expect(pre?.textContent).toBe('{\n  "a": 1,\n  "b": [\n    2,\n    3\n  ]\n}');
    expect(screen.getByRole('button', { name: 'Copy JSON' })).toBeTruthy();
  });

  it('renders a text payload verbatim', () => {
    const { container } = render(PayloadView, { payload: { formatName: 'text', formatId: 3, text: 'hello world' } });
    expect(container.querySelector('pre')?.textContent).toBe('hello world');
  });

  it('shows byte length, hex, download, and copy-base64 for bytes', () => {
    // "Aci" -> 0x41 0x63 0x69
    const { container } = render(PayloadView, { payload: { formatName: 'bytes', formatId: 2, base64: 'QWNp' } });
    expect(container.textContent).toContain('3 bytes');
    expect(container.querySelector('pre')?.textContent).toBe('41 63 69');
    const download = container.querySelector('a.payload-download') as HTMLAnchorElement;
    expect(download.getAttribute('href')).toBe('data:application/octet-stream;base64,QWNp');
    expect(screen.getByRole('button', { name: 'Copy base64' })).toBeTruthy();
  });

  it('treats an unknown format like bytes and names the format', () => {
    const { container } = render(PayloadView, { payload: { formatName: 'protobuf', formatId: 200, base64: 'QWNp' } });
    expect(container.textContent).toContain('format protobuf');
    expect(container.textContent).toContain('3 bytes');
  });

  it('renders a muted placeholder for none', () => {
    render(PayloadView, { payload: { formatName: 'none', formatId: 0 } });
    expect(screen.getByText('No payload')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });

  it('offers no edit affordance unless editable and json/text', () => {
    render(PayloadView, { payload: { formatName: 'json', formatId: 1, json: {} }, editable: false });
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });

  it('saves an edited json payload after successful validation', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();
    render(PayloadView, { payload: { formatName: 'json', formatId: 1, json: { a: 1 } }, editable: true, onSave });

    await user.click(screen.getByRole('button', { name: 'Edit' }));
    const editor = screen.getByRole('textbox', { name: 'Payload editor' }) as HTMLTextAreaElement;
    expect(editor.value).toBe('{\n  "a": 1\n}');

    await fireEvent.input(editor, { target: { value: '{"b": 2}' } });
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(onSave).toHaveBeenCalledOnce();
    expect(onSave).toHaveBeenCalledWith('{"b": 2}');
  });

  it('blocks save and shows an error for invalid json', async () => {
    const user = userEvent.setup();
    const onSave = vi.fn();
    render(PayloadView, { payload: { formatName: 'json', formatId: 1, json: { a: 1 } }, editable: true, onSave });

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
    render(PayloadView, { payload: { formatName: 'bytes', formatId: 2, base64: 'QWNp' }, editable: true });
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });

  it('renders a size notice for a truncated payload with no editor or copy affordance', () => {
    const { container } = render(PayloadView, {
      payload: { formatName: 'json', formatId: 1, byteLength: 512 * 1024, truncated: true },
      editable: true
    });
    expect(container.textContent).toContain('too large to display');
    expect(container.textContent).toContain('512.0 KB');
    expect(container.querySelector('pre')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Copy JSON' })).toBeNull();
  });
});
