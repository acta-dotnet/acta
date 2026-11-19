<script lang="ts">
  import { apiHref, controlRequest, payloadRoutes, type JobCheckpoint, type JobControlResponse } from '../../api.ts';
  import ConfirmAction from '../../components/ConfirmAction.svelte';
  import DataTable from '../../components/DataTable.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import StateView from '../../components/StateView.svelte';
  import { displayFormatter } from '../../format.ts';
  import { jobControlMessage } from '../../lib/controlMessages.ts';
  import { payloadFormatLabel } from './jobDetailState.ts';
  import StoredPayload, { downloadName } from './StoredPayload.svelte';

  // Checkpoints (variables, signals, timers, progress, children) come from the aggregate detail read's first
  // page, each value by its format and size with its download beside it; a value is read only while its row is open,
  // again when the checkpoint is written, and a larger set says so. A signal row carries a Clear action where the
  // server would take it: for a control-capable caller, on a finished job or a permanent job between firings, never on
  // a system job, whose signals are commands it has yet to apply.
  let {
    checkpoints = [],
    total = null,
    jobRef = '',
    jobName = '',
    status = '',
    kind = 'ordinary',
    canControl = false,
    onCleared = () => {}
  }: {
    checkpoints?: JobCheckpoint[];
    total?: number | null;
    jobRef?: string;
    jobName?: string | null;
    status?: string;
    kind?: string;
    canControl?: boolean;
    onCleared?: () => void;
  } = $props();

  let clearable = $derived(
    canControl &&
      !jobName?.startsWith('sys.') &&
      checkpoints.some((checkpoint) => checkpoint.kind === 'signal') &&
      (['succeeded', 'failed', 'cancelled'].includes(status) || (kind === 'permanent' && status === 'idle'))
  );
  let confirming = $state<string | null>(null);
  let busy = $state(false);
  let message = $state('');
  let messageKind = $state('');

  // By kind and name, the rows open now, which read their value, and the rows ever opened, which keep the last value
  // read so a reopened row shows it at once.
  let open = $state<Record<string, boolean>>({});
  let seen = $state<Record<string, boolean>>({});
  const keyOf = (checkpoint: JobCheckpoint) => checkpoint.kind + '/' + checkpoint.name;
  const pathOf = (checkpoint: JobCheckpoint) => payloadRoutes.checkpointValue(jobRef, checkpoint.kind, checkpoint.name);

  function toggled(key: string, isOpen: boolean): void {
    open[key] = isOpen;
    if (isOpen) seen[key] = true;
  }

  async function clear(name: string) {
    confirming = null;
    busy = true;
    message = '';
    try {
      const result = await controlRequest<JobControlResponse>(
        `jobs/${jobRef}/signals/${encodeURIComponent(name)}`,
        undefined,
        { action: 'not-found', status: null },
        'DELETE'
      );
      message = jobControlMessage('clear-signal', result.action, result.reason);
      messageKind = result.action === 'applied' ? 'ok' : 'warn';
      onCleared();
    } catch (e) {
      message = (e as Error).message;
      messageKind = 'bad';
    } finally {
      busy = false;
    }
  }
</script>

<section class="detail-panel" aria-label="Job checkpoints">
  <h2>Checkpoints</h2>
  {#if checkpoints.length === 0}
    <StateView emptyText="No checkpoints." />
  {:else}
    <DataTable>
      <caption class="sr-only">Job checkpoints</caption>
      <thead>
        <tr>
          <th>Kind</th><th>Name</th><th>Status</th><th>Due</th><th>Value</th>
          {#if clearable}<th><span class="sr-only">Actions</span></th>{/if}
        </tr>
      </thead>
      <tbody>
        {#each checkpoints as checkpoint (checkpoint.kind + '/' + checkpoint.name)}
          <tr>
            <td>{checkpoint.kind}</td>
            <td class="mono">{checkpoint.name}</td>
            <td>{checkpoint.status ?? '·'}</td>
            <td><RelativeTime value={checkpoint.dueAtUtc} /></td>
            <td>
              {#if checkpoint.valueByteLength != null}
                {@const key = keyOf(checkpoint)}
                {@const fileName = `${jobRef}-${checkpoint.kind}-${checkpoint.name}`}
                <div class="checkpoint-value-cell">
                  <details ontoggle={(e) => toggled(key, e.currentTarget.open)}>
                    <summary>View <span class="dim">{payloadFormatLabel(checkpoint.valueFormatId)}, {displayFormatter.bytes(checkpoint.valueByteLength)}</span></summary>
                    {#if seen[key]}
                      <div class="checkpoint-value">
                        <!-- Two writes in one millisecond share a time, so the format and size key the read as well. -->
                        <StoredPayload
                          path={pathOf(checkpoint)}
                          formatId={checkpoint.valueFormatId}
                          byteLength={checkpoint.valueByteLength}
                          {fileName}
                          label={checkpoint.kind + ' ' + checkpoint.name}
                          version={`${checkpoint.valueFormatId}|${checkpoint.valueByteLength}|${checkpoint.modifiedAtUtc}`}
                          active={open[key] === true}
                          showDownload={false} />
                      </div>
                    {/if}
                  </details>
                  <a
                    class="payload-download"
                    href={apiHref(pathOf(checkpoint))}
                    download={downloadName(fileName, checkpoint.valueFormatId)}
                    aria-label={'Download ' + checkpoint.kind + ' ' + checkpoint.name}>Download</a>
                </div>
              {:else}
                <span class="dim">·</span>
              {/if}
            </td>
            {#if clearable}
              <td>
                {#if checkpoint.kind === 'signal'}
                  <button disabled={busy} onclick={() => (confirming = checkpoint.name)} aria-label={'Clear signal ' + checkpoint.name}>Clear</button>
                {/if}
              </td>
            {/if}
          </tr>
        {/each}
      </tbody>
    </DataTable>
    {#if total !== null && total > checkpoints.length}
      <p class="detail-help">Showing {displayFormatter.number(checkpoints.length)} of {displayFormatter.number(total)}.</p>
    {/if}
  {/if}
  {#if message}
    <div class="control-message {messageKind}" role="status">{message}</div>
  {/if}
</section>

{#if confirming}
  <ConfirmAction
    title={'Clear signal ' + confirming + '?'}
    body="The job forgets this signal and its value, so its next wait for it waits again. To give it another value, clear it, run the job again, then raise it."
    confirmLabel="Clear signal"
    danger
    showReason={false}
    onConfirm={() => clear(confirming!)}
    onCancel={() => (confirming = null)} />
{/if}

<style>
  summary { cursor: pointer; color: var(--muted); }
  .checkpoint-value { margin-top: 8px; }
  .checkpoint-value-cell { display: flex; align-items: baseline; gap: 10px; }
  .checkpoint-value-cell details { flex: 1; min-width: 0; }
  .payload-download { color: var(--accent); }
</style>
