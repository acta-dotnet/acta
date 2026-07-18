// Pure validators for SignalDrawer's two inputs. Split out so node --test can exercise them
// directly (no Svelte compiler needed) - same reason jobControlState.ts stays plain .ts.
//
// validateSignalName mirrors the backend's IdentifierSyntax.ValidateUserKebab
// (src/Acta.Contracts/Primitives/IdentifierSyntax.cs), which MapSignal calls at the wire boundary
// (src/Acta.AspNetCore/Features/Jobs/ActaControlEndpoints.cs): strict single-segment kebab
// (`^[a-z][a-z0-9-]*$`, no trailing '-'), reject the bare reserved name "sys" and anything starting
// with the "sys." prefix, max ExtendedMaxLength (128) chars. This is a client-side echo for fast
// inline feedback only - the server stays authoritative and re-validates on every request.
//
// validateSignalPayload mirrors ActaControlEndpoints.MapSignal's own empty-vs-JSON distinction: an
// empty (or whitespace-only) textarea raises a presence-only signal (zero-byte request body);
// anything else must be valid JSON, stored verbatim as the signal's payload.
const RESERVED_NAME = 'sys';
const RESERVED_PREFIX = 'sys.';
const MAX_LENGTH = 128; // IdentifierSyntax.ExtendedMaxLength

function isKebab(value: string): boolean {
  return value.length > 0 && !value.endsWith('-') && /^[a-z][a-z0-9-]*$/.test(value);
}

/** Returns an error message, or null if `name` is a legal signal name. */
export function validateSignalName(name: string): string | null {
  if (!name) {
    return 'Signal name is required.';
  }
  if (name.length > MAX_LENGTH) {
    return `Signal name must be at most ${MAX_LENGTH} characters.`;
  }
  if (!isKebab(name)) {
    return 'Signal name must be lowercase kebab-case: letters, digits, and hyphens only, starting with a letter (e.g. "payment-received").';
  }
  if (name === RESERVED_NAME || name.startsWith(RESERVED_PREFIX)) {
    return `"${RESERVED_NAME}" and the "${RESERVED_PREFIX}" prefix are reserved for system-internal signals.`;
  }
  return null;
}

export interface SignalPayloadResult {
  ok: boolean;
  error: string | null;
  /** Parsed payload, or undefined for an empty textarea (presence-only signal). */
  value?: unknown;
}

/** Empty (or whitespace-only) text is a presence-only signal; anything else must be valid JSON. */
export function validateSignalPayload(text: string): SignalPayloadResult {
  const trimmed = text.trim();
  if (trimmed === '') {
    return { ok: true, error: null, value: undefined };
  }
  try {
    return { ok: true, error: null, value: JSON.parse(trimmed) };
  } catch {
    return { ok: false, error: 'Payload must be valid JSON, or left empty for a presence-only signal.' };
  }
}
