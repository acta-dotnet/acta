<script lang="ts">
  import Icon from '../Icon.svelte';

  // One calendar version's status as a badge: a draft is work waiting to be published (warn), the
  // published version is the one schedules follow (ok), a superseded one is history, an archived one is
  // retired. The version number rides along when the badge stands alone.
  let { status, version = null }: { status: string; version?: number | null } = $props();

  const CLASSES: Record<string, string> = { draft: 'warn', published: 'ok', superseded: '', archived: 'retired' };
  const ICONS: Record<string, string> = { published: 'check-circle', archived: 'minus-circle' };
  let badgeClass = $derived(CLASSES[status] ?? '');
  let icon = $derived(ICONS[status] ?? null);
</script>

<span class="badge {badgeClass}">
  {#if icon}<Icon name={icon} />{:else}<i class="dot" aria-hidden="true"></i>{/if}
  {#if version !== null}<span class="mono">v{version}</span>{/if}
  {status}
</span>
