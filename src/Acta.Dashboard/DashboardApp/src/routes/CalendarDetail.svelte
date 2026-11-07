<script lang="ts">
  import { createQuery, useQueryClient } from '@tanstack/svelte-query';
  import {
    api,
    ApiError,
    archiveCalendar,
    previewCalendar,
    publishCalendar,
    saveCalendarDraft,
    type CalendarDetailView,
    type Paged
  } from '../api.ts';
  import { capabilitiesQuery, can, Permission, keys } from '../query.ts';
  import Page from '../components/Page.svelte';
  import Icon from '../components/Icon.svelte';
  import StateView from '../components/StateView.svelte';
  import ScheduleStatus from '../components/ScheduleStatus.svelte';
  import ChangeHistory from '../components/ChangeHistory.svelte';
  import ConfirmAction from '../components/ConfirmAction.svelte';
  import CopyButton from '../components/CopyButton.svelte';
  import DataTable from '../components/DataTable.svelte';
  import PageFreshness from '../components/PageFreshness.svelte';
  import VersionStatus from '../components/calendar/VersionStatus.svelte';
  import VersionChip from '../components/calendar/VersionChip.svelte';
  import WeeklyEditor from '../components/calendar/WeeklyEditor.svelte';
  import CalendarPreview from '../components/calendar/CalendarPreview.svelte';
  import { mergeHistory, type HistoryEvent } from '../components/changeHistory.ts';
  import { definitionTemplate, forecastSummary, weeklyFromJson, weeklyProblems, weeklyToJson } from '../components/calendar/calendarDefinition.ts';
  import {
    archiveBlockedText,
    archiveBlockers,
    calendarControlMessage,
    calendarVerbSucceeded,
    familyState,
    publishImpact,
    publishImpactText
  } from '../components/calendar/calendarFamily.ts';
  import { eventFacts } from '../lib/eventFacts.ts';
  import { jsonError, prettyJson } from '../lib/rawJson.ts';
  import { displayFormatter } from '../format.ts';
  import { createUrlFilters } from '../urlFilters.ts';
  import { scope } from '../scope.ts';
  import { routes } from '../routes.ts';

  let { jobNamespace, calendarName }: { jobNamespace: string; calendarName: string } = $props();

  // `v` picks the version shown; `role` opens the editor for a family that does not exist yet.
  const params = createUrlFilters({ version: 'v', role: 'role' }, { version: '', role: '' });
  let requestedVersion = $derived(Number($params.version) || null);
  let path = $derived(`calendars/${encodeURIComponent(jobNamespace)}/${encodeURIComponent(calendarName)}`);
  const queryClient = useQueryClient();

  // An editor must never have its in-progress inputs clobbered by a background refresh, so the reads
  // neither poll nor refetch on focus; they refetch after a control applies.
  const detail = createQuery(() => ({
    queryKey: keys.detail('calendars', `${path}?v=${requestedVersion ?? ''}`),
    queryFn: async ({ signal }: { signal: AbortSignal }): Promise<CalendarDetailView | null> => {
      try {
        return await api<CalendarDetailView>(`${path}/detail`, { version: requestedVersion }, { signal });
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null;
        throw error;
      }
    },
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false
  }));
  // The head flattened beside its two first pages, so the template reads one family.
  let family = $derived(
    detail.data
      ? {
          ...detail.data.calendar,
          versions: detail.data.versions.items,
          versionsTotal: detail.data.versions.totalCount,
          dependents: detail.data.dependents.items,
          dependentsTotal: detail.data.dependents.totalCount
        }
      : null
  );
  let missing = $derived(detail.isSuccess && detail.data === null);

  const capabilities = createQuery(() => capabilitiesQuery());
  let canControlNow = $derived(can(capabilities.data, Permission.admin));
  let shownState = $derived(family ? familyState(family, canControlNow) : null);

  let previewLimit = $state(10);
  const browserZone = displayFormatter.localTimeZone;
  const preview = createQuery(() => ({
    queryKey: keys.detail('calendar-preview', `${path}?v=${family?.shownVersion ?? ''}&limit=${previewLimit}`),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      previewCalendar(jobNamespace, calendarName, family!.shownVersion, { limit: previewLimit, timeZoneId: browserZone, signal }),
    enabled: family !== null,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchInterval: false
  }));
  let impact = $derived(publishImpact(preview.data));
  let impactText = $derived(publishImpactText(preview.data));
  let forecastText = $derived(forecastSummary(preview.data, previewLimit));
  // The server refuses an archive while a live schedule binds the family, so the button waits for the preview's bound
  // schedules and says why it is held: still checking, the check failed, or what binds the family.
  let blockers = $derived(archiveBlockers(preview.data));
  let archiveHold = $derived(
    blockers !== null
      ? blockers.count > 0
        ? `${archiveBlockedText(blockers)} Archive it once none does: deploy their jobs without the binding, or retire them.`
        : null
      : preview.isError
        ? 'The bound schedules could not be checked because the forecast did not load. Refresh to try again.'
        : 'Checking the schedules bound to this calendar.'
  );
  // The preview lists at most its cap, so the marks below can cover fewer rows than its overdue total counts.
  let overdueListed = $derived((preview.data?.schedules ?? []).filter((s) => s.overdue).length);
  let overdueKeys = $derived(
    new Set((preview.data?.schedules ?? []).filter((s) => s.overdue).map((s) => `${s.jobNamespace}/${s.jobName}/${s.scheduleName}`))
  );

  // The three calendar codes are filtered server-side by code and namespace; the family's name is a
  // details fact, so the rows of the namespace's other families are dropped here.
  const HISTORY_CODES = ['calendar.draft-saved', 'calendar.published', 'calendar.archived'];
  const history = createQuery(() => ({
    queryKey: keys.detail('calendar-history', path),
    queryFn: async ({ signal }: { signal: AbortSignal }) => {
      const pages = await Promise.all(
        HISTORY_CODES.map((eventCode) =>
          api<Paged<HistoryEvent>>('events', { jobNamespace, eventCode, pageSize: 20 }, { signal }).then((page) =>
            page.items.filter((ev) => eventFacts(ev.detailsText).calendar === calendarName)
          )
        )
      );
      return mergeHistory(pages);
    },
    enabled: family !== null
  }));

  // The editor: definition text is the one source; the weekly model is derived from it and written back
  // through it, so the visual and JSON editors never disagree.
  let editing = $state(false);
  let draftText = $state('');
  let draftRole = $state('availability');
  let editorMode = $state<'visual' | 'json'>('visual');
  let draftNote = $state('');
  let serverProblems = $state<string[]>([]);
  let message = $state('');
  let messageKind = $state('');
  let busy = $state(false);
  let confirming = $state<'publish' | 'archive' | null>(null);
  let overdueDecision = $state<'refuse' | 'discard'>('refuse');
  let showJson = $state(false);

  let weekly = $derived(editing ? weeklyFromJson(draftText) : null);
  let jsonProblem = $derived(editing ? jsonError(draftText) : null);
  let localProblems = $derived(weekly ? weeklyProblems(weekly) : jsonProblem ? [jsonProblem] : []);
  // The read carries the shown definition as a JSON object; the view, the copy, and a new draft work on its text.
  let shownText = $derived(family ? JSON.stringify(family.definition) : '');
  let shownWeekly = $derived(family ? weeklyFromJson(shownText) : null);
  // A new calendar's editor opens from its URL only for a caller who may save it.
  let creating = $derived(missing && $params.role !== '' && canControlNow);
  let expectedLatest = $derived(shownState?.latestVersion ?? 0);

  // Opens once per page: after the first save the family exists, and the cached 404 must not reopen the template.
  let templateOpened = false;
  $effect(() => {
    if (creating && !editing && !templateOpened) {
      templateOpened = true;
      startDraft(definitionTemplate($params.role), $params.role);
    }
  });
  // JSON edited into something the visual editor cannot hold drops to the JSON editor rather than hiding the text.
  $effect(() => {
    if (editing && editorMode === 'visual' && weekly === null) editorMode = 'json';
  });

  function startDraft(text: string, role: string) {
    draftText = jsonError(text) === null ? prettyJson(text) : text;
    draftRole = role;
    draftNote = '';
    serverProblems = [];
    message = '';
    editorMode = weeklyFromJson(text) ? 'visual' : 'json';
    editing = true;
  }

  function cancelDraft() {
    editing = false;
    serverProblems = [];
    if (creating) location.hash = routes.calendars({ namespace: $scope }).slice(1);
  }

  // The page's refresh reads the preview too: the archive gate and the publish impact are read from it.
  function refresh() {
    void detail.refetch();
    if (family !== null) void preview.refetch();
  }

  async function refreshAll() {
    await queryClient.invalidateQueries({ queryKey: ['calendars'] });
    void preview.refetch();
    void history.refetch();
  }

  function show(version: number | null) {
    location.hash = routes.calendar(jobNamespace, calendarName, { namespace: $scope, version }).slice(1);
  }

  // One shape for the three control verbs: busy while the call runs, the answer worded as the family's message, the
  // page refreshed when the family is as asked, applied or already so, and a failed call shown where the answer would be.
  async function run(call: () => Promise<{ message: string; kind: string; applied: boolean; after?: () => void }>) {
    busy = true;
    message = '';
    try {
      const result = await call();
      message = result.message;
      messageKind = result.kind;
      if (result.applied) {
        await refreshAll();
        result.after?.();
      }
    } catch (error) {
      message = (error as Error).message;
      messageKind = 'bad';
    } finally {
      busy = false;
    }
  }

  function saveDraft() {
    return run(async () => {
      const result = await saveCalendarDraft(jobNamespace, calendarName, draftRole, draftText, expectedLatest, draftNote);
      serverProblems = result.problems ?? [];
      const applied = calendarVerbSucceeded(result.action);
      if (applied) editing = false;
      return {
        message: calendarControlMessage('draft', result.action, { version: result.version }),
        kind: applied ? 'ok' : result.action === 'rejected' ? 'warn' : 'bad',
        applied,
        after: () => show(result.version)
      };
    });
  }

  function publish(note: string) {
    confirming = null;
    if (!family) return;
    const shown = family;
    return run(async () => {
      const result = await publishCalendar(jobNamespace, calendarName, shown.currentVersion ?? 0, shown.shownVersion, overdueDecision, note);
      const reason = result.reason ?? (result.overdueSchedules.length > 0 ? `${result.overdueSchedules.length} schedules hold an overdue occurrence.` : null);
      const applied = calendarVerbSucceeded(result.action);
      return {
        message: calendarControlMessage('publish', result.action, { version: result.version, retimed: result.retimedSchedules.length, reason }),
        kind: applied ? 'ok' : 'warn',
        applied
      };
    });
  }

  function archive(note: string) {
    confirming = null;
    if (!family || family.currentVersion === null) return;
    const version = family.currentVersion;
    return run(async () => {
      const result = await archiveCalendar(jobNamespace, calendarName, version, note);
      return {
        message: calendarControlMessage('archive', result.action, { version: result.version, dependents: result.dependentCount }),
        kind: result.action === 'applied' ? 'ok' : 'warn',
        applied: result.action === 'applied'
      };
    });
  }

  let loading = $derived(detail.isPending);
  let error = $derived(detail.error instanceof Error ? detail.error.message : detail.error ? String(detail.error) : '');
  let backHref = $derived(routes.calendars({ namespace: $scope }));
  let scopeLabel = $derived(jobNamespace === 'sys' ? 'shared' : jobNamespace);
  let versionsOldestFirst = $derived(family ? [...family.versions].sort((a, b) => a.version - b.version) : []);
  let lifecycleText = $derived.by(() => {
    if (!family || !shownState) return '';
    if (family.currentVersion === null) {
      return 'No current version. A schedule bound to this calendar reports evaluation errors until one is published, and a deploy that binds it is refused.';
    }
    return `Version ${family.currentVersion} is current: a new binding follows it, and the next publication retimes every bound schedule.`;
  });
  let publishBody = $derived.by(() => {
    if (!family) return '';
    const version = family.shownVersion;
    if (!preview.data) {
      return `Version ${version} becomes current and every schedule bound to this calendar is retimed to it in one transaction. The forecast has not loaded, so they are not counted here.`;
    }
    const retimes =
      impact.retimed === 0
        ? `Version ${version} becomes current; no live schedule binds this calendar, so nothing is retimed.`
        : `Version ${version} becomes current and up to ${impact.retimed} bound ${impact.retimed === 1 ? 'schedule is' : 'schedules are'} retimed to it in one transaction; each cursor moves to the first occurrence after now.`;
    if (impact.overdue === 0) return retimes;
    const overdue =
      overdueDecision === 'discard'
        ? ` The ${impact.overdue} overdue ${impact.overdue === 1 ? 'occurrence is' : 'occurrences are'} discarded and audited as skipped.`
        : ` The publication is refused while ${impact.overdue} ${impact.overdue === 1 ? 'schedule holds' : 'schedules hold'} an overdue occurrence.`;
    return retimes + overdue;
  });
