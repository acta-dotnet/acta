<script>
  import { onMount } from 'svelte';
  import { hashParams, route } from './router';
  import { scope } from './scope';
  import { api, online } from './api';
  import ScopeSelector from './components/ScopeSelector.svelte';
  import AppearanceMenu from './components/AppearanceMenu.svelte';
  import QuickSearch from './components/QuickSearch.svelte';
  import Icon from './components/Icon.svelte';
  import LogoMark from './components/LogoMark.svelte';
  import { sidebarRail, toggleSidebarRail } from './theme/sidebar.ts';
  import Overview from './routes/Overview.svelte';
  import JobsList from './routes/JobsList.svelte';
  import JobDetail from './routes/JobDetail.svelte';
  import EnqueueJob from './routes/EnqueueJob.svelte';
  import EventsList from './routes/EventsList.svelte';
  import DefinitionsList from './routes/DefinitionsList.svelte';
  import DefinitionDetail from './routes/DefinitionDetail.svelte';
  import SchedulesList from './routes/SchedulesList.svelte';
  import ScheduleDetail from './routes/ScheduleDetail.svelte';
  import WorkersList from './routes/WorkersList.svelte';
  import WorkerDetail from './routes/WorkerDetail.svelte';
  import AlertsList from './routes/AlertsList.svelte';
  import NamespacesList from './routes/NamespacesList.svelte';
  import NamespaceDetail from './routes/NamespaceDetail.svelte';
  import TenantsList from './routes/TenantsList.svelte';
  import TenantDetail from './routes/TenantDetail.svelte';
  import NotFound from './routes/NotFound.svelte';
  import { navigationGroups, navigationHref, routeMetadata, routes } from './routes.ts';
  import { QueryClientProvider, createQuery } from '@tanstack/svelte-query';
  import { createAppQueryClient, capabilitiesQuery, canControl } from './query';

  const queryClient = createAppQueryClient();
  // App renders the QueryClientProvider in its own markup, so it is not itself inside that context.
  // Pass the client explicitly (same instance the provider gets) so this top-level fetch shares the
  // one cache key every child later reads from context - one fetch, not two.
  const capabilities = createQuery(() => capabilitiesQuery(), () => queryClient);

  let currentMetadata = $derived(routeMetadata[$route.name]);
  // The jobs list serves three nav entries (Jobs / Recurring jobs / Job history); the baked-in
  // view param decides which one lights up. hashchange re-runs this via $route.
  let activeNavName = $derived.by(() => {
    const base = currentMetadata.activeNav;
    if (base !== 'jobs' || $route.name !== 'jobs') return base;
    const view = hashParams().get('view');
    return view === 'recurring' ? 'jobs-recurring' : view === 'history' ? 'jobs-history' : 'jobs';
  });

  // Read-only gate every control surface reads (directly, via canControl(capabilities.data) on its
  // own createQuery(() => capabilitiesQuery()) - same cache key, one fetch). Fails closed (false)
  // until capabilities has loaded.
  let canControlNow = $derived(canControl(capabilities.data));
  // "Unknown while loading" is still read-only for controls, but it is not evidence that the host
  // has disabled them. Wait for the successful response before showing the operator banner.
  let showReadonlyBanner = $derived(capabilities.isSuccess && !canControlNow);
  let bannerDismissed = $state(false);
  let mobileNavOpen = $state(false);
  let quickSearch = $state();

  // While the backend is unreachable, probe a cheap endpoint so the banner clears on its own when the
  // process returns - even on a page that is not actively polling. Backs off 2s -> 4s -> ... -> 30s
  // rather than hammering once a second, and idles (no timer) while online.
  let heartbeat;
  onMount(() => {
    let delay = 2000;
    function probe() {
      api('overview').catch(() => {});
      delay = Math.min(delay * 2, 30_000);
      heartbeat = setTimeout(probe, delay);
    }
    const unsub = online.subscribe((up) => {
      if (up) {
        clearTimeout(heartbeat);
        heartbeat = undefined;
        delay = 2000;
      } else if (heartbeat === undefined) {
        delay = 2000;
        heartbeat = setTimeout(probe, delay);
      }
    });
    return () => {
      unsub();
      clearTimeout(heartbeat);
    };
  });
</script>

