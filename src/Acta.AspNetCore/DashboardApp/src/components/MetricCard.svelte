<script>
  import { displayFormatter } from '../format';
  import Icon from './Icon.svelte';

  let { label, value, tone = '', href = null, hint = '', note = '', icon = '' } = $props();
  let displayValue = $derived(typeof value === 'number' ? displayFormatter.number(value) : (value ?? '-'));
</script>

<svelte:element this={href ? 'a' : 'div'} class="metric {tone}" {href} title={hint}>
  {#if icon}<span class="metric-icon"><Icon name={icon} /></span>{/if}
  <span class="metric-value">{displayValue}{#if note}<span class="metric-note">{note}</span>{/if}</span>
  <span class="metric-label">{label}</span>
</svelte:element>
