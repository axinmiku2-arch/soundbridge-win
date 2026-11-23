param(
  [ValidateSet("win-x64")]
  [string] $Architecture = "win-x64"
)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
  $publishDir = Join-Path "publish" $Architecture
  dotnet restore AudioRelay.csproj --locked-mode
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  dotnet publish AudioRelay.csproj -c Release --no-restore --output $publishDir
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  Write-Output "Built $publishDir\AudioRelay.exe"
}
finally {
  Pop-Location
}
