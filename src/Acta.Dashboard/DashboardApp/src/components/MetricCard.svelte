<script>
  import { displayFormatter } from '../format';
  import Icon from './Icon.svelte';

  // `direction` is which way the value is moving: its arrow takes the icon's place and its words join the hint,
  // so nothing is added beside the value. A link's hint is read out as its description; a card that is no link
  // has none read, so its label carries the words for a screen reader.
  let { label, value, tone = '', href = null, hint = '', note = '', icon = '', direction = null } = $props();
  let displayValue = $derived(typeof value === 'number' ? displayFormatter.number(value) : (value ?? '-'));
</script>

<svelte:element this={href ? 'a' : 'div'} class="metric {tone}" {href} title={[hint, direction?.text].filter(Boolean).join('. ')}>
  {#if direction || icon}<span class="metric-icon" class:direction><Icon name={direction?.icon ?? icon} /></span>{/if}
  <span class="metric-value">{displayValue}{#if note}<span class="metric-note">{note}</span>{/if}</span>
  <span class="metric-label">{label}{#if direction && !href}<span class="sr-only">, {direction.text}</span>{/if}</span>
</svelte:element>
