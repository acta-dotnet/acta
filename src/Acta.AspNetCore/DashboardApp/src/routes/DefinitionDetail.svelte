<script>
  import { createQuery } from '@tanstack/svelte-query';
  import { api, setDefinitionOverrides } from '../api';
  import { keys } from '../query';
  import Page from '../components/Page.svelte';
  import Icon from '../components/Icon.svelte';
  import StateView from '../components/StateView.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import ChangeHistory from '../components/ChangeHistory.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import TagEditor from '../components/TagEditor.svelte';
  import { routes } from '../routes';
  import { displayFormatter } from '../format';

  let { defId } = $props();

  // The override slots, in the entity's policy order. `key` matches both the list item field prefix
  // (key / keyOverride / keyEffective) and the PATCH payload field. `kind` drives parsing on save.
  const FIELDS = [
    { key: 'priority', label: 'Priority', kind: 'enum' },
    { key: 'maxAttempts', label: 'Max attempts', kind: 'int' },
    { key: 'backoff', label: 'Backoff', kind: 'str' },
    { key: 'executionTimeoutSeconds', label: 'Exec timeout (s)', kind: 'int' },
    { key: 'deadlineSeconds', label: 'Deadline (s)', kind: 'int' },
    { key: 'deadlineBehavior', label: 'Deadline behavior', kind: 'enum' },
    { key: 'jobRetentionSeconds', label: 'Retention (s)', kind: 'int' },
    { key: 'auditLevel', label: 'Audit level', kind: 'enum' },
    { key: 'alertProfile', label: 'Alert profile', kind: 'enum' },
    { key: 'alertChannelName', label: 'Alert channel', kind: 'str' },
    { key: 'runbookUrl', label: 'Runbook URL', kind: 'str' },
    { key: 'displayName', label: 'Display name', kind: 'str' },
    { key: 'description', label: 'Description', kind: 'str' }
  ];

  // An editor must never have its in-progress inputs clobbered by a background refresh, so this
  // query neither polls nor refetches on window focus or network reconnect; it refetches only after a successful save.
  const detail = createQuery(() => ({
    queryKey: keys.detail('definitions', defId),
    queryFn: async ({ signal }) => {
      // Single-by-id read (GET /api/definitions/{id}); a 404 surfaces its "Definition not found." title.
      const def = await api('definitions/' + encodeURIComponent(defId), {}, { signal });
      let history = [];
      try {
        history = (await api('definitions/' + def.jobDefinitionId + '/events', { pageSize: 20 }, { signal })).items;
      } catch (e) {
        if (e?.name === 'AbortError') throw e;
        // history is best-effort; the editor still works without it
      }
      return { def, history };
    },
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false
  }));

  let def = $derived(detail.data?.def ?? null);
  let history = $derived(detail.data?.history ?? []);
  let loading = $derived(detail.isPending);
  let error = $derived(detail.error ? (detail.error instanceof Error ? detail.error.message : String(detail.error)) : '');

  let saving = $state(false);
  let message = $state('');
  let messageKind = $state('');
  let note = $state('');
  // The editable override inputs, keyed by field key. '' = inherit (clear the override).
  let inputs = $state({});

  // Reseed whenever a fresh definition arrives (initial load and post-save refetch).
  $effect(() => {
    if (def) seedInputs(def);
  });

  function seedInputs(d) {
    const next = {};
    for (const f of FIELDS) {
      const v = d[f.key + 'Override'];
      next[f.key] = v === null || v === undefined ? '' : String(v);
    }
    inputs = next;
  }

  function parse(kind, raw) {
    const s = raw.trim();
    if (s === '') return null; // clear -> inherit default
    if (kind === 'int') {
      const n = parseInt(s, 10);
      return Number.isNaN(n) ? null : n;
    }
    if (kind === 'dec') {
      const n = Number(s);
      return Number.isNaN(n) ? null : n;
    }
    return s; // enum (kebab wire name) or string
  }

  function displayPolicyValue(field, value) {
    if (value === null || value === undefined) return '·';
    return (field.kind === 'int' || field.kind === 'dec') && typeof value === 'number' ? displayFormatter.number(value) : value;
  }

  async function save() {
    if (!def) return;
    saving = true;
    message = '';
    const overrides = {};
    for (const f of FIELDS) {
      overrides[f.key] = parse(f.kind, inputs[f.key] ?? '');
    }
    try {
      const res = await setDefinitionOverrides(def.jobDefinitionId, def.version, overrides, note);
      message = res.message;
      messageKind = res.action === 'applied' ? 'ok' : 'warn';
      if (res.action === 'applied') {
        note = '';
        await detail.refetch(); // refresh version + effective values
      }
    } catch (e) {
      message = e instanceof Error ? e.message : String(e);
      messageKind = 'warn';
    } finally {
      saving = false;
    }
  }

  function clearAll() {
    const next = {};
    for (const f of FIELDS) next[f.key] = '';
    inputs = next;
  }

  const jobsHref = (d) => routes.jobs({ jobName: d.jobName, namespace: d.jobNamespace });
