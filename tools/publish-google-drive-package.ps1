#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet("test", "release")]
    [string]$Channel = "test",

    [Parameter(Mandatory = $false)]
    [string]$Version = "",

    [Parameter(Mandatory = $false)]
    [string]$Description = "",

    [string]$ReleaseDate = (Get-Date -Format "yyyy-MM-dd"),

    [string]$SourceDir = "",

    [string]$Remote = "",

    [string]$Target = "",

    [string]$ProjectFolder = "",

    [string]$RclonePath = "rclone",

    [switch]$SkipAuthSetup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[publish] $Message"
}

function Get-AppName {
    return "MajiOCR.Windows"
}

function Get-ProjectFolderName {
    return "MajiOCR_Windows"
}

function Get-ReleaseFolderName {
    return "Release"
}

function Get-TestFolderName {
    return "Test"
}

function Resolve-FullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Join-DrivePath {
    param(
        [string]$RemoteName,
        [string[]]$Parts
    )

    $cleanRemote = $RemoteName.Trim()
    if (-not $cleanRemote.EndsWith(":", [StringComparison]::Ordinal)) {
        $cleanRemote += ":"
    }

    $cleanParts = $Parts | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        $_.Trim().Trim("/")
    }

    return $cleanRemote + ($cleanParts -join "/")
}

function Get-RemoteName {
    param([string]$RemoteName)
    return $RemoteName.Trim().TrimEnd(":")
}

function Resolve-RcloneExecutable {
    param([string]$RequestedPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates += $RequestedPath
    }

    $candidates += @(
        (Join-Path $PSScriptRoot "rclone\rclone.exe"),
        (Join-Path $PSScriptRoot "rclone.exe")
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }

        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
    }

    $installFromTools = Join-Path $PSScriptRoot "install-rclone.cmd"
    throw "rclone was not found. Please run .\tools\install-rclone.cmd or install rclone to PATH."
}

function Invoke-Rclone {
    param([string[]]$Arguments)

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $RclonePath
    $startInfo.Arguments = ConvertTo-ProcessArguments -Arguments $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo

    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        $details = (($stdout, $stderr) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join [Environment]::NewLine
        throw "rclone $($Arguments -join ' ') failed: $details"
    }

    return $stdout.Trim()
}

function ConvertTo-ProcessArguments {
    param([string[]]$Arguments)

    $quotedArguments = foreach ($item in $Arguments) {
        if ($null -eq $item) {
            '""'
            continue
        }

        $argument = [string]$item
        if ($argument -notmatch '[\s"]') {
            $argument
            continue
        }

        '"' + ($argument -replace '"', '\"') + '"'
    }

    return ($quotedArguments -join " ")
}

function Ensure-RcloneRemote {
    param(
        [string]$RemoteName,
        [string]$RootFolderId = ""
    )

    $name = Get-RemoteName $RemoteName
    $remotesOutput = Invoke-Rclone -Arguments @("listremotes")
    $remotes = $remotesOutput -split "(`r`n|`n|`r)" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $exists = $remotes | Where-Object { $_.Trim().TrimEnd(":") -eq $name } | Select-Object -First 1
    if ($exists) {
        return
    }

    if ($SkipAuthSetup) {
        throw "Missing rclone Google Drive remote: $name. Finish authorization first, then run this script again."
    }

    Write-Host ""
    Write-Host "Google Drive remote '$name' is not configured yet."
    Write-Host "The script will now launch Google Drive authorization in your browser."
    Write-Host "Please sign in to the Google account for folder ($RootFolderId) and grant permission."
    
    $configArgs = @("config", "create", $name, "drive", "scope", "drive", "config_is_local", "true")
    if (-not [string]::IsNullOrWhiteSpace($RootFolderId)) {
        $configArgs += @("root_folder_id", $RootFolderId)
    }

    try {
        Invoke-Rclone -Arguments $configArgs | Out-Null
    }
    catch {
        Write-Host ""
        Write-Host "Automatic authorization setup failed. Please run interactive setup manually:"
        Write-Host "  .\tools\rclone\rclone.exe config"
        throw
    }

    $remotesOutput = Invoke-Rclone -Arguments @("listremotes")
    $remotes = $remotesOutput -split "(`r`n|`n|`r)" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $exists = $remotes | Where-Object { $_.Trim().TrimEnd(":") -eq $name } | Select-Object -First 1
    if (-not $exists) {
        throw "Google Drive remote was not created successfully: $name"
    }
}

