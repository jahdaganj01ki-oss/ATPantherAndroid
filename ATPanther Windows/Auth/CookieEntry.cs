using System.Text;

namespace ATPanther.Auth;

/// <summary>
/// Nachbau von <c>okhttp3.Cookie</c> – nur die Attribute, die der MemoryCookieJar
/// auswertet (API-CONTRACT 1.3). Bewusst KEIN <c>CookieContainer</c>: dessen
/// Domain-/Path-Semantik weicht von OkHttp ab und würde still andere Cookies senden.
/// </summary>
public sealed class CookieEntry
{
    public string Name { get; }
    public string Value { get; }
    public string Domain { get; }
    public string Path { get; }
    public bool Secure { get; }
    public bool HostOnly { get; }

    private CookieEntry(string name, string value, string domain, string path, bool secure, bool hostOnly)
    {
        Name = name;
        Value = value;
        Domain = domain;
        Path = path;
        Secure = secure;
        HostOnly = hostOnly;
    }

    /// <summary>
    /// Manuell gesetztes Cookie, exakt wie AuthService.kt:151–159:
    /// Domain <c>login.alditalk-kundenbetreuung.de</c>, Path <c>/</c>, kein Secure-Flag.
    /// </summary>
    public static CookieEntry Manual(string name, string value, string domain, string path) =>
        new(name, value, domain, path, secure: false, hostOnly: false);

    /// <summary>Set-Cookie-Header parsen; null ⇒ Cookie wird verworfen (OkHttp-Verhalten).</summary>
    public static CookieEntry? Parse(Uri url, string header)
    {
        if (string.IsNullOrEmpty(header)) return null;

        int semi = header.IndexOf(';');
        string nameValue = semi < 0 ? header : header.Substring(0, semi);
        int eq = nameValue.IndexOf('=');
        if (eq < 0) return null;

        string name = nameValue.Substring(0, eq).Trim();
        string value = nameValue.Substring(eq + 1).Trim();
        if (name.Length == 0) return null;

        string domain = url.Host;
        bool hostOnly = true;
        string path = DefaultPath(url);
        bool secure = false;

        if (semi >= 0)
        {
            foreach (string raw in header.Substring(semi + 1).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string attr = raw.Trim();
                int ae = attr.IndexOf('=');
                string key = (ae < 0 ? attr : attr.Substring(0, ae)).Trim();
                string val = ae < 0 ? string.Empty : attr.Substring(ae + 1).Trim();

                if (key.Equals("Domain", StringComparison.OrdinalIgnoreCase))
                {
                    domain = val.TrimStart('.');
                    hostOnly = false;
                }
                else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
                {
                    // OkHttp ignoriert einen Path, der nicht mit '/' beginnt
                    if (val.Length > 0 && val[0] == '/') path = val;
                }
                else if (key.Equals("Secure", StringComparison.OrdinalIgnoreCase))
                {
                    secure = true;
                }
            }
        }

        if (domain.Length == 0) return null;
        if (!DomainMatches(url.Host, domain, hostOnly)) return null;

        return new CookieEntry(name, value, domain, path, secure, hostOnly);
    }

    /// <summary>okhttp3.Cookie.matches(url): Secure, Domain, Path.</summary>
    public bool Matches(Uri url)
    {
        if (Secure && !url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
        if (!DomainMatches(url.Host, Domain, HostOnly)) return false;
        return PathMatches(url.AbsolutePath, Path);
    }

    public override string ToString() => Name + "=" + Value;

    private static string DefaultPath(Uri url)
    {
        string p = url.AbsolutePath;
        if (p.Length == 0 || p[0] != '/') return "/";
        if (p == "/") return "/";
        int last = p.LastIndexOf('/');
        return last <= 0 ? "/" : p.Substring(0, last + 1);
    }

    private static bool DomainMatches(string host, string domain, bool hostOnly)
    {
        if (hostOnly) return host.Equals(domain, StringComparison.OrdinalIgnoreCase);
        if (host.Equals(domain, StringComparison.OrdinalIgnoreCase)) return true;
        return host.Length > domain.Length + 1 &&
               host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathMatches(string requestPath, string cookiePath)
    {
        if (requestPath.Length == 0) requestPath = "/";
        if (requestPath == cookiePath) return true;
        if (!requestPath.StartsWith(cookiePath, StringComparison.OrdinalIgnoreCase)) return false;
        if (cookiePath.EndsWith("/")) return true;
        return requestPath.Length > cookiePath.Length && requestPath[cookiePath.Length] == '/';
    }
}
