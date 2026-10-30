// The dashboard's copy of the key rules the enqueue path enforces (KeySyntax and DeduplicationKey in Acta): a key is
// trimmed and keeps its case, is printable ASCII without whitespace, is at most 128 characters as a deduplication
// key and 64 as a correlation key, and a deduplication key never starts with the reserved `sys.` prefix. The form
// checks before it sends, in the server's words, so a pasted key is never cut or quietly dropped; the server stays
// authoritative on what it stores. Pure functions, so node --test runs them.

export type KeyKind = 'deduplication' | 'correlation';

export const KEY_LIMITS: Record<KeyKind, number> = { deduplication: 128, correlation: 64 };

export interface KeyCheck {
  /** The key as the server would store it, or null when the field is empty or its text is not a key. */
  value: string | null;
  error: string | null;
}

const PRINTABLE_ASCII = /^[\x21-\x7e]+$/;

export function checkKey(raw: string, kind: KeyKind): KeyCheck {
  if (raw === '') return { value: null, error: null };
  const trimmed = raw.trim();
  if (trimmed === '') return { value: null, error: 'A key needs at least one character; clear the field to leave it out.' };
  const limit = KEY_LIMITS[kind];
  if (trimmed.length > limit) return { value: null, error: `Key length ${trimmed.length} exceeds the ${limit}-char limit.` };
  if (/\s/.test(trimmed)) return { value: null, error: 'A key holds no whitespace; join the parts with a dash, a dot, or a colon.' };
  if (!PRINTABLE_ASCII.test(trimmed)) {
    return { value: null, error: 'A key is printable ASCII (0x21 to 0x7E); this one has a character outside that range.' };
  }
  if (kind === 'deduplication' && trimmed.toLowerCase().startsWith('sys.')) {
    return { value: null, error: "The prefix 'sys.' is reserved for the system jobs' keys." };
  }
  return { value: trimmed, error: null };
}

export function keyHint(kind: KeyKind): string {
  return `Up to ${KEY_LIMITS[kind]} printable ASCII characters, no spaces; case counts.`;
}
