using Backend.Services;
using Xunit;

namespace Backend.Tests;

[CollectionDefinition("AndroidToolchainEnvironment", DisableParallelization = true)]
public sealed class AndroidToolchainEnvironmentCollectionDefinition;

[Collection("AndroidToolchainEnvironment")]
public sealed class AndroidToolchainTests : IDisposable
{
    private static readonly string[] RelevantEnvironmentVariables =
    [
        "ANDROID_HOME",
        "ANDROID_SDK_ROOT",
        "ANDROID_NDK_ROOT",
        "NDKROOT",
        "NDK_ROOT",
        "JAVA_HOME"
    ];

    private readonly string _rootPath;
    private readonly Dictionary<string, string?> _originalEnvironmentValues;

    public AndroidToolchainTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "backend-android-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _originalEnvironmentValues = CaptureEnvironmentVariables();
        ClearRelevantEnvironmentVariables();
    }

    [Fact]
    public void ResolveSdkRoot_SkipsInvalidEnvAndUsesValidAlternativeEnv()
    {
        var sdkRoot = CreateSdkRoot("sdk-valid");
        Environment.SetEnvironmentVariable("ANDROID_SDK_ROOT", Path.Combine(_rootPath, "missing-sdk"));
        Environment.SetEnvironmentVariable("ANDROID_HOME", sdkRoot);

        var resolved = AndroidToolchain.ResolveSdkRoot();

        Assert.Equal(sdkRoot, resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveNdkRoot_PrefersKnownStableVersionWithinSdk()
    {
        var sdkRoot = CreateSdkRoot("sdk-root");
        var preferredNdkRoot = CreateNdkRoot(sdkRoot, "27.2.12479018");
        CreateNdkRoot(sdkRoot, "29.0.14206865");

        var resolved = AndroidToolchain.ResolveNdkRoot(sdkRoot);

        Assert.Equal(preferredNdkRoot, resolved, ignoreCase: true);
    }

    [Fact]
    public void ResolveJavaHome_FallsBackToBundledJdkWithinSdk()
    {
        var sdkRoot = CreateSdkRoot("sdk-root");
        var javaHome = CreateJavaHome(Path.Combine(sdkRoot, "ndk"), "jdk-21.0.2");
        Environment.SetEnvironmentVariable("ANDROID_HOME", sdkRoot);

        var resolved = AndroidToolchain.ResolveJavaHome();

        Assert.Equal(javaHome, resolved, ignoreCase: true);
    }

    public void Dispose()
    {
        foreach (var pair in _originalEnvironmentValues)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private Dictionary<string, string?> CaptureEnvironmentVariables()
    {
        return RelevantEnvironmentVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);
    }

    private void ClearRelevantEnvironmentVariables()
    {
        foreach (var variableName in RelevantEnvironmentVariables)
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    private string CreateSdkRoot(string directoryName)
    {
        var sdkRoot = Path.Combine(_rootPath, directoryName);
        Directory.CreateDirectory(Path.Combine(sdkRoot, "build-tools", "36.0.0"));
        Directory.CreateDirectory(Path.Combine(sdkRoot, "platforms", "android-34"));
        Directory.CreateDirectory(Path.Combine(sdkRoot, "platform-tools"));
        Directory.CreateDirectory(Path.Combine(sdkRoot, "licenses"));
        Directory.CreateDirectory(Path.Combine(sdkRoot, "ndk"));
        return sdkRoot;
    }

    private static string CreateNdkRoot(string sdkRoot, string directoryName)
    {
        var ndkRoot = Path.Combine(sdkRoot, "ndk", directoryName);
        Directory.CreateDirectory(Path.Combine(ndkRoot, "toolchains", "llvm"));
        return ndkRoot;
    }

    private static string CreateJavaHome(string parentDirectory, string directoryName)
    {
        var javaHome = Path.Combine(parentDirectory, directoryName);
        Directory.CreateDirectory(Path.Combine(javaHome, "bin"));
        File.WriteAllText(Path.Combine(javaHome, "bin", "java.exe"), string.Empty);
        return javaHome;
    }
}
