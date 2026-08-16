<script lang="ts">
  import { createQuery } from '@tanstack/svelte-query';
  import { api, enqueueJob, ApiError, type JobInputTemplate, type JobPayloadView, type NamespaceListItem, type Paged } from '../api.ts';
  import { capabilitiesQuery, canControl, keys } from '../query.ts';
  import { hashParams } from '../router.ts';
  import { cloneInputState, enqueueInputFields, inputContractLabel, templateSeed, type EnqueueInputState } from './enqueueTemplate.ts';
  import { isSysNamespace } from './namespaceAdmin.ts';
  import { scope } from '../scope.ts';
  import { routes } from '../routes.ts';
  import Page from '../components/Page.svelte';
  import Icon from '../components/Icon.svelte';
  import PayloadView from '../components/PayloadView.svelte';
  import Dropdown from '../components/Dropdown.svelte';

  const initial = hashParams();
  const fromRef = initial.get('from');
  const priorities = ['', 'bulk', 'normal', 'high', 'critical', 'realtime'];

  let namespace = $state(initial.get('ns') ?? '');
  let jobName = $state(initial.get('jobName') ?? '');
  let includeInput = $state(false);
  // Format-faithful so a clone round-trips its stored format (v1: json and text). Manual enqueue keeps
  // the json default; a text clone flips it to text so the string is sent verbatim, never json-quoted.
  let input = $state<EnqueueInputState>({ format: 'json', json: {}, text: '' });
  let deduplicationKey = $state('');
  let correlationKey = $state('');
  let tenantKey = $state('');
  let priority = $state('');
  let delaySeconds = $state<number | null>(null);

  let busy = $state(false);
  let message = $state('');
  let messageKind = $state('');
  let createdRef = $state('');

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(canControl(capabilities.data));

  // Both identifiers are picked, never typed: enqueue resolves namespace and job name against the
  // registered catalog and rejects anything else, so free text could only produce a failed request.
  const namespacesQuery = createQuery(() => ({
    queryKey: keys.list('enqueue-namespaces', {}),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const all: NamespaceListItem[] = [];
      let cursor: string | undefined;
      // Bounded walk: the catalog is small, but a misbehaving cursor must never spin forever.
      for (let guard = 0; guard < 100; guard++) {
        const page = await api<Paged<NamespaceListItem>>('namespaces', { pageSize: 100, cursor }, { signal });
        all.push(...page.items);
        if (!page.hasMore || !page.nextCursor) break;
        cursor = page.nextCursor;
      }
      return all.filter((item) => !isSysNamespace(item.jobNamespace)).map((item) => item.jobNamespace);
    },
    enabled: canControlNow,
    staleTime: 60_000
  }));
  let namespaceNames = $derived(namespacesQuery.data ?? []);

  // Retired definitions reject enqueue, so the picker offers active ones only.
  const definitionsQuery = createQuery(() => {
    const jobNamespace = namespace.trim();
    return {
      queryKey: keys.list('enqueue-definitions', { jobNamespace }),
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        api<Paged<{ jobName: string }>>(
          'definitions',
          { jobNamespace, status: 'active', pageSize: 200 },
          { signal }
        ),
      enabled: canControlNow && jobNamespace.length > 0,
      staleTime: 60_000
    };
  });
  let definitionNames = $derived(definitionsQuery.data?.items.map((item) => item.jobName) ?? []);

  // Both catalogs are long and data-driven, so they get the filtering Dropdown rather than a native
  // select. A value carried in from the URL stays selectable while its catalog loads, which is why
  // each list re-adds the current value when the catalog has not caught up yet.
  let namespaceOptions = $derived([
    { value: '', label: 'Select a namespace' },
    ...(namespace && !namespaceNames.includes(namespace) ? [{ value: namespace, label: namespace }] : []),
    ...namespaceNames.map((name) => ({ value: name, label: name })),
  ]);
  let jobNameOptions = $derived([
    { value: '', label: namespace.trim().length === 0 ? 'Select a namespace first' : 'Select a job' },
    ...(jobName && !definitionNames.includes(jobName) ? [{ value: jobName, label: jobName }] : []),
    ...definitionNames.map((name) => ({ value: name, label: name })),
  ]);

  // Changing the namespace invalidates a job name from the previous one. Done on the event rather than
  // in an effect so a name prefilled from the URL (clone) survives the first load.
  function onNamespaceChange(value: string): void {
    namespace = value;
    jobName = '';
  }

  // Clone prefill: load the source job's stored input on its own, not off the detail aggregate.
  // Json/text seed the editor; none, a binary format, or a truncated (over-cap) input leaves the new
  // job without input (binary clone is out of scope for v1).
  const cloneQuery = createQuery(() => ({
    queryKey: keys.detail('enqueue-clone-input', fromRef ?? ''),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<JobPayloadView | null> => {
      try {
        return await api<JobPayloadView>(`jobs/${fromRef}/input`, {}, { signal });
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null;
        throw error;
      }
    },
    enabled: canControlNow && !!fromRef,
    staleTime: Infinity
  }));

  let cloneApplied = false;
  $effect(() => {
    const loaded = cloneQuery.data;
    if (!loaded || cloneApplied) return;
    cloneApplied = true;
    const seeded = cloneInputState(loaded);
    if (seeded) {
      input = seeded;
      includeInput = true;
    }
  });

  // Compile-time input shape from the generated manifest (no descriptor on this host -> nulls, and
  // the editor keeps its {} default). Serves both the contract line and the editor seed.
  const templateQuery = createQuery(() => {
    const jobNamespace = namespace.trim();
    const name = jobName.trim();
    return {
      queryKey: keys.detail('enqueue-input-template', `${jobNamespace}/${name}`),
      queryFn: ({ signal }: { signal: AbortSignal }) =>
        api<JobInputTemplate>('jobs/input-template', { jobNamespace, jobName: name }, { signal }),
      enabled: canControlNow && jobNamespace.length > 0 && name.length > 0,
      staleTime: Infinity
    };
  });
  let inputContract = $derived(inputContractLabel(templateQuery.data));

  let inputEdited = $state(false);
  $effect(() => {
    if (!includeInput) return;
    // Template seeding is json-only and never fights a text clone (templateSeed guards on clonePrefilled).
    const seed = templateSeed(templateQuery.data, { edited: inputEdited, clonePrefilled: cloneApplied });
    if (seed !== undefined) input.json = seed;
  });

  let inputPayload = $derived<JobPayloadView>(
    input.format === 'text'
      ? { formatName: 'text', formatId: 3, text: input.text }
      : { formatName: 'json', formatId: 1, json: input.json }
  );

  // PayloadView reports the edited draft; dispatch on the active format (a text clone stays text, a
  // json editor parses).
  function applyInput(value: string): void {
    inputEdited = true;
    if (input.format === 'text') input.text = value;
    else input.json = JSON.parse(value);
  }

  let canSubmit = $derived(canControlNow && !busy && namespace.trim().length > 0 && jobName.trim().length > 0);

  async function submit(): Promise<void> {
    if (!canSubmit) return;
    const inputFields = enqueueInputFields(includeInput, input);
    if ('error' in inputFields) {
      message = inputFields.error;
      messageKind = 'bad';
      createdRef = '';
      return;
    }
    busy = true;
    message = '';
    createdRef = '';
    try {
      const result = await enqueueJob({
        jobNamespace: namespace,
        jobName,
        ...inputFields.fields,
        deduplicationKey: deduplicationKey || null,
        correlationKey: correlationKey || null,
        tenantKey: tenantKey || null,
        priority: priority || null,
        delaySeconds: delaySeconds ?? null
      });
      createdRef = result.jobRef;
      message = result.action === 'deduplicated'
        ? 'A job with this deduplication key already exists; returning the existing job.'
        : 'Job enqueued.';
      messageKind = 'ok';
    } catch (error) {
      message = error instanceof Error ? error.message : String(error);
      messageKind = 'bad';
    } finally {
      busy = false;
    }
  }

  let backHref = $derived(routes.jobs({ namespace: $scope }));
  let createdHref = $derived(createdRef ? routes.job(createdRef, { namespace: namespace.trim() }) : backHref);