</script>

<Page title={def ? def.jobName : 'Definition'}>
  {#snippet breadcrumb()}
    <a href={routes.definitions()}><Icon name="chevron-left" />Definitions</a>
  {/snippet}
  {#snippet actions()}
    <PageFreshness
      dataUpdatedAt={detail.dataUpdatedAt}
      isFetching={detail.isFetching}
      isError={!!detail.error}
      onRefresh={() => detail.refetch()} />
  {/snippet}

  {#if loading || error || !def}
    <div class="panel">
      <StateView {loading} {error} loadingText="Loading definition..." emptyText="Definition not found." onRetry={() => detail.refetch()} />
    </div>
  {:else}
    <section class="entity-summary" aria-label="Definition identity">
      <div class="entity-meta mono">definition #{def.jobDefinitionId} · {def.jobNamespace} / {def.jobName} · version {def.version}</div>
      <StatusBadge status={def.status} />
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel">
          <h2>Policy overrides</h2>
          <p class="detail-help">
            Each policy field shows the code default, operator override, and effective value. Blank overrides inherit the default;
            identity, contract, and formats remain code-owned.
          </p>

          <div class="policy-wrap">
            <table class="policy">
              <caption class="sr-only">Definition policy defaults, overrides, and effective values</caption>
              <thead>
                <tr><th>Policy field</th><th>Default</th><th>Override</th><th>Effective</th></tr>
              </thead>
              <tbody>
                {#each FIELDS as f}
                  <tr>
                    <td>{f.label}</td>
                    <td class="mono dim">{displayPolicyValue(f, def[f.key])}</td>
                    <td><input class="ovr-in" bind:value={inputs[f.key]} placeholder="inherit" /></td>
                    <td class="mono">{displayPolicyValue(f, def[f.key + 'Effective'])}</td>
                  </tr>
                {/each}
              </tbody>
            </table>
          </div>

          <label class="detail-field">
            <span>Audit note</span>
            <input bind:value={note} placeholder="Why are you changing this?" disabled={saving} />
          </label>
          <div class="detail-form-actions">
            <button class="primary" onclick={save} disabled={saving}>{saving ? 'Saving...' : 'Save overrides'}</button>
            <button onclick={clearAll} disabled={saving}>Clear all</button>
          </div>
          {#if message}<div class="control-message {messageKind}" role="status">{message}</div>{/if}
        </section>

        <ChangeHistory {history} emptyText="No recorded policy changes." />
      </div>

      <aside class="detail-rail">
        <section class="detail-panel">
          <h2>Definition state</h2>
          <p>{def.status}</p>
          <p class="detail-help">Registration state and contract identity are owned by the deployed job manifest. Operator policy overrides remain editable on the left.</p>
        </section>

        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={jobsHref(def)}>Jobs</a>
          </nav>
        </section>

        <TagEditor path={`definitions/${def.jobDefinitionId}/tags`} />

        <section class="detail-panel">
          <h2>Contract</h2>
          <dl class="detail-readonly">
            <div><dt>Input</dt><dd class="mono">{def.inputTypeName ?? '·'}</dd></div>
            <div><dt>Output</dt><dd class="mono">{def.outputTypeName ?? '·'}</dd></div>
          </dl>
        </section>
      </aside>
    </div>
  {/if}
</Page>

<style>
  .policy-wrap { overflow-x: auto; }

  table.policy { width: 100%; border-collapse: collapse; }
  table.policy th {
    text-align: left; padding: 6px 8px; color: var(--muted); font-weight: 600;
    text-transform: uppercase; letter-spacing: 0.04em; font-size: var(--text-sm);
    box-shadow: 0 1px 0 var(--line);
  }
  table.policy td { text-align: left; padding: 7px 8px; border-bottom: 1px solid var(--line); vertical-align: middle; }
  table.policy tr:last-child td { border-bottom: none; }
  .dim { color: var(--muted); }

  /* Inline override inputs sit in the policy table cells, so they keep a compact local style rather
     than the labeled .detail-field grid the rest of the form uses. */
  .ovr-in {
    box-sizing: border-box; width: 100%;
    padding: 6px 10px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel);
    color: var(--ink);
    font: inherit;
  }
  .ovr-in:hover { border-color: var(--accent); }
  .ovr-in:focus { outline: none; border-color: var(--accent); }
  .ovr-in::placeholder { color: var(--muted); }
</style>