# --- Resolve Target & Remote ---
$rootFolderId = ""
$targetsConfigFile = Join-Path $PSScriptRoot "publish-targets.json"
if (Test-Path -LiteralPath $targetsConfigFile -PathType Leaf) {
    try {
        $targetsConfig = Get-Content -LiteralPath $targetsConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($Target) -and $targetsConfig.targets.$Target) {
            $targetObj = $targetsConfig.targets.$Target
            if ([string]::IsNullOrWhiteSpace($Remote) -and $targetObj.remote) {
                $Remote = $targetObj.remote
            }
            if ([string]::IsNullOrWhiteSpace($ProjectFolder) -and $targetObj.projectFolder) {
                $ProjectFolder = $targetObj.projectFolder
            }
            if ($targetObj.rootFolderId) {
                $rootFolderId = $targetObj.rootFolderId
            }
        }
        elseif ([string]::IsNullOrWhiteSpace($Remote) -and $targetsConfig.defaultTarget -and $targetsConfig.targets.$($targetsConfig.defaultTarget)) {
            $defaultTargetObj = $targetsConfig.targets.$($targetsConfig.defaultTarget)
            $Remote = $defaultTargetObj.remote
            if ([string]::IsNullOrWhiteSpace($ProjectFolder) -and $defaultTargetObj.projectFolder) {
                $ProjectFolder = $defaultTargetObj.projectFolder
            }
            if ($defaultTargetObj.rootFolderId) {
                $rootFolderId = $defaultTargetObj.rootFolderId
            }
        }
    }
    catch {
        Write-Warning "Failed to parse publish-targets.json: $_"
    }
}

if ([string]::IsNullOrWhiteSpace($Remote)) {
    $Remote = $env:GOOGLE_DRIVE_RCLONE_REMOTE
}
if ([string]::IsNullOrWhiteSpace($Remote)) {
    $Remote = "gdrive"
}

if ([string]::IsNullOrWhiteSpace($ProjectFolder)) {
    $ProjectFolder = Get-ProjectFolderName
}

# --- Resolve Version ---
if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionFile = Join-Path $PSScriptRoot "..\VERSION.txt"
    if (Test-Path -LiteralPath $versionFile -PathType Leaf) {
        $content = Get-Content -LiteralPath $versionFile -Raw -Encoding UTF8
        $match = [regex]::Match($content, "Version:\s*(?<version>[0-9A-Za-z.\-+]+)")
        if ($match.Success) {
            $Version = $match.Groups["version"].Value
        }
    }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Version is required. Specify -Version or define it in VERSION.txt"
}

if ([string]::IsNullOrWhiteSpace($Description)) {
    $Description = if ($Channel -eq "release") { "MajiOCR.Windows Release v$Version" } else { "MajiOCR.Windows Test v$Version" }
}

# --- Resolve SourceDir ---
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $candidatePaths = @(
        (Join-Path $PSScriptRoot "..\MajiOCR.Windows\bin\Release\net10.0-windows10.0.26100.0\win-x64"),
        (Join-Path $PSScriptRoot "..\MajiOCR.Windows\bin\Release\net10.0-windows10.0.26100.0"),
        (Join-Path $PSScriptRoot "..\MajiOCR.Windows\bin\Debug\net10.0-windows10.0.26100.0\win-x64")
    )
    foreach ($cp in $candidatePaths) {
        if (Test-Path -LiteralPath $cp -PathType Container) {
            $SourceDir = $cp
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($SourceDir) -or -not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
    throw "Source folder not found. Please build the project or specify -SourceDir"
}

$resolvedSource = Resolve-FullPath $SourceDir
$appName = Get-AppName
$exePath = Join-Path $resolvedSource "$appName.exe"
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "Source folder is missing main executable: $exePath"
}

$RclonePath = Resolve-RcloneExecutable -RequestedPath $RclonePath
Ensure-RcloneRemote -RemoteName $Remote -RootFolderId $rootFolderId

$rootDir = Resolve-FullPath (Join-Path $PSScriptRoot "..")
$workDir = Join-Path $rootDir "tmp\publish-google-drive"
$stageDir = Join-Path $workDir "stage"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$targetFolder = if ($Channel -eq "release") { Get-ReleaseFolderName } else { Get-TestFolderName }
$zipName = if ($Channel -eq "release") {
    "${appName}_v$Version.zip"
} else {
    "${appName}_v$Version-test_$timestamp.zip"
}
$zipPath = Join-Path $workDir $zipName
$manifestPath = Join-Path $workDir "latest.json"

Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
New-Item -ItemType Directory -Force -Path $workDir | Out-Null
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue

