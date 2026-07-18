<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import type { AdminControlResult, NamespaceListItem, Paged } from '../api.ts';
  import { api } from '../api.ts';
  import { capabilitiesQuery, canControl, keys } from '../query.ts';
  import { useControlMutation } from '../lib/useControlMutation.ts';
  import Page from '../components/Page.svelte';
  import Icon from '../components/Icon.svelte';
  import StateView from '../components/StateView.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import ChangeHistory from '../components/ChangeHistory.svelte';
  import TagEditor from '../components/TagEditor.svelte';
  import ConfirmAction from '../components/ConfirmAction.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import { mergeHistory, type HistoryEvent } from '../components/changeHistory.ts';
  import { scope } from '../scope.ts';
  import { buildNamespaceMetadataPayload, isSysNamespace, namespaceAdminNeedsReload } from './namespaceAdmin.ts';
  import { routes } from '../routes.ts';

  let { namespaceName }: { namespaceName: string } = $props();

  async function loadNamespace(signal: AbortSignal): Promise<NamespaceListItem | null> {
    let cursor: string | undefined;
    for (let guard = 0; guard < 100; guard++) {
      const page = await api<Paged<NamespaceListItem>>(
        'namespaces/admin',
        { nameStartsWith: namespaceName, pageSize: 100, cursor },
        { signal }
      );
      const match = page.items.find((item) => item.name === namespaceName);
      if (match) return match;
      if (!page.hasMore || !page.nextCursor) break;
      cursor = page.nextCursor;
    }
    return null;
  }

  // Editors never poll or refetch on focus/reconnect. Fresh server values are pulled only after a
  // successful metadata save, or when the operator explicitly accepts a conflict reload.
  const detail = createQuery(() => ({
    queryKey: keys.detail('namespace-detail', namespaceName),
    queryFn: ({ signal }) => loadNamespace(signal),
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false
  }));
  const capabilities = createQuery(() => capabilitiesQuery());

  // Admin audit trail: one small query per namespace admin event code, merged newest-first.
  const HISTORY_CODES = ['namespace.suspended', 'namespace.resumed', 'namespace.metadata-changed'];
  const history = createQuery(() => ({
    queryKey: keys.detail('namespace-history', namespaceName),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const pages = await Promise.all(
        HISTORY_CODES.map((eventCode) =>
          api<Paged<HistoryEvent>>('events', { jobNamespace: namespaceName, eventCode, pageSize: 20 }, { signal }).then(
            (page) => page.items
          )
        )
      );
      return mergeHistory(pages);
    }
  }));

  let namespace = $state<NamespaceListItem | null>(null);
  let ownerTeamInput = $state('');
  let descriptionInput = $state('');
  let metadataNote = $state('');

  // Reseed only when the detail query deliberately receives a fresh row. Local lifecycle updates
  // change `namespace` directly, so suspending/resuming cannot erase unsaved metadata input.
  $effect(() => {
    const fresh = detail.data;
    if (!fresh) return;
    namespace = fresh;
    ownerTeamInput = fresh.ownerTeam ?? '';
    descriptionInput = fresh.description ?? '';
    metadataNote = '';
  });

  let loading = $derived(detail.isPending);
  let error = $derived(detail.error instanceof Error ? detail.error.message : detail.error ? String(detail.error) : '');
  let canControlNow = $derived(canControl(capabilities.data));
  let systemNamespace = $derived(namespace ? isSysNamespace(namespace.id) : false);

  const metadataMutation = useControlMutation<
    { name: string; ownerTeam: string; description: string; expectedVersion: number; reason?: string },
    AdminControlResult
  >({
    path: (vars) => `namespaces/${encodeURIComponent(vars.name)}`,
    method: 'PATCH',
    body: (vars) => ({ expectedVersion: vars.expectedVersion, ...buildNamespaceMetadataPayload(vars) }),
    notFound: () => ({ action: 'notFound', version: null }),
    versionConflict: () => ({ action: 'versionConflict', version: null }),
    invalidateKeys: () => [['namespaces/admin']] as const
  });
  let metadataMessage = $state('');
  let metadataMessageKind = $state('');
  let metadataNeedsReload = $state(false);

  async function saveMetadata() {
    if (!namespace) return;
    metadataMessage = '';
    metadataNeedsReload = false;
    try {
      const result = await metadataMutation.mutateAsync({
        name: namespace.name,
        ownerTeam: ownerTeamInput,
        description: descriptionInput,
        expectedVersion: namespace.version,
        reason: metadataNote
      });
      if (namespaceAdminNeedsReload(result.action)) {
        metadataMessage =
          result.action === 'notFound'
            ? 'Namespace not found. Reload the current record before trying again.'
            : 'Changed since you loaded it. Reload the current values before trying again.';
        metadataMessageKind = 'warn';
        metadataNeedsReload = true;
        return;
      }
      metadataMessage = 'Metadata saved.';
      metadataMessageKind = 'ok';
      await detail.refetch();
      void history.refetch();
    } catch (e) {
      metadataMessage = e instanceof Error ? e.message : String(e);
      metadataMessageKind = 'bad';
    }
  }

  async function reloadMetadata() {
    await detail.refetch();
    metadataNeedsReload = false;
    metadataMessage = '';
  }

  const statusMutation = useControlMutation<
    { name: string; action: 'suspend' | 'resume'; reason?: string },
    AdminControlResult
  >({
    path: (vars) => `namespaces/${encodeURIComponent(vars.name)}/${vars.action}`,
    notFound: () => ({ action: 'notFound', version: null }),
    versionConflict: () => ({ action: 'versionConflict', version: null }),
    invalidateKeys: () => [['namespaces/admin']] as const
  });
  let confirming = $state<'suspend' | 'resume' | null>(null);
  let statusMessage = $state('');
  let statusMessageKind = $state('');

  async function runStatus(action: 'suspend' | 'resume', reason: string) {
    if (!namespace) return;
    confirming = null;
    statusMessage = '';
    try {
      const result = await statusMutation.mutateAsync({ name: namespace.name, action, reason });
      if (namespaceAdminNeedsReload(result.action)) {
        statusMessage = result.action === 'notFound' ? 'Namespace not found.' : 'Namespace changed; reload before trying again.';
        statusMessageKind = 'warn';
        return;
      }
      namespace = {
        ...namespace,
        status: action === 'suspend' ? 'suspended' : 'active',
        version: result.version ?? namespace.version
      };
      statusMessage = action === 'suspend' ? 'Namespace suspended.' : 'Namespace resumed.';
      statusMessageKind = 'ok';
      void history.refetch();
    } catch (e) {
      statusMessage = e instanceof Error ? e.message : String(e);
      statusMessageKind = 'bad';
    }
  }

  let backHref = $derived(routes.namespaces({ namespace: $scope }));
  let jobsHref = $derived(routes.jobs({ namespace: namespace?.name }));
