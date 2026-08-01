<script lang="ts">
  import DataTable from '../../components/DataTable.svelte';
  import RelativeTime from '../../components/RelativeTime.svelte';
  import StatusBadge from '../../components/StatusBadge.svelte';
  import Icon from '../../components/Icon.svelte';
  import { childRollup } from './jobDetailState.ts';
  import { statusTonePresentation } from '../../components/jobTimelineState.ts';
  import type { JobLineage, JobSnapshot } from './types.ts';
  import { routes } from '../../routes.ts';
  import { displayFormatter, statusClass } from '../../format.ts';
  import JobRef from '../../components/JobRef.svelte';

  let {
    job,
    lineage = null
  }: { job: JobSnapshot; lineage?: JobLineage | null } = $props();

  let hasLineage = $derived(
    !!lineage &&
      (lineage.ancestors.length > 0 || lineage.children.length > 0 || lineage.steps.length > 0 || !!lineage.activeWait)
  );
  let rollup = $derived(lineage ? childRollup(lineage.children) : []);
  const isSignal = (kind: string) => kind.toLowerCase() === 'signal';
  // The timeline's node vocabulary in miniature: a toned ring dot per job in the tree.
  const toneOf = (status: string) => 't-' + statusTonePresentation(statusClass(status)).tone;
  const glyphOf = (status: string) => statusTonePresentation(statusClass(status)).icon;
</script>

{#if lineage && hasLineage}
  <section class="panel lineage-map" id="job-lineage-evidence" aria-labelledby="job-lineage-heading">
    <h2 id="job-lineage-heading">Lineage map</h2>
    <ul class="tree">
      {#each lineage.ancestors as ancestor, index (ancestor.jobRef)}
        <li class="node ancestor {toneOf(ancestor.status)}" style={'--depth:' + index}>
          <span class="tree-dot" title={ancestor.status}><Icon name={glyphOf(ancestor.status)} /></span>
          <a href={routes.job(ancestor.jobRef, { namespace: job.jobNamespace })} class="mono" title={ancestor.jobRef}>{ancestor.jobName}</a>
        </li>
      {/each}
      <li class="node focus {toneOf(job.status)}" style={'--depth:' + lineage.ancestors.length}>
        <span class="tree-dot" title={job.status}><Icon name={glyphOf(job.status)} /></span><span class="mono">{job.jobName}</span><span class="dim">this job</span>
      </li>
      {#each lineage.steps as step (step.name)}
        <li class="node step" style={'--depth:' + (lineage.ancestors.length + 1)}><span class="mono">{step.name}</span><span class="dim">: {step.explanation}</span></li>
      {/each}
      {#if lineage.activeWait}
        <li class="node wait" style={'--depth:' + (lineage.ancestors.length + 1)}>
          Waiting on {isSignal(lineage.activeWait.kind) ? 'signal' : 'timer'} <span class="mono">{lineage.activeWait.name}</span>
          {#if lineage.activeWait.dueAtUtc}<span class="dim"> · due <RelativeTime value={lineage.activeWait.dueAtUtc} /></span>{/if}
        </li>
      {/if}
      {#each lineage.children as child (child.jobRef)}
        <li class="node child {toneOf(child.status)}" style={'--depth:' + (lineage.ancestors.length + 1)}>
          <span class="tree-dot" title={child.status}><Icon name={glyphOf(child.status)} /></span>
          <a href={routes.job(child.jobRef, { namespace: job.jobNamespace })} class="mono">{child.jobName}</a>
        </li>
      {/each}
      {#if lineage.childrenHasMore}<li class="node more" style={'--depth:' + (lineage.ancestors.length + 1)}><span class="dim">+ more children (showing the first 100)</span></li>{/if}
    </ul>
  </section>

  {#if lineage.children.length > 0}
    <section class="panel" aria-labelledby="job-children-heading">
      <h2 id="job-children-heading">Children</h2>
      <p>{displayFormatter.number(lineage.children.length)}{lineage.childrenHasMore ? '+' : ''} {lineage.children.length === 1 ? 'child' : 'children'}: <span class="dim">{rollup.map(([status, count]) => `${displayFormatter.number(count)} ${status}`).join(', ')}</span></p>
      <DataTable>
        <caption class="sr-only">Child jobs</caption>
        <thead><tr><th>Job</th><th>Status</th><th>Age</th></tr></thead>
        <tbody>
          {#each lineage.children as child (child.jobRef)}
            <tr>
              <td><a href={routes.job(child.jobRef, { namespace: job.jobNamespace })}>{child.jobName} <JobRef value={child.jobRef} /></a></td>
              <td><StatusBadge status={child.status} /></td>
              <td><RelativeTime value={child.createdAtUtc} /></td>
            </tr>
          {/each}
        </tbody>
      </DataTable>
      {#if lineage.childrenHasMore}<p class="dim">Showing the first 100 children.</p>{/if}
    </section>
  {/if}
{/if}

<style>
  .tree { list-style: none; margin: 0; padding: 0; }
  .node { display: flex; align-items: center; gap: 0.5rem; padding: 0.25rem 0; margin-left: calc(var(--depth, 0) * 1.25rem); position: relative; }
  .node::before { content: ''; position: absolute; left: -0.7rem; top: 0; bottom: 0; border-left: 1px solid var(--line); }
  .node.focus { font-weight: 600; }
  .node.step, .node.wait, .node.more { font-size: 0.9rem; }
  .tree-dot {
    width: 17px; height: 17px; border-radius: 50%; flex: none;
    display: grid; place-items: center; font-size: 9px;
    background: var(--tone-bg, var(--badge-bg)); color: var(--tone, var(--muted));
    box-shadow: inset 0 0 0 1.5px color-mix(in srgb, var(--tone, var(--muted)) 55%, transparent);
  }
  .tree-dot :global(.ic) { top: 0; }
  .node.focus .tree-dot { box-shadow: inset 0 0 0 1.5px var(--tone, var(--muted)), 0 0 0 3px color-mix(in srgb, var(--tone, var(--muted)) 15%, transparent); }
</style>
