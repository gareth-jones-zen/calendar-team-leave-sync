using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PyCompat;

namespace TeamLeave;

/// <summary>A ValueError, as the Python version raised for settings that can't be used (exit code 2).</summary>
public class PyValueError(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A KeyError from str.format: a template names a field it wasn't given. Its text is Python's str(KeyError).</summary>
public sealed class PyKeyError(string key) : Exception(Py.ReprString(key));

/// <summary>The bits of Python's behaviour this tool depends on that PyCompat doesn't hold.</summary>
public static class PyBits
{
    /// <summary>list(text): the code points, each as a string.</summary>
    public static List<string> Characters(string text)
    {
        var list = new List<string>();
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                list.Add(text.Substring(i, 2));
                i++;
            }
            else
                list.Add(text[i].ToString());
        }
        return list;
    }

    /// <summary>urllib.parse.quote(text) with its default safe="/": letters, digits, "_.-~" and "/" as they are, the
    /// rest as %XX of the UTF-8 bytes.</summary>
    public static string Quote(string text)
    {
        var b = new StringBuilder();
        foreach (var by in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)by;
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' or '/')
                b.Append(c);
            else
                b.Append('%').Append(by.ToString("X2", CultureInfo.InvariantCulture));
        }
        return b.ToString();
    }

    /// <summary>template.format(**fields), for the plain "{field}", "{{" and "}}" the settings use. Anything else
    /// (a conversion, a format spec, a positional field) is refused, where Python would format it.</summary>
    public static string Format(string template, params (string Name, string Value)[] fields)
    {
        var b = new StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    b.Append('{');
                    i++;
                    continue;
                }
                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                    throw new PyValueError("Single '{' encountered in format string");
                var name = template[(i + 1)..close];
                if (name.Length == 0 || name.Contains('{') || name.Contains('!') || name.Contains(':') || name.Contains('.') || name.Contains('['))
                    throw new PyValueError($"The title template {Py.ReprString(template)} uses a format the .NET version doesn't support.");
                var found = fields.FirstOrDefault(f => f.Name == name);
                if (found.Name is null)
                    throw new PyKeyError(name);
                b.Append(found.Value);
                i = close;
            }
            else if (c == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    b.Append('}');
                    i++;
                    continue;
                }
                throw new PyValueError("Single '}' encountered in format string");
            }
            else
                b.Append(c);
        }
        return b.ToString();
    }

    /// <summary>What a value is as a Python dict key from json.loads: equal keys are the same key, so 1, 1.0 and True
    /// are one key, as in Python. Null for null (Python's None, a key like any other).</summary>
    public static string Key(JsonNode? node)
    {
        if (node is null)
            return "N";
        if (node is JsonValue v)
        {
            switch (v.GetValueKind())
            {
                case JsonValueKind.String:
                    return "S" + v.GetValue<string>();
                case JsonValueKind.True:
                    return "F1";
                case JsonValueKind.False:
                    return "F0";
                case JsonValueKind.Number:
                    var text = Py.Str(v);
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? "F" + d.ToString("R", CultureInfo.InvariantCulture)
                        : "F" + text;
            }
        }
        return "J" + node.ToJsonString();   // unhashable in Python; never matches anything else
    }

    /// <summary>repr() of a value from json.loads (text quoted, numbers and None as Python prints them).</summary>
    public static string Repr(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String
        ? Py.ReprString(v.GetValue<string>())
        : Py.Str(node);

    /// <summary>date.fromisoformat: "YYYY-MM-DD", "YYYYMMDD", or an ISO week date. Throws FormatException (Python's
    /// ValueError) for anything else.</summary>
    public static DateOnly DateFromIso(string text)
    {
        if (text.Length is not (7 or 8 or 10))
            throw new FormatException($"Invalid isoformat string: {Py.ReprString(text)}");
        var parsed = PyTime.FromIso(text);
        if (parsed.TimeOfDay != TimeSpan.Zero)
            throw new FormatException($"Invalid isoformat string: {Py.ReprString(text)}");
        return DateOnly.FromDateTime(parsed);
    }

    /// <summary>pathlib's with_suffix(".tmp"): the last suffix replaced (or added).</summary>
    public static string WithSuffix(string path, string suffix)
    {
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 && dot < name.Length - 1 ? name[..dot] : name;
        return Path.Combine(Path.GetDirectoryName(path) ?? "", stem + suffix);
    }

    /// <summary>type(error).__name__, as the Python version would have named the same failure.</summary>
    public static string TypeName(Exception error) => error switch
    {
        LeaveError => "LeaveError",
        SafetyStop => "SafetyStop",
        M365Graph.SignInNeededException or PeopleFirstClient.SignInNeededException => "SignInNeeded",
        PeopleFirstClient.PeopleFirstException => "PeopleFirstError",
        M365Graph.GraphRequestException => "HTTPError",
        TimeoutException => "TimeoutError",
        PyKeyError => "KeyError",
        PyValueError or FormatException => "ValueError",
        _ => error.GetType().Name,
    };

    /// <summary>str(error), as the Python version would have put the same failure.</summary>
    public static string Text(Exception error) => error switch
    {
        M365Graph.GraphRequestException graph => $"HTTP Error {(int)graph.Status}: {Reason(graph.Status)}",
        _ => error.Message,
    };

    private static string Reason(HttpStatusCode status) => (int)status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        412 => "Precondition Failed",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => status.ToString(),
    };
}
