import { writable } from 'svelte/store';

const STORAGE_KEY = 'acta-sidebar-v1';

function load(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === 'rail';
  } catch {
    return false;
  }
}

/** true = the sidebar is collapsed to the icon-only rail. Desktop-only; the mobile drawer ignores it. */
export const sidebarRail = writable<boolean>(load());

sidebarRail.subscribe((rail) => {
  try {
    localStorage.setItem(STORAGE_KEY, rail ? 'rail' : 'expanded');
  } catch {
    // The in-memory setting still applies for this browser session.
  }
});

export function toggleSidebarRail(): void {
  sidebarRail.update((rail) => !rail);
}
