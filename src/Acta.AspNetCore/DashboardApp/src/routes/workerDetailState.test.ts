import assert from 'node:assert/strict';
import test from 'node:test';
import { workerStatusInterpretation, workerSupportSummary, type WorkerDetailShape } from './workerDetailState.ts';

test('worker status interpretations distinguish lifecycle states', () => {
  assert.match(workerStatusInterpretation('active'), /eligible to claim/);
  assert.match(workerStatusInterpretation('draining'), /without claiming new/);
  assert.match(workerStatusInterpretation('stopped'), /Stopped cleanly/);
  assert.match(workerStatusInterpretation('dead'), /expired unexpectedly/);
});

test('worker support summary carries durable identity and version evidence', () => {
  const worker: WorkerDetailShape = {
    workerId: 42,
    jobNamespace: 'billing',
    status: 'active',
    host: 'host-a',
    deploymentVersion: 'deploy-1',
    engineVersion: 'engine-1',
    dotnetVersion: '.NET 10',
    processId: 4242,
    maxConcurrency: 8,
    lastHeartbeatAtUtc: '2026-07-14T08:00:00Z',
    startedAtUtc: '2026-07-14T07:00:00Z',
    modifiedAtUtc: '2026-07-14T08:00:00Z'
  };

  const summary = workerSupportSummary(worker);
  assert.match(summary, /Acta worker 42/);
  assert.match(summary, /host-a\/4242/);
  assert.match(summary, /deploy-1/);
  assert.match(summary, /Last heartbeat: 2026-07-14/);
});
