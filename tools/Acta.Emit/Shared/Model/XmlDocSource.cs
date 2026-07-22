using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Acta.Emit.Shared.Model;

/// <summary>
/// Loads the compiler-emitted XML doc file for an assembly and exposes per-type / per-property
/// summary lookups.
/// </summary>
internal sealed class XmlDocSource
{
    private readonly Dictionary<string, string> _summaries = new(StringComparer.Ordinal);

    public static XmlDocSource ForAssembly(Assembly asm)
    {
        var dllPath = asm.Location;
        var xmlPath = Path.ChangeExtension(dllPath, ".xml");
        var src = new XmlDocSource();
        if (!File.Exists(xmlPath))
        {
            return src; // graceful fallback: no XML, no summaries
        }

        var doc = XDocument.Load(xmlPath);
        foreach (var member in doc.Root?.Element("members")?.Elements("member") ?? [])
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null)
            {
                continue;
            }

            src._summaries[name] = NormalizeSummary(RenderXmlContent(summary));
        }
        return src;
    }

    // Renders inline XML to Markdown: XElement.Value alone strips empty elements like <see/>.
    private static string RenderXmlContent(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText txt:
                    sb.Append(txt.Value);
                    break;
                case XElement el when el.Name.LocalName == "c":
                    sb.Append('`').Append(el.Value).Append('`');
                    break;
                case XElement el when el.Name.LocalName == "see":
                {
                    var cref = el.Attribute("cref")?.Value;
                    var langw = el.Attribute("langword")?.Value;
                    var href = el.Attribute("href")?.Value;
                    var inner = el.Value;
                    var label =
                        !string.IsNullOrEmpty(inner) ? inner
                        : cref is not null ? ShortenCref(cref)
                        : langw ?? href ?? "";
                    sb.Append('`').Append(label).Append('`');
                    break;
                }
                case XElement el when el.Name.LocalName == "paramref" || el.Name.LocalName == "typeparamref":
                    sb.Append('`').Append(el.Attribute("name")?.Value ?? el.Value).Append('`');
                    break;
                case XElement el when el.Name.LocalName == "para":
                    sb.Append(' ').Append(RenderXmlContent(el)).Append(' ');
                    break;
                case XElement el:
                    sb.Append(RenderXmlContent(el));
                    break;
            }
        }
        return sb.ToString();
    }

    // T:Acta.JobNamespace -> JobNamespace; M:..PauseAsync(...) -> PauseAsync; !:Foo -> Foo
    private static string ShortenCref(string cref)
    {
        var withoutPrefix = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = withoutPrefix.IndexOf('(');
        var trimmed = paren > 0 ? withoutPrefix[..paren] : withoutPrefix;
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot > 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }

    public string? ForType(Type t) => Lookup($"T:{TypeKey(t)}");

    public string? ForProperty(PropertyInfo p) => Lookup($"P:{TypeKey(p.DeclaringType!)}.{p.Name}");

    public string? ForField(FieldInfo f) => Lookup($"F:{TypeKey(f.DeclaringType!)}.{f.Name}");

    private string? Lookup(string key) => _summaries.TryGetValue(key, out var v) ? v : null;

    // CLR nested-type separator '+' becomes '.' in XML doc keys.
    private static string TypeKey(Type t) => t.FullName!.Replace('+', '.');

    private static string NormalizeSummary(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        var seenSpace = true;
        foreach (var ch in raw)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                if (!seenSpace)
                {
                    sb.Append(' ');
                    seenSpace = true;
                }
            }
            else if (ch == ' ')
            {
                if (!seenSpace)
                {
                    sb.Append(' ');
                    seenSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                seenSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
