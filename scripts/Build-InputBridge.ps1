[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\LinkLauncher\obj\InputBridge')
)

$ErrorActionPreference = 'Stop'

function ConvertTo-BatchPath([string]$Path)
{
    # Paths are quoted in the generated batch file. Doubling percent signs keeps
    # literal percent characters from being treated as environment variables.
    return '"' + $Path.Replace('%', '%%') + '"'
}

function Get-HashText([string]$Text)
{
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try
    {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([System.BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '')
    }
    finally
    {
        $sha256.Dispose()
    }
}

function Get-HashFile([string]$Path)
{
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try
    {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '')
    }
    finally
    {
        $stream.Dispose()
        $sha256.Dispose()
    }
}

function Invoke-BuildBatch([string]$BatchPath)
{
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $env:ComSpec
    $startInfo.Arguments = '/d /v:off /c ""{0}""' -f $BatchPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $buildCommand = [System.Diagnostics.Process]::Start($startInfo)
    try
    {
        $standardOutput = $buildCommand.StandardOutput.ReadToEndAsync()
        $standardError = $buildCommand.StandardError.ReadToEndAsync()
        $buildCommand.WaitForExit()
        $outputText = $standardOutput.GetAwaiter().GetResult()
        $errorText = $standardError.GetAwaiter().GetResult()
        if ($outputText.Length -ne 0) { Write-Host $outputText.TrimEnd() }
        if ($errorText.Length -ne 0) { Write-Host $errorText.TrimEnd() }
        return $buildCommand.ExitCode
    }
    finally { $buildCommand.Dispose() }
}

function Invoke-VcBuild([string]$Architecture, [string]$VcVarsAll, [string]$RepositoryRoot, [string]$StageDirectory)
{
    $sourceDirectory = Join-Path $RepositoryRoot 'src\LinkLauncher.InputBridge'
    $objectDirectory = Join-Path $StageDirectory $Architecture
    New-Item -ItemType Directory -Path $objectDirectory -Force | Out-Null

    $machine = if ($Architecture -eq 'x64') { 'x64' } else { 'x86' }
    $dllName = "LinkLauncher.MouseHook.$Architecture.dll"
    $dllEntry = if ($Architecture -eq 'x64') { 'DllMain' } else { 'DllMain@12' }
    $definitionFile = Join-Path $sourceDirectory "BridgeHook.$Architecture.def"
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('@echo off')
    $lines.Add('setlocal DisableDelayedExpansion')
    $lines.Add('set VSCMD_SKIP_SENDTELEMETRY=1')
    $lines.Add(('call {0} {1} >nul' -f (ConvertTo-BatchPath $VcVarsAll), $Architecture))
    $lines.Add('if errorlevel 1 exit /b %errorlevel%')
    $lines.Add(('cd /d {0}' -f (ConvertTo-BatchPath $RepositoryRoot)))
    $lines.Add('if errorlevel 1 exit /b %errorlevel%')

    $compilerFlags = '/nologo /c /TC /utf-8 /W4 /WX /O1 /GS- /Zl /Oi- /Ob0 /DWIN32_LEAN_AND_MEAN /DUNICODE /D_UNICODE'
    foreach ($unit in @('BridgeHook.c', 'BridgeState.c', 'BridgeMemory.c'))
    {
        $objectFile = Join-Path $objectDirectory ([System.IO.Path]::ChangeExtension($unit, '.obj'))
        $sourceFile = Join-Path $sourceDirectory $unit
        $lines.Add(('cl {0} /Fo{1} {2}' -f $compilerFlags, (ConvertTo-BatchPath $objectFile), (ConvertTo-BatchPath $sourceFile)))
        $lines.Add('if errorlevel 1 exit /b %errorlevel%')
    }

    $dllPath = Join-Path $StageDirectory $dllName
    $importLibrary = Join-Path $StageDirectory ([System.IO.Path]::ChangeExtension($dllName, '.lib'))
    $exportFile = Join-Path $StageDirectory ([System.IO.Path]::ChangeExtension($dllName, '.exp'))
    $hookObject = Join-Path $objectDirectory 'BridgeHook.obj'
    $stateObject = Join-Path $objectDirectory 'BridgeState.obj'
    $memoryObject = Join-Path $objectDirectory 'BridgeMemory.obj'
    $lines.Add(('link /nologo /dll /machine:{0} /nodefaultlib /entry:{1} /dynamicbase /nxcompat /opt:ref /opt:icf /out:{2} /implib:{3} /def:{4} {5} {6} {7} user32.lib kernel32.lib' -f `
        $machine, $dllEntry, (ConvertTo-BatchPath $dllPath), (ConvertTo-BatchPath $importLibrary), `
        (ConvertTo-BatchPath $definitionFile), (ConvertTo-BatchPath $hookObject), (ConvertTo-BatchPath $stateObject), (ConvertTo-BatchPath $memoryObject)))
    $lines.Add('if errorlevel 1 exit /b %errorlevel%')
    $lines.Add('exit /b 0')

    $batchPath = Join-Path $StageDirectory "Build-$Architecture.cmd"
    [System.IO.File]::WriteAllLines($batchPath, $lines, [System.Text.Encoding]::ASCII)
    $exitCode = Invoke-BuildBatch $batchPath
    if ($exitCode -ne 0)
    {
        throw "VC build failed for $Architecture (exit code $exitCode). See the compiler output above."
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceDirectory = Join-Path $repositoryRoot 'src\LinkLauncher.InputBridge'
if ([System.IO.Path]::IsPathRooted($OutputDirectory))
{
    $outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else
{
    $outputPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $OutputDirectory))
}

$requiredFiles = @(
    'BridgeHook.c',
    'BridgeHook.h',
    'BridgeHook.x64.def',
    'BridgeHook.x86.def',
    'BridgeHookHost.c',
    'BridgeState.c',
    'BridgeState.h',
    'BridgeMemory.c'
)
foreach ($file in $requiredFiles)
{
    $path = Join-Path $sourceDirectory $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Required native source file is missing: $path"
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf))
{
    throw "Visual Studio Installer vswhere.exe was not found: $vswhere"
}

