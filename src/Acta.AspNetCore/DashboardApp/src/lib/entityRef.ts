// Public entity refs (job_/wrk_/alr_) are a type tag plus 26 lowercase Crockford Base32 characters
// encoding the canonical big-endian UUID bytes - the same codec Acta's CrockfordBase32 writes: 130
// bits MSB-first, whose two leading pad bits must be zero.
//
// The dashboard renders refs and never needs the underlying uuid, with one exception: SQL copied for
// a human at a prompt, where the stored columns (workers.worker_ref, events.actor_key) hold the
// canonical lowercase uuid text, not the ref string. That is what this decoder is for.

// Crockford's alphabet, minus I, L, O, and U.
const ALPHABET = '0123456789abcdefghjkmnpqrstvwxyz';

// Crockford aliases: o reads as 0, and i / l read as 1. Decoding accepts them; encoding never emits them.
function digitOf(character: string): number {
  const lower = character.toLowerCase();
  if (lower === 'o') return 0;
  if (lower === 'i' || lower === 'l') return 1;
  return ALPHABET.indexOf(lower);
}

const REF = /^[a-z0-9]+_([0-9a-zA-Z]{26})$/;

/**
 * Decode a public entity ref to its canonical lowercase uuid text, or null when the value is not a
 * well-formed ref. The prefix is not checked against a known entity: any tag decodes, because the
 * caller already knows which entity it asked for.
 */
export function refToUuid(value: string | null | undefined): string | null {
  const match = REF.exec((value ?? '').trim());
  if (!match) return null;

  const symbols = match[1];
  // The first symbol carries the two pad bits, so it must decode below 8 for the value to fit 128 bits.
  if (digitOf(symbols[0]) < 0 || digitOf(symbols[0]) > 7) return null;

  let accumulator = 0n;
  for (const character of symbols) {
    const digit = digitOf(character);
    if (digit < 0) return null;
    accumulator = (accumulator << 5n) | BigInt(digit);
  }

  const hex = accumulator.toString(16).padStart(32, '0');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
