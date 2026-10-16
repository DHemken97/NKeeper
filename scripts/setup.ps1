<#
.SYNOPSIS
    NKeeper setup for Windows. Installs or upgrades NKeeper from this checkout; safe to run again.

.DESCRIPTION
    Modes
      Docker (recommended)  PostgreSQL, the API, guacd and nginx in Docker Desktop containers.
      Iis                   The API (and web frontend) as an IIS site with HTTPS, using PostgreSQL
                            for Windows. guacd (needed for RDP/VNC) has no Windows build, so it
                            runs in Docker Desktop when available. Run as administrator.

    Easiest: double-click scripts\setup.cmd, or run
        powershell -ExecutionPolicy Bypass -File scripts\setup.ps1

.EXAMPLE
    .\scripts\setup.ps1
    Asks a few questions, then installs with Docker Desktop.

.EXAMPLE
    .\scripts\setup.ps1 -Mode Iis -HostName nkeeper.corp.example.com -Yes
    Installs under IIS without asking.
#>
[CmdletBinding()]
param(
    # Docker (default) or Iis.
    [ValidateSet('Docker', 'Iis')]
    [string]$Mode,
    # Names/addresses clients use to open NKeeper (default: this computer's name and IP address).
    [string[]]$HostName,
    # Separate domain for web sessions (nkw-<id>.<domain>).
    [string]$WebDomain,
    # Only reachable from this computer (https://localhost).
    [switch]$LocalOnly,
    # HTTPS port (default 443) and HTTP port that redirects to it (default 80).
    [int]$HttpsPort,
    [int]$HttpPort,
    # Docker mode: your own certificate (PEM, with intermediates) and key, instead of NKeeper's private CA.
    [string]$CertificateFile,
    [string]$KeyFile,
    # IIS mode: thumbprint of your own certificate in LocalMachine\My, instead of NKeeper's private CA.
    [string]$CertificateThumbprint,
    # First administrator (defaults: admin, admin@<hostname>, generated password shown once).
    [string]$AdminUser,
    [string]$AdminEmail,
    [string]$AdminPassword,
    # IIS mode: use an existing PostgreSQL database instead of installing PostgreSQL.
    [string]$ConnectionString,
    # IIS mode: skip guacd (RDP and VNC won't work).
    [switch]$NoGuacd,
    # Don't ask; use the defaults.
    [switch]$Yes,
    # Remove NKeeper (keeps the database unless -Purge).
    [switch]$Uninstall,
    [switch]$Purge
)

Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Repo = Split-Path -Parent $PSScriptRoot
$GuacdImage = 'guacamole/guacd:1.6.0'
$Project = if ($env:COMPOSE_PROJECT_NAME) { $env:COMPOSE_PROJECT_NAME } else { 'nkeeper' }
$DataDir = Join-Path $env:ProgramData 'NKeeper'
$StateFile = Join-Path $DataDir 'state.json'
$SiteDir = Join-Path $env:SystemDrive 'inetpub\nkeeper'
$RedirectDir = Join-Path $env:SystemDrive 'inetpub\nkeeper-redirect'
$SiteName = 'NKeeper'
$RedirectSiteName = 'NKeeper HTTP redirect'
$PoolName = 'NKeeper'

$script:GeneratedPassword = $false
$script:Bootstrapped = $false
$script:CaFile = $null

# --- Helpers ---------------------------------------------------------------------------------------

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "    $Text" -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Warning $Text }
function Stop-Setup([string]$Text) { throw "NKeeper setup: $Text" }

function Test-Interactive { -not $Yes -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected }

function Read-Answer([string]$Question, [string]$Default) {
    if (-not (Test-Interactive)) { return $Default }
    $answer = Read-Host "$Question [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim()
}

function Confirm-Step([string]$Question) {
    if ($Yes) { return $true }
    if (-not (Test-Interactive)) { return $false }
    return (Read-Host "$Question [Y/n]") -notmatch '^[Nn]'
}

function New-RandomText([int]$Length) {
    $bytes = New-Object byte[] $Length
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    return (-join ($bytes | ForEach-Object { $_.ToString('x2') })).Substring(0, $Length)
}

# Meets NKeeper's policy: 12+ characters with upper case, lower case and a digit.
function New-AdminPassword { "Nk$(New-RandomText 6)-$(New-RandomText 6)-$(New-RandomText 6)A7" }

function Test-AdminPassword([string]$Value) {
    $Value.Length -ge 12 -and $Value -cmatch '[A-Z]' -and $Value -cmatch '[a-z]' -and $Value -match '[0-9]'
}

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DefaultNames {
    $names = @()
    try { $names += [Net.Dns]::GetHostEntry('').HostName } catch { $names += $env:COMPUTERNAME }
    try {
        $ip = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' } |
            Select-Object -First 1 | ForEach-Object { $_.IPv4Address.IPAddress } | Select-Object -First 1
        if ($ip) { $names += $ip }
    } catch { }
    return ($names | Where-Object { $_ } | Select-Object -Unique)
}

function Test-IpAddress([string]$Value) { $null -ne ($Value -as [Net.IPAddress]) -and $Value -match '[.:]' }

function Get-FirstName([string[]]$Names) {
    $named = $Names | Where-Object { -not (Test-IpAddress $_) } | Select-Object -First 1
    if ($named) { return $named }
    return $Names[0]
}

function Get-Url([string]$Name, [int]$Port) {
    if ($Name -match ':') { $Name = "[$Name]" }
    if ($Port -eq 443) { return "https://$Name" }
    return "https://${Name}:$Port"
}

