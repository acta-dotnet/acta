<script>
  // The Acta mark: a crossbar-less A with the ledger dot inside. currentColor, so it takes the
  // accent (or any text color) wherever it sits.
  let { size = 28 } = $props();
</script>

<svg class="logo-mark" width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">
  <path
    fill="currentColor"
    d="M10.62 2.6 1.9 20.55a.6.6 0 0 0 .54.86h2.4a.9.9 0 0 0 .81-.5L12 7.06l6.35 13.85a.9.9 0 0 0 .81.5h2.4a.6.6 0 0 0 .54-.86L13.38 2.6a1.53 1.53 0 0 0-2.76 0Z" />
  <circle fill="currentColor" cx="12" cy="16.4" r="2.5" />
</svg>