<QueryClientProvider client={queryClient}>
{#if !$online}
  <div class="offline-banner" role="status">
    Acta dashboard backend offline: the dashboard process is not responding. Reconnecting automatically.
  </div>
{/if}

{#if showReadonlyBanner && !bannerDismissed && $online}
  <div class="readonly-banner" role="status">
    Read-only - controls disabled on this host.
    <button class="dismiss" onclick={() => (bannerDismissed = true)} aria-label="Dismiss">×</button>
  </div>
{/if}

<div class="app">
  <button
    class="mobile-nav-toggle iconly"
    aria-label={mobileNavOpen ? 'Close navigation' : 'Open navigation'}
    aria-controls="dashboard-navigation"
    aria-expanded={mobileNavOpen}
    onclick={() => (mobileNavOpen = !mobileNavOpen)}>
    {mobileNavOpen ? '×' : '☰'}
  </button>

  <div class="shell">
    {#if mobileNavOpen}<button class="nav-scrim" aria-label="Close navigation" onclick={() => (mobileNavOpen = false)}></button>{/if}
    <nav id="dashboard-navigation" class="side" class:rail={$sidebarRail} class:mobile-open={mobileNavOpen} aria-label="Dashboard sections">
      <div class="brand-q">
        <a class="brand" href={routes.overview({ namespace: $scope })} aria-label="Acta: go to Overview" onclick={() => (mobileNavOpen = false)}>
          <span class="brand-mark"><LogoMark size={30} /></span><span class="nav-label">Acta<span class="brand-dot">.</span></span>
        </a>
        <div class="brand-sub">operator dashboard</div>
      </div>
      <div class="side-scope"><ScopeSelector /></div>
      <button
        class="side-search"
        onclick={() => { mobileNavOpen = false; quickSearch?.openPalette(); }}
        title={$sidebarRail ? 'Search (Ctrl+K)' : undefined}>
        <Icon name="magnifying-glass" /><span class="nav-label">Search</span><kbd class="nav-kbd">Ctrl K</kbd>
      </button>
      <!-- One each over every group; the first group takes the slack in CSS, so Configure and
           Admin settle at the bottom by the appearance trigger without duplicating markup. -->
      <div class="side-scroll">
        {#each navigationGroups as group}
          <section class="nav-group" aria-labelledby={'nav-' + group.label.toLowerCase()}>
            <h2 class="nav-group-label" id={'nav-' + group.label.toLowerCase()}>{group.label}</h2>
            {#each group.routes as item}
              <a
                href={navigationHref(item, $scope)}
                class:active={activeNavName === item.name}
                title={$sidebarRail ? item.label : undefined}
                onclick={() => (mobileNavOpen = false)}>
                {#if item.icon}<Icon name={item.icon} />{/if}<span class="nav-label">{item.label}</span>
              </a>
            {/each}
          </section>
        {/each}
      </div>
      <button
        class="side-collapse"
        onclick={toggleSidebarRail}
        title={$sidebarRail ? 'Expand sidebar' : 'Collapse sidebar'}
        aria-label={$sidebarRail ? 'Expand sidebar' : 'Collapse sidebar'}
        aria-expanded={!$sidebarRail}>
        <Icon name={$sidebarRail ? 'chevron-right' : 'chevron-left'} />
      </button>
      <div class="side-theme"><AppearanceMenu /></div>
    </nav>
    <main class:fill-page={currentMetadata.fullHeight}>
    {#if $route.name === 'overview'}
      <Overview />
    {:else if $route.name === 'jobs'}
      <JobsList />
    {:else if $route.name === 'job-detail'}
      {#key $route.jobRef}
        <JobDetail jobRef={$route.jobRef} />
      {/key}
    {:else if $route.name === 'enqueue'}
      <EnqueueJob />
    {:else if $route.name === 'events'}
      <EventsList />
    {:else if $route.name === 'definitions'}
      <DefinitionsList />
    {:else if $route.name === 'definition-detail'}
      {#key $route.defId}
        <DefinitionDetail defId={$route.defId} />
      {/key}
    {:else if $route.name === 'schedules'}
      <SchedulesList />
    {:else if $route.name === 'schedule-detail'}
      {#key $route.scheduleNamespace + '/' + $route.scheduleJobName + '/' + $route.scheduleName}
        <ScheduleDetail
          jobNamespace={$route.scheduleNamespace}
          jobName={$route.scheduleJobName}
          scheduleName={$route.scheduleName} />
      {/key}
    {:else if $route.name === 'workers'}
      <WorkersList />
    {:else if $route.name === 'worker-detail'}
      {#key $route.workerId}
        <WorkerDetail workerId={$route.workerId} />
      {/key}
    {:else if $route.name === 'alerts'}
      <AlertsList />
    {:else if $route.name === 'namespaces'}
      <NamespacesList />
    {:else if $route.name === 'namespace-detail'}
      {#key $route.namespaceName}
        <NamespaceDetail namespaceName={$route.namespaceName} />
      {/key}
    {:else if $route.name === 'tenants'}
      <TenantsList />
    {:else if $route.name === 'tenant-detail'}
      {#key $route.tenantKey}
        <TenantDetail tenantKey={$route.tenantKey} />
      {/key}
    {:else if $route.name === 'tenant-new'}
      <TenantDetail />
    {:else}
      <NotFound />
    {/if}
    </main>
  </div>
</div>
<QuickSearch bind:this={quickSearch} />
</QueryClientProvider>
