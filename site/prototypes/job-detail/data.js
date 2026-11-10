(() => {
  const ref = (tail) => `job_019f9b3509487d1b911f2020${tail}`;
  const node = (tail, name, status, extra = {}) => ({
    ref: ref(tail),
    name,
    status,
    namespace: 'publishing',
    kind: 'ordinary',
    attempts: 1,
    maxAttempts: 5,
    created: '2026-11-10T13:00:00Z',
    modified: '2026-11-10T13:12:00Z',
    duration: '2.4s',
    children: [],
    schedules: [],
    checkpoints: [],
    events: [],
    input: null,
    result: null,
    ...extra,
  });

  const chapters = Array.from({ length: 6 }, (_, index) => node(
    `b42a100${index}`,
    `render-chapter-${index + 1}`,
    index === 5 ? 'executing' : 'succeeded',
    { duration: `${index + 2}.1s`, input: { manuscriptId: 'MS-2048', chapter: index + 1 } },
  ));
  const typeset = node('b7d23061', 'typeset-pdf', 'suspended', {
    children: chapters,
    wait: { kind: 'children', name: 'chapters' },
    headline: 'Waiting for the final chapter render.',
    input: { manuscriptId: 'MS-2048', template: 'journal-article' },
  });
  const validate = node('c64f128a', 'validate-assets', 'failed', {
    attempts: 3,
    maxAttempts: 3,
    reason: 'Figure 04 has a resolution of 96 DPI. The publication requires at least 300 DPI.',
    failureCode: 'AssetResolutionException',
    input: { manuscriptId: 'MS-2048', asset: 'figure-04.tif', minimumDpi: 300 },
    events: [
      { title: 'Execution 3 failed', detail: 'AssetResolutionException · figure-04.tif', when: '6m ago', at: '2026-11-10T13:12:00Z', status: 'failed' },
      { title: 'Execution 2 failed', detail: 'The same source asset failed validation.', when: '7m ago', at: '2026-11-10T13:11:00Z', status: 'failed' },
    ],
  });
  const cover = node('d8a73109', 'render-cover', 'succeeded', {
    result: { artifact: 'cover-MS-2048.pdf', pages: 1 },
    duration: '1.8s',
  });
  const epub = node('e1f942b8', 'export-epub', 'executing', {
    headline: 'Exporting the publication package.',
    worker: { ref: 'wrk_019f9b3509487d1b911f2020a18b4206', host: 'publisher-02', heartbeat: '2s ago' },
    duration: '18s',
  });

  const publication = node('a94b72c1', 'publish-manuscript', 'suspended', {
    headline: 'A child job needs attention.',
    reason: 'Asset validation failed. Other child jobs are still in progress.',
    wait: { kind: 'children', name: 'build-publication' },
    children: [typeset, validate, cover, epub],
    selected: validate.ref,
    input: { manuscriptId: 'MS-2048', edition: '2026.11', formats: ['pdf', 'epub'] },
    checkpoints: [
      { kind: 'variable', name: 'manuscript-id', value: 'MS-2048' },
      { kind: 'step', name: 'load-manuscript', value: 'succeeded' },
    ],
    correlation: 'submission-MS-2048',
    events: [
      { title: 'Waiting for child jobs', detail: 'build-publication', when: '6m ago', at: '2026-11-10T13:12:00Z', status: 'suspended' },
      { title: 'Child jobs created', detail: '4 direct children', when: '8m ago', at: '2026-11-10T13:10:00Z', status: 'ready' },
      { title: 'Execution 1 started', detail: 'publisher-01', when: '8m ago', at: '2026-11-10T13:10:00Z', status: 'executing' },
    ],
  });
  const failed = node('8b4f20d3', 'send-invoice', 'failed', {
    namespace: 'billing',
    attempts: 5,
    maxAttempts: 5,
    headline: 'The mail provider timed out.',
    reason: 'All 5 attempts failed. Check the provider before running this job again.',
    failureCode: 'TimeoutException',
    correlation: 'order-1842',
    deduplication: 'invoice-1842',
    input: { invoiceId: 1842, recipient: 'accounts@example.test' },
    events: [
      { title: 'Execution 5 failed', detail: 'TimeoutException · SMTP provider did not respond within 30s.', when: '6m ago', at: '2026-11-10T13:12:00Z', status: 'failed' },
      { title: 'Execution 4 failed', detail: 'The same provider timed out.', when: '9m ago', at: '2026-11-10T13:09:00Z', status: 'failed' },
      { title: 'Execution 3 failed', detail: 'The same provider timed out.', when: '10m ago', at: '2026-11-10T13:08:00Z', status: 'failed' },
      { title: 'Execution 2 failed', detail: 'The same provider timed out.', when: '12m ago', at: '2026-11-10T13:06:00Z', status: 'failed' },
      { title: 'Execution 1 failed', detail: 'The same provider timed out.', when: '13m ago', at: '2026-11-10T13:05:00Z', status: 'failed' },
      { title: 'Job enqueued', detail: 'Input persisted successfully.', when: '18m ago', at: '2026-11-10T13:00:00Z', status: 'ready' },
    ],
  });
  const video = node('f92a18b6', 'transcode-video', 'suspended', {
    namespace: 'media',
    headline: 'Waiting for rendition jobs.',
    wait: { kind: 'children', name: 'renditions' },
    children: Array.from({ length: 12 }, (_, index) => node(
      `f12a0${String(index).padStart(3, '0')}`,
      `encode-${['2160p', '1080p', '720p', '480p'][index % 4]}-${['h264', 'vp9', 'av1'][Math.floor(index / 4)]}`,
      index < 6 ? 'succeeded' : index < 9 ? 'executing' : 'ready',
      { namespace: 'media', input: { videoId: 'VID-5092', rendition: index + 1 }, duration: index < 6 ? '42s' : '18s' },
    )),
    input: { videoId: 'VID-5092', source: 'uploads/keynote.mov' },
    checkpoints: [{ kind: 'step', name: 'inspect-source', value: 'succeeded' }],
    events: [{ title: 'Waiting for renditions', detail: '12 direct child jobs', when: '1m ago', at: '2026-11-10T13:17:00Z', status: 'suspended' }],
  });
  const recurring = node('72c9e1a4', 'reconcile-ledger', 'idle', {
    namespace: 'billing',
    kind: 'permanent',
    headline: 'Next scheduled firing in 42m.',
    attempts: 128,
    schedules: [
      { name: 'hourly', expression: '0 * * * *', zone: 'UTC', next: 'Today at 14:00', at: '2026-11-10T14:00:00Z', status: 'Active' },
      { name: 'nightly', expression: '0 2 * * *', zone: 'UTC', next: 'Tomorrow at 02:00', at: '2026-11-11T02:00:00Z', status: 'Active' },
    ],
    events: [
      { title: 'Execution 128 completed', detail: 'The permanent job returned to idle.', when: '18m ago', at: '2026-11-10T13:00:00Z', status: 'succeeded' },
      { title: 'Hourly schedule fired', detail: 'hourly · UTC', when: '18m ago', at: '2026-11-10T13:00:00Z', status: 'ready' },
    ],
  });
  const completed = node('53b0a21f', 'generate-report', 'succeeded', {
    namespace: 'reports',
    headline: 'Report generated successfully.',
    duration: '2.4s',
    input: { month: '2026-10', region: 'EU' },
    result: { report: 'monthly-sales-2026-10.pdf', pages: 18, total: 384120.50, currency: 'EUR' },
    events: [{ title: 'Execution 1 completed', detail: 'Result persisted successfully.', when: '6m ago', at: '2026-11-10T13:12:00Z', status: 'succeeded' }],
  });
  const approval = node('4ac1902d', 'approve-publication', 'suspended', {
    headline: 'Waiting for editor approval.',
    wait: { kind: 'signal', name: 'editor-approval', due: '2026-11-12T18:00:00Z', dueLabel: '12 Nov at 18:00 UTC' },
    input: { manuscriptId: 'MS-2048', reviewer: 'editorial-board' },
    checkpoints: [{ kind: 'variable', name: 'pdf-artifact', value: 'proof-MS-2048.pdf' }],
    events: [{ title: 'Waiting for a signal', detail: 'editor-approval', when: '6m ago', at: '2026-11-10T13:12:00Z', status: 'suspended' }],
  });

  window.ACTA_PROTOTYPES = {
    scenarios: {
      publication: { label: 'Parent with a failed child', job: publication },
      failed: { label: 'Failed job', job: failed },
      video: { label: 'Large child family', job: video },
      recurring: { label: 'Recurring job', job: recurring },
      completed: { label: 'Completed job', job: completed },
      approval: { label: 'Waiting for approval', job: approval },
    },
    variants: {
      focus: { letter: 'A', title: 'Focus', description: 'One page. Current issue first. Details on demand.', recommended: true },
      workspace: { letter: 'B', title: 'Workspace', description: 'A larger lineage view with a contextual inspector.' },
      outline: { letter: 'C', title: 'Outline', description: 'An expandable job tree with a compact activity view.' },
    },
  };
})();
