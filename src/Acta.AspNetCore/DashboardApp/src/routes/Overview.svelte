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
  // executing while the head ages.
  // One relay tick moves at most this many source rows; a backlog beyond it cannot clear in the
  // next tick, which is what "lagging outbox" means here. Below it, backlog is between-tick drift.
  const OUTBOX_TICK_ENVELOPE = 5120;

  // A live schedule sits briefly past due between its due instant and the slot firing, so overdue is
  // only worth reporting once it is longer than any normal fire delay. Same envelope as the oldest
  // ready job rather than a second invented number.
  const SCHEDULE_LAG_ENVELOPE = 300;

  function buildVerdict(o, outboxLines, ns) {
    if (!o) {
      return null;
    }
    const plural = (n, word) => displayFormatter.number(n) + ' ' + word + (n === 1 ? '' : 's');
    // executorCapacity separates the two diagnoses the old proxy had to guess between: no capacity at
    // all is a deployment problem, while slots sitting idle beside a due backlog is the worse one -
    // workers are up and not claiming.
    const stalled = o.readyCount > 0 && o.executingCount === 0 && o.oldestReadyAgeSeconds > 60;

    const urgent = [];
    if (o.unresolvedCriticalAlertCount > 0) {
      urgent.push({ text: plural(o.unresolvedCriticalAlertCount, 'critical alert'), href: routes.alerts({ namespace: ns }) });
    }
    if (o.readyCount > 0 && o.executorCapacity === 0) {
      urgent.push({ text: plural(o.readyCount, 'ready job') + ' with no live workers', href: routes.workers({ namespace: ns }) });
    } else if (stalled) {
      urgent.push({
        text: plural(o.readyCount, 'ready job') + ' not draining while ' + plural(o.executorCapacity, 'executor slot') + ' sit idle',
        href: routes.jobs({ namespace: ns, status: 'Ready' })
      });
    }

    // A dead worker is a tombstone, not an incident - it left or crashed and won't return. Real lost
    // capacity surfaces as the stalled backlog above, so dead workers do not feed the verdict at all.
    const soft = [];
    if (o.oldestReadyAgeSeconds > 300 && !stalled) {
      soft.push({ text: 'oldest ready job waiting ' + displayFormatter.duration(o.oldestReadyAgeSeconds), href: routes.jobs({ namespace: ns, status: 'Ready' }) });
    }
    // A schedule that stops firing moves nothing else on this snapshot: no job is enqueued, so ready,
    // failed and the workers all stay quiet while the verdict would otherwise read clean.
    if (o.scheduleLagSeconds > SCHEDULE_LAG_ENVELOPE) {
      soft.push({ text: 'schedule overdue by ' + displayFormatter.duration(o.scheduleLagSeconds), href: routes.schedules({ namespace: ns }) });
    }
    if (o.failedCount > 0) soft.push({ text: plural(o.failedCount, 'failed job'), href: routes.jobs({ namespace: ns, status: 'Failed' }) });
    if (o.staleWorkerCount > 0) soft.push({ text: plural(o.staleWorkerCount, 'stale worker'), href: routes.workers({ namespace: ns }) });
    if (o.unresolvedAlertCount > 0 && o.unresolvedCriticalAlertCount === 0) {
      soft.push({ text: plural(o.unresolvedAlertCount, 'unresolved alert'), href: routes.alerts({ namespace: ns }) });
    }
    for (const line of outboxLines ?? []) {
      if (line.backlog > OUTBOX_TICK_ENVELOPE) {
        soft.push({ text: 'outbox lagging ' + displayFormatter.number(line.backlog) + ' rows' + (ns ? '' : ' in ' + line.jobNamespace) });
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
    <StateView loading={true} loadingText="Loading overview..." />
  {:else if error}
    <div class="panel"><StateView {error} onRetry={() => snapshot.refetch()} /></div>
  {:else}
    {#if verdict}
      <div class="verdict {verdict.tone}">
        <span class="verdict-label"><Icon name={verdict.tone === 'ok' ? 'check-circle' : verdict.tone === 'bad' ? 'x-circle' : 'warn'} />{verdict.label}</span>
        <span class="verdict-reason">
          {#each verdict.reasons as reason, i}
            {#if i > 0}{' · '}{/if}
            {#if reason.href}<a href={reason.href}>{reason.text}</a>{:else}{reason.text}{/if}
          {:else}
            No critical alerts, no stuck backlog, workers healthy.
          {/each}
        </span>
      </div>
    {/if}

    <div class="metrics">
      <MetricCard
        label="Total jobs" icon="cube"
        value={overview.jobCount}
        note={overview.systemJobCount > 0 ? '+' + displayFormatter.number(overview.systemJobCount) + ' system' : ''}
        hint={displayFormatter.number(overview.systemJobCount) + ' framework (sys.-prefixed) jobs of ' + displayFormatter.number(overview.jobCount) + ' total'}
        href={routes.jobs({ namespace: $scope })} />
      <MetricCard label="Ready" icon="clock" value={overview.readyCount} href={routes.jobs({ namespace: $scope, status: 'Ready' })} />
      <MetricCard
        label="Oldest ready" icon="stopwatch"
        value={displayFormatter.duration(overview.oldestReadyAgeSeconds)}
        tone={overview.oldestReadyAgeSeconds > 300 ? 'warn' : ''}
        hint="How long the oldest due job has been waiting for a worker" />
      <MetricCard
        label="Executing" icon="lightning-bolt"
        value={overview.executingCount}
        note={overview.executorCapacity > 0 ? ' / ' + displayFormatter.number(overview.executorCapacity) : ''}
        tone={overview.executorCapacity > 0 && overview.executingCount >= overview.executorCapacity ? 'warn' : ''}
        hint={overview.executorCapacity > 0
          ? displayFormatter.number(overview.executorCapacity) + ' executor slots across live workers - at the ceiling, only more workers go faster'
          : 'No live workers, so nothing can be claimed'}
        href={routes.jobs({ namespace: $scope, status: 'Executing' })} />
      <MetricCard
        label="Failed" icon="x-circle"
        value={overview.failedCount}
        tone={overview.failedCount > 0 ? 'bad' : 'ok'}
        href={routes.jobs({ namespace: $scope, status: 'Failed' })} />
      <MetricCard
        label="Unresolved alerts" icon="bell"
        value={overview.unresolvedAlertCount}
        tone={overview.unresolvedCriticalAlertCount > 0 ? 'bad' : overview.unresolvedAlertCount > 0 ? 'warn' : 'ok'}
        hint={displayFormatter.number(overview.unresolvedCriticalAlertCount) + ' critical'}
        href={routes.alerts({ namespace: $scope })} />
      <MetricCard
        label="Dead workers" icon="person"
        value={overview.deadWorkerCount}
        tone={overview.deadWorkerCount > 0 ? 'warn' : 'ok'}
        hint={displayFormatter.number(overview.staleWorkerCount) + ' stale'}
        href={routes.workers({ namespace: $scope, status: 'Dead' })} />
      <MetricCard
        label="Due soon" icon="calendar"
        value={overview.dueSoonScheduleCount}
        hint="Live schedules due within the next hour"
        href={routes.schedules({ namespace: $scope })} />
      <MetricCard
        label="Schedule lag" icon="calendar"
        value={displayFormatter.duration(overview.scheduleLagSeconds)}
        tone={overview.scheduleLagSeconds > SCHEDULE_LAG_ENVELOPE ? 'warn' : ''}
        hint="How far past due the most overdue live schedule is; a schedule that stops firing moves no other number here"
        href={routes.schedules({ namespace: $scope })} />
    </div>

    <div class="panel">
      <h2>Recent failed jobs</h2>
      {#if failedJobs.items.length === 0}
        <StateView emptyText="No failed jobs." />
      {:else}
        <DataTable ledger>
          <caption class="sr-only">Recent failed jobs</caption>
          <thead><tr><th>Job</th><th>Name</th><th>Namespace</th><th>Failed</th></tr></thead>
          <tbody>
            {#each failedJobs.items as job}
              <tr class="trouble">
                <td><JobRef value={job.jobRef} href={routes.job(job.jobRef, { namespace: job.jobNamespace })} /></td>
                <td>{job.jobName}</td>
                <td class="dim">{job.jobNamespace}</td>
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
        <DataTable ledger>
          <caption class="sr-only">Unresolved critical alerts</caption>
          <thead><tr><th>Alert</th><th>Severity</th><th>Namespace</th><th>Created</th></tr></thead>
          <tbody>
            {#each criticalAlerts.items as alert}
              <tr class="trouble">
                <!-- The job ref rides with the title rather than taking its own column, so this
                     panel keeps the same four columns as the others without losing the link. -->
                <td>
                  {alert.title}
                  {#if alert.jobRef}<JobRef value={alert.jobRef} href={routes.job(alert.jobRef, { namespace: alert.jobNamespace ?? $scope })} />{/if}
                </td>
                <td><SeverityBadge severity={alert.severity} /></td>
                <td class="dim">{alert.jobNamespace ?? $scope}</td>
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
        <DataTable ledger>
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
        <DataTable ledger>
          <caption class="sr-only">Next schedules</caption>
          <thead><tr><th>Schedule</th><th>Expression</th><th>Namespace</th><th>Next run</th></tr></thead>
          <tbody>
            {#each schedules.items as schedule}
              <tr>
                <!-- Schedule name is the identity; the job it fires rides with it so the panel
                     keeps the shared four columns. -->
                <td>
                  <a href={routes.schedule(schedule.jobNamespace, schedule.jobName, schedule.scheduleName)}>{schedule.scheduleName}</a>
                  <a class="dim" href={routes.jobs({ namespace: schedule.jobNamespace, jobName: schedule.jobName })}>{schedule.jobName}</a>
                </td>
                <td class="mono">{schedule.expression}</td>
                <td class="dim">{schedule.jobNamespace}</td>
                <td><RelativeTime value={schedule.nextRunAtUtc} /></td>
              </tr>
            {/each}
          </tbody>
        </DataTable>
      {/if}
    </div>
  {/if}
</Page>
