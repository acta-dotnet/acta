<script>
  import { displayFormatter } from '../format';

  let { label, value, tone = '', href = null, hint = '', note = '' } = $props();
  let displayValue = $derived(typeof value === 'number' ? displayFormatter.number(value) : (value ?? '-'));
</script>

<svelte:element this={href ? 'a' : 'div'} class="metric {tone}" {href} title={hint}>
  <span class="metric-value">{displayValue}{#if note}<span class="metric-note">{note}</span>{/if}</span>
  <span class="metric-label">{label}</span>
</svelte:element>