function Test-PortInUse([int]$Port) {
    $null -ne (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
}

function Write-TextFile([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

# .env files: KEY=value lines, LF line endings, no BOM.
function Get-EnvValue([string]$Path, [string]$Key) {
    if (-not (Test-Path $Path)) { return '' }
    $line = Get-Content $Path | Where-Object { $_.StartsWith("$Key=") } | Select-Object -Last 1
    if ($null -eq $line) { return '' }
    return $line.Substring($Key.Length + 1)
}

function Set-EnvValue([string]$Path, [string]$Key, [string]$Value) {
    $lines = @(Get-Content $Path)
    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i].StartsWith("$Key=")) { $lines[$i] = "$Key=$Value"; $found = $true }
    }
    if (-not $found) { $lines += "$Key=$Value" }
    Write-TextFile $Path (($lines -join "`n") + "`n")
}

function Update-SessionPath {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

function Wait-Url([string]$Url, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) { return $true }
        } catch { }
        Start-Sleep -Seconds 2
    }
    return $false
}

# Runs a native command with its output discarded; returns its exit code. (Windows PowerShell turns
# redirected stderr into errors, which $ErrorActionPreference = 'Stop' would make fatal.)
function Invoke-Quiet([scriptblock]$Command) {
    $ErrorActionPreference = 'Continue'
    & $Command *> $null
    return $LASTEXITCODE
}

# Runs a native command and returns its standard output, ignoring stderr.
function Invoke-Capture([scriptblock]$Command) {
    $ErrorActionPreference = 'Continue'
    & $Command 2> $null
}

# Runs a native command; stops setup if it fails.
function Invoke-Native([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { Stop-Setup "$What failed (exit code $LASTEXITCODE)." }
}

# --- Questions -------------------------------------------------------------------------------------

# The saved state as a hashtable (missing keys read as $null), or $null before the first install.
function Get-State {
    if (-not (Test-Path $StateFile)) { return $null }
    $state = @{}
    (Get-Content $StateFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $state[$_.Name] = $_.Value }
    return $state
}

function Resolve-Settings {
    $state = Get-State
    if (-not $script:Mode) {
        if ($state) { $script:Mode = 'Iis' }
        elseif (Test-Path (Join-Path $Repo '.env')) { $script:Mode = 'Docker' }
        elseif (Test-Interactive) {
            Write-Host 'How should NKeeper run?'
            Write-Host '  1) Docker Desktop containers (recommended)'
            Write-Host '  2) IIS on this computer (run as administrator)'
            $script:Mode = if ((Read-Answer 'Choose' '1') -match '^(2|iis)$') { 'Iis' } else { 'Docker' }
        } else { $script:Mode = 'Docker' }
    }

    # A re-run keeps the previous answers unless parameters override them.
    $previous = @()
    $previousHttps = 0
    $previousHttp = 0
    $previousLocal = $false
    if ($script:Mode -eq 'Docker') {
        $envFile = Join-Path $Repo '.env'
        $previous = @((Get-EnvValue $envFile 'NKEEPER_SERVER_NAMES') -split ' ' | Where-Object { $_ })
        $previousHttps = [int]("0" + (Get-EnvValue $envFile 'NKEEPER_HTTPS_PORT'))
        $previousHttp = [int]("0" + (Get-EnvValue $envFile 'NKEEPER_HTTP_REDIRECT_PORT'))
        $previousLocal = (Get-EnvValue $envFile 'NKEEPER_HTTPS_BIND') -eq '127.0.0.1'
        # .env.example's values (localhost on 8443) aren't a previous choice.
        if (($previous -join ' ') -eq 'localhost' -and $previousHttps -eq 8443) { $previous = @(); $previousHttps = 0; $previousHttp = 0 }
    } elseif ($state) {
        $previous = @($state['Names'])
        $previousHttps = [int]$state['HttpsPort']
        $previousHttp = [int]$state['HttpPort']
        $previousLocal = [bool]$state['LocalOnly']
    }
    if (-not $script:HttpsPort) { $script:HttpsPort = if ($previousHttps) { $previousHttps } else { 443 } }
    if (-not $script:HttpPort) { $script:HttpPort = if ($previousHttp) { $previousHttp } else { 80 } }
    # A local-only install stays local-only unless new names are given.
    if (-not $script:HostName -and $previousLocal -and ($previous -join ' ') -eq 'localhost') { $script:LocalOnly = [switch]$true }

    if ($LocalOnly) {
        $script:HostName = @('localhost')
    } elseif (-not $script:HostName) {
        $default = if ($previous.Count) { $previous } else { Get-DefaultNames }
        $answer = Read-Answer 'Names or addresses people will use to open NKeeper (space-separated)' ($default -join ' ')
        $script:HostName = @($answer -split '[ ,]+' | Where-Object { $_ })
    } else {
        $script:HostName = @($script:HostName | ForEach-Object { $_ -split '[ ,]+' } | Where-Object { $_ })
    }
    if (-not $script:HostName) { Stop-Setup 'No host name given (use -HostName).' }

    if ([bool]$CertificateFile -ne [bool]$KeyFile) { Stop-Setup '-CertificateFile and -KeyFile go together.' }
    if ($CertificateFile) {
        $script:CertificateFile = (Resolve-Path $CertificateFile).Path
        $script:KeyFile = (Resolve-Path $KeyFile).Path
    }
}

function Resolve-Admin {
    if (-not $script:AdminUser) { $script:AdminUser = Read-Answer "First administrator's user name" 'admin' }
    if (-not $script:AdminEmail) {
        $domain = Get-FirstName $script:HostName
        if ($domain -notmatch '\.') { $domain = "$domain.local" }
        $script:AdminEmail = Read-Answer 'Their email address' "admin@$domain"
    }
    if (-not $script:AdminPassword -and (Test-Interactive)) {
        $secure = Read-Host 'Their password (12+ characters, upper and lower case and a digit; empty = generate)' -AsSecureString
        $script:AdminPassword = (New-Object Net.NetworkCredential('', $secure)).Password
    }
    if (-not $script:AdminPassword) {
        $script:AdminPassword = New-AdminPassword
        $script:GeneratedPassword = $true
    }
    if (-not (Test-AdminPassword $script:AdminPassword)) {
        Stop-Setup 'The password needs 12+ characters with upper case, lower case and a digit.'
    }
}

# --- Docker ----------------------------------------------------------------------------------------

function Initialize-Docker([switch]$Optional) {
    Update-SessionPath
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        if ($Optional) { return $false }
        if ((Get-Command winget -ErrorAction SilentlyContinue) -and (Confirm-Step "Docker Desktop isn't installed. Install it now with winget?")) {
            Write-Step 'Installing Docker Desktop'
            winget install -e --id Docker.DockerDesktop --accept-package-agreements --accept-source-agreements
            Write-Host ''
            Write-Host 'Docker Desktop is installed. Start it (it may ask you to sign out or restart, and to'
            Write-Host 'enable WSL 2), wait until it says "Engine running", then run this script again.'
            exit 0
        }
        Stop-Setup 'Docker Desktop is required for -Mode Docker: https://docs.docker.com/desktop/setup/install/windows-install/ (or use -Mode Iis).'
    }

    if ((Invoke-Quiet { docker info }) -ne 0) {
        $desktop = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
        if (Test-Path $desktop) {
            Write-Step 'Starting Docker Desktop'
            Start-Process $desktop
            $deadline = (Get-Date).AddMinutes(3)
            do { Start-Sleep -Seconds 3 } while ((Invoke-Quiet { docker info }) -ne 0 -and (Get-Date) -lt $deadline)
        }
        if ($LASTEXITCODE -ne 0) {
            if ($Optional) { return $false }
            Stop-Setup "Docker isn't running. Start Docker Desktop and run this script again."
        }
    }
    $osType = docker info --format '{{.OSType}}'
    if ($osType -ne 'linux') {
        if ($Optional) { return $false }
        Stop-Setup 'Docker Desktop is using Windows containers. Switch to Linux containers (tray icon menu) and run this script again.'
    }
    return $true
}