</script>

<Page title="Enqueue job">
  {#snippet breadcrumb()}<a href={backHref}><Icon name="chevron-left" />Jobs</a>{/snippet}

  {#if !canControlNow}
    <div class="panel"><p class="dim">Enqueue is a control action and is disabled on this host.</p></div>
  {:else}
    <div class="detail-workspace">
      <div class="detail-main">
        <section class="detail-panel">
          <h2>Job</h2>
          <p class="detail-help">Enqueue a new job. The namespace and job name identify a registered definition; the input is stored as raw JSON and handed to the job on its first attempt.</p>
          <form onsubmit={(event) => { event.preventDefault(); submit(); }}>
            <div class="detail-field">
              <span>Namespace</span>
              <Dropdown
                label="Namespace"
                placeholder="Select a namespace"
                options={namespaceOptions}
                value={namespace}
                disabled={busy}
                onchange={onNamespaceChange} />
            </div>
            <div class="detail-field">
              <span>Job name</span>
              <Dropdown
                label="Job name"
                placeholder={namespace.trim().length === 0 ? 'Select a namespace first' : 'Select a job'}
                options={jobNameOptions}
                bind:value={jobName}
                disabled={busy || namespace.trim().length === 0} />
              {#if inputContract}<span class="field-hint">{inputContract}</span>{/if}
            </div>

            <label class="detail-field checkbox">
              <input type="checkbox" bind:checked={includeInput} disabled={busy} />
              <span>Provide input{input.format === 'json' ? ' (JSON)' : ''}</span>
            </label>
            {#if includeInput}
              <div class="enqueue-input"><PayloadView payload={inputPayload} editable={!busy} onSave={applyInput} /></div>
            {/if}

            <label class="detail-field">
              <span>Deduplication key</span>
              <input bind:value={deduplicationKey} maxlength="256" placeholder="Optional; a matching key returns the existing job" disabled={busy} />
            </label>
            <label class="detail-field">
              <span>Correlation key</span>
              <input bind:value={correlationKey} maxlength="256" placeholder="Optional trace / request / order id" disabled={busy} />
            </label>
            <label class="detail-field">
              <span>Tenant key</span>
              <input bind:value={tenantKey} maxlength="128" placeholder="Optional tenant key" disabled={busy} />
            </label>
            <label class="detail-field">
              <span>Priority</span>
              <select bind:value={priority} disabled={busy}>
                {#each priorities as p}<option value={p}>{p === '' ? 'Definition default' : p}</option>{/each}
              </select>
            </label>
            <label class="detail-field">
              <span>Delay (seconds)</span>
              <input bind:value={delaySeconds} type="number" min="0" placeholder="Optional; hold before the first run" disabled={busy} />
            </label>

            <div class="detail-form-actions">
              <button class="primary" type="submit" disabled={!canSubmit}>{busy ? 'Enqueuing...' : 'Enqueue job'}</button>
            </div>
          </form>
          {#if message}
            <div class="control-message {messageKind}" role="status">
              {message}
              {#if createdRef}<a href={createdHref}>Open job <span aria-hidden="true">-&gt;</span></a>{/if}
            </div>
          {/if}
        </section>
      </div>

      <aside class="detail-rail">
        <section class="detail-panel">
          <h2>Notes</h2>
          <p class="detail-help">A deduplication key makes the enqueue idempotent: a repeat with the same key returns the existing job instead of inserting a new one. Priority and delay default to the definition policy when left unset.</p>
        </section>
      </aside>
    </div>
  {/if}
</Page>

<style>
  .detail-field.checkbox { grid-template-columns: auto 1fr; align-items: center; }
  .detail-field.checkbox input { justify-self: start; }
  .enqueue-input { margin-top: 12px; }
  .field-hint { color: var(--muted); font-size: var(--text-xs); font-weight: 400; }
</style>
