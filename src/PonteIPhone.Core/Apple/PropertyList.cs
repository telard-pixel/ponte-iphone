using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PonteIPhone.Core.Apple;

/// <summary>
/// Liste di proprietà di Apple (plist), il formato di tutti i messaggi con il servizio Apple e con l'iPhone.
/// Valori come oggetti .NET semplici: Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, long, double,
/// bool, byte[], DateTime. Si scrive sempre in XML; si legge XML e binario ("bplist00").
/// </summary>
public static class PropertyList
{
    private static readonly DateTime AppleEpoch = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ scrittura (XML)

    public static byte[] ToXml(object root)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        Write(sb, root);
        sb.Append("</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Write(StringBuilder sb, object value)
    {
        switch (value)
        {
            case IDictionary<string, object> dict:
                sb.Append("<dict>");
                foreach (var (k, v) in dict)
                {
                    sb.Append("<key>").Append(Escape(k)).Append("</key>");
                    Write(sb, v);
                }
                sb.Append("</dict>");
                break;
            case IEnumerable<object> list when value is not string:
                sb.Append("<array>");
                foreach (var v in list) Write(sb, v);
                sb.Append("</array>");
                break;
            case string s:
                sb.Append("<string>").Append(Escape(s)).Append("</string>");
                break;
            case bool b:
                sb.Append(b ? "<true/>" : "<false/>");
                break;
            case byte[] data:
                sb.Append("<data>").Append(Convert.ToBase64String(data)).Append("</data>");
                break;
            case int or long or uint or ulong or short or ushort:
                sb.Append("<integer>").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("</integer>");
                break;
            case double or float:
                sb.Append("<real>").Append(Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)).Append("</real>");
                break;
            case DateTime d:
                sb.Append("<date>").Append(d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</date>");
                break;
            default:
                throw new ArgumentException("Tipo non rappresentabile in un plist: " + value.GetType().Name);
        }
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ------------------------------------------------------------------ lettura

    /// <summary>Legge un plist XML o binario.</summary>
    public static object Parse(byte[] data)
    {
        if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == "bplist00") return ParseBinary(data);
        return ParseXml(data);
    }

    public static Dictionary<string, object> ParseDict(byte[] data) =>
        Parse(data) as Dictionary<string, object> ?? throw new FormatException("Il plist non è un dizionario");

    private static object ParseXml(byte[] data)
    {
        // Alcuni messaggi finiscono con zeri: si tolgono prima di leggere.
        int len = data.Length;
        while (len > 0 && data[len - 1] == 0) len--;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(data, 0, len), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new FormatException("plist vuoto");
        var first = root.Name.LocalName == "plist" ? root.Elements().FirstOrDefault() : root;
        return first is null ? new Dictionary<string, object>() : ReadXml(first);
    }

    private static object ReadXml(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "dict":
            {
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                string? key = null;
                foreach (var child in e.Elements())
                {
                    if (child.Name.LocalName == "key") key = child.Value;
                    else if (key is not null)
                    {
                        dict[key] = ReadXml(child);
                        key = null;
                    }
                }
                return dict;
            }
            case "array": return e.Elements().Select(ReadXml).ToList();
            case "string": return e.Value;
            case "integer":
                return long.TryParse(e.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
                    : (long)ulong.Parse(e.Value.Trim(), CultureInfo.InvariantCulture);
            case "real": return double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture);
            case "true": return true;
            case "false": return false;
            case "data": return Convert.FromBase64String(string.Concat(e.Value.Where(c => !char.IsWhiteSpace(c))));
            case "date": return DateTime.Parse(e.Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            default: return e.Value;
        }
    }

    private static object ParseBinary(byte[] d)
    {
        if (d.Length < 40) throw new FormatException("plist binario troppo corto");
        var trailer = d.AsSpan(d.Length - 32);
        int offsetSize = trailer[6];
        int refSize = trailer[7];
        long count = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        long top = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        long tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
        if (offsetSize is < 1 or > 8 || refSize is < 1 or > 8 || count <= 0 || count > 1_000_000 || top >= count)
            throw new FormatException("plist binario non valido");

        long Offset(long i) => (long)ReadUInt(d, (int)(tableOffset + i * offsetSize), offsetSize);
        var cache = new Dictionary<long, object>();
        var visiting = new HashSet<long>();

        object Read(long index)
        {
            if (cache.TryGetValue(index, out var done)) return done;
            if (index < 0 || index >= count || !visiting.Add(index)) throw new FormatException("riferimento non valido nel plist");
            int p = (int)Offset(index);
            byte marker = d[p];
            int hi = marker >> 4, lo = marker & 0xF;
            object result;

            int Length(ref int pos)
            {
                if (lo != 0xF) return lo;
                byte m = d[pos];
                int bytes = 1 << (m & 0xF);
                long n = (long)ReadUInt(d, pos + 1, bytes);
                pos += 1 + bytes;
                return checked((int)n);
            }

            switch (hi)
            {
                case 0x0:
                    result = lo switch { 0x8 => false, 0x9 => true, _ => "" };
                    break;
                case 0x1:
                {
                    int bytes = 1 << lo;
                    result = bytes == 8 ? (long)ReadUInt(d, p + 1, 8) : bytes > 8 ? (long)ReadUInt(d, p + 1 + bytes - 8, 8) : (long)ReadUInt(d, p + 1, bytes);
                    break;
                }
                case 0x2:
                    result = lo == 2 ? BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(p + 1)) : BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(p + 1));
                    break;
                case 0x3:
                    result = AppleEpoch.AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(p + 1)));
                    break;
                case 0x4:
                {
                    int pos = p + 1;
                    int n = Length(ref pos);
                    result = d.AsSpan(pos, n).ToArray();
                    break;
                }
                case 0x5:
                {
                    int pos = p + 1;
                    int n = Length(ref pos);
                    result = Encoding.ASCII.GetString(d, pos, n);
                    break;
                }
                case 0x6:
                {
                    int pos = p + 1;
                    int n = Length(ref pos);
                    result = Encoding.BigEndianUnicode.GetString(d, pos, n * 2);
                    break;
                }
                case 0x8:
                    result = (long)ReadUInt(d, p + 1, lo + 1);
                    break;
                case 0xA:
                {
                    int pos = p + 1;
                    int n = Length(ref pos);
                    var list = new List<object>(n);
                    for (int i = 0; i < n; i++) list.Add(Read((long)ReadUInt(d, pos + i * refSize, refSize)));
                    result = list;
                    break;
                }
                case 0xD:
                {
                    int pos = p + 1;
                    int n = Length(ref pos);
                    var dict = new Dictionary<string, object>(n, StringComparer.Ordinal);
                    for (int i = 0; i < n; i++)
                    {
                        var k = Read((long)ReadUInt(d, pos + i * refSize, refSize));
                        var v = Read((long)ReadUInt(d, pos + (n + i) * refSize, refSize));
                        dict[k as string ?? Convert.ToString(k, CultureInfo.InvariantCulture) ?? ""] = v;
                    }
                    result = dict;
                    break;
                }
                default:
                    throw new FormatException($"tipo 0x{marker:X2} non gestito nel plist");
            }
            visiting.Remove(index);
            cache[index] = result;
            return result;
        }

        return Read(top);
    }

    private static ulong ReadUInt(byte[] d, int pos, int bytes)
    {
        ulong v = 0;
        for (int i = 0; i < bytes; i++) v = (v << 8) | d[pos + i];
        return v;
    }

    // ------------------------------------------------------------------ comodità

    public static string? GetString(this IReadOnlyDictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;

    public static long? GetLong(this IReadOnlyDictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) ? v switch { long l => l, int i => i, double x => (long)x, string s when long.TryParse(s, out var p) => p, _ => null } : null;

    public static bool GetBool(this IReadOnlyDictionary<string, object> d, string key) => d.TryGetValue(key, out var v) && v is true;

    public static byte[]? GetData(this IReadOnlyDictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) ? v switch { byte[] b => b, string s => Encoding.ASCII.GetBytes(s), _ => null } : null;

    public static Dictionary<string, object>? GetDict(this IReadOnlyDictionary<string, object> d, string key) =>
        d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
}