function Invoke-Compose {
    Push-Location $Repo
    try {
        docker compose @args
        if ($LASTEXITCODE -ne 0) { Stop-Setup "docker compose $($args -join ' ') failed." }
    } finally { Pop-Location }
}

function Install-Docker {
    Initialize-Docker | Out-Null
    $envFile = Join-Path $Repo '.env'
    $fresh = (Invoke-Quiet { docker volume inspect "${Project}_postgres-data" }) -ne 0

    Write-Step "Writing $envFile"
    if (-not (Test-Path $envFile)) {
        Write-TextFile $envFile ((Get-Content (Join-Path $Repo '.env.example') -Raw) -replace "`r`n", "`n")
    }
    if (-not (Get-EnvValue $envFile 'POSTGRES_PASSWORD')) {
        if (-not $fresh) { Stop-Setup 'A database already exists but .env has no POSTGRES_PASSWORD. Restore .env, or start over with -Uninstall -Purge.' }
        Set-EnvValue $envFile 'POSTGRES_PASSWORD' (New-RandomText 48)
    }
    Set-EnvValue $envFile 'NKEEPER_HTTPS_BIND' $(if ($LocalOnly) { '127.0.0.1' } else { '0.0.0.0' })
    Set-EnvValue $envFile 'NKEEPER_HTTPS_PORT' $HttpsPort
    Set-EnvValue $envFile 'NKEEPER_HTTP_REDIRECT_PORT' $HttpPort
    Set-EnvValue $envFile 'NKEEPER_SERVER_NAMES' ($HostName -join ' ')
    Set-EnvValue $envFile 'NKEEPER_WEB_BASE_DOMAIN' "$WebDomain"
    if ($fresh) {
        Resolve-Admin
        Set-EnvValue $envFile 'NKEEPER_BOOTSTRAP_ADMIN_ENABLED' 'true'
        Set-EnvValue $envFile 'NKEEPER_BOOTSTRAP_ADMIN_ALLOW_OUTSIDE_DEVELOPMENT' 'true'
        Set-EnvValue $envFile 'NKEEPER_BOOTSTRAP_ADMIN_USERNAME' $AdminUser
        Set-EnvValue $envFile 'NKEEPER_BOOTSTRAP_ADMIN_EMAIL' $AdminEmail
        Set-EnvValue $envFile 'NKEEPER_BOOTSTRAP_ADMIN_PASSWORD' $AdminPassword
        $script:Bootstrapped = $true
    } else {
        Write-Ok 'Existing installation found: upgrading it (data and accounts are kept).'
    }

    $localPort = [int]("0" + (Get-EnvValue $envFile 'NKEEPER_HTTP_PORT'))
    if (-not $localPort) { $localPort = 8080 }
    Push-Location $Repo
    $running = Invoke-Capture { docker compose ps -q frontend }
    Pop-Location
    if (-not $running) {
        foreach ($port in @($HttpsPort, $HttpPort, $localPort)) {
            if (Test-PortInUse $port) { Stop-Setup "Port $port is already in use on this computer. Free it, or choose others with -HttpsPort/-HttpPort." }
        }
    }

    Write-Step 'Building and starting the containers (the first build takes a few minutes)'
    Invoke-Compose up -d --build --remove-orphans

    if ($CertificateFile) {
        Write-Step 'Installing your certificate'
        Invoke-Compose cp $CertificateFile 'frontend:/etc/nginx/tls/custom.crt'
        Invoke-Compose cp $KeyFile 'frontend:/etc/nginx/tls/custom.key'
        Invoke-Compose exec -T -u 0 frontend sh -c 'chown 101:101 /etc/nginx/tls/custom.* && chmod 644 /etc/nginx/tls/custom.crt && chmod 600 /etc/nginx/tls/custom.key'
        Invoke-Compose restart frontend
    }

    Write-Step 'Waiting for NKeeper to start'
    if (-not (Wait-Url "http://127.0.0.1:$localPort/api/auth/antiforgery" 240)) {
        Push-Location $Repo; docker compose logs --tail 40 backend; Pop-Location
        Stop-Setup "NKeeper didn't start in time (logs above). Check: docker compose logs backend"
    }
    Write-Ok 'NKeeper is up.'

    if ($script:Bootstrapped) {
        # The administrator now exists: stop passing their password to the API.
        Set-EnvValue $envFile 'NKEEPER_BOOTSTRAP_ADMIN_ENABLED' 'false'
        Set-EnvValue $envFile 'NKEEPER_BOOTSTRAP_ADMIN_PASSWORD' ''
        Invoke-Compose up -d backend
    }

    if (-not $CertificateFile) {
        Push-Location $Repo
        $custom = (Invoke-Quiet { docker compose exec -T frontend test -s /etc/nginx/tls/custom.crt }) -eq 0
        if (-not $custom -and (Invoke-Quiet { docker compose cp 'frontend:/etc/nginx/tls/ca.crt' (Join-Path $Repo 'nkeeper-ca.crt') }) -eq 0) {
            $script:CaFile = Join-Path $Repo 'nkeeper-ca.crt'
        }
        Pop-Location
    }

    if (-not $LocalOnly) { Add-FirewallRule }
    Show-Summary
}

