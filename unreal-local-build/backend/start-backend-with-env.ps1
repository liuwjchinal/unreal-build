$ErrorActionPreference = 'Stop'

$backendDir = Split-Path -Path $PSCommandPath -Parent
$envLog = Join-Path $backendDir 'AppData\last-android-env.txt'

@(
    "ANDROID_HOME=$env:ANDROID_HOME"
    "ANDROID_SDK_ROOT=$env:ANDROID_SDK_ROOT"
    "ANDROID_NDK_ROOT=$env:ANDROID_NDK_ROOT"
    "NDKROOT=$env:NDKROOT"
    "NDK_ROOT=$env:NDK_ROOT"
    "JAVA_HOME=$env:JAVA_HOME"
    "PWD=$((Get-Location).Path)"
) | Set-Content -Path $envLog

& 'C:\Program Files\dotnet\dotnet.exe' '.\bin\Release\net10.0\Backend.dll'
