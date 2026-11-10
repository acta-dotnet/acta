<script lang="ts">
  import { tick, untrack } from 'svelte';
  import { createQuery } from '@tanstack/svelte-query';
  import { api, ApiError, type JobDetailView } from '../../api.ts';
  import { keys } from '../../query.ts';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import StatusBadge from '../../components/StatusBadge.svelte';
  import StateView from '../../components/StateView.svelte';
  import Icon from '../../components/Icon.svelte';
  import JobRef from '../../components/JobRef.svelte';
  import type { JobLineage, JobDetail } from './types.ts';
  import { childRollup } from './jobDetailState.ts';
  import { routes } from '../../routes.ts';
  import { TERMINAL_STATUSES } from '../../format.ts';

  let { job, lineage = null }: { job: JobDetail; lineage?: JobLineage | null } = $props();
  const inspectorId = $props.id();
  let branchRef = $state<string | null>(null);
  let selectedRef = $state<string | null>(null);
  let filter = $state('all');
  let showAll = $state(false);

  // Siblings often share a job name (a fan-out starts many of one kind), so each node also carries the compact
  // reference JobRef shows; JobRef itself is interactive and cannot sit inside the node's button.
  const compactRef = (ref: string) => {
    const match = /^([a-z0-9]+)_[0-9a-f]{24}([0-9a-f]{8})$/i.exec(ref);
    return match ? match[1] + '…' + match[2] : ref;
  };

  async function load(ref: string, signal: AbortSignal): Promise<JobDetailView | null> {
    try { return await api<JobDetailView>('jobs/' + ref + '/detail', {}, { signal }); }
    catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  }
  const branchQuery = createQuery(() => ({
    queryKey: keys.detail('jobs', branchRef ?? job.jobRef),
    queryFn: ({ signal }: { signal: AbortSignal }) => load(branchRef ?? job.jobRef, signal),
    enabled: !!branchRef && branchRef !== job.jobRef
  }));
  const selectedQuery = createQuery(() => ({
    queryKey: keys.detail('jobs', selectedRef ?? job.jobRef),
    queryFn: ({ signal }: { signal: AbortSignal }) => load(selectedRef ?? job.jobRef, signal),
    enabled: !!selectedRef
  }));

  let current = $derived(!branchRef || branchRef === job.jobRef ? lineage : branchQuery.data?.lineage ?? null);
  let currentJob = $derived(!branchRef || branchRef === job.jobRef ? job : branchQuery.data?.job ?? null);
  let children = $derived.by(() => {
    const rows = [...(current?.children ?? [])];
    return rows.sort((a, b) => Number(b.status === 'failed') - Number(a.status === 'failed'));
  });
  let matching = $derived(children.filter((child) => filter === 'problems' ? child.status === 'failed' : filter === 'active' ? !TERMINAL_STATUSES.includes(child.status) : true));
  let shown = $derived(showAll ? matching : matching.slice(0, 4));
  let rollup = $derived(childRollup(children));
  let selected = $derived(current?.children.find((child) => child.jobRef === selectedRef) ?? null);
  let selectedDetail = $derived(selectedQuery.data);
  // Ancestors run farthest first, so the branch's path below this job is everything after it. Past the
  // read's ancestor cap this job drops out of the list and the path starts at the nearest ones listed.
  let branchPath = $derived.by(() => {
    const ancestors = current?.ancestors ?? [];
    return ancestors.slice(ancestors.findIndex((node) => node.jobRef === job.jobRef) + 1);
  });
  let branchPathCut = $derived(!!current?.ancestors.length && !current.ancestors.some((node) => node.jobRef === job.jobRef));

  // A changed parent read, from a poll or a refresh, means its children may have moved on, so the inspected
  // child and the explored branch are read again with it. Structural sharing keeps an unchanged lineage the same
  // object, so a quiet poll costs nothing here.
  $effect(() => {
    void lineage;
    untrack(() => {
      if (selectedRef) void selectedQuery.refetch();
      if (branchRef) void branchQuery.refetch();
    });
  });

  export function inspect(ref: string): void {
    // The status panel names a child of this page's job; an explored branch that lacks it returns to this job.
    if (branchRef && !current?.children.some((child) => child.jobRef === ref)) explore(job.jobRef);
    selectedRef = ref;
    void tick().then(() => document.getElementById(inspectorId)?.scrollIntoView({ block: 'nearest', behavior: 'instant' }));
  }
  function explore(ref: string): void {
    branchRef = ref === job.jobRef ? null : ref;
    selectedRef = null;
    filter = 'all';
    showAll = false;
  }
</script>

