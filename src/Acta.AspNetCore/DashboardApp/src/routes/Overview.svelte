<script>
  import { createQuery, keepPreviousData } from '@tanstack/svelte-query';
  import { api } from '../api';
  import { keys } from '../query';
  import { livePaused, listRefetchInterval } from '../polling';
  import { displayFormatter } from '../format';
  import { scope } from '../scope';
  import Page from '../components/Page.svelte';
  import FreshnessIndicator from '../components/FreshnessIndicator.svelte';
  import DataTable from '../components/DataTable.svelte';
  import StateView from '../components/StateView.svelte';
  import StatusBadge from '../components/StatusBadge.svelte';
  import SeverityBadge from '../components/SeverityBadge.svelte';
  import Icon from '../components/Icon.svelte';
  import MetricCard from '../components/MetricCard.svelte';
  import RelativeTime from '../components/RelativeTime.svelte';
  import { routes } from '../routes';
  import JobRef from '../components/JobRef.svelte';

  // One composite snapshot query: the five reads land together so the verdict, metric cards, and
  // panels always describe the same instant. Scope changes swap the key, so caching is per-scope.
  const snapshot = createQuery(() => {
    const ns = $scope;
    return {
      queryKey: keys.list('overview', { jobNamespace: ns }),
      queryFn: async ({ signal }) => {
        const [overview, failedJobs, criticalAlerts, workers, schedules, outbox] = await Promise.all([
          api('overview', { jobNamespace: ns }, { signal }),
          api('jobs', { status: 'Failed', pageSize: 10, jobNamespace: ns }, { signal }),
          api('alerts', { unresolvedOnly: true, severityAtLeast: 'Critical', pageSize: 10, jobNamespace: ns }, { signal }),
          api('workers', { pageSize: 10, jobNamespace: ns }, { signal }),
          api('schedules', { pageSize: 10, jobNamespace: ns }, { signal }),
          api('overview/outbox', { jobNamespace: ns }, { signal })
        ]);
        return { overview, failedJobs, criticalAlerts, workers, schedules, outbox };
      },
      refetchInterval: $livePaused ? false : listRefetchInterval,
      placeholderData: keepPreviousData
    };
  });

  let overview = $derived(snapshot.data?.overview ?? null);
  let failedJobs = $derived(snapshot.data?.failedJobs ?? null);
  let criticalAlerts = $derived(snapshot.data?.criticalAlerts ?? null);
  let workers = $derived(snapshot.data?.workers ?? null);
  let schedules = $derived(snapshot.data?.schedules ?? null);
  let outbox = $derived(snapshot.data?.outbox ?? []);
  let error = $derived(snapshot.error ? snapshot.error.message : null);
  let loading = $derived(snapshot.isPending);

  let verdict = $derived(buildVerdict(overview, outbox, $scope));

  // Triage verdict from the overview snapshot. Workers are ephemeral, so a dead/stale worker is only a
  // soft signal; the real "act now" worker problem is no capacity - due jobs sitting with nothing
  // executing while the head ages (the overview carries no live-worker count, so this is the proxy).
  // One relay tick moves at most this many source rows; a backlog beyond it cannot clear in the
  // next tick, which is what "lagging outbox" means here. Below it, backlog is between-tick drift.
  const OUTBOX_TICK_ENVELOPE = 5120;

  function buildVerdict(o, outboxLines, ns) {
    if (!o) {
      return null;
    }
    const plural = (n, word) => displayFormatter.number(n) + ' ' + word + (n === 1 ? '' : 's');
    const stalled = o.readyCount > 0 && o.executingCount === 0 && o.oldestReadyAgeSeconds > 60;

    const urgent = [];
    if (o.unresolvedCriticalAlertCount > 0) urgent.push(plural(o.unresolvedCriticalAlertCount, 'critical alert'));
    if (stalled) urgent.push(plural(o.readyCount, 'ready job') + ' not draining (no live workers?)');

    // A dead worker is a tombstone, not an incident - it left or crashed and won't return. Real lost
    // capacity surfaces as the stalled backlog above, so dead workers do not feed the verdict at all.
    const soft = [];
    if (o.oldestReadyAgeSeconds > 300 && !stalled) soft.push('oldest ready job waiting ' + displayFormatter.duration(o.oldestReadyAgeSeconds));
    if (o.failedCount > 0) soft.push(plural(o.failedCount, 'failed job'));
    if (o.staleWorkerCount > 0) soft.push(plural(o.staleWorkerCount, 'stale worker'));
    if (o.unresolvedAlertCount > 0 && o.unresolvedCriticalAlertCount === 0) {
      soft.push(plural(o.unresolvedAlertCount, 'unresolved alert'));
    }
    for (const line of outboxLines ?? []) {
      if (line.backlog > OUTBOX_TICK_ENVELOPE) {
        soft.push('outbox lagging ' + displayFormatter.number(line.backlog) + ' rows' + (ns ? '' : ' in ' + line.jobNamespace));
      }
    }

    if (urgent.length > 0) {
      return { label: 'Action needed', tone: 'bad', reasons: urgent.concat(soft) };
    }
    if (soft.length > 0) {
      return { label: 'Degraded', tone: 'warn', reasons: soft };
    }
    // Deliberately not "Healthy": these are limited signals, so claim only the absence of visible
    // trouble, not system health.
    return { label: 'No immediate issues detected', tone: 'ok', reasons: [] };
  }
