// The Learn section's page script: copy buttons, the person/agent switch, the question on the hub,
// the check questions, and which how-tos this reader has marked done (kept in this browser only).
(() => {
  const store = {
    get(key, fallback) { try { const value = localStorage.getItem(key); return value === null ? fallback : JSON.parse(value); } catch { return fallback; } },
    set(key, value) { try { localStorage.setItem(key, JSON.stringify(value)); } catch { /* a private window keeps nothing */ } },
  };

  // A copy button copies the listing it stands on.
  const copyStatus = document.getElementById('copy-status');
  document.addEventListener('click', async event => {
    const button = event.target.closest('button.copy');
    if (!button) return;
    try {
      await navigator.clipboard.writeText(button.closest('.listing').querySelector('code').textContent);
      button.textContent = 'Copied'; button.dataset.copied = '';
      copyStatus.textContent = 'Copied to the clipboard.';
    } catch { button.textContent = 'Select and copy'; }
    setTimeout(() => { button.textContent = 'Copy'; delete button.dataset.copied; copyStatus.textContent = ''; }, 1600);
  });

  // The same page as a person reads it, or as the Markdown an agent receives.
  const readers = [...document.querySelectorAll('[data-reader]')];
  const personFace = document.querySelector('.person-face'), agentFace = document.querySelector('.agent-face');
  let markdownLoaded = false;
  async function readAs(reader) {
    readers.forEach(button => button.setAttribute('aria-pressed', String(button.dataset.reader === reader)));
    personFace.hidden = reader === 'agent';
    agentFace.hidden = reader !== 'agent';
    store.set('acta-learn-reader', reader);
    if (reader !== 'agent' || markdownLoaded) return;
    markdownLoaded = true;
    const url = readers.find(button => button.dataset.md).dataset.md, body = document.getElementById('md-body');
    try {
      const response = await fetch(url);
      if (!response.ok) throw new Error(String(response.status));
      const text = await response.text();
      body.textContent = text;
      const tokens = Math.max(100, Math.round(text.length / 400) * 100);
      document.querySelector('[data-md-size]').textContent = `${(new Blob([text]).size / 1024).toFixed(1)} KB, about ${tokens.toLocaleString('en')} tokens`;
    } catch { body.textContent = `Could not load ${url}.`; }
  }
  readers.forEach(button => button.addEventListener('click', () => readAs(button.dataset.reader)));
  if (store.get('acta-learn-reader', 'person') === 'agent') readAs('agent');

  // Done marks: a filled step in every list that names the how-to.
  const done = new Set(store.get('acta-learn-done', []));
  const doneToggle = document.querySelector('[data-done-toggle]');
  function paintDone() {
    document.querySelectorAll('a[data-id]').forEach(link => link.toggleAttribute('data-done', done.has(link.dataset.id)));
    document.querySelectorAll('[data-section]').forEach(section => {
      const count = section.querySelectorAll('.entry[data-done]').length;
      section.querySelector('[data-progress]').textContent = count ? `${count} of ${section.querySelectorAll('.entry').length} done` : '';
    });
    if (doneToggle) {
      const on = done.has(doneToggle.dataset.doneToggle);
      doneToggle.setAttribute('aria-pressed', String(on));
      doneToggle.textContent = on ? 'Done. Undo' : 'Mark as done';
    }
  }
  if (doneToggle) doneToggle.addEventListener('click', () => {
    const id = doneToggle.dataset.doneToggle;
    if (!done.delete(id)) done.add(id);
    store.set('acta-learn-done', [...done]);
    paintDone();
  });
  paintDone();

  // Check questions: pick an answer, see which one holds and why.
  document.querySelectorAll('.check').forEach(check => {
    const answer = Number(check.dataset.answer), options = [...check.querySelectorAll('.check-options button')];
    options.forEach((button, n) => button.addEventListener('click', () => {
      options.forEach((other, k) => { if (k === answer) other.dataset.result = 'right'; else delete other.dataset.result; });
      if (n !== answer) button.dataset.result = 'wrong';
      check.querySelector('details').open = true;
    }));
  });

  // The question on the hub: words in, the how-tos that answer them out.
  const ask = document.getElementById('ask'), indexSource = document.getElementById('how-to-index');
  if (!ask || !indexSource) return;
  // /learn used to be the tutorial: a link to a place this page does not have is sent on to where the tutorial lives now.
  if (location.hash.length > 1 && !document.getElementById(location.hash.slice(1))) { location.replace('/learn/first-project' + location.hash); return; }
  const results = document.getElementById('ask-results');
  const filler = new Set('a an the to of in on at for with and or how do does i my is it its be can until that this when from by as into acta job jobs some only like process'.split(' '));
  const stem = word => word.replace(/ies$/, 'y').replace(/(ing|ed|es|s)$/, '') || word;
  const words = text => text.toLowerCase().split(/[^a-z0-9:]+/).filter(Boolean).map(stem);
  const index = JSON.parse(indexSource.textContent).map((entry, order) => ({ entry, order, fields: [[words(entry.t), 6], [words(entry.k), 4], [words(entry.d), 2], [words(entry.s), 2], [words(entry.x), 1]] }));
  const matches = (query, word) => word.startsWith(query) || (word.length >= 4 && query.startsWith(word));
  const escapeHtml = text => text.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  let active = -1;

  function search() {
    const all = [...new Set(words(ask.value))], meaningful = all.filter(word => !filler.has(word)), query = meaningful.length ? meaningful : all;
    active = -1;
    if (!query.length) { results.hidden = true; results.innerHTML = ''; return; }
    // A word that few how-tos carry says more than one that many do.
    const rarity = query.map(word => Math.log(1 + index.length / (1 + index.filter(item => item.fields.some(([fieldWords]) => fieldWords.some(fieldWord => matches(word, fieldWord)))).length)));
    const scored = index.map(item => {
      let hit = 0, score = 0;
      query.forEach((word, n) => {
        const weight = item.fields.filter(([fieldWords]) => fieldWords.some(fieldWord => matches(word, fieldWord))).reduce((sum, [, w]) => sum + w, 0);
        if (weight) { hit++; score += weight * rarity[n]; }
      });
      return { ...item, hit, score };
    });
    // Most of the words should fit; when no how-to manages that, the closest ones are still worth showing.
    const need = Math.ceil(query.length / 2);
    const fits = scored.filter(item => item.hit >= need);
    const found = (fits.length ? fits : scored.filter(item => item.hit)).sort((a, b) => b.hit - a.hit || b.score - a.score || a.order - b.order).slice(0, 8);
    results.hidden = false;
    results.innerHTML = found.length
      ? found.map(({ entry }) => `<a href="${entry.u}"><span class="r-title">How to ${escapeHtml(entry.t[0].toLowerCase() + entry.t.slice(1))}</span><span class="r-sec">${escapeHtml(entry.s)}${entry.m ? ' · ' + entry.m : ''}</span><span class="r-note">${escapeHtml(entry.d)}</span></a>`).join('')
      : '<p class="ask-none">No how-to answers that yet. <a href="https://github.com/acta-dotnet/acta/issues/new">Tell us what you were trying to do</a>, and it becomes the next one.</p>';
  }
  function move(step) {
    const links = [...results.querySelectorAll('a')];
    if (!links.length) return;
    active = (active + step + links.length) % links.length;
    links.forEach((link, n) => link.classList.toggle('active', n === active));
    links[active].scrollIntoView({ block: 'nearest' });
  }
  ask.addEventListener('input', search);
  ask.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown') { event.preventDefault(); move(1); }
    else if (event.key === 'ArrowUp') { event.preventDefault(); move(-1); }
    else if (event.key === 'Enter') { const link = results.querySelector('a.active') || results.querySelector('a'); if (link) location.href = link.href; }
    else if (event.key === 'Escape') { ask.value = ''; search(); }
  });
  document.querySelectorAll('[data-ask]').forEach(button => button.addEventListener('click', () => { ask.value = button.dataset.ask; search(); ask.focus(); }));
  const asked = new URLSearchParams(location.search).get('q');
  if (asked) { ask.value = asked; search(); }
})();

