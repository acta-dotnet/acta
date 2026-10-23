// The text facts of an event's details payload. Acta writes one flat JSON object of named text facts
// (actor, reason, to, outcome, schedule); a missing payload, text that is not JSON, or JSON that is not an
// object reads as no facts, and a fact whose value is not text is skipped.
export function eventFacts(detailsText: string | null | undefined): Readonly<Record<string, string | undefined>> {
  if (!detailsText) return {};
  let parsed: unknown;
  try {
    parsed = JSON.parse(detailsText);
  } catch {
    return {};
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) return {};
  return Object.fromEntries(Object.entries(parsed).filter(([, value]) => typeof value === 'string'));
}
