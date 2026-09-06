param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint,

    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $projectRoot "dist\AIPalmHost.exe"

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Build dist\AIPalmHost.exe before signing it."
}

$signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $signTool) {
    throw "SignTool was not found. Install the Windows SDK first."
}

& $signTool.FullName sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d "AI Palm Host" /du "https://nawah.almshary.site" $executable
if ($LASTEXITCODE -ne 0) { throw "Code signing failed." }

& $signTool.FullName verify /pa /v $executable
if ($LASTEXITCODE -ne 0) { throw "Signature verification failed." }

Write-Host "AIPalmHost.exe was signed and verified."
