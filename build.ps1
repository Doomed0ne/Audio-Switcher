[CmdletBinding()]
param(
    # Increment 1.2.29 to 1.2.30, build and verify that package, then persist 1.2.30.
    [switch]$BumpPatch
)

$ErrorActionPreference = "Stop"

# Configuration
$pluginName = "AudioSwitcher"
$projectPath = Join-Path "src" "AudioSwitcherPlugin.csproj"
$manifestPath = Join-Path "src" (Join-Path "package" (Join-Path "metadata" "LoupedeckPackage.yaml"))
$releaseDir = Join-Path "bin" "Release"
$stagingDir = "dist"

$manifestContent = [System.IO.File]::ReadAllText($manifestPath)
$projectContent = [System.IO.File]::ReadAllText($projectPath)
$manifestVersionMatches = [regex]::Matches($manifestContent, '(?m)^[ \t]*version:[ \t]*(?<version>\d+\.\d+\.\d+)[ \t]*$')
if ($manifestVersionMatches.Count -ne 1) {
    throw "Expected exactly one three-part version in $manifestPath; found $($manifestVersionMatches.Count)."
}
$currentVersion = $manifestVersionMatches[0].Groups['version'].Value

function Get-ProjectVersion([string]$content, [string]$tagName, [string]$filePath) {
    $matches = [regex]::Matches($content, "<$tagName>(?<version>[^<]+)</$tagName>")
    if ($matches.Count -ne 1) {
        throw "Expected exactly one <$tagName> in $filePath; found $($matches.Count)."
    }
    return $matches[0].Groups['version'].Value
}

function Replace-ProjectVersion([string]$content, [string]$tagName, [string]$oldVersion, [string]$newVersion, [string]$filePath) {
    $pattern = "(<$tagName>)$([regex]::Escape($oldVersion))(</$tagName>)"
    $matches = [regex]::Matches($content, $pattern)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one <$tagName>$oldVersion</$tagName> in $filePath; found $($matches.Count)."
    }
    return [regex]::Replace($content, $pattern, ('${1}' + $newVersion + '${2}'))
}

$projectVersion = Get-ProjectVersion $projectContent 'Version' $projectPath
$assemblyVersion = Get-ProjectVersion $projectContent 'AssemblyVersion' $projectPath
$fileVersion = Get-ProjectVersion $projectContent 'FileVersion' $projectPath
$expectedAssemblyVersion = "$currentVersion.0"
if ($projectVersion -ne $currentVersion -or
    $assemblyVersion -ne $expectedAssemblyVersion -or
    $fileVersion -ne $expectedAssemblyVersion) {
    throw "Version metadata is out of sync. Manifest=$currentVersion, Version=$projectVersion, AssemblyVersion=$assemblyVersion, FileVersion=$fileVersion. Fix the mismatch before building."
}

$buildVersion = $currentVersion
if ($BumpPatch) {
    $numericVersion = [regex]::Match($currentVersion, '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$')
    if (-not $numericVersion.Success) {
        throw "Cannot increment non-numeric three-part version '$currentVersion'."
    }
    $nextPatch = [long]$numericVersion.Groups['patch'].Value + 1
    $buildVersion = "$($numericVersion.Groups['major'].Value).$($numericVersion.Groups['minor'].Value).$nextPatch"
}

$buildAssemblyVersion = "$buildVersion.0"
$versionTag = $buildVersion -replace '\.', '_'
$outputPackage = "${pluginName}_${versionTag}.lplug4"

# 0. Prerequisites
Write-Host "`n=== Checking Prerequisites ===" -ForegroundColor Cyan
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) { Write-Error "dotnet SDK not found."; exit 1 }
if (-not (Get-Command "logiplugintool" -ErrorAction SilentlyContinue)) { Write-Error "logiplugintool not found."; exit 1 }

# 1. Clean
Write-Host "`n=== Cleaning ===" -ForegroundColor Cyan
$dirsToClean = @("bin", "obj", $stagingDir, "src/bin", "src/obj")
foreach ($dir in $dirsToClean) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
if (Test-Path $outputPackage) { Remove-Item $outputPackage -Force }

