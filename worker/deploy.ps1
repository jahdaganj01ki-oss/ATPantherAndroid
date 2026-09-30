<#
  Deploy der Monitor-Freigabe (Cloudflare Worker + D1) in einem Durchgang.

  Aufruf:
    pwsh -File .\deploy.ps1 -Token "<API-TOKEN>" -AccountId "<ACCOUNT-ID>"

  Was das Skript tut:
    1. Token/Account pruefen (bricht mit klarer Meldung ab, wenn die
       Berechtigungen fehlen - das ist der haeufigste Fehler)
    2. D1-Datenbank anlegen oder die vorhandene finden
    3. schema.sql anwenden
    4. Worker als ESM-Modul hochladen (kein Build-Step noetig)
    5. workers.dev-Subdomain aktivieren
    6. Smoke-Test: /health, /state und ein exklusiver Lease-Test mit zwei
       simulierten Geraeten

  Noetige Token-Permissions (Account-Scope, Konto muss enthalten sein):
    Account | D1               | Edit
    Account | Workers Scripts  | Edit
    Account | Account Settings | Read
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Token,
    [string]$AccountId = 'dc36919cd74227d24176872483647bf1',
    [string]$DatabaseName = 'at-panther-lock',
    [string]$ScriptName = 'at-panther-lock'
)

$ErrorActionPreference = 'Stop'
$Api = 'https://api.cloudflare.com/client/v4'
$Headers = @{ Authorization = "Bearer $Token" }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Invoke-Cf {
    param([string]$Method, [string]$Uri, [object]$Body)
    $params = @{
        Method = $Method; Uri = $Uri; Headers = $Headers; TimeoutSec = 120
    }
    if ($null -ne $Body) {
        $params['ContentType'] = 'application/json'
        $params['Body'] = ($Body | ConvertTo-Json -Depth 12 -Compress)
    }
    try {
        return Invoke-RestMethod @params
    } catch {
        $detail = $_.ErrorDetails.Message
        if (-not $detail) { $detail = $_.Exception.Message }
        throw "$Method $Uri fehlgeschlagen: $detail"
    }
}

# ── 1) Zugang pruefen ──────────────────────────────────────────────────────
Write-Host '== 1) Zugang pruefen' -ForegroundColor Cyan
$verify = Invoke-Cf GET "$Api/user/tokens/verify"
if (-not $verify.result.status -eq 'active') { throw "Token ist nicht aktiv." }
Write-Host "   Token aktiv."

$accounts = Invoke-Cf GET "$Api/accounts?per_page=50"
$visible = @($accounts.result)
if ($visible.Count -eq 0) {
    throw @"
Der Token sieht KEIN Konto. Er ist vermutlich auf ein anderes Konto
eingeschraenkt oder hat gar keine Account-Permissions.

Neu erstellen unter cloudflare.com -> Mein Profil -> API-Tokens:
  Template "Edit Cloudflare Workers" UND zusaetzlich
  Account | D1 | Edit
  dazu "Account | Account Settings | Read"
  Unter "Account Resources" das Konto $AccountId auswaehlen.

Test vorher mit:
  curl -H "Authorization: Bearer <TOKEN>" $Api/accounts
"@
}
$accountOk = $visible | Where-Object { $_.id -eq $AccountId }
if (-not $accountOk) {
    Write-Warning "Konto $AccountId ist fuer diesen Token nicht sichtbar. Sichtbar: $(($visible | ForEach-Object { $_.name }) -join ', ')"
    $AccountId = $visible[0].id
    Write-Host "   Verwende stattdessen: $AccountId ($($visible[0].name))"
}
Write-Host "   Konto: $($accountOk.name -ne $null ? $accountOk.name : $visible[0].name)"

# ── 2) D1-Datenbank ────────────────────────────────────────────────────────
Write-Host '== 2) D1-Datenbank' -ForegroundColor Cyan
$dbList = Invoke-Cf GET "$Api/accounts/$AccountId/d1/database"
$db = @($dbList.result) | Where-Object { $_.name -eq $DatabaseName } | Select-Object -First 1
if (-not $db) {
    $created = Invoke-Cf POST "$Api/accounts/$AccountId/d1/database" @{ name = $DatabaseName }
    $db = $created.result
    Write-Host "   angelegt: $($db.name)  uuid=$($db.uuid)"
} else {
    Write-Host "   vorhanden: $($db.name)  uuid=$($db.uuid)"
}
$dbId = $db.uuid

# ── 3) Schema anwenden ─────────────────────────────────────────────────────
Write-Host '== 3) Schema anwenden' -ForegroundColor Cyan
$schema = Get-Content (Join-Path $here 'schema.sql') -Raw
$statements = $schema -split ';' | ForEach-Object {
    $s = ($_ -replace '(?m)^\s*--.*$', '').Trim()
    if ($s) { $s }
}
foreach ($sql in $statements) {
    $r = Invoke-Cf POST "$Api/accounts/$AccountId/d1/database/$dbId/query" @{ sql = $sql }
    if (-not $r.success) { throw "Schema-Ausfuehrung fehlgeschlagen: $($r.errors | ConvertTo-Json -Compress)" }
}
Write-Host "   $($statements.Count) Anweisung(en) ausgefuehrt."

