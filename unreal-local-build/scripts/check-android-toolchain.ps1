param(
    [string]$EngineRoot = "",
    [string]$SdkRoot = "",
    [string]$NdkRoot = "",
    [string]$JavaHome = "",
    [switch]$ShowLogSnippet
)

$ErrorActionPreference = "Stop"

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 80) -ForegroundColor DarkGray
    Write-Host $Title -ForegroundColor Cyan
    Write-Host ("=" * 80) -ForegroundColor DarkGray
}

function Write-Check {
    param(
        [string]$Label,
        [bool]$Ok,
        [string]$Detail
    )

    $status = if ($Ok) { "[OK]" } else { "[FAIL]" }
    $color = if ($Ok) { "Green" } else { "Red" }
    Write-Host ("{0} {1}: {2}" -f $status, $Label, $Detail) -ForegroundColor $color
}

function Resolve-SdkRoot {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        return $Explicit.Trim()
    }

    foreach ($name in @("ANDROID_SDK_ROOT", "ANDROID_HOME")) {
        $value = [Environment]::GetEnvironmentVariable($name, "Process")
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value.Trim()
        }
    }

    return ""
}

function Resolve-JavaHome {
    param([string]$Explicit)

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        return $Explicit.Trim()
    }

    $value = [Environment]::GetEnvironmentVariable("JAVA_HOME", "Process")
    if ([string]::IsNullOrWhiteSpace($value)) {
        return ""
    }

    return $value.Trim()
}

function Resolve-NdkRoot {
    param(
        [string]$Explicit,
        [string]$ResolvedSdkRoot
    )

    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        $candidates.Add($Explicit.Trim())
    }

    foreach ($name in @("ANDROID_NDK_ROOT", "NDKROOT", "NDK_ROOT")) {
        $value = [Environment]::GetEnvironmentVariable($name, "Process")
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $candidates.Add($value.Trim())
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ResolvedSdkRoot)) {
        $candidates.Add((Join-Path $ResolvedSdkRoot "ndk-bundle"))
        $candidates.Add((Join-Path $ResolvedSdkRoot "ndk"))
    }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) {
            continue
        }

        if (Test-Path (Join-Path $candidate "toolchains\llvm")) {
            return (Resolve-Path $candidate).Path
        }

        $child = Get-ChildItem -Path $candidate -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName "toolchains\llvm") } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($child) {
            return $child.FullName
        }
    }

    return ""
}

function Resolve-EngineRoot {
    param(
        [string]$Explicit,
        [string]$ResolvedSdkRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($Explicit)) {
        return $Explicit.Trim()
    }

    if ([string]::IsNullOrWhiteSpace($ResolvedSdkRoot)) {
        return ""
    }

    $normalized = $ResolvedSdkRoot.Replace('/', '\')
    $marker = "\Engine\Platforms\Android\SDK"
    $index = $normalized.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase)
    if ($index -lt 0) {
        return ""
    }

    return $normalized.Substring(0, $index)
}

function Test-FileSet {
    param(
        [string]$Root,
        [string[]]$RelativePaths
    )

    $results = foreach ($relativePath in $RelativePaths) {
        $fullPath = Join-Path $Root $relativePath
        [PSCustomObject]@{
            RelativePath = $relativePath
            FullPath = $fullPath
            Exists = (Test-Path $fullPath)
        }
    }

    return $results
}

