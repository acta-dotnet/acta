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
