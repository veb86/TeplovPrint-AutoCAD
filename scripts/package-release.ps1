param(
    [string]$Version = "1.0.7",
    [string]$LegacyAcadOutput,
    [string]$CoreAcadOutput,
    [string]$NanoCadOutput
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+(?:\.\d+){0,2}$') {
    throw "Version must look like 1.15.7.1, 1.15.6 or 1.16."
}

$root = Split-Path -Parent $PSScriptRoot
$releaseBase = Join-Path $root "release"
$releaseName = "v$Version"
$releaseRoot = Join-Path $releaseBase $releaseName
$stagingRoot = Join-Path $releaseBase ("._{0}-{1}" -f $releaseName, $PID)

function Resolve-BuildOutput {
    param(
        [string]$Candidate,
        [Parameter(Mandatory = $true)][string]$DefaultRelativePath
    )


    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return Join-Path $root $DefaultRelativePath
    }

    if ([System.IO.Path]::IsPathRooted($Candidate)) {
        return [System.IO.Path]::GetFullPath($Candidate)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $root $Candidate))
}

if (Test-Path -LiteralPath $releaseRoot) {
    throw "Release already exists: $releaseRoot"
}

New-Item -ItemType Directory -Force -Path $releaseBase | Out-Null
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

$separators = [char[]]@('\', '/')

function Test-ForeignRuntime {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    # Only Windows x64 native/runtime assets are needed by AutoCAD and nanoCAD.
    $parts = $RelativePath.Split($separators, [System.StringSplitOptions]::RemoveEmptyEntries)
    return $parts.Length -ge 2 -and $parts[0] -eq 'runtimes' -and @('win', 'win-x64') -notcontains $parts[1]
}

function Copy-ReleaseTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Build output was not found: $Source"
    }

    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path.TrimEnd($separators)
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    $sourceItems = Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force
    foreach ($sourceItem in $sourceItems) {

        if (-not $sourceItem.PSIsContainer -and @('.pdb', '.log', '.err') -contains $sourceItem.Extension) {
            continue
        }

        $relative = $sourceItem.FullName.Substring($sourceRoot.Length).TrimStart($separators)
        if (Test-ForeignRuntime -RelativePath $relative) {
            continue
        }

        $target = Join-Path $Destination $relative
        if ($sourceItem.PSIsContainer) {
            New-Item -ItemType Directory -Force -Path $target | Out-Null
        }
        else {
            $targetParent = Split-Path -Parent $target
            New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
            Copy-Item -LiteralPath $sourceItem.FullName -Destination $target -Force
        }
    }
}

function Add-ReleasePackage {
    param(
        [Parameter(Mandatory = $true)][string]$FolderName,
        [Parameter(Mandatory = $true)][string]$ArchiveName,
        [Parameter(Mandatory = $true)][string]$BuildOutput,
        [Parameter(Mandatory = $true)][string]$MainDll
    )

    $dllPath = Join-Path $BuildOutput $MainDll
    if (-not (Test-Path -LiteralPath $dllPath)) {
        throw "Expected release DLL was not found: $dllPath"
    }

    $packageRoot = Join-Path $stagingRoot $FolderName
    Copy-ReleaseTree -Source $BuildOutput -Destination $packageRoot

    $archivePath = Join-Path $stagingRoot $ArchiveName
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
}

