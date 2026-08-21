import { render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import DefinitionDetailHarness from '../test/DefinitionDetailHarness.svelte';

const jobNamespace = 'billing';
const jobName = 'send-invoice';

const definition = {
  jobNamespace,
  jobName,
  version: 3,
  status: 'active',
  inputTypeName: 'SendInvoiceInput',
  outputTypeName: 'SendInvoiceOutput',
  priority: 'normal',
  priorityOverride: null,
  priorityEffective: 'normal',
  maxAttempts: 5,
  maxAttemptsOverride: null,
  maxAttemptsEffective: 5,
  backoff: 'exponential',
  backoffOverride: null,
  backoffEffective: 'exponential',
  executionTimeoutSeconds: 300,
  executionTimeoutSecondsOverride: null,
  executionTimeoutSecondsEffective: 300,
  deadlineSeconds: null,
  deadlineSecondsOverride: null,
  deadlineSecondsEffective: null,
  deadlineBehavior: 'fail',
  deadlineBehaviorOverride: null,
  deadlineBehaviorEffective: 'fail',
  jobRetentionSeconds: 86400,
  jobRetentionSecondsOverride: null,
  jobRetentionSecondsEffective: 86400,
  auditLevel: 'standard',
  auditLevelOverride: null,
  auditLevelEffective: 'standard',
  alertProfile: 'default',
  alertProfileOverride: null,
  alertProfileEffective: 'default',
  alertChannelName: null,
  alertChannelNameOverride: null,
  alertChannelNameEffective: null,
  runbookUrl: null,
  runbookUrlOverride: null,
  runbookUrlEffective: null,
  displayName: null,
  displayNameOverride: null,
  displayNameEffective: null,
  description: null,
  descriptionOverride: null,
  descriptionEffective: null
};

function pagedResponse(items: unknown[]) {
  return new Response(
    JSON.stringify({ items, nextCursor: null, hasMore: false, pageSize: 20, totalCount: items.length }),
    { status: 200 }
  );
}

const overrideEvent = {
  jobEventId: 1,
  eventCode: 'definition.overrides-updated',
  createdAtUtc: '2026-07-14T08:00:00Z',
  actorCode: 'operator',
  actorKey: 'marko',
  reasonMessage: 'Raised max attempts for the holiday backlog'
};

const executionEvent = {
  jobEventId: 2,
  eventCode: 'job.execution-finished',
  createdAtUtc: '2026-07-14T09:00:00Z',
  actorCode: 'system',
  actorKey: null,
  reasonMessage: null
};

function stubFetch(handler: (url: URL) => Response) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => handler(new URL(input as string | URL))
  ));
}

describe('DefinitionDetail change history', () => {
  it('requests the definition-change event code, not the unfiltered job-lineage stream', async () => {
    const calls: URL[] = [];
    stubFetch((url) => {
      calls.push(url);
      if (url.pathname.endsWith('/events')) return pagedResponse([overrideEvent]);
      return new Response(JSON.stringify(definition), { status: 200 });
    });

    render(DefinitionDetailHarness, { jobNamespace, jobName });
    await screen.findByText(jobName);

    const eventsCall = calls.find((url) => url.pathname.endsWith('/events'));
    expect(eventsCall).toBeTruthy();
    expect(eventsCall!.searchParams.get('eventCode')).toBe('definition.overrides-updated');
  });

  it('renders the override event and none of the execution noise for the same definition', async () => {
    stubFetch((url) => {
      if (url.pathname.endsWith('/events')) {
        // The unfiltered URL is what the flooded panel used to hit; only the filtered request
        // should ever be issued, and only it returns the override row.
        return url.searchParams.get('eventCode') === 'definition.overrides-updated'
          ? pagedResponse([overrideEvent])
          : pagedResponse([executionEvent]);
      }
      return new Response(JSON.stringify(definition), { status: 200 });
    });

    render(DefinitionDetailHarness, { jobNamespace, jobName });

    expect(await screen.findByText('Raised max attempts for the holiday backlog')).toBeTruthy();
    expect(screen.queryByText('job.execution-finished')).toBeNull();
  });

  it('shows the empty-history text when the definition has no override events', async () => {
    stubFetch((url) => {
      if (url.pathname.endsWith('/events')) return pagedResponse([]);
      return new Response(JSON.stringify(definition), { status: 200 });
    });

    render(DefinitionDetailHarness, { jobNamespace, jobName });

    expect(await screen.findByText('No recorded policy changes.')).toBeTruthy();
  });
});
