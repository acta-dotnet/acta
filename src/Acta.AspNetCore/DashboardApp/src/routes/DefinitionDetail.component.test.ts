import { fireEvent, render, screen } from '@testing-library/svelte';
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
  concurrencyLimit: null,
  concurrencyLimitOverride: null,
  concurrencyLimitEffective: null,
  rateLimit: null,
  rateLimitOverride: null,
  rateLimitEffective: null,
  rateKey: null,
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

const retiredEvent = {
  jobEventId: 3,
  eventCode: 'definition.retired',
  createdAtUtc: '2026-07-15T08:00:00Z',
  actorCode: 'operator',
  actorKey: 'marko',
  reasonMessage: 'Handler left the build'
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
  it('requests each definition-change event code, not the unfiltered job-lineage stream', async () => {
    const calls: URL[] = [];
    stubFetch((url) => {
      calls.push(url);
      if (url.pathname.endsWith('/events')) return pagedResponse([overrideEvent]);
      return new Response(JSON.stringify(definition), { status: 200 });
    });

    render(DefinitionDetailHarness, { jobNamespace, jobName });
    await screen.findByText(jobName);

    const codes = calls.filter((url) => url.pathname.endsWith('/events')).map((url) => url.searchParams.get('eventCode'));
    expect(codes).toContain('definition.overrides-updated');
    expect(codes).toContain('definition.retired');
    expect(codes).not.toContain(null);
  });

  it('renders the definition-change events and none of the execution noise for the same definition', async () => {
    stubFetch((url) => {
      if (url.pathname.endsWith('/events')) {
        // The unfiltered URL is what the flooded panel used to hit; only the two filtered requests
        // should ever be issued, and only they return definition-change rows.
        const code = url.searchParams.get('eventCode');
        if (code === 'definition.overrides-updated') return pagedResponse([overrideEvent]);
        if (code === 'definition.retired') return pagedResponse([retiredEvent]);
        return pagedResponse([executionEvent]);
      }
      return new Response(JSON.stringify(definition), { status: 200 });
    });

    render(DefinitionDetailHarness, { jobNamespace, jobName });

    expect(await screen.findByText('Raised max attempts for the holiday backlog')).toBeTruthy();
    expect(await screen.findByText('Handler left the build')).toBeTruthy();
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

describe('DefinitionDetail retire', () => {
  function stubDetail(row: Record<string, unknown>, calls: { url: URL; init?: RequestInit }[] = []) {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = new URL(input as string | URL);
        calls.push({ url, init });
        if (url.pathname.endsWith('/retire')) {
          return new Response(
            JSON.stringify({ jobNamespace, jobName, action: 'applied', message: 'Definition retired.' }),
            { status: 200 }
          );
        }
        if (url.pathname.endsWith('/events')) return pagedResponse([]);
        return new Response(JSON.stringify(row), { status: 200 });
      })
    );
    return calls;
  }

  it('offers the retire action on an active definition', async () => {
    stubDetail(definition);
    render(DefinitionDetailHarness, { jobNamespace, jobName });

    const button = (await screen.findByText('Retire definition')) as HTMLButtonElement;
    expect(button.disabled).toBe(false);
  });

  it('disables the retire action once the definition is retired', async () => {
    stubDetail({ ...definition, status: 'retired' });
    render(DefinitionDetailHarness, { jobNamespace, jobName });

    const button = (await screen.findByText('Retire definition')) as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });

  it('posts the retire with the version it read and the typed reason', async () => {
    const calls = stubDetail(definition);
    render(DefinitionDetailHarness, { jobNamespace, jobName });

    await fireEvent.click(await screen.findByText('Retire definition'));
    await fireEvent.input(screen.getByLabelText(/Reason \(required/), { target: { value: 'handler dropped' } });
    await fireEvent.click(screen.getByText('Retire and cancel queued jobs'));

    await screen.findByText('Definition retired.');
    const retire = calls.find((call) => call.url.pathname.endsWith('/retire'));
    expect(retire).toBeTruthy();
    expect(retire!.init?.method).toBe('POST');
    expect(JSON.parse(String(retire!.init?.body))).toEqual({ expectedVersion: 3, reasonMessage: 'handler dropped' });
  });
});
