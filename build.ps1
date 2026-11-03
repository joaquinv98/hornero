# Compila la solución y corre todos los tests.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
dotnet build BillingOss.slnx -c $Configuration
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test BillingOss.slnx -c $Configuration --no-build
exit $LASTEXITCODE
