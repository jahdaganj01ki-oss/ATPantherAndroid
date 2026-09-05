using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ATPanther.Auth;

/// <summary>
/// org.json-Nachbau für System.Text.Json: <c>optString</c>/<c>getString</c>/
/// <c>optLong</c>/<c>optBoolean</c> inklusive der Typumwandlungen, die Android
/// mitmacht (API-CONTRACT 3.2–3.4). Fehlende Felder liefern Defaults statt
/// Exceptions – nur <c>getString</c>/<c>getJSONArray</c> werfen.
/// </summary>
public static class JsonText
{
    public static JsonNode? Get(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? value) ? value : null;

    /// <summary>Wert als Text; Zahlen/Booleans werden textlich konvertiert wie bei org.json.</summary>
    public static string AsText(JsonNode? node, string fallback)
    {
        if (node == null) return fallback;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out string? s)) return s;
            return value.ToJsonString();
        }
        return node.ToJsonString();
    }

    /// <summary>JSONObject.optString(key) ⇒ "" bei fehlendem Feld.</summary>
    public static string OptString(JsonObject obj, string key) => AsText(Get(obj, key), string.Empty);

    /// <summary>JSONObject.optString(key, default).</summary>
    public static string OptString(JsonObject obj, string key, string fallback) => AsText(Get(obj, key), fallback);

    /// <summary>JSONObject.getString(key) ⇒ Exception, wenn der Schlüssel fehlt.</summary>
    public static string RequireString(JsonObject obj, string key)
    {
        JsonNode? node = Get(obj, key);
        if (node == null) throw new JsonException($"Value of type null for key {key}");
        return AsText(node, string.Empty);
    }

    /// <summary>JSONObject.optLong(key, fallback).</summary>
    public static long OptLong(JsonObject obj, string key, long fallback)
    {
        string text = OptString(obj, key, string.Empty);
        return long.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : fallback;
    }

    /// <summary>JSONObject.optBoolean(key, fallback) – akzeptiert true/false als Bool oder String.</summary>
    public static bool OptBoolean(JsonObject obj, string key, bool fallback)
    {
        JsonNode? node = Get(obj, key);
        if (node is not JsonValue value) return fallback;
        if (value.TryGetValue<bool>(out bool flag)) return flag;
        if (value.TryGetValue<string>(out string? text))
        {
            if (text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return fallback;
    }

    /// <summary>JSONObject.getJSONArray(key) ⇒ Exception, wenn kein Array.</summary>
    public static JsonArray RequireArray(JsonObject obj, string key)
    {
        JsonNode? node = Get(obj, key);
        if (node is JsonArray array) return array;
        throw new JsonException($"No value for {key}");
    }

    /// <summary>JSONObject.optJSONObject(key) ⇒ null, wenn fehlt oder kein Objekt.</summary>
    public static JsonObject? OptObject(JsonObject obj, string key) => Get(obj, key) as JsonObject;

    public static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value.Substring(0, maxChars);
}
