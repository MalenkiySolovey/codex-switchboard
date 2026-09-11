using System.Globalization;
using System.Text.Json;

namespace CodexSwitcher.Core.Catalog;

/// <summary>
/// Deterministic RFC 6901 JSON pointer extractor with safe numeric transforms.
/// Non-Turing-complete: does not evaluate expressions or execute code.
/// </summary>
public static class JsonPointerExtractor
{
    /// <summary>
    /// Navigates a JSON document using an RFC 6901 pointer.
    /// </summary>
    public static bool TryResolvePointer(JsonElement root, string pointer, out JsonElement target)
    {
        target = default;
        if (string.IsNullOrEmpty(pointer))
        {
            target = root;
            return true;
        }

        if (!pointer.StartsWith('/'))
            return false;

        var current = root;
        var tokens = pointer[1..].Split('/');

        foreach (var rawToken in tokens)
        {
            var token = UnescapePointerToken(rawToken);

            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    if (current.TryGetProperty(token, out var prop))
                    {
                        current = prop;
                    }
                    else
                    {
                        // Case-insensitive fallback for minor schema variations
                        var found = false;
                        foreach (var p in current.EnumerateObject())
                        {
                            if (string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase))
                            {
                                current = p.Value;
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                            return false;
                    }
                    break;

                case JsonValueKind.Array:
                    if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                        return false;

                    if (index < 0 || index >= current.GetArrayLength())
                        return false;

                    current = current[index];
                    break;

                default:
                    return false;
            }
        }

        target = current;
        return true;
    }

    /// <summary>
    /// Extracts a decimal value and applies safe configured transforms (e.g. scale, centsToUnits).
    /// </summary>
    public static bool TryExtractDecimal(
        JsonElement root,
        JsonFieldMapping mapping,
        out decimal value)
    {
        value = 0m;
        if (mapping == null || string.IsNullOrWhiteSpace(mapping.Pointer))
            return false;

        if (!TryResolvePointer(root, mapping.Pointer, out var element))
            return false;

        if (!TryGetDecimalValue(element, out var rawDecimal))
            return false;

        if (!string.IsNullOrWhiteSpace(mapping.Transform))
        {
            if (string.Equals(mapping.Transform, "scale", StringComparison.OrdinalIgnoreCase))
            {
                var factor = mapping.ScaleFactor ?? 1m;
                rawDecimal *= factor;
            }
            else if (string.Equals(mapping.Transform, "centsToUnits", StringComparison.OrdinalIgnoreCase))
            {
                rawDecimal /= 100m;
            }
        }

        value = rawDecimal;
        return true;
    }

    /// <summary>
    /// Extracts a string value from a JSON pointer.
    /// </summary>
    public static bool TryExtractString(
        JsonElement root,
        string pointer,
        out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(pointer))
            return false;

        if (!TryResolvePointer(root, pointer, out var element))
            return false;

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return true;
        }

        if (element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetRawText();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Extracts a list of string identifiers from an array at the given JSON pointer.
    /// Supports array of strings or array of objects with an 'id' property.
    /// </summary>
    public static bool TryExtractStringArray(
        JsonElement root,
        string pointer,
        out List<string> items)
    {
        items = new List<string>();
        if (!TryResolvePointer(root, pointer, out var element) || element.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    items.Add(s);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    var id = idProp.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                        items.Add(id);
                }
            }
        }

        return items.Count > 0;
    }

    private static bool TryGetDecimalValue(JsonElement element, out decimal result)
    {
        result = 0m;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDecimal(out result);

            case JsonValueKind.String:
                var text = element.GetString();
                return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result);

            default:
                return false;
        }
    }

    private static string UnescapePointerToken(string token) =>
        token.Replace("~1", "/").Replace("~0", "~");
}
