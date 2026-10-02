(() => {
  const root = document.getElementById('claiming');
  if (!root) return;
  const get = name => document.getElementById(`claiming-${name}`);
  // Each frame is a persisted-state illustration, not a timing simulation.
  const job = (id, state, label) => ({ id, state, label });
  const ready = id => job(id, 'ready', 'Ready');
  const blocked = id => job(id, 'blocked', 'Blocked');
  const running = (id, worker) => job(id, 'running', `Worker ${worker} · leased`);
  const done = id => job(id, 'done', 'Succeeded');
  const idle = ['Available', 'Available'];
  const independent = [ready('C1'), ready('C2')];
  const frames = [
    { name: 'Enqueue', title: 'Only the head of each lane is Ready.', text: 'A2 waits behind A1 even if A2 has higher priority. B1 and the unlaned jobs can run independently. This example shows Direct execution; Buffered workers claim before starting a handler.', lanes: [[ready('A1'), blocked('A2'), blocked('A3')], [ready('B1'), blocked('B2')], independent], workers: idle },
    { name: 'Claim', title: 'Two peers claim two eligible heads.', text: 'SQL atomically records each owner and lease. Worker 1 takes A1; Worker 2 takes B1. A2, A3 and B2 remain Blocked. This is one possible selection: eligible jobs compete by priority, next-run time, then job ID.', lanes: [[running('A1', 1), blocked('A2'), blocked('A3')], [running('B1', 2), blocked('B2')], independent], workers: ['Running A1 · lease renewed by heartbeat', 'Running B1 · lease renewed by heartbeat'] },
    { name: 'Finish', title: 'Finishing A1 opens the gate for A2.', text: 'A1 succeeds and A2 becomes Ready in the same SQL transaction. A terminal failure or cancellation also advances the lane. A2 can be claimed by any peer; customer-42 is not attached to Worker 1.', lanes: [[done('A1'), ready('A2'), blocked('A3')], [running('B1', 2), blocked('B2')], independent], workers: idle.map((s, i) => i ? 'Running B1 · lease renewed by heartbeat' : s) },
    { name: 'Retry', title: 'A retry holds its lane, not the whole namespace.', text: 'B1 fails with attempts remaining and waits until its next retry is due. It keeps its place: B2 cannot overtake it. Worker 2 is free and takes A2 from the other lane. Delays, pauses and durable waits can also hold a lane head.', lanes: [[done('A1'), running('A2', 2), blocked('A3')], [job('B1', 'waiting', 'Retry not due'), blocked('B2')], independent], workers: ['Available', 'Running A2 · lease renewed by heartbeat'] },
    { name: 'Recover', title: 'An expired lease returns the same job to eligible work.', text: 'Worker 2 disappears and stops renewing A2. After its lease lapses, the recovery job makes A2 available again; Worker 1 claims it in a new execution and resumes from recorded state. A3 stays Blocked. Recovery is not completion, and external effects still need idempotency.', lanes: [[done('A1'), running('A2', 1), blocked('A3')], [job('B1', 'waiting', 'Retry not due'), blocked('B2')], independent], workers: ['Recovered A2 · new lease', 'Offline · old lease expired'] }
  ];
  const pending = id => job(id, 'waiting', 'Pending');
  const held = id => job(id, 'blocked', 'Pending · behind older row');
  const deliveries = {
    A1: { row: 41, name: 'Reserve stock', handler: 'reserve-stock', order: 1042 },
    A2: { row: 42, name: 'Charge card', handler: 'charge-card', order: 1042 },
    A3: { row: 43, name: 'Send receipt', handler: 'send-receipt', order: 1042 },
    A4: { row: 44, name: 'Dispatch parcel', handler: 'dispatch-parcel', order: 1042 },
    B1: { row: 51, name: 'Reserve stock', handler: 'reserve-stock', order: 1073 },
    B2: { row: 52, name: 'Charge card', handler: 'charge-card', order: 1073 }
  };
  const outboxFrames = [
    { name: 'Save the order', title: 'Checkout saves the order and a list of work to deliver.', text: 'The shop saves order #1042 and inserts four requests into its own acta_outbox table in one transaction. Each carries lane order-1042 and an order ID as input. Order #1073 has its own lane. These are saved requests, not jobs in the Acta database yet.', source: [[pending('A1'), held('A2'), held('A3'), held('A4')], [pending('B1'), held('B2')]], lanes: [[], [], []], workers: idle, transfer: 'Waiting for the relay to pick up the saved requests', relay: 'The outbox is this ordinary SQL table inside the shop database.', phase: 0 },
    { name: 'Claim source rows', title: 'The relay picks the first request for each order.', text: 'The relay claims row 41 (reserve stock for #1042) and row 51 (reserve stock for #1073). Their source leases protect delivery. Rows 42–44 remain Pending behind row 41; they cannot pass it, even if another relay runs. Neither reserve-stock handler has started.', source: [[job('A1', 'running', 'Claimed · relay lease'), held('A2'), held('A3'), held('A4')], [job('B1', 'running', 'Claimed · relay lease'), held('B2')]], lanes: [[], [], []], workers: idle, transfer: '41 / A1 · reserve-stock({ orderId: 1042 }) → Acta database', relay: 'Source claim = permission to deliver a request, not to run its handler.', phase: 1 },
    { name: 'Commit target jobs', title: 'The same request now has a durable job in Acta.', text: 'The relay commits A1 and B1 into the separate Acta database. Notice that row 41 still exists in the shop while A1 is Ready in the ledger. If the relay crashes here, it repeats the delivery with deduplication key reserve-stock:1042 and finds the existing job instead of inserting another.', source: [[job('A1', 'running', 'Claimed · target committed'), held('A2'), held('A3'), held('A4')], [job('B1', 'running', 'Claimed · target committed'), held('B2')]], lanes: [[ready('A1')], [ready('B1')], []], workers: idle, transfer: '41 / A1 → job A1 committed · same input, lane and deduplication key', relay: 'Two database commits: the shop transaction and the later Acta transaction.', phase: 1 },
    { name: 'Delete delivered rows', title: 'The job stays. Its temporary delivery row disappears.', text: 'After the Acta commit, the relay deletes source rows 41 and 51. The shop order remains saved; A1 and B1 remain Ready in Acta. The outbox now exposes row 42 (charge card for #1042) and row 52 (charge card for #1073) for the next delivery.', source: [[pending('A2'), held('A3'), held('A4')], [pending('B2')]], removed: ['A1', 'B1'], lanes: [[ready('A1')], [ready('B1')], []], workers: idle, transfer: '✓ A1 and B1 committed → delete rows 41 and 51 from the shop outbox', relay: 'The outbox holds requests until delivery; the ledger holds jobs through execution.', phase: 1 },
    { name: 'Run + deliver next', title: 'Reserve stock runs while charge card waits in the ledger.', text: 'Worker 1 claims A1 and runs reserve-stock for #1042. The relay can already deliver A2, so row 42 leaves the outbox. A2 is Blocked in the ledger until A1 finishes. Delivery ordering and execution ordering are two separate gates. Order #1073 progresses independently.', source: [[pending('A3'), held('A4')], []], removed: ['A2', 'B2'], lanes: [[running('A1', 1), blocked('A2')], [running('B1', 2), blocked('B2')], []], workers: ['Running reserve-stock(#1042) · A1', 'Running reserve-stock(#1073) · B1'], transfer: '42 / A2 · charge-card({ orderId: 1042 }) → job A2 Blocked', relay: 'The relay does not wait for reserve-stock to finish before delivering charge-card.', phase: 2 },
    { name: 'Delivery retry', title: 'An unknown receipt handler holds the next delivery.', text: 'Suppose send-receipt is not registered at the target. Row 43 cannot be admitted and waits for a delivery retry; row 44 (dispatch parcel) stays behind it at the source. Meanwhile A1 has succeeded and Worker 1 runs A2. The admitted jobs keep progressing, and order #1073 can finish.', source: [[job('A3', 'waiting', 'Pending · retry later'), held('A4')], []], lanes: [[done('A1'), running('A2', 1)], [done('B1'), done('B2')], []], workers: ['Running charge-card(#1042) · A2', 'Available'], transfer: '43 / A3 · send-receipt → rejected: unknown job route', relay: 'No A3 job exists in the ledger. This is a delivery failure, not a handler failure.', phase: 1 },
    { name: 'Quarantine', title: 'Quarantine lets the source lane move past a poison request.', text: 'If row 43 exhausts its delivery rejection budget, it stays in the shop table as Quarantined and stops holding row 44. Dispatch parcel is now eligible for delivery. It is not in Acta yet. A quarantined receipt needs an operator decision; lanes do not guarantee every earlier task succeeded.', source: [[job('A3', 'quarantined', 'Quarantined · retained'), pending('A4')], []], lanes: [[done('A1'), done('A2')], [done('B1'), done('B2')], []], workers: idle, transfer: '44 / A4 · dispatch-parcel({ orderId: 1042 }) → next eligible delivery', relay: 'Quarantined rows remain inspectable; successful deliveries are deleted.', phase: 1 }
  ];
  let index = 0;
  let outbox = false;
  const names = ['customer-42', 'customer-73', 'No lane'];
  function buildSteps() {
    get('steps').replaceChildren();
    (outbox ? outboxFrames : frames).forEach((frame, i) => {
      const button = element('button', 'button', `${i + 1}. ${frame.name}`);
      button.type = 'button';
      button.addEventListener('click', () => { index = i; render(); });
      get('steps').append(button);
    });
  }
  function element(tag, className, text) {
    const el = document.createElement(tag);
    el.className = className;
    if (text) el.textContent = text;
    return el;
  }
  function lanes(target, rows, source = false) {
    target.replaceChildren();
    rows.forEach((jobs, i) => {
      const row = element('div', 'claiming-lane');
      const label = element('div', 'claiming-lane-name', outbox && i < 2 ? `order-${i ? 1073 : 1042}` : names[i]);
      label.append(element('small', '', i === 2 ? 'Independent jobs' : source ? 'Staged lane order' : 'Ordered lane'));
      const cards = element('div', 'claiming-jobs');
      if (!jobs.length) cards.append(element('span', 'walk-rule', source ? 'Delivered · no source rows' : 'No jobs admitted yet'));
      jobs.forEach((j, n) => {
        if (n && i !== 2) cards.append(element('span', 'walk-arrow', '→'));
        const card = element('div', `walk-job ${j.state}`);
        card.append(element('b', '', j.id));
        if (outbox && deliveries[j.id]) {
          card.append(element('strong', 'claiming-job-action', deliveries[j.id].name));
          card.append(element('small', 'claiming-job-input', `orderId: ${deliveries[j.id].order}`));
        }
        card.append(element('span', '', j.label));
        cards.append(card);
      });
      row.append(label, cards);
      target.append(row);
    });
  }
  function sourceRows(frame) {
    const wrapper = element('div', 'walk-scroll');
    const table = element('table', 'walk-table');
    const caption = element('caption', 'sr-only', 'Shop acta_outbox rows: simplified columns for this example. All requests target namespace orders.');
    const head = element('thead', '');
    const headers = element('tr', '');
    ['Row / request', 'job_name + input', 'lane', 'Source status'].forEach(text => {
      const th = element('th', '', text);
      th.setAttribute('scope', 'col');
      headers.append(th);
    });
    head.append(headers);
    const body = element('tbody', '');
    frame.source.flat().forEach(j => {
      const info = deliveries[j.id];
      const row = element('tr', j.state);
      const request = element('th', '', `${info.row} / ${j.id}`);
      request.setAttribute('scope', 'row');
      const payload = element('td', '');
      payload.append(element('strong', '', info.handler), element('code', '', `{ orderId: ${info.order} }`));
      row.append(request, payload, element('td', '', `order-${info.order}`), element('td', 'claiming-source-status', j.label));
      body.append(row);
    });
    table.append(caption, head, body);
    wrapper.append(table);
    get('outbox').replaceChildren(wrapper);
    if (frame.removed) get('outbox').append(element('div', 'claiming-deleted', `✓ Deleted after target commit: ${frame.removed.map(id => `row ${deliveries[id].row} / ${id}`).join(', ')}. Their jobs remain below.`));
  }
  function render() {
    const story = outbox ? outboxFrames : frames;
    const frame = story[index];
    lanes(get('lanes'), frame.lanes);
    get('source').hidden = !outbox;
    if (outbox) {
      sourceRows(frame);
      get('relay-note').textContent = frame.relay;
      get('transfer').textContent = frame.transfer;
      root.querySelectorAll('.claiming-journey > span').forEach((el, i) => el.className = i === frame.phase ? 'active' : '');
    }
    get('ledger-label').textContent = outbox ? 'Acta database · jobs ledger · namespace orders' : 'Acta SQL ledger · namespace orders';
    get('workers').replaceChildren();
    frame.workers.forEach((status, i) => {
      const worker = element('div', `walk-worker ${status.startsWith('Offline') ? 'offline' : status === 'Available' ? '' : 'busy'}`);
      worker.append(element('strong', '', `Worker ${i + 1}`), element('span', '', status));
      get('workers').append(worker);
    });
    get('step').textContent = `${index + 1} / ${story.length} · ${frame.name}`;
    get('heading').textContent = frame.title;
    get('description').textContent = frame.text;
    get('back').disabled = index === 0;
    get('next').disabled = index === story.length - 1;
    get('mode').setAttribute('aria-pressed', String(outbox));
    get('mode').textContent = outbox ? 'Back to lane claiming' : 'Show outbox example';
    Array.from(get('steps').children).forEach((button, i) => {
      button.setAttribute('aria-current', i === index ? 'step' : 'false');
    });
  }
  get('next').addEventListener('click', () => { index = Math.min(index + 1, (outbox ? outboxFrames : frames).length - 1); render(); });
  get('back').addEventListener('click', () => { index = Math.max(index - 1, 0); render(); });
  get('replay').addEventListener('click', () => { index = 0; render(); });
  get('mode').addEventListener('click', () => { outbox = !outbox; index = 0; buildSteps(); render(); });
  root.querySelectorAll('.claiming-controls').forEach(el => { el.hidden = false; });
  function openHelp() {
    if (location.hash === '#claiming-lane-rules' || location.hash === '#claiming-outbox-ordering') {
      document.getElementById(location.hash.slice(1)).open = true;
    }
  }
  root.querySelectorAll('.walk-footnote a').forEach(link => link.addEventListener('click', () => {
    document.getElementById(link.getAttribute('href').slice(1)).open = true;
  }));
  addEventListener('hashchange', openHelp);
  openHelp();
  buildSteps();
  render();
})();
