using System.Text.RegularExpressions;

namespace Backend.Services;

public sealed record OpenHarmonyToolchainValidationResult(
    string? SdkRoot,
    string? HvigorPath,
    string? NodeHome,
    string? NodeExecutablePath,
    string? JavaHome,
    string? JavaExecutablePath,
    bool HasSdk,
    bool HasHvigor,
    bool HasNode,
    bool HasJava);

public static class OpenHarmonyToolchain
{
    private const string RuntimeSettingsSectionName = "/Script/OpenHarmonyRuntimeSettings.OpenHarmonyRuntimeSettings";

    public static bool HasRuntimeSettings(string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return false;
        }

        foreach (var filePath in EnumerateConfigFiles(projectDirectory))
        {
            try
            {
                var content = File.ReadAllText(filePath);
                if (content.Contains(RuntimeSettingsSectionName, StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("OpenHarmonyRuntimeSettings", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Ignore unreadable config fragments and continue.
            }
        }

        return false;
    }

    public static OpenHarmonyToolchainValidationResult Validate(string? projectDirectory)
    {
        var sdkRoot = ResolveSdkRoot(projectDirectory);
        var hvigorPath = ResolveHvigorPath(projectDirectory);
        var nodeHome = ResolveNodeHome(projectDirectory);
        var nodeExecutablePath = ResolveNodeExecutablePath(nodeHome);
        var javaHome = ResolveJavaHome(projectDirectory);
        var javaExecutablePath = ResolveJavaExecutablePath(javaHome);

        return new OpenHarmonyToolchainValidationResult(
            sdkRoot,
            hvigorPath,
            nodeHome,
            nodeExecutablePath,
            javaHome,
            javaExecutablePath,
            !string.IsNullOrWhiteSpace(sdkRoot) && Directory.Exists(sdkRoot),
            !string.IsNullOrWhiteSpace(hvigorPath) && File.Exists(hvigorPath),
            !string.IsNullOrWhiteSpace(nodeExecutablePath),
            !string.IsNullOrWhiteSpace(javaExecutablePath));
    }

    public static string? ResolveSdkRoot(string? projectDirectory)
    {
        var configuredPath = ResolveConfiguredDirectory(projectDirectory, "SdkRootPath");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return ResolveExistingDirectory(Environment.GetEnvironmentVariable("OPENHARMONY_SDK_ROOT"));
    }

    public static string? ResolveHvigorPath(string? projectDirectory)
    {
        var configuredPath = ResolveConfiguredFile(projectDirectory, "HvigorPath");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var envPath = ResolveExistingFile(Environment.GetEnvironmentVariable("OPENHARMONY_HVIGOR_PATH"));
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }

        return FindCommandOnPath("hvigorw.bat")
            ?? FindCommandOnPath("hvigorw")
            ?? FindCommandOnPath("hvigor");
    }

    public static string? ResolveNodeHome(string? projectDirectory)
    {
        var configuredPath = ResolveConfiguredDirectory(projectDirectory, "NodeHomePath");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return ResolveExistingDirectory(Environment.GetEnvironmentVariable("NODE_HOME"));
    }

    public static string? ResolveJavaHome(string? projectDirectory)
    {
        var configuredPath = ResolveConfiguredDirectory(projectDirectory, "JavaHomePath");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return ResolveExistingDirectory(Environment.GetEnvironmentVariable("JAVA_HOME"));
    }

    private static IEnumerable<string> EnumerateConfigFiles(string projectDirectory)
    {
        var configDirectory = Path.Combine(projectDirectory, "Config");
        return Directory.Exists(configDirectory)
            ? Directory.EnumerateFiles(configDirectory, "*.ini", SearchOption.AllDirectories)
            : Array.Empty<string>();
    }

    private static string? ResolveConfiguredDirectory(string? projectDirectory, string key)
    {
        var configuredPath = ResolveConfiguredPath(projectDirectory, key);
        return ResolveExistingDirectory(configuredPath);
    }

    private static string? ResolveConfiguredFile(string? projectDirectory, string key)
    {
        var configuredPath = ResolveConfiguredPath(projectDirectory, key);
        return ResolveExistingFile(configuredPath);
    }

    private static string? ResolveConfiguredPath(string? projectDirectory, string key)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        foreach (var filePath in EnumerateConfigFiles(projectDirectory))
        {
            try
            {
                foreach (var line in File.ReadLines(filePath))
                {
                    var match = Regex.Match(line, $"^\\s*{Regex.Escape(key)}=(.*)$", RegexOptions.IgnoreCase);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var rawValue = match.Groups[1].Value.Trim();
                    return ConvertConfiguredPath(rawValue, projectDirectory);
                }
            }
            catch
            {
                // Ignore unreadable config fragments and continue.
            }
        }

        return null;
    }

    private static string? ConvertConfiguredPath(string rawValue, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var trimmedValue = rawValue.Trim();
        string? pathValue = null;

        var wrapperMatch = Regex.Match(trimmedValue, "^\\((?:FilePath|Path)=\"([^\"]+)\"\\)$", RegexOptions.IgnoreCase);
        if (wrapperMatch.Success)
        {
            pathValue = wrapperMatch.Groups[1].Value;
        }
        else if (trimmedValue.StartsWith('"') && trimmedValue.EndsWith('"') && trimmedValue.Length >= 2)
        {
            pathValue = trimmedValue[1..^1];
        }
        else
        {
            pathValue = trimmedValue;
        }

        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        var normalizedPath = pathValue.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedPath))
        {
            return normalizedPath;
        }

        return Path.GetFullPath(Path.Combine(projectDirectory, normalizedPath));
    }

    private static string? ResolveNodeExecutablePath(string? nodeHome)
    {
        if (!string.IsNullOrWhiteSpace(nodeHome))
        {
            var nodeExecutable = Path.Combine(nodeHome, "node.exe");
            if (File.Exists(nodeExecutable))
            {
                return nodeExecutable;
            }
        }

        return FindCommandOnPath("node.exe") ?? FindCommandOnPath("node");
    }

    private static string? ResolveJavaExecutablePath(string? javaHome)
    {
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var javaExecutable = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(javaExecutable))
            {
                return javaExecutable;
            }
        }

        return FindCommandOnPath("java.exe") ?? FindCommandOnPath("java");
    }

    private static string? FindCommandOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries and continue.
            }
        }

        return null;
    }

    private static string? ResolveExistingDirectory(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? path.Trim() : null;
    }

    private static string? ResolveExistingFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path.Trim() : null;
    }
}
