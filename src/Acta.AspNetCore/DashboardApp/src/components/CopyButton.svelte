<script>
  import Icon from './Icon.svelte';
  let { value, label = 'Copy', showLabel = false } = $props();

  let copied = $state(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(String(value));
      copied = true;
      setTimeout(() => (copied = false), 1500);
    } catch {
      // clipboard unavailable (insecure origin); leave the button inert
    }
  }
</script>

<button type="button" class="copy" title={copied ? 'Copied' : 'Copy to clipboard'} aria-label={copied ? 'Copied' : label} onclick={copy}>
  <Icon name={copied ? 'check-circle' : 'copy'} />
  {#if showLabel}<span>{copied ? 'Copied' : label}</span>{/if}
</button>
