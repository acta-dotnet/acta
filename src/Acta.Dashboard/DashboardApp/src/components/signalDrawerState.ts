// Pure validators for SignalDrawer's two inputs. Split out so node --test can exercise them
// directly (no Svelte compiler needed) - same reason jobControlState.ts stays plain .ts.
//
// validateSignalName mirrors the backend's NameSyntax.Canonicalize with reserveSystem
// (src/Acta/Primitives/NameSyntax.cs), which the signal endpoint calls at the wire boundary: the name
// is trimmed and lowercased, then must be dotted kebab (`[a-z][a-z0-9-]*` segments joined by '.', no
// trailing '-'), neither the bare reserved name "sys" nor under the "sys." prefix, max
// ExtendedMaxLength (128) chars. This is a client-side echo for fast inline feedback only - the server
// stays authoritative and re-validates on every request.
//
// validateSignalPayload mirrors the signal endpoint's own empty-vs-JSON distinction: an empty (or
// whitespace-only) textarea raises a presence-only signal (zero-byte request body); anything else must
// be valid JSON, sent and stored verbatim as the signal's payload, never re-serialized (lib/rawJson.ts).
import { jsonError } from '../lib/rawJson.ts';

const RESERVED_NAME = 'sys';
const RESERVED_PREFIX = 'sys.';
const MAX_LENGTH = 128; // NameSyntax.ExtendedMaxLength

function isKebab(segment: string): boolean {
  return segment.length > 0 && !segment.endsWith('-') && /^[a-z][a-z0-9-]*$/.test(segment);
}

/** Returns an error message, or null if `input` is a legal signal name. */
export function validateSignalName(input: string): string | null {
  const name = input.trim().toLowerCase();
  if (!name) {
    return 'Signal name is required.';
  }
  if (name.length > MAX_LENGTH) {
    return `Signal name must be at most ${MAX_LENGTH} characters.`;
  }
  if (!name.split('.').every(isKebab)) {
    return 'Signal name must be kebab-case: letters, digits, and hyphens, starting with a letter, with "." between segments (e.g. "payment-received").';
  }
  if (name === RESERVED_NAME || name.startsWith(RESERVED_PREFIX)) {
    return `"${RESERVED_NAME}" and the "${RESERVED_PREFIX}" prefix are reserved for system-internal signals.`;
  }
  return null;
}

export interface SignalPayloadResult {
  ok: boolean;
  error: string | null;
  /** The payload's JSON text, or undefined for an empty textarea (presence-only signal). */
  json?: string;
}

/** Empty (or whitespace-only) text is a presence-only signal; anything else must be valid JSON. */
export function validateSignalPayload(text: string): SignalPayloadResult {
  const trimmed = text.trim();
  if (trimmed === '') {
    return { ok: true, error: null, json: undefined };
  }
  return jsonError(trimmed) === null
    ? { ok: true, error: null, json: trimmed }
    : { ok: false, error: 'Payload must be valid JSON, or left empty for a presence-only signal.' };
}
