import { render, screen, waitFor } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import StoredPayload, { payloadDisplayLimitBytes } from './StoredPayload.svelte';

const jobRef = 'job_019f9b3509487d1b911f2020158384a3';
const path = `jobs/${jobRef}/result`;

// Answers each read with the next response, and records the path it asked for.
function serve(...responses: (() => Response | Promise<Response>)[]): string[] {
  const paths: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: URL | RequestInfo) => {
      paths.push(new URL(String(input)).pathname);
      return responses.shift()!();
    })
  );
  return paths;
}

const json = (text: string, headers: Record<string, string> = {}) => () =>
  new Response(text, { status: 200, headers: { 'Content-Type': 'application/json', ...headers } });
const bytes = (formatId: number) => () =>
  new Response(new Uint8Array([65, 99, 105]), {
    status: 200,
    headers: { 'Content-Type': 'application/octet-stream', 'X-Acta-Payload-Format': String(formatId) }
  });

function props(overrides: Record<string, unknown> = {}) {
  return { path, formatId: 1, byteLength: 13, fileName: `${jobRef}-result`, label: 'result', version: 'v1', ...overrides };
}

const pre = (container: HTMLElement) => container.querySelector('pre')?.textContent;

describe('StoredPayload', () => {
  it('reads a json body at the limit and shows its exact text, every digit kept', async () => {
    const paths = serve(json('{"orderId":9007199254740993}'));
    const { container } = render(StoredPayload, props({ byteLength: payloadDisplayLimitBytes }));

    await waitFor(() => expect(pre(container)).toBe('{\n  "orderId": 9007199254740993\n}'));
    expect(paths).toEqual([`/api/v1/${path}`]);
    expect(container.querySelector('.copy-slot')?.classList.contains('waiting')).toBe(false);
  });

  it('shows bytes as hex, an application format by its name, and stored json that is not strict JSON as bytes', async () => {
    serve(bytes(2), bytes(200), bytes(1));
    const first = render(StoredPayload, props({ formatId: 2, byteLength: 3 }));
    await waitFor(() => expect(pre(first.container)).toBe('41 63 69'));
    first.unmount();

    const custom = render(StoredPayload, props({ formatId: 200, byteLength: 3 }));
    await waitFor(() => expect(custom.container.textContent).toContain('format custom-200'));
    custom.unmount();

    const malformed = render(StoredPayload, props({ formatId: 1, byteLength: 3 }));
    await waitFor(() => expect(malformed.container.textContent).toContain('stored JSON is malformed'));
  });

  it('reads nothing over the limit, states its size, and offers its download right after the sentence', () => {
    const paths = serve();
    const { container } = render(StoredPayload, props({ formatId: 2, byteLength: 12 * 1048576 }));

    const line = container.querySelector('p')!;
    expect(line.textContent?.trim()).toMatch(/^Payload is 12\.0 MB; too large to display\.\s+Download$/);
    const download = screen.getByRole('link', { name: 'Download result' });
    expect(line.contains(download)).toBe(true);
    expect(download.getAttribute('href')).toBe(new URL(`api/v1/${path}`, document.baseURI).href);
    expect(download.getAttribute('download')).toBe(`${jobRef}-result.bin`);
    expect(paths).toHaveLength(0);
  });

  it('holds the limit against the answer: a declared length past it is not read, and a longer body is not shown', async () => {
    serve(json('{}', { 'Content-Length': String(payloadDisplayLimitBytes + 1) }), json(`"${'x'.repeat(payloadDisplayLimitBytes)}"`));
    const declared = render(StoredPayload, props());
    expect(await screen.findByText(/Payload is 256\.0 KB; too large to display\./)).toBeTruthy();
    expect(pre(declared.container)).toBeUndefined();
    declared.unmount();

    const streamed = render(StoredPayload, props());
    expect(await screen.findByText(/; too large to display\./)).toBeTruthy();
    expect(pre(streamed.container)).toBeUndefined();
  });

  it('stops reading when the job says the payload crossed the limit while it was in view', async () => {
    const paths = serve(json('{"a":1}'));
    const { container, rerender } = render(StoredPayload, props());
    await waitFor(() => expect(pre(container)).toContain('"a": 1'));

    await rerender(props({ byteLength: payloadDisplayLimitBytes + 1, version: 'v2' }));

    expect(screen.getByText(/too large to display/)).toBeTruthy();
    expect(paths).toHaveLength(1);
  });

  it('closes an open editor when the payload grows past the limit, so no edit stays open with nothing to edit', async () => {
    serve(json('{"a":1}'));
    const { container, rerender } = render(StoredPayload, props({ editable: true }));
    await waitFor(() => expect(pre(container)).toContain('"a": 1'));
    screen.getByRole('button', { name: 'Edit' }).click();
    expect(await screen.findByRole('textbox', { name: 'Payload editor' })).toBeTruthy();

    await rerender(props({ editable: true, byteLength: payloadDisplayLimitBytes + 1, version: 'v2' }));

    expect(screen.queryByRole('textbox', { name: 'Payload editor' })).toBeNull();
    expect(screen.getByText(/too large to display/)).toBeTruthy();
  });

  it('names a json download .json and any other .bin, and offers it before the body lands', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));
    render(StoredPayload, props({ fileName: `${jobRef}-input`, label: 'input' }));
    expect(screen.getByRole('link', { name: 'Download input' }).getAttribute('download')).toBe(`${jobRef}-input.json`);
  });

  it('shows the loading state in the body box until the body lands', async () => {
    let answer: (response: Response) => void = () => {};
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((resolve) => (answer = resolve))));
    const { container } = render(StoredPayload, props());

    expect(await screen.findByText('Loading result...', {}, { timeout: 2000 })).toBeTruthy();
    expect(container.querySelector('.payload-box')).not.toBeNull();
    answer(json('{"sent":true}')());
    await waitFor(() => expect(pre(container)).toBe('{\n  "sent": true\n}'));
  });

  it('shows a failed read with a retry that reads again', async () => {
    const paths = serve(
      () => new Response('{"title":"Service unavailable.","detail":"The database is unreachable."}', { status: 503 }),
      json('{"count":4}')
    );
    const { container } = render(StoredPayload, props());

    expect((await screen.findByRole('alert')).textContent).toContain('The database is unreachable.');
    screen.getByRole('button', { name: 'Retry' }).click();
    await waitFor(() => expect(pre(container)).toBe('{\n  "count": 4\n}'));
    expect(paths).toHaveLength(2);
  });

  it('reads nothing stored as no payload, and a job that is gone as the not-found error', async () => {
    serve(() => new Response(null, { status: 204 }), () => new Response('{"title":"Job not found.","status":404}', { status: 404 }));
    const none = render(StoredPayload, props());
    expect(await screen.findByText('No payload')).toBeTruthy();
    none.unmount();

    render(StoredPayload, props());
    expect((await screen.findByRole('alert')).textContent).toContain('Job not found.');
  });

  it('treats an answer that is not a payload as an error, never as bytes', async () => {
    serve(() => new Response('<html>Sign in</html>', { status: 200, headers: { 'Content-Type': 'text/html' } }));
    const { container } = render(StoredPayload, props());

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
    expect(pre(container)).toBeUndefined();
  });

  it('keeps the newest answer when an older one lands after it', async () => {
    let first: (response: Response) => void = () => {};
    serve(() => new Promise<Response>((resolve) => (first = resolve)), json('{"n":2}'));
    const { container, rerender } = render(StoredPayload, props());

    await rerender(props({ version: 'v2' }));
    await waitFor(() => expect(pre(container)).toContain('"n": 2'));
    first(json('{"n":1}')());
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(pre(container)).toContain('"n": 2');
  });

  it('reads again when its version changes, keeping the last body in view meanwhile, and not when it stays', async () => {
    let second: (response: Response) => void = () => {};
    const paths = serve(json('{"count":3}'), () => new Promise<Response>((resolve) => (second = resolve)));
    const { container, rerender } = render(StoredPayload, props());
    await waitFor(() => expect(pre(container)).toContain('3'));

    await rerender(props());
    expect(paths).toHaveLength(1);
    await rerender(props({ version: 'v2' }));

    await waitFor(() => expect(paths).toHaveLength(2));
    expect(pre(container)).toContain('3');
    expect(container.querySelector('.copy-slot')?.classList.contains('waiting')).toBe(true);
    second(json('{"count":5}')());
    await waitFor(() => expect(pre(container)).toContain('5'));
  });

  it('reads nothing while inactive, and reads once made active', async () => {
    const paths = serve(json('{}'));
    const { rerender } = render(StoredPayload, props({ active: false }));
    expect(paths).toHaveLength(0);

    await rerender(props({ active: true }));
    await waitFor(() => expect(paths).toHaveLength(1));
  });
});