function Get-LatestAutomationLog {
    param([string]$ResolvedEngineRoot)

    if ([string]::IsNullOrWhiteSpace($ResolvedEngineRoot)) {
        return $null
    }

    $logRoot = Join-Path $ResolvedEngineRoot "Engine\Programs\AutomationTool\Saved\Logs"
    if (-not (Test-Path $logRoot)) {
        return $null
    }

    return Get-ChildItem -Path $logRoot -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

$resolvedSdkRoot = Resolve-SdkRoot -Explicit $SdkRoot
$resolvedJavaHome = Resolve-JavaHome -Explicit $JavaHome
$resolvedNdkRoot = Resolve-NdkRoot -Explicit $NdkRoot -ResolvedSdkRoot $resolvedSdkRoot
$resolvedEngineRoot = Resolve-EngineRoot -Explicit $EngineRoot -ResolvedSdkRoot $resolvedSdkRoot

$sdkLicensesPath = if ($resolvedSdkRoot) { Join-Path $resolvedSdkRoot "licenses" } else { "" }
$sdkLicenseFilePath = if ($resolvedSdkRoot) { Join-Path $resolvedSdkRoot "licenses\android-sdk-license" } else { "" }
$sdkBuildToolsPath = if ($resolvedSdkRoot) { Join-Path $resolvedSdkRoot "build-tools" } else { "" }
$sdkPlatformsPath = if ($resolvedSdkRoot) { Join-Path $resolvedSdkRoot "platforms" } else { "" }
$ndkSysrootPath = if ($resolvedNdkRoot) { Join-Path $resolvedNdkRoot "toolchains\llvm\prebuilt\windows-x86_64\sysroot" } else { "" }

$sdkChecks = @(
    [PSCustomObject]@{ Label = "SDK root"; Path = $resolvedSdkRoot; Exists = (-not [string]::IsNullOrWhiteSpace($resolvedSdkRoot) -and (Test-Path $resolvedSdkRoot)) },
    [PSCustomObject]@{ Label = "SDK licenses"; Path = $sdkLicensesPath; Exists = ($resolvedSdkRoot -and (Test-Path $sdkLicensesPath)) },
    [PSCustomObject]@{ Label = "SDK license file"; Path = $sdkLicenseFilePath; Exists = ($resolvedSdkRoot -and (Test-Path $sdkLicenseFilePath)) },
    [PSCustomObject]@{ Label = "SDK build-tools"; Path = $sdkBuildToolsPath; Exists = ($resolvedSdkRoot -and (Test-Path $sdkBuildToolsPath)) },
    [PSCustomObject]@{ Label = "SDK platforms"; Path = $sdkPlatformsPath; Exists = ($resolvedSdkRoot -and (Test-Path $sdkPlatformsPath)) }
)

$ndkRequiredFiles = @(
    "toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\21\crtbegin_so.o",
    "toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\21\libGLESv3.so",
    "toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\21\libEGL.so",
    "toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\21\libandroid.so",
    "toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\21\libOpenSLES.so",
    "toolchains\llvm\prebuilt\windows-x86_64\sysroot\usr\lib\aarch64-linux-android\21\libc.so"
)

$ndkChecks = @(
    [PSCustomObject]@{ Label = "NDK root"; Path = $resolvedNdkRoot; Exists = (-not [string]::IsNullOrWhiteSpace($resolvedNdkRoot) -and (Test-Path $resolvedNdkRoot)) },
    [PSCustomObject]@{ Label = "NDK sysroot"; Path = $ndkSysrootPath; Exists = ($resolvedNdkRoot -and (Test-Path $ndkSysrootPath)) }
)

$ndkFileChecks = if ($resolvedNdkRoot) { Test-FileSet -Root $resolvedNdkRoot -RelativePaths $ndkRequiredFiles } else { @() }
$backendProcesses = Get-Process Backend -ErrorAction SilentlyContinue | Sort-Object StartTime
$latestLog = Get-LatestAutomationLog -ResolvedEngineRoot $resolvedEngineRoot

Write-Section "Android build machine check"
Write-Host ("Time: {0}" -f (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")) -ForegroundColor Gray
Write-Host ("Host: {0}" -f $env:COMPUTERNAME) -ForegroundColor Gray

Write-Section "Environment variables"
foreach ($name in @("ANDROID_HOME", "ANDROID_SDK_ROOT", "ANDROID_NDK_ROOT", "NDKROOT", "NDK_ROOT", "JAVA_HOME")) {
    $value = [Environment]::GetEnvironmentVariable($name, "Process")
    if ([string]::IsNullOrWhiteSpace($value)) {
        Write-Host ("{0} = <empty>" -f $name) -ForegroundColor Yellow
    } else {
        Write-Host ("{0} = {1}" -f $name, $value)
    }
}

Write-Section "Resolved paths"
Write-Host ("EngineRoot = {0}" -f $(if ($resolvedEngineRoot) { $resolvedEngineRoot } else { "<empty>" }))
Write-Host ("SdkRoot    = {0}" -f $(if ($resolvedSdkRoot) { $resolvedSdkRoot } else { "<empty>" }))
Write-Host ("NdkRoot    = {0}" -f $(if ($resolvedNdkRoot) { $resolvedNdkRoot } else { "<empty>" }))
Write-Host ("JavaHome   = {0}" -f $(if ($resolvedJavaHome) { $resolvedJavaHome } else { "<empty>" }))

Write-Section "SDK checks"
foreach ($check in $sdkChecks) {
    Write-Check -Label $check.Label -Ok ([bool]$check.Exists) -Detail $check.Path
}

Write-Section "NDK checks"
foreach ($check in $ndkChecks) {
    Write-Check -Label $check.Label -Ok ([bool]$check.Exists) -Detail $check.Path
}
foreach ($check in $ndkFileChecks) {
    Write-Check -Label $check.RelativePath -Ok ([bool]$check.Exists) -Detail $check.FullPath
}

Write-Section "JAVA check"
Write-Check -Label "JAVA_HOME" -Ok (-not [string]::IsNullOrWhiteSpace($resolvedJavaHome) -and (Test-Path $resolvedJavaHome)) -Detail $resolvedJavaHome

Write-Section "Backend processes"
if ($backendProcesses) {
    foreach ($process in $backendProcesses) {
        Write-Host ("PID={0} StartTime={1} Path={2}" -f $process.Id, $process.StartTime.ToString("yyyy-MM-dd HH:mm:ss"), $process.Path)
    }
    Write-Host "If you changed Android environment variables, these Backend processes must be fully restarted." -ForegroundColor Yellow
} else {
    Write-Host "No Backend process detected." -ForegroundColor Yellow
}

Write-Section "Latest AutomationTool log"
if ($latestLog) {
    Write-Host ("LatestLog = {0}" -f $latestLog.FullName)
    Write-Host ("LastWrite = {0}" -f $latestLog.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))

    $interestingPatterns = @(
        "NDKROOT",
        "ANDROID_HOME",
        "JAVA_HOME",
        "crtbegin_so.o",
        "libGLESv3",
        "libEGL",
        "libandroid",
        "libOpenSLES",
        "unable to find library",
        "cannot open crtbegin_so.o"
    )

    $matches = Select-String -Path $latestLog.FullName -Pattern $interestingPatterns -SimpleMatch -ErrorAction SilentlyContinue
    if ($matches) {
        $lines = $matches | Select-Object -ExpandProperty Line -Unique
        foreach ($line in $lines) {
            Write-Host $line
        }
    } else {
        Write-Host "No Android environment or linker error lines found in latest log."
    }

    if ($ShowLogSnippet) {
        Write-Host ""
        Write-Host "Last 80 lines:" -ForegroundColor Cyan
        Get-Content $latestLog.FullName | Select-Object -Last 80
    }
} else {
    Write-Host "Could not locate AutomationTool log directory automatically. Pass -EngineRoot if needed." -ForegroundColor Yellow
}

Write-Section "Quick conclusion"
$hasSdkIssue = $sdkChecks.Exists -contains $false
$hasNdkIssue = $ndkChecks.Exists -contains $false -or ($ndkFileChecks.Exists -contains $false)
$hasJavaIssue = [string]::IsNullOrWhiteSpace($resolvedJavaHome) -or -not (Test-Path $resolvedJavaHome)

if (-not $hasSdkIssue -and -not $hasNdkIssue -and -not $hasJavaIssue) {
    Write-Host "Base directories look valid. If Android build still fails, restart Backend and compare the latest AutomationTool log values." -ForegroundColor Green
} else {
    if ($hasNdkIssue) {
        Write-Host "NDK is incomplete or points to the wrong directory. This directly explains crtbegin_so.o / libEGL / libc linker failures." -ForegroundColor Red
    }
    if ($hasSdkIssue) {
        Write-Host "SDK, licenses, build-tools, or platforms are incomplete." -ForegroundColor Red
    }
    if ($hasJavaIssue) {
        Write-Host "JAVA_HOME is invalid." -ForegroundColor Red
    }
}
