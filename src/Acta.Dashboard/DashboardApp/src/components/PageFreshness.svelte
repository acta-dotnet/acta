<script lang="ts">
  import FreshnessIndicator from './FreshnessIndicator.svelte';

  let {
    dataUpdatedAt = 0,
    isFetching = false,
    isError = false,
    polling = false,
    onRefresh = () => {}
  }: {
    dataUpdatedAt?: number;
    isFetching?: boolean;
    isError?: boolean;
    polling?: boolean;
    onRefresh?: () => void;
  } = $props();
</script>

<FreshnessIndicator {dataUpdatedAt} {isFetching} {isError} {polling} {onRefresh} />
