param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw (Join-Path $PSScriptRoot '../../src/Pins.MLAstro.Plugin/MLAstroPlugin.cs')
$names = 'ManualMoveAsync', 'SetAutomationAsync', 'RequireMotionSafe', 'ConfirmFreshReadyAsync', 'RecoverConnectionAsync', 'LatchMotionFailure', 'CancelManual', 'StopAsync'
$methods = foreach ($name in $names) {
    $match = [regex]::Match($source, '(?m)^    (?:public|private) (?:async )?[^\r\n]*\b' + $name + '\([^\r\n]*\)\s*\{')
    if (!$match.Success) { throw "Could not locate method $name" }
    $open = $source.IndexOf('{', $match.Index)
    $depth = 0
    $end = $open
    do {
        if ($source[$end] -eq '{') { $depth++ }
        if ($source[$end] -eq '}') { $depth-- }
        $end++
        if ($end -gt $source.Length) { throw "Unterminated method $name" }
    } while ($depth -gt 0)
    $source.Substring($match.Index, $end - $match.Index)
}
$property = [regex]::Match($source, '(?m)^    private bool MotionWorkQuiescent =>[^\r\n]+;')
if (!$property.Success) { throw 'Could not locate MotionWorkQuiescent' }
$generated = "// Generated from the actual plugin by run.ps1; do not edit.`npartial class PluginHarness {`n" + $property.Value + "`n" + ($methods -join "`n") + "`n}`n"
$extracted = Join-Path $PSScriptRoot 'ExtractedMethods.cs'
try {
    Set-Content -Path $extracted -Value $generated -Encoding utf8
    $project = Join-Path $PSScriptRoot 'Pins.MLAstro.SafetyChecks.csproj'
    & $Dotnet restore $project --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $Dotnet run --no-restore --project $project
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Remove-Item -LiteralPath $extracted -ErrorAction SilentlyContinue
}