<section class="panel focus-lineage" aria-labelledby="job-lineage-heading" id="job-lineage-evidence">
  <div class="lineage-heading">
    <div><h2 id="job-lineage-heading">Lineage</h2><p class="dim">{children.length}{current?.childrenHasMore ? '+' : ''} direct child {children.length === 1 ? 'job' : 'jobs'}</p></div>
    <div class="lineage-rollup">{#each rollup as [status, count]}<span><StatusBadge {status} /> {count}</span>{/each}</div>
  </div>

  {#if branchRef}
    <nav class="lineage-path" aria-label="Lineage path">
      <button class="link-button" onclick={() => explore(job.jobRef)}>This job</button>
      {#if branchPathCut}<Icon name="chevron-right" /><span class="dim">…</span>{/if}
      {#each branchPath as ancestor (ancestor.jobRef)}
        <Icon name="chevron-right" /><button class="link-button" onclick={() => explore(ancestor.jobRef)}>{ancestor.jobName}</button>
      {/each}
      <Icon name="chevron-right" /><span>{currentJob?.jobName ?? 'Loading branch'}</span>
    </nav>
  {:else if lineage?.ancestors.length}
    <nav class="lineage-path" aria-label="Ancestor jobs">
      {#if lineage.ancestorsHasMore}<span class="dim">Earlier ancestors…</span>{/if}
      {#each lineage.ancestors as ancestor (ancestor.jobRef)}
        <a href={routes.job(ancestor.jobRef, { namespace: job.jobNamespace })} title={ancestor.jobRef}>{ancestor.jobName}</a><Icon name="chevron-right" />
      {/each}<span>This job</span>
    </nav>
  {/if}

  {#if branchRef && (branchQuery.isPending || branchQuery.error || !current)}
    <StateView loading={branchQuery.isPending} error={branchQuery.error?.message} emptyText="This job is no longer available." onRetry={() => branchQuery.refetch()} />
  {:else if currentJob}
    {#if children.length > 4}
      <div class="lineage-filters" aria-label="Child job filters">
        {#each [['all', 'All'], ['problems', 'Problems'], ['active', 'Active']] as [value, label]}
          <button class:active={filter === value} aria-pressed={filter === value} onclick={() => { filter = value; showAll = false; }}>{label}</button>
        {/each}
      </div>
    {/if}
    <div class="lineage-root"><span>{branchRef ? currentJob.jobName : 'This job'}</span><StatusBadge status={currentJob.status} /></div>
    {#if children.length}
      <div class="lineage-children" class:unconnected={shown.length > 4} style={'--children:' + Math.max(shown.length, 1)} aria-label="Child jobs">
        {#each shown as child (child.jobRef)}
          <button class="lineage-child" class:failed={child.status === 'failed'} class:selected={selectedRef === child.jobRef} aria-pressed={selectedRef === child.jobRef} aria-label={'Inspect ' + child.jobName + ' ending ' + child.jobRef.slice(-8)} title={child.jobRef} onclick={() => inspect(child.jobRef)}>
            <span class="child-name">{child.jobName}</span><span class="child-ref mono">{compactRef(child.jobRef)}</span><StatusBadge status={child.status} />
          </button>
        {/each}
      </div>
      {#if matching.length === 0}<p class="dim">No children match this filter.</p>{/if}
      {#if matching.length > 4}<button class="link-button" onclick={() => (showAll = !showAll)}>{showAll ? 'Show fewer children' : 'Show all ' + matching.length + ' children'}</button>{/if}
      {#if current?.childrenHasMore}<p class="dim">Showing the first {children.length} children.{#if branchRef}{' '}<a href={routes.job(currentJob.jobRef, { namespace: job.jobNamespace })}>Open {currentJob.jobName}</a> for its own detail.{/if}</p>{/if}
      <p class="lineage-help dim">Select a child to inspect it. Explore its children without leaving this page.</p>
    {/if}
  {/if}

  {#if selected}
    <section class="lineage-inspector" id={inspectorId} aria-label="Selected child">
      <div class="lineage-heading"><h3>{selected.jobName}</h3><button class="icon-btn" title="Close child inspector" aria-label="Close child inspector" onclick={() => (selectedRef = null)}><Icon name="x" /></button></div>
      <div class="selected-identity"><StatusBadge status={selectedDetail?.job.status ?? selected.status} /><JobRef value={selected.jobRef} copy /><span class="dim">Updated <RelativeTime value={selected.modifiedAtUtc} /></span></div>
      {#if selectedQuery.isPending}<StateView loading={true} loadingText="Loading child details…" />
      {:else if selectedQuery.error}<StateView error={selectedQuery.error.message} onRetry={() => selectedQuery.refetch()} />
      {:else if selectedDetail}
        <p>{selectedDetail.explain?.reason ?? selectedDetail.explain?.headline ?? 'Open this job for its complete execution details.'}</p>
        {#if selectedDetail.explain?.activeWait}<p class="dim">Waiting on <span class="mono">{selectedDetail.explain.activeWait.name}</span>{#if selectedDetail.explain.activeWait.dueAtUtc}{' · due '}<RelativeTime value={selectedDetail.explain.activeWait.dueAtUtc} />{/if}</p>{/if}
      {:else}<p class="dim">This job is no longer available. Its recorded lineage is shown above.</p>{/if}
      <div class="control-row">
        {#if selectedDetail?.lineage?.children.length}<button onclick={() => explore(selected!.jobRef)}>{selectedDetail.lineage.children.length}{selectedDetail.lineage.childrenHasMore ? '+' : ''} {selectedDetail.lineage.children.length === 1 && !selectedDetail.lineage.childrenHasMore ? 'child' : 'children'}<Icon name="chevron-right" /></button>{/if}
        <a class="lineage-open" href={routes.job(selected.jobRef, { namespace: job.jobNamespace })}>Open job detail<Icon name="chevron-right" /></a>
      </div>
    </section>
  {/if}
</section>

<style>
  .lineage-heading { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 12px; }
  .lineage-heading h2, .lineage-heading h3 { margin: 0; }
  .lineage-heading p, .lineage-help { font-size: var(--text-micro); margin: 4px 0 0; }
  .lineage-rollup { display: flex; flex-wrap: wrap; gap: 8px 12px; font-size: var(--text-micro); }
  .lineage-rollup > span { display: inline-flex; gap: 5px; align-items: center; }
  .lineage-rollup :global(.badge) { font-size: var(--text-micro); }
  .lineage-path { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 20px 0; font-size: var(--text-sm); }
  .lineage-filters { display: flex; gap: 6px; margin-top: 16px; }
  .lineage-filters button { border-color: transparent; background: transparent; padding: 4px 10px; font-size: var(--text-micro); }
  .lineage-filters button.active { border-color: var(--line); background: var(--panel-subtle); color: var(--accent); }
  .lineage-root { position: relative; margin: 24px auto 36px; padding: 14px; border: 1px solid var(--line); border-radius: 10px; width: min(240px, 100%); display: grid; gap: 10px; justify-items: start; font-size: var(--text-sm); overflow-wrap: anywhere; }
  .lineage-root::after { content: ''; position: absolute; top: 100%; height: 25px; left: 50%; border-left: 1px solid var(--line); }
  .lineage-root:last-child { margin-bottom: 0; }
  .lineage-root:last-child::after { display: none; }
  .lineage-children { position: relative; display: grid; grid-template-columns: repeat(min(var(--children), 4), minmax(0, 1fr)); gap: 14px; width: 100%; max-width: calc(var(--children) * 260px); margin: 0 auto 20px; }
  .lineage-children::before { content: ''; position: absolute; top: -12px; left: calc(50% / var(--children)); right: calc(50% / var(--children)); border-top: 1px solid var(--line); }
  .lineage-child { position: relative; display: grid; justify-items: start; align-content: start; gap: 10px; padding: 14px; min-height: 112px; background: var(--panel); border-radius: 10px; text-align: left; min-width: 0; font-size: var(--text-sm); }
  .lineage-child::before { content: ''; position: absolute; height: 12px; bottom: 100%; left: 50%; border-left: 1px solid var(--line); }
  .lineage-child.failed { border-color: color-mix(in srgb, var(--bad) 65%, var(--line)); }
  .lineage-child.failed::before { border-color: var(--bad); }
  .lineage-child.selected { outline: 2px solid var(--accent); outline-offset: 2px; }
  .child-name { font-weight: 500; overflow-wrap: anywhere; font-size: var(--text-sm); }
  .child-ref { margin-top: -6px; color: var(--muted); font-size: var(--text-micro); }
  .lineage-children.unconnected { grid-template-columns: repeat(4, minmax(0, 1fr)); }
  .unconnected::before, .unconnected .lineage-child::before { display: none; }
  .lineage-inspector { margin-top: 20px; padding-top: 18px; border-top: 1px solid var(--line); font-size: var(--text-sm); }
  .selected-identity { display: flex; gap: 12px; align-items: center; flex-wrap: wrap; margin: 10px 0; }
  .lineage-open { display: inline-flex; align-items: center; gap: 6px; padding: 6px 0; }
  @container (max-width: 700px) {
    .lineage-root { width: auto; margin: 20px 0 12px; border: 0; border-bottom: 1px solid var(--line); border-radius: 0; padding: 10px 0; display: flex; justify-content: space-between; }
    .lineage-root::after, .lineage-children::before, .lineage-child::before { display: none; }
    .lineage-children, .lineage-children.unconnected { display: grid; grid-template-columns: minmax(0, 1fr); gap: 8px; margin-left: 8px; padding-left: 16px; border-left: 1px solid var(--line); }
    .lineage-child { min-height: 0; grid-template-columns: minmax(0, 1fr) auto; padding: 12px; gap: 8px; }
  }
</style>
