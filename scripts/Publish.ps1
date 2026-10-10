[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\LinkLauncher\LinkLauncher.csproj'
$projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8)
$versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw 'LinkLauncher.csproj に Version が設定されていません。'
}

$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') {
    throw "成果物名に使えない Version です: $version"
}

$releaseName = "LinkLauncher-v$version-win-x64"
$releaseRoot = Join-Path $repoRoot 'artifacts\releases'
$finalDirectory = Join-Path $releaseRoot $releaseName
$finalZip = Join-Path $releaseRoot "$releaseName.zip"
$finalHash = "$finalZip.sha256"

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
foreach ($path in @($finalDirectory, $finalZip, $finalHash)) {
    if (Test-Path -LiteralPath $path) {
        throw "既存の成果物を保護するため中止しました: $path"
    }
}

$guid = [Guid]::NewGuid().ToString('N')
$stageDirectory = Join-Path $releaseRoot ".$releaseName.$guid.staging"
$stageAppDirectory = Join-Path $stageDirectory 'LinkLauncher'
$stageZip = Join-Path $releaseRoot ".$releaseName.$guid.zip"
$stageHash = "$stageZip.sha256"

try {
    $null = New-Item -ItemType Directory -Path $stageAppDirectory -Force
    $dotnet = Get-Command dotnet -ErrorAction Stop
    & $dotnet.Source publish $projectPath -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=true -p:InputProbe=false -o $stageAppDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish が終了コード $LASTEXITCODE で失敗しました。"
    }

    $requiredPublishFiles = @(
        'LinkLauncher.exe',
        'LinkLauncher.dll',
        'LinkLauncher.deps.json',
        'LinkLauncher.runtimeconfig.json',
        'LinkLauncher.MouseHook.x64.dll',
        'LinkLauncher.MouseHook.x86.dll',
        'LinkLauncher.MouseHookHost.x86.exe'
    )
    foreach ($name in $requiredPublishFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stageAppDirectory $name) -PathType Leaf)) {
            throw "publish 出力に必要なファイルがありません: $name"
        }
    }

    $packageFiles = @(
        @{ Source = 'LICENSE'; Destination = 'LICENSE' },
        @{ Source = 'THIRD_PARTY_NOTICES.md'; Destination = 'THIRD_PARTY_NOTICES.md' },
        @{ Source = 'README.md'; Destination = 'README.md' },
        @{ Source = 'packaging\START_HERE.html'; Destination = 'START_HERE.html' }
        @{ Source = 'licenses\DOTNET-LICENSE.txt'; Destination = 'licenses\DOTNET-LICENSE.txt' }
    )
    foreach ($file in $packageFiles) {
        $source = Join-Path $repoRoot $file.Source
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "配布に必要なファイルがありません: $source"
        }
        $destination = Join-Path $stageAppDirectory $file.Destination
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stageDirectory,
        $stageZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )
    $sha256 = (Get-FileHash -LiteralPath $stageZip -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        $stageHash,
        "$sha256  $releaseName.zip$([Environment]::NewLine)",
        [System.Text.Encoding]::ASCII
    )

    # 既存成果物を置き換えないよう、移動時も上書きしない。
    [System.IO.File]::Move($stageZip, $finalZip)
    [System.IO.File]::Move($stageHash, $finalHash)
    [System.IO.Directory]::Move($stageDirectory, $finalDirectory)

    Write-Host "配布物を作成しました: $finalDirectory"
    Write-Host "ZIP: $finalZip"
    Write-Host "SHA-256: $finalHash"
}
finally {
    # Remove only this invocation's uniquely named staging paths under artifacts/releases.
    $releaseRootFull = [System.IO.Path]::GetFullPath($releaseRoot).TrimEnd('\') + '\'
    foreach ($temporaryPath in @($stageDirectory, $stageZip, $stageHash)) {
        if (-not (Test-Path -LiteralPath $temporaryPath)) {
            continue
        }
        $temporaryPathFull = [System.IO.Path]::GetFullPath($temporaryPath)
        $temporaryName = [System.IO.Path]::GetFileName($temporaryPathFull)
        if ($temporaryPathFull.StartsWith($releaseRootFull, [System.StringComparison]::OrdinalIgnoreCase) -and
            $temporaryName.Contains($guid)) {
            Remove-Item -LiteralPath $temporaryPathFull -Recurse -Force
        }
    }
}