$vswhereOutput = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vswhereExitCode = $LASTEXITCODE
$installationPath = [string]($vswhereOutput | Select-Object -First 1)
if ($vswhereExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($installationPath))
{
    throw 'Visual Studio C++ Build Tools (VC.Tools.x86.x64) were not found.'
}
$installationPath = $installationPath.Trim()

$vcVarsAll = Join-Path $installationPath 'VC\Auxiliary\Build\vcvarsall.bat'
$toolsetRoot = Join-Path $installationPath 'VC\Tools\MSVC'
if (-not (Test-Path -LiteralPath $vcVarsAll -PathType Leaf) -or -not (Test-Path -LiteralPath $toolsetRoot -PathType Container))
{
    throw "The selected Visual Studio installation has no usable VC toolset: $installationPath"
}
$toolset = Get-ChildItem -LiteralPath $toolsetRoot -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if ($null -eq $toolset)
{
    throw "No installed MSVC toolset was found under $toolsetRoot"
}
foreach ($compilerPath in @(
    (Join-Path $toolset.FullName 'bin\Hostx64\x64\cl.exe'),
    (Join-Path $toolset.FullName 'bin\Hostx64\x86\cl.exe')
))
{
    if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf))
    {
        throw "The selected MSVC toolset is missing a required compiler: $compilerPath"
    }
}

$inputFiles = @($PSCommandPath) + @($requiredFiles | ForEach-Object { Join-Path $sourceDirectory $_ })
$hashParts = foreach ($file in $inputFiles)
{
    $hash = Get-HashFile $file
    '{0}={1}' -f [System.IO.Path]::GetFileName($file), $hash
}
$hashParts += 'Toolset={0}' -f $toolset.FullName
$inputHash = Get-HashText (($hashParts | Sort-Object) -join "`n")

