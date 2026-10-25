// Payload JSON travels as text end to end: the read API returns it as a string, the editors keep the
// operator's text, and a write splices that text into the request body. JSON.parse is used only to
// validate, never to rebuild a value, so a number past 2^53 or a long decimal reaches the server exactly
// as typed. Pure functions, so node --test exercises them without the Svelte runtime.

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
