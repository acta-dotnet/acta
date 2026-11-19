// Payload JSON travels as text end to end: a read keeps a payload's body as its text, the editors keep the
// operator's text, and a write splices that text into the request body. JSON.parse is used only to validate,
// never to rebuild a value, so a number past 2^53 or a long decimal reaches the server exactly as typed. Pure
// functions, so node --test exercises them without the Svelte runtime.

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

/**
 * Valid JSON text re-indented by two spaces, as JSON.stringify(value, null, 2) would print it, without parsing a number:
 * a \uXXXX escape it would print as the character itself (the server's encoder escapes non-ASCII) is shown decoded.
 */
export function prettyJson(text: string): string {
  let out = '';
  let depth = 0;
  let inString = false;
  let escaped = false;
  const newline = () => '\n' + '  '.repeat(depth);

  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inString) {
      const decoded = !escaped && c === '\\' ? decodedEscape(text, i) : null;
      if (decoded) {
        out += decoded.char;
        i += decoded.length - 1;
        continue;
      }
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

// The character a \uXXXX escape at `at` stands for when JSON.stringify prints it unescaped: not a control character,
// quote, backslash or lone surrogate. A surrogate pair decodes as one character from both escapes.
function decodedEscape(text: string, at: number): { char: string; length: number } | null {
  const unit = escapedUnit(text, at);
  if (unit === null || unit < 0x20 || unit === 0x22 || unit === 0x5c || (unit >= 0xdc00 && unit <= 0xdfff)) return null;
  if (unit < 0xd800 || unit > 0xdbff) return { char: String.fromCharCode(unit), length: 6 };
  const low = escapedUnit(text, at + 6);
  return low !== null && low >= 0xdc00 && low <= 0xdfff ? { char: String.fromCharCode(unit, low), length: 12 } : null;
}

function escapedUnit(text: string, at: number): number | null {
  const hex = text.slice(at + 2, at + 6);
  return text[at] === '\\' && text[at + 1] === 'u' && /^[0-9a-fA-F]{4}$/.test(hex) ? parseInt(hex, 16) : null;
}

/** A json payload's envelope for a request: the text spliced in as the envelope's json member, every digit kept. */
export function jsonPayload(text: string): RawJson {
  return new RawJson(`{"json":${text}}`);
}

