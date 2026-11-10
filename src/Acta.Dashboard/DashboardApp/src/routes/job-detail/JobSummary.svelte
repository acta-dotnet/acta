<script lang="ts">
  import type { JobDetail } from './types.ts';
  import DetailFacts, { type DetailFact } from '../../components/DetailFacts.svelte';
  import { payloadFormatLabel } from './jobDetailState.ts';
  import { routes } from '../../routes.ts';
  import { consecutiveFailuresText, displayFormatter } from '../../format.ts';
  let { job, maxAttempts = null }: { job: JobDetail; maxAttempts?: number | null } = $props();
  let facts = $derived<DetailFact[]>([
    { label: 'Full reference', value: job.jobRef, kind: 'mono', copy: true, wide: true },
    { label: 'Kind', value: job.kind },
    { label: 'Version', value: job.version },
    { label: 'Priority', value: job.priority },
    { label: 'Recorded failures', value: job.failureCount > 0 ? consecutiveFailuresText(job.failureCount) : null },
    { label: 'Input format', value: payloadFormatLabel(job.inputFormatId) },
    { label: 'Attempt budget', value: maxAttempts === null ? null : displayFormatter.number(maxAttempts) + (job.kind === 'permanent' ? ' per firing' : '') },
    { label: 'Tenant', value: job.tenantName, href: job.tenantName ? routes.tenant(job.tenantName, { namespace: job.jobNamespace }) : undefined },
    { label: 'Lane', value: job.lane, kind: 'mono', href: job.lane ? routes.jobs({ lane: job.lane, namespace: job.jobNamespace }) : undefined },
    { label: 'Concurrency key', value: job.concurrencyKey, kind: 'mono' },
    { label: 'Deduplication key', value: job.deduplicationKey, kind: 'mono', copy: true },
    { label: 'Correlation id', value: job.correlationKey, kind: 'mono', href: job.correlationKey ? routes.jobs({ correlationKey: job.correlationKey, namespace: job.jobNamespace }) : undefined, copy: true },
    { label: 'Parent', value: job.parentJobRef, kind: 'reference', href: job.parentJobRef ? routes.job(job.parentJobRef, { namespace: job.jobNamespace }) : undefined },
    { label: 'Lineage root', value: job.lineageRootJobRef !== job.jobRef && job.lineageRootJobRef !== job.parentJobRef ? job.lineageRootJobRef : null, kind: 'reference', href: job.lineageRootJobRef ? routes.job(job.lineageRootJobRef, { namespace: job.jobNamespace }) : undefined },
    { label: 'Created', value: job.createdAtUtc, kind: 'time' },
    { label: 'Modified', value: job.modifiedAtUtc, kind: 'time' },
    { label: 'Retention until', value: job.retentionUntilUtc, kind: 'time' },
  ]);
</script>

<section aria-label="Job technical details"><DetailFacts {facts} /></section>
