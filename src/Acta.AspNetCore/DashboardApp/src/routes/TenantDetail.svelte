<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import type { AdminControlResult, Paged, TenantListItem } from '../api.ts';
  import { api, ApiError, registerTenant } from '../api.ts';
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
  import { buildTenantMetadataPayload, tenantAdminNeedsReload } from './tenantAdmin.ts';
  import { routes } from '../routes.ts';

  let { tenantKey = null }: { tenantKey?: string | null } = $props();
  let creating = $derived(!tenantKey);
  let newKeyEl: HTMLInputElement | null = $state(null);

  $effect(() => {
    if (creating) newKeyEl?.focus();
  });

  async function loadTenant(signal: AbortSignal): Promise<TenantListItem | null> {
    if (!tenantKey) return null;
    try {
      return await api<TenantListItem>(`tenants/${encodeURIComponent(tenantKey)}`, {}, { signal });
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  }

  // The editor owns its input state: no polling and no focus/reconnect refreshes. It deliberately
  // refetches only after a successful metadata save or an explicit conflict reload.
  const detail = createQuery(() => ({
    queryKey: keys.detail('tenant-detail', tenantKey ?? 'new'),
    queryFn: ({ signal }) => loadTenant(signal),
    enabled: !creating,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false
  }));
  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  let tenant = $state<TenantListItem | null>(null);

  // Admin audit trail: one small query per tenant admin event code, merged newest-first.
  const HISTORY_CODES = ['tenant.suspended', 'tenant.resumed', 'tenant.metadata-changed'];
  const history = createQuery(() => ({
    queryKey: keys.detail('tenant-history', `${tenant?.tenantId ?? 0}`),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const pages = await Promise.all(
        HISTORY_CODES.map((eventCode) =>
          api<Paged<HistoryEvent>>('events', { tenantId: tenant!.tenantId, eventCode, pageSize: 20 }, { signal }).then(
            (page) => page.items
          )
        )
      );
      return mergeHistory(pages);
    },
    enabled: !!tenant
  }));

  let displayNameInput = $state('');
  let descriptionInput = $state('');
  let metadataNote = $state('');

  $effect(() => {
    const fresh = detail.data;
    if (!fresh) return;
    tenant = fresh;
    displayNameInput = fresh.displayName ?? '';
    descriptionInput = fresh.description ?? '';
    metadataNote = '';
  });

  let loading = $derived(!creating && detail.isPending);
  let error = $derived(detail.error instanceof Error ? detail.error.message : detail.error ? String(detail.error) : '');

  let newKey = $state('');
  let newDisplayName = $state('');
  let newDescription = $state('');
  let registerBusy = $state(false);
  let registerMessage = $state('');
  let registerMessageKind = $state('');
  let createdKey = $state('');

  async function submitRegister() {
    if (!newKey.trim()) return;
    registerBusy = true;
    registerMessage = '';
    createdKey = '';
    try {
      const result = await registerTenant(newKey, newDisplayName, newDescription);
      createdKey = result.tenantKey;
      registerMessage = `Tenant ${result.tenantKey} registered.`;
      registerMessageKind = 'ok';
      newKey = '';
      newDisplayName = '';
      newDescription = '';
    } catch (e) {
      registerMessage = e instanceof Error ? e.message : String(e);
      registerMessageKind = 'bad';
    } finally {
      registerBusy = false;
    }
  }

  const metadataMutation = useControlMutation<
    { tenantKey: string; displayName: string; description: string; expectedVersion: number; reason?: string },
    AdminControlResult
  >({
    path: (vars) => `tenants/${encodeURIComponent(vars.tenantKey)}`,
    method: 'PATCH',
    body: (vars) => ({ expectedVersion: vars.expectedVersion, ...buildTenantMetadataPayload(vars) }),
    notFound: () => ({ action: 'notFound', version: null }),
    versionConflict: () => ({ action: 'versionConflict', version: null }),
    invalidateKeys: () => [['tenants']] as const
  });
  let metadataMessage = $state('');
  let metadataMessageKind = $state('');
  let metadataNeedsReload = $state(false);

  async function saveMetadata() {
    if (!tenant) return;
    metadataMessage = '';
    metadataNeedsReload = false;
    try {
      const result = await metadataMutation.mutateAsync({
        tenantKey: tenant.tenantKey,
        displayName: displayNameInput,
        description: descriptionInput,
        expectedVersion: tenant.version,
        reason: metadataNote
      });
      if (tenantAdminNeedsReload(result.action)) {
        metadataMessage =
          result.action === 'notFound'
            ? 'Tenant not found. Reload the current record before trying again.'
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
    { tenantKey: string; action: 'suspend' | 'resume'; reason?: string },
    AdminControlResult
  >({
    path: (vars) => `tenants/${encodeURIComponent(vars.tenantKey)}/${vars.action}`,
    notFound: () => ({ action: 'notFound', version: null }),
    versionConflict: () => ({ action: 'versionConflict', version: null }),
    invalidateKeys: () => [['tenants']] as const
  });
  let confirming = $state<'suspend' | 'resume' | null>(null);
  let statusMessage = $state('');
  let statusMessageKind = $state('');

  async function runStatus(action: 'suspend' | 'resume', reason: string) {
    if (!tenant) return;
    confirming = null;
    statusMessage = '';
    try {
      const result = await statusMutation.mutateAsync({ tenantKey: tenant.tenantKey, action, reason });
      if (tenantAdminNeedsReload(result.action)) {
        statusMessage = result.action === 'notFound' ? 'Tenant not found.' : 'Tenant changed; reload before trying again.';
        statusMessageKind = 'warn';
        return;
      }
      tenant = {
        ...tenant,
        status: action === 'suspend' ? 'suspended' : 'active',
        version: result.version ?? tenant.version
      };
      statusMessage = action === 'suspend' ? 'Tenant suspended.' : 'Tenant resumed.';
      statusMessageKind = 'ok';
      void history.refetch();
    } catch (e) {
      statusMessage = e instanceof Error ? e.message : String(e);
      statusMessageKind = 'bad';
    }
  }

  let backHref = $derived(routes.tenants({ namespace: $scope }));
  let createdHref = $derived(
    createdKey
      ? routes.tenant(createdKey, { namespace: $scope })
      : routes.tenants({ namespace: $scope })
  );
  let jobsHref = $derived(routes.jobs({ tenantId: tenant?.tenantId, namespace: $scope }));
</script>

<Page title={creating ? 'Register tenant' : tenant?.displayName ?? tenant?.tenantKey ?? 'Tenant'}>
  {#snippet breadcrumb()}
    <a href={backHref}><Icon name="chevron-left" />Tenants</a>
  {/snippet}
  {#if !creating}
    {#snippet actions()}
      <PageFreshness
        dataUpdatedAt={detail.dataUpdatedAt}
        isFetching={detail.isFetching}
        isError={!!detail.error}
        onRefresh={() => detail.refetch()} />
    {/snippet}
  {/if}

  {#if creating}
    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel">
          <h2>Registration</h2>
          <p class="detail-help">Create the stable tenant identity used by enqueue requests. Display metadata can be added afterward.</p>
          <form onsubmit={(event) => { event.preventDefault(); submitRegister(); }}>
            <label class="detail-field">
              <span>Tenant key</span>
              <input bind:this={newKeyEl} bind:value={newKey} maxlength="128" placeholder="Opaque key, GUID, or customer code" disabled={!canControlNow || registerBusy} />
            </label>
            <label class="detail-field">
              <span>Display name</span>
              <input bind:value={newDisplayName} maxlength="128" placeholder="Human display label (optional)" disabled={!canControlNow || registerBusy} />
            </label>
            <label class="detail-field">
              <span>Description</span>
              <textarea bind:value={newDescription} rows="4" maxlength="512" placeholder="Human-readable context (optional)" disabled={!canControlNow || registerBusy}></textarea>
            </label>
            <div class="detail-form-actions">
              <button class="primary" type="submit" disabled={!canControlNow || registerBusy || !newKey.trim()}>
                {registerBusy ? 'Registering...' : 'Register tenant'}
              </button>
            </div>
          </form>
          {#if registerMessage}
            <div class="control-message {registerMessageKind}" role="status">
              {registerMessage}
              {#if createdKey}<a href={createdHref}>Open tenant <span aria-hidden="true">→</span></a>{/if}
            </div>
          {/if}
        </section>
      </div>
      <aside class="detail-rail">
        <section class="detail-panel">
          <h2>What happens next</h2>
          <p class="detail-help">The tenant is registered as active. Open its detail view to edit metadata or suspend enqueue access.</p>
        </section>
      </aside>
    </div>
  {:else if loading || error || !tenant}
    <div class="panel">
      <StateView {loading} {error} loadingText="Loading tenant..." emptyText="Tenant not found." onRetry={() => detail.refetch()} />
    </div>
  {:else}
    <section class="entity-summary" aria-label="Tenant identity">
      <div class="entity-meta mono">tenant #{tenant.tenantId} · {tenant.tenantKey} · version {tenant.version}</div>
      <StatusBadge status={tenant.status} />
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel">
          <h2>Metadata</h2>
          <p class="detail-help">Operator-owned context for this tenant. The tenant key is stable and cannot be edited.</p>
          <form onsubmit={(event) => { event.preventDefault(); saveMetadata(); }}>
            <label class="detail-field">
              <span>Display name</span>
              <input bind:value={displayNameInput} maxlength="128" placeholder="Human display label" disabled={!canControlNow || metadataMutation.isPending} />
            </label>
            <label class="detail-field">
              <span>Description</span>
              <textarea bind:value={descriptionInput} rows="4" maxlength="512" placeholder="Operator-readable description" disabled={!canControlNow || metadataMutation.isPending}></textarea>
            </label>
            <label class="detail-field">
              <span>Audit note</span>
              <input bind:value={metadataNote} maxlength="512" placeholder="Why are you changing this?" disabled={!canControlNow || metadataMutation.isPending} />
            </label>
            <div class="detail-form-actions">
              {#if metadataNeedsReload}<button type="button" onclick={reloadMetadata}>Reload current values</button>{/if}
              <button class="primary" type="submit" disabled={!canControlNow || metadataMutation.isPending}>
                {metadataMutation.isPending ? 'Saving...' : 'Save metadata'}
              </button>
            </div>
          </form>
          {#if metadataMessage}<div class="control-message {metadataMessageKind}" role="status">{metadataMessage}</div>{/if}
        </section>

        <ChangeHistory history={history.data ?? []} loading={history.isPending} emptyText="No recorded tenant changes." />
      </div>

      <aside class="detail-rail">
        <section class="detail-panel">
          <h2>Lifecycle</h2>
          <p>{tenant.status === 'suspended' ? 'Suspended; new enqueues naming this tenant are rejected.' : 'Active and accepting enqueue requests.'}</p>
          {#if canControlNow}
            <p class="detail-help">Suspending rejects new enqueues that name this tenant key. Jobs already admitted keep running and may still create inherited child jobs.</p>
            {#if tenant.status === 'suspended'}
              <button disabled={statusMutation.isPending} onclick={() => (confirming = 'resume')}>Resume tenant</button>
            {:else}
              <button class="danger-outline" disabled={statusMutation.isPending} onclick={() => (confirming = 'suspend')}>Suspend tenant</button>
            {/if}
          {/if}
          {#if statusMessage}<div class="control-message {statusMessageKind}" role="status">{statusMessage}</div>{/if}
        </section>

        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={jobsHref}>Jobs</a>
          </nav>
        </section>

        <TagEditor path={`tenants/${encodeURIComponent(tenant.tenantKey)}/tags`} />
      </aside>
    </div>
  {/if}
</Page>

{#if confirming && tenant}
  <ConfirmAction
    title={(confirming === 'suspend' ? 'Suspend' : 'Resume') + ' tenant ' + tenant.tenantKey + '?'}
    body={confirming === 'suspend'
      ? 'New enqueues naming this tenant key will be rejected until the tenant is resumed. Jobs already admitted keep running and may still create inherited child jobs.'
      : 'The tenant becomes eligible to enqueue jobs again immediately.'}
    confirmLabel={confirming === 'suspend' ? 'Suspend tenant' : 'Resume tenant'}
    onConfirm={(note) => runStatus(confirming!, note)}
    onCancel={() => (confirming = null)} />
{/if}