function Uninstall-Docker {
    Initialize-Docker | Out-Null
    Write-Step "Removing NKeeper's containers"
    if ($Purge) {
        Invoke-Compose down -v --remove-orphans
        Remove-Item (Join-Path $Repo '.env'), (Join-Path $Repo 'nkeeper-ca.crt') -ErrorAction SilentlyContinue
        Write-Ok 'Containers, database and certificates deleted.'
    } else {
        Invoke-Compose down --remove-orphans
        Write-Ok "Containers removed. The database is kept (volume ${Project}_postgres-data); -Purge deletes it."
    }
    Remove-FirewallRule
}

function Add-FirewallRule {
    if (-not (Test-Administrator)) {
        Write-Warn "Not running as administrator, so Windows Firewall wasn't changed. Allow inbound TCP $HttpsPort and $HttpPort if other computers can't connect."
        return
    }
    Remove-FirewallRule
    New-NetFirewallRule -DisplayName 'NKeeper (HTTPS)' -Group 'NKeeper' -Direction Inbound -Protocol TCP `
        -LocalPort @($HttpsPort, $HttpPort) -Action Allow -Profile Domain, Private | Out-Null
    Write-Ok "Windows Firewall allows TCP $HttpsPort and $HttpPort (domain and private networks)."
}

function Remove-FirewallRule {
    if (Test-Administrator) { Get-NetFirewallRule -Group 'NKeeper' -ErrorAction SilentlyContinue | Remove-NetFirewallRule }
}

# --- IIS -------------------------------------------------------------------------------------------

function Install-WingetPackage([string]$Id, [string]$What, [string[]]$Extra = @()) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        Stop-Setup "$What is required, and winget isn't available to install it. Install $What, then run this script again."
    }
    Write-Step "Installing $What"
    winget install -e --id $Id --accept-package-agreements --accept-source-agreements --disable-interactivity @Extra
    # winget returns non-zero for "already installed"; check the result instead of the exit code.
    Update-SessionPath
}

function Install-IisFeatures {
    Write-Step 'Enabling IIS (with WebSockets and HTTP redirection)'
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os.ProductType -eq 1) {
        $features = 'IIS-WebServerRole', 'IIS-WebServer', 'IIS-CommonHttpFeatures', 'IIS-StaticContent', 'IIS-DefaultDocument',
            'IIS-HttpErrors', 'IIS-HttpRedirect', 'IIS-WebSockets', 'IIS-RequestFiltering', 'IIS-HttpLogging',
            'IIS-ManagementConsole', 'IIS-ManagementScriptingTools'
        $missing = $features | Where-Object { (Get-WindowsOptionalFeature -Online -FeatureName $_).State -ne 'Enabled' }
        if ($missing) { Enable-WindowsOptionalFeature -Online -All -NoRestart -FeatureName $missing | Out-Null }
    } else {
        Install-WindowsFeature -Name Web-Server, Web-Static-Content, Web-Default-Doc, Web-Http-Errors, Web-Http-Redirect,
            Web-WebSockets, Web-Filtering, Web-Http-Logging, Web-Mgmt-Console, Web-Scripting-Tools | Out-Null
    }
    Import-Module WebAdministration
}

function Install-IisPrerequisites {
    Install-IisFeatures

    $sdk = $false
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { $sdk = [bool](dotnet --list-sdks | Where-Object { $_ -match '^10\.' }) }
    if (-not $sdk) { Install-WingetPackage 'Microsoft.DotNet.SDK.10' '.NET 10 SDK' }

    # The Hosting Bundle registers the ASP.NET Core Module with IIS, so it must come after IIS.
    $ancm = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    if (-not (Test-Path $ancm)) {
        Install-WingetPackage 'Microsoft.DotNet.HostingBundle.10' 'the ASP.NET Core Hosting Bundle'
        if (-not (Test-Path $ancm)) { Install-WingetPackage 'Microsoft.DotNet.HostingBundle.10' 'the ASP.NET Core Hosting Bundle (repair)' @('--force') }
        if (-not (Test-Path $ancm)) { Stop-Setup 'The ASP.NET Core Module is missing after installing the Hosting Bundle. Repair the Hosting Bundle from Apps & Features, then run this script again.' }
        $null = Invoke-Quiet { net stop was /y }
        $null = Invoke-Quiet { net start w3svc }
    }

    $node = Get-Command node -ErrorAction SilentlyContinue
    $nodeOk = $false
    if ($node) {
        $version = [version]((node --version).TrimStart('v'))
        $nodeOk = ($version.Major -eq 20 -and $version.Minor -ge 19) -or ($version.Major -eq 22 -and $version.Minor -ge 12) -or $version.Major -ge 23
    }
    if (-not $nodeOk) { Install-WingetPackage 'OpenJS.NodeJS.LTS' 'Node.js LTS' }

    foreach ($tool in 'dotnet', 'node', 'npm') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Stop-Setup "$tool isn't on PATH after installing it. Open a new PowerShell window and run this script again." }
    }
}

function Get-Psql {
    $psql = Get-ChildItem (Join-Path $env:ProgramFiles 'PostgreSQL\*\bin\psql.exe') -ErrorAction SilentlyContinue |
        Sort-Object { [int]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
    if ($psql) { return $psql.FullName }
    return $null
}

function Invoke-Psql([string]$Psql, [string]$SuperPassword, [string]$Sql) {
    $env:PGPASSWORD = $SuperPassword
    try {
        $Sql | & $Psql -h 127.0.0.1 -U postgres -d postgres -v ON_ERROR_STOP=1 -q -X
        if ($LASTEXITCODE -ne 0) { Stop-Setup 'Setting up the database failed (see above).' }
    } finally { Remove-Item Env:\PGPASSWORD }
}

function Initialize-Database($State) {
    if ($ConnectionString) { return $ConnectionString }
    if ($State -and $State['ConnectionString']) { return $State['ConnectionString'] }

    Write-Step 'Setting up PostgreSQL'
    $psql = Get-Psql
    $super = if ($State) { $State['PostgresSuperPassword'] } else { $null }
    if (-not $psql) {
        $super = New-RandomText 32
        Install-WingetPackage 'PostgreSQL.PostgreSQL.17' 'PostgreSQL 17' @('--override', "--mode unattended --unattendedmodeui none --superpassword $super --serverport 5432")
        $psql = Get-Psql
        if (-not $psql) { Stop-Setup 'PostgreSQL was not installed. Install PostgreSQL 15 or later, then run this script again (or pass -ConnectionString).' }
        Save-State @{ PostgresSuperPassword = $super }
    } elseif (-not $super) {
        if (-not (Test-Interactive)) { Stop-Setup 'PostgreSQL is already installed: pass -ConnectionString for an existing database, or run interactively to enter the postgres password.' }
        $secure = Read-Host "PostgreSQL is already installed. Password of its 'postgres' superuser" -AsSecureString
        $super = (New-Object Net.NetworkCredential('', $secure)).Password
    }

    $password = New-RandomText 48
    Invoke-Psql $psql $super @"
SET password_encryption = 'scram-sha-256';
DO `$`$ BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'nkeeper') THEN CREATE ROLE nkeeper LOGIN; END IF;
END `$`$;
ALTER ROLE nkeeper WITH LOGIN PASSWORD '$password';
SELECT 'CREATE DATABASE nkeeper OWNER nkeeper' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'nkeeper')\gexec
"@
    Write-Ok 'Database nkeeper ready.'
    return "Host=127.0.0.1;Port=5432;Database=nkeeper;Username=nkeeper;Password=$password"
}

# State (names, ports, database login) for re-runs; readable by administrators only.
function Save-State([hashtable]$Values) {
    New-Item -ItemType Directory -Force $DataDir | Out-Null
    $current = Get-State
    if (-not $current) { $current = @{} }
    foreach ($key in $Values.Keys) { $current[$key] = $Values[$key] }
    Write-TextFile $StateFile ($current | ConvertTo-Json)
    Protect-File $StateFile @()
}

# Only SYSTEM, Administrators and the given identities can read the file.
function Protect-File([string]$Path, [string[]]$Readers) {
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $identity = (New-Object Security.Principal.SecurityIdentifier($sid)).Translate([Security.Principal.NTAccount])
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'Allow')))
    }
    foreach ($reader in $Readers) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($reader, 'Read', 'Allow')))
    }
    Set-Acl -Path $Path -AclObject $acl
}

function Get-SanText([string[]]$Names) {
    $entries = @('DNS=localhost', 'DNS=*.web.localhost', 'IPAddress=127.0.0.1')
    foreach ($name in $Names) {
        if (Test-IpAddress $name) { $entries += "IPAddress=$name" }
        elseif ($name.StartsWith('*.')) { $entries += "DNS=$name" }
        else { $entries += "DNS=$name"; $entries += "DNS=*.web.$name" }
    }
    return ($entries | Select-Object -Unique) -join '&'
}

function Get-Certificate($State) {
    if ($CertificateThumbprint) {
        $cert = Get-Item "Cert:\LocalMachine\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
        if (-not $cert -or -not $cert.HasPrivateKey) { Stop-Setup "No certificate with a private key and thumbprint $CertificateThumbprint in LocalMachine\My." }
        return $cert.Thumbprint
    }

    $names = @($HostName)
    if ($WebDomain) { $names += "*.$WebDomain" }
    $san = Get-SanText $names
    $ca = $null
    if ($State -and $State['CaThumbprint']) { $ca = Get-Item "Cert:\LocalMachine\My\$($State['CaThumbprint'])" -ErrorAction SilentlyContinue }
    if ($State -and $State['ServerThumbprint'] -and $State['ServerSan'] -eq $san -and $ca) {
        $existing = Get-Item "Cert:\LocalMachine\My\$($State['ServerThumbprint'])" -ErrorAction SilentlyContinue
        if ($existing -and $existing.NotAfter -gt (Get-Date).AddDays(30)) { return $existing.Thumbprint }
    }

    Write-Step "Issuing an HTTPS certificate from NKeeper's private CA"
    $first = Get-FirstName $HostName
    if (-not $ca) {
        $ca = New-SelfSignedCertificate -Subject "CN=NKeeper private CA ($first), O=NKeeper" -CertStoreLocation Cert:\LocalMachine\My `
            -KeyAlgorithm ECDSA_nistP256 -HashAlgorithm SHA256 -KeyUsage CertSign, CRLSign -KeyExportPolicy NonExportable `
            -NotAfter (Get-Date).AddYears(10) -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0')
    }
    # 825 days: the longest browsers accept for certificates from a private CA.
    $server = New-SelfSignedCertificate -Subject "CN=$first, O=NKeeper" -CertStoreLocation Cert:\LocalMachine\My -Signer $ca `
        -KeyAlgorithm ECDSA_nistP256 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays(825) -TextExtension @("2.5.29.17={text}$san", '2.5.29.37={text}1.3.6.1.5.5.7.3.1', '2.5.29.19={critical}{text}ca=0')
    # IIS (running as the app pool) only needs the certificate; the server itself trusts its CA so
    # local checks work.
    $rootStore = New-Object Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
    $rootStore.Open('ReadWrite'); $rootStore.Add($ca); $rootStore.Close()
    Save-State @{ CaThumbprint = $ca.Thumbprint; ServerThumbprint = $server.Thumbprint; ServerSan = $san }
    return $server.Thumbprint
}

