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
  import { buildNamespaceDetailsPayload, isSysNamespace, namespaceAdminNeedsReload } from './namespaceAdmin.ts';
  import { routes } from '../routes.ts';

  let { namespaceName }: { namespaceName: string } = $props();

  async function loadNamespace(signal: AbortSignal): Promise<NamespaceListItem | null> {
    let cursor: string | undefined;
    for (let guard = 0; guard < 100; guard++) {
      const page = await api<Paged<NamespaceListItem>>(
        'namespaces',
        { nameContains: namespaceName, pageSize: 100, cursor },
        { signal }
      );
      const match = page.items.find((item) => item.jobNamespace === namespaceName);
      if (match) return match;
      if (!page.hasMore || !page.nextCursor) break;
      cursor = page.nextCursor;
    }
    return null;
  }

  // Editors never poll or refetch on focus/reconnect. Fresh server values are pulled only after a
  // successful details save, or when the operator explicitly accepts a conflict reload.
  const detail = createQuery(() => ({
    queryKey: keys.detail('namespace-detail', namespaceName),
    queryFn: ({ signal }) => loadNamespace(signal),
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false
  }));
  const capabilities = createQuery(() => capabilitiesQuery());

  // Admin audit trail: one small query per namespace admin event code, merged newest-first.
  const HISTORY_CODES = ['namespace.suspended', 'namespace.resumed', 'namespace.updated'];
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
  let detailsNote = $state('');

  // Reseed only when the detail query deliberately receives a fresh row. Local lifecycle updates
  // change `namespace` directly, so suspending/resuming cannot erase unsaved details input.
  $effect(() => {
    const fresh = detail.data;
    if (!fresh) return;
    namespace = fresh;
    ownerTeamInput = fresh.ownerTeam ?? '';
    descriptionInput = fresh.description ?? '';
    detailsNote = '';
  });

  let loading = $derived(detail.isPending);
  let error = $derived(detail.error instanceof Error ? detail.error.message : detail.error ? String(detail.error) : '');
  let canControlNow = $derived(canControl(capabilities.data));
  let systemNamespace = $derived(namespace ? isSysNamespace(namespace.jobNamespace) : false);

  const detailsMutation = useControlMutation<
    { name: string; ownerTeam: string; description: string; expectedVersion: number; reason?: string },
    AdminControlResult
  >({
    path: (vars) => `namespaces/${encodeURIComponent(vars.name)}`,
    method: 'PATCH',
    body: (vars) => ({ expectedVersion: vars.expectedVersion, ...buildNamespaceDetailsPayload(vars) }),
    notFound: () => ({ action: 'notFound', version: null }),
    versionConflict: () => ({ action: 'versionConflict', version: null }),
    invalidateKeys: () => [['namespaces']] as const
  });
  let detailsMessage = $state('');
  let detailsMessageKind = $state('');
  let detailsNeedsReload = $state(false);

  async function saveDetails() {
    if (!namespace) return;
    detailsMessage = '';
    detailsNeedsReload = false;
    try {
      const result = await detailsMutation.mutateAsync({
        name: namespace.jobNamespace,
        ownerTeam: ownerTeamInput,
        description: descriptionInput,
        expectedVersion: namespace.version,
        reason: detailsNote
      });
      if (namespaceAdminNeedsReload(result.action)) {
        detailsMessage =
          result.action === 'notFound'
            ? 'Namespace not found. Reload the current record before trying again.'
            : 'Changed since you loaded it. Reload the current values before trying again.';
        detailsMessageKind = 'warn';
        detailsNeedsReload = true;
        return;
      }
      detailsMessage = 'Details saved.';
      detailsMessageKind = 'ok';
      await detail.refetch();
      void history.refetch();
    } catch (e) {
      detailsMessage = e instanceof Error ? e.message : String(e);
      detailsMessageKind = 'bad';
    }
  }

  async function reloadDetails() {
    await detail.refetch();
    detailsNeedsReload = false;
    detailsMessage = '';
  }

  const statusMutation = useControlMutation<
    { name: string; action: 'suspend' | 'resume'; reason?: string },
    AdminControlResult
  >({
    path: (vars) => `namespaces/${encodeURIComponent(vars.name)}/${vars.action}`,
    notFound: () => ({ action: 'notFound', version: null }),
    versionConflict: () => ({ action: 'versionConflict', version: null }),
    invalidateKeys: () => [['namespaces']] as const
  });
  let confirming = $state<'suspend' | 'resume' | null>(null);
  let statusMessage = $state('');
  let statusMessageKind = $state('');

  async function runStatus(action: 'suspend' | 'resume', reason: string) {
    if (!namespace) return;
    confirming = null;
    statusMessage = '';
    try {
      const result = await statusMutation.mutateAsync({ name: namespace.jobNamespace, action, reason });
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
      <div class="entity-meta mono">{namespace.jobNamespace} · version {namespace.version}</div>
      <StatusBadge status={namespace.status} />
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel">
          <h2>Details</h2>
          {#if systemNamespace}
            <p class="detail-help">Seeded system namespace: protected, not editable.</p>
            <dl class="detail-readonly">
              <div><dt>Owner team</dt><dd>{namespace.ownerTeam ?? '·'}</dd></div>
              <div><dt>Description</dt><dd>{namespace.description ?? '·'}</dd></div>
            </dl>
          {:else}
            <p class="detail-help">Operator-owned context for this namespace. Its identity remains runtime-owned.</p>
            <form onsubmit={(event) => { event.preventDefault(); saveDetails(); }}>
              <label class="detail-field">
                <span>Owner team</span>
                <input bind:value={ownerTeamInput} placeholder="Owning team identifier" disabled={!canControlNow || detailsMutation.isPending} />
              </label>
              <label class="detail-field">
                <span>Description</span>
                <textarea bind:value={descriptionInput} rows="4" placeholder="Operator-readable description" disabled={!canControlNow || detailsMutation.isPending}></textarea>
              </label>
              <label class="detail-field">
                <span>Audit note</span>
                <input bind:value={detailsNote} placeholder="Why are you changing this?" disabled={!canControlNow || detailsMutation.isPending} />
              </label>
              <div class="detail-form-actions">
                {#if detailsNeedsReload}<button type="button" onclick={reloadDetails}>Reload current values</button>{/if}
                <button class="primary" type="submit" disabled={!canControlNow || detailsMutation.isPending}>
                  {detailsMutation.isPending ? 'Saving...' : 'Save details'}
                </button>
              </div>
            </form>
          {/if}
          {#if detailsMessage}<div class="control-message {detailsMessageKind}" role="status">{detailsMessage}</div>{/if}
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
            <a href={routes.workers({ namespace: namespace.jobNamespace })}>Workers</a>
            <a href={routes.alerts({ namespace: namespace.jobNamespace })}>Alerts</a>
          </nav>
        </section>

        <TagEditor path={`namespaces/${encodeURIComponent(namespace.jobNamespace)}/tags`} />
      </aside>
    </div>
  {/if}
</Page>

{#if confirming && namespace}
  <ConfirmAction
    title={(confirming === 'suspend' ? 'Suspend' : 'Resume') + ' namespace ' + namespace.jobNamespace + '?'}
    body={confirming === 'suspend'
      ? 'New enqueue requests will be rejected until the namespace is resumed. Existing jobs are unaffected.'
      : 'The namespace becomes eligible to enqueue jobs again immediately.'}
    confirmLabel={confirming === 'suspend' ? 'Suspend namespace' : 'Resume namespace'}
    onConfirm={(note) => runStatus(confirming!, note)}
    onCancel={() => (confirming = null)} />
{/if}
