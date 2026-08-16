<script lang="ts">
  import { tick } from 'svelte';
  import { createQuery, type QueryClient } from '@tanstack/svelte-query';
  import { api, type NamespaceListItem, type Paged } from '../api.ts';
  import { keys, capabilitiesQuery } from '../query.ts';
  import { scope, setScope } from '../scope.ts';
  import { routes } from '../routes.ts';
  import { loadRecents, matchPages, parseQuery, pushRecent, type RecentItem } from '../lib/quickSearch.ts';
  import { jobsListSql, COPY_SQL_TITLE } from '../lib/copyAsSql.ts';
  import CopyButton from './CopyButton.svelte';
  import Icon from './Icon.svelte';

  interface DefinitionHit {
    jobNamespace: string;
    jobName: string;
  }
  // Namespaces reuse the exported list-item shape rather than a local hit interface: this palette
  // reads the same `namespaces` page NamespaceDetail does, and a private copy of the row silently
  // drifted from the wire field name once already.
  interface TenantHit {
    tenantKey: string;
    displayName: string | null;
    status: string;
  }

  interface Row {
    id: string;
    group: string;
    icon?: string;
    label: string;
    hint?: string;
    href?: string;
    run?: () => void | Promise<void>;
  }

  // Tests render the palette standalone; the optional client bypasses the provider context the
  // same way App.svelte does for its own top-level query.
  let { client = undefined }: { client?: QueryClient } = $props();
  // svelte-ignore state_referenced_locally
  const clientArg = client ? () => client! : undefined;

  const listId = 'quick-search-list';

  // Teach the grammar where it's needed: chips on the empty palette that prefill their prefix.
  const TRY_PREFIXES: Array<{ chip: string; hint: string }> = [
    { chip: 'ns:', hint: 'switch namespace scope' },
    { chip: 'tag:', hint: 'find tagged items' },
    { chip: 'corr:', hint: 'jobs by correlation id' },
    { chip: 'key:', hint: 'job by deduplication key' },
    { chip: 'id:', hint: 'job by internal id' }
  ];

  let open = $state(false);
  let raw = $state('');
  let debounced = $state('');
  let active = $state(0);
  let lookupError = $state('');
  let recents = $state<RecentItem[]>([]);
  let opener: Element | null = null;
  let inputEl: HTMLInputElement | null = $state(null);
  let boxEl: HTMLDivElement | null = $state(null);

  const capabilities = createQuery(() => capabilitiesQuery(), clientArg);

  // Recognition follows every keystroke (jump rows are instant); the network probes follow the
  // debounced text so a paste or fast typing costs one request per entity, not one per key.
  let recognition = $derived(parseQuery(raw));
  $effect(() => {
    const value = raw;
    const timer = setTimeout(() => (debounced = value), 150);
    return () => clearTimeout(timer);
  });
  let textQ = $derived.by(() => {
    const parsed = parseQuery(debounced);
    return parsed?.kind === 'text' ? parsed : null;
  });
  let probeEnabled = $derived(open && (textQ?.folded.length ?? 0) >= 2);

  const definitions = createQuery(() => ({
    queryKey: keys.list('palette-definitions', { nameContains: textQ?.folded }),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      api<Paged<DefinitionHit>>('definitions', { nameContains: textQ?.folded, pageSize: 5 }, { signal }),
    enabled: probeEnabled,
    staleTime: 15_000
  }), clientArg);
  const namespaces = createQuery(() => ({
    queryKey: keys.list('palette-namespaces', { nameContains: textQ?.folded }),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      api<Paged<NamespaceListItem>>('namespaces', { nameContains: textQ?.folded, pageSize: 5 }, { signal }),
    enabled: probeEnabled,
    staleTime: 15_000
  }), clientArg);
  const tenants = createQuery(() => ({
    queryKey: keys.list('palette-tenants', { nameContains: textQ?.raw }),
    queryFn: ({ signal }: { signal: AbortSignal }) =>
      api<Paged<TenantHit>>('tenants', { nameContains: textQ?.raw, pageSize: 5 }, { signal }),
    enabled: probeEnabled,
    staleTime: 15_000
  }), clientArg);

  // Hash link with palette-only params (tags / name filters that routes.* helpers don't cover).
  function hashHref(path: string, params: Record<string, string | undefined>): string {
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(params)) {
      if (value) search.set(key, value);
    }
    const encoded = search.toString();
    return '#/' + path + (encoded ? `?${encoded}` : '');
  }

  let rows = $derived.by((): Row[] => {
    const ns = $scope;
    const out: Row[] = [];
    const rec = recognition;

    if (!rec) {
      for (const item of recents) {
        out.push({ id: 'recent:' + item.href, group: 'Recent', icon: item.icon ?? 'counter-clockwise-clock', label: item.label, href: item.href });
      }
      for (const page of matchPages('', ns)) {
        out.push({ id: 'page:' + page.name, group: 'Pages', icon: page.icon, label: page.label, href: page.href });
      }
      return out;
    }

    if (rec.kind === 'jobRef') {
      out.push({ id: 'jump:ref', group: 'Jump', icon: 'cube', label: 'Open job ' + rec.ref, href: routes.job(rec.ref, { namespace: ns }) });
      return out;
    }
    if (rec.kind === 'workerRef') {
      out.push({ id: 'jump:worker', group: 'Jump', icon: 'desktop', label: 'Open worker ' + rec.ref, href: routes.worker(rec.ref, { namespace: ns }) });
      return out;
    }
    if (rec.kind === 'alertRef') {
      out.push({ id: 'jump:alert', group: 'Jump', icon: 'bell', label: 'Open alert ' + rec.ref, href: routes.alert(rec.ref, { namespace: ns }) });
      return out;
    }
    if (rec.kind === 'jobId') {
      out.push({ id: 'jump:id', group: 'Jump', icon: 'cube', label: 'Open job id ' + rec.id, hint: 'needs numeric-id lookup enabled on the host', href: routes.job('id:' + rec.id, { namespace: ns }) });
      return out;
    }
    if (rec.kind === 'correlation') {
      out.push({ id: 'jump:corr', group: 'Jump', icon: 'target', label: `Jobs with correlation "${rec.key}"`, href: routes.jobs({ namespace: ns, correlationKey: rec.key }) });
      return out;
    }
    if (rec.kind === 'dedupKey') {
      if (ns) {
        out.push({ id: 'jump:key', group: 'Jump', icon: 'cube', label: `Look up deduplication key "${rec.key}" in ${ns}`, run: () => lookupByKey(ns, rec.key) });
      } else {
        out.push({ id: 'jump:key-hint', group: 'Jump', icon: 'warn', label: 'Select a namespace scope to look up a deduplication key' });
      }
      return out;
    }
    if (rec.kind === 'scope') {
      out.push({ id: 'jump:scope', group: 'Jump', icon: 'layers', label: `Switch scope to ${rec.name}`, run: () => switchScope(rec.name) });
      return out;
    }
    if (rec.kind === 'tag') {
      // Every tag-bearing list, jobs first; each page reads the same `tags` filter param.
      const tagged: Array<[string, string, string]> = [
        ['Jobs', 'jobs', 'cube'],
        ['Tenants', 'tenants', 'person'],
        ['Definitions', 'definitions', 'reader'],
        ['Namespaces', 'namespaces', 'layers'],
        ['Schedules', 'schedules', 'calendar'],
        ['Workers', 'workers', 'desktop']
      ];
      for (const [label, path, icon] of tagged) {
        out.push({ id: 'tag:' + path, group: 'Tagged ' + rec.token, icon, label: label + ' tagged ' + rec.token, href: hashHref(path, { ns: ns || undefined, tags: rec.token }) });
      }
      return out;
    }

    for (const page of matchPages(rec.folded, ns)) {
      out.push({ id: 'page:' + page.name, group: 'Pages', icon: page.icon, label: page.label, href: page.href });
    }
    for (const hit of definitions.data?.items ?? []) {
      out.push({
        id: 'def:' + hit.jobNamespace + '/' + hit.jobName,
        group: 'Definitions',
        icon: 'reader',
        label: hit.jobName,
        hint: hit.jobNamespace,
        href: routes.definition(hit.jobNamespace, hit.jobName, { namespace: ns })
      });
    }
    for (const hit of namespaces.data?.items ?? []) {
      // Switching the working scope is the operator's common case; the admin detail stays a click
      // away through the namespaces page (the More row below).
      out.push({
        id: 'ns:' + hit.jobNamespace,
        group: 'Namespaces',
        icon: 'layers',
        label: hit.jobNamespace,
        hint: 'set scope',
        run: () => switchScope(hit.jobNamespace)
      });
    }
    for (const hit of tenants.data?.items ?? []) {
      out.push({
        id: 'tenant:' + hit.tenantKey,
        group: 'Tenants',
        icon: 'person',
        label: hit.tenantKey,
        hint: hit.displayName ?? undefined,
        href: routes.tenant(hit.tenantKey, { namespace: ns })
      });
    }
    out.push({
      id: 'more:corr',
      group: 'More',
      icon: 'target',
      label: `Jobs with correlation "${rec.raw}"`,
      href: routes.jobs({ namespace: ns, correlationKey: rec.raw })
    });
    out.push({
      id: 'more:defs',
      group: 'More',
      icon: 'reader',
      label: `All definitions matching "${rec.folded}"`,
      href: hashHref('definitions', { ns: ns || undefined, name: rec.folded })
    });
    out.push({
      id: 'more:namespaces',
      group: 'More',
      icon: 'layers',
      label: `All namespaces matching "${rec.folded}"`,
      href: hashHref('namespaces', { ns: ns || undefined, name: rec.folded })
    });
    return out;
  });

  // Clamp the active row when the list reshapes under the cursor.
  $effect(() => {
    if (active >= rows.length) active = rows.length > 0 ? rows.length - 1 : 0;
  });

  let correlationSql = $derived(
    recognition?.kind === 'correlation'
      ? jobsListSql(
          { namespace: $scope, correlationKey: recognition.key },
          { provider: capabilities.data?.provider, schema: capabilities.data?.schema }
        )
      : ''
  );

  export function openPalette(): void {
    opener = document.activeElement;
    open = true;
    raw = '';
    debounced = '';
    active = 0;
    lookupError = '';
    recents = loadRecents();
    void tick().then(() => inputEl?.focus());
  }

  function close(options: { restoreFocus: boolean } = { restoreFocus: true }): void {
    if (!open) return;
    open = false;
    if (options.restoreFocus && opener instanceof HTMLElement) opener.focus();
    opener = null;
  }

  function navigate(row: Row): void {
    if (!row.href) return;
    pushRecent({ href: row.href, label: row.label, icon: row.icon }, Date.now());
    close({ restoreFocus: false });
    location.hash = row.href;
  }

  // Apply the scope and stay open with a cleared query: the chip confirms it, and the operator
  // usually keeps searching within the scope they just picked.
  function switchScope(name: string): void {
    setScope(name);
    raw = '';
    debounced = '';
    active = 0;
    lookupError = '';
    recents = loadRecents();
    inputEl?.focus();
  }

  async function lookupByKey(ns: string, key: string): Promise<void> {
    lookupError = '';
    try {
      const job = await api<{ jobRef: string }>('jobs/by-key', { jobNamespace: ns, deduplicationKey: key });
      navigate({ id: 'jump:key', group: 'Jump', icon: 'cube', label: 'job ' + key, href: routes.job(job.jobRef, { namespace: ns }) });
    } catch (e) {
      lookupError = e instanceof Error ? e.message : String(e);
    }
  }

  function select(row: Row | undefined): void {
    if (!row) return;
    if (row.run) void row.run();
    else if (row.href) navigate(row);
  }

  async function move(delta: number): Promise<void> {
    if (rows.length === 0) return;
    active = Math.min(Math.max(active + delta, 0), rows.length - 1);
    await tick();
    boxEl?.querySelector<HTMLElement>('[data-active="true"]')?.scrollIntoView({ block: 'nearest' });
  }

  function isEditable(target: EventTarget | null): boolean {
    return (
      target instanceof HTMLElement
      && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)
    );
  }

  function onWindowKeydown(event: KeyboardEvent): void {
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      if (open) close();
      else openPalette();
      return;
    }
    if (event.key === '/' && !open && !event.ctrlKey && !event.metaKey && !event.altKey && !isEditable(event.target)) {
      event.preventDefault();
      openPalette();
    }
  }

  function onInputKeydown(event: KeyboardEvent): void {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      void move(event.key === 'ArrowDown' ? 1 : -1);
      return;
    }
    if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      void move(event.key === 'Home' ? -rows.length : rows.length);
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      select(rows[active]);
      return;
    }
  }

  function focusableItems(): HTMLElement[] {
    if (!boxEl) return [];
    return [...boxEl.querySelectorAll<HTMLElement>('button, input, [href], [tabindex]:not([tabindex="-1"])')]
      .filter((el) => !el.hasAttribute('disabled') && el.offsetParent !== null);
  }

  // Overlay-level: Escape closes from anywhere inside, Tab wraps within the palette so the scope
  // chip, prefix chips, and Copy SQL stay keyboard-reachable without focus escaping the overlay.
  function onOverlayKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close();
      return;
    }
    if (event.key !== 'Tab') return;
    const items = focusableItems();
    if (items.length === 0) return;
    event.preventDefault();
    const index = items.indexOf(document.activeElement as HTMLElement);
    const next = event.shiftKey
      ? (index <= 0 ? items.length - 1 : index - 1)
      : (index === items.length - 1 ? 0 : index + 1);
    items[next]?.focus();
  }
