<#
.SYNOPSIS
    Updates https://delyvo.octopi-digital.com (IIS site "Delyvo", OneDrop Courier) with the latest code.

.DESCRIPTION
    Run in PowerShell as Administrator, from this folder:   .\Deploy-Iis.ps1

      1. git pull           local changes are kept
      2. build              dotnet publish src/Web (Release) into a staging folder
      3. offline            app_offline.htm: the site shows "updating", the app stops
      4. database           tools/db/publish.ps1 against the site's database: dbup pre -> dacpac -> dbup
      5. copy               into C:\inetpub\delyvo. Nothing outside wwwroot is deleted; wwwroot is mirrored
      6. online             app_offline.htm removed
      7. check              the sign-in page must answer 200

    Steps 1-2 change nothing on the site, so a failure there leaves it running as it was. A failure from
    step 3 on stops with the site on its "updating" page, on purpose; the message says how to bring it back.

    Disk: this server is short of space, so every run removes its build folders afterwards.

    Never touched: the site's settings (on the IIS app pool, not in any file), its logs
    (C:\inetpub\delyvo-data), and the logins, merchants and parcels already in the database.

.PARAMETER NoPull
    Deploys the code as it is now, without git pull.

.PARAMETER BuildOnly
    Steps 1-2 only: proves the app builds. The site and the database are not touched.

.EXAMPLE
    .\Deploy-Iis.ps1
#>
param(
    [switch]$NoPull,

    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"

$Repo     = $PSScriptRoot
$Site     = "Delyvo"
$HostName = "delyvo.octopi-digital.com"
$SitePath = "C:\inetpub\delyvo"
$Staging  = Join-Path $env:TEMP "delyvo-publish"
$DbLog    = Join-Path $env:TEMP "delyvo-database.log"

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

# Runs a program and judges it by its exit code alone. Programs print warnings and progress on their error
# stream (git, dotnet), and Windows PowerShell turns those lines into errors whenever output is captured.
function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)] [string]$Failure,
        [Parameter(Mandatory = $true)] [scriptblock]$Command,
        [int]$OkBelow = 1
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $Command 2>&1 | ForEach-Object { "$_" } | Out-Host
    }
    finally {
        $ErrorActionPreference = $previous
    }
    if ($LASTEXITCODE -ge $OkBelow) { throw "$Failure (exit $LASTEXITCODE)" }
}

# Build folders are removed after every run, success or not: this disk cannot keep them.
function Remove-BuildLeftovers {
    Remove-Item -Recurse -Force $Staging -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $Repo "src"), (Join-Path $Repo "tests"), (Join-Path $Repo "tools") -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in "bin", "obj" } |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName -ErrorAction SilentlyContinue }
}

$FreeGb = (Get-PSDrive C).Free / 1GB
if ($FreeGb -lt 0.6) { throw ("Only {0:N2} GB free on C:. An update needs about 0.6 GB; free some space first." -f $FreeGb) }
if ($FreeGb -lt 1.5) { Write-Host ("Warning: only {0:N2} GB free on C:." -f $FreeGb) -ForegroundColor Yellow }

