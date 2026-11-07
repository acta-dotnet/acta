<script lang="ts">
  import VersionMark from './VersionMark.svelte';

  // One calendar version's status as a badge: a draft is work waiting to be published (warn), the
  // published version is the one schedules follow (ok), a superseded one is history, an archived one is
  // retired. The version number rides along when the badge stands alone.
  let { status, version = null }: { status: string; version?: number | null } = $props();

  const CLASSES: Record<string, string> = { draft: 'warn', published: 'ok', superseded: '', archived: 'retired' };
  let badgeClass = $derived(CLASSES[status] ?? '');
</script>

<span class="badge {badgeClass}">
  <VersionMark {status} />
  {#if version !== null}<span class="version">v{version}</span>{/if}
  {status}
</span>

<style>
  /* A badge capitalizes its word; a version marker is written v1, never V1. The number keeps the badge's own face, with
     tabular digits, so it and the word sit on one baseline. */
  .version { text-transform: none; font-variant-numeric: tabular-nums; }
</style>