</script>

<Page title={namespace?.name ?? 'Namespace'}>
  {#snippet breadcrumb()}
    <a href={backHref}><Icon name="chevron-left" />Namespaces</a>
  {/snippet}
  {#snippet actions()}
    <PageFreshness
      dataUpdatedAt={detail.dataUpdatedAt}
      isFetching={detail.isFetching}
      isError={!!detail.error}
      onRefresh={() => detail.refetch()} />
  {/snippet}

  {#if loading || error || !namespace}
    <div class="panel">
      <StateView
        {loading}
        {error}
        loadingText="Loading namespace..."
        emptyText="Namespace not found."
        onRetry={() => detail.refetch()} />
    </div>
  {:else}
    <section class="entity-summary" aria-label="Namespace identity">
      <div class="entity-meta mono">namespace #{namespace.id} · version {namespace.version}</div>
      <StatusBadge status={namespace.status} />
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel">
          <h2>Metadata</h2>
          {#if systemNamespace}
            <p class="detail-help">Seeded system namespace — protected, not editable.</p>
            <dl class="detail-readonly">
              <div><dt>Owner team</dt><dd>{namespace.ownerTeam ?? '—'}</dd></div>
              <div><dt>Description</dt><dd>{namespace.description ?? '—'}</dd></div>
            </dl>
          {:else}
            <p class="detail-help">Operator-owned context for this namespace. Its identity remains runtime-owned.</p>
            <form onsubmit={(event) => { event.preventDefault(); saveMetadata(); }}>
              <label class="detail-field">
                <span>Owner team</span>
                <input bind:value={ownerTeamInput} placeholder="Owning team identifier" disabled={!canControlNow || metadataMutation.isPending} />
              </label>
              <label class="detail-field">
                <span>Description</span>
                <textarea bind:value={descriptionInput} rows="4" placeholder="Operator-readable description" disabled={!canControlNow || metadataMutation.isPending}></textarea>
              </label>
              <label class="detail-field">
                <span>Audit note</span>
                <input bind:value={metadataNote} placeholder="Why are you changing this?" disabled={!canControlNow || metadataMutation.isPending} />
              </label>
              <div class="detail-form-actions">
                {#if metadataNeedsReload}<button type="button" onclick={reloadMetadata}>Reload current values</button>{/if}
                <button class="primary" type="submit" disabled={!canControlNow || metadataMutation.isPending}>
                  {metadataMutation.isPending ? 'Saving...' : 'Save metadata'}
                </button>
              </div>
            </form>
          {/if}
          {#if metadataMessage}<div class="control-message {metadataMessageKind}" role="status">{metadataMessage}</div>{/if}
        </section>

        <ChangeHistory history={history.data ?? []} loading={history.isPending} emptyText="No recorded namespace changes." />
      </div>

      <aside class="detail-rail">
        <section class="detail-panel">
          <h2>Lifecycle</h2>
          {#if systemNamespace}
            <p class="detail-help">The system namespace is always available and has no operator lifecycle controls.</p>
          {:else}
            <p>{namespace.status === 'suspended' ? 'Suspended; new enqueue requests are rejected.' : 'Active and accepting enqueue requests.'}</p>
            {#if canControlNow}
              <p class="detail-help">Suspending blocks new enqueue requests. Existing in-flight and queued jobs are unaffected.</p>
              {#if namespace.status === 'suspended'}
                <button disabled={statusMutation.isPending} onclick={() => (confirming = 'resume')}>Resume namespace</button>
              {:else}
                <button class="danger-outline" disabled={statusMutation.isPending} onclick={() => (confirming = 'suspend')}>Suspend namespace</button>
              {/if}
            {/if}
            {#if statusMessage}<div class="control-message {statusMessageKind}" role="status">{statusMessage}</div>{/if}
          {/if}
        </section>

        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={jobsHref}>Jobs</a>
            <a href={routes.workers({ namespace: namespace.name })}>Workers</a>
            <a href={routes.alerts({ namespace: namespace.name })}>Alerts</a>
          </nav>
        </section>

        <TagEditor path={`namespaces/${encodeURIComponent(namespace.name)}/tags`} />
      </aside>
    </div>
  {/if}
</Page>

{#if confirming && namespace}
  <ConfirmAction
    title={(confirming === 'suspend' ? 'Suspend' : 'Resume') + ' namespace ' + namespace.name + '?'}
    body={confirming === 'suspend'
      ? 'New enqueue requests will be rejected until the namespace is resumed. Existing jobs are unaffected.'
      : 'The namespace becomes eligible to enqueue jobs again immediately.'}
    confirmLabel={confirming === 'suspend' ? 'Suspend namespace' : 'Resume namespace'}
    onConfirm={(note) => runStatus(confirming!, note)}
    onCancel={() => (confirming = null)} />
{/if}
