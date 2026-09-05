namespace ATPanther.Auth;

/// <summary>
/// In-Memory-CookieJar, funktionsgleich <c>util/OkHttpCookieJar.kt:15–31</c>:
/// Ablage pro Request-Host, Ersetzen gleicher Namen, Zustellung nach
/// <see cref="CookieEntry.Matches"/>, kein Ablauf, kein Purging.
/// Thread-sicher (OkHttp ruft den Jar parallel auf – daher dort ConcurrentHashMap +
/// synchronized, hier ein Gate pro Liste).
/// </summary>
public sealed class MemoryCookieJar
{
    private readonly object gate = new();
    private readonly Dictionary<string, List<CookieEntry>> store =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set-Cookie-Header einer Antwort übernehmen (OkHttp-Bridge-Verhalten).</summary>
    public void SaveFromResponse(Uri url, IEnumerable<string> setCookieHeaders)
    {
        var parsed = new List<CookieEntry>();
        foreach (string header in setCookieHeaders)
        {
            CookieEntry? cookie = CookieEntry.Parse(url, header);
            if (cookie != null) parsed.Add(cookie);
        }
        if (parsed.Count == 0) return;
        SaveFromResponse(url, parsed);
    }

    public void SaveFromResponse(Uri url, IReadOnlyList<CookieEntry> cookies)
    {
        if (cookies.Count == 0) return;
        lock (gate)
        {
            if (!store.TryGetValue(url.Host, out List<CookieEntry>? existing))
            {
                existing = new List<CookieEntry>();
                store[url.Host] = existing;
            }
            lock (existing)
            {
                existing.RemoveAll(c => cookies.Any(n => n.Name == c.Name));
                existing.AddRange(cookies);
            }
        }
    }

    public IReadOnlyList<CookieEntry> LoadForRequest(Uri url)
    {
        lock (gate)
        {
            if (!store.TryGetValue(url.Host, out List<CookieEntry>? existing))
                return Array.Empty<CookieEntry>();
            lock (existing)
            {
                var matches = new List<CookieEntry>();
                foreach (CookieEntry c in existing)
                    if (c.Matches(url)) matches.Add(c);
                return matches;
            }
        }
    }

    /// <summary>Cookie-Headerwert für diese URL oder null (dann kein Header).</summary>
    public string? CookieHeaderFor(Uri url)
    {
        IReadOnlyList<CookieEntry> cookies = LoadForRequest(url);
        if (cookies.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < cookies.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            sb.Append(cookies[i].Name).Append('=').Append(cookies[i].Value);
        }
        return sb.ToString();
    }

    public void Clear()
    {
        lock (gate) store.Clear();
    }
}
