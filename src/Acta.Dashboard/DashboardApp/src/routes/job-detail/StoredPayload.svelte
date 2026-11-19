<script module lang="ts">
  /** The largest stored payload the dashboard reads to show; a larger one is offered as a download only. */
  export const payloadDisplayLimitBytes = 256 * 1024;

  /** A payload's download name: its stem, then .json for the JSON format and .bin for any other. */
  export function downloadName(fileName: string, formatId: number): string {
    return fileName + (formatId === 1 ? '.json' : '.bin');
  }
</script>

<script lang="ts">
  import { apiHref, getJobPayload, type PayloadRead } from '../../api.ts';
  import PayloadView from '../../components/PayloadView.svelte';
  import StateView from '../../components/StateView.svelte';
  import { displayFormatter } from '../../format.ts';
  import { payloadFormatLabel } from './jobDetailState.ts';

  // One stored payload of a job, its input, its result, or a checkpoint's value, given by its format, its size, and the
  // route that serves its body. While `active`, a body at or under the display limit is read, and read again when
  // `version` changes; the last body stays in view, without Copy or Edit, until the newer one lands. Over the limit,
  // by the job's read or by the answer itself, nothing is held and the payload states its size. Every payload offers
  // its download, a plain link to the same route, so the browser streams the file and the page never holds it.
  let {
    path,
    formatId,
    byteLength,
    fileName,
    label,
    version = '',
    active = true,
    showDownload = true,
    editable = false,
    editing = $bindable(false),
    over = $bindable(false),
    onSave
  }: {
    path: string;
    formatId: number;
    byteLength: number;
    // The download's name without its extension, such as job_...-input.
    fileName: string;
    // What the payload is, as the download's name says it: input, result, or a checkpoint's kind and name.
    label: string;
    // Changes when the stored payload may have: the body is read again.
    version?: string;
    active?: boolean;
    showDownload?: boolean;
    editable?: boolean;
    editing?: boolean;
    over?: boolean;
    // The edited text, and the version of the body the edit started from; answer false to keep the editor open.
    onSave?: (value: string, version: string) => boolean | void | Promise<boolean | void>;
  } = $props();

  // The body in view and the path and version it was read at; the read in flight; the read that failed.
  let shown = $state<{ path: string; version: string; read: PayloadRead } | null>(null);
  let pending = $state<string | null>(null);
  let failed = $state<{ key: string; message: string } | null>(null);
  let editedFrom = $state<string | null>(null);
  let controller: AbortController | undefined;

  let key = $derived(path + '|' + version);
  let overLimit = $derived(byteLength > payloadDisplayLimitBytes || shown?.read.kind === 'over');
  let shownSize = $derived(shown?.read.kind === 'over' ? shown.read.byteLength : byteLength);
  let current = $derived(shown !== null && shown.path + '|' + shown.version === key && pending === null);
  let note = $derived(
    shown?.read.kind === 'bytes' && shown.read.formatId === 1
      ? 'stored JSON is malformed; shown as bytes'
      : formatId > 2
        ? 'format ' + payloadFormatLabel(formatId)
        : ''
  );

  async function load(from: string, at: string) {
    controller?.abort();
    const own = new AbortController();
    controller = own;
    pending = from + '|' + at;
    failed = null;
    if (shown && shown.path !== from) shown = null;
    try {
      const read = await getJobPayload(from, { signal: own.signal, limit: payloadDisplayLimitBytes });
      if (controller !== own) return;
      shown = { path: from, version: at, read };
      pending = null;
    } catch (e) {
      if (controller !== own) return;
      failed = { key: from + '|' + at, message: (e as Error).message };
      pending = null;
    }
  }

  // A read starts while active, unless the job's read already puts the payload over the limit, and a failed one waits
  // for Retry. A newer version starts a newer read, and an answer to an older one is dropped.
  $effect(() => {
    if (!active || byteLength > payloadDisplayLimitBytes) return;
    if (pending === key || failed?.key === key || (shown !== null && shown.path + '|' + shown.version === key)) return;
    void load(path, version);
  });

  $effect(() => () => controller?.abort());

  $effect(() => {
    over = overLimit;
  });

  // The edit starts from the body in view; an editor that can no longer stand on a JSON body closes.
  $effect(() => {
    if (!editing) {
      editedFrom = null;
    } else if (overLimit || shown?.read.kind !== 'json') {
      editing = false;
    } else if (editedFrom === null) {
      editedFrom = shown.version;
    }
  });

  let body = $derived(
    shown?.read.kind === 'json'
      ? { json: shown.read.text }
      : shown?.read.kind === 'bytes'
        ? { base64: shown.read.base64 }
        : {}
  );
</script>

{#snippet download()}
  {#if showDownload}
    <a class="payload-download" href={apiHref(path)} download={downloadName(fileName, formatId)} aria-label={'Download ' + label}>Download</a>
  {/if}
{/snippet}

<div class="stored-payload">
  {#if overLimit}
    <p class="dim">
      Payload is {displayFormatter.bytes(shownSize)}; too large to display.{#if showDownload}{' '}{@render download()}{/if}
    </p>
  {:else if shown?.read.kind === 'none' && current}
    <p class="dim">No payload</p>
  {:else}
    <PayloadView
      {...body}
      {note}
      {current}
      {editable}
      bind:editing
      onSave={(value) => onSave?.(value, editedFrom ?? version)}
      tools={download}>
      {#snippet placeholder()}
        {#if failed}
          <StateView error={failed.message} onRetry={() => load(path, version)} />
        {:else}
          <StateView loading loadingText={'Loading ' + label + '...'} />
        {/if}
      {/snippet}
    </PayloadView>
    {#if failed && (editing || shown)}
      <StateView error={failed.message} onRetry={() => load(path, version)} />
    {/if}
  {/if}
</div>

<style>
  .payload-download { color: var(--accent); }
  .dim .payload-download { text-decoration: underline; }
  /* A short body takes the box the loading line held, so it lands without moving anything; a long one grows it once. */
  .stored-payload :global(pre),
  .stored-payload :global(.payload-box) { min-height: calc(4.5rem + 22px); }
</style>
