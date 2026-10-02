(() => {
  const root = document.getElementById('admission-figure');
  if (!root) return;
  const get = name => document.getElementById(`admission-${name}`);
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  // Each frame is a persisted-state illustration of the same five jobs, not a timing simulation.
  // A1, A2, A3 belong to customer 42; B1, B2 to customer 73; they arrived in the order A1, B1, A2, A3, B2.
  const all = ['A1', 'B1', 'A2', 'A3', 'B2'];
  const s = (state, label) => ({ state, label });
  const ready = s('ready', 'Ready');
  const blocked = s('blocked', 'Blocked');
  const done = s('done', 'Succeeded');
  const running = (n, note) => s('running', `Executor ${n} · ${note || 'running'}`);
  const rearmed = s('rearmed', 'Re-armed · Ready in 2 s');
  const retry = s('retry', 'Failed · retry not due');
  const booked = at => s('booked', `Booked · ${at} s`);
  const idle = ['Available', 'Available', 'Available'];
  const jobs = (...pairs) => Object.fromEntries(all.map((id, i) => [id, pairs[i]]));
  const mechanisms = {
    lane: {
      tab: 'Lane',
      axis: ['<b>Order.</b> One job of the lane at a time, in enqueue order.', 'o => o.Lane("customer-42")'],
      ledger: 'Acta SQL ledger · two lanes',
      rule: 'Only lane heads enter the claim scan. Blocked jobs stay outside it, however deep the lane.',
      rows: [['customer-42', 'Ordered lane', ['A1', 'A2', 'A3']], ['customer-73', 'Ordered lane', ['B1', 'B2']]],
      frames: [
        { name: 'Enqueue', title: 'Only the head of each lane is Ready.', text: 'A1 and B1 are claimable. A2, A3 and B2 are Blocked, outside the claim scan, so a deep lane costs the claim nothing. Enqueue order is commit order: two producers cannot interleave a lane.', jobs: jobs(ready, ready, blocked, blocked, blocked), executors: idle },
        { name: 'Claim', title: 'Two heads run. The third executor has nothing eligible.', text: 'Executor 1 takes A1 and executor 2 takes B1. The followers stay Blocked, so no executor is spent claiming a job that cannot run yet.', jobs: jobs(running(1), running(2), blocked, blocked, blocked), executors: ['Running A1', 'Running B1', 'Available · nothing eligible'] },
        { name: 'Finish and fail', title: 'A1 succeeds and promotes A2. B1 fails and holds its lane.', text: 'A2 becomes Ready in the same transaction that finished A1. B1 failed with attempts left, so it keeps its place at the head: B2 cannot overtake it. Head-of-line blocking is the price of order, so a poison head needs a policy: a retry budget, after which the lane moves on.', jobs: jobs(done, retry, ready, blocked, blocked), executors: idle },
        { name: 'Retry', title: 'Any executor takes A2. B1 retries from the head.', text: 'A lane belongs to the ledger, not to a worker, so executor 3 can run A2. When B1’s retry is due it runs again, still ahead of B2.', jobs: jobs(done, running(1, 'attempt 2'), running(3), blocked, blocked), executors: ['Running B1 · attempt 2', 'Available', 'Running A2'] },
        { name: 'Drained', title: 'Both lanes drained in enqueue order.', text: 'A1, A2, A3 and B1, B2 finished in the order they were committed, on whichever executor was free. Many lanes run in parallel, and jobs without a lane were never affected.', jobs: jobs(done, done, done, done, done), executors: idle }
      ]
    },
    key: {
      tab: 'ConcurrencyKey',
      axis: ['<b>How many at once: one.</b> Mutual exclusion per key, no order.', 'o => o.ConcurrencyKey("customer-42")'],
      ledger: 'Acta SQL ledger · two keys',
      rule: 'Every job is in the claim scan. The key is tested after the claim and before the handler.',
      rows: [['customer-42', 'Concurrency key', ['A1', 'A2', 'A3']], ['customer-73', 'Concurrency key', ['B1', 'B2']]],
      frames: [
        { name: 'Enqueue', title: 'Every job is Ready. The key is checked after the claim.', text: 'Nothing is Blocked. All five jobs are claimable, and the key is tested once an executor has claimed a job and before its handler runs.', jobs: jobs(ready, ready, ready, ready, ready), executors: idle, held: [null, null] },
        { name: 'Claim', title: 'A1 and B1 take their keys. A2 finds customer-42 held and bounces.', text: 'Executor 3 claimed A2, found the key held by A1, and handed the executor back. A2 is re-armed Ready a couple of seconds out, budget-neutral: no retry spent, no executor waiting on a lock.', jobs: jobs(running(1), running(2), rearmed, ready, ready), executors: ['Running A1 · holds customer-42', 'Running B1 · holds customer-73', 'Claimed A2 · key held · handed back'], held: ['A1', 'B1'] },
        { name: 'Overtake', title: 'A1 finishes. A3 takes the key before A2 is due back.', text: 'Mutual exclusion, not ordering: a younger job can take a freed key while an older one waits out its re-arm. B1 fails, and its key is released with it, so B2 runs now. A failing job does not hold the others.', jobs: jobs(done, s('retry', 'Failed · retry not due · key released'), rearmed, running(1), running(2)), executors: ['Running A3 · holds customer-42', 'Running B2 · holds customer-73', 'Available'], held: ['A3', 'B2'] },
        { name: 'Re-arm', title: 'A2 comes back at its instant and takes the key.', text: 'A2 is claimed again when its re-arm is due; the key is free, so it runs. B1 retries on a free executor. Never two at once per key; who goes first was never promised.', jobs: jobs(done, running(1, 'attempt 2'), running(3), done, done), executors: ['Running B1 · attempt 2 · holds customer-73', 'Available', 'Running A2 · holds customer-42'], held: ['A2', 'B1'] },
        { name: 'Drained', title: 'One at a time per key, in whatever order the key allowed.', text: 'They finished as A1, A3, B2, A2, B1. Use a concurrency key for work that must never overlap itself and does not care who goes first.', jobs: jobs(done, done, done, done, done), executors: idle, held: [null, null] }
      ]
    },
    limit: {
      tab: 'ConcurrencyLimit',
      axis: ['<b>How many at once: N.</b> The same gate with N slots, per key or per definition.', '[Job("rebuild-index", ConcurrencyLimit = 2)]'],
      ledger: 'Acta SQL ledger · one definition, two slots',
      rule: 'No enqueue supplied a key, so the definition name is the key and both slots are shared by every job of it, cluster-wide.',
      rows: [['rebuild-index', 'ConcurrencyLimit = 2', all]],
      frames: [
        { name: 'Enqueue', title: 'Five jobs, two slots.', text: 'ConcurrencyLimit = 2 on the definition. Every job is Ready and claimable; the slot is taken after the claim, like a key, because a limit is a key with a size.', jobs: jobs(ready, ready, ready, ready, ready), executors: idle, slots: [null, null] },
        { name: 'Claim', title: 'A1 and B1 fill the slots. A2 bounces.', text: 'The third claim finds no free slot and re-arms A2 a couple of seconds out, budget-neutral, exactly as a key with one slot would. The executor is handed back.', jobs: jobs(running(1, 'slot 1'), running(2, 'slot 2'), rearmed, ready, ready), executors: ['Running A1 · slot 1', 'Running B1 · slot 2', 'Claimed A2 · no free slot · handed back'], slots: ['A1', 'B1'] },
        { name: 'Rotate', title: 'A slot frees and the next claim takes it.', text: 'A1 finishes and A3 is claimed into the freed slot while A2 is still waiting out its re-arm. The limit says how many, never which.', jobs: jobs(done, running(2, 'slot 2'), rearmed, running(1, 'slot 1'), ready), executors: ['Running A3 · slot 1', 'Running B1 · slot 2', 'Available'], slots: ['A3', 'B1'] },
        { name: 'Drain', title: 'Two at a time until the backlog is gone.', text: 'A2 and B2 take the slots as they free. Put a ConcurrencyKey on the enqueue and the same two slots become per key: two per customer at once.', jobs: jobs(done, done, running(1, 'slot 1'), done, running(3, 'slot 2')), executors: ['Running A2 · slot 1', 'Available', 'Running B2 · slot 2'], slots: ['A2', 'B2'] },
        { name: 'Drained', title: 'Never more than two in flight.', text: 'Order was not promised and not kept; the count was. The limit is one attribute on the definition, and an operator can change it on a running system.', jobs: jobs(done, done, done, done, done), executors: idle, slots: [null, null] }
      ]
    },
    rate: {
      tab: 'RateLimit',
      axis: ['<b>How often.</b> Starts are metered on one clock across the namespace’s workers.', '[Job("send-invoice", RateLimit = "2/s")]'],
      ledger: 'Acta SQL ledger · one meter, 2 starts a second',
      rule: 'Each early arrival is given the next free instant and re-arms at exactly that instant: one re-arm per job, no re-race. Running count is not the meter’s business.',
      ticks: [0, 0.5, 1, 1.5],
      frames: [
        { name: 'Arrive', title: 'Five jobs arrive at once. The meter books a turn for each.', text: 'An idle meter admits one second’s worth of the rate at once, so A1 and B1 start now. A2, A3 and B2 arrived before their turn, so each is given the next free instant on the shared clock and re-armed Ready for exactly that instant.', jobs: jobs(running(1), running(2), booked('0.5'), booked('1.0'), booked('1.5')), executors: ['Running A1', 'Running B1', 'Available'], now: 0, at: { A1: 0, B1: 0, A2: 0.5, A3: 1, B2: 1.5 } },
        { name: '0.5 s', title: 'A2’s instant arrives and it starts, while A1 and B1 still run.', text: 'Three jobs in flight: the meter paces starts, not how many are running. Add a ConcurrencyLimit when both matter; rate and limit are independent gates and a job passes both.', jobs: jobs(running(1), running(2), running(3), booked('1.0'), booked('1.5')), executors: ['Running A1', 'Running B1', 'Running A2'], now: 0.5, at: { A1: 0, B1: 0, A2: 0.5, A3: 1, B2: 1.5 } },
        { name: '1.0 s', title: 'A3 starts on its booked turn. A1 has finished.', text: 'The backlog drains in arrival order at the rate, each job returning at its own instant. A thousand waiting jobs would cost a thousand re-arms, not a thousand executors re-racing a counter.', jobs: jobs(done, running(2), running(3), running(1), booked('1.5')), executors: ['Running A3', 'Running B1', 'Running A2'], now: 1, at: { A1: 0, B1: 0, A2: 0.5, A3: 1, B2: 1.5 } },
        { name: '1.5 s', title: 'B2 takes the last booking.', text: 'Never more than the rate plus its burst: two at once, then one every half second. The published envelope is the rate plus its burst over any window, and the release certification checks three meters (10/s, 50/s, 100/s) never admitted past it.', jobs: jobs(done, done, done, running(1), running(2)), executors: ['Running A3', 'Running B2', 'Available'], now: 1.5, at: { A1: 0, B1: 0, A2: 0.5, A3: 1, B2: 1.5 } },
        { name: 'Drained', title: 'Five starts, paced over two seconds, in arrival order.', text: 'Definitions that should share one budget name the same RateKey: one meter for every job that calls the same partner API.', jobs: jobs(done, done, done, done, done), executors: idle, now: 2, at: { A1: 0, B1: 0, A2: 0.5, A3: 1, B2: 1.5 } }
      ]
    }
  };
  const order = ['lane', 'key', 'limit', 'rate'];
  let current = 'lane';
  let index = 0;
  let timer = null;
  function element(tag, className, text) {
    const el = document.createElement(tag);
    if (className) el.className = className;
    if (text) el.textContent = text;
    return el;
  }
  function card(id, state) {
    const el = element('div', `walk-job ${state.state}`);
    el.append(element('b', '', id), element('span', '', state.label));
    return el;
  }
  function buildTabs() {
    order.forEach(key => {
      const button = get(`tab-${key}`);
      button.addEventListener('click', () => select(key));
      button.addEventListener('keydown', e => {
        const i = order.indexOf(key);
        const next = e.key === 'ArrowRight' ? order[(i + 1) % order.length] : e.key === 'ArrowLeft' ? order[(i + order.length - 1) % order.length] : null;
        if (!next) return;
        e.preventDefault();
        select(next);
        get(`tab-${next}`).focus();
      });
    });
  }
  function buildSteps() {
    get('steps').replaceChildren();
    mechanisms[current].frames.forEach((frame, i) => {
      const button = element('button', 'button', `${i + 1}. ${frame.name}`);
      button.type = 'button';
      button.addEventListener('click', () => { stop(); index = i; render(); });
      get('steps').append(button);
    });
  }
  function gate(m, frame) {
    const target = get('gate');
    target.replaceChildren();
    if (m.ticks) {
      const ruler = element('div', 'admission-ruler');
      m.ticks.forEach(t => {
        const col = element('div', `admission-tick${t < frame.now ? ' past' : t === frame.now ? ' now' : ''}`);
        col.append(element('i', '', `${t.toFixed(1)} s${t === frame.now ? ' · now' : ''}`));
        all.filter(id => frame.at[id] === t).forEach(id => col.append(card(id, frame.jobs[id])));
        ruler.append(col);
      });
      const row = element('div', 'admission-row');
      const label = element('div', 'admission-row-name', 'send-invoice');
      label.append(element('small', '', 'RateLimit = "2/s"'));
      row.append(label, ruler);
      target.append(row);
      return;
    }
    m.rows.forEach(([name, kind, ids], r) => {
      const row = element('div', 'admission-row');
      const label = element('div', 'admission-row-name', name);
      label.append(element('small', '', kind));
      const cards = element('div', 'admission-jobs');
      const held = frame.slots || (frame.held ? [frame.held[r]] : null);
      if (held) {
        const slots = element('div', 'admission-slots');
        held.forEach((id, i) => slots.append(element('span', `admission-slot${id ? ' held' : ''}`, `${held.length > 1 ? `slot ${i + 1}` : 'key'} · ${id ? `held by ${id}` : 'free'}`)));
        cards.append(slots);
      }
      ids.forEach((id, n) => {
        if (n && current === 'lane') cards.append(element('span', 'walk-arrow', '→'));
        cards.append(card(id, frame.jobs[id]));
      });
      row.append(label, cards);
      target.append(row);
    });
  }
  function render() {
    const m = mechanisms[current];
    const frame = m.frames[index];
    get('axis').innerHTML = `<span>${m.axis[0]}</span><code>${m.axis[1].replace(/</g, '&lt;')}</code>`;
    get('ledger-label').textContent = m.ledger;
    get('rule').textContent = m.rule;
    gate(m, frame);
    get('executors').replaceChildren();
    frame.executors.forEach((status, i) => {
      const el = element('div', `walk-worker ${status.startsWith('Claimed') ? 'bounced' : status.startsWith('Available') ? '' : 'busy'}`);
      el.append(element('strong', '', `Executor ${i + 1}`), element('span', '', status));
      get('executors').append(el);
    });
    get('step').textContent = `${m.tab} · ${index + 1} / ${m.frames.length} · ${frame.name}`;
    get('heading').textContent = frame.title;
    get('description').textContent = frame.text;
    get('back').disabled = index === 0;
    get('next').disabled = index === m.frames.length - 1;
    get('play').textContent = timer ? 'Pause' : index === m.frames.length - 1 ? 'Replay' : 'Play';
    get('play').setAttribute('aria-pressed', String(Boolean(timer)));
    Array.from(get('steps').children).forEach((button, i) => button.setAttribute('aria-current', i === index ? 'step' : 'false'));
    order.forEach(key => {
      const tab = get(`tab-${key}`);
      tab.setAttribute('aria-selected', String(key === current));
      tab.tabIndex = key === current ? 0 : -1;
    });
    get('panel').setAttribute('aria-labelledby', `admission-tab-${current}`);
  }
  function select(key) {
    stop();
    current = key;
    index = 0;
    buildSteps();
    render();
  }
  function stop() {
    if (timer) clearInterval(timer);
    timer = null;
  }
  function play() {
    stop();
    if (index === mechanisms[current].frames.length - 1) index = 0;
    timer = setInterval(() => {
      if (index >= mechanisms[current].frames.length - 1) { stop(); render(); return; }
      index += 1;
      render();
    }, 2800);
    render();
  }
  get('next').addEventListener('click', () => { stop(); index = Math.min(index + 1, mechanisms[current].frames.length - 1); render(); });
  get('back').addEventListener('click', () => { stop(); index = Math.max(index - 1, 0); render(); });
  get('play').addEventListener('click', () => { if (timer) { stop(); render(); } else play(); });
  root.querySelectorAll('.walk-navigation').forEach(el => { el.hidden = false; });
  buildTabs();
  buildSteps();
  render();
  // One orchestrated run when the figure first scrolls into view, unless motion is reduced; after that
  // the reader drives it. Every frame also stands alone as a static picture.
  if (!reduced && 'IntersectionObserver' in window) {
    const observer = new IntersectionObserver(entries => {
      if (!entries.some(e => e.isIntersecting)) return;
      observer.disconnect();
      if (!timer && index === 0) play();
    }, { threshold: 0.6 });
    observer.observe(root);
  }
})();
