$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $ProjectRoot

& .\host-win\build_icon.ps1
if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
  throw "Application icon build failed (exit code: $LASTEXITCODE)"
}

dotnet publish .\host-win\NawahHost.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  --output .\dist

if ($LASTEXITCODE -ne 0) {
  throw "Application build failed (exit code: $LASTEXITCODE)"
}

Write-Host "Application created at dist\AIPalmHost.exe"
