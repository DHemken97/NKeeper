<#
.SYNOPSIS
Builds NKeeper as a single container image (nginx, API, PostgreSQL and guacd together; see
container\Dockerfile), for Proxmox VE OCI containers, a registry, or plain `docker run`.

.EXAMPLE
scripts\build-image.ps1                               # dist\nkeeper-<version>-amd64.tar (OCI archive)
scripts\build-image.ps1 -Push ghcr.io/me/nkeeper      # also push <version> and latest
scripts\build-image.ps1 -Load                         # also load it into Docker as nkeeper:<version>

.NOTES
The archive is what Proxmox VE uploads as a container template (an OCI archive, not
`docker save` output). Needs Docker Desktop (with buildx).
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Name = 'nkeeper',
    # Comma-separated; one archive each.
    [string]$Platform = 'linux/amd64',
    # Push <repository>:<version> and :latest (run docker login first).
    [string]$Push = '',
    [switch]$Load,
    [switch]$NoArchive,
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$builder = 'nkeeper-builder'
if (-not $Output) { $Output = Join-Path $repo 'dist' }

function Fail([string]$message) { Write-Error "build-image: $message"; exit 1 }

# Runs a native command and returns its exit code. Windows PowerShell turns a native command's
# stderr (where buildx writes its progress) into errors under 'Stop', so it runs under 'Continue'.
function Invoke-Native([string]$exe, [string[]]$arguments, [switch]$Quiet) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($Quiet) { & $exe @arguments *> $null } else { & $exe @arguments 2>&1 | ForEach-Object { Write-Host "$_" } }
    } finally {
        $ErrorActionPreference = $previous
    }
    return $LASTEXITCODE
}

# Runs a native command and stops on a non-zero exit code.
function Invoke-Checked([string]$exe, [string[]]$arguments) {
    $code = Invoke-Native $exe $arguments
    if ($code -ne 0) { Fail "$exe $($arguments[0]) $($arguments[1]) failed (exit code $code)." }
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Fail "Docker isn't installed." }
if ((Invoke-Native docker @('buildx', 'version') -Quiet) -ne 0) { Fail "Docker buildx isn't available. Install or update Docker Desktop." }
if ((Invoke-Native docker @('info') -Quiet) -ne 0) { Fail "Docker isn't running. Start Docker Desktop." }

if (-not $Version) {
    $ErrorActionPreference = 'Continue'
    $Version = (git -C $repo describe --tags --always --dirty 2>$null)
    $ErrorActionPreference = 'Stop'
    if (-not $Version) { $Version = 'dev' }
    $Version = $Version.TrimStart('v')
}
if ($Version -notmatch '^[A-Za-z0-9_.-]+$') { Fail "the version may only contain letters, digits, '.', '_' and '-'." }
if ($Load -and $Platform.Contains(',')) { Fail '-Load takes one platform; drop it or pass a single -Platform.' }
if ($NoArchive -and -not $Push -and -not $Load) { Fail 'nothing to do: -NoArchive needs -Push or -Load.' }

# A BuildKit builder of its own: it can write OCI archives and cross-build other platforms.
if ((Invoke-Native docker @('buildx', 'inspect', $builder) -Quiet) -ne 0) {
    Write-Host "==> Creating the buildx builder $builder"
    Invoke-Checked docker @('buildx', 'create', '--name', $builder, '--driver', 'docker-container')
}

function Build([string[]]$extra) {
    # No provenance attestation: it adds an "unknown/unknown" platform entry some importers choke on.
    $arguments = @('buildx', 'build', '--builder', $builder, '-f', (Join-Path $repo 'container/Dockerfile'),
        '--provenance=false', '--build-arg', "NKEEPER_VERSION=$Version") + $extra + @($repo)
    Invoke-Checked docker $arguments
}

if (-not $NoArchive) {
    New-Item -ItemType Directory -Force $Output | Out-Null
    foreach ($p in $Platform.Split(',')) {
        $arch = ($p -replace '^[^/]*/', '') -replace '/', ''
        $file = Join-Path $Output "$Name-$Version-$arch.tar"
        Write-Host "==> Building ${Name}:$Version for $p -> $file"
        Build @('--platform', $p, '--output', "type=oci,dest=$file,name=${Name}:$Version")
    }
}

if ($Load) {
    Write-Host "==> Loading ${Name}:$Version into Docker"
    Build @('--platform', $Platform, '-t', "${Name}:$Version", '-t', "${Name}:latest", '--load')
}

if ($Push) {
    Write-Host "==> Pushing ${Push}:$Version and ${Push}:latest"
    Build @('--platform', $Platform, '-t', "${Push}:$Version", '-t', "${Push}:latest", '--push')
}

Write-Host ''
Write-Host "Done: NKeeper $Version."
if (-not $NoArchive) {
    Write-Host "  Archives: $(Join-Path $Output "$Name-$Version-*.tar")"
    Write-Host '  Proxmox VE: upload it under a storage''s CT Templates (Upload), then Create CT from it.'
}
if ($Push) { Write-Host "  Pushed: ${Push}:$Version (and latest)" }
if ($Load) { Write-Host "  Run: docker run -d --name nkeeper -p 443:443 -p 80:80 -v nkeeper-data:/var/lib/nkeeper ${Name}:$Version" }
Write-Host '  See docs/installation.md#single-container-image-proxmox-ve for the rest.'