$outputs = @(
    'LinkLauncher.MouseHook.x64.dll',
    'LinkLauncher.MouseHook.x86.dll',
    'LinkLauncher.MouseHookHost.x86.exe'
)
$cachePath = Join-Path $outputPath 'InputBridge.build-cache.json'
$allOutputsExist = $true
foreach ($name in $outputs)
{
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath $name) -PathType Leaf))
    {
        $allOutputsExist = $false
        break
    }
}
$cachedHash = $null
if (Test-Path -LiteralPath $cachePath -PathType Leaf)
{
    try { $cachedHash = (Get-Content -LiteralPath $cachePath -Raw | ConvertFrom-Json).InputHash }
    catch { $cachedHash = $null }
}
if ($allOutputsExist -and $cachedHash -eq $inputHash)
{
    Write-Host 'Native input bridge is up to date.'
    exit 0
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$stagePath = [System.IO.Path]::GetFullPath((Join-Path $outputPath ('.build-' + [Guid]::NewGuid().ToString('N'))))
$outputPrefix = $outputPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $stagePath.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase))
{
    throw "Refusing to create a build staging directory outside the output directory: $stagePath"
}

try
{
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    Invoke-VcBuild -Architecture 'x64' -VcVarsAll $vcVarsAll -RepositoryRoot $repositoryRoot -StageDirectory $stagePath
    Invoke-VcBuild -Architecture 'x86' -VcVarsAll $vcVarsAll -RepositoryRoot $repositoryRoot -StageDirectory $stagePath

    $hostObjectDirectory = Join-Path $stagePath 'host-x86'
    New-Item -ItemType Directory -Path $hostObjectDirectory -Force | Out-Null
    $hostObject = Join-Path $hostObjectDirectory 'BridgeHookHost.obj'
    $hostSource = Join-Path $sourceDirectory 'BridgeHookHost.c'
    $hostPath = Join-Path $stagePath 'LinkLauncher.MouseHookHost.x86.exe'
    $hostMemoryObject = Join-Path $stagePath 'x86\BridgeMemory.obj'
    $hostBatchPath = Join-Path $stagePath 'Build-Host-x86.cmd'
    $hostLines = @(
        '@echo off',
        'setlocal DisableDelayedExpansion',
        'set VSCMD_SKIP_SENDTELEMETRY=1',
        ('call {0} x86 >nul' -f (ConvertTo-BatchPath $vcVarsAll)),
        'if errorlevel 1 exit /b %errorlevel%',
        ('cd /d {0}' -f (ConvertTo-BatchPath $repositoryRoot)),
        'if errorlevel 1 exit /b %errorlevel%',
        ('cl /nologo /c /TC /utf-8 /W4 /WX /O1 /GS- /Zl /Oi- /Ob0 /DWIN32_LEAN_AND_MEAN /DUNICODE /D_UNICODE /Fo{0} {1}' -f (ConvertTo-BatchPath $hostObject), (ConvertTo-BatchPath $hostSource)),
        'if errorlevel 1 exit /b %errorlevel%',
        ('link /nologo /subsystem:windows /machine:x86 /nodefaultlib /entry:BridgeHostEntry /dynamicbase /nxcompat /opt:ref /opt:icf /out:{0} {1} {2} user32.lib kernel32.lib' -f (ConvertTo-BatchPath $hostPath), (ConvertTo-BatchPath $hostObject), (ConvertTo-BatchPath $hostMemoryObject)),
        'if errorlevel 1 exit /b %errorlevel%',
        'exit /b 0'
    )
    [System.IO.File]::WriteAllLines($hostBatchPath, $hostLines, [System.Text.Encoding]::ASCII)
    $hostExitCode = Invoke-BuildBatch $hostBatchPath
    if ($hostExitCode -ne 0)
    {
        throw "VC build failed for the x86 host (exit code $hostExitCode). See the compiler output above."
    }

    foreach ($name in $outputs)
    {
        $builtPath = Join-Path $stagePath $name
        if (-not (Test-Path -LiteralPath $builtPath -PathType Leaf))
        {
            throw "The native build did not produce the expected output: $builtPath"
        }
    }
    foreach ($name in $outputs)
    {
        Move-Item -LiteralPath (Join-Path $stagePath $name) -Destination (Join-Path $outputPath $name) -Force
    }

    $cache = [ordered]@{
        InputHash = $inputHash
        Toolset = $toolset.FullName
        Outputs = $outputs
    } | ConvertTo-Json -Depth 3
    [System.IO.File]::WriteAllText($cachePath, $cache, [System.Text.Encoding]::UTF8)
    Write-Host "Built native input bridge into $outputPath"
}
finally
{
    $resolvedStage = [System.IO.Path]::GetFullPath($stagePath)
    if ($resolvedStage.StartsWith($outputPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedStage -PathType Container))
    {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
