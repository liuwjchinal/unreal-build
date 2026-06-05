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

    private static readonly string[] PreferredNdkDirectoryNames =
    [
        "27.2.12479018",
        "android-ndk-r27c"
    ];

    private static readonly string[] SdkMarkerDirectories =
    [
        "build-tools",
        "platforms",
        "platform-tools",
        "licenses",
        "cmdline-tools",
        "ndk"
    ];

    public static string? ResolveSdkRoot()
    {
        foreach (var envName in SdkEnvNames)
        {
            var candidate = NormalizeSdkCandidate(Environment.GetEnvironmentVariable(envName));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        foreach (var fallbackPath in EnumerateSdkFallbackCandidates())
        {
            var candidate = NormalizeSdkCandidate(fallbackPath);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string? ResolveJavaHome()
    {
        var javaHome = NormalizeJavaHomeCandidate(Environment.GetEnvironmentVariable("JAVA_HOME"));
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            return javaHome;
        }

        var sdkRoot = ResolveSdkRoot();
        foreach (var fallbackPath in EnumerateJavaFallbackCandidates(sdkRoot))
        {
            javaHome = NormalizeJavaHomeCandidate(fallbackPath);
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                return javaHome;
            }
        }

        return null;
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
            missingDetail = "Android NDK directory does not exist.";
            return false;
        }

        var sysrootRoot = GetSysrootRoot(ndkRoot);
        if (!Directory.Exists(sysrootRoot))
        {
            missingDetail = @"Android NDK is missing toolchains\llvm\prebuilt\windows-x86_64\sysroot.";
            return false;
        }

        return TryLocateRequiredLinkerRuntime(ndkRoot, out missingDetail);
    }

    private static string? NormalizeSdkCandidate(string? value)
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

        return SdkMarkerDirectories.Any(directoryName => Directory.Exists(Path.Combine(trimmed, directoryName)))
            ? trimmed
            : null;
    }

    private static string? NormalizeJavaHomeCandidate(string? value)
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

        if (File.Exists(Path.Combine(trimmed, "bin", "java.exe")))
        {
            return trimmed;
        }

        var childVersion = Directory.EnumerateDirectories(trimmed)
            .Where(path => File.Exists(Path.Combine(path, "bin", "java.exe")))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(childVersion) ? null : childVersion;
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

        var childVersion = SelectPreferredNdkRoot(
            Directory.EnumerateDirectories(trimmed)
                .Where(path => Directory.Exists(Path.Combine(path, "toolchains", "llvm"))));

        return string.IsNullOrWhiteSpace(childVersion) ? trimmed : childVersion;
    }

    private static string? SelectPreferredNdkRoot(IEnumerable<string> candidates)
    {
        var candidateList = candidates.ToList();
        if (candidateList.Count == 0)
        {
            return null;
        }

        foreach (var preferredDirectoryName in PreferredNdkDirectoryNames)
        {
            var preferred = candidateList.FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    preferredDirectoryName,
                    StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }
        }

        return candidateList
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IEnumerable<string> EnumerateSdkFallbackCandidates()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            yield return Path.Combine(localApplicationData, "Android", "Sdk");
        }

        yield return @"C:\Android\Sdk";
        yield return @"C:\Android\SDK";

        foreach (var driveRoot in EnumerateFixedDriveRoots())
        {
            yield return Path.Combine(driveRoot, "SDK");
            yield return Path.Combine(driveRoot, "Android", "Sdk");

            foreach (var engineRoot in EnumerateLikelyEngineRoots(driveRoot))
            {
                yield return Path.Combine(engineRoot, "Engine", "Platforms", "Android", "SDK");
            }
        }
    }

    private static IEnumerable<string> EnumerateJavaFallbackCandidates(string? sdkRoot)
    {
        if (!string.IsNullOrWhiteSpace(sdkRoot))
        {
            yield return Path.Combine(sdkRoot, "jbr");
            yield return Path.Combine(sdkRoot, "jdk");
            yield return Path.Combine(sdkRoot, "ndk");
        }

        yield return @"C:\Program Files\Android\Android Studio\jbr";
        yield return @"C:\Program Files\Android\jbr";
        yield return @"C:\Program Files\Android\jdk";
        yield return @"C:\Program Files\Java";
    }

    private static IEnumerable<string> EnumerateFixedDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            yield return drive.RootDirectory.FullName;
        }
    }

    private static IEnumerable<string> EnumerateLikelyEngineRoots(string driveRoot)
    {
        IEnumerable<string> topLevelDirectories;
        try
        {
            topLevelDirectories = Directory.EnumerateDirectories(driveRoot);
        }
        catch
        {
            yield break;
        }

        foreach (var directory in topLevelDirectories)
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith("UnrealEngine", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }
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
            missingDetail = @"Android NDK sysroot is missing usr\lib\aarch64-linux-android.";
            return false;
        }

        var apiLevels = Directory.EnumerateDirectories(runtimeRoot)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (apiLevels.Count == 0)
        {
            missingDetail = "Android NDK sysroot does not contain any API level directories.";
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

        missingDetail = "Android NDK sysroot is missing one or more required linker runtime files.";
        return false;
    }
}