</script>

<Page title="Overview">
  {#snippet actions()}
    <FreshnessIndicator
      dataUpdatedAt={snapshot.dataUpdatedAt}
      isFetching={snapshot.isFetching}
      isError={!!snapshot.error}
      onRefresh={() => snapshot.refetch()} />
  {/snippet}

  {#if loading}
    <div class="state">Loading overview...</div>
  {:else if error}
    <div class="panel"><StateView {error} onRetry={() => snapshot.refetch()} /></div>
  {:else}
    {#if verdict}
      <div class="verdict {verdict.tone}">
        <span class="verdict-label"><Icon name={verdict.tone === 'ok' ? 'check-circle' : verdict.tone === 'bad' ? 'x-circle' : 'warn'} />{verdict.label}</span>
        <span class="verdict-reason">
          {verdict.reasons.length > 0 ? verdict.reasons.join(' · ') : 'No critical alerts, no stuck backlog, workers healthy.'}
        </span>
      </div>
    {/if}

    <div class="metrics">
      <MetricCard
        label="Total jobs"
        value={overview.jobCount}
        note={overview.systemJobCount > 0 ? '+' + displayFormatter.number(overview.systemJobCount) + ' system' : ''}
        hint={displayFormatter.number(overview.systemJobCount) + ' framework (sys.-prefixed) jobs of ' + displayFormatter.number(overview.jobCount) + ' total'}
        href={routes.jobs({ namespace: $scope })} />
      <MetricCard label="Ready" value={overview.readyCount} href={routes.jobs({ namespace: $scope, status: 'Ready' })} />
      <MetricCard
        label="Oldest ready"
        value={displayFormatter.duration(overview.oldestReadyAgeSeconds)}
        tone={overview.oldestReadyAgeSeconds > 300 ? 'warn' : ''}
        hint="How long the oldest due job has been waiting for a worker" />
      <MetricCard label="Executing" value={overview.executingCount} href={routes.jobs({ namespace: $scope, status: 'Executing' })} />
      <MetricCard
        label="Failed"
        value={overview.failedCount}
        tone={overview.failedCount > 0 ? 'bad' : 'ok'}
        href={routes.jobs({ namespace: $scope, status: 'Failed' })} />
      <MetricCard
        label="Unresolved alerts"
        value={overview.unresolvedAlertCount}
        tone={overview.unresolvedCriticalAlertCount > 0 ? 'bad' : overview.unresolvedAlertCount > 0 ? 'warn' : 'ok'}
        hint={displayFormatter.number(overview.unresolvedCriticalAlertCount) + ' critical'}
        href={routes.alerts({ namespace: $scope })} />
      <MetricCard
        label="Dead workers"
        value={overview.deadWorkerCount}
        tone={overview.deadWorkerCount > 0 ? 'warn' : 'ok'}
        hint={displayFormatter.number(overview.staleWorkerCount) + ' stale'}
        href={routes.workers({ namespace: $scope, status: 'Dead' })} />
      <MetricCard
        label="Due soon"
        value={overview.dueSoonScheduleCount}
        hint="Live schedules due within the next hour"
        href={routes.schedules({ namespace: $scope })} />
    </div>

    <div class="panel">
      <h2>Recent failed jobs</h2>
      {#if failedJobs.items.length === 0}
        <StateView emptyText="No failed jobs." />
      {:else}
        <DataTable>
          <caption class="sr-only">Recent failed jobs</caption>
          <thead><tr><th>Job</th><th>Namespace / name</th><th>Failed</th></tr></thead>
          <tbody>
            {#each failedJobs.items as job}
              <tr>
                <td><JobRef value={job.jobRef} href={routes.job(job.jobRef, { namespace: job.jobNamespace })} /></td>
                <td>{job.jobNamespace} / {job.jobName}</td>
                <td><RelativeTime value={job.modifiedAtUtc} /></td>
              </tr>
            {/each}
          </tbody>
        </DataTable>
      {/if}
    </div>

    <div class="panel">
      <h2>Unresolved critical alerts</h2>
      {#if criticalAlerts.items.length === 0}
        <StateView emptyText="No unresolved critical alerts." />
      {:else}
        <DataTable>
          <caption class="sr-only">Unresolved critical alerts</caption>
          <thead><tr><th>Severity</th><th>Title</th><th>Job</th><th>Created</th></tr></thead>
          <tbody>
            {#each criticalAlerts.items as alert}
              <tr>
                <td><SeverityBadge severity={alert.severity} /></td>
                <td>{alert.title}</td>
                <td>{#if alert.jobRef}<JobRef value={alert.jobRef} href={routes.job(alert.jobRef, { namespace: alert.jobNamespace ?? $scope })} />{:else}<span class="dim">-</span>{/if}</td>
                <td><RelativeTime value={alert.createdAtUtc} /></td>
              </tr>
            {/each}
          </tbody>
        </DataTable>
      {/if}
    </div>

    <div class="panel">
      <h2>Workers</h2>
      {#if workers.items.length === 0}
        <StateView emptyText="No workers seen." />
      {:else}
        <DataTable>
          <caption class="sr-only">Workers</caption>
          <thead><tr><th>Worker</th><th>Status</th><th>Namespace</th><th>Last heartbeat</th></tr></thead>
          <tbody>
            {#each workers.items as worker}
              <tr class:trouble={worker.status === 'dead'}>
                <td class="mono"><a href={routes.worker(worker.workerId, { namespace: worker.jobNamespace })}>{worker.workerId}</a></td>
                <td><StatusBadge status={worker.status} /></td>
                <td>{worker.jobNamespace}</td>
                <td><RelativeTime value={worker.lastSeenAtUtc} /></td>
              </tr>
            {/each}
          </tbody>
        </DataTable>
      {/if}
    </div>

    <div class="panel">
      <h2>Next schedules</h2>
      {#if schedules.items.length === 0}
        <StateView emptyText="No live schedules." />
      {:else}
        <DataTable>
          <caption class="sr-only">Next schedules</caption>
          <thead><tr><th>Job</th><th>Schedule</th><th>Expression</th><th>Next run</th></tr></thead>
          <tbody>
            {#each schedules.items as schedule}
              <tr>
                <td><a href={routes.jobs({ namespace: schedule.jobNamespace, jobName: schedule.jobName })}>{schedule.jobNamespace} / {schedule.jobName}</a></td>
                <td><a href={routes.schedule(schedule.jobNamespace, schedule.jobName, schedule.scheduleName)}>{schedule.scheduleName}</a></td>
                <td class="mono">{schedule.expression}</td>
                <td><RelativeTime value={schedule.nextRunAtUtc} /></td>
              </tr>
            {/each}
          </tbody>
        </DataTable>
      {/if}
    </div>
  {/if}
</Page>
