<#
.SYNOPSIS
    Publica o Monitoramento (Release, win-x64, arquivo único) em .\publish\.
.PARAMETER SelfContained
    Embute o runtime do .NET (não exige o .NET 10 Desktop Runtime instalado; o executável fica bem maior).
#>
[CmdletBinding()]
param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$projeto = Join-Path $raiz 'src\Monitoramento.App\Monitoramento.App.csproj'
$saida = Join-Path $raiz 'publish'

if (Test-Path $saida) { Remove-Item $saida -Recurse -Force }

$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish $projeto `
    -c Release `
    -r win-x64 `
    --self-contained $sc `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $saida
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)." }

$exe = Join-Path $saida 'Monitoramento.App.exe'
$mb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Publicado em $saida"
Write-Host "Executável: $exe ($mb MB)$(if ($SelfContained) { ' - com runtime embutido' } else { ' - requer .NET 10 Desktop Runtime' })"
Write-Host "Mantenha a pasta wwwroot ao lado do executável."
