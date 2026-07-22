<script lang="ts">
  import CopyButton from './CopyButton.svelte';
  import { displayFormatter } from '../format.ts';
  import type { JobPayloadView } from '../api.ts';

  // Dumb, format-aware payload preview with an optional validated-textarea edit mode. Fetching and
  // saving live in the parent: `onSave` receives the edited raw JSON text (json) or raw text (text).
  // `editing` is bindable so a parent can reveal its own edit-only fields (the amend reason) exactly
  // while the editor is open.
  let {
    payload,
    editable = false,
    editing = $bindable(false),
    onSave
  }: {
    payload: JobPayloadView;
    editable?: boolean;
    editing?: boolean;
    onSave?: (value: string) => void | Promise<void>;
  } = $props();

  const HEX_LIMIT = 64;

  let draft = $state('');
  let saving = $state(false);

  let format = $derived(payload.format);
  let isJson = $derived(format === 'json');
  let isText = $derived(format === 'text');
  let isNone = $derived(format === 'none');
  // Any non-none/json/text format (bytes, or a consumer-defined id) travels as base64.
  let isBytes = $derived(!isNone && !isJson && !isText);
  // A payload over the size cap ships no body, so it can be neither previewed nor edited.
  let isTruncated = $derived(payload.truncated === true);
  let canEdit = $derived(editable && !isTruncated && (isJson || isText));

  let jsonText = $derived(isJson ? JSON.stringify(payload.json ?? null, null, 2) : '');
  let textValue = $derived(isText ? payload.text ?? '' : '');
  let bytes = $derived(isBytes ? decodeBase64(payload.base64 ?? '') : null);

  // Live validation: json drafts must parse before Save is allowed. Text drafts never fail.
  let parseError = $derived.by(() => {
    if (!editing || !isJson) return '';
    try {
      JSON.parse(draft);
      return '';
    } catch (error) {
      return error instanceof Error ? error.message : 'Invalid JSON.';
    }
  });

  function decodeBase64(base64: string): { length: number; hex: string; truncated: boolean } {
    let binary = '';
    try {
      binary = atob(base64);
    } catch {
      return { length: 0, hex: '', truncated: false };
    }
    const shown = Math.min(binary.length, HEX_LIMIT);
    let hex = '';
    for (let i = 0; i < shown; i++) hex += binary.charCodeAt(i).toString(16).padStart(2, '0') + ' ';
    return { length: binary.length, hex: hex.trim(), truncated: binary.length > shown };
  }

  function startEdit(): void {
    draft = isJson ? jsonText : textValue;
    editing = true;
  }

  function cancelEdit(): void {
    editing = false;
  }

  async function save(): Promise<void> {
    if (parseError || saving) return;
    saving = true;
    try {
      await onSave?.(draft);
      editing = false;
    } finally {
      saving = false;
    }
  }
</script>

{#if editing}
  <div class="payload-edit">
    <textarea class="mono" bind:value={draft} rows="14" spellcheck="false" aria-label="Payload editor"></textarea>
    {#if parseError}<p class="control-message bad" role="alert">{parseError}</p>{/if}
    <div class="payload-actions">
      <button class="primary" type="button" onclick={save} disabled={!!parseError || saving}>{saving ? 'Saving...' : 'Save'}</button>
      <button type="button" onclick={cancelEdit} disabled={saving}>Cancel</button>
    </div>
  </div>
{:else if isTruncated}
  <p class="dim">Payload is {displayFormatter.bytes(payload.byteLength)}; too large to display.</p>
{:else if isNone}
  <p class="dim">No payload</p>
{:else if isJson}
  <div class="payload-block">
    <div class="payload-tools"><CopyButton value={jsonText} label="Copy JSON" /></div>
    <pre class="mono">{jsonText}</pre>
  </div>
  {#if canEdit}<div class="payload-actions"><button type="button" onclick={startEdit}>Edit</button></div>{/if}
{:else if isText}
  <div class="payload-block">
    <div class="payload-tools"><CopyButton value={textValue} label="Copy text" /></div>
    <pre class="mono">{textValue}</pre>
  </div>
  {#if canEdit}<div class="payload-actions"><button type="button" onclick={startEdit}>Edit</button></div>{/if}
{:else}
  <div class="payload-block">
    <div class="payload-tools">
      {#if format !== 'bytes'}<span class="dim">format {format}</span>{/if}
      <a class="payload-download" href={`data:application/octet-stream;base64,${payload.base64 ?? ''}`} download="payload.bin">Download</a>
      <CopyButton value={payload.base64 ?? ''} label="Copy base64" />
    </div>
    {#if bytes}
      <p class="dim">{bytes.length} bytes</p>
      {#if bytes.hex}<pre class="mono">{bytes.hex}{bytes.truncated ? ' ...' : ''}</pre>{/if}
    {/if}
  </div>
{/if}

<style>
  .payload-block { min-width: 0; }
  .payload-tools { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 8px; }
  .payload-actions { display: flex; gap: 8px; margin-top: 10px; }
  pre {
    margin: 0;
    padding: 10px 12px;
    background: var(--badge-bg);
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    overflow: auto;
    max-height: 420px;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .payload-download { color: var(--accent); }
  .payload-edit textarea {
    display: block;
    width: 100%;
    padding: 10px 12px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font-family: var(--mono);
    font-size: 0.94em;
    line-height: 1.5;
    resize: vertical;
    min-height: 180px;
  }
  .payload-edit textarea:hover { border-color: var(--accent); }
  .payload-edit textarea:focus { outline: none; border-color: var(--accent); }
</style>
