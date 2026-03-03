using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Soulman.IconOverlay;

/// <summary>
/// Shell icon overlay handler that shows an overlay on blacklisted/purged files.
/// This runs inside explorer.exe and MUST be a standalone COM DLL with no external dependencies.
/// </summary>
[ComVisible(true)]
[Guid("E1B3A7F4-8C9D-4E5F-A1B2-C3D4E5F6A7B8")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SoulmanBlacklistOverlay : IShellIconOverlayIdentifier, IDisposable
{
    // Constants
    private const string OverlayName = "Soulman Blacklisted";
    private const string SettingsFileName = "appsettings.json";
    private static readonly Guid ClassGuid = new("E1B3A7F4-8C9D-4E5F-A1B2-C3D4E5F6A7B8");
    
    // Icon path: we use an installed icon file or fall back to extracting from our DLL
    private static readonly string? InstallDir = GetInstallDirectory();
    private static readonly string SettingsPath = GetSettingsPath();
    
    // Cached blacklist - refreshed periodically
    private string[]? _cachedBlacklist;
    private DateTime _lastRefresh;
    private readonly object _cacheLock = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromSeconds(30);
    
    // Cached icon file path
    private string? _iconPath;
    private int _iconIndex;
    
    public SoulmanBlacklistOverlay()
    {
        // Try to find/extract the overlay icon
        InitializeIcon();
    }
    
    // IShellIconOverlayIdentifier implementation
    
    public int GetOverlayInfo(StringBuilder iconFile, int iconFileCount, out int iconIndex, out uint flags)
    {
        // Flags: 0 = icon file path is a filesystem path
        flags = 0;
        iconIndex = 0;
        
        try
        {
            if (_iconPath != null && File.Exists(_iconPath))
            {
                iconFile.Append(_iconPath);
                iconIndex = _iconIndex;
                return 0; // S_OK
            }
            
            // Fall back to using our DLL as the icon source (icon index 0)
            var dllPath = typeof(SoulmanBlacklistOverlay).Assembly.Location;
            if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
            {
                iconFile.Append(dllPath);
                iconIndex = 0;
                return 0; // S_OK
            }
            
            // No icon available
            return 1; // S_FALSE
        }
        catch
        {
            return 1; // S_FALSE
        }
    }
    
    public int GetPriority(out int priority)
    {
        // Priority: 0 is highest, 100 is lowest. We want to be visible even with OneDrive etc.
        // Use a relatively high priority (low number) since blacklist is important feedback.
        // Note: Windows limits total overlays to 15, and many apps fight for them.
        priority = 50; // Middle priority
        return 0; // S_OK
    }
    
    public int IsMemberOf(string path, uint attributes)
    {
        if (string.IsNullOrEmpty(path))
            return 1; // S_FALSE
        
        try
        {
            var blacklist = GetBlacklist();
            if (blacklist == null || blacklist.Length == 0)
                return 1; // S_FALSE
            
            // Convert the path to the same format as blacklist entries
            // The blacklist entries are like "Movies/something" or "Music/something"
            // We need to check if the absolute path is under a sync root AND matches a blacklist entry
            
            // Read the settings to get sync roots
            var settings = LoadSettings();
            if (settings == null)
                return 1; // S_FALSE
            
            var syncPath = ResolveToSyncPath(path, settings);
            if (syncPath == null)
                return 1; // S_FALSE - not under any sync root
            
            // Check if this sync path (or a parent) is blacklisted
            foreach (var entry in blacklist)
            {
                if (string.Equals(entry, syncPath, StringComparison.OrdinalIgnoreCase))
                    return 0; // S_OK - exact match
                
                // Check if this path is under a blacklisted directory
                if (syncPath.StartsWith(entry + "/", StringComparison.OrdinalIgnoreCase))
                    return 0; // S_OK - under blacklisted dir
            }
            
            return 1; // S_FALSE
        }
        catch
        {
            return 1; // S_FALSE
        }
    }
    
    // Helper methods
    
    private void InitializeIcon()
    {
        // Look for overlay icon in these locations:
        // 1. <InstallDir>/overlay.ico
        // 2. <InstallDir>/soulman.ico
        // 3. Embedded in our DLL (fallback)
        
        if (InstallDir != null)
        {
            var overlayPath = Path.Combine(InstallDir, "overlay.ico");
            if (File.Exists(overlayPath))
            {
                _iconPath = overlayPath;
                _iconIndex = 0;
                return;
            }
            
            var mainIconPath = Path.Combine(InstallDir, "soulman.ico");
            if (File.Exists(mainIconPath))
            {
                _iconPath = mainIconPath;
                _iconIndex = 0; // Use first icon (or could use a specific index)
                return;
            }
        }
        
        // Fall back to DLL - no separate icon file
        _iconPath = null;
        _iconIndex = 0;
    }
    
    private string[]? GetBlacklist()
    {
        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            if (_cachedBlacklist != null && now - _lastRefresh < CacheExpiry)
                return _cachedBlacklist;
            
            try
            {
                var settings = LoadSettings();
                _cachedBlacklist = settings?.PurgedPaths ?? Array.Empty<string>();
                _lastRefresh = now;
            }
            catch
            {
                // Keep existing cache or empty
                _cachedBlacklist ??= Array.Empty<string>();
            }
            
            return _cachedBlacklist;
        }
    }
    
    private static SoulmanSettings? LoadSettings()
    {
        var path = SettingsPath;
        if (!File.Exists(path))
            return null;
        
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            
            if (!doc.RootElement.TryGetProperty("Soulman", out var sec))
                return null;
            
            var s = new SoulmanSettings();
            if (sec.TryGetProperty("DestinationPath", out var v)) s.DestinationPath = v.GetString();
            if (sec.TryGetProperty("MovieDestinationPath", out v)) s.MovieDestinationPath = v.GetString();
            if (sec.TryGetProperty("TvDestinationPath", out v)) s.TvDestinationPath = v.GetString();
            if (sec.TryGetProperty("PurgedPaths", out v))
            {
                s.PurgedPaths = v.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(x => x.Length > 0)
                    .ToArray();
            }
            return s;
        }
        catch
        {
            return null;
        }
    }
    
    private static string? ResolveToSyncPath(string absolutePath, SoulmanSettings settings)
    {
        static string? Rel(string path, string? root, string prefix)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            try
            {
                var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return null;
                var rel = path[r.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                return $"{prefix}/{rel}";
            }
            catch { return null; }
        }
        
        return Rel(absolutePath, settings.DestinationPath, "Music")
            ?? Rel(absolutePath, settings.MovieDestinationPath, "Movies")
            ?? Rel(absolutePath, settings.TvDestinationPath, "TV");
    }
    
    private static string GetSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Soulman", SettingsFileName);
    }
    
    private static string? GetInstallDirectory()
    {
        // Try to find the Soulman installation directory
        // 1. Same directory as our DLL
        // 2. Common Program Files
        // 3. Program Files
        
        try
        {
            var dllPath = typeof(SoulmanBlacklistOverlay).Assembly.Location;
            if (!string.IsNullOrEmpty(dllPath) && File.Exists(dllPath))
                return Path.GetDirectoryName(dllPath);
        }
        catch { }
        
        // Fall back to standard locations
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var installDir = Path.Combine(programFiles, "Soulman");
        if (Directory.Exists(installDir))
            return installDir;
        
        return null;
    }
    
    public void Dispose()
    {
        // Nothing to dispose
    }
    
    // Registration functions (called by regasm/regsvr32)
    [ComRegisterFunction]
    public static void Register(Type t)
    {
        // Register the COM class
        var keyPath = $@"Software\Classes\CLSID\{{{ClassGuid}}}";
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(keyPath);
        key.SetValue("", OverlayName);
        
        // Register as icon overlay handler
        var overlayPath = $@"Software\Microsoft\Windows\CurrentVersion\ShellIconOverlayIdentifiers\{OverlayName}";
        using var overlayKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(overlayPath);
        overlayKey.SetValue("", ClassGuid.ToString("B")); // Format: {guid}
        
        // InprocServer32 is set by regasm
    }
    
    [ComUnregisterFunction]
    public static void Unregister(Type t)
    {
        // Remove overlay handler registration
        try
        {
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\ShellIconOverlayIdentifiers\{OverlayName}",
                false);
        }
        catch { }
        
        // CLSID cleanup is handled by regasm
    }
}

/// <summary>
/// Soulman settings model (subset needed for overlay).
/// Must be a separate copy without external dependencies since this runs in explorer.exe.
/// </summary>
internal sealed class SoulmanSettings
{
    public string? DestinationPath { get; set; }
    public string? MovieDestinationPath { get; set; }
    public string? TvDestinationPath { get; set; }
    public string[]? PurgedPaths { get; set; }
}

/// <summary>
/// Shell icon overlay identifier interface.
/// This is the COM interface that Windows Explorer calls to check for overlays.
/// </summary>
[ComImport]
[Guid("0c6a4200-c589-11d0-999a-00c04fd655e1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellIconOverlayIdentifier
{
    [PreserveSig]
    int GetOverlayInfo([Out] StringBuilder iconFile, int iconFileCount, out int iconIndex, out uint flags);
    
    [PreserveSig]
    int GetPriority(out int priority);
    
    [PreserveSig]
    int IsMemberOf([MarshalAs(UnmanagedType.LPWStr)] string path, uint attributes);
}