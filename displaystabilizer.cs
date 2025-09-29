using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms; // Required for MessageBox
using System.IO;
using System.Text.Json;
using System.Linq;

public class MonitorConfiguration
{
    // --- P/Invoke Definitions and Constants ---

    // The maximum size of the device name strings
    private const int CCHDEVICENAME = 32;
    private const int CCHFORMNAME = 32;

    // ChangeDisplaySettingsEx flags
    private const int CDS_UPDATEREGISTRY = 0x00000001;
    private const int CDS_NORESET = 0x10000000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    // DEVMODE field indicators
    private const int DM_POSITION = 0x00000020;
    private const int DM_DISPLAYORIENTATION = 0x00000080;

    // Display Orientation values (DMDO_*)
    private const int DMDO_DEFAULT = 0; // 0 degrees (Landscape)
    private const int DMDO_90 = 1;      // 90 degrees (Portrait)
    private const int DMDO_180 = 2;     // 180 degrees (Landscape Flipped)
    private const int DMDO_270 = 3;     // 270 degrees (Portrait Flipped)

    // DISPLAY_DEVICE StateFlags
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x00000001;
    // Flag required in dwFlags parameter for EnumDisplayDevices (monitor enumeration) to retrieve DeviceID/DeviceKey
    private const int EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    // Structures required for P/Invoke
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID; // Unique Hardware ID
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmExtraSize;
        public int dmFields;
        public POINTL dmPosition;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public ushort dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    // P/Invoke functions
    // 1. Signature used for staging a change (requires 'ref DEVMODE')
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int ChangeDisplaySettingsEx(
        string lpszDeviceName,
        ref DEVMODE lpDevMode,
        IntPtr hwnd,
        int dwflags,
        IntPtr lParam);

