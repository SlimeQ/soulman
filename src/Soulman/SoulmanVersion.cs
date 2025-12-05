using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Soulman;

public static class SoulmanVersion
{
    public static string GetLabel()
    {
        var version = GetVersion();
        return string.IsNullOrWhiteSpace(version) ? "Soulman" : $"Soulman {version}";
    }

    public static string GetVersion()
    {
        try
        {
            var releaseTag = TryGetAssemblyMetadata("ReleaseTag");
            if (!string.IsNullOrWhiteSpace(releaseTag))
            {
                return TrimMetadata(releaseTag);
            }

            var clickOnceVersion = TryGetClickOnceVersion();
            if (!string.IsNullOrWhiteSpace(clickOnceVersion))
            {
                return TrimMetadata(clickOnceVersion);
            }

            var productVersion = TryGetProductVersion();
            if (!string.IsNullOrWhiteSpace(productVersion))
            {
                return TrimMetadata(productVersion);
            }

            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(info))
            {
                return TrimMetadata(info);
            }

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            if (ver != null)
            {
                return TrimMetadata(ver.ToString());
            }
        }
        catch
        {
            // ignore and fall through to empty
        }

        return string.Empty;
    }

    private static string? TryGetAssemblyMetadata(string key)
    {
        try
        {
            var attrs = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return attrs.FirstOrDefault()?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProductVersion()
    {
        try
        {
            var path = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var ver = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return TrimMetadata(ver);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetClickOnceVersion()
    {
        try
        {
            var deploymentAsm = Assembly.Load("System.Deployment, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a");
            var type = deploymentAsm?.GetType("System.Deployment.Application.ApplicationDeployment");
            if (type == null)
            {
                return null;
            }

            var isNetworkDeployedProp = type.GetProperty("IsNetworkDeployed", BindingFlags.Public | BindingFlags.Static);
            if (isNetworkDeployedProp == null)
            {
                return null;
            }

            var isNetworkDeployed = isNetworkDeployedProp.GetValue(null) as bool?;
            if (isNetworkDeployed != true)
            {
                return null;
            }

            var currentDeploymentProp = type.GetProperty("CurrentDeployment", BindingFlags.Public | BindingFlags.Static);
            var currentDeployment = currentDeploymentProp?.GetValue(null);
            if (currentDeployment == null)
            {
                return null;
            }

            var versionProp = currentDeployment.GetType().GetProperty("CurrentVersion");
            var versionValue = versionProp?.GetValue(currentDeployment)?.ToString();
            return versionValue;
        }
        catch
        {
            return null;
        }
    }

    private static string TrimMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var core = value.Split('+')[0];
        return core;
    }
}