function Export-CaCertificate($State) {
    if ($CertificateThumbprint -or -not $State['CaThumbprint']) { return }
    $ca = Get-Item "Cert:\LocalMachine\My\$($State['CaThumbprint'])" -ErrorAction SilentlyContinue
    if (-not $ca) { return }
    $pem = "-----BEGIN CERTIFICATE-----`n" + ([Convert]::ToBase64String($ca.RawData, 'InsertLineBreaks') -replace "`r`n", "`n") + "`n-----END CERTIFICATE-----`n"
    $script:CaFile = Join-Path $Repo 'nkeeper-ca.crt'
    Write-TextFile $script:CaFile $pem
}

function Build-NKeeper {
    Write-Step 'Building NKeeper (a few minutes the first time)'
    $build = Join-Path $DataDir 'build'
    if (Test-Path $build) { Remove-Item -Recurse -Force $build }
    New-Item -ItemType Directory -Force $build | Out-Null
    # Build from a copy, so the checkout doesn't fill with build output.
    foreach ($part in 'backend', 'frontend') {
        robocopy (Join-Path $Repo $part) (Join-Path $build $part) /E /NFL /NDL /NJH /NJS /NP /XD node_modules bin obj dist .vite | Out-Null
        if ($LASTEXITCODE -ge 8) { Stop-Setup "Copying $part for the build failed." }
    }
    Push-Location (Join-Path $build 'frontend')
    try {
        Invoke-Native 'npm ci' { npm ci --no-audit --no-fund --loglevel=error }
        Invoke-Native 'Building the web frontend' { npm run build --silent }
    } finally { Pop-Location }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    Invoke-Native 'Building the API' {
        dotnet publish (Join-Path $build 'backend\src\NKeeper.Api\NKeeper.Api.csproj') -c Release -o (Join-Path $build 'site') -p:DebugType=none --nologo -v quiet
    }
    # The API serves the frontend itself when it's in wwwroot (no nginx under IIS).
    robocopy (Join-Path $build 'frontend\dist') (Join-Path $build 'site\wwwroot') /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { Stop-Setup 'Copying the web frontend failed.' }
    return (Join-Path $build 'site')
}