try {

# --- 1. Latest code -----------------------------------------------------------------------------------
if (-not $NoPull) {
    Write-Step "git pull"
    Invoke-Tool "git pull failed. In $Repo run 'git status' and sort it out, then run this again" { git -C $Repo pull --ff-only --autostash }
}

# --- 2. Build -----------------------------------------------------------------------------------------
Write-Step "Publishing the app (Release)"
if (Test-Path $Staging) { Remove-Item -Recurse -Force $Staging }
# --disable-build-servers: no compiler processes left behind, holding memory and locking the build folders.
Invoke-Tool "The publish failed" { dotnet publish (Join-Path $Repo "src\Web") -c Release -o $Staging --nologo -v minimal --disable-build-servers }

if ($BuildOnly) {
    Write-Host ""
    Write-Host "  Build OK. Nothing on the site or in the database was changed (-BuildOnly)." -ForegroundColor Green
    return
}

# The site's own database, from its settings in IIS.
Import-Module WebAdministration
$Connection = (Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' `
    -Filter "system.applicationHost/applicationPools/add[@name='$Site']/environmentVariables" -Name Collection |
    Where-Object { $_.name -eq 'ConnectionStrings__Database' }).value
if (-not $Connection) { throw "The $Site app pool has no ConnectionStrings__Database setting." }

# --- 3. Offline ---------------------------------------------------------------------------------------
Write-Step "Taking the site offline for the update"
$Offline = Join-Path $SitePath "app_offline.htm"
Set-Content -Path $Offline -Encoding UTF8 -Value @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Updating</title><meta http-equiv="refresh" content="15"></head>
<body style="font-family:system-ui,sans-serif;text-align:center;padding:4rem 1rem">
<h1>We are updating the site</h1><p>Back in a minute. This page reloads by itself.</p></body></html>
"@

try {
    $Dll = Join-Path $SitePath "Web.dll"
    foreach ($attempt in 1..30) {
        if (-not (Test-Path $Dll)) { break }
        try {
            [IO.File]::Open($Dll, 'Open', 'ReadWrite', 'None').Dispose()
            break
        }
        catch {
            Start-Sleep -Seconds 1
        }
    }

    # --- 4. Database ----------------------------------------------------------------------------------
    # The project's own script, in its own order. Its output goes to a file: it is long, and a pipeline that
    # stops reading early would stop the script half way through the schema.
    Write-Step "Updating the database (log: $DbLog)"
    & (Join-Path $Repo "tools\db\publish.ps1") -ConnectionString $Connection *> $DbLog
    if (-not (Select-String -Path $DbLog -Pattern 'Database is up to date\.' -Quiet) -or
        (Select-String -Path $DbLog -Pattern 'failed\.' -Quiet)) {
        throw "The database update failed; the reason is in $DbLog."
    }
    Get-Content $DbLog | Where-Object { $_ -match "Executing Database Server script|No new scripts|Update complete" } | Out-Host

    # --- 5. Copy --------------------------------------------------------------------------------------
    Write-Step "Copying the new version into $SitePath"
    $WebRoot = Join-Path $Staging "wwwroot"
    # The repository's development settings never go onto the server.
    Invoke-Tool "Copying the app failed" -OkBelow 8 { robocopy $Staging $SitePath /E /XD $WebRoot /XF appsettings.Development.json app_offline.htm /R:3 /W:2 /NFL /NDL /NJH /NP }
    if (Test-Path $WebRoot) {
        Invoke-Tool "Copying the web files failed" -OkBelow 8 { robocopy $WebRoot (Join-Path $SitePath "wwwroot") /MIR /R:3 /W:2 /NFL /NDL /NJH /NP }
    }
}
catch {
    Write-Host ""
    Write-Host "Stopped: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "The site is still showing its 'updating' page, on purpose. Fix the problem and run this again." -ForegroundColor Yellow
    Write-Host "To bring the site back as it is now instead, delete: $Offline" -ForegroundColor Yellow
    exit 1
}

# --- 6. Online ----------------------------------------------------------------------------------------
Remove-Item -Force $Offline

# --- 7. Check -----------------------------------------------------------------------------------------
Write-Step "Checking https://$HostName"
# Pointed at 127.0.0.1 on purpose: this server cannot open its own public address.
$Status = ""
foreach ($attempt in 1..30) {
    $Status = curl.exe -s -o NUL -w "%{http_code}" --max-time 30 --resolve "${HostName}:443:127.0.0.1" "https://$HostName/Account/Login"
    if ($Status -eq "200") { break }
    Start-Sleep -Seconds 2
}

Write-Host ""
if ($Status -eq "200") {
    Write-Host "  Live at https://$HostName" -ForegroundColor Green
}
else {
    Write-Host "  The site did not answer (last status: $Status)." -ForegroundColor Red
    Write-Host "  Startup errors: Event Viewer -> Windows Logs -> Application (source: IIS AspNetCore Module V2)," -ForegroundColor Red
    Write-Host "  and C:\inetpub\delyvo-data\logs (newest file)." -ForegroundColor Red
    exit 1
}
Write-Host ""

}
finally {
    Remove-BuildLeftovers
}