# 2. Build
Write-Host "`n=== Building Project ===" -ForegroundColor Cyan
$dotnetBuildArguments = @($projectPath, "-c", "Release")
if ($BumpPatch) {
    $dotnetBuildArguments += @(
        "-p:Version=$buildVersion",
        "-p:AssemblyVersion=$buildAssemblyVersion",
        "-p:FileVersion=$buildAssemblyVersion"
    )
}
& dotnet build @dotnetBuildArguments
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

# 3. Stage Files
Write-Host "`n=== Staging Files ===" -ForegroundColor Cyan
$metadataSrc = Join-Path $releaseDir "metadata"
$binSrc = Join-Path $releaseDir "win"
$metadataDest = Join-Path $stagingDir "metadata"
$binDest = Join-Path $stagingDir "win"

if (-not (Test-Path $metadataSrc)) { Write-Error "Metadata not found."; exit 1 }
if (-not (Test-Path $binSrc)) { Write-Error "Binaries not found."; exit 1 }

New-Item -ItemType Directory -Path $metadataDest -Force | Out-Null
New-Item -ItemType Directory -Path $binDest -Force | Out-Null

Copy-Item "$metadataSrc\*" $metadataDest -Recurse
Get-ChildItem $binSrc | Where-Object { 
    $_.Name -ne "PluginApi.dll" -and 
    $_.Name -ne "PluginApi.xml" -and
    $_.Name -ne "LoupedeckPackage.yaml" 
} | Copy-Item -Destination $binDest

# A version bump is staged in the package and passed to MSBuild first. Source version
# files are updated only after package verification succeeds.
if ($BumpPatch) {
    $stagedManifest = Join-Path $metadataDest "LoupedeckPackage.yaml"
    $stagedManifestContent = [System.IO.File]::ReadAllText($stagedManifest)
    $manifestPattern = '(?m)^([ \t]*version:[ \t]*)' + [regex]::Escape($currentVersion) + '([ \t]*)$'
    $stagedMatches = [regex]::Matches($stagedManifestContent, $manifestPattern)
    if ($stagedMatches.Count -ne 1) {
        throw "Expected exactly one current version in staged manifest $stagedManifest."
    }
    $stagedManifestContent = [regex]::Replace($stagedManifestContent, $manifestPattern, ('${1}' + $buildVersion + '${2}'))
    [System.IO.File]::WriteAllText($stagedManifest, $stagedManifestContent)
}

# 4. Pack
Write-Host "`n=== Packaging ===" -ForegroundColor Cyan
logiplugintool pack $stagingDir $outputPackage
if ($LASTEXITCODE -ne 0) { Write-Error "Packaging failed."; exit 1 }

# 5. Verify
Write-Host "`n=== Verifying Package ===" -ForegroundColor Cyan
logiplugintool verify $outputPackage
if ($LASTEXITCODE -ne 0) { Write-Error "Verification failed."; exit 1 }

if ($BumpPatch) {
    $updatedProjectContent = Replace-ProjectVersion $projectContent 'Version' $currentVersion $buildVersion $projectPath
    $updatedProjectContent = Replace-ProjectVersion $updatedProjectContent 'AssemblyVersion' $expectedAssemblyVersion $buildAssemblyVersion $projectPath
    $updatedProjectContent = Replace-ProjectVersion $updatedProjectContent 'FileVersion' $expectedAssemblyVersion $buildAssemblyVersion $projectPath
    $updatedManifestContent = [regex]::Replace(
        $manifestContent,
        '(?m)^([ \t]*version:[ \t]*)' + [regex]::Escape($currentVersion) + '([ \t]*)$',
        ('${1}' + $buildVersion + '${2}')
    )
    [System.IO.File]::WriteAllText($projectPath, $updatedProjectContent)
    [System.IO.File]::WriteAllText($manifestPath, $updatedManifestContent)
    Write-Host "Version metadata updated to $buildVersion after successful package verification." -ForegroundColor Cyan
}

Write-Host "`nSUCCESS: Package created at $outputPackage" -ForegroundColor Green