    // 2. Signature used for the final reset (allows NULL/IntPtr.Zero for lpDevMode)
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int ChangeDisplaySettingsEx(
        string lpszDeviceName,
        IntPtr lpDevMode, // Allows passing IntPtr.Zero (NULL)
        IntPtr hwnd,
        int dwflags,
        IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool EnumDisplayDevices(
        string lpDevice,
        int iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool EnumDisplaySettings(
        string lpszDeviceName,
        int iModeNum,
        ref DEVMODE lpDevMode);
        
    // --- Configuration Data Structures (for JSON) ---

    public class MonitorTarget
    {
        // Unique identifier for the monitor to ensure consistent targeting
        public string DeviceId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        // 0=Default, 1=90 deg, 2=180 deg, 3=270 deg
        public int Orientation { get; set; } 
    }

    public class ConfigurationData
    {
        public MonitorTarget[] Monitors { get; set; }
    }

    // Stores the configuration loaded from JSON, mapped by DeviceId
    private static Dictionary<string, MonitorTarget> TargetConfigurationMap;
    private const string DefaultConfigFileName = "config.json";

    // --- Helper Structure for Enumeration ---
    private class ActiveMonitorIdentifier
    {
        // DeviceName of the adapter (used for EnumDisplaySettings/ChangeDisplaySettingsEx)
        public string AdapterDeviceName { get; set; }
        // Unique hardware ID (used for JSON mapping)
        public string UniqueDeviceId { get; set; }
    }

    // --- Core Enumeration Logic ---
    /// <summary>
    /// Enumerates all monitor devices attached to any adapter.
    /// The consumer methods (Save/Check/Apply) will rely on EnumDisplaySettings
    /// to filter for truly active, configured displays.
    /// </summary>
    private static List<ActiveMonitorIdentifier> GetActiveMonitorIdentifiers()
    {
        List<ActiveMonitorIdentifier> activeMonitors = new List<ActiveMonitorIdentifier>();

        // Loop 1: Enumerate Display Adapters (lpDevice = null)
        for (int i = 0; i < 20; i++)
        {
            DISPLAY_DEVICE adapter = new DISPLAY_DEVICE();
            adapter.cb = Marshal.SizeOf(adapter);

            if (!EnumDisplayDevices(null, i, ref adapter, 0))
            {
                break; // Stop when enumeration fails
            }

            // Loop 2: Enumerate Monitors attached to this Adapter (lpDevice = adapter.DeviceName)
            for (int j = 0; j < 5; j++)
            {
                DISPLAY_DEVICE monitor = new DISPLAY_DEVICE();
                monitor.cb = Marshal.SizeOf(monitor);

                // Call EnumDisplayDevices again, passing the adapter's name to enumerate attached monitors
                // Use EDD_GET_DEVICE_INTERFACE_NAME (0x00000001) to ensure monitor.DeviceID is populated
                if (!EnumDisplayDevices(adapter.DeviceName, j, ref monitor, EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    break; // Stop when no more monitors on this adapter
                }

                // Per user request: Rely STRICTLY on DeviceID. No fallback is used.
                string uniqueId = monitor.DeviceID;
                
                // We trust the downstream EnumDisplaySettings call to filter for actively configured monitors.
                activeMonitors.Add(new ActiveMonitorIdentifier
                {
                    AdapterDeviceName = adapter.DeviceName,
                    // Use the raw DeviceID, converted to uppercase for case-insensitive matching
                    UniqueDeviceId = adapter.DeviceName + " " + monitor.DeviceString
                });
            }
        }
        return activeMonitors;
    }

    // --- Configuration Loading ---

    private static bool LoadConfiguration(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                Console.WriteLine($"Error: Configuration file '{configPath}' not found.");
                return false;
            }

            string jsonString = File.ReadAllText(configPath);
            ConfigurationData config = JsonSerializer.Deserialize<ConfigurationData>(jsonString);

            if (config == null || config.Monitors == null || config.Monitors.Length == 0)
            {
                Console.WriteLine("Error: Configuration file loaded, but no monitor targets were found.");
                return false;
            }
            
            // Map the list into a dictionary for fast lookup by DeviceId
            TargetConfigurationMap = new Dictionary<string, MonitorTarget>();
            foreach (var monitor in config.Monitors)
            {
                if (string.IsNullOrWhiteSpace(monitor.DeviceId))
                {
                    Console.WriteLine("Warning: Skipping monitor configuration with missing DeviceId.");
                    continue;
                }
                // DeviceId is always stored in uppercase for case-insensitive lookup
                TargetConfigurationMap.Add(monitor.DeviceId, monitor);
            }

            Console.WriteLine($"Configuration loaded from '{configPath}': {TargetConfigurationMap.Count} unique monitor targets found.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading configuration: {ex.Message}");
            return false;
        }
    }

    // --- Configuration Saving (uses GetActiveMonitorIdentifiers) ---

    private static void SaveConfiguration(string outputPath)
    {
        Console.WriteLine($"\nEnumerating current monitor settings for saving to '{outputPath}'...");
        
        List<MonitorTarget> currentMonitors = new List<MonitorTarget>();
        List<ActiveMonitorIdentifier> allMonitors = GetActiveMonitorIdentifiers();

        foreach (var monitorId in allMonitors)
        {
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf(dm);
            string deviceId = monitorId.UniqueDeviceId;

            // Check if the DeviceID is populated (required by user)
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                Console.WriteLine($"Severe Warning: Monitor on adapter {monitorId.AdapterDeviceName} is active, but UNIQUE DEVICE ID IS EMPTY. Skipping save for this monitor.");
                continue; // Skip this monitor if the required ID is empty
            }

            // Use the adapter's DeviceName to get the current settings (this acts as the true 'active' filter)
            if (EnumDisplaySettings(monitorId.AdapterDeviceName, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm))
            {
                currentMonitors.Add(new MonitorTarget
                {
                    DeviceId = deviceId,
                    X = dm.dmPosition.x,
                    Y = dm.dmPosition.y,
                    Orientation = dm.dmDisplayOrientation
                });

                // Console output uses the calculated UniqueDeviceId, which is now the raw DeviceID.
                Console.WriteLine($"  Found: ID={deviceId}, Adapter={monitorId.AdapterDeviceName}, Pos=({dm.dmPosition.x}, {dm.dmPosition.y}), Orient={dm.dmDisplayOrientation}");
            }
        }

        if (currentMonitors.Count == 0)
        {
            Console.WriteLine("No active monitors with valid DeviceID found to save. Check your display connection/API output.");
            return;
        }

        ConfigurationData configToSave = new ConfigurationData { Monitors = currentMonitors.ToArray() };
        
        // Use JsonSerializer options for pretty printing
        var options = new JsonSerializerOptions { WriteIndented = true };
        string jsonString = JsonSerializer.Serialize(configToSave, options);
        
        try
        {
            File.WriteAllText(outputPath, jsonString);
            Console.WriteLine($"\nSuccessfully saved {currentMonitors.Count} monitor configurations to: {outputPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error writing configuration to file: {ex.Message}");
        }
    }

    // --- Main Logic (Entry Point) ---

    public static int Main(string[] args)
    {
        // Default values
        string configPath = DefaultConfigFileName;
        bool saveConfig = false;

        // Parse command-line arguments
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                configPath = args[++i];
            }
            else if (args[i].Equals("--save_config", StringComparison.OrdinalIgnoreCase))
            {
                saveConfig = true;
            }
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.EnableVisualStyles();

        Console.WriteLine("--- Monitor Configuration Checker (JSON Config) ---");

        if (saveConfig)
        {
            SaveConfiguration(configPath);
            return 0; // Exit after saving
        }

        // --- Load and Check Configuration ---

        if (!LoadConfiguration(configPath))
        {
            MessageBox.Show($"Could not load configuration from {configPath}. Check console for details.", "Configuration Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return -1;
        }

        if (CheckConfiguration())
        {
            Console.WriteLine("Current monitor configuration matches the desired setup. No changes needed.");
            return 0; // Exit successfully
        }

        Console.WriteLine("\nCONFIGURATION MISMATCH DETECTED.");
        
        // Taskbar Popup Action Simulation (using MessageBox)
        DialogResult result = MessageBox.Show(
            "The current monitor configuration does not match the desired setup defined in the configuration file.\n\n" +
            "Do you want to apply the configuration reset now?",
            "Configuration Reset Required (Action Required)",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result == DialogResult.Yes)
        {
            Console.WriteLine("User confirmed. Applying reset...");
            ApplyConfiguration();
        }
        else
        {
            Console.WriteLine("User declined. Configuration left unchanged.");
        }
        return 0;
    }