function New-WebConfig([hashtable]$Environment) {
    $variables = ($Environment.GetEnumerator() | Sort-Object Name | ForEach-Object {
            '          <environmentVariable name="{0}" value="{1}" />' -f $_.Name, [Security.SecurityElement]::Escape([string]$_.Value)
        }) -join "`r`n"
    return @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Written by scripts\setup.ps1 (contains secrets: readable only by administrators and the app pool). -->
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet" arguments=".\NKeeper.Api.dll" hostingModel="inprocess" stdoutLogEnabled="false">
        <environmentVariables>
$variables
        </environmentVariables>
      </aspNetCore>
      <webSocket enabled="true" />
      <security>
        <requestFiltering removeServerHeader="true">
          <!-- Uploads through web sessions (1 GB). -->
          <requestLimits maxAllowedContentLength="1073741824" />
        </requestFiltering>
      </security>
      <httpProtocol>
        <customHeaders>
          <remove name="X-Powered-By" />
        </customHeaders>
      </httpProtocol>
    </system.webServer>
  </location>
</configuration>
"@
}

function Install-Iis {
    if (-not (Test-Administrator)) { Stop-Setup 'IIS mode installs Windows features and services: run PowerShell as administrator.' }
    $state = Get-State
    $fresh = -not ($state -and $state['Installed'])

    Install-IisPrerequisites
    $connection = Initialize-Database $state
    Save-State @{ ConnectionString = $connection; Names = @($HostName); HttpsPort = $HttpsPort; HttpPort = $HttpPort; WebDomain = "$WebDomain"; LocalOnly = [bool]$LocalOnly }
    if ($fresh) { Resolve-Admin }
    $site = Build-NKeeper
    $thumbprint = Get-Certificate (Get-State)

    $guacd = $false
    if (-not $NoGuacd -and (Initialize-Docker -Optional)) {
        Write-Step 'Starting guacd (for RDP and VNC) in Docker on 127.0.0.1:4822'
        $null = Invoke-Quiet { docker rm -f nkeeper-guacd }
        Invoke-Native 'Starting guacd' { docker run -d --name nkeeper-guacd --restart unless-stopped -p 127.0.0.1:4822:4822 --cap-drop ALL --security-opt no-new-privileges $GuacdImage | Out-Null }
        $guacd = $true
    } elseif (-not $NoGuacd) {
        Write-Warn "Docker Desktop isn't available, so guacd isn't running: RDP and VNC sessions won't work. Install Docker Desktop and run this script again to add them."
    }

    Write-Step "Installing the IIS site in $SiteDir"
    Import-Module WebAdministration
    if (-not (Test-Path "IIS:\AppPools\$PoolName")) { New-WebAppPool -Name $PoolName | Out-Null }
    $pool = "IIS:\AppPools\$PoolName"
    Set-ItemProperty $pool -Name managedRuntimeVersion -Value ''
    # Remote sessions live in the worker process: never stop or recycle it on a timer.
    Set-ItemProperty $pool -Name startMode -Value AlwaysRunning
    Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Set-ItemProperty $pool -Name processModel.loadUserProfile -Value $true
    Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
    if ((Get-WebAppPoolState -Name $PoolName).Value -eq 'Started') { Stop-WebAppPool -Name $PoolName; Start-Sleep -Seconds 3 }

    New-Item -ItemType Directory -Force $SiteDir | Out-Null
    robocopy $site $SiteDir /MIR /NFL /NDL /NJH /NJS /NP /XF web.config | Out-Null
    if ($LASTEXITCODE -ge 8) { Stop-Setup "Copying NKeeper to $SiteDir failed." }

    $environment = @{
        ASPNETCORE_ENVIRONMENT = 'Production'
        ConnectionStrings__NKeeper = $connection
        Database__MigrateOnStartup = 'true'
        Guacd__Host = '127.0.0.1'
        Guacd__Port = '4822'
        WebProxy__BaseDomain = "$WebDomain"
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    }
    if ($fresh) {
        $environment['BootstrapAdmin__Enabled'] = 'true'
        $environment['BootstrapAdmin__AllowOutsideDevelopment'] = 'true'
        $environment['BootstrapAdmin__UserName'] = $AdminUser
        $environment['BootstrapAdmin__Email'] = $AdminEmail
        $environment['BootstrapAdmin__Password'] = $AdminPassword
        $script:Bootstrapped = $true
    } else {
        Write-Ok 'Existing installation found: upgrading it (data and accounts are kept).'
    }
    $webConfig = Join-Path $SiteDir 'web.config'
    Write-TextFile $webConfig (New-WebConfig $environment)
    Protect-File $webConfig @("IIS AppPool\$PoolName")

    # The site: HTTPS on every host name (web sessions use nkw-<id>.<domain> names).
    $ip = if ($LocalOnly) { '127.0.0.1' } else { '*' }
    if (-not (Get-Site $SiteName)) {
        New-Website -Name $SiteName -PhysicalPath $SiteDir -ApplicationPool $PoolName -Port $HttpsPort -IPAddress $ip -Ssl | Out-Null
    }
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $SiteDir
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $PoolName
    Get-WebBinding -Name $SiteName | Remove-WebBinding
    New-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort -IPAddress $ip
    $bindingIp = if ($LocalOnly) { '127.0.0.1' } else { '0.0.0.0' }
    Get-Item "IIS:\SslBindings\$bindingIp!$HttpsPort" -ErrorAction SilentlyContinue | Remove-Item
    Get-Item "Cert:\LocalMachine\My\$thumbprint" | New-Item "IIS:\SslBindings\$bindingIp!$HttpsPort" | Out-Null
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true

    Install-RedirectSite (Get-FirstName $HostName)

    Start-WebAppPool -Name $PoolName -ErrorAction SilentlyContinue
    Start-Website -Name $SiteName

    Write-Step 'Waiting for NKeeper to start'
    # Local check only: accept the certificate whatever it covers.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    $checkName = if ($LocalOnly -or $HostName -contains 'localhost') { 'localhost' } else { Get-FirstName $HostName }
    $readyUrl = (Get-Url 'localhost' $HttpsPort) + '/health/ready'
    if (-not (Wait-Url $readyUrl 180)) {
        Stop-Setup "NKeeper didn't start in time. Check the Application event log (source: IIS AspNetCore Module V2), or set stdoutLogEnabled=true in $webConfig."
    }
    Write-Ok "NKeeper is up ($checkName)."

    if ($script:Bootstrapped) {
        # The administrator now exists: stop passing their password to the API.
        foreach ($key in @($environment.Keys)) { if ($key -like 'BootstrapAdmin__*') { $environment.Remove($key) } }
        Write-TextFile $webConfig (New-WebConfig $environment)
        Protect-File $webConfig @("IIS AppPool\$PoolName")
    }
    Save-State @{ Installed = $true; Guacd = $guacd }

    Export-CaCertificate (Get-State)
    if (-not $LocalOnly) { Add-FirewallRule }
    Show-Summary
}

