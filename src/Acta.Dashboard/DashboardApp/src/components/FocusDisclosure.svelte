<script lang="ts">
  import type { Snippet } from 'svelte';
  import Icon from './Icon.svelte';

  let { title, hint = '', open = $bindable(false), lazy = false, hideHeading = false, id, children }: {
    title: string;
    hint?: string;
    open?: boolean;
    lazy?: boolean;
    hideHeading?: boolean;
    id?: string;
    children: Snippet;
  } = $props();
</script>

<details class="focus-disclosure" class:hide-heading={hideHeading} {id} bind:open>
  <summary><Icon name="chevron-right" /><span class="disclosure-title">{title}</span>{#if hint}<span class="disclosure-hint">{hint}</span>{/if}</summary>
  {#if !lazy || open}<div class="focus-disclosure-body">{@render children()}</div>{/if}
</details>