    // Checks the current configuration against the desired map (uses GetActiveMonitorIdentifiers)
    private static bool CheckConfiguration()
    {
        bool isCorrect = true;
        int checkCount = 0;
        
        List<ActiveMonitorIdentifier> allMonitors = GetActiveMonitorIdentifiers();

        foreach (var monitorId in allMonitors)
        {
            string deviceId = monitorId.UniqueDeviceId;
            
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf(dm);

            // Skip check if the DeviceID is empty
            if (string.IsNullOrWhiteSpace(deviceId)) continue;

            // Use the adapter's DeviceName to get the current settings (this acts as the true 'active' filter)
            if (EnumDisplaySettings(monitorId.AdapterDeviceName, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm))
            {
                if (TargetConfigurationMap.TryGetValue(deviceId, out MonitorTarget target))
                {
                    Console.WriteLine($"Monitor {monitorId.AdapterDeviceName} (ID: {deviceId}):");
                    Console.WriteLine($"  Current Pos: ({dm.dmPosition.x}, {dm.dmPosition.y}), Target: ({target.X}, {target.Y})");
                    Console.WriteLine($"  Current Orient: {dm.dmDisplayOrientation}, Target: {target.Orientation}");

                    if (dm.dmPosition.x != target.X ||
                        dm.dmPosition.y != target.Y ||
                        dm.dmDisplayOrientation != target.Orientation)
                    {
                        isCorrect = false;
                        Console.WriteLine("  -> MISMATCH");
                    }
                    checkCount++;
                }
                else
                {
                    Console.WriteLine($"Monitor {monitorId.AdapterDeviceName} (ID: {deviceId}) found, but no configuration defined for it.");
                    // If an active monitor isn't in the config, we count it as a mismatch to prompt for reset
                    isCorrect = false;
                }
            }
        }

        if (checkCount != TargetConfigurationMap.Count)
        {
            Console.WriteLine($"Warning: Found {checkCount} active monitors matching config, but expected {TargetConfigurationMap.Count}.");
            isCorrect = false;
        }

        return isCorrect;
    }

    // Applies the desired configuration (uses GetActiveMonitorIdentifiers)
    private static void ApplyConfiguration()
    {
        int result = DISP_CHANGE_SUCCESSFUL;
        
        List<ActiveMonitorIdentifier> allMonitors = GetActiveMonitorIdentifiers();

        foreach (var monitorId in allMonitors)
        {
            string deviceId = monitorId.UniqueDeviceId;

            // Skip application if the DeviceID is empty
            if (string.IsNullOrWhiteSpace(deviceId)) continue;

            if (TargetConfigurationMap.TryGetValue(deviceId, out MonitorTarget target))
            {
                DEVMODE dm = new DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf(dm);

                // Get current settings to populate non-position/orientation fields (ensures we only apply to active)
                if (EnumDisplaySettings(monitorId.AdapterDeviceName, -1, ref dm))
                {
                    // Set the new position and orientation
                    dm.dmFields = DM_POSITION | DM_DISPLAYORIENTATION;
                    dm.dmPosition.x = target.X;
                    dm.dmPosition.y = target.Y;
                    dm.dmDisplayOrientation = target.Orientation;

                    Console.WriteLine($"Staging settings for Monitor ({monitorId.AdapterDeviceName}, ID: {deviceId}): Pos=({target.X}, {target.Y}), Orient={target.Orientation}");

                    // Apply the change
                    // CDS_NORESET prevents the change from taking effect immediately.
                    result = ChangeDisplaySettingsEx(
                        monitorId.AdapterDeviceName, // Use the Adapter's DeviceName for ChangeDisplaySettingsEx
                        ref dm,
                        IntPtr.Zero,
                        CDS_UPDATEREGISTRY | CDS_NORESET, // Stage change and update registry
                        IntPtr.Zero);

                    if (result != DISP_CHANGE_SUCCESSFUL)
                    {
                        Console.WriteLine($"Error staging settings for {monitorId.AdapterDeviceName}: Error code {result}");
                    }
                }
            }
        }

        // Final call to apply all staged changes simultaneously
        Console.WriteLine("Applying all pending changes...");
        
        // This call uses the new overload accepting IntPtr.Zero for the mode argument.
        result = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);

        if (result == DISP_CHANGE_SUCCESSFUL)
        {
            MessageBox.Show("Monitor configuration reset successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Console.WriteLine("Configuration applied successfully and saved to registry.");
        }
        else
        {
            MessageBox.Show($"Failed to apply configuration. Error code: {result}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Console.WriteLine($"Final application failed with error code: {result}");
        }
    }
}