function Get-Site([string]$Name) { Get-Website | Where-Object { $_.Name -eq $Name } }

# Plain HTTP: a tiny site that redirects to HTTPS (IIS's own HTTP Redirection).
function Install-RedirectSite([string]$Name) {
    $default = Get-Site 'Default Web Site'
    if ($default -and $default.State -eq 'Started' -and ($default.bindings.Collection | Where-Object { $_.bindingInformation -eq "*:${HttpPort}:" })) {
        if (-not (Confirm-Step "IIS's 'Default Web Site' uses port $HttpPort. Stop it so NKeeper can redirect HTTP to HTTPS?")) {
            Write-Warn "Skipped the HTTP redirect: http:// addresses won't reach NKeeper (use $(Get-Url $Name $HttpsPort))."
            return
        }
        Stop-Website -Name 'Default Web Site'
        Set-ItemProperty 'IIS:\Sites\Default Web Site' -Name serverAutoStart -Value $false
    }
    New-Item -ItemType Directory -Force $RedirectDir | Out-Null
    $target = Get-Url $Name $HttpsPort
    Write-TextFile (Join-Path $RedirectDir 'web.config') @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Written by scripts\setup.ps1: sends plain HTTP to NKeeper's HTTPS address, keeping the path. -->
<configuration>
  <system.webServer>
    <httpRedirect enabled="true" destination="$target" exactDestination="false" childOnly="false" httpResponseStatus="Permanent" />
  </system.webServer>
</configuration>
"@
    $ip = if ($LocalOnly) { '127.0.0.1' } else { '*' }
    if (-not (Get-Site $RedirectSiteName)) {
        New-Website -Name $RedirectSiteName -PhysicalPath $RedirectDir -ApplicationPool $PoolName -Port $HttpPort -IPAddress $ip | Out-Null
    }
    Get-WebBinding -Name $RedirectSiteName | Remove-WebBinding
    New-WebBinding -Name $RedirectSiteName -Protocol http -Port $HttpPort -IPAddress $ip
    Start-Website -Name $RedirectSiteName -ErrorAction SilentlyContinue
}

