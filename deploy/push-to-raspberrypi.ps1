[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$PiHost,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z_][a-z0-9_-]*$')]
    [string]$PiUser,

    [ValidateRange(1, 65535)]
    [int]$PiPort = 22,

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$RemoteDirectory = 'pihole-domain-review'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("pihole-domain-review-{0}.tar.gz" -f [guid]::NewGuid().ToString('N'))
$sshTarget = "$PiUser@$PiHost"
$remotePath = "`$HOME/$RemoteDirectory"

try {
    if (-not (Get-Command tar.exe -ErrorAction SilentlyContinue)) {
        throw 'tar.exe is required. Use PowerShell 7 or install Windows tar support.'
    }
    if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) {
        throw 'ssh.exe is required. Install or enable Windows OpenSSH.'
    }

    Write-Host "Creating deployment archive..."
    & tar.exe -czf $archivePath `
        --exclude=./.git `
        --exclude=./bin `
        --exclude=./obj `
        --exclude=./data `
        --exclude=./artifacts `
        -C $repoRoot .
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the deployment archive (exit code $LASTEXITCODE)."
    }

    $remoteCommand = @"
set -eu
mkdir -p "$remotePath"
base64 -d - | tar -xzf - -C "$remotePath"
cd "$remotePath"
if ! command -v docker >/dev/null 2>&1; then
    echo 'Docker is not installed or is not on the SSH user PATH.' >&2
    exit 20
fi
if docker compose version >/dev/null 2>&1; then
    docker compose up -d --build
elif command -v docker-compose >/dev/null 2>&1; then
    docker-compose up -d --build
else
    echo 'Docker Compose is not installed.' >&2
    exit 21
fi
"@
    Write-Host "Pushing to $sshTarget. OpenSSH will ask for the SSH password once."

    $archiveBase64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($archivePath))
    $archiveBase64 |
        & ssh.exe -p $PiPort -o StrictHostKeyChecking=accept-new $sshTarget $remoteCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Remote deployment failed (exit code $LASTEXITCODE)."
    }

    Write-Host "Deployment complete. Open http://$PiHost`:5178/"
}
finally {
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
}
