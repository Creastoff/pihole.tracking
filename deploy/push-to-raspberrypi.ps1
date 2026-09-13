[CmdletBinding()]
param(
    [Parameter(Mandatory = $false, Position = 0)]
    [string]$PiHost = "kristof-rp",

    [ValidatePattern('^[a-z_][a-z0-9_-]*$')]
    [string]$PiUser = "kristof",

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
$excludeFile = Join-Path $repoRoot 'deploy/deploy.exclude'
$startScript = Join-Path $repoRoot 'deploy/start-container.sh'

try {
    if (-not (Get-Command tar.exe -ErrorAction SilentlyContinue)) {
        throw 'tar.exe is required. Use PowerShell 7 or install Windows tar support.'
    }
    if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) {
        throw 'ssh.exe is required. Install or enable Windows OpenSSH.'
    }
    if (-not (Test-Path -LiteralPath $excludeFile) -or -not (Test-Path -LiteralPath $startScript)) {
        throw 'The shared deployment files in deploy/ are missing.'
    }

    Write-Host "Creating deployment archive..."
    & tar.exe -czf $archivePath `
        --exclude-from=$excludeFile `
        -C $repoRoot .
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the deployment archive (exit code $LASTEXITCODE)."
    }

    $remoteCommand = @"
set -eu
mkdir -p "$remotePath"
    base64 -d - | tar -xzf - -C "$remotePath"
cd "$remotePath"
bash deploy/start-container.sh
"@
    $remoteCommand = $remoteCommand -replace "`r`n", "`n"
    Write-Host "Pushing to $sshTarget. OpenSSH will ask for the SSH password once."

    $archiveBase64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($archivePath))
    $sshStartInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $sshStartInfo.FileName = (Get-Command ssh.exe).Source
    $sshStartInfo.UseShellExecute = $false
    $sshStartInfo.RedirectStandardInput = $true
    $sshStartInfo.ArgumentList.Add('-p')
    $sshStartInfo.ArgumentList.Add($PiPort.ToString())
    $sshStartInfo.ArgumentList.Add('-o')
    $sshStartInfo.ArgumentList.Add('StrictHostKeyChecking=accept-new')
    $sshStartInfo.ArgumentList.Add($sshTarget)
    $sshStartInfo.ArgumentList.Add($remoteCommand)

    $sshProcess = [System.Diagnostics.Process]::new()
    try {
        $sshProcess.StartInfo = $sshStartInfo
        if (-not $sshProcess.Start()) {
            throw 'Could not start ssh.exe.'
        }

        $writeTask = $sshProcess.StandardInput.WriteAsync($archiveBase64)
        $writeTask.GetAwaiter().GetResult()
        $sshProcess.StandardInput.Close()
        $sshProcess.WaitForExit()
        if ($sshProcess.ExitCode -ne 0) {
            throw "Remote deployment failed (exit code $($sshProcess.ExitCode))."
        }
    }
    finally {
        $sshProcess.Dispose()
    }

    Write-Host "Deployment complete. Open http://$PiHost`:5178/"
}
finally {
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
}