function Add-ManualInstallPackage {
    param(
        [Parameter(Mandatory = $true)][string]$ArchiveName,
        [Parameter(Mandatory = $true)][string]$LegacyAcadOutput,
        [Parameter(Mandatory = $true)][string]$CoreAcadOutput,
        [Parameter(Mandatory = $true)][string]$NanoCadOutput
    )

    # The archive mirrors %USERPROFILE%: everything is installed into the current user's
    # %APPDATA% and does not require administrator rights.
    $packageRoot = Join-Path $stagingRoot 'manual-install'
    $roaming = Join-Path (Join-Path $packageRoot 'AppData') 'Roaming'
    $bundle = [System.IO.Path]::Combine($roaming, 'Autodesk', 'ApplicationPlugins', 'TeplovPrint.bundle')
    $nanoCad = [System.IO.Path]::Combine($roaming, 'TeplovPrint', 'nanoCAD24')

    $inputs = @(
        @{ Output = $LegacyAcadOutput; Dll = 'AcadBatchPlot.dll'; Destination = [System.IO.Path]::Combine($bundle, 'Contents', 'AutoCAD2015-2024') },
        @{ Output = $CoreAcadOutput; Dll = 'AcadBatchPlot.Core.dll'; Destination = [System.IO.Path]::Combine($bundle, 'Contents', 'AutoCAD2025-2027') },
        @{ Output = $NanoCadOutput; Dll = 'NcadBatchPlot.dll'; Destination = $nanoCad }
    )
    foreach ($item in $inputs) {
        $dllPath = Join-Path $item.Output $item.Dll
        if (-not (Test-Path -LiteralPath $dllPath)) {
            throw "Expected release DLL was not found: $dllPath"
        }

        Copy-ReleaseTree -Source $item.Output -Destination $item.Destination
    }

    Copy-Item -LiteralPath ([System.IO.Path]::Combine($root, 'installer_acad', 'PackageContents.xml')) -Destination $bundle -Force
    foreach ($file in @('INSTALL.md', 'install.cmd', 'uninstall.cmd')) {
        Copy-Item -LiteralPath ([System.IO.Path]::Combine($root, 'installer_manual', $file)) -Destination $packageRoot -Force
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $packageRoot -Force

    $archivePath = Join-Path $stagingRoot $ArchiveName
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
}

try {
    $legacyAcadOutput = Resolve-BuildOutput -Candidate $LegacyAcadOutput -DefaultRelativePath 'bin-acad'
    $coreAcadOutput = Resolve-BuildOutput -Candidate $CoreAcadOutput -DefaultRelativePath 'bin-acad2025-2027'
    $nanoCadOutput = Resolve-BuildOutput -Candidate $NanoCadOutput -DefaultRelativePath 'bin-nanocad24'

    Add-ReleasePackage `
        -FolderName 'AutoCAD2015-2024' `
        -ArchiveName "Teplov-Print-AutoCAD2015-2024-v$Version.zip" `
        -BuildOutput $legacyAcadOutput `
        -MainDll 'AcadBatchPlot.dll'


    Add-ReleasePackage `
        -FolderName 'AutoCAD2025-2027' `
        -ArchiveName "Teplov-Print-AutoCAD2025-2027-v$Version.zip" `
        -BuildOutput $coreAcadOutput `
        -MainDll 'AcadBatchPlot.Core.dll'

    Add-ReleasePackage `
        -FolderName 'nanoCAD24' `
        -ArchiveName "Teplov-Print-nanoCAD24-v$Version.zip" `
        -BuildOutput $nanoCadOutput `
        -MainDll 'NcadBatchPlot.dll'

    Add-ManualInstallPackage `
        -ArchiveName "Teplov-Print-manual-install-v$Version.zip" `
        -LegacyAcadOutput $legacyAcadOutput `
        -CoreAcadOutput $coreAcadOutput `
        -NanoCadOutput $nanoCadOutput

    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $stagingRoot -Force
    Get-ChildItem -LiteralPath $root -File -Filter '*.mp4' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stagingRoot -Force
    }

    $manifestLines = @(
        "Teplov Print release v$Version",
        "GeneratedAt=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
        "",
        "Archives (SHA256):"
    )
    Get-ChildItem -LiteralPath $stagingRoot -File -Filter '*.zip' | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $manifestLines += "$hash  $($_.Name)"
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $stagingRoot 'release-manifest.txt'),
        $manifestLines,
        [System.Text.UTF8Encoding]::new($false))

    Move-Item -LiteralPath $stagingRoot -Destination $releaseRoot
    Write-Host "Local release created: $releaseRoot" -ForegroundColor Green
}
catch {
    if (Test-Path -LiteralPath $stagingRoot) {

        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    throw
}
