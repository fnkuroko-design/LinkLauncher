[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateSet('Handshake', 'State')][string]$Suite = 'Handshake')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new check directory; preserve old results.' }
New-Item -ItemType Directory -Path $output | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($installation)) { throw 'MSVC installation not found.' }
$batch = Join-Path $output 'Run.cmd'
$lines = @(
    '@echo off',
    'setlocal DisableDelayedExpansion',
    'set VSCMD_SKIP_SENDTELEMETRY=1',
    'call "%LL_CHECK_VCVARS%" x64 >nul',
    'if errorlevel 1 exit /b %errorlevel%',
    'cd /d "%LL_CHECK_OUTPUT%"',
    'if errorlevel 1 exit /b %errorlevel%',
    'cl /nologo /TC /utf-8 /W4 /WX /I"%LL_CHECK_NATIVE%" "%LL_CHECK_SOURCE%" "%LL_CHECK_NATIVE%\BridgeState.c" "%LL_CHECK_NATIVE%\BridgeRequest.c" /Fe:HandshakeChecks.exe /link user32.lib',
    'if errorlevel 1 exit /b %errorlevel%',
    'HandshakeChecks.exe',
    'exit /b %errorlevel%'
)
[IO.File]::WriteAllLines($batch, $lines, [Text.Encoding]::ASCII)
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = $env:ComSpec
$info.Arguments = '/d /v:off /c ""%LL_CHECK_BATCH%""'
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.EnvironmentVariables['LL_CHECK_BATCH'] = $batch
$info.EnvironmentVariables['LL_CHECK_VCVARS'] = Join-Path $installation 'VC\Auxiliary\Build\vcvarsall.bat'
$info.EnvironmentVariables['LL_CHECK_OUTPUT'] = $output
$info.EnvironmentVariables['LL_CHECK_NATIVE'] = Join-Path $repo 'src\LinkLauncher.InputBridge'
$info.EnvironmentVariables['LL_CHECK_SOURCE'] = if ($Suite -eq 'State') {
    Join-Path $repo 'src\LinkLauncher.InputBridge\BridgeStateChecks.c'
} else { Join-Path $PSScriptRoot 'HandshakeChecks.c' }
$process = [Diagnostics.Process]::Start($info)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Isolated check process exceeded 30 seconds.' }
    $stdout.GetAwaiter().GetResult() | Write-Output
    $stderr.GetAwaiter().GetResult() | Write-Output
    if ($process.ExitCode -ne 0) { throw "Handshake checks failed: $($process.ExitCode)" }
} finally { $process.Dispose() }