</script>

<svelte:window onkeydown={onWindowKeydown} />

{#if open}
  <div class="palette-overlay" role="presentation" onkeydown={onOverlayKeydown} onpointerdown={(event) => { if (event.target === event.currentTarget) close(); }}>
    <div class="palette" role="dialog" aria-modal="true" aria-label="Quick search" bind:this={boxEl}>
      <div class="palette-head">
        <Icon name="magnifying-glass" />
        <input
          bind:this={inputEl}
          type="text"
          role="combobox"
          aria-expanded="true"
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={rows.length > 0 ? listId + '-' + active : undefined}
          aria-label="Quick search"
          autocomplete="off"
          spellcheck="false"
          placeholder="Search or paste anything: names, job_/wrk_/alr_ refs, ns:, tags…"
          value={raw}
          oninput={(event) => { raw = event.currentTarget.value; active = 0; lookupError = ''; }}
          onkeydown={onInputKeydown} />
        {#if $scope}
          <button class="palette-scope" onclick={() => setScope('')} title="Clear namespace scope">
            {$scope} <Icon name="x" />
          </button>
        {/if}
      </div>

      {#if lookupError}<div class="palette-error" role="alert">{lookupError}</div>{/if}

      {#if !raw.trim()}
        <div class="palette-try">
          <span class="palette-try-label">Type a name, paste a job ref, or try</span>
          {#each TRY_PREFIXES as prefix}
            <button
              type="button"
              title={prefix.hint}
              onpointerdown={(event) => { if (event.button !== 0) return; event.preventDefault(); raw = prefix.chip; active = 0; inputEl?.focus(); }}>
              {prefix.chip}
            </button>
          {/each}
        </div>
      {/if}

      <ul class="palette-list" id={listId} role="listbox" aria-label="Results">
        {#each rows as row, index}
          {#if index === 0 || rows[index - 1].group !== row.group}
            <li class="palette-group" role="presentation">{row.group}</li>
          {/if}
          <li
            id={listId + '-' + index}
            role="option"
            aria-selected={active === index}
            data-active={active === index}
            class="palette-row"
            class:active={active === index}
            class:inert={!row.href && !row.run}
            onpointerdown={(event) => { if (event.button !== 0) return; event.preventDefault(); active = index; select(row); }}>
            {#if row.icon}<Icon name={row.icon} />{/if}
            <span class="palette-label">{row.label}</span>
            {#if row.hint}<span class="palette-hint">{row.hint}</span>{/if}
          </li>
        {/each}
        {#if rows.length === 0}
          <li class="palette-empty" role="presentation">
            No matches. Try a name fragment, a job ref, id:123, ns:&lt;namespace&gt;, key:&lt;dedup&gt;, corr:&lt;correlation&gt;, a name:value tag, or tag:&lt;name&gt;.
          </li>
        {/if}
      </ul>

      <div class="palette-foot">
        <span class="palette-keys"><kbd>↑↓</kbd> navigate · <kbd>Enter</kbd> open · <kbd>Esc</kbd> close</span>
        {#if correlationSql}<CopyButton value={correlationSql} label="Copy SQL" title={COPY_SQL_TITLE} />{/if}
      </div>
    </div>
  </div>
{/if}
