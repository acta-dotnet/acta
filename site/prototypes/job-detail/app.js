(() => {
  const { scenarios, variants } = window.ACTA_PROTOTYPES;
  const params = new URLSearchParams(location.search);
  const state = {
    variant: variants[params.get('view')] ? params.get('view') : 'focus',
    scenario: scenarios[params.get('scenario')] ? params.get('scenario') : 'publication',
    theme: ['light', 'dark', 'paper'].includes(params.get('theme')) ? params.get('theme') : 'light',
    tab: 'overview',
    filter: 'all',
    branch: null,
    selected: null,
    expanded: new Set(),
  };
  const mobile = matchMedia('(max-width: 760px)');
  let job;
  let nodes;
  let pendingAction;
  let toastTimer;
  const app = document.getElementById('app');
  const inspector = document.getElementById('inspector');
  const confirmation = document.getElementById('confirmation');

  const paths = {
    chevron: 'm6 3 5 5-5 5',
    back: 'm10 3-5 5 5 5',
    check: 'm3 8 3 3 7-7',
    x: 'm4 4 8 8m0-8-8 8',
    clock: 'M8 4v4l3 2 M14 8a6 6 0 1 1-12 0 6 6 0 0 1 12 0',
    play: 'm5 3 8 5-8 5z',
    pause: 'M5 3v10M11 3v10',
    copy: 'M6 5V2h7v9h-3 M3 5h7v9H3z',
    more: 'M3 8h.1M8 8h.1M13 8h.1',
    tree: 'M8 2v4M3 6h10M3 6v5m10-5v5M1 11h4v3H1zM11 11h4v3h-4z',
    cube: 'm8 1 6 3.5v7L8 15l-6-3.5v-7zM2 4.5 8 8l6-3.5M8 8v7',
    search: 'M11 11l4 4M12 7a5 5 0 1 1-10 0 5 5 0 0 1 10 0',
    calendar: 'M2 4h12v10H2zM2 7h12M5 2v4m6-4v4',
    activity: 'M1 8h3l2-5 4 10 2-5h3',
    alert: 'm8 1 7 13H1zM8 5v4m0 2v.5',
    code: 'm5 4-4 4 4 4m6-8 4 4-4 4M9 2 7 14',
    arrow: 'M2 8h11m-4-4 4 4-4 4',
    refresh: 'M13 6a5.5 5.5 0 1 0 .3 4M13 2v4H9',
  };
  const icon = (name) => `<svg class="icon ${name === 'copy' ? 'icon-copy' : ''}" viewBox="0 0 16 16" aria-hidden="true"><path d="${paths[name] || paths.cube}"></path></svg>`;
  const escape = (value) => String(value ?? '').replace(/[&<>"']/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
  const labels = { failed: 'Failed', succeeded: 'Succeeded', executing: 'Executing', suspended: 'Waiting', paused: 'Paused', ready: 'Ready', idle: 'Idle' };
  const statusIcon = (status) => ({ failed: 'x', succeeded: 'check', executing: 'play', suspended: 'clock', paused: 'pause', ready: 'clock', idle: 'clock' }[status] || 'clock');
  const status = (node) => `<span class="status ${node.status}">${icon(statusIcon(node.status))}${labels[node.status]}</span>`;
  const actionButton = (action, label, className = '', reference = '') => `<button type="button" class="${className}" data-action="${action}" ${reference ? `data-ref="${reference}"` : ''}>${label}</button>`;
  const refButton = (reference) => `<button type="button" class="ref-token" data-action="reference" data-ref="${reference}" title="${reference}" aria-label="Full reference and copy options for ${reference}">job…${reference.slice(-8)} ${icon('copy')}</button>`;
  const exactTime = (value) => new Date(value).toLocaleString('en-GB', { dateStyle: 'full', timeStyle: 'long', timeZone: 'UTC' });
  const time = (label, value) => `<button type="button" class="time-detail" data-action="time" data-at="${value}" title="${escape(exactTime(value))}" aria-label="${escape(label)}. Show exact date and time.">${escape(label)}</button>`;

  function indexNodes(root, parent = null, map = new Map()) {
    map.set(root.ref, { ...root, parent });
    root.children.forEach((child) => indexNodes(child, root.ref, map));
    return map;
  }

  function resetScenario() {
    job = structuredClone(scenarios[state.scenario].job);
    nodes = indexNodes(job);
    state.selected = job.selected || job.children.find((child) => child.status === 'failed')?.ref || job.children[0]?.ref || job.ref;
    state.branch = job.ref;
    state.expanded = new Set([job.ref]);
    state.tab = state.variant === 'workspace' && job.children.length ? 'lineage' : 'overview';
    state.filter = job.children.length > 8 ? 'active' : 'all';
  }

  function persistLocation() {
    const query = new URLSearchParams({ view: state.variant, scenario: state.scenario, theme: state.theme });
    history.replaceState(null, '', `${location.pathname}?${query}`);
  }

  function renderSidebar() {
    const items = [['Overview', 'activity'], ['Jobs', 'cube'], ['Job history', 'clock'], ['Permanent jobs', 'refresh'], ['Alerts', 'alert'], ['Workers', 'cube'], ['Events', 'activity']];
    return `<aside class="sidebar" aria-label="Dashboard context">
      <div><div class="brand"><svg viewBox="3.95 4.95 30.1 30.1" aria-hidden="true"><path d="M6 33 19 7 32 33" fill="none" stroke="currentColor" stroke-width="4.1" stroke-linecap="round" stroke-linejoin="round"/><circle fill="currentColor" cx="19" cy="27" r="3"/></svg>Acta.</div><p class="brand-subtitle">operator dashboard</p></div>
      <div class="namespace mono">${escape(job.namespace)}</div>
      <nav aria-label="Dashboard navigation preview"><div class="nav-section">Operate</div>
        ${items.map(([label, glyph]) => `<span class="nav-item ${label === 'Jobs' ? 'active' : ''}">${icon(glyph)}${label}</span>`).join('')}
        <div class="nav-section">Configure</div>
        ${[['Schedules', 'clock'], ['Definitions', 'code'], ['Calendars', 'calendar']].map(([label, glyph]) => `<span class="nav-item">${icon(glyph)}${label}</span>`).join('')}
      </nav>
      <div class="sidebar-foot">Interactive design prototype<br>Illustrative job data</div>
    </aside>`;
  }

  function renderPrototypeBar() {
    return `<header class="prototype-bar">
      <div class="prototype-label"><strong>Job detail explorations</strong><span>Interactive prototypes · sample data</span></div>
      <nav class="variant-picker" aria-label="Prototype variants">
        ${Object.entries(variants).map(([id, variant]) => `<button type="button" data-action="variant" data-value="${id}" aria-pressed="${state.variant === id}" title="${variant.description}"><span class="variant-letter">${variant.letter}</span> ${variant.title}${variant.recommended ? '<span class="recommendation">Recommended</span>' : ''}</button>`).join('')}
      </nav>
      <div class="prototype-controls"><label>Example<select id="scenario-select" aria-label="Example job">${Object.entries(scenarios).map(([id, scenario]) => `<option value="${id}" ${state.scenario === id ? 'selected' : ''}>${scenario.label}</option>`).join('')}</select></label>
        <label>Theme<select id="theme-select" aria-label="Theme">${['light', 'dark', 'paper'].map((theme) => `<option value="${theme}" ${state.theme === theme ? 'selected' : ''}>${theme[0].toUpperCase() + theme.slice(1)}</option>`).join('')}</select></label>
      </div>
    </header>`;
  }

  function renderHeader() {
    const terminal = ['succeeded', 'failed', 'cancelled'].includes(job.status);
    return `<div class="breadcrumb">${icon('back')}<span>Jobs</span>${job.parent ? `<span>· Child job</span>` : ''}</div>
      <header class="page-heading"><div class="heading-title"><h1>${escape(job.name)}</h1>
        <div class="identity-line">${status(job)}<span>${escape(job.namespace)}</span>${refButton(job.ref)}${job.kind === 'permanent' ? '<span>Permanent job</span>' : ''}</div>
      </div><div class="header-actions">${actionButton('refresh', icon('refresh'), 'icon-button')}
        <details class="overflow-menu"><summary aria-label="More job actions" title="More job actions">${icon('more')}</summary><div class="menu-items">
          ${actionButton('copy-summary', `${icon('copy')}Copy summary`)}
          ${actionButton('clone', `${icon('copy')}Clone job`, '', job.ref)}
          ${job.status !== 'executing' && job.status !== 'failed' ? actionButton('retry', `${icon('refresh')}Run again`, '', job.ref) : ''}
          ${!terminal ? actionButton('cancel', `${icon('x')}Cancel job`, 'danger', job.ref) : ''}
          <hr>${actionButton('details', 'Technical details')}${actionButton('related', 'Related pages')}
        </div></details>
      </div></header>`;
  }

  function renderCurrentState() {
    const failedChild = job.children.find((child) => child.status === 'failed');
    const problem = job.status === 'failed' || !!failedChild;
    const action = failedChild ? actionButton('inspect', 'Inspect failed child ' + icon('arrow'), 'primary', failedChild.ref)
      : job.status === 'failed' ? actionButton('retry', icon('refresh') + 'Run again', 'primary', job.ref)
      : job.wait?.kind === 'signal' ? actionButton('signal', 'Raise signal', 'primary', job.ref)
      : job.result ? actionButton('payload', 'View result ' + icon('arrow'), '', job.ref)
      : job.kind === 'permanent' ? actionButton('run-now', icon('play') + 'Run now', '', job.ref)
      : '';
    const context = [];
    if (job.status === 'failed') context.push(`<span>Attempt ${job.attempts} of ${job.maxAttempts}</span>`);
    if (job.status === 'succeeded') context.push(`<span>Completed in ${job.duration}</span>`);
    if (job.wait?.kind === 'children') context.push(`<span>Waiting on <code>${escape(job.wait.name)}</code></span>`);
    if (job.wait?.kind === 'signal') context.push(`<span>Signal <code>${escape(job.wait.name)}</code></span>`);
    if (job.wait?.due) context.push(`<span>Expires ${time(job.wait.dueLabel, job.wait.due)}</span>`);
    if (problem || job.status === 'succeeded') context.push(`<span>${time(problem ? 'Last failure 6m ago' : 'Completed 6m ago', job.modified)}</span>`);
    return `<section class="current-state ${problem ? 'problem' : ''}" aria-label="Current job state"><div>
      <div class="state-title">${problem ? icon('alert') : ''}${escape(job.headline)}</div>
      ${job.reason ? `<p class="state-description">${escape(job.reason)}</p>` : ''}
      ${context.length ? `<div class="state-context">${context.join('')}</div>` : ''}
    </div>${action ? `<div class="state-primary">${action}</div>` : ''}</section>`;
  }

  function renderCounts(children) {
    const counts = children.reduce((result, child) => ({ ...result, [child.status]: (result[child.status] || 0) + 1 }), {});
    return `<div class="counts">${Object.entries(counts).map(([kind, count]) => `<span class="${kind}"><i class="dot" aria-hidden="true"></i>${count} ${labels[kind].toLowerCase()}</span>`).join('')}</div>`;
  }

  function renderJobNode(node, parent, root = false) {
    return `<article class="job-node ${node.status === 'failed' ? 'is-problem' : ''} ${state.selected === node.ref ? 'is-selected' : ''}" data-graph-node="${node.ref}" ${parent ? `data-parent="${parent}"` : ''}>
      <button type="button" class="node-target" data-action="inspect" data-ref="${node.ref}" aria-label="Inspect ${node.ref === job.ref ? 'current job' : escape(node.name)}" title="${node.ref}"><span class="node-name">${root && node.ref === job.ref ? 'This job' : escape(node.name)}</span></button>
      <div class="node-meta">${status(node)}<span class="${node.status === 'succeeded' ? 'node-duration' : ''}">${node.status === 'succeeded' ? node.duration : node.status === 'executing' ? 'In progress' : node.status === 'failed' ? `Attempt ${node.attempts}` : ''}</span></div>
      ${node.children.length && !root ? `<button type="button" class="node-branch" data-action="branch" data-ref="${node.ref}">${node.children.length} children ${icon('chevron')}</button>` : ''}
    </article>`;
  }

  function branchTrail(branch) {
    const ancestors = [];
    let cursor = branch;
    while (cursor?.parent) {
      cursor = nodes.get(cursor.parent);
      ancestors.unshift(cursor);
    }
    return ancestors.length ? `<nav class="branch-trail" aria-label="Lineage path">${ancestors.map((ancestor) => actionButton('branch', ancestor.ref === job.ref ? 'This job' : escape(ancestor.name), 'text-link', ancestor.ref) + icon('chevron')).join('')}<span>${escape(branch.name)}</span></nav>` : '';
  }

  function renderLineage(mode = 'fan') {
    if (!job.children.length) return '';
    const branch = nodes.get(state.branch) || nodes.get(job.ref);
    const priority = { failed: 0, executing: 1, suspended: 2, ready: 3, succeeded: 4 };
    const children = [...branch.children].sort((a, b) => priority[a.status] - priority[b.status]);
    const visible = mode === 'fan' ? children.slice(0, 4) : children.filter((child) => state.filter === 'all' || (state.filter === 'problems' ? child.status === 'failed' : ['executing', 'suspended', 'ready'].includes(child.status)));
    const selected = nodes.get(state.selected);
    const descendants = mode === 'columns' && selected?.parent === branch.ref ? selected.children : [];
    let diagram;
    if (mobile.matches) {
      diagram = `<div class="mobile-lineage">${renderOutline(branch, false)}</div>`;
    } else if (mode === 'fan') {
      diagram = `<div class="lineage-stage fan-graph"><svg class="connectors" aria-hidden="true"></svg><div class="fan-root">${renderJobNode(branch, null, true)}</div><div class="fan-children">${visible.map((child) => renderJobNode(child, branch.ref)).join('')}</div></div>`;
    } else {
      diagram = `<div class="graph-viewport"><div class="lineage-stage column-graph ${descendants.length ? 'with-descendants' : ''} ${visible.length > 8 ? 'large-family' : ''}"><svg class="connectors" aria-hidden="true"></svg><div class="fan-root">${renderJobNode(branch, null, true)}</div><div class="graph-column">${visible.map((child) => renderJobNode(child, branch.ref)).join('') || '<p class="muted small">No child jobs match this filter.</p>'}</div>${descendants.length ? `<div class="graph-column">${descendants.map((child) => renderJobNode(child, selected.ref)).join('')}</div>` : ''}</div></div>`;
    }
    return `<section class="panel" aria-label="Lineage preview"><header class="panel-head"><div><h2>Lineage</h2><p>${branch.children.length} direct child jobs${branch.ref !== job.ref ? ' in this branch' : ''}</p></div><div class="panel-tools">${renderCounts(branch.children)}${mode === 'fan' ? actionButton('show-tree', 'Expand lineage ' + icon('arrow')) : ''}</div></header>
      ${branchTrail(branch)}${mode === 'columns' ? `<div class="filter-pills" aria-label="Filter child jobs">${[['all', 'All'], ['problems', 'Problems'], ['active', 'Active']].map(([value, label]) => `<button type="button" data-action="filter" data-value="${value}" aria-pressed="${state.filter === value}">${label}</button>`).join('')}</div>` : ''}
      ${diagram}<footer class="lineage-caption"><span>${mode === 'fan' && branch.children.length > 4 ? `Showing 4 of ${branch.children.length} children. Active and failed jobs appear first.` : mode === 'columns' && visible.length !== branch.children.length ? `${visible.length} of ${branch.children.length} direct children shown.` : 'Select a job to inspect it. Open a branch to explore its children.'}</span>${branch.ref !== job.ref ? actionButton('branch', 'Back to this job', 'text-link', job.ref) : ''}</footer>
    </section>`;
  }

  function renderOutline(root = job, includePanel = true) {
    function row(node, depth = 0) {
      const expanded = state.expanded.has(node.ref);
      const toggle = node.children.length ? `<button type="button" class="tree-toggle" data-action="expand" data-ref="${node.ref}" aria-expanded="${expanded}" aria-label="${expanded ? 'Collapse' : 'Expand'} ${node.ref === job.ref ? 'current job' : escape(node.name)}">${icon('chevron')}</button>` : `<span aria-hidden="true">${icon(statusIcon(node.status))}</span>`;
      return `<div class="outline-row ${state.selected === node.ref ? 'is-selected' : ''} ${node.status === 'failed' ? 'problem' : ''}">${toggle}<button type="button" class="node-target" data-action="inspect" data-ref="${node.ref}" title="${node.ref}">${node.ref === job.ref ? 'This job' : escape(node.name)}${node.children.length ? ` <span class="muted small">· ${node.children.length}</span>` : ''}</button>${status(node)}<span class="outline-duration">${node.status === 'succeeded' ? node.duration : ''}</span></div>${node.children.length && expanded ? `<div class="outline-children">${node.children.map((child) => row(child, depth + 1)).join('')}</div>` : ''}`;
    }
    const body = `<div class="outline-tree">${row(root)}</div>`;
    return includePanel ? `<section class="panel" aria-label="Lineage outline"><header class="panel-head"><div><h2>Lineage</h2><p>Expand branches to explore the family.</p></div><div class="panel-tools">${actionButton('expand-all', 'Expand all')}${actionButton('collapse-all', 'Collapse all')}</div></header>${body}</section>` : body;
  }

  function renderActivity(node = job, title = 'Recent activity', collapsed = false) {
    if (!node.events.length) return '';
    const events = title === 'Recent activity' ? node.events.slice(0, 3) : node.events;
    const rows = events.map((event) => `<article class="activity-row ${event.status}">${icon(statusIcon(event.status))}<div><strong>${escape(event.title)}</strong><p>${escape(event.detail)}</p></div>${time(event.when, event.at)}</article>`).join('');
    const more = node.events.length > events.length ? actionButton('history', 'Full history ' + icon('arrow'), 'text-link', node.ref) : '';
    if (collapsed) return `<details class="disclosure activity-disclosure"><summary>Activity<span class="muted">${node.events.length} recorded events</span></summary><div class="activity-list">${rows}${more}</div></details>`;
    return `<section class="panel" aria-label="${title}"><header class="panel-head"><div><h2>${title}</h2><p>Execution and state changes.</p></div>${more}</header><div class="activity-list">${rows}</div></section>`;
  }

  function renderJson(value) {
    const tokens = JSON.stringify(value, null, 2).match(/"(?:\\.|[^"\\])*"(?=\s*:)|"(?:\\.|[^"\\])*"|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null|[\s\S]/g) || [];
    return tokens.map((token) => {
      if (token.startsWith('"')) return `<span class="${token.endsWith('"') && token.includes('"') ? 'json-string' : ''}">${escape(token)}</span>`;
      if (/^-?\d/.test(token)) return `<span class="json-number">${escape(token)}</span>`;
      return escape(token);
    }).join('').replace(/<span class="json-string">([^<]*)<\/span>(?=\s*:)/g, '<span class="json-key">$1</span>');
  }

  function renderPayloads(node = job, open = false) {
    if (node.input === null && node.result === null && !node.checkpoints.length) return '';
    return `<details class="disclosure" id="payloads" ${open ? 'open' : ''}><summary>${node.checkpoints.length ? 'Data and checkpoints' : 'Stored data'}<span class="muted">${[node.input !== null ? 'Input' : '', node.result !== null ? 'Result' : '', node.checkpoints.length ? `${node.checkpoints.length} checkpoints` : ''].filter(Boolean).join(' · ')}</span></summary><div class="disclosure-body">
      <div class="payload-pair">${[['Input', node.input], ['Result', node.result]].filter(([, value]) => value !== null).map(([label, value]) => `<section class="payload"><h3>${label}</h3><pre>${renderJson(value)}</pre></section>`).join('')}</div>
      ${node.checkpoints.length ? `<div style="margin-top:16px">${node.checkpoints.map((checkpoint) => `<div class="checkpoint-row"><span>${checkpoint.kind}</span><code>${escape(checkpoint.name)}</code><span>${escape(checkpoint.value)}</span></div>`).join('')}</div>` : ''}
    </div></details>`;
  }

  function renderSchedules() {
    if (!job.schedules.length) return '';
    return `<section class="panel" aria-label="Schedules"><header class="panel-head"><div><h2>Schedules</h2><p>This permanent job is fired by ${job.schedules.length} schedules.</p></div></header><div class="panel-body">${job.schedules.map((schedule) => `<div class="schedule-row"><div><strong>${schedule.name}</strong><p><code>${schedule.expression}</code> · ${schedule.zone}</p></div>${time(schedule.next, schedule.at)}${actionButton('schedule', 'Open ' + icon('arrow'), '', schedule.name)}</div>`).join('')}</div></section>`;
  }

  function renderTechnical() {
    return `<details class="disclosure" id="technical-details"><summary>Technical details<span class="muted">Identifiers, dates, policy</span></summary><div class="disclosure-body"><dl class="detail-grid">
      <div><dt>Reference</dt><dd>${refButton(job.ref)}</dd></div><div><dt>Kind</dt><dd>${job.kind === 'permanent' ? 'Permanent' : 'Ordinary'}</dd></div>
      <div><dt>Created</dt><dd>${time('18m ago', job.created)}</dd></div><div><dt>Modified</dt><dd>${time('6m ago', job.modified)}</dd></div>
      <div><dt>Priority</dt><dd>Normal</dd></div><div><dt>Recorded executions</dt><dd>${job.attempts}</dd></div>
      ${job.correlation ? `<div><dt>Correlation key</dt><dd class="mono">${escape(job.correlation)}</dd></div>` : ''}${job.deduplication ? `<div><dt>Deduplication key</dt><dd class="mono">${escape(job.deduplication)}</dd></div>` : ''}
    </dl></div></details>`;
  }

  function renderInspector(node, inDrawer = false) {
    const info = [];
    if (node.wait) info.push(`<div><dt>Waiting on</dt><dd>${node.wait.kind} <code>${escape(node.wait.name)}</code></dd></div>`);
    if (node.status === 'failed') info.push(`<div><dt>Attempts</dt><dd>${node.attempts} of ${node.maxAttempts}</dd></div>`);
    if (node.failureCode) info.push(`<div><dt>Failure</dt><dd class="mono">${escape(node.failureCode)}</dd></div>`);
    if (node.status === 'succeeded') info.push(`<div><dt>Execution duration</dt><dd>${node.duration}</dd></div>`);
    if (node.worker) info.push(`<div><dt>Worker</dt><dd>${escape(node.worker.host)} · heartbeat ${node.worker.heartbeat}</dd></div>`);
    const actions = node.status === 'failed' ? actionButton('retry', icon('refresh') + 'Run again', 'primary', node.ref)
      : node.children.length ? actionButton('branch', 'Explore children ' + icon('arrow'), '', node.ref)
      : node.result ? actionButton('payload', 'View result', '', node.ref) : '';
    return `${inDrawer ? '' : `<header class="panel-head"><div><p class="eyebrow">Selected job</p><h2 style="margin-top:4px">${node.ref === job.ref ? 'This job' : escape(node.name)}</h2></div></header>`}<div class="inspector-body">
      ${status(node)}${refButton(node.ref)}
      ${node.reason || node.headline ? `<p class="inspector-note ${node.status === 'failed' ? 'problem' : ''}">${escape(node.reason || node.headline)}</p>` : ''}
      ${info.length ? `<dl class="inspector-facts">${info.join('')}</dl>` : ''}
    </div>${actions ? `<footer class="inspector-footer">${actions}</footer>` : ''}`;
  }

  function renderFocus() {
    return `<div class="section-stack">${renderLineage()}${renderSchedules()}${job.result ? renderPayloads(job, true) : ''}${renderActivity(job, 'Recent activity', true)}${!job.result ? renderPayloads() : ''}${renderTechnical()}</div>`;
  }

  function renderWorkspace() {
    const tabs = [['overview', 'Overview']];
    if (job.children.length) tabs.push(['lineage', 'Lineage']);
    if (job.events.length) tabs.push(['activity', 'Activity']);
    if (job.schedules.length) tabs.push(['schedules', `Schedules · ${job.schedules.length}`]);
    if (!tabs.some(([id]) => id === state.tab)) state.tab = 'overview';
    const nav = `<nav class="section-tabs" aria-label="Job views">${tabs.map(([id, label]) => `<button type="button" data-action="tab" data-value="${id}" aria-selected="${state.tab === id}">${label}</button>`).join('')}</nav>`;
    let content;
    if (state.tab === 'lineage') content = `<div class="workspace"><div>${renderLineage('columns')}</div><aside class="panel inspector-panel" aria-label="Selected job">${renderInspector(nodes.get(state.selected))}</aside></div>`;
    else if (state.tab === 'activity') content = renderActivity(job, 'Activity');
    else if (state.tab === 'schedules') content = renderSchedules();
    else content = `<div class="section-stack">${job.children.length ? renderLineage() : renderActivity()}${renderPayloads(job, !!job.result)}${renderSchedules()}</div>`;
    return `${nav}<div class="section-stack">${content}${renderTechnical()}</div>`;
  }

  function renderOutlineLayout() {
    return `<div class="section-stack">${job.children.length ? `<div class="outline-layout"><div>${renderOutline()}</div><aside class="panel" aria-label="Selected job">${renderInspector(nodes.get(state.selected))}</aside></div>` : ''}${renderSchedules()}${job.result ? renderPayloads(job, true) : ''}${renderActivity()}${!job.result ? renderPayloads() : ''}${renderTechnical()}</div>`;
  }

  function render() {
    document.documentElement.dataset.theme = state.theme;
    const content = state.variant === 'workspace' ? renderWorkspace() : state.variant === 'outline' ? renderOutlineLayout() : renderFocus();
    app.innerHTML = `${renderPrototypeBar()}<div class="shell">${renderSidebar()}<main id="job-content" tabindex="-1">${renderHeader()}${renderCurrentState()}${content}
      <footer class="related-footer"><span>Related</span>${actionButton('definition', 'Definition', 'text-link')}${actionButton('namespace', 'Namespace', 'text-link')}${actionButton('events', 'Events', 'text-link')}</footer>
      <p class="idea-note">${variants[state.variant].description} Exact timestamps and complete identifiers are available on hover, keyboard focus, and click.</p>
    </main></div>`;
    app.querySelector('[data-action="refresh"]')?.setAttribute('aria-label', 'Refresh prototype');
    persistLocation();
    requestAnimationFrame(drawConnectors);
  }

  function drawConnectors() {
    app.querySelectorAll('.lineage-stage').forEach((stage) => {
      const svg = stage.querySelector('.connectors');
      if (!svg) return;
      const origin = stage.getBoundingClientRect();
      const fan = stage.classList.contains('fan-graph');
      const boxes = new Map([...stage.querySelectorAll('[data-graph-node]')].map((node) => [node.dataset.graphNode, node.getBoundingClientRect()]));
      svg.setAttribute('viewBox', `0 0 ${stage.offsetWidth} ${stage.offsetHeight}`);
      svg.innerHTML = [...stage.querySelectorAll('[data-parent]')].map((node) => {
        const parent = boxes.get(node.dataset.parent);
        const child = boxes.get(node.dataset.graphNode);
        if (!parent || !child) return '';
        const start = fan ? [parent.x + parent.width / 2 - origin.x, parent.bottom - origin.y] : [parent.right - origin.x, parent.y + parent.height / 2 - origin.y];
        const end = fan ? [child.x + child.width / 2 - origin.x, child.y - origin.y] : [child.x - origin.x, child.y + child.height / 2 - origin.y];
        const middle = fan ? (start[1] + end[1]) / 2 : (start[0] + end[0]) / 2;
        const path = fan ? `M${start} V${middle} H${end[0]} V${end[1]}` : `M${start} H${middle} V${end[1]} H${end[0]}`;
        return `<path class="${node.classList.contains('is-problem') ? 'problem' : ''}" d="${path}"></path>`;
      }).join('');
    });
  }

  function showToast(message) {
    const toast = document.getElementById('toast');
    toast.textContent = message;
    toast.classList.add('visible');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toast.classList.remove('visible'), 3000);
  }

  function openDrawer(title, content) {
    inspector.innerHTML = `<header class="drawer-head"><h2 id="inspector-title">${escape(title)}</h2><button type="button" class="icon-button" data-action="close-drawer" aria-label="Close inspector">${icon('x')}</button></header><div class="drawer-content">${content}</div>`;
    if (!inspector.open) inspector.showModal();
    inspector.querySelector('[aria-label="Close inspector"]').focus({ preventScroll: true });
  }

  function inspect(reference) {
    state.selected = reference;
    const selected = nodes.get(reference);
    if (!selected) return;
    render();
    app.querySelector(`.node-target[data-ref="${reference}"]`)?.focus({ preventScroll: true });
    if (state.variant === 'focus' || mobile.matches) openDrawer(selected.ref === job.ref ? 'This job' : selected.name, renderInspector(selected, true));
  }

  function openAction(kind, reference) {
    pendingAction = { kind, reference };
    const selected = nodes.get(reference) || job;
    const copy = {
      retry: ['Run this job again?', 'Re-arms the same job from its persisted input. External side effects from an earlier attempt may be repeated.', 'Preview run again'],
      cancel: ['Cancel this job?', 'Cancels this job and its non-terminal descendants. Finished descendants retain their results.', 'Preview cancellation'],
      'run-now': ['Run now?', 'Triggers this permanent job without moving its recurring schedule cursor.', 'Preview firing'],
      signal: ['Raise editor-approval?', 'Delivers the approval signal this job is waiting for. The waiting handler can then continue.', 'Preview signal'],
      clone: ['Clone this job?', 'Creates a new job using a copy of the stored input.', 'Preview clone'],
    }[kind];
    confirmation.innerHTML = `<h2 id="confirmation-title">${copy[0]}</h2><p>${escape(copy[1])}</p><p class="small mono">${escape(selected.name)} · job…${selected.ref.slice(-8)}</p><div class="actions">${actionButton('close-confirmation', 'Keep as is')}${actionButton('confirm-preview', copy[2], 'primary')}</div>`;
    if (!confirmation.open) confirmation.showModal();
  }

  function activate(action, element) {
    const reference = element.dataset.ref || job.ref;
    const selected = nodes.get(reference) || job;
    if (action === 'variant') {
      state.variant = element.dataset.value;
      state.tab = state.variant === 'workspace' && job.children.length ? 'lineage' : 'overview';
      render();
    } else if (action === 'inspect') inspect(reference);
    else if (action === 'branch') {
      state.branch = reference;
      state.selected = selected.children.find((child) => child.status === 'failed')?.ref || selected.children[0]?.ref || reference;
      state.expanded.add(reference);
      inspector.close();
      render();
    } else if (action === 'expand') {
      state.expanded.has(reference) ? state.expanded.delete(reference) : state.expanded.add(reference);
      render();
      if (inspector.open && inspector.querySelector('.outline-tree')) openDrawer('Job family', renderOutline());
    } else if (action === 'expand-all' || action === 'show-tree') {
      state.expanded = new Set(nodes.keys());
      if (action === 'show-tree') openDrawer('Job family', renderOutline());
      else {
        render();
        if (inspector.open && inspector.querySelector('.outline-tree')) openDrawer('Job family', renderOutline());
      }
    } else if (action === 'collapse-all') {
      state.expanded = new Set([job.ref]);
      render();
      if (inspector.open && inspector.querySelector('.outline-tree')) openDrawer('Job family', renderOutline());
    } else if (action === 'filter') {
      state.filter = element.dataset.value;
      render();
    } else if (action === 'tab') {
      state.tab = element.dataset.value;
      render();
    } else if (action === 'reference') openDrawer('Full job reference', `<p class="eyebrow">Full reference</p><code class="ref-full">${escape(reference)}</code>${actionButton('copy-reference', icon('copy') + 'Copy full reference', '', reference)}`);
    else if (action === 'copy-reference' || action === 'copy-summary') {
      const value = action === 'copy-reference' ? reference : `${job.name}\n${job.ref}\n${job.status}\n${job.reason || job.headline}`;
      if (navigator.clipboard) navigator.clipboard.writeText(value).then(() => showToast(action === 'copy-reference' ? 'Full reference copied.' : 'Job summary copied.')).catch(() => openDrawer('Copy value', `<p class="muted">Select the value to copy it.</p><code class="ref-full">${escape(value)}</code>`));
      else openDrawer('Copy value', `<p class="muted">Select the value to copy it.</p><code class="ref-full">${escape(value)}</code>`);
    } else if (action === 'time') openDrawer('Exact date and time', `<p class="eyebrow">UTC</p><p style="margin:8px 0 16px">${escape(exactTime(element.dataset.at))}</p><code class="ref-full">${escape(element.dataset.at)}</code><p class="small muted">Hover or focus a relative time to reveal its full timestamp.</p>`);
    else if (action === 'close-drawer') inspector.close();
    else if (action === 'close-confirmation') confirmation.close();
    else if (['retry', 'cancel', 'signal', 'run-now', 'clone'].includes(action)) openAction(action, reference);
    else if (action === 'confirm-preview') {
      confirmation.close();
      const target = nodes.get(pendingAction.reference) || job;
      showToast(`Prototype action preview · ${pendingAction.kind} · ${target.name}`);
    } else if (action === 'payload') openDrawer(selected.result ? 'Result' : 'Stored data', `<div class="payload">${selected.result !== null ? `<h3>Result</h3><pre>${renderJson(selected.result)}</pre>` : `<h3>Input</h3><pre>${renderJson(selected.input)}</pre>`}</div>`);
    else if (action === 'details') {
      const details = document.getElementById('technical-details');
      details.open = true;
      details.scrollIntoView({ block: 'center' });
    } else if (action === 'schedule') {
      const schedule = job.schedules.find((item) => item.name === element.dataset.ref);
      openDrawer(schedule.name, `<dl class="inspector-facts"><div><dt>Expression</dt><dd class="mono">${schedule.expression}</dd></div><div><dt>Time zone</dt><dd>${schedule.zone}</dd></div><div><dt>Next firing</dt><dd>${time(schedule.next, schedule.at)}</dd></div><div><dt>Status</dt><dd>${schedule.status}</dd></div></dl>`);
    } else if (action === 'definition') openDrawer('Registered definition', `<dl class="inspector-facts"><div><dt>Namespace</dt><dd>${escape(job.namespace)}</dd></div><div><dt>Definition</dt><dd class="mono">${escape(job.name)}</dd></div><div><dt>Attempt budget</dt><dd>${job.maxAttempts} per firing</dd></div><div><dt>Priority</dt><dd>Normal</dd></div></dl>`);
    else if (action === 'namespace') openDrawer('Namespace', `<p class="mono">${escape(job.namespace)}</p><p class="muted small" style="margin-top:12px">Related namespace context belongs in a focused inspector.</p>`);
    else if (action === 'events' || action === 'history') openDrawer('Recorded events', renderActivity(selected, 'Activity'));
    else if (action === 'related') openDrawer('Related pages', `<div class="section-stack">${actionButton('definition', 'Definition ' + icon('arrow'))}${actionButton('namespace', 'Namespace ' + icon('arrow'))}${actionButton('events', 'Events ' + icon('arrow'))}</div>`);
    else if (action === 'refresh') showToast('Sample data refreshed.');
  }

  document.addEventListener('click', (event) => {
    const element = event.target.closest('[data-action]');
    if (!element) return;
    const scope = inspector.contains(element) ? inspector : app;
    const selector = `[data-action="${element.dataset.action}"]${element.dataset.ref ? `[data-ref="${element.dataset.ref}"]` : ''}${element.dataset.value ? `[data-value="${element.dataset.value}"]` : ''}`;
    activate(element.dataset.action, element);
    if (!confirmation.open && (!inspector.open || scope === inspector)) scope.querySelector(selector)?.focus({ preventScroll: true });
  });
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || inspector.open || confirmation.open) return;
    app.querySelectorAll('.overflow-menu[open]').forEach((menu) => {
      menu.open = false;
      menu.querySelector('summary').focus();
    });
  });
  document.addEventListener('change', (event) => {
    if (event.target.id === 'scenario-select') {
      state.scenario = event.target.value;
      resetScenario();
      render();
    } else if (event.target.id === 'theme-select') {
      state.theme = event.target.value;
      render();
    }
  });
  document.addEventListener('pointerdown', (event) => {
    app.querySelectorAll('.overflow-menu[open]').forEach((menu) => {
      if (!menu.contains(event.target)) menu.open = false;
    });
  });
  mobile.addEventListener('change', render);
  window.addEventListener('resize', drawConnectors);
  resetScenario();
  render();
})();
