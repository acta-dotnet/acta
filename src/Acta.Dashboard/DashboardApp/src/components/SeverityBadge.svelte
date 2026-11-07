<script>
  import { statusClass, statusIcon } from '../format';
  import Icon from './Icon.svelte';
  let { severity } = $props();
  let icon = $derived(statusIcon(severity));
</script>

<span class="badge {statusClass(severity)}">{#if icon}<Icon name={icon} />{:else}<i class="dot" aria-hidden="true"></i>{/if}{severity}</span>
