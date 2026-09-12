// Copyright (c) 2024 AT Panther contributors.
//
// AT Panther is a utility that monitors ALDI Talk data volume and automatically books
// 1 GB when the remaining data drops below a configurable threshold. This file lists the
// source files that must be mapped before porting, so the Windows build is faithful to the
// Android implementation.

using System.Collections.Immutable;

return ImmutableArray.Create(new SourceMapping
{
    Id = "at-panther",
    Title = "AT Panther Android → Windows port",
    RootNotes = new[]
    {
        "Backend is ALDI Talk portal, not a self-hosted API.",
        "Auth uses ForgeRock-style callbacks: PoW solve → credential submit → OAuth2 authorize → manual redirect chain.",
        "Session cookie is iPlanetDirectoryPro on login.alditalk-kundenbetreuung.de.",
        "Mobile top-up books 1 GB; remaining data is read from the account/usage endpoint after login.",
        "Freeze fixes that must survive the port: bounded log UI, bounded DB/log store, incremental log UI refresh, safe cookie/session store, bounded retry loop, explicit pause after repeated failures.",
        "Pause rules: stop after 3 consecutive connection/login failures (or 5 successful relogs without a successful usage read). Resume only after explicit user action."
    },
    Directories = new[]
    {
        new SourceDirectory
        {
            Path = "app/src/main/java/com/alditalk/panther/auth",
            Purpose = "ForgeRock login flow, PoW solving, OAuth2 authorize, redirect-chain resolution.",
            KeyFiles = new[] { "AuthService.kt" }
        },
        new SourceDirectory
        {
            Path = "app/src/main/java/com/alditalk/panther/service",
            Purpose = "Background monitoring loop, login/relogin, top-up trigger, pause logic, notifications/foreground behavior.",
            KeyFiles = new[] { "MonitorService.kt", "MonitorWakeReceiver.kt" }
        },
        new SourceDirectory
        {
            Path = "app/src/main/java/com/alditalk/panther/data",
            Purpose = "LogEntry model, Room DAO/Database, bounded history trimming.",
            KeyFiles = new[] { "LogEntry.kt", "LogDao.kt", "AppDatabase.kt" }
        },
        new SourceDirectory
        {
            Path = "app/src/main/java/com/alditalk/panther/util",
            Purpose = "Crypto helpers (SHA-1/SHA-256, base64url, PKCE, cookie jar).",
            KeyFiles = new[] { "CryptoExtensions.kt", "PkceUtil.kt", "OkHttpCookieJar.kt" }
        },
        new SourceDirectory
        {
            Path = "app/src/main/java/com/alditalk/panther",
            Purpose = "App entrypoint, theme/night mode, shared preferences, user-facing settings.",
            KeyFiles = new[] { "PantherApp.kt", "MainActivity.kt" }
        },
        new SourceDirectory
        {
            Path = "app/src/main/res",
            Purpose = "Android resources: strings, colors, themes, layout, manifest. Useful for parity of wording and visual intent.",
            KeyFiles = new[] { "values/strings.xml", "values/colors.xml", "values/themes.xml", "layout/activity_main.xml", "AndroidManifest.xml" }
        }
    },
    DeviceVariants = new[]
    {
        new DeviceVariant { Folder = "Redmi Note 9 Pro", ApplicationId = "com.alditalk.panther.redminote9pro", Notes = "MIUI-specific persistence guidance and battery/autostart settings." },
        new DeviceVariant { Folder = "Ulefone Power Armor X11Pro", ApplicationId = "com.alditalk.panther.x11pro", Notes = "Aggressive background-kill handling; same app logic as root." }
    },
    EndpointsToConfirm = new[]
    {
        "ForgeRock login endpoint used by the app",
        "OAuth2 authorize endpoint and redirect URI used by the app",
        "Account/usage endpoint that returns remaining MB",
        "Top-up trigger endpoint for 1 GB booking",
        "Any refresh/re-login indicator returned by the API"
    },
    CredentialsToKeepFromAppSource = new[]
    {
        "Client identifier used in the OAuth2 authorize call",
        "Redirect URI used in the OAuth2 authorize call",
        "Exact User-Agent string used by the app",
        "Exact JSON content type/media type used for credential submit",
        "Exact callback field names and token field names returned by ForgeRock"
    },
    AndroidBehaviorParity = new[]
    {
        "Parse PoW parameters from TextOutputCallback message using the same regex shapes as the app.",
        "Submit PoW answer, phone, password, and login button value exactly as the app does.",
        "Store iPlanetDirectoryPro cookie with the same domain/path semantics.",
        "Perform OAuth2 authorize with PKCE S256, nonce, and state.",
        "Follow redirects manually, resolving '//host/...' style paths against the current base URL.",
        "Detect session expiry and trigger re-login with the same failure-counting and pause behavior."
    },
    WindowsParityPlan = new[]
    {
        "Keep the same request sequence, headers, cookie handling, and failure counting.",
        "Store credentials with DPAPI or Windows Credential Manager, not plaintext.",
        "Use a background worker/timer service instead of an Android Service, with a system-tray presence.",
        "Use a bounded in-memory or local log store with the same cap as the Android UI and trimming behavior.",
        "Surface pause state clearly and require explicit user action to resume.",
        "Provide threshold (MB) and interval (seconds) settings matching the app defaults (850 MB, 60 s)."
    }
});

public sealed record SourceMapping
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string[] RootNotes { get; init; }
    public required SourceDirectory[] Directories { get; init; }
    public required DeviceVariant[] DeviceVariants { get; init; }
    public required string[] EndpointsToConfirm { get; init; }
    public required string[] CredentialsToKeepFromAppSource { get; init; }
    public required string[] AndroidBehaviorParity { get; init; }
    public required string[] WindowsParityPlan { get; init; }
}

public sealed record SourceDirectory
{
    public required string Path { get; init; }
    public required string Purpose { get; init; }
    public required string[] KeyFiles { get; init; }
}

public sealed record DeviceVariant
{
    public required string Folder { get; init; }
    public required string ApplicationId { get; init; }
    public required string Notes { get; init; }
}