</script>

<Page title={calendarName}>
  {#snippet breadcrumb()}
    <a href={backHref}><Icon name="chevron-left" />Calendars</a>
  {/snippet}
  {#snippet actions()}
    <PageFreshness dataUpdatedAt={detail.dataUpdatedAt} isFetching={detail.isFetching} isError={!!detail.error} onRefresh={refresh} />
  {/snippet}

  {#if loading || error}
    <div class="panel">
      <StateView {loading} {error} loadingText="Loading calendar..." onRetry={() => detail.refetch()} />
    </div>
  {:else if missing && !creating}
    <div class="panel">
      <StateView emptyText={requestedVersion ? `Version ${requestedVersion} is not one of this calendar's.` : 'Calendar not found.'} />
      {#if requestedVersion}
        <div class="control-row"><button onclick={() => show(null)}>Show the current version</button></div>
      {:else if canControlNow}
        <p class="detail-help">Start it here as a new family in <span class="mono">{jobNamespace}</span>.</p>
        <div class="control-row">
          <button class="primary" onclick={() => (location.hash = routes.calendar(jobNamespace, calendarName, { namespace: $scope, role: 'availability' }).slice(1))}>
            <Icon name="plus" />Availability calendar
          </button>
          <button onclick={() => (location.hash = routes.calendar(jobNamespace, calendarName, { namespace: $scope, role: 'occurrence' }).slice(1))}>
            <Icon name="plus" />Occurrence calendar
          </button>
        </div>
      {/if}
    </div>
  {:else}
    <section class="entity-summary calendar-summary" aria-label="Calendar identity">
      <div class="entity-meta mono">{scopeLabel} / {calendarName} · {family?.role ?? draftRole}</div>
      {#if family && shownState}
        {#if family.currentVersion !== null}<VersionStatus status="published" version={family.currentVersion} />{:else}<span class="badge no-current">No current version</span>{/if}
        {#if !shownState.shownIsCurrent}
          <span class="shown-note"><span class="dim">showing</span> <VersionStatus status={shownState.shownStatus} version={family.shownVersion} /></span>
        {/if}
      {:else}
        <span class="badge warn">new</span>
      {/if}
    </section>

    <div class="detail-workspace">
      <div class="detail-main">
        {#if family}
          <section class="detail-panel" aria-label="Versions">
            <ol class="versions">
              {#each versionsOldestFirst as version (version.version)}
                <li>
                  <VersionChip
                    version={version.version}
                    status={version.status}
                    at={version.publishedAtUtc ?? version.createdAtUtc}
                    shown={version.version === family.shownVersion}
                    href={routes.calendar(jobNamespace, calendarName, { namespace: $scope, version: version.version })} />
                </li>
              {/each}
            </ol>
            {#if family.versionsTotal !== null && family.versionsTotal > family.versions.length}
              <p class="detail-help">Showing the newest {displayFormatter.number(family.versions.length)} of {displayFormatter.number(family.versionsTotal)} versions.</p>
            {/if}
          </section>
        {/if}

        <section class="detail-panel" aria-label="Definition">
          <div class="definition-head">
            <h2>
              {#if editing}
                {creating ? `New ${draftRole} calendar` : `Draft from version ${family?.shownVersion}`}
              {:else}
                Version {family?.shownVersion} definition
              {/if}
            </h2>
            <!-- A saved version never changes, so the page shows it as text and edits only a new draft. -->
            {#if !editing && family && canControlNow && shownState}
              <button onclick={() => startDraft(shownText, family!.role)}>
                <Icon name="plus" />{shownState.shownIsDraft ? 'Edit as a new draft' : 'Start a draft from this version'}
              </button>
            {/if}
          </div>

          {#if editing}
            {#if weekly !== null || editorMode === 'visual'}
              <div class="editor-modes" role="tablist" aria-label="Editor">
                <button type="button" role="tab" aria-selected={editorMode === 'visual'} class:active={editorMode === 'visual'} onclick={() => (editorMode = 'visual')}>Week</button>
                <button type="button" role="tab" aria-selected={editorMode === 'json'} class:active={editorMode === 'json'} onclick={() => (editorMode = 'json')}>JSON</button>
              </div>
            {:else}
              <p class="detail-help">
                {draftRole === 'availability'
                  ? 'The week editor holds a weekly-availability; a union, intersection, or subtraction is edited as JSON.'
                  : 'Occurrence calendars are edited as JSON: explicit-instants, daily-local-time, business-day, at-opening, or a union of them.'}
              </p>
            {/if}
            {#if editorMode === 'visual' && weekly}
              <WeeklyEditor model={weekly} onChange={(model) => (draftText = weeklyToJson(model))} />
            {:else}
              <textarea class="json-editor mono" bind:value={draftText} rows="18" spellcheck="false" aria-label="Definition JSON"></textarea>
            {/if}
            {#if localProblems.length > 0 || serverProblems.length > 0}
              <ul class="problems" role="alert" aria-label="Definition problems">
                {#each localProblems as problem}<li>{problem}</li>{/each}
                {#each serverProblems as problem}<li>{problem}</li>{/each}
              </ul>
            {/if}
            <label class="control-reason">
              Note (optional, recorded with the draft)
              <textarea bind:value={draftNote} rows="2" maxlength="512"></textarea>
            </label>
            <div class="detail-form-actions">
              <button class="primary" disabled={busy || localProblems.length > 0 || !canControlNow} onclick={saveDraft}>Save as version {expectedLatest + 1}</button>
              <button disabled={busy} onclick={cancelDraft}>Cancel</button>
            </div>
          {:else if family}
            {#if shownWeekly}
              <WeeklyEditor model={shownWeekly} readOnly={true} />
              <button type="button" class="link-button" onclick={() => (showJson = !showJson)}>{showJson ? 'Hide JSON' : 'Show JSON'}</button>
            {/if}
            {#if !shownWeekly || showJson}
              <pre class="definition mono">{prettyJson(shownText)}</pre>
            {/if}
            <div class="control-row definition-actions">
              <CopyButton value={shownText} label="Copy definition" showLabel={true} />
            </div>
          {/if}
          {#if message}
            <div class="control-message {messageKind}" role="status">{message}</div>
          {/if}
        </section>

        {#if family}
          <section class="detail-panel" aria-label="Forecast">
            <h2>Forecast</h2>
            <CalendarPreview
              preview={preview.data ?? null}
              loading={preview.isFetching}
              error={preview.error instanceof Error ? preview.error.message : ''}
              limit={previewLimit}
              onLimit={(limit) => (previewLimit = limit)}
              onRefresh={() => preview.refetch()} />
          </section>

          <section class="detail-panel" aria-label="Bound schedules">
            <h2>Bound schedules</h2>
            {#if family.dependents.length === 0}
              <StateView emptyText="No schedule binds this calendar through the catalog." />
            {:else}
              <p class="detail-help">
                Each follows the current version; a publication retimes every one here to the new one in one transaction.
                {#if impact.overdue > 0}{impact.overdue} of them hold an overdue occurrence, which a publication must decide.{/if}
              </p>
              <DataTable>
                <caption class="sr-only">Bound schedules</caption>
                <thead><tr><th>Schedule</th><th>Binding</th><th>Status</th><th class="mobile-hide">Delivery</th></tr></thead>
                <tbody>
                  {#each family.dependents as dependent (`${dependent.jobNamespace}/${dependent.jobName}/${dependent.scheduleName}/${dependent.binding}`)}
                    {@const key = `${dependent.jobNamespace}/${dependent.jobName}/${dependent.scheduleName}`}
                    <tr class:overdue={overdueKeys.has(key)}>
                      <td><a href={routes.schedule(dependent.jobNamespace, dependent.jobName, dependent.scheduleName)}>{key}</a></td>
                      <td>{dependent.binding}</td>
                      <td><ScheduleStatus status={dependent.status} />{#if overdueKeys.has(key)} <span class="badge bad">overdue</span>{/if}</td>
                      <td class="mobile-hide">{dependent.scheduledDelivery}</td>
                    </tr>
                  {/each}
                </tbody>
              </DataTable>
              {#if impact.overdue > overdueListed}
                <p class="detail-help">Overdue marks cover the first {displayFormatter.number(overdueListed)} of {displayFormatter.number(impact.overdue)} overdue schedules.</p>
              {/if}
              {#if family.dependentsTotal !== null && family.dependentsTotal > family.dependents.length}
                <p class="detail-help">Showing {displayFormatter.number(family.dependents.length)} of {displayFormatter.number(family.dependentsTotal)} schedules.</p>
              {/if}
            {/if}
          </section>

          <ChangeHistory history={history.data ?? []} loading={history.isPending} emptyText="No recorded calendar changes." />
        {/if}
      </div>

      <aside class="detail-rail">
        <section class="detail-panel">
          <h2>Lifecycle</h2>
          {#if family && shownState}
            <p>{lifecycleText}</p>
            {#if shownState.shownIsDraft}
              <p class="detail-help">Version {family.shownVersion} is a draft: no schedule follows it until it is published.</p>
            {/if}
            {#if shownState.canPublish}
              <ul class="publish-impact" aria-label="What publishing changes">
                <li>{impactText ?? 'Counting the bound schedules...'}</li>
                {#if forecastText}<li>{forecastText}</li>{/if}
              </ul>
              {#if impact.overdue > 0}
                <fieldset class="overdue-choice">
                  <legend>{impact.overdue} bound {impact.overdue === 1 ? 'schedule holds' : 'schedules hold'} an overdue occurrence</legend>
                  <label><input type="radio" bind:group={overdueDecision} value="refuse" /> Refuse the publication until they drain</label>
                  <label><input type="radio" bind:group={overdueDecision} value="discard" /> Discard them, audited as skipped</label>
                </fieldset>
              {/if}
            {/if}
            <div class="control-row">
              {#if shownState.canPublish}
                <button class="primary" disabled={busy} onclick={() => (confirming = 'publish')}><Icon name="check" />Publish version {family.shownVersion}</button>
              {/if}
              {#if shownState.canArchive}
                <button
                  class="danger-outline"
                  disabled={busy || archiveHold !== null}
                  aria-describedby={archiveHold !== null ? 'archive-hold' : undefined}
                  onclick={() => (confirming = 'archive')}><Icon name="minus-circle" />Archive calendar</button>
              {/if}
            </div>
            {#if shownState.canArchive && archiveHold !== null}
              <p class="detail-help archive-hold" id="archive-hold">{archiveHold}</p>
            {/if}
          {:else}
            <p>A new family: its first saved version is a draft, published from here once it reads right.</p>
          {/if}
        </section>

        <section class="detail-panel go-to" aria-label="Go to">
          <p class="detail-kicker">Go to</p>
          <nav>
            <a href={routes.schedules({ namespace: $scope })}>Schedules</a>
            <a href={routes.events({ namespace: jobNamespace })}>Events</a>
          </nav>
        </section>
      </aside>
    </div>
  {/if}
</Page>

{#if confirming === 'publish' && family}
  <ConfirmAction
    title={`Publish version ${family.shownVersion} of ${calendarName}?`}
    body={publishBody}
    confirmLabel="Publish"
    requireReason={true}
    onConfirm={publish}
    onCancel={() => (confirming = null)} />
{/if}

{#if confirming === 'archive' && family}
  <ConfirmAction
    title={`Archive ${calendarName}?`}
    body={`Version ${family.currentVersion}, the current one, is retired: the calendar has no current version until another is published, and a deploy that binds it is refused until then.`}
    confirmLabel="Archive calendar"
    danger={true}
    requireReason={true}
    onConfirm={archive}
    onCancel={() => (confirming = null)} />
{/if}

<style>
  /* Versions are the family's one sequence, so they read left to right as a strip, oldest first. */
  .versions {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin: 0;
    padding: 0;
    list-style: none;
  }
  .versions li { display: flex; }
  /* Three items share the summary; on a phone the badges drop under the name rather than crushing it. */
  .calendar-summary { flex-wrap: wrap; gap: 10px 20px; }
  .calendar-summary .entity-meta { flex: 1 1 auto; min-width: 16ch; }
  .shown-note { display: inline-flex; align-items: center; gap: 6px; white-space: nowrap; }
  /* A badge capitalizes its one word; this one is a phrase, so it reads in sentence case. */
  .no-current { text-transform: none; }
  .definition-head {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    justify-content: space-between;
    gap: 8px 16px;
    margin-bottom: 6px;
  }
  .definition-head h2 { margin: 0; }
  /* What a publication changes sits on the button's own panel, so it is read before the click. */
  .publish-impact {
    margin: 0 0 12px;
    padding: 8px 12px;
    border-left: 3px solid var(--accent);
    background: var(--panel-subtle);
    list-style: none;
    font-size: var(--text-sm);
  }
  .publish-impact li + li { margin-top: 4px; color: var(--muted); }
  .archive-hold { margin: 8px 0 0; }
  .editor-modes {
    display: inline-flex;
    gap: 0;
    margin-bottom: 12px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    overflow: hidden;
  }
  .editor-modes button {
    border: 0;
    border-radius: 0;
    padding: 5px 14px;
  }
  .editor-modes button.active { background: var(--nav-active-bg); color: var(--accent); }
  .json-editor {
    width: 100%;
    padding: 10px 12px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel-subtle);
    color: var(--ink);
    font-size: var(--text-sm);
    line-height: 1.45;
    resize: vertical;
    white-space: pre;
    tab-size: 2;
  }
  .json-editor:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
  .problems {
    margin: 10px 0 0;
    padding: 8px 12px 8px 28px;
    border: 1px solid var(--bad);
    border-radius: var(--radius-control);
    background: var(--badge-bad-bg);
    color: var(--bad);
    font-size: var(--text-sm);
  }
  .problems li { margin: 2px 0; }
  .control-reason { margin-top: 14px; }
  .definition {
    margin: 0;
    padding: 10px 12px;
    border: 1px solid var(--line);
    border-radius: var(--radius-control);
    background: var(--panel-subtle);
    font-size: var(--text-sm);
    line-height: 1.45;
    overflow: auto;
    max-height: 420px;
  }
  .link-button {
    border: 0;
    padding: 4px 0;
    margin: 6px 0;
    background: none;
    color: var(--muted);
    font-size: var(--text-sm);
  }
  .link-button:hover:not(:disabled) { color: var(--accent); }
  .definition-actions { margin-top: 12px; margin-bottom: 0; }
  .overdue-choice {
    margin: 0 0 12px;
    padding: 8px 12px;
    border: 1px solid var(--warn);
    border-radius: var(--radius-control);
    font-size: var(--text-sm);
  }
  .overdue-choice legend { color: var(--warn); font-weight: 600; padding: 0 4px; }
  .overdue-choice label { display: flex; gap: 8px; align-items: center; padding: 3px 0; }
</style>
