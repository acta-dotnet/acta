<script>
  import { statusClass, statusIcon } from '../format';
  import Icon from './Icon.svelte';
  let { status } = $props();
  let icon = $derived(statusIcon(status));
</script>

<span class="badge {statusClass(status)}">{#if icon}<Icon name={icon} />{:else}<i class="dot" aria-hidden="true"></i>{/if}{status}</span>
