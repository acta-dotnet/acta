<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import { api, controlRequest, type AdminControlResult } from '../api.ts';
  import { capabilitiesQuery, canControl } from '../query.ts';
  import Icon from './Icon.svelte';

  interface Tag {
    name: string;
    value: string | null;
  }

  // `path` is the entity's tag subresource, e.g. `jobs/${jobRef}/tags`. The editor GETs it, and
  // POST/DELETE against the same path add and remove individual tags.
  let { path, title = 'Tags' }: { path: string; title?: string } = $props();

  const client = useQueryClient();
  const tags = createQuery(() => ({
    queryKey: ['tags', path],
    queryFn: ({ signal }: { signal: AbortSignal }) => api<Tag[]>(path, {}, { signal })
  }));
  const capabilities = createQuery(() => capabilitiesQuery());
  let canEdit = $derived(canControl(capabilities.data));
  let collapsed = $derived(!canEdit && !tags.isPending && !tags.error && (tags.data ?? []).length === 0);

  let input = $state('');
  let busy = $state(false);
  let message = $state('');

  function refresh() {
    return client.invalidateQueries({ queryKey: ['tags', path] });
  }

  async function add() {
    const token = input.trim();
    if (!token || busy) return;
    const separator = token.indexOf(':');
    const name = separator < 0 ? token : token.slice(0, separator);
    const value = separator < 0 ? null : token.slice(separator + 1);
    busy = true;
    message = '';
    try {
      const result = await controlRequest<AdminControlResult>(path, { name, value }, { action: 'notFound', version: null });
      if (result.action === 'notFound') {
        message = 'Target not found.';
      } else {
        input = '';
        await refresh();
      }
    } catch (e) {
      message = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  async function remove(name: string) {
    if (busy) return;
    busy = true;
    message = '';
    try {
      const result = await controlRequest<AdminControlResult>(
        `${path}/${encodeURIComponent(name)}`,
        undefined,
        { action: 'notFound', version: null },
        'DELETE'
      );
      if (result.action === 'notFound') {
        message = 'Target not found.';
      } else {
        await refresh();
      }
    } catch (e) {
      message = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }
</script>

{#if !collapsed}
  <section class="detail-panel" aria-labelledby="tag-editor-heading">
    <h2 id="tag-editor-heading">{title}</h2>
    {#if tags.isPending}
      <p class="detail-help">Loading tags...</p>
    {:else if tags.error}
      <p class="control-message bad" role="status">{tags.error instanceof Error ? tags.error.message : String(tags.error)}</p>
    {:else}
      <div class="tag-list">
        {#each tags.data ?? [] as tag (tag.name)}
          <span class="chip tag-chip">
            <span class="mono">{tag.name}{tag.value == null ? '' : ': ' + tag.value}</span>
            {#if canEdit}
              <button type="button" class="tag-remove" title={'Remove ' + tag.name} aria-label={'Remove ' + tag.name} disabled={busy} onclick={() => remove(tag.name)}>
                <Icon name="x" />
              </button>
            {/if}
          </span>
        {:else}
          <p class="detail-help">No tags.</p>
        {/each}
      </div>
      {#if canEdit}
        <form class="tag-add" onsubmit={(event) => { event.preventDefault(); add(); }}>
          <input bind:value={input} placeholder="name or name:value" disabled={busy} aria-label="New tag" />
          <button class="primary" type="submit" disabled={busy || !input.trim()}>Add</button>
        </form>
      {/if}
      {#if message}<div class="control-message bad" role="status">{message}</div>{/if}
    {/if}
  </section>
{/if}

<style>
  .tag-list { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; }
  .tag-chip { display: inline-flex; align-items: center; gap: 6px; }
  .tag-remove { padding: 0; border: 0; background: transparent; color: var(--muted); cursor: pointer; line-height: 1; }
  .tag-remove:hover:not(:disabled) { color: var(--bad); }
  .tag-add { display: flex; gap: 8px; }
  .tag-add input { flex: 1; padding: 6px 10px; border: 1px solid var(--line); border-radius: var(--radius-control); background: var(--panel); color: var(--ink); font: inherit; }
</style>