function Uninstall-Iis {
    if (-not (Test-Administrator)) { Stop-Setup 'Run PowerShell as administrator.' }
    Import-Module WebAdministration -ErrorAction SilentlyContinue
    $state = Get-State
    Write-Step "Removing NKeeper's IIS site"
    foreach ($name in $SiteName, $RedirectSiteName) { if (Get-Site $name) { Remove-Website -Name $name } }
    if (Test-Path "IIS:\AppPools\$PoolName") { Remove-WebAppPool -Name $PoolName }
    if ($state) {
        $bindingIp = '0.0.0.0'
        Get-Item "IIS:\SslBindings\$bindingIp!$($state['HttpsPort'])" -ErrorAction SilentlyContinue | Remove-Item
    }
    Remove-Item -Recurse -Force $SiteDir, $RedirectDir, (Join-Path $DataDir 'build') -ErrorAction SilentlyContinue
    if (Get-Command docker -ErrorAction SilentlyContinue) { $null = Invoke-Quiet { docker rm -f nkeeper-guacd } }
    Remove-FirewallRule
    if ($Purge -and $state) {
        $psql = Get-Psql
        if ($psql -and $state['PostgresSuperPassword']) {
            Invoke-Psql $psql $state['PostgresSuperPassword'] "DROP DATABASE IF EXISTS nkeeper WITH (FORCE);`nDROP ROLE IF EXISTS nkeeper;"
        }
        foreach ($thumb in $state['ServerThumbprint'], $state['CaThumbprint']) {
            if ($thumb) { Get-ChildItem Cert:\LocalMachine\My, Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $thumb | Remove-Item }
        }
        Remove-Item -Recurse -Force $DataDir, (Join-Path $Repo 'nkeeper-ca.crt') -ErrorAction SilentlyContinue
        Write-Ok 'Removed, including the database and certificates. (PostgreSQL itself stays installed.)'
    } else {
        Write-Ok "Removed. The database (nkeeper) and $DataDir are kept; -Purge deletes them."
    }
}

# --- Summary ---------------------------------------------------------------------------------------

function Show-Summary {
    $first = Get-FirstName $HostName
    Write-Host ''
    Write-Host 'NKeeper is ready.' -ForegroundColor Green
    Write-Host ''
    Write-Host "  Open:        $(Get-Url $first $HttpsPort)"
    foreach ($name in $HostName) { if ($name -ne $first) { Write-Host "               $(Get-Url $name $HttpsPort)" } }
    if ($script:Bootstrapped) {
        Write-Host "  Sign in as:  $AdminUser"
        if ($script:GeneratedPassword) { Write-Host "  Password:    $AdminPassword   (shown only now: store it, or change it after signing in)" -ForegroundColor Yellow }
    }
    if ($script:CaFile) {
        Write-Host ''
        Write-Host "  HTTPS uses a certificate from NKeeper's private CA. To avoid browser warnings (and for"
        Write-Host '  web sessions inside NKeeper), install this CA as trusted on each client computer:'
        Write-Host "    $($script:CaFile)"
        Write-Host '  (Windows: certutil -addstore -f Root nkeeper-ca.crt as administrator; macOS: add to the'
        Write-Host '  System keychain and trust it; Linux: copy to /usr/local/share/ca-certificates/ and run'
        Write-Host '  update-ca-certificates.) Or use your own certificate: see -CertificateFile / -CertificateThumbprint.'
        # Only when asked interactively: Windows shows its own confirmation for adding a root CA.
        if ($Mode -eq 'Docker' -and (Test-Interactive) -and (Read-Host 'Trust it on this computer now (for your Windows account)? [y/N]') -match '^[Yy]') {
            Import-Certificate -FilePath $script:CaFile -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
        }
    }
    $webDomain = if ($WebDomain) { $WebDomain } else { "web.$first" }
    Write-Host ''
    Write-Host "  Web sessions open on nkw-<id>.${webDomain}: add a wildcard DNS record (*.$webDomain)"
    Write-Host '  pointing at this server to use them from other computers.'
    if ($Mode -eq 'Docker') { Write-Host '  Upgrade: git pull, then run this script again. Logs: docker compose logs -f backend' }
    else { Write-Host '  Upgrade: git pull, then run this script again (as administrator).' }
}

# --- Main ------------------------------------------------------------------------------------------

try {
    if ($Uninstall) {
        if (-not $Mode) { $Mode = if (Get-State) { 'Iis' } else { 'Docker' } }
        if ($Mode -eq 'Iis') { Uninstall-Iis } else { Uninstall-Docker }
        exit 0
    }
    Resolve-Settings
    if ($Mode -eq 'Docker') { Install-Docker } else { Install-Iis }
} catch {
    Write-Host ''
    Write-Host "error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
