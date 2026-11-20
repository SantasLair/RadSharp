<#
.SYNOPSIS
  Measures start-up (process start to first presented frame) of a published RadSharp app.
.DESCRIPTION
  Runs the exe with RADSHARP_STARTUP_TRACE=1 and reads the "startup: N ms" line the SDL host prints to stderr on the
  first present. Cold = first run of a fresh copy of the publish folder (new files: no file cache, first-run virus
  scan; no admin needed); warm = median of -Runs runs afterwards. Each process is stopped by its own PID.
.EXAMPLE
  ./tools/measure-startup.ps1 artifacts/size/s-Hello/Hello.exe
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$Runs = 5,
    [int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
$src = Split-Path (Resolve-Path $Exe) -Parent
$fresh = Join-Path ([IO.Path]::GetTempPath()) ("radsharp-startup-" + [guid]::NewGuid().ToString('N'))
Copy-Item $src $fresh -Recurse
$target = Join-Path $fresh (Split-Path $Exe -Leaf)

function Measure-Once([string]$path) {
    $psi = [Diagnostics.ProcessStartInfo]::new($path)
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables['RADSHARP_STARTUP_TRACE'] = '1'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    try {
        $task = $p.StandardError.ReadLineAsync()
        if (-not $task.Wait($TimeoutSeconds * 1000)) { throw "no startup line within $TimeoutSeconds s" }
        $wall = $sw.Elapsed.TotalMilliseconds
        if ($task.Result -notmatch 'startup: (\d+) ms') { throw "unexpected output: $($task.Result)" }
        [pscustomobject]@{ InApp = [int]$Matches[1]; Wall = [int]$wall }
    }
    finally {
        if (-not $p.HasExited) { $p.Kill() }
        $p.WaitForExit()
        $p.Dispose()
    }
}

try {
    $cold = Measure-Once $target
    $warm = 1..$Runs | ForEach-Object { Start-Sleep -Milliseconds 500; Measure-Once $target }
    $sorted = $warm.InApp | Sort-Object
    $median = $sorted[[int][math]::Floor($sorted.Count / 2)]
    "{0}: cold {1} ms (harness wall {2}); warm median {3} ms [{4}]" -f (Split-Path $Exe -Leaf), $cold.InApp, $cold.Wall, $median, ($warm.InApp -join ', ')
}
finally {
    Remove-Item $fresh -Recurse -Force -ErrorAction SilentlyContinue
}
