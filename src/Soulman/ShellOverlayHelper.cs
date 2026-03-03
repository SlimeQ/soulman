using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Soulman;

/// <summary>
/// Helper for Windows shell icon overlay registration and notification.
/// </summary>
internal static class ShellOverlayHelper
{
    // SHChangeNotify flags
    private const uint SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNF_PATH = 0x00000001;
    private const uint SHCNF_FLUSH = 0x10000000;
    
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string? dwItem1, string? dwItem2);
    
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHChangeNotifyRegisterThread(int tid);
    
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetModuleHandle(string? lpModuleName);
    
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadString(int hInstance, uint uID, StringBuilder lpBuffer, int nBufferMax);
    
    /// <summary>
    /// Notifies Windows Explorer that a file/folder has changed, triggering overlay refresh.
    /// Call this after modifying the blacklist.
    /// </summary>
    public static void NotifyUpdate(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        
        try
        {
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATH | SHCNF_FLUSH, path, null);
        }
        catch
        {
            // Best effort - Explorer update is not critical
        }
    }
    
    /// <summary>
    /// Notifies Windows Explorer to refresh all overlays.
    /// Call this after installing/uninstalling the overlay handler.
    /// </summary>
    public static void NotifyRefreshAll()
    {
        if (!OperatingSystem.IsWindows()) return;
        
        try
        {
            // This triggers Explorer to re-scan icon overlays by signaling a general update
            // Note: There's no direct API to refresh overlays, but updating any item can trigger re-evaluation
            // The most reliable way is to restart Explorer or use SHCNE_ASSOCCHANGED (but that's heavy)
            
            // Use SHCNE_ASSOCCHANGED to hint that file associations changed - this triggers overlay refresh
            const uint SHCNE_ASSOCCHANGED = 0x08000000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, null, null);
        }
        catch
        {
            // Best effort
        }
    }
    
    /// <summary>
    /// Installs the icon overlay COM handler.
    /// Requires the IconOverlay DLL to be built and present.
    /// </summary>
    public static (bool Success, string Message) InstallOverlay(string? dllPath = null)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Icon overlay is only supported on Windows.");
        
        // Find the COM host DLL
        var exeDir = AppContext.BaseDirectory;
        var comHostDll = dllPath ?? System.IO.Path.Combine(exeDir, "IconOverlay.comhost.dll");
        
        if (!System.IO.File.Exists(comHostDll))
        {
            return (false, $"Icon overlay DLL not found: {comHostDll}\n\nThe icon overlay must be built on Windows. Run the publish script on Windows to create it.");
        }
        
        // The actual COM registration is done via regasm or by the embedded [ComRegisterFunction]
        // For .NET 8 with EnableComHosting, we use regasm-style registration
        
        // Note: For shell extensions, the COM object must be registered in HKLM (requires admin)
        // The [ComRegisterFunction] in SoulmanBlacklistOverlay handles this
        
        return (true, $"Icon overlay DLL found at: {comHostDll}\n\nTo register, run:\n  regsvr32 \"{comHostDll}\"\n\nOr use an elevated PowerShell:\n  regasm \"{comHostDll}\" /codebase\n\nNote: Registration requires administrator privileges.");
    }
    
    /// <summary>
    /// Uninstalls the icon overlay COM handler.
    /// </summary>
    public static (bool Success, string Message) UninstallOverlay()
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Icon overlay is only supported on Windows.");
        
        // The [ComUnregisterFunction] in SoulmanBlacklistOverlay handles HKLM cleanup
        
        return (true, "To unregister the icon overlay, run an elevated command:\n  regsvr32 /u \"path\\to\\IconOverlay.comhost.dll\"\n\nor PowerShell:\n  regasm /u \"path\\to\\IconOverlay.comhost.dll\"");
    }
}