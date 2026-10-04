param(
    [Parameter(Mandatory = $true)][string] $OriginalGame,
    [Parameter(Mandatory = $true)][string[]] $ArtisanDll,
    [string] $RepairDll = '',
    [string] $BepInExCore = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core',
    [switch] $SkipBuild
)
$ErrorActionPreference = 'Stop'
$OriginalGame = (Resolve-Path -LiteralPath $OriginalGame).Path
$BepInExCore = (Resolve-Path -LiteralPath $BepInExCore).Path
$managed = Join-Path $OriginalGame 'valheim_server_Data\Managed'
if (!(Test-Path -LiteralPath $managed)) { $managed = Join-Path $OriginalGame 'valheim_Data\Managed' }
foreach ($required in @((Join-Path $managed 'assembly_valheim.dll'), (Join-Path $BepInExCore '0Harmony.dll'), (Join-Path $OriginalGame 'MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll'))) {
    if (!(Test-Path -LiteralPath $required)) { throw "Required original input missing: $required" }
}
if ($RepairDll) { $RepairDll = (Resolve-Path -LiteralPath $RepairDll).Path } else { $RepairDll = '-' }
if (!$SkipBuild) {
    & dotnet build "$PSScriptRoot\ArtisanStorageChecks.csproj" -c Debug "-p:BepInExCore=$BepInExCore"
    if ($LASTEXITCODE) { throw 'Standalone harness build failed.' }
}
$exe = Join-Path $PSScriptRoot 'bin\Debug\net48\ArtisanStorageChecks.exe'
$reportDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ArtisanStorageChecks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $reportDirectory | Out-Null
$index = 0
foreach ($inputDll in $ArtisanDll) {
    $index++
    $inputDll = (Resolve-Path -LiteralPath $inputDll).Path
    $stdout = Join-Path $reportDirectory "$index.stdout.txt"
    $stderr = Join-Path $reportDirectory "$index.stderr.txt"
    $arguments = @('--embedded-mono', $OriginalGame, $managed, $BepInExCore, $inputDll, $RepairDll) | ForEach-Object { '"' + $_ + '"' }
    # Fresh disposable child per Artisan version; same-name assemblies cannot leak across versions.
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $reportDirectory -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        $null = $process.Handle
        if (!$process.WaitForExit(45000)) { $process.Kill(); $process.WaitForExit(); throw 'Mono checks timed out.' }
        Get-Content -LiteralPath $stdout, $stderr
        if ($process.ExitCode) { throw "Mono checks failed with exit code $($process.ExitCode). Reports: $reportDirectory" }
    }
    finally {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
Write-Host "Reports retained: $reportDirectory"
