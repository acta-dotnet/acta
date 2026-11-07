// IANA zone ids for the time zone override suggestions. The browser already ships the full tzdb, so
// the list costs no bundle bytes and never goes stale. Suggestions only, never a closed set: the
// server resolves through TimeZoneInfo.FindSystemTimeZoneById, which on a Windows host also accepts
// Windows ids ("Central European Standard Time"), and blank clears the override.
export function ianaTimeZones(): string[] {
  const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  if (typeof supported !== 'function') return [];
  try {
    return supported.call(Intl, 'timeZone');
  } catch {
    return [];
  }
}

/** Case-insensitive substring match on the zone id, in list order (UTC and local stay on top). */
export function filterZones(zones: string[], query: string): string[] {
  const needle = query.trim().toLowerCase();
  if (needle === '') return zones;
  return zones.filter((zone) => zone.toLowerCase().includes(needle));
}

/**
 * The two likely picks first: UTC (what schedules run in unless overridden, and absent from the
 * canonical tzdb list) then the browser's own zone, followed by every other zone.
 */
export function suggestedTimeZones(): string[] {
  const zones = ianaTimeZones();
  if (zones.length === 0) return zones;
  const local = Intl.DateTimeFormat().resolvedOptions().timeZone;
  const lead = local && local !== 'UTC' && zones.includes(local) ? ['UTC', local] : ['UTC'];
  return [...lead, ...zones.filter((zone) => !lead.includes(zone))];
}
