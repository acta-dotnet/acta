<script lang="ts">
  import Page from '../components/Page.svelte';
  import { routes } from '../routes.ts';
  import { scope } from '../scope.ts';
</script>

<Page title="Page not found">
  <section class="panel">
    <p>The dashboard route in this URL does not exist.</p>
    <a href={routes.overview({ namespace: $scope })}>Return to Overview</a>
  </section>
</Page>

<style>
  /* Beside body text a link needs more than its colour to read as one. */
  a { text-decoration: underline; text-underline-offset: 3px; }
</style>
