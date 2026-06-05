$ErrorActionPreference = 'Stop'

Write-Host ("CWD=" + (Get-Location).Path)
Write-Host ("ANDROID_HOME=" + $env:ANDROID_HOME)
Write-Host ("ANDROID_SDK_ROOT=" + $env:ANDROID_SDK_ROOT)
Write-Host ("ANDROID_NDK_ROOT=" + $env:ANDROID_NDK_ROOT)
Write-Host ("NDKROOT=" + $env:NDKROOT)
Write-Host ("JAVA_HOME=" + $env:JAVA_HOME)
