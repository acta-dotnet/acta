// The dashboard's copy of the name and tag rules the enqueue path enforces (NameSyntax, TagInput, and TagLimits in
// Acta): a lane or a tag name is trimmed and folded to lowercase, is dotted kebab-case (segments of [a-z][a-z0-9-]*
// joined by dots, none ending in a dash) of at most 128 characters, and is never `sys` or under `sys.`; a tag value
// keeps its text, at most 128 characters with no control character, and an empty one is a presence-only tag; a job
// carries at most 32 tags, no two of one name once folded. The form checks before it sends, in the server's words, so
// a bad row is fixed in place and never cut or quietly dropped; the server stays authoritative on what it stores.
// Pure functions, so node --test runs them.

export const NAME_LIMIT = 128;
export const TAG_LIMITS = { count: 32, value: 128 };

export interface NameCheck {
  /** The name as the server would store it, or null when the field is empty or its text is not a name. */
  value: string | null;
  error: string | null;
}

const SEGMENT = '[a-z]([a-z0-9-]*[a-z0-9])?';
const DOTTED_KEBAB = new RegExp(`^${SEGMENT}(\\.${SEGMENT})*$`);

export function checkName(raw: string): NameCheck {
  if (raw === '') return { value: null, error: null };
  const name = raw.trim().toLowerCase();
  if (name === '') return { value: null, error: 'A name needs at least one character; clear the field to leave it out.' };
  if (name.length > NAME_LIMIT) return { value: null, error: `Identifier must not exceed ${NAME_LIMIT} characters (got ${name.length}).` };
  if (!DOTTED_KEBAB.test(name)) {
    return { value: null, error: `Identifier '${raw.trim()}' is not kebab-case or dotted-kebab (each segment must match [a-z][a-z0-9-]*).` };
  }
  if (name === 'sys' || name.startsWith('sys.')) return { value: null, error: "The name 'sys' and the prefix 'sys.' are reserved for the system." };
  return { value: name, error: null };
}

/** A tag row as typed; an empty value is a presence-only tag. */
export interface TagDraft {
  name: string;
  value: string;
}

export interface TagRowCheck {
  nameError: string | null;
  valueError: string | null;
}

export interface TagsCheck {
  /** The rows as the server would store them; undefined when there are none, so the request carries no tags member. */
  tags: { name: string; value: string | null }[] | undefined;
  rows: TagRowCheck[];
  /** The first error across the rows and the set, or null. */
  error: string | null;
}

// eslint-disable-next-line no-control-regex
const CONTROL = /[\x00-\x1f\x7f-\x9f]/;

export function checkTags(drafts: TagDraft[]): TagsCheck {
  const rows: TagRowCheck[] = [];
  const tags: { name: string; value: string | null }[] = [];
  const seen = new Set<string>();
  let error: string | null = null;
  for (const draft of drafts) {
    const name = draft.name.trim() === '' ? { value: null, error: 'A tag needs a name; remove the row to leave it out.' } : checkName(draft.name);
    let nameError = name.error;
    if (name.value !== null) {
      if (seen.has(name.value)) nameError = `Duplicate normalized tag name '${name.value}'.`;
      seen.add(name.value);
    }
    const valueError =
      draft.value.length > TAG_LIMITS.value
        ? `Value length ${draft.value.length} exceeds the ${TAG_LIMITS.value}-char limit.`
        : CONTROL.test(draft.value)
          ? 'Value must not contain control characters.'
          : null;
    rows.push({ nameError, valueError });
    error ??= nameError ?? valueError;
    if (nameError === null && valueError === null) tags.push({ name: name.value!, value: draft.value === '' ? null : draft.value });
  }
  if (drafts.length > TAG_LIMITS.count) error ??= `A job may carry at most ${TAG_LIMITS.count} tags.`;
  return { tags: tags.length === 0 ? undefined : tags, rows, error };
}
