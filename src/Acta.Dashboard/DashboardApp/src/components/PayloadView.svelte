<script lang="ts">
  import type { Snippet } from 'svelte';
  import CopyButton from './CopyButton.svelte';
  import { jsonError, prettyJson } from '../lib/rawJson.ts';

  // Dumb payload preview with an optional validated-textarea edit mode, always laid out as a tools row over a body box.
  // `json` is a JSON body's exact text, shown, copied, and edited as text, never parsed into numbers (lib/rawJson.ts);
  // `base64` is any other body, shown as a hex preview. With neither, the box holds `placeholder`, or says there is no
  // payload. `note` leads the tools row and `tools` follows the copy control. Copy and Edit act only on a `current`
  // body. Fetching and saving live in the parent: `onSave` receives the edited JSON text, and the editor stays open
  // when it answers false. `editing` is bindable so a parent can reveal its own edit-only fields while it is open.
  let {
    json,
    base64,
    note = '',
    current = true,
    editable = false,
    editing = $bindable(false),
    onSave,
    tools,
    placeholder
  }: {
    json?: string;
    base64?: string;
    note?: string;
    current?: boolean;
    editable?: boolean;
    editing?: boolean;
    onSave?: (value: string) => boolean | void | Promise<boolean | void>;
    tools?: Snippet;
    placeholder?: Snippet;
  } = $props();

  const HEX_LIMIT = 64;

  let draft = $state('');
  let saving = $state(false);

  let jsonText = $derived(json === undefined ? '' : prettyJson(json));
  let bytes = $derived(base64 === undefined ? null : decodeBase64(base64));
  let hasBody = $derived(json !== undefined || base64 !== undefined);
  let canEdit = $derived(editable && current && json !== undefined);

  // Live validation: a draft must parse before Save is allowed.
  let parseError = $derived(editing ? jsonError(draft) ?? '' : '');

  function decodeBase64(text: string): { length: number; hex: string; truncated: boolean } {
    let binary = '';
    try {
      binary = atob(text);
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
      if ((await onSave?.(draft)) !== false) editing = false;
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
{:else if !hasBody && !placeholder}
  <p class="dim">No payload</p>
{:else}
  <div class="payload-block">
    <div class="payload-tools">
      {#if note}<span class="dim">{note}</span>{/if}
      {#if bytes}<span class="dim">{bytes.length} bytes</span>{/if}
      <!-- The copy control keeps its place while it waits for a current body, so nothing beside it moves. -->
      <span class="copy-slot" class:waiting={!hasBody || !current}>
        <CopyButton value={json !== undefined ? jsonText : (base64 ?? '')} label={json !== undefined ? 'Copy JSON' : 'Copy base64'} />
      </span>
      {@render tools?.()}
    </div>
    {#if json !== undefined}
      <pre class="mono">{jsonText}</pre>
    {:else if bytes}
      <pre class="mono">{bytes.hex}{bytes.truncated ? ' ...' : ''}</pre>
    {:else}
      <div class="payload-box">{@render placeholder?.()}</div>
    {/if}
  </div>
  {#if canEdit}<div class="payload-actions"><button type="button" onclick={startEdit}>Edit</button></div>{/if}
{/if}

<style>
  .payload-block { min-width: 0; }
  .payload-tools { display: flex; align-items: center; justify-content: flex-end; gap: 10px; flex-wrap: wrap; margin-bottom: 8px; }
  .copy-slot.waiting { visibility: hidden; }
  .payload-actions { display: flex; gap: 8px; margin-top: 10px; }
  pre,
  .payload-box {
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
</style>
