(() => {
  const root = document.getElementById('fleet-figure');
  if (!root) return;
  const get = name => document.getElementById(`fleet-${name}`);
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  // Each frame is a persisted-state illustration of one namespace: the workers table, the job rows a claim
  // stamps, and the shared rows every worker reserves against. Times are seconds since frame 1.
  const alive = (status, chips = []) => ({ state: chips.length ? 'busy' : '', status, chips });
  const dead = status => ({ state: 'dead', status, chips: [] });
  const empty = { state: 'empty', status: 'Not started', chips: [] };
  const chip = (text, kind) => ({ text, kind });
  const row = (id, status, cls, by, lease, exec, note) => ({ id, status, cls, by, lease, exec, note });
  const ready = (id, exec = 0, note = '') => row(id, 'Ready', 'ready', '', '', exec, note);
  const leased = (id, by, lease, exec = 1, note = '') => row(id, 'Executing', 'leased', by, lease, exec, note);
  const done = (id, exec = 1) => row(id, 'Succeeded', 'done', '', '', exec, '');
  const idleShared = { recovery: ['sys.recovery', 'Due in 40 s', ''], key: ['rebuild:acme', 'Free', ''], meter: ['send-invoice · 10/s', 'Idle', ''] };
  const frames = [
    { name: 'Peers', title: 'Three peer processes, one database, no leader.', text: 'Each worker registers in the namespace, heartbeats, claims due rows, runs handlers, and refreshes its leases. Nothing else coordinates them: no control plane, no broker, no election. The workers table is the only membership list, and every worker is in it as an equal.',
      workers: [alive('Registered · beat 1 s ago'), alive('Registered · beat 1 s ago'), alive('Registered · beat 1 s ago'), empty],
      jobs: [ready('J1'), ready('J2'), ready('J3'), ready('J4', 0, 'key rebuild:acme'), ready('J5', 0, 'key rebuild:acme'), ready('J6', 0, 'handler in build 1 only')], shared: idleShared },
    { name: 'Claim', title: 'Each claim takes what is due and skips what another claim holds.', text: 'W1 reads its namespace’s due rows by priority, next-run time, then id, and stamps the rows it takes with its name, a lease, and execution number 1. W2 and W3 claim at the same moment; a row one claim holds is skipped, never waited on, so workers never queue behind each other and each attempt belongs to exactly one worker.',
      workers: [alive('Claimed 2 · beat 1 s ago', [chip('J1'), chip('J2')]), alive('Claimed 1 · beat 1 s ago', [chip('J3')]), alive('Claimed 1 · beat 1 s ago', [chip('J4')]), empty],
      jobs: [leased('J1', 'W1', '180 s'), leased('J2', 'W1', '180 s'), leased('J3', 'W2', '180 s'), leased('J4', 'W3', '180 s', 1, 'holds key rebuild:acme'), ready('J5', 0, 'key rebuild:acme'), ready('J6', 0, 'handler in build 1 only')],
      shared: { recovery: ['sys.recovery', 'Due in 39 s', ''], key: ['rebuild:acme', 'Held by W3 · J4', 'held'], meter: ['send-invoice · 10/s', 'Idle', ''] } },
    { name: 'Heartbeat', title: 'Every 45 seconds, each worker renews every lease it holds.', text: 'J2 is a long handler. Its lease keeps moving forward for as long as W1 beats, so no lease has to be sized for the slowest job. Heartbeat 45 s, lease 180 s, dead after 315 s of silence: the lease and dead-worker windows derive from the one heartbeat setting, so every replica agrees on all three by construction.',
      workers: [alive('Beat now · 2 leases renewed', [chip('J2')]), alive('Beat now · 1 lease renewed', [chip('J3')]), alive('Beat now · 1 lease renewed', [chip('J4')]), empty],
      jobs: [done('J1'), leased('J2', 'W1', '180 s again'), leased('J3', 'W2', '180 s again'), leased('J4', 'W3', '180 s again', 1, 'holds key rebuild:acme'), ready('J5', 0, 'key rebuild:acme'), ready('J6', 0, 'handler in build 1 only')],
      shared: { recovery: ['sys.recovery', 'Due in 15 s', ''], key: ['rebuild:acme', 'Held by W3 · J4', 'held'], meter: ['send-invoice · 10/s', 'Idle', ''] } },
    { name: 'A worker dies', title: 'W2 is killed mid-handler. Its lease starts to lapse.', text: 'No heartbeat arrives from W2. The other workers keep claiming and finishing; nothing waits on W2 and nothing has to notice yet. J3 stays leased to a dead process until its lease expires at 180 s, then becomes recoverable; W2 itself is marked dead after 315 s of silence.',
      workers: [alive('Beat 20 s ago', [chip('J2')]), dead('Killed · no heartbeat for 190 s'), alive('Beat 20 s ago', [chip('J4')]), empty],
      jobs: [done('J1'), leased('J2', 'W1', '160 s'), row('J3', 'Executing', 'lapsed', 'W2', 'expired 10 s ago', 1, 'lease lapsed'), leased('J4', 'W3', '160 s', 1, 'holds key rebuild:acme'), ready('J5', 0, 'key rebuild:acme'), ready('J6', 0, 'handler in build 1 only')],
      shared: { recovery: ['sys.recovery', 'Due now', 'firing'], key: ['rebuild:acme', 'Held by W3 · J4', 'held'], meter: ['send-invoice · 10/s', 'Idle', ''] } },
    { name: 'Recovery', title: 'Whichever worker claims the recovery slot runs the recovery pass.', text: 'sys.recovery is an ordinary recurring job. W3 claims it this time, finds J3’s lapsed lease, returns the row to Ready, and records the orphaned attempt. W2 itself is marked dead by a later pass, once it has been silent for 315 s. The claim is the election; there is nothing else to elect. sys.retention, sys.alerts and sys.outbox are slots of the same kind, each firing once per cadence on whichever worker claims it.',
      workers: [alive('Beat 1 s ago', [chip('J2')]), dead('Silent · marked dead at 315 s'), alive('Running the recovery pass', [chip('J4'), chip('sys.recovery', 'sys')]), empty],
      jobs: [done('J1'), leased('J2', 'W1', '180 s'), ready('J3', 1, 'attempt 1 orphaned · recorded'), leased('J4', 'W3', '180 s', 1, 'holds key rebuild:acme'), ready('J5', 0, 'key rebuild:acme'), ready('J6', 0, 'handler in build 1 only')],
      shared: { recovery: ['sys.recovery', 'Run by W3 · 1 lease reclaimed', 'firing'], key: ['rebuild:acme', 'Held by W3 · J4', 'held'], meter: ['send-invoice · 10/s', 'Idle', ''] } },
    { name: 'Takeover', title: 'W1 picks J3 up as execution 2. Recorded steps are not run again.', text: 'The handler re-enters from the top, and every durable step J3 already recorded returns its stored result instead of running. If W2’s old attempt somehow answers late, that write carries execution number 1 against a row now on execution 2, and the version check fences it off: a stale attempt can never overwrite its replacement.',
      workers: [alive('Beat 1 s ago', [chip('J2'), chip('J3 · exec 2')]), dead('Killed · late write fenced off'), alive('Beat 1 s ago', [chip('J4')]), empty],
      jobs: [done('J1'), leased('J2', 'W1', '180 s'), leased('J3', 'W1', '180 s', 2, 'steps 1 to 3 replayed from rows'), leased('J4', 'W3', '180 s', 1, 'holds key rebuild:acme'), ready('J5', 0, 'key rebuild:acme'), ready('J6', 0, 'handler in build 1 only')],
      fenced: 'W2 · J3 · execution 1 · version 4 → rejected: row is on execution 2, version 6',
      shared: { recovery: ['sys.recovery', 'Due in 55 s', ''], key: ['rebuild:acme', 'Held by W3 · J4', 'held'], meter: ['send-invoice · 10/s', 'Idle', ''] } },
    { name: 'Shared gates', title: 'A key or a meter is a row, so the gate holds across the fleet.', text: 'W3 still holds the concurrency key rebuild:acme for J4. W1 claims J5, which carries the same key, finds the row held, and hands its executor back: one at a time means one in the whole fleet, not one per process. A rate meter is the same kind of row, so ten a second is ten a second across the namespace’s workers. A lane head is claimable by any worker, and finishing it promotes the next member wherever that happens to run.',
      workers: [alive('Claimed J5 · key held · handed back', [chip('J3 · exec 2'), chip('J5 bounced', 'bounced')]), dead('Killed'), alive('Beat 1 s ago', [chip('J4')]), empty],
      jobs: [done('J1'), done('J2'), leased('J3', 'W1', '180 s', 2), leased('J4', 'W3', '180 s', 1, 'holds key rebuild:acme'), row('J5', 'Ready', 'rearmed', '', '', 1, 're-armed · key held by W3'), ready('J6', 0, 'handler in build 1 only')],
      shared: { recovery: ['sys.recovery', 'Due in 50 s', ''], key: ['rebuild:acme', 'Held by W3 · J4 · W1 turned away', 'held'], meter: ['send-invoice · 10/s', 'Next free instant booked', 'held'] } },
    { name: 'Add a worker', title: 'A new build joins the fleet and simply starts claiming.', text: 'W4 starts, registers, and claims. It runs the next release, which drops J6’s definition, so it hands J6 back to Ready, budget-neutral, and stops claiming that definition for the rest of its life; W1, still on build 1, picks J6 up. Old and new builds share the namespace through the deploy, and a rollback is the same picture in reverse. Remove any worker and the others carry on.',
      workers: [alive('Beat 1 s ago', [chip('J3 · exec 2'), chip('J6')]), dead('Dead · retention purges the row later'), alive('Beat 1 s ago', [chip('J4'), chip('J5')]), { state: 'joining', status: 'Registered · build 2 · claiming', chips: [chip('J6 handed back', 'bounced')] }],
      jobs: [done('J1'), done('J2'), leased('J3', 'W1', '180 s', 2), leased('J4', 'W3', '180 s', 1), leased('J5', 'W3', '180 s', 2, 'holds key rebuild:acme'), leased('J6', 'W1', '180 s', 1, 'handed back by W4, no failure charged')],
      shared: { recovery: ['sys.recovery', 'Due in 45 s', ''], key: ['rebuild:acme', 'Held by W3 · J5', 'held'], meter: ['send-invoice · 10/s', 'Idle', ''] } }
  ];
  let index = 0;
  let timer = null;
  function element(tag, className, text) {
    const el = document.createElement(tag);
    if (className) el.className = className;
    if (text) el.textContent = text;
    return el;
  }
  function workers(frame) {
    get('workers').replaceChildren();
    frame.workers.forEach((w, i) => {
      const box = element('div', `walk-worker fleet-worker ${w.state}`);
      const name = element('div', 'fleet-worker-name');
      name.append(element('span', 'fleet-beat'), element('span', '', `W${i + 1}`), element('small', '', w.state === 'joining' ? 'build 2' : w.state === 'empty' ? '' : 'build 1'));
      box.append(name, element('span', 'fleet-worker-status', w.status));
      if (w.chips.length) {
        const chips = element('div', 'fleet-chips');
        w.chips.forEach(c => chips.append(element('span', `fleet-chip ${c.kind || ''}`, c.text)));
        box.append(chips);
      }
      get('workers').append(box);
    });
  }
  function jobs(frame) {
    const body = get('rows');
    body.replaceChildren();
    frame.jobs.forEach(j => {
      const tr = element('tr');
      const th = element('th', '', j.id);
      th.setAttribute('scope', 'row');
      const status = element('td', j.cls, j.status);
      const note = element('td', '');
      if (j.note) note.append(element('code', '', j.note));
      tr.append(th, status, element('td', '', j.by || '·'), element('td', j.cls === 'lapsed' ? 'lapsed' : '', j.lease || '·'), element('td', '', String(j.exec)), note);
      body.append(tr);
    });
    if (frame.fenced) {
      const tr = element('tr');
      const th = element('th', '', 'late write');
      th.setAttribute('scope', 'row');
      const cell = element('td', 'fenced', frame.fenced);
      cell.colSpan = 5;
      tr.append(th, cell);
      body.append(tr);
    }
  }
  function shared(frame) {
    ['recovery', 'key', 'meter'].forEach(name => {
      const [title, status, state] = frame.shared[name];
      const box = get(name);
      box.className = state;
      box.querySelector('b').textContent = title;
      box.querySelector('span').textContent = status;
    });
  }
  function render() {
    const frame = frames[index];
    workers(frame);
    jobs(frame);
    shared(frame);
    get('step').textContent = `${index + 1} / ${frames.length} · ${frame.name}`;
    get('heading').textContent = frame.title;
    get('description').textContent = frame.text;
    get('back').disabled = index === 0;
    get('next').disabled = index === frames.length - 1;
    get('play').textContent = timer ? 'Pause' : index === frames.length - 1 ? 'Replay' : 'Play';
    get('play').setAttribute('aria-pressed', String(Boolean(timer)));
    Array.from(get('steps').children).forEach((button, i) => button.setAttribute('aria-current', i === index ? 'step' : 'false'));
  }
  function stop() {
    if (timer) clearInterval(timer);
    timer = null;
  }
  function play() {
    stop();
    if (index === frames.length - 1) index = 0;
    timer = setInterval(() => {
      if (index >= frames.length - 1) { stop(); render(); return; }
      index += 1;
      render();
    }, 3200);
    render();
  }
  frames.forEach((frame, i) => {
    const button = element('button', 'button', `${i + 1}. ${frame.name}`);
    button.type = 'button';
    button.addEventListener('click', () => { stop(); index = i; render(); });
    get('steps').append(button);
  });
  get('next').addEventListener('click', () => { stop(); index = Math.min(index + 1, frames.length - 1); render(); });
  get('back').addEventListener('click', () => { stop(); index = Math.max(index - 1, 0); render(); });
  get('play').addEventListener('click', () => { if (timer) { stop(); render(); } else play(); });
  root.querySelectorAll('.walk-navigation, .walk-steps').forEach(el => { el.hidden = false; });
  render();
  // One orchestrated run on first sight unless motion is reduced; after that the reader steps.
  if (!reduced && 'IntersectionObserver' in window) {
    const observer = new IntersectionObserver(entries => {
      if (!entries.some(e => e.isIntersecting)) return;
      observer.disconnect();
      if (!timer && index === 0) play();
    }, { threshold: 0.5 });
    observer.observe(root);
  }
})();