# ── 4) Worker hochladen ────────────────────────────────────────────────────
Write-Host '== 4) Worker hochladen' -ForegroundColor Cyan
$code = Get-Content (Join-Path $here 'src\index.js') -Raw
$metadata = @{
    main_module       = 'index.js'
    compatibility_date = '2025-01-01'
    bindings          = @(@{ type = 'd1'; name = 'DB'; id = $dbId })
} | ConvertTo-Json -Depth 6 -Compress

$boundary = [System.Guid]::NewGuid().ToString()
$LF = "`n"
$part1 = "--$boundary$LF" +
        "Content-Disposition: form-data; name=`"metadata`"$LF" +
        "Content-Type: application/json$LF$LF" +
        "$metadata$LF"
$part2 = "--$boundary$LF" +
        "Content-Disposition: form-data; name=`"index.js`"; filename=`"index.js`"$LF" +
        "Content-Type: application/javascript$LF$LF" +
        "$code$LF"
$closing = "--$boundary--$LF"
$body = [System.Text.Encoding]::UTF8.GetBytes($part1 + $part2 + $closing)

$upload = Invoke-RestMethod -Method PUT `
    -Uri "$Api/accounts/$AccountId/workers/scripts/$ScriptName" `
    -Headers ($Headers + @{ 'Content-Type' = "multipart/form-data; boundary=$boundary" }) `
    -Body $body -TimeoutSec 180
if (-not $upload.success) { throw "Upload fehlgeschlagen: $($upload.errors | ConvertTo-Json -Compress)" }
Write-Host "   hochgeladen: $ScriptName"

# ── 5) workers.dev-Subdomain ───────────────────────────────────────────────
Write-Host '== 5) workers.dev-Subdomain' -ForegroundColor Cyan
$sub = Invoke-Cf GET "$Api/accounts/$AccountId/workers/subdomain"
$subName = $sub.result.subdomain
if (-not $subName) {
    $subName = "atp-lock-$([Math]::Abs($AccountId.GetHashCode()) % 100000)"
    $r = Invoke-Cf PUT "$Api/accounts/$AccountId/workers/subdomain" @{ subdomain = $subName }
    if (-not $r.success) { throw "Subdomain konnte nicht angelegt werden: $($r.errors | ConvertTo-Json -Compress)" }
    Write-Host "   Subdomain angelegt: $subName"
} else {
    Write-Host "   Subdomain: $subName"
}
$enable = Invoke-Cf POST "$Api/accounts/$AccountId/workers/scripts/$ScriptName/subdomain" @{ enabled = $true }
if (-not $enable.success) { throw "Subdomain-Aktivierung fehlgeschlagen: $($enable.errors | ConvertTo-Json -Compress)" }
$url = "https://$ScriptName.$subName.workers.dev"
Write-Host "   URL: $url" -ForegroundColor Green

# ── 6) Smoke-Test ──────────────────────────────────────────────────────────
Write-Host '== 6) Smoke-Test' -ForegroundColor Cyan
$health = Invoke-RestMethod "$url/health" -TimeoutSec 30
if (-not $health.ok) { throw "Worker nicht erreichbar: $url" }
Write-Host "   /health ok"

$state0 = Invoke-RestMethod "$url/state" -TimeoutSec 30
Write-Host "   /state: owner=$($state0.state.owner)"

function Sync([string]$deviceId, [string]$variant, [bool]$claim, [bool]$steal) {
    $payload = @{
        deviceId = $deviceId; variant = $variant; ttlSeconds = 900
        claimIfFree = $claim; steal = $steal
    } | ConvertTo-Json -Compress
    return Invoke-RestMethod -Method POST -Uri "$url/sync" `
        -ContentType 'application/json' -Body $payload -TimeoutSec 30
}

$a = Sync 'deploy-test-windows' 'windows' $true $false
if (-not $a.granted) { throw "Testgeraet A hat die Freigabe nicht bekommen." }
Write-Host "   A (windows) hat die Freigabe: $($a.state.owner)"

$b = Sync 'deploy-test-ulefone' 'ulefone' $true $false
if ($b.granted) { throw "FEHLER: Geraet B hat die Freigabe trotzdem bekommen - kein Doppelt-Inhaber-Schutz!" }
Write-Host "   B (ulefone) abgewiesen: $($b.state.owner) fragt ab"

$steal = Sync 'deploy-test-ulefone' 'ulefone' $true $true
if (-not $steal.granted) { throw "Uebernehmen hat nicht funktioniert." }
Write-Host "   B hat uebernommen: $($steal.state.owner)"

$rel = Invoke-RestMethod -Method POST -Uri "$url/release" -ContentType 'application/json' `
    -Body (@{ deviceId = 'deploy-test-ulefone' } | ConvertTo-Json -Compress) -TimeoutSec 30
if (-not $rel.released) { throw "Freigabe konnte nicht abgegeben werden." }
$after = Invoke-RestMethod "$url/state" -TimeoutSec 30
if ($null -ne $after.state.owner) { throw "Freigabe ist nicht wieder frei." }
Write-Host "   Freigabe wieder frei."

Write-Host ''
Write-Host 'FERTIG - diese URL in den Apps eintragen (Monitor-Freigabe):' -ForegroundColor Green
Write-Host "  $url" -ForegroundColor Green
Write-Host ''
Write-Host "database_id fuer wrangler.toml: $dbId"