// The names a job is stored under: a card on hover, focus or tap says what a rename does.
(() => {
  const names = document.querySelectorAll('.ct');
  if (!names.length) return;
  const text = {
    job: ['This name is a contract.', 'It is stored with every job. Treat it as permanent once jobs exist under it.'],
    namespace: ['This namespace is a contract.', 'Every job is stored under it, and there is no rename.'],
    step: ['This name is a contract.', "It is the key to the step's stored result. Rename it and the step runs again."],
    signal: ['This name is a contract.', 'A renamed signal is a different signal. Let the waiting jobs finish first.'],
    timer: ['This name is a contract.', 'A renamed timer is a different timer. Let the sleeping jobs finish first.'],
    variable: ['This name is a contract.', 'It is the key to a stored value. A renamed variable reads as absent.'],
    child: ['This name is a contract.', 'It is how a handler that runs again finds this child. A renamed child is new work.'],
  };
  const tip = document.createElement('div');
  const star = '<svg viewBox="0 0 16 16" aria-hidden="true"><path d="M8 1.6l1.9 4.3 4.6.4-3.5 3.1 1.1 4.6L8 11.5 3.9 14l1.1-4.6L1.5 6.3l4.6-.4z" fill="currentColor"/></svg>';
  tip.className = 'ct-tip'; tip.id = 'ct-tip'; tip.setAttribute('role', 'tooltip'); tip.hidden = true;
  document.body.appendChild(tip);
  let open = null;
  const hide = () => {
    if (!open) return;
    open.classList.remove('is-open'); open.removeAttribute('aria-describedby'); open = null;
    tip.classList.remove('is-shown'); tip.hidden = true;
  };
  const show = el => {
    const t = text[el.dataset.ct]; if (!t) return;
    if (open && open !== el) hide();
    tip.innerHTML = star; const lead = document.createElement('b'); lead.textContent = t[0]; tip.append(lead, t[1]);
    tip.hidden = false; open = el; el.classList.add('is-open'); el.setAttribute('aria-describedby', 'ct-tip');
    const r = el.getBoundingClientRect(), w = tip.offsetWidth, h = tip.offsetHeight, vw = document.documentElement.clientWidth;
    const left = Math.max(16, Math.min(r.left + r.width / 2 - w / 2, vw - w - 16));
    const top = r.top - h - 10 < 72 ? r.bottom + 10 : r.top - h - 10;
    tip.style.left = left + scrollX + 'px'; tip.style.top = top + scrollY + 'px';
    void tip.offsetWidth; tip.classList.add('is-shown');
  };
  names.forEach(el => {
    el.addEventListener('mouseenter', () => show(el));
    el.addEventListener('mouseleave', () => { if (document.activeElement !== el) hide(); });
    el.addEventListener('focus', () => show(el));
    el.addEventListener('blur', hide);
    el.addEventListener('click', event => { event.stopPropagation(); el.focus(); show(el); });
  });
  document.addEventListener('click', () => { if (document.activeElement && document.activeElement.classList.contains('ct')) document.activeElement.blur(); hide(); });
  addEventListener('keydown', event => { if (event.key === 'Escape') hide(); });
  document.querySelectorAll('.listing').forEach(box => box.addEventListener('scroll', hide, { passive: true, capture: true }));
})();
