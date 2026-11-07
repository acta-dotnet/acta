// Payload JSON travels as text end to end: a read keeps a payload envelope's embedded value as its text,
// the editors keep the operator's text, and a write splices that text into the request body. JSON.parse is
// used only to validate, never to rebuild a value, so a number past 2^53 or a long decimal reaches the
// server exactly as typed. Pure functions, so node --test exercises them without the Svelte runtime.

/** JSON text a request body carries verbatim, in place of a value JSON.stringify would rebuild. */
export class RawJson {
  readonly text: string;

  constructor(text: string) {
    this.text = text;
  }
}

/** The request body: JSON.stringify, except that a RawJson body or top-level member is spliced in as its text. */
export function stringifyBody(body: unknown): string {
  if (body instanceof RawJson) return body.text;
  if (body === null || typeof body !== 'object' || Array.isArray(body)) return JSON.stringify(body);

  const entries = Object.entries(body);
  const raw = entries.filter(([, value]) => value instanceof RawJson);
  if (raw.length === 0) return JSON.stringify(body);

  const rest = JSON.stringify(Object.fromEntries(entries.filter(([, value]) => !(value instanceof RawJson))));
  const spliced = raw.map(([key, value]) => `${JSON.stringify(key)}:${(value as RawJson).text}`).join(',');
  return rest === '{}' ? `{${spliced}}` : `${rest.slice(0, -1)},${spliced}}`;
}

/** The parse error for text that is not one JSON value, or null when it is. */
export function jsonError(text: string): string | null {
  try {
    JSON.parse(text);
    return null;
  } catch (error) {
    return error instanceof Error ? error.message : 'Invalid JSON.';
  }
}

/** Valid JSON text re-indented by two spaces, as JSON.stringify(value, null, 2) would print it, without parsing a number. */
export function prettyJson(text: string): string {
  let out = '';
  let depth = 0;
  let inString = false;
  let escaped = false;
  const newline = () => '\n' + '  '.repeat(depth);

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      out += c;
      if (escaped) escaped = false;
      else if (c === '\\') escaped = true;
      else if (c === '"') inString = false;
      continue;
    }

    switch (c) {
      case '"':
        inString = true;
        out += c;
        break;
      case '{':
      case '[': {
        const close = c === '{' ? '}' : ']';
        let next = i + 1;
        while (next < text.length && /\s/.test(text[next])) next++;
        if (text[next] === close) {
          out += c + close;
          i = next;
        } else {
          depth++;
          out += c + newline();
        }
        break;
      }
      case '}':
      case ']':
        depth--;
        out += newline() + c;
        break;
      case ',':
        out += ',' + newline();
        break;
      case ':':
        out += ': ';
        break;
      default:
        if (!/\s/.test(c)) out += c;
    }
  }
  return out;
}

/** A json payload's envelope for a request: the text spliced in as the envelope's json member, every digit kept. */
export function jsonPayload(text: string): RawJson {
  return new RawJson(`{"json":${text}}`);
}

/**
 * Response text in which every `json` member's value is rewritten as a JSON string of its exact text, so JSON.parse
 * hands a payload envelope's embedded value over as text and never rebuilds a number. A `json` key nested inside such
 * a value is part of that text.
 */
export function quotePayloadJson(text: string): string {
  let out = '';
  let copied = 0;
  let quote = text.indexOf('"');
  while (quote >= 0) {
    const end = stringEnd(text, quote);
    let next = end;
    if (end - quote === 6 && text.startsWith('"json"', quote)) {
      next = skipSpace(text, end);
      // A string followed by a colon is a member's key; any other "json" string is a value and stays.
      if (text[next] === ':') {
        const start = skipSpace(text, next + 1);
        const valueEnd = jsonValueEnd(text, start);
        out += text.slice(copied, start) + JSON.stringify(text.slice(start, valueEnd));
        copied = valueEnd;
        next = valueEnd;
      }
    }
    quote = text.indexOf('"', next);
  }
  return copied === 0 ? text : out + text.slice(copied);
}

function skipSpace(text: string, at: number): number {
  while (at < text.length && /\s/.test(text[at])) at++;
  return at;
}

// The index just past the string token that opens at `start`.
function stringEnd(text: string, start: number): number {
  for (let i = start + 1; i < text.length; i++) {
    if (text[i] === '\\') i++;
    else if (text[i] === '"') return i + 1;
  }
  return text.length;
}

// The index just past the JSON value that starts at `start`.
function jsonValueEnd(text: string, start: number): number {
  const first = text[start];
  if (first === '"') return stringEnd(text, start);
  if (first !== '{' && first !== '[') {
    let i = start;
    while (i < text.length && !/[\s,}\]]/.test(text[i])) i++;
    return i;
  }
  let depth = 0;
  for (let i = start; i < text.length; i++) {
    const c = text[i];
    if (c === '"') i = stringEnd(text, i) - 1;
    else if (c === '{' || c === '[') depth++;
    else if ((c === '}' || c === ']') && --depth === 0) return i + 1;
  }
  return text.length;
}
