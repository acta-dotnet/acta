<script module lang="ts">
  export interface DetailFact {
    label: string;
    value: string | number | null | undefined;
    kind?: 'mono' | 'time' | 'reference';
    href?: string;
    copy?: boolean;
    wide?: boolean;
  }
</script>

<script lang="ts">
  import CopyButton from './CopyButton.svelte';
  import JobRef from './JobRef.svelte';
  import RelativeTime from './RelativeTime.svelte';
  import { displayFormatter } from '../format.ts';
  let { facts }: { facts: DetailFact[] } = $props();
  let visible = $derived(facts.filter((fact) => fact.value !== null && fact.value !== undefined));
</script>

<dl class="detail-readonly detail-readonly-grid">
  {#each visible as fact (fact.label)}
    <div class:detail-fact-wide={fact.wide}>
      <dt>{fact.label}</dt>
      <dd class:mono={fact.kind === 'mono'}>
        {#if fact.kind === 'time'}<RelativeTime value={String(fact.value)} />
        {:else if fact.kind === 'reference'}<JobRef value={String(fact.value)} href={fact.href} />
        {:else if fact.href}<a class:mono={fact.kind === 'mono'} href={fact.href}>{fact.value}</a>
        {:else}{typeof fact.value === 'number' ? displayFormatter.number(fact.value) : fact.value}{/if}
        {#if fact.copy}<CopyButton value={fact.value} label={'Copy ' + fact.label.toLowerCase()} />{/if}
      </dd>
    </div>
  {/each}
</dl>
