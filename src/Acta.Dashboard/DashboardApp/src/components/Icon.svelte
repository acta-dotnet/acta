<script lang="ts">
  let { name }: { name: string } = $props();
  const paths: Record<string, string> = {
    'magnifying-glass': 'M10.2 10.2 14 14M11.5 6.5a5 5 0 1 1-10 0 5 5 0 0 1 10 0',
    'pause': 'M5 3v9M10 3v9',
    'play': 'm5 2.5 7 5-7 5z',
    'reload': 'M12.7 6a5.3 5.3 0 1 0-.2 3.5M12.7 2.2V6H9',
    'x': 'm3.5 3.5 8 8m0-8-8 8',
    'clock': 'M13 7.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0M7.5 4v3.5l2.5 1.7',
    'check-circle': 'M13 7.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0M5 7.5l1.7 1.8 3.6-3.8',
    'x-circle': 'M13 7.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0M5.5 5.5l4 4m0-4-4 4',
    'minus-circle': 'M13 7.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0M4.5 7.5h6',
    'warn': 'm7.5 1.5 6.5 11.5H1zM7.5 5v3.5m0 2v.2',
    'arrow-down': 'M7.5 2.5v10m-4-4 4 4 4-4',
    'arrow-up': 'M7.5 12.5v-10m-4 4 4-4 4 4',
    'chevron-left': 'm9.5 3.5-4 4 4 4',
    'chevron-right': 'm5.5 3.5 4 4-4 4',
    'copy': 'M5.25 4.75V2.25H12.75V10.25H10.25M2.25 4.75H10.25V12.75H2.25Z',
    'plus': 'M7.5 2.5v10M2.5 7.5h10',
    'lightning-bolt': 'm8.5 1-6 7h4L6 14l6.5-8H8z',
    'calendar': 'M2 3.5h11v10H2zM2 6.5h11M5 1.5v4m5-4v4M5 9h1m3 0h1M5 11h1',
    'counter-clockwise-clock': 'M2 5a5.5 5.5 0 1 1-.1 4M2 1.5V5h3.5M7.5 4v3.5l2.5 1.7',
    'person': 'M9.8 4.5a2.3 2.3 0 1 1-4.6 0 2.3 2.3 0 0 1 4.6 0M2.5 13v-1.5a5 4 0 0 1 10 0V13',
    'bell': 'M3 10.5V6a4.5 4.5 0 0 1 9 0v4.5M2 10.5h11M6 13h3',
    'activity-log': 'M2 3h.1M5 3h8M2 7.5h.1M5 7.5h8M2 12h.1M5 12h8',
    'dashboard': 'M2 2h4v4H2zM9 2h4v4H9zM2 9h4v4H2zM9 9h4v4H9z',
    'cube': 'm7.5 1 6 3.5v7l-6 3-6-3v-7zM1.5 4.5l6 3.2 6-3.2M7.5 7.7v6.8',
    'reader': 'M3 1.5h9v12H3zM5.5 4.5h4m-4 3h4m-4 3H8',
    'layers': 'm7.5 1 6 3-6 3-6-3zM1.5 7.5l6 3 6-3M1.5 11l6 3 6-3',
    'desktop': 'M1.5 2.5h12v8h-12zM7.5 10.5v3M4.5 13.5h6',
    'check': 'm3 7.5 3 3 6-6',
    'stopwatch': 'M12.5 8.5a5 5 0 1 1-10 0 5 5 0 0 1 10 0M5.5 1h4M7.5 1v2.5M11.5 4.5 13 3M7.5 5.5v3.3',
    'target': 'M13.5 7.5a6 6 0 1 1-12 0 6 6 0 0 1 12 0M10.5 7.5a3 3 0 1 1-6 0 3 3 0 0 1 6 0M7.5 7.5h.1',
  };
</script>

<svg class="ic" viewBox="0 0 15 15" aria-hidden="true">
  <path d={paths[name]} fill="none" stroke="currentColor" stroke-width={name === 'copy' ? 1.15 : 1.25} stroke-linecap="round" stroke-linejoin="round" />
</svg>
