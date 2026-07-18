// A tag filter input is free text: whitespace- or comma-separated tokens, each a bare tag name or
// `name:value`. Parsing keeps the raw tokens (the server validates and canonicalizes names); it only
// splits, trims, drops blanks, and dedupes so the same token isn't sent twice.
export function parseTagTokens(raw: string): string[] {
  const seen = new Set<string>();
  const tokens: string[] = [];
  for (const token of raw.split(/[\s,]+/)) {
    const trimmed = token.trim();
    if (trimmed && !seen.has(trimmed)) {
      seen.add(trimmed);
      tokens.push(trimmed);
    }
  }
  return tokens;
}
