# Android → .NET 8 WinForms: Abbildungen mit Fallstricken

Zielplattform: `net8.0-windows`, WinForms, Tray-Unterstützung. Kein lokales SDK nötig — die Dateien
entstehen von Hand, gebaut wird in GitHub Actions.

## Projektdateien von Hand

`<Projekt>.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>AT Panther</AssemblyName>
    <ApplicationIcon>assets\app.ico</ApplicationIcon>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
```

`Program.cs` mit `ApplicationConfiguration.Initialize()` und `Application.Run(...)`.

## Netzwerk

| Android | Windows | Fallstrick |
| --- | --- | --- |
| `OkHttpClient` | `HttpClient` mit `SocketsHttpHandler` | .NET-Timeout-Default ist 100 s, Android oft 30 s |
| `connectTimeout`/`readTimeout` | `handler.ConnectTimeout` / `HttpClient.Timeout` | `HttpClient.Timeout` deckt die Gesamtdauer ab, nicht nur Read |
| `followRedirects(false)` | `handler.AllowAutoRedirect = false` | Für Auth-Ketten abschalten, für API-Aufrufe getrennten Client nutzen |
| `MemoryCookieJar` | `CookieContainer` **oder** eigene Jar-Klasse | Manuell gesetzte Cookies brauchen exakte Domain **und** Path |
| `Request.Builder.header()` | `HttpRequestMessage.Headers.Add()` | Header mit `Content-`-Präfix gehören in `Content.Headers` |
| `toRequestBody("application/json")` | `StringContent(body, Encoding.UTF8, "application/json")` | Leerer Body: `new StringContent("", Encoding.UTF8, "application/json")` = 0 Bytes |
| `JSONObject.optString(k, d)` | `JsonDocument` + `TryGetProperty` | `optString` liefert `""` statt Exception — nachbilden |
| `org.json` Schlüsselausgabe | `JsonSerializer` / manuell gebautes `JsonObject` | Schlüsselreihenfolge kann sich ändern; bei Signaturen kritisch |

Zwei Clients wie im Original: ein Auth-Client ohne Auto-Redirect und ein API-Client mit.
Nicht einen "besseren" einheitlichen Client bauen.

## Kryptografie

```csharp
// SHA-1 Hex, Kleinbuchstaben – wie Java getBytes() -> Hex-Ausgabe
static string Sha1Hex(string input) =>
    Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

// SHA-256 Base64Url ohne Padding (PKCE code_challenge)
static string Base64UrlSha256(string verifier) =>
    Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
```

Beim Original prüfen, ob `sha1()` Klein- oder Großbuchstaben liefert und ob UTF-8 oder ein anderer
Charset verwendet wird. Abweichung führt dazu, dass ein PoW-Nonce nie akzeptiert wird.

## Hintergrund und Lebenszyklus

| Android | Windows |
| --- | --- |
| `Service` / Foreground-Service | `NotifyIcon` + `System.Windows.Forms.Timer` oder eigener Worker-Task |
| `AlarmManager` / `WakeReceiver` | Timer-Periode; Rechner-Sleep behandeln (`PowerModeChanged`) |
| Persistent Notification / Kanal | `NotifyIcon.Text` + `BalloonTip`; Alarmzustand als Icon-Variante |
| App wird vom System gekillt | Autostart-Option (`Run`-Key) anbieten, wenn Original "läuft weiter" garantiert |

Zählerlogik (Fehlerzähler, Pausen, Limits) ist **Verhalten, nicht Implementierung** — 1:1 übernehmen,
inklusive welcher Ereignisse zählen (Ausnahmen zählen oft mit).

## Datenhaltung

| Android | Windows |
| --- | --- |
| Room (`@Dao`, `@Database`) | SQLite (eigene Tabellenanlage) oder JSON-/CSV-Datei |
| `SharedPreferences` | JSON-Datei in `%APPDATA%\<App>\settings.json` |
| Keystore / verschlüsselte Secrets | `ProtectedData` (DPAPI, CurrentUser) |

DPAPI ist benutzergebunden: ein Export der Datei auf einen anderen Rechner entschlüsselt nicht. Das
in `PARITY.md` dokumentieren.

## UI

Compose-Elemente eins zu eins als WinForms-Controls nachbauen, mit gleichem Verhalten pro Zustand
(aktiv, pausiert, Fehler, Verlaufsliste). Designpräferenz monochrom:

```csharp
// dunkles, monochromes Schema
var bg = Color.FromArgb(16, 16, 16);
var fg = Color.Gainsboro;
var accent = Color.White;
```

Reihenfolge und Beschriftung von Buttons, Feldern und Log-Einträgen nicht "verbessern" —
Gleichheit vor Schönheit, sonst ist die Parität nicht mehr nachprüfbar.

## Threading

Coroutines → `async`/`await`. `Dispatchers.IO` → Task.Run bzw. HttpClient-asynchron.
Niemals `.Result`/`.Wait()` im UI-Thread (Deadlock). UI-Updates nur über den UI-Thread
(`Invoke`/`BeginInvoke`).
