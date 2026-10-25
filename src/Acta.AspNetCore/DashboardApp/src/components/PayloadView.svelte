<script lang="ts">
  import CopyButton from './CopyButton.svelte';
  import { displayFormatter } from '../format.ts';
  import { jsonError, prettyJson } from '../lib/rawJson.ts';
  import type { JobPayloadView } from '../api.ts';

  // Dumb, format-aware payload preview with an optional validated-textarea edit mode. Fetching and
  // saving live in the parent: `onSave` receives the edited JSON text. The stored JSON text is shown,
  // copied, and edited as text, never parsed into numbers (lib/rawJson.ts). `editing` is bindable so a
  // parent can reveal its own edit-only fields (the amend reason) exactly while the editor is open.
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

  let format = $derived(payload.formatName);
  let isNone = $derived(format === 'none');
  // A stored json row that is not strict JSON ships as base64 with `malformed` set and renders as bytes.
  let isMalformed = $derived(payload.malformed === true);
  let isJson = $derived(format === 'json' && !isMalformed);
  // Any other format (bytes, or a consumer-defined id) travels as base64.
  let isBytes = $derived(!isNone && !isJson);
  // A payload over the size cap ships no body, so it can be neither previewed nor edited.
  let isTruncated = $derived(payload.truncated === true);
  let canEdit = $derived(editable && !isTruncated && isJson);

  let jsonText = $derived(isJson ? prettyJson(payload.json ?? 'null') : '');
  let bytes = $derived(isBytes ? decodeBase64(payload.base64 ?? '') : null);

  // Live validation: a draft must parse before Save is allowed.
  let parseError = $derived(editing ? jsonError(draft) ?? '' : '');

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
    draft = jsonText;
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
{:else}
  <div class="payload-block">
    <div class="payload-tools">
      {#if isMalformed}<span class="dim">stored JSON is malformed; shown as bytes</span>
      {:else if format !== 'bytes'}<span class="dim">format {format}</span>{/if}
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