Write-Step "Staging application files from: $resolvedSource"

Get-ChildItem -LiteralPath $resolvedSource | Where-Object {
    $_.Name -notin @(
        "card_manager.db", "card_manager.db-wal", "card_manager.db-shm", "appsettings.json", "UpdateSettings.json", "logs", "updates", "CardImages",
        "contacts.sqlite3", "contacts.sqlite3-wal", "contacts.sqlite3-shm", "contacts.sqlite3-journal", "card-images", "versions", ".backup_snapshot", ".pending", "google_oauth_config.json", "manifest.json"
    ) -and
    $_.Extension -notin @(".pdb", ".xml", ".msix", ".zip")
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stageDir $_.Name) -Recurse -Force
}

$rootVersion = Join-Path $rootDir "VERSION.txt"
if (Test-Path -LiteralPath $rootVersion) {
    Copy-Item -LiteralPath $rootVersion -Destination (Join-Path $stageDir "VERSION.txt") -Force
}
$rootChangeLog = Join-Path $rootDir "CHANGELOG.txt"
if (Test-Path -LiteralPath $rootChangeLog) {
    Copy-Item -LiteralPath $rootChangeLog -Destination (Join-Path $stageDir "CHANGELOG.txt") -Force
}

$forbidden = Get-ChildItem -LiteralPath $stageDir -Recurse -Force | Where-Object {
    ($_.PSIsContainer -and $_.Name -in @("logs", "updates", "CardImages", "card-images", "versions", ".backup_snapshot", ".pending")) -or
    (-not $_.PSIsContainer -and (
        $_.Name -in @("card_manager.db", "card_manager.db-wal", "card_manager.db-shm", "appsettings.json", "UpdateSettings.json", "contacts.sqlite3", "contacts.sqlite3-wal", "contacts.sqlite3-shm", "contacts.sqlite3-journal", "google_oauth_config.json") -or
        $_.Extension.Equals(".log", [StringComparison]::OrdinalIgnoreCase)
    ))
}
if ($forbidden) {
    $names = ($forbidden | Select-Object -ExpandProperty FullName) -join ", "
    throw "Staging folder contains forbidden items: $names"
}

Write-Step "Creating ZIP package: $zipName"
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -Force
$sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
$zipSize = (Get-Item -LiteralPath $zipPath).Length

Write-Step "Package created. Size: $zipSize bytes, SHA256: $sha256"

$driveFolder = Join-DrivePath -RemoteName $Remote -Parts @($ProjectFolder, $targetFolder)
Write-Step "Target Google Drive Folder: $driveFolder"

Invoke-Rclone -Arguments @("mkdir", $driveFolder) | Out-Null

Write-Step "Uploading ZIP package..."
Invoke-Rclone -Arguments @("copyto", $zipPath, "$driveFolder/$zipName") | Out-Null

Write-Step "Generating public share link for ZIP..."
$packageUrl = Invoke-Rclone -Arguments @("link", "$driveFolder/$zipName")

$changeLogUrl = ""
if (Test-Path -LiteralPath $rootChangeLog) {
    Write-Step "Uploading CHANGELOG.txt..."
    Invoke-Rclone -Arguments @("copyto", $rootChangeLog, "$driveFolder/CHANGELOG.txt") | Out-Null
    $changeLogUrl = Invoke-Rclone -Arguments @("link", "$driveFolder/CHANGELOG.txt")
}

$manifestObj = [ordered]@{
    version = $Version
    releaseDate = $ReleaseDate
    type = $Channel
    description = $Description
    packageUrl = $packageUrl
    changeLogUrl = $changeLogUrl
    sha256 = $sha256
    fileName = $zipName
}
$manifestJson = $manifestObj | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $manifestJson, [System.Text.Encoding]::UTF8)

Write-Step "Uploading latest.json..."
Invoke-Rclone -Arguments @("copyto", $manifestPath, "$driveFolder/latest.json") | Out-Null
$latestJsonUrl = Invoke-Rclone -Arguments @("link", "$driveFolder/latest.json")

Write-Host ""
Write-Host "=================================================="
Write-Host "Publish Complete!"
Write-Host "  Remote: $Remote"
Write-Host "  Drive Folder: $driveFolder"
Write-Host "  Version: $Version ($Channel)"
Write-Host "  Package URL: $packageUrl"
Write-Host "  latest.json URL: $latestJsonUrl"
Write-Host "  SHA256: $sha256"
Write-Host "=================================================="
