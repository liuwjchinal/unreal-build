namespace Backend.Services;

public sealed record AndroidToolchainValidationResult(
    string? SdkRoot,
    string? NdkRoot,
    string? JavaHome,
    bool HasSdk,
    bool HasLicense,
    bool HasBuildTools,
    bool HasJavaHome,
    bool HasNdk,
    bool HasNdkSysroot,
    bool HasRequiredLinkerRuntime,
    string? MissingDetail);

public static class AndroidToolchain
{
    private static readonly string[] SdkEnvNames =
    [
        "ANDROID_SDK_ROOT",
        "ANDROID_HOME"
    ];

    private static readonly string[] NdkEnvNames =
    [
        "ANDROID_NDK_ROOT",
        "NDKROOT",
        "NDK_ROOT"
    ];

    public static string? ResolveSdkRoot()
    {
        foreach (var envName in SdkEnvNames)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    public static string? ResolveJavaHome()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        return string.IsNullOrWhiteSpace(javaHome) ? null : javaHome.Trim();
    }

    public static string? ResolveNdkRoot(string? sdkRoot)
    {
        foreach (var envName in NdkEnvNames)
        {
            var candidate = NormalizeNdkCandidate(Environment.GetEnvironmentVariable(envName));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            var ndkBundle = NormalizeNdkCandidate(Path.Combine(sdkRoot, "ndk-bundle"));
            if (!string.IsNullOrWhiteSpace(ndkBundle))
            {
                return ndkBundle;
            }

            var ndkDirectory = Path.Combine(sdkRoot, "ndk");
            var normalizedChild = NormalizeNdkCandidate(ndkDirectory);
            if (!string.IsNullOrWhiteSpace(normalizedChild))
            {
                return normalizedChild;
            }
        }

        return null;
    }

    public static IReadOnlyDictionary<string, string> BuildProcessEnvironmentOverrides()
    {
        var sdkRoot = ResolveSdkRoot();
        var ndkRoot = ResolveNdkRoot(sdkRoot);
        var javaHome = ResolveJavaHome();
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            overrides["ANDROID_HOME"] = sdkRoot;
            overrides["ANDROID_SDK_ROOT"] = sdkRoot;
        }

        if (!string.IsNullOrWhiteSpace(ndkRoot))
        {
            overrides["ANDROID_NDK_ROOT"] = ndkRoot;
            overrides["NDKROOT"] = ndkRoot;
            overrides["NDK_ROOT"] = ndkRoot;
        }

        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            overrides["JAVA_HOME"] = javaHome;
        }

        return overrides;
    }

    public static AndroidToolchainValidationResult Validate()
    {
        var sdkRoot = ResolveSdkRoot();
        var ndkRoot = ResolveNdkRoot(sdkRoot);
        var javaHome = ResolveJavaHome();

        var hasSdk = !string.IsNullOrWhiteSpace(sdkRoot) && Directory.Exists(sdkRoot);
        var hasLicense = hasSdk && File.Exists(Path.Combine(sdkRoot!, "licenses", "android-sdk-license"));
        var hasBuildTools = hasSdk &&
                            Directory.Exists(Path.Combine(sdkRoot!, "build-tools")) &&
                            Directory.EnumerateDirectories(Path.Combine(sdkRoot!, "build-tools")).Any();
        var hasJavaHome = !string.IsNullOrWhiteSpace(javaHome) && Directory.Exists(javaHome);
        var hasNdk = !string.IsNullOrWhiteSpace(ndkRoot) && Directory.Exists(ndkRoot);
        var hasNdkSysroot = hasNdk && Directory.Exists(GetSysrootRoot(ndkRoot!));
        var hasRequiredLinkerRuntime = false;
        string? missingDetail = null;

        if (hasNdkSysroot)
        {
            hasRequiredLinkerRuntime = TryLocateRequiredLinkerRuntime(ndkRoot!, out missingDetail);
        }

        return new AndroidToolchainValidationResult(
            sdkRoot,
            ndkRoot,
            javaHome,
            hasSdk,
            hasLicense,
            hasBuildTools,
            hasJavaHome,
            hasNdk,
            hasNdkSysroot,
            hasRequiredLinkerRuntime,
            missingDetail);
    }

    public static bool IsUsableNdkRoot(string? ndkRoot, out string? missingDetail)
    {
        if (string.IsNullOrWhiteSpace(ndkRoot) || !Directory.Exists(ndkRoot))
        {
            missingDetail = "Android NDK 目录不存在。";
            return false;
        }

        var sysrootRoot = GetSysrootRoot(ndkRoot);
        if (!Directory.Exists(sysrootRoot))
        {
            missingDetail = @"Android NDK 缺少 toolchains\llvm\prebuilt\windows-x86_64\sysroot。";
            return false;
        }

        return TryLocateRequiredLinkerRuntime(ndkRoot, out missingDetail);
    }

    private static string? NormalizeNdkCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!Directory.Exists(trimmed))
        {
            return null;
        }

        if (Directory.Exists(Path.Combine(trimmed, "toolchains", "llvm")))
        {
            return trimmed;
        }

        var childVersion = Directory.EnumerateDirectories(trimmed)
            .Where(path => Directory.Exists(Path.Combine(path, "toolchains", "llvm")))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(childVersion) ? trimmed : childVersion;
    }

    private static string GetSysrootRoot(string ndkRoot)
    {
        return Path.Combine(ndkRoot, "toolchains", "llvm", "prebuilt", "windows-x86_64", "sysroot");
    }

    private static bool TryLocateRequiredLinkerRuntime(string ndkRoot, out string? missingDetail)
    {
        var runtimeRoot = Path.Combine(GetSysrootRoot(ndkRoot), "usr", "lib", "aarch64-linux-android");
        if (!Directory.Exists(runtimeRoot))
        {
            missingDetail = @"Android NDK sysroot 缺少 usr\lib\aarch64-linux-android 目录。";
            return false;
        }

        var apiLevels = Directory.EnumerateDirectories(runtimeRoot)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (apiLevels.Count == 0)
        {
            missingDetail = "Android NDK sysroot 未找到任何 API Level 目录。";
            return false;
        }

        foreach (var apiLevelDirectory in apiLevels)
        {
            var requiredFiles = new[]
            {
                "crtbegin_so.o",
                "libGLESv3.so",
                "libEGL.so",
                "libandroid.so",
                "libOpenSLES.so",
                "libc.so"
            };

            var missing = requiredFiles
                .Where(fileName => !File.Exists(Path.Combine(apiLevelDirectory, fileName)))
                .ToList();

            if (missing.Count == 0)
            {
                missingDetail = null;
                return true;
            }
        }

        missingDetail = "Android NDK sysroot 缺少关键链接文件：crtbegin_so.o / libGLESv3.so / libEGL.so / libandroid.so / libOpenSLES.so / libc.so。";
        return false;
    }
}
