using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using GTA.Math;
using GTA.UI;
using LemonUI;
using LemonUI.Menus;
using LemonUI.Elements;
using ExtendedLSC.ManualTransmission;
using ExtendedLSC.WheelFitment;

namespace ExtendedLSC
{
    /// <summary>
    /// ikt's Manual Transmission (Gears.asi) integration via LoadLibrary/GetProcAddress
    /// API reference: https://github.com/ikt32/GTAVManualTransmission
    /// </summary>
    public static class ManualTransmissionAPI
    {
        private static bool? _isAvailable = null;
        private static bool _initialized = false;
        private static IntPtr _moduleHandle = IntPtr.Zero;
        private static string _lastError = null;

        // Logging delegate - set by Main class
        public static Action<string> Log { get; set; }

        // Kernel32 imports for dynamic loading
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModules(IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded);

        [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, [Out] System.Text.StringBuilder lpBaseName, uint nSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        // Function delegates
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr MT_GetVersionDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool MT_IsActiveDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MT_SetActiveDelegate(bool active);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool MT_NeutralGearDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int MT_GetShiftModeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MT_SetShiftModeDelegate(int mode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int MT_GetShiftIndicatorDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int MT_GetManagedVehicleDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MT_AddIgnoreVehicleDelegate(int vehicle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MT_DelIgnoreVehicleDelegate(int vehicle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void MT_ClearIgnoredVehiclesDelegate();

        // Function pointers
        private static MT_GetVersionDelegate _getVersion;
        private static MT_IsActiveDelegate _isActive;
        private static MT_SetActiveDelegate _setActive;
        private static MT_NeutralGearDelegate _neutralGear;
        private static MT_GetShiftModeDelegate _getShiftMode;
        private static MT_SetShiftModeDelegate _setShiftMode;
        private static MT_GetShiftIndicatorDelegate _getShiftIndicator;
        private static MT_GetManagedVehicleDelegate _getManagedVehicle;
        private static MT_AddIgnoreVehicleDelegate _addIgnoreVehicle;
        private static MT_DelIgnoreVehicleDelegate _delIgnoreVehicle;
        private static MT_ClearIgnoredVehiclesDelegate _clearIgnoredVehicles;

        /// <summary>
        /// Get the last error message for debugging
        /// </summary>
        public static string LastError => _lastError;

        /// <summary>
        /// Initialize the API by getting the module handle and function pointers
        /// Gears.asi is already loaded by the ASI loader, we just need to get its handle
        /// </summary>
        private static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Log?.Invoke("[MT API] Initializing Manual Transmission API...");

                // First, try to find Gears.asi by enumerating all loaded modules
                _moduleHandle = FindModuleByName("Gears.asi");

                if (_moduleHandle == IntPtr.Zero)
                {
                    // Try GetModuleHandle with various names
                    string[] names = { "Gears.asi", "Gears", "gears.asi", "gears" };
                    foreach (var name in names)
                    {
                        _moduleHandle = GetModuleHandle(name);
                        if (_moduleHandle != IntPtr.Zero)
                        {
                            Log?.Invoke($"[MT API] Found via GetModuleHandle(\"{name}\")");
                            break;
                        }
                    }
                }

                if (_moduleHandle == IntPtr.Zero)
                {
                    // Last resort: try LoadLibrary with full path
                    string gtaPath = AppDomain.CurrentDomain.BaseDirectory;
                    string fullPath = Path.Combine(gtaPath, "Gears.asi");
                    if (File.Exists(fullPath))
                    {
                        Log?.Invoke($"[MT API] Trying LoadLibrary: {fullPath}");
                        _moduleHandle = LoadLibrary(fullPath);
                        if (_moduleHandle != IntPtr.Zero)
                        {
                            Log?.Invoke("[MT API] Loaded via LoadLibrary");
                        }
                    }
                    else
                    {
                        _lastError = $"Gears.asi not found at: {fullPath}";
                        Log?.Invoke($"[MT API] {_lastError}");
                    }
                }

                if (_moduleHandle == IntPtr.Zero)
                {
                    _lastError = "Could not find or load Gears.asi module";
                    Log?.Invoke($"[MT API] {_lastError}");
                    _isAvailable = false;
                    return;
                }

                Log?.Invoke($"[MT API] Module handle: 0x{_moduleHandle.ToInt64():X}");

                // Get function pointers
                _getVersion = GetDelegate<MT_GetVersionDelegate>("MT_GetVersion");
                _isActive = GetDelegate<MT_IsActiveDelegate>("MT_IsActive");
                _setActive = GetDelegate<MT_SetActiveDelegate>("MT_SetActive");
                _neutralGear = GetDelegate<MT_NeutralGearDelegate>("MT_NeutralGear");
                _getShiftMode = GetDelegate<MT_GetShiftModeDelegate>("MT_GetShiftMode");
                _setShiftMode = GetDelegate<MT_SetShiftModeDelegate>("MT_SetShiftMode");
                _getShiftIndicator = GetDelegate<MT_GetShiftIndicatorDelegate>("MT_GetShiftIndicator");
                _getManagedVehicle = GetDelegate<MT_GetManagedVehicleDelegate>("MT_GetManagedVehicle");
                _addIgnoreVehicle = GetDelegate<MT_AddIgnoreVehicleDelegate>("MT_AddIgnoreVehicle");
                _delIgnoreVehicle = GetDelegate<MT_DelIgnoreVehicleDelegate>("MT_DelIgnoreVehicle");
                _clearIgnoredVehicles = GetDelegate<MT_ClearIgnoredVehiclesDelegate>("MT_ClearIgnoredVehicles");

                Log?.Invoke($"[MT API] GetVersion delegate: {(_getVersion != null ? "OK" : "NULL")}");

                // Test if the API works
                if (_getVersion != null)
                {
                    var ptr = _getVersion();
                    _isAvailable = ptr != IntPtr.Zero;
                    if (_isAvailable == true)
                    {
                        string ver = Marshal.PtrToStringAnsi(ptr);
                        Log?.Invoke($"[MT API] SUCCESS! Version: {ver}");
                    }
                    else
                    {
                        _lastError = "MT_GetVersion returned null";
                        Log?.Invoke($"[MT API] {_lastError}");
                    }
                }
                else
                {
                    _lastError = "Could not get MT_GetVersion function pointer";
                    Log?.Invoke($"[MT API] {_lastError}");
                    _isAvailable = false;
                }
            }
            catch (Exception ex)
            {
                _lastError = $"Exception: {ex.Message}";
                Log?.Invoke($"[MT API] {_lastError}");
                _isAvailable = false;
            }
        }

        /// <summary>
        /// Find a module by name by enumerating all loaded modules
        /// </summary>
        private static IntPtr FindModuleByName(string moduleName)
        {
            try
            {
                IntPtr hProcess = GetCurrentProcess();
                IntPtr[] modules = new IntPtr[1024];
                uint cbNeeded;

                if (EnumProcessModules(hProcess, modules, (uint)(modules.Length * IntPtr.Size), out cbNeeded))
                {
                    int count = (int)(cbNeeded / IntPtr.Size);
                    Log?.Invoke($"[MT API] Enumerating {count} loaded modules...");

                    var sb = new System.Text.StringBuilder(260);
                    for (int i = 0; i < count; i++)
                    {
                        sb.Clear();
                        if (GetModuleFileNameEx(hProcess, modules[i], sb, (uint)sb.Capacity) > 0)
                        {
                            string path = sb.ToString();
                            string name = Path.GetFileName(path);

                            // Log ASI files we find
                            if (name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase))
                            {
                                Log?.Invoke($"[MT API] Found ASI: {name}");
                            }

                            if (name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                            {
                                Log?.Invoke($"[MT API] MATCH! {path}");
                                return modules[i];
                            }
                        }
                    }
                }
                else
                {
                    Log?.Invoke($"[MT API] EnumProcessModules failed: {Marshal.GetLastWin32Error()}");
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[MT API] FindModuleByName error: {ex.Message}");
            }

            return IntPtr.Zero;
        }

        private static T GetDelegate<T>(string procName) where T : Delegate
        {
            if (_moduleHandle == IntPtr.Zero) return null;
            IntPtr procAddr = GetProcAddress(_moduleHandle, procName);
            if (procAddr == IntPtr.Zero) return null;
            return Marshal.GetDelegateForFunctionPointer<T>(procAddr);
        }

        /// <summary>
        /// Check if ikt's Manual Transmission mod (Gears.asi) is installed and API is working
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable == null)
                {
                    Initialize();
                }
                return _isAvailable ?? false;
            }
        }

        /// <summary>
        /// Get the mod version string
        /// </summary>
        public static string GetVersion()
        {
            if (!IsAvailable || _getVersion == null) return null;
            try
            {
                IntPtr ptr = _getVersion();
                return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Check if Manual Transmission is currently active (controlling the vehicle)
        /// </summary>
        public static bool IsActive()
        {
            if (!IsAvailable || _isActive == null) return false;
            try { return _isActive(); }
            catch { return false; }
        }

        /// <summary>
        /// Enable or disable Manual Transmission
        /// </summary>
        public static void SetActive(bool active)
        {
            if (!IsAvailable || _setActive == null) return;
            try { _setActive(active); }
            catch { }
        }

        /// <summary>
        /// Check if currently in neutral gear
        /// </summary>
        public static bool InNeutral()
        {
            if (!IsAvailable || _neutralGear == null) return false;
            try { return _neutralGear(); }
            catch { return false; }
        }

        /// <summary>
        /// Shift modes: 1=Sequential, 2=H-Pattern, 3=Automatic
        /// </summary>
        public enum ShiftMode
        {
            Sequential = 1,
            HPattern = 2,
            Automatic = 3
        }

        /// <summary>
        /// Get current shift mode
        /// </summary>
        public static ShiftMode GetShiftMode()
        {
            if (!IsAvailable || _getShiftMode == null) return ShiftMode.Automatic;
            try { return (ShiftMode)_getShiftMode(); }
            catch { return ShiftMode.Automatic; }
        }

        /// <summary>
        /// Set shift mode
        /// </summary>
        public static void SetShiftMode(ShiftMode mode)
        {
            if (!IsAvailable || _setShiftMode == null) return;
            try { _setShiftMode((int)mode); }
            catch { }
        }

        /// <summary>
        /// Get shift indicator: 0=None, 1=Shift Up, 2=Shift Down
        /// </summary>
        public static int GetShiftIndicator()
        {
            if (!IsAvailable || _getShiftIndicator == null) return 0;
            try { return _getShiftIndicator(); }
            catch { return 0; }
        }

        /// <summary>
        /// Get the vehicle handle currently managed by MT
        /// </summary>
        public static int GetManagedVehicle()
        {
            if (!IsAvailable || _getManagedVehicle == null) return 0;
            try { return _getManagedVehicle(); }
            catch { return 0; }
        }

        /// <summary>
        /// Tell MT to ignore a specific vehicle (for custom control)
        /// </summary>
        public static void AddIgnoreVehicle(int vehicleHandle)
        {
            if (!IsAvailable || _addIgnoreVehicle == null) return;
            try { _addIgnoreVehicle(vehicleHandle); }
            catch { }
        }

        /// <summary>
        /// Remove a vehicle from MT's ignore list
        /// </summary>
        public static void RemoveIgnoreVehicle(int vehicleHandle)
        {
            if (!IsAvailable || _delIgnoreVehicle == null) return;
            try { _delIgnoreVehicle(vehicleHandle); }
            catch { }
        }

        /// <summary>
        /// Clear all vehicles from MT's ignore list
        /// </summary>
        public static void ClearIgnoredVehicles()
        {
            if (!IsAvailable || _clearIgnoredVehicles == null) return;
            try { _clearIgnoredVehicles(); }
            catch { }
        }

        /// <summary>
        /// Get display name for shift mode
        /// </summary>
        public static string GetShiftModeName(ShiftMode mode)
        {
            switch (mode)
            {
                case ShiftMode.Sequential: return "Sequential";
                case ShiftMode.HPattern: return "H-Pattern";
                case ShiftMode.Automatic: return "Automatic";
                default: return "Unknown";
            }
        }
    }

    /// <summary>
    /// Extended Los Santos Customs - Main entry point
    /// Overlays a custom menu while blocking game input
    /// </summary>
    public partial class Main : Script
    {
        // Menu system
        private ObjectPool menuPool;
        private NativeMenu mainMenu;

        // Dynamic mod menus (rebuilt per vehicle) - flat structure
        private Dictionary<int, NativeMenu> modMenusByIndex = new Dictionary<int, NativeMenu>();
        private NativeMenu resprayMenu;
        private NativeMenu wheelsMenu;
        private List<NativeMenu> dynamicCategoryMenus = new List<NativeMenu>(); // User-created custom categories

        // Special menus that need refresh capability
        private NativeMenu hornMenu;
        private NativeMenu windowTintMenu;
        private NativeMenu plateMenu;
        private NativeMenu turboMenu;
        private NativeMenu headlightsMenu;

        // Config-based menus: Key = category path, Value = menu
        private Dictionary<string, NativeMenu> configMenusByPath = new Dictionary<string, NativeMenu>();

        // Track item ownership status for icon display (key = NativeItem, value = 1=installed, 2=owned-not-installed)
        private Dictionary<NativeItem, int> itemOwnershipStatus = new Dictionary<NativeItem, int>();
        private const int STATUS_INSTALLED = 1;
        private const int STATUS_OWNED = 2;

        // Track scroll position per menu (to match LemonUI's internal firstItem)
        private Dictionary<NativeMenu, int> menuFirstItem = new Dictionary<NativeMenu, int>();
        private Dictionary<NativeMenu, int> menuLastSelectedIndex = new Dictionary<NativeMenu, int>();

        // Track config item metadata for refresh: Key = NativeItem, Value = (categoryPath, itemValue)
        private Dictionary<NativeItem, (string categoryPath, int itemValue, int price)> configItemMetadata = new Dictionary<NativeItem, (string, int, int)>();

        // State
        private bool isMenuActive = false;
        private Vehicle currentVehicle = null;

        // ELSC Manual Transmission (our own implementation - no external dependencies)
        private ELSCTransmission elscTransmission = new ELSCTransmission();
        private TransmissionHUD transmissionHUD;
        private Vehicle lastTransmissionVehicle = null;

        // ELSC Wheel Fitment (VStancer-style wheel adjustments)
        private WheelFitment.WheelFitment wheelFitment = new WheelFitment.WheelFitment();
        private Vehicle lastFitmentVehicle = null;
        private bool _fitmentInitAllowed = true; // Disable if crashes occur
        // Auto-applies saved stances to specific cars within range (even when not driving them).
        private WheelFitment.VehicleStanceManager stanceManager = new WheelFitment.VehicleStanceManager();
        private bool _stanceMgrInit = false;
        // NFS-style drag gauge HUD (+ white-screen UI preview harness for design iteration).
        private ManualTransmission.DragHUD dragHUD = new ManualTransmission.DragHUD();

        // Manual transmission key binding setup state
        private int mtBindingState = 0; // 0=none, 1=waiting for shift up, 2=waiting for shift down
        private NativeItem mtBindingItem = null; // Reference to the menu item to update after binding

        // Transaction display (custom drawn to match GTA style)
        private int transactionAmount = 0;
        private int transactionStartTime = 0;
        private const int TRANSACTION_DISPLAY_DURATION = 4000; // 4 seconds

        // Consolidated Debug Menu (F7 to open, select tool, B to exit)
        private enum DebugMode { None, Menu, Resizer, SpriteBrowser, MenuPosition, InputTiming, StatsCalibration }
        private DebugMode activeDebugMode = DebugMode.None;
        private int debugMenuSelection = 0;
        private readonly string[] debugMenuOptions = { "Resizer", "Sprite Browser", "Menu Position", "Input Timing", "Stats Calibration", "Exit Debug" };
        private bool debugGameInputMode = false;  // R3 toggle: when true, allows game input and hides our menu for comparison
        private bool debugBlockBButton = false;    // Flag to block B button processing this frame

        // Resizer debug settings (text scales, sprite scales, element dimensions)
        private int resizerFieldIndex = 0;
        private float transactionTextScale = 0.5f;
        private float descriptionTextScale = 0.35f;
        private float spriteScale = 1.8f; // Multiplier for tick/ownership icons
        private float arrowSpriteScale = 1.5f;       // Multiplier for scroll arrow sprite
        private float statsRowHeight = 0.026f;       // Height of each stat row
        private float statsSegmentHeight = 0.007f;   // Height of stat bar segments
        private float statsSegmentGap = 0.001f;      // Gap between segments
        private float statsLabelScale = 0.35f;       // Text scale for stat labels
        private float statsPanelPadding = 0.011f;    // Padding inside stats panel
        private float statsContentOffsetY = 0.01f;   // Vertical offset for stat bars within panel
        private float statsBarInset = 0.012f;        // Inset from edges to squeeze bars from both sides
        private float statsTextLeftPadding = 0.005f; // Left padding for stat label text
        private float statsBarOffsetY = 0.006f;      // Vertical offset to center bars with text
        private List<string> activeResizerFields = new List<string>(); // Built dynamically

        // Light mode tracking (for continuous application of brake/reverse lights)
        private int currentLightMode = 0; // 0=Off, 6=Reverse, 7=Brake Lights

        // "Not enough cash" message (replaces description temporarily)
        private bool showNotEnoughCash = false;
        private int notEnoughCashStartTime = 0;
        private const int NOT_ENOUGH_CASH_DURATION = 5000; // 5 seconds
        private NativeItem notEnoughCashItem = null;
        private string notEnoughCashOriginalDesc = null;

        // Preview system - temporarily apply mods when hovering
        private bool isPreviewingMod = false;
        private int previewModIndex = -1;
        private int previewOriginalValue = -1;

        // Original vehicle stats (stored when entering performance mod menu)
        private float originalTopSpeed = 0f;
        private float originalAcceleration = 0f;
        private float originalBraking = 0f;
        private float originalTraction = 0f;
        private bool hasStoredOriginalStats = false;

        // Config
        private Keys menuKey = Keys.F5;
        // debugLogging is now in ModSettings

        // LSC detection
        private int lastInterior = 0;
        private static readonly int[] LSC_INTERIORS = { 39938 };
        private bool isInLSC = false;
        private bool waitingForVehicleStop = false;
        private Vector3 lastVehiclePos = Vector3.Zero;

        // Mechanic management
        private const bool ENABLE_MECHANIC_REPLACEMENT = false; // Disabled for UI alignment
        private Ped customMechanic = null;
        private Vector3 customMechanicPos = Vector3.Zero;

        // Walk-around camera
        private bool isWalkAroundActive = false;
        private Vehicle walkAroundVehicle = null;
        private Vector3 cameraOrbitPos = Vector3.Zero;  // Target camera orbit position
        private Vector3 smoothCamPos = Vector3.Zero;    // Smoothed/actual camera position
        private float smoothCamHeight = 1.5f;           // Smoothed camera height
        private Camera walkAroundCam = null;
        private float cameraHeight = 1.5f;
        private float lookOffsetH = 0f;  // Horizontal look offset (left/right from car center)
        private int cameraModeCooldown = 0;  // Prevent immediate re-entry
        private float vehicleCamMinDist = 1f;  // Dynamic based on vehicle size
        private float vehicleCamMaxDist = 4f;
        private float vehicleCamStartDist = 3f;
        private float vehicleHalfLength = 2.5f;  // For box path clamping
        private float vehicleHalfWidth = 1f;
        private const float CAM_SMOOTH_SPEED = 0.1f;  // Smoothing factor (0-1, lower = smoother)

        // Preset camera positions for mod categories (built dynamically per vehicle)
        private int currentPresetIndex = -1;  // -1 = free roam
        private bool isInPresetMode = false;
        private bool isNavigatingMenu = false;  // Flag to prevent CloseMenu during menu navigation
        private NativeMenu wheelFitmentParent = null; // Parent (Wheels) menu to restore when the fitment menu closes
        private List<ModCameraPreset> availablePresets = new List<ModCameraPreset>();

        // Custom menu input handling (native GTA-style acceleration)
        private int inputHeldSince = 0;           // Game time when direction was first held
        private int inputLastRepeat = 0;          // Game time of last repeat action
        private int inputRepeatCount = 0;         // How many times input has repeated
        private GTA.Control inputHeldDirection = GTA.Control.FrontendPause;  // Which direction is being held (FrontendPause = none)

        // Tunable input timing values (use debug mode to adjust)
        private int inputInitialDelay = 82;       // ms before first repeat
        private int inputSlowRepeat = 255;        // ms between repeats for first few
        private int inputFastRepeat = 83;         // ms between repeats after acceleration
        private int inputAccelThreshold = 3;      // repeats before switching to fast

        // Input timing debug settings
        private int inputTimingDebugSetting = 0;  // 0=InitialDelay, 1=SlowRepeat, 2=FastRepeat, 3=AccelThreshold
        private readonly string[] inputTimingSettingNames = { "Initial Delay", "Slow Repeat", "Fast Repeat", "Accel Threshold" };

        // Stats calibration debug settings (divisors and multipliers for each stat bar)
        // Divisors based on actual handling.meta max values across all GTA V vehicles
        private int statsCalibrationSetting = 0;  // 0-7 for 4 stats x 2 values each
        private float statsTopSpeedDiv = 65f;     // Max ~65 m/s (supercars with upgrades)
        private float statsTopSpeedMult = 0.95f;
        private float statsAccelDiv = 0.50f;      // Max ~0.50 (fInitialDriveForce with upgrades)
        private float statsAccelMult = 0.95f;
        private float statsBrakingDiv = 1.5f;     // Max ~1.5 (fBrakeForce)
        private float statsBrakingMult = 0.95f;
        private float statsTractionDiv = 3.0f;    // Max ~3.0 (fTractionCurveMax)
        private float statsTractionMult = 0.95f;
        private readonly string[] statsCalibrationNames = {
            "TopSpeed Div", "TopSpeed Mult",
            "Accel Div", "Accel Mult",
            "Braking Div", "Braking Mult",
            "Traction Div", "Traction Mult"
        };

        // Menu Position debug settings - multiple UI elements can be positioned
        private enum UIElement { Menu, ScrollArrows, Description, StatsPanel }
        private UIElement selectedUIElement = UIElement.Menu;
        private readonly string[] uiElementNames = { "Menu", "Scroll Arrows", "Description", "Stats Panel" };

        // Offsets for each UI element (relative to menu position)
        private float scrollArrowsOffsetX = 0f;
        private float scrollArrowsOffsetY = -0.004f;
        private float descriptionOffsetX = 0f;
        private float descriptionOffsetY = -0.0124f;
        private float statsPanelOffsetX = 0f;
        private float statsPanelOffsetY = -0.0035f;

        // Scale for custom UI elements (width, height multipliers)
        private float scrollArrowsScaleW = 1f;
        private float scrollArrowsScaleH = 1f;
        private float descriptionScaleW = 1f;
        private float descriptionScaleH = 1.43f;
        private float statsPanelScaleW = 1f;
        private float statsPanelScaleH = 1.31f;

        // Menu Position debug: position vs scale mode, speed control
        private bool menuPositionScaleMode = false;  // false = position, true = scale
        private float menuPositionSpeed = 1f;        // Right stick adjusts this (0.1 to 5.0)
        private bool menuPositionUniformScale = true; // L3 toggle: true = both axes scale together

        // Description editor state (editorModeEnabled is in ModSettings)
        private bool isEditingDescription = false;
        private string editingCategoryName = null;
        private int keyboardCheckCooldown = 0;

        // Sprite browser debug settings
        private int spriteBrowserPage = 0;
        private int spriteBrowserDictIndex = 0;
        private static readonly (string dict, string[] sprites)[] SpriteDictionaries = new[]
        {
            ("CommonMenu", new[] {
                "arrowleft", "arrowright", "shop_arrows_upANDdown", "shop_box_tick", "shop_box_tickb",
                "shop_box_cross", "shop_box_crossb", "shop_box_blank", "shop_box_blankb", "shop_lock",
                "shop_ammo_icon_a", "shop_ammo_icon_b", "shop_armour_icon_a", "shop_armour_icon_b",
                "shop_clothing_icon_a", "shop_clothing_icon_b", "shop_franklin_icon_a", "shop_franklin_icon_b",
                "shop_garage_icon_a", "shop_garage_icon_b", "shop_gunclub_icon_a", "shop_gunclub_icon_b",
                "shop_health_icon_a", "shop_health_icon_b", "shop_makeup_icon_a", "shop_makeup_icon_b",
                "shop_mask_icon_a", "shop_mask_icon_b", "shop_michael_icon_a", "shop_michael_icon_b",
                "shop_new_star", "shop_tattoos_icon_a", "shop_tattoos_icon_b", "shop_trevor_icon_a", "shop_trevor_icon_b",
                "gradient_bgd", "gradient_nav", "header_gradient", "header_gradient_script"
            }),
            ("mpcarhud", new[] {
                "transport_car_icon", "transport_bike_icon", "transport_boat_icon", "transport_heli_icon",
                "transport_plane_icon", "mp_rankbarfill", "albany", "annis", "banshee", "benefactor",
                "bf", "bollokan", "bravado", "brute", "buckingham", "canis", "cheval", "classique",
                "coil", "declasse", "dewbauchee", "dinka", "dundreary", "emperor", "enus", "fathom",
                "gallilvanter", "grotti", "hijak", "hvy", "imponte", "invetero", "jacksheepe", "jobuilt",
                "karin", "lampadati", "lcc", "maibatsu", "mammoth", "maxwell", "mtl", "nagasaki",
                "obey", "ocelot", "overflod", "pedal_and_metal", "pegassi", "pfister", "principe",
                "progen", "schyster", "shitzu", "speedophile", "stanley", "steel_horse", "truffade",
                "ubermacht", "vapid", "vulcar", "weeny", "western", "western_motorcycle_company", "willard", "ztype"
            }),
            ("mpinventory", new[] {
                "mp_specitem_coke", "mp_specitem_heroin", "mp_specitem_meth", "mp_specitem_weed",
                "mp_specitem_cash", "mp_specitem_weapons", "mp_specitem_crates"
            }),
            ("mpmissionend", new[] {
                "tickedtickbox", "emptytickbox"
            }),
            ("shopui_title_carmod", new[] { "shopui_title_carmod" }),
            ("shopui_title_carmod2", new[] { "shopui_title_carmod2" })
        };
        private DateTime lbPressStart = DateTime.MinValue;
        private DateTime xPressStart = DateTime.MinValue;
        private DateTime aPressStart = DateTime.MinValue;
        private DateTime bPressStart = DateTime.MinValue;
        private DateTime yPressStart = DateTime.MinValue;
        private bool wasLbPressed = false;
        private bool wasXPressed = false;
        private bool wasAPressed = false;
        private bool wasBPressed = false;
        private bool wasYPressed = false;
        private int debugTickCounter = 0;
        private DateTime lastDebugStatusLog = DateTime.MinValue;
        private DateTime lastLbHeldTime = DateTime.MinValue; // Track when LB was last held (for combo detection)

        // Struct to hold preset info
        private struct ModCameraPreset
        {
            public string Name;
            public VehicleModType ModType;
            public Vector3 LocalCameraOffset;  // X = left/right, Y = front/back, Z = up/down (relative to vehicle dimensions)

            public ModCameraPreset(string name, VehicleModType modType, Vector3 offset)
            {
                Name = name;
                ModType = modType;
                LocalCameraOffset = offset;
            }
        }

        // All possible mod camera positions (will filter to available ones per vehicle)
        private static readonly ModCameraPreset[] AllModPresets = new ModCameraPreset[]
        {
            new ModCameraPreset("Front Bumper", VehicleModType.FrontBumper, new Vector3(0, 1f, -0.2f)),
            new ModCameraPreset("Rear Bumper", VehicleModType.RearBumper, new Vector3(0, -1f, -0.1f)),
            new ModCameraPreset("Hood", VehicleModType.Hood, new Vector3(0.4f, 0.8f, 0.3f)),
            new ModCameraPreset("Spoiler", VehicleModType.Spoilers, new Vector3(-0.4f, -0.8f, 0.4f)),
            new ModCameraPreset("Roof", VehicleModType.Roof, new Vector3(-0.7f, 0.2f, 0.8f)),
            new ModCameraPreset("Side Skirts", VehicleModType.SideSkirt, new Vector3(-1f, 0, -0.2f)),
            new ModCameraPreset("Grille", VehicleModType.Grille, new Vector3(0, 0.9f, 0)),
            new ModCameraPreset("Exhaust", VehicleModType.Exhaust, new Vector3(0.4f, -0.9f, -0.3f)),
            new ModCameraPreset("Fender", VehicleModType.Fender, new Vector3(-0.9f, 0.3f, 0.1f)),
            new ModCameraPreset("Roll Cage", VehicleModType.Frame, new Vector3(-0.6f, 0, 0.5f)),
            new ModCameraPreset("Engine", VehicleModType.Engine, new Vector3(0.3f, 0.7f, 0.4f)),
        };

        #region Manual Transmission Integration (ikt's Gears.asi)

        // Integration with ikt's Manual Transmission mod
        // All transmission logic is handled by Gears.asi - we just provide menu access
        // API: https://github.com/ikt32/GTAVManualTransmission

        #endregion

        public Main()
        {
            menuPool = new ObjectPool();

            // Initialize settings first (needed for logging)
            ModSettings.Log = Log;
            ModSettings.Load();

            // Initialize folder-based menu config
            MenuConfig.Log = Log;
            MenuConfig.Initialize();

            // Initialize vehicle save data (tracks purchased mods)
            VehicleSaveData.Log = Log;
            VehicleSaveData.Initialize();

            // Initialize Manual Transmission API logging (for ikt's mod if present)
            ManualTransmissionAPI.Log = Log;

            // Initialize ELSC's built-in manual transmission
            ELSCTransmission.Log = Log;
            VehicleMemory.Log = Log;
            ELSCTransmission.Initialize();
            transmissionHUD = new TransmissionHUD(elscTransmission);
            Log($"[ELSC MT] Built-in transmission available: {ELSCTransmission.IsAvailable}");

            // Initialize ELSC's wheel fitment system
            WheelBones.Log = Log;
            WheelFitment.WheelFitment.Log = Log;
            Log($"[ELSC Fitment] Wheel fitment system ready");

            // Per-specific-car stance manager (auto-applies saved stances to cars in range, even parked).
            // The player's CURRENT car is excluded — it's managed live by `wheelFitment`.
            ManualTransmission.DragHUD.Log = Log;
            dragHUD.Initialize();
            WheelFitment.VehicleStanceManager.Log = Log;
            stanceManager.ExcludeVehicle = () =>
            {
                var pp = Game.Player.Character;
                return (pp != null && pp.Exists() && pp.IsInVehicle()) ? pp.CurrentVehicle : null;
            };
            try { stanceManager.Initialize(); _stanceMgrInit = true; } catch (Exception ex) { Log($"[Stance] init failed: {ex.Message}"); }

            // Build static menus
            BuildMainMenu();

            // Hook events
            Tick += OnTick;
            KeyDown += OnKeyDown;

            Log("ExtendedLSC initialized");
        }

        #region Menu Building

        // Custom banner from PNG file
        private CustomSprite customBanner = null;
        private bool bannerLoadAttempted = false;

        /// <summary>
        /// Creates a NativeMenu with consistent settings.
        /// </summary>
        private NativeMenu CreateMenu(string title, string subtitle = null)
        {
            // If no subtitle provided, use the title as subtitle (since banner covers title area)
            var menu = new NativeMenu(title, subtitle ?? title);
            menu.Alignment = GTA.UI.Alignment.Left; // Left-justified text like Menyoo/native GTA
            menu.HeldTime = 999999; // Disable LemonUI's repeat - we handle it ourselves
            menu.MaxItems = 10; // Show 10 items, then scroll (shows scroll indicator)
            menu.ItemCount = CountVisibility.Always; // Always show "X/Y" counter
            menuPool.Add(menu);

            // Track scroll position to match LemonUI's internal firstItem
            menu.Shown += (s, e) =>
            {
                menuFirstItem[menu] = 0;
                menuLastSelectedIndex[menu] = menu.SelectedIndex;
            };

            menu.SelectedIndexChanged += (s, e) =>
            {
                // Clear "not enough cash" message and restore original description
                if (showNotEnoughCash && notEnoughCashItem != null)
                {
                    notEnoughCashItem.Description = notEnoughCashOriginalDesc;
                    notEnoughCashItem = null;
                    notEnoughCashOriginalDesc = null;
                }
                showNotEnoughCash = false;
                notEnoughCashStartTime = 0;

                // Replicate LemonUI's scroll logic
                int maxItems = menu.MaxItems;
                int totalItems = menu.Items.Count;
                int newIndex = e.Index;

                if (!menuFirstItem.TryGetValue(menu, out int firstItem)) firstItem = 0;
                if (!menuLastSelectedIndex.TryGetValue(menu, out int lastIndex)) lastIndex = 0;

                if (totalItems > maxItems)
                {
                    int lower = firstItem;
                    int upper = firstItem + maxItems;

                    if (newIndex >= lower && newIndex < upper)
                    {
                        // Still in visible range, no scroll
                    }
                    else if (newIndex == upper)
                    {
                        // Scrolled down past visible
                        firstItem++;
                    }
                    else if (newIndex == lower - 1)
                    {
                        // Scrolled up past visible
                        firstItem--;
                    }
                    else
                    {
                        // Jumped (e.g., wrap around)
                        if (newIndex < maxItems)
                            firstItem = 0;
                        else
                            firstItem = newIndex - maxItems + 1;
                    }

                    firstItem = Math.Max(0, Math.Min(firstItem, totalItems - maxItems));
                }
                else
                {
                    firstItem = 0;
                }

                menuFirstItem[menu] = firstItem;
                menuLastSelectedIndex[menu] = newIndex;
            };

            // Clear "not enough cash" message and restore original description when menu closes
            menu.Closed += (s, e) =>
            {
                if (showNotEnoughCash && notEnoughCashItem != null)
                {
                    notEnoughCashItem.Description = notEnoughCashOriginalDesc;
                    notEnoughCashItem = null;
                    notEnoughCashOriginalDesc = null;
                }
                showNotEnoughCash = false;
                notEnoughCashStartTime = 0;
            };

            return menu;
        }

        private void BuildMainMenu()
        {
            // Create menu - empty title (custom banner), "CATEGORIES" subtitle
            mainMenu = new NativeMenu("", "CATEGORIES");
            mainMenu.Alignment = GTA.UI.Alignment.Left; // Left-justified text like Menyoo/native GTA
            mainMenu.HeldTime = 999999; // Disable LemonUI's repeat - we handle it ourselves
            mainMenu.MaxItems = 10; // Show 10 items, then scroll (shows scroll indicator)
            mainMenu.ItemCount = CountVisibility.Always; // Always show "X/Y" counter
            menuPool.Add(mainMenu);

            // Load custom banner PNG
            LoadCustomBannerFromFile();

            // Close event
            mainMenu.Closed += (sender, args) =>
            {
                if (!isNavigatingMenu)
                {
                    CloseMenu();
                }
            };
        }

        private void LoadCustomBannerFromFile()
        {
            if (bannerLoadAttempted) return;
            bannerLoadAttempted = true;

            try
            {
                string scriptsDir = AppDomain.CurrentDomain.BaseDirectory;
                string bannerPath = Path.Combine(scriptsDir, "ExtendedLSC", "banner.png");

                if (File.Exists(bannerPath))
                {
                    customBanner = new CustomSprite(bannerPath, new SizeF(100, 100), new PointF(0, 0), Color.White, 0f, false);
                    Log($"Custom banner loaded from: {bannerPath}");
                }
                else
                {
                    Log($"Banner not found at: {bannerPath}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading custom banner: {ex.Message}");
            }
        }

        // Menu position adjustment
        // (activeDebugMode == DebugMode.MenuPosition) replaced by activeDebugMode == DebugMode.MenuPosition

        // ============== LEMONUI MENU ITEM LAYOUT REFERENCE ==============
        // NativeItem:
        //   - Title property      -> renders on LEFT side of item
        //   - AltTitle property   -> renders on RIGHT side of item
        //   - Description         -> renders at bottom when item is selected
        //
        // NativeSubmenuItem:
        //   - Title (3rd param)   -> renders on RIGHT side (NOT left!)
        //   - Has arrow ">" on right
        //   - DO NOT USE if you want left-aligned category names
        //
        // Solution: Use NativeItem + manual Activated handler for submenus
        //   var item = new NativeItem("Category Name");  // LEFT side
        //   item.AltTitle = "$500";                      // RIGHT side (price)
        //   item.Activated += (s,e) => { parentMenu.Visible = false; submenu.Visible = true; };
        // ================================================================

        // Auto-offset constants for LEFT-aligned menu (text left-justified)
        // Menu appears on LEFT side of screen (native LSC position)
        private const float BASE_ASPECT = 1.7778f;   // 16:9
        private const float BASE_OFFSET_X = 0f;      // Left side of screen, minimal offset
        private const float OFFSET_SCALE_X = 0f;     // TODO: derive from ultrawide testing

        private void DrawCustomBanner()
        {
            // Find any visible menu in the pool to get banner position
            NativeMenu visibleMenu = null;
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu && menu.Visible)
                {
                    visibleMenu = menu;
                    break;
                }
            }

            if (visibleMenu == null) return;

            // Draw custom banner on all menus
            if (customBanner != null)
            {
                // Get LemonUI banner position to align our custom sprite
                var menuBanner = visibleMenu.Banner;
                if (menuBanner != null)
                {
                    // Convert LemonUI coordinates to CustomSprite coordinates
                    var spritePos = LemonUIToCustomSprite(menuBanner.Position);
                    var spriteSize = LemonUIToCustomSpriteSize(menuBanner.Size);

                    customBanner.Position = spritePos;
                    customBanner.Size = spriteSize;
                    customBanner.Draw();
                }
            }

            // Draw scroll indicators (for all menus)
            DrawScrollIndicators(visibleMenu);

            // Draw tick icons for installed items
            DrawTicks(visibleMenu);

            // Draw custom description below scroll indicator (or below items if no scroll indicator)
            DrawCustomDescription(visibleMenu);

            // Draw vehicle stats bars below description (LSC style)
            DrawVehicleStats(visibleMenu);

            // Note: Menu position adjustment is handled in OnTick for MenuPosition debug mode
            // with element-specific controls (see switch on selectedUIElement)
        }

        /// <summary>
        /// Draw dark overlay with text for manual transmission key binding
        /// </summary>
        private void DrawMTBindingOverlay()
        {
            if (mtBindingState <= 0) return;

            // Draw semi-transparent dark background covering most of screen
            Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1.0f, 1.0f, 0, 0, 0, 200);

            // Draw title text
            string title = "MANUAL TRANSMISSION SETUP";
            Function.Call(Hash.SET_TEXT_FONT, 4); // Pricedown font
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.8f);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 255, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, title);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.35f);

            // Draw instruction text
            string instruction = mtBindingState == 1
                ? "Press a key or button for SHIFT UP"
                : "Press a key or button for SHIFT DOWN";

            Function.Call(Hash.SET_TEXT_FONT, 0); // Chalet London
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.5f);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 200, 0, 255); // Yellow
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, instruction);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.45f);

            // Draw hint text
            string hint = "Press ESC / B to cancel";
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.35f);
            Function.Call(Hash.SET_TEXT_COLOUR, 150, 150, 150, 255); // Gray
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, hint);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.55f);
        }

        private void DrawScrollIndicators(NativeMenu menu)
        {
            if (!ModSettings.ShowScrollIndicators) return;
            if (menu == null || !menu.Visible) return;

            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;

            // No need for scroll indicators if all items fit
            if (totalItems <= maxVisible) return;

            // Request the CommonMenu texture dictionary (needed for arrows sprite)
            if (!Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, "CommonMenu"))
            {
                Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, "CommonMenu", false);
                return; // Wait for next frame
            }

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = screenW / screenH;

            // LemonUI uses 1080p base coordinates
            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            // Menu banner gives us the top-left position
            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            // Calculate center X position of menu (in normalized 0-1 coords)
            float menuCenterX = (bannerPos.X + bannerSize.Width / 2f) / lemonXBase;

            // Calculate Y position below the visible items (like Menyoo's scroller indicator)
            // Each item is about 38 pixels in 1080p base, plus subtitle area
            float subtitleHeight = 38f;
            float itemHeight = 38f;

            // Calculate bottom of visible items area
            float menuTopY = (bannerPos.Y + bannerSize.Height + subtitleHeight) / lemonYBase;
            float visibleItemsHeight = (itemHeight * Math.Min(totalItems, maxVisible)) / lemonYBase;
            float indicatorY = menuTopY + visibleItemsHeight + 0.0173f; // Center of indicator bar

            // Calculate menu width in normalized coords
            float menuWidth = bannerSize.Width / lemonXBase;

            // Apply scroll arrows offset and scale for independent positioning
            float drawCenterX = menuCenterX + scrollArrowsOffsetX;
            float drawIndicatorY = indicatorY + scrollArrowsOffsetY;
            float drawWidth = menuWidth * scrollArrowsScaleW;
            float drawHeight = 0.035f * scrollArrowsScaleH;

            // Draw background rect for scroll indicator (like Menyoo's scroller indicator rect)
            Function.Call(Hash.DRAW_RECT, drawCenterX, drawIndicatorY, drawWidth, drawHeight, 0, 0, 0, 200);

            // Get texture resolution for proper scaling (like Menyoo does)
            Vector3 textureRes = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, "CommonMenu", "shop_arrows_upANDdown");
            float spriteW = (textureRes.X / (1920f * 2f)) * arrowSpriteScale;
            float spriteH = (textureRes.Y / (1080f * 2f)) * arrowSpriteScale;

            // Draw the up/down arrows sprite twice for bolder appearance
            Function.Call(Hash.DRAW_SPRITE,
                "CommonMenu",
                "shop_arrows_upANDdown",
                drawCenterX,
                drawIndicatorY,
                spriteW,
                spriteH,
                0f,
                255, 255, 255, 255);

            // Second pass for bolder look
            Function.Call(Hash.DRAW_SPRITE,
                "CommonMenu",
                "shop_arrows_upANDdown",
                drawCenterX,
                drawIndicatorY,
                spriteW,
                spriteH,
                0f,
                255, 255, 255, 255);
        }

        /// <summary>
        /// Draw tick icons for installed items (replaces "" text)
        /// </summary>
        private void DrawTicks(NativeMenu menu)
        {
            if (menu == null || !menu.Visible) return;
            if (menu.Items.Count == 0) return;

            // Request the CommonMenu texture dictionary (has garage icons)
            if (!Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, "CommonMenu"))
            {
                Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, "CommonMenu", false);
                return;
            }

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = screenW / screenH;

            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            float subtitleHeight = 38f;
            float itemHeight = 37.5f; // Slightly less than 38 to prevent drift on long lists

            // Menu right edge X position (normalized) - offset to keep sprite inside menu
            float menuRightX = (bannerPos.X + bannerSize.Width - 28f) / lemonXBase;

            // First visible item Y position (use 38 for initial offset, 37.5 for per-item)
            float firstItemY = (bannerPos.Y + bannerSize.Height + subtitleHeight + 38f / 2f) / lemonYBase;

            // Get garage icon sprite size
            Vector3 textureRes = Function.Call<Vector3>(Hash.GET_TEXTURE_RESOLUTION, "CommonMenu", "shop_garage_icon_a");
            float spriteW = textureRes.X / (1920f * 2.5f);
            float spriteH = textureRes.Y / (1080f * 2.5f);

            // Calculate which items are visible
            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;
            int selectedIndex = menu.SelectedIndex;

            // Use tracked scroll position (matches LemonUI's internal firstItem)
            int firstVisibleIndex = 0;
            if (menuFirstItem.TryGetValue(menu, out int tracked))
                firstVisibleIndex = tracked;
            else if (totalItems > maxVisible)
            {
                // Fallback: approximate if not tracked yet
                firstVisibleIndex = Math.Max(0, selectedIndex - maxVisible + 1);
                firstVisibleIndex = Math.Min(firstVisibleIndex, totalItems - maxVisible);
            }

            // Draw icons for each visible item based on ownership/installation status
            int visibleCount = Math.Min(maxVisible, totalItems);
            for (int i = 0; i < visibleCount; i++)
            {
                int itemIndex = firstVisibleIndex + i;
                if (itemIndex >= totalItems) break;

                var item = menu.Items[itemIndex] as NativeItem;
                if (item == null) continue;

                // Check ownership status from dictionary
                if (itemOwnershipStatus.TryGetValue(item, out int status))
                {
                    float itemY = firstItemY + (i * itemHeight / lemonYBase);
                    bool isHovered = (itemIndex == selectedIndex);

                    // Determine sprite based on status and hover state
                    // Installed + hovered: shop_garage_icon_b (more visible)
                    // Installed + not hovered: shop_garage_icon_a
                    // Owned but not installed: shop_tick_icon (tinted black when hovered for visibility)
                    string spriteName;
                    int r = 255, g = 255, b = 255; // Default white tint

                    if (status == STATUS_INSTALLED)
                    {
                        spriteName = isHovered ? "shop_garage_icon_b" : "shop_garage_icon_a";
                    }
                    else // STATUS_OWNED
                    {
                        spriteName = "shop_tick_icon";
                        if (isHovered)
                        {
                            r = 0; g = 0; b = 0; // Black tint when hovered
                        }
                    }

                    Function.Call(Hash.DRAW_SPRITE,
                        "CommonMenu",
                        spriteName,
                        menuRightX,
                        itemY,
                        spriteW * spriteScale,
                        spriteH * spriteScale,
                        0f,
                        r, g, b, 255);
                }
            }
        }

        /// <summary>
        /// Draw custom description below the scroll indicator
        /// </summary>
        private void DrawCustomDescription(NativeMenu menu)
        {
            if (menu == null || !menu.Visible) return;
            if (menu.SelectedIndex < 0 || menu.SelectedIndex >= menu.Items.Count) return;

            // Check if "not enough cash" message should be shown
            bool showingNotEnoughCash = false;
            if (showNotEnoughCash)
            {
                int elapsed = Game.GameTime - notEnoughCashStartTime;
                if (elapsed < NOT_ENOUGH_CASH_DURATION)
                {
                    showingNotEnoughCash = true;
                }
                else
                {
                    // Timer expired, reset state
                    showNotEnoughCash = false;
                    notEnoughCashStartTime = 0;
                }
            }

            var selectedItem = menu.Items[menu.SelectedIndex];

            // Handle "not enough cash" by setting item's Description - LemonUI draws it consistently
            if (showingNotEnoughCash)
            {
                // Store original description if this is a new item
                if (notEnoughCashItem != selectedItem)
                {
                    // Restore previous item's description if any
                    if (notEnoughCashItem != null && notEnoughCashOriginalDesc != null)
                    {
                        notEnoughCashItem.Description = notEnoughCashOriginalDesc;
                    }
                    notEnoughCashItem = selectedItem;
                    notEnoughCashOriginalDesc = selectedItem.Description;
                }
                selectedItem.Description = "Sorry - you cannot afford this item.";
                return; // Let LemonUI handle the drawing
            }
            else if (notEnoughCashItem != null)
            {
                // Restore original description when error clears
                notEnoughCashItem.Description = notEnoughCashOriginalDesc;
                notEnoughCashItem = null;
                notEnoughCashOriginalDesc = null;
            }

            // If item has built-in Description, LemonUI draws it - skip our custom drawing
            bool hasNativeDescription = !string.IsNullOrEmpty(selectedItem.Description);
            if (hasNativeDescription) return;

            string description = GetDescriptionFromConfig(selectedItem.Title);

            // If we have a MenuConfig description OR debugging description, draw our custom box
            bool isDebugEditingDesc = (activeDebugMode == DebugMode.Resizer) && resizerFieldIndex < activeResizerFields.Count && activeResizerFields[resizerFieldIndex] == "Description Text";
            if (string.IsNullOrEmpty(description) && !isDebugEditingDesc) return;

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = screenW / screenH;

            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;
            bool hasScrollIndicator = totalItems > maxVisible;

            // Calculate positions
            float subtitleHeight = 38f;
            float itemHeight = 38f;
            float scrollIndicatorHeight = hasScrollIndicator ? 0.035f : 0f;

            float menuTopY = (bannerPos.Y + bannerSize.Height + subtitleHeight) / lemonYBase;
            float visibleItemsHeight = (itemHeight * Math.Min(totalItems, maxVisible)) / lemonYBase;

            // Description starts below scroll indicator (or below items if no scroll indicator)
            float descY = menuTopY + visibleItemsHeight;
            if (hasScrollIndicator)
            {
                descY += 0.0173f + scrollIndicatorHeight / 2f + 0.01f; // Below scroll indicator
            }
            else
            {
                descY += 0.01f; // Small gap below items
            }

            // Calculate menu left edge and width
            float menuLeftX = bannerPos.X / lemonXBase;
            float menuWidth = bannerSize.Width / lemonXBase;
            float menuCenterX = menuLeftX + menuWidth / 2f;

            // Apply description offset and scale for independent positioning
            float drawDescCenterX = menuCenterX + descriptionOffsetX;
            float drawDescLeftX = menuLeftX + descriptionOffsetX;
            float drawDescY = descY + descriptionOffsetY;
            float drawDescWidth = menuWidth * descriptionScaleW;

            // Calculate dynamic height based on text content
            float drawDescHeight = GetDescriptionHeight(menu, menuWidth, menuLeftX);
            // Fallback to standard single-line height (matches the calculated formula: padding + lineHeight)
            if (drawDescHeight == 0f) drawDescHeight = (0.012f + 0.018f) * descriptionScaleH;

            // Draw description background
            Function.Call(Hash.DRAW_RECT, drawDescCenterX, drawDescY + drawDescHeight / 2f, drawDescWidth, drawDescHeight, 0, 0, 0, 200);

            // Determine text and color
            string displayText;
            int textR, textG, textB;

            // Check if we're in debug mode editing the description text (reuse local var)

            if (showingNotEnoughCash)
            {
                displayText = "Sorry - you cannot afford this item.";
                textR = 255; textG = 255; textB = 255; // White
            }
            else
            {
                // Use actual description, but yellow if in debug mode editing description
                displayText = string.IsNullOrEmpty(description) ? "No description available." : description;
                if (isDebugEditingDesc)
                {
                    textR = 255; textG = 255; textB = 0; // Bright yellow when editing
                }
                else
                {
                    textR = 255; textG = 255; textB = 255; // White
                }
            }

            // Draw description text
            Function.Call(Hash.SET_TEXT_FONT, 0); // Chalet London
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, descriptionTextScale);
            Function.Call(Hash.SET_TEXT_COLOUR, textR, textG, textB, 255);
            Function.Call(Hash.SET_TEXT_WRAP, drawDescLeftX + 0.005f, drawDescLeftX + drawDescWidth - 0.005f);
            Function.Call(Hash.SET_TEXT_LEADING, 0);

            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, displayText);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, drawDescLeftX + 0.005f, drawDescY + 0.005f);
        }

        /// <summary>
        /// Get the actual height of the description box based on text content
        /// </summary>
        private float GetDescriptionHeight(NativeMenu menu, float menuWidth, float menuLeftX)
        {
            if (menu == null || menu.SelectedIndex < 0 || menu.SelectedIndex >= menu.Items.Count)
                return 0.045f; // Default single line height

            // Get the description text (checks item.Description first, then MenuConfig)
            var selectedItem = menu.Items[menu.SelectedIndex];
            string description = GetItemDescription(selectedItem);

            // For "not enough cash", use standard single-line height (don't resize box)
            if (showNotEnoughCash)
            {
                int elapsed = Game.GameTime - notEnoughCashStartTime;
                if (elapsed < NOT_ENOUGH_CASH_DURATION)
                {
                    // Use standard single-line height for error message
                    float errorLineHeight = 0.018f * descriptionScaleH;
                    float errorPadding = 0.012f * descriptionScaleH;
                    return errorPadding + errorLineHeight;
                }
            }

            if (string.IsNullOrEmpty(description))
                return 0f; // No description, no height

            // Estimate line count based on text length and available width
            // At descriptionTextScale ~0.35, roughly 38 chars fit per line in menu width
            int charsPerLine = 38;
            int lineCount = (int)Math.Ceiling((double)description.Length / charsPerLine);
            lineCount = Math.Max(1, Math.Min(lineCount, 5)); // Clamp between 1-5 lines

            // Calculate height: base padding + line height per line
            float lineHeight = 0.018f * descriptionScaleH;
            float padding = 0.012f * descriptionScaleH;
            return padding + (lineHeight * lineCount);
        }

        /// <summary>
        /// Draw vehicle performance stats bars below the description (LSC style)
        /// </summary>
        private void DrawVehicleStats(NativeMenu menu)
        {
            if (menu == null || !menu.Visible) return;
            if (currentVehicle == null || !currentVehicle.Exists()) return;

            // Get menu position for drawing
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = screenW / screenH;

            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = menu.Banner.Position;
            var bannerSize = menu.Banner.Size;

            int totalItems = menu.Items.Count;
            int maxVisible = menu.MaxItems;
            bool hasScrollIndicator = totalItems > maxVisible;

            // Calculate menu layout
            float subtitleHeight = 38f;
            float itemHeight = 38f;
            float scrollIndicatorHeight = hasScrollIndicator ? 0.035f : 0f;

            float menuTopY = (bannerPos.Y + bannerSize.Height + subtitleHeight) / lemonYBase;
            float visibleItemsHeight = (itemHeight * Math.Min(totalItems, maxVisible)) / lemonYBase;

            // Calculate Y position below description
            // Menu dimensions (apply position offsets from debug mode)
            float menuLeftX = bannerPos.X / lemonXBase + statsPanelOffsetX;
            float menuWidth = bannerSize.Width / lemonXBase;
            float menuCenterX = menuLeftX + menuWidth / 2f;

            float descY = menuTopY + visibleItemsHeight;
            if (hasScrollIndicator)
                descY += 0.0173f + scrollIndicatorHeight / 2f + 0.01f;
            else
                descY += 0.01f;

            // Get dynamic description height based on text content
            float descHeight = GetDescriptionHeight(menu, menuWidth, menuLeftX - statsPanelOffsetX);

            // Position stats below description if it exists, otherwise directly below menu/arrows
            float statsStartY;
            if (descHeight > 0f)
            {
                // Has description - position below it
                statsStartY = descY + descHeight + descriptionOffsetY + 0.005f + statsPanelOffsetY;
            }
            else
            {
                // No description - position directly below menu items/arrows
                statsStartY = descY + statsPanelOffsetY;
            }

            // Stats panel dimensions (use resizer debug values and scale)
            float statPanelHeight = (statsRowHeight * 4 + statsPanelPadding) * statsPanelScaleH;
            float statPanelWidth = menuWidth * statsPanelScaleW;

            // Draw stats background panel
            Function.Call(Hash.DRAW_RECT, menuCenterX, statsStartY + statPanelHeight / 2f, statPanelWidth, statPanelHeight, 0, 0, 0, 200);

            // Get vehicle stats
            float topSpeed = Function.Call<float>(Hash.GET_VEHICLE_ESTIMATED_MAX_SPEED, currentVehicle);
            float acceleration = Function.Call<float>(Hash.GET_VEHICLE_ACCELERATION, currentVehicle);
            float braking = Function.Call<float>(Hash.GET_VEHICLE_MAX_BRAKING, currentVehicle);
            float traction = Function.Call<float>(Hash.GET_VEHICLE_MAX_TRACTION, currentVehicle);

            // LSC stat bars use a visual scaling formula that doesn't directly reflect handling values
            // The bars are known to be "unreliable" per the modding community
            // We use empirically-calibrated maximums to match real LSC appearance
            // Reference: https://gtamods.com/wiki/Handling.meta

            // Native function return value ranges (approximate):
            // GET_VEHICLE_ESTIMATED_MAX_SPEED: 30-50 m/s for most cars (supercars: 50-60)
            // GET_VEHICLE_ACCELERATION: 0.25-0.45 (fInitialDriveForce from handling.meta)
            // GET_VEHICLE_MAX_BRAKING: 0.8-3.0 (calculated from fBrakeForce * factors)
            // GET_VEHICLE_MAX_TRACTION: 2.0-2.8 (fTractionCurveMax from handling.meta)

            // Scaling formula: Use square root for compression (mimics game's visual scaling)
            // This prevents supercars from maxing out all bars while showing progression
            // Values are calibrated via Debug Menu > Stats Calibration
            float topSpeedPct = (float)Math.Sqrt(Math.Min(1f, topSpeed / statsTopSpeedDiv)) * statsTopSpeedMult;
            float accelPct = (float)Math.Sqrt(Math.Min(1f, acceleration / statsAccelDiv)) * statsAccelMult;
            float brakingPct = (float)Math.Sqrt(Math.Min(1f, braking / statsBrakingDiv)) * statsBrakingMult;
            float tractionPct = (float)Math.Sqrt(Math.Min(1f, traction / statsTractionDiv)) * statsTractionMult;

            // Check if previewing a performance mod to show blue/red stat changes
            float previewTopSpeedPct = -1f;
            float previewAccelPct = -1f;
            float previewBrakingPct = -1f;
            float previewTractionPct = -1f;

            // If we're previewing a mod, show the preview vs original
            if (isPreviewingMod && previewModIndex >= 0 && hasStoredOriginalStats)
            {
                // Performance mod indices: Engine=11, Brakes=12, Transmission=13, Suspension=15, Armor=16, Turbo=18
                bool isPerformanceMod = previewModIndex == 11 || previewModIndex == 12 ||
                                         previewModIndex == 13 || previewModIndex == 15 ||
                                         previewModIndex == 16 || previewModIndex == 18;

                if (isPerformanceMod)
                {
                    // Current stats (with preview applied) become the preview percentages
                    previewTopSpeedPct = topSpeedPct;
                    previewAccelPct = accelPct;
                    previewBrakingPct = brakingPct;
                    previewTractionPct = tractionPct;

                    // Use stored original stats as the base (same formula as above)
                    topSpeedPct = (float)Math.Sqrt(Math.Min(1f, originalTopSpeed / statsTopSpeedDiv)) * statsTopSpeedMult;
                    accelPct = (float)Math.Sqrt(Math.Min(1f, originalAcceleration / statsAccelDiv)) * statsAccelMult;
                    brakingPct = (float)Math.Sqrt(Math.Min(1f, originalBraking / statsBrakingDiv)) * statsBrakingMult;
                    tractionPct = (float)Math.Sqrt(Math.Min(1f, originalTraction / statsTractionDiv)) * statsTractionMult;
                }
            }

            // Draw each stat row (apply width scale to internal elements)
            float currentY = statsStartY + statsContentOffsetY;
            float labelX = menuLeftX + statsTextLeftPadding;  // Adjustable left padding for text
            float barStartX = menuLeftX + 0.095f * statsPanelScaleW + statsBarInset; // Bar starts after label, plus inset
            float barWidth = (menuWidth - 0.105f) * statsPanelScaleW - (statsBarInset * 2); // Remaining width minus inset from both sides
            float rowHeight = statsRowHeight * statsPanelScaleH;

            DrawStatRow("Top Speed", topSpeedPct, labelX, barStartX, barWidth, currentY, statsBarOffsetY, previewTopSpeedPct);
            currentY += rowHeight;
            DrawStatRow("Acceleration", accelPct, labelX, barStartX, barWidth, currentY, statsBarOffsetY, previewAccelPct);
            currentY += rowHeight;
            DrawStatRow("Braking", brakingPct, labelX, barStartX, barWidth, currentY, statsBarOffsetY, previewBrakingPct);
            currentY += rowHeight;
            DrawStatRow("Traction", tractionPct, labelX, barStartX, barWidth, currentY, statsBarOffsetY, previewTractionPct);
        }

        /// <summary>
        /// Draw a single stat row with label and segmented bar (supports partial segment fills)
        /// </summary>
        private void DrawStatRow(string label, float percentage, float labelX, float barStartX, float barWidth, float y, float barYOffset, float previewPercentage = -1f)
        {
            // Draw label text (use resizer debug value for scale)
            Function.Call(Hash.SET_TEXT_FONT, 0); // Chalet London
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, statsLabelScale);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 255, 255);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, label);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, labelX, y);

            // Draw segmented bar (5 segments like real LSC, with partial fill support)
            int totalSegments = 5;
            float fillAmount = percentage * totalSegments; // e.g., 3.65 means 3 full + 65% of 4th
            float previewFillAmount = previewPercentage >= 0 ? previewPercentage * totalSegments : -1f;

            // Use resizer debug values for segment dimensions
            float segmentWidth = barWidth / totalSegments;
            float segmentY = y + 0.007f + barYOffset; // Center vertically with text, plus adjustable offset

            for (int i = 0; i < totalSegments; i++)
            {
                float segLeftX = barStartX + (i * segmentWidth);
                float segCenterX = segLeftX + segmentWidth / 2f;
                float actualSegWidth = segmentWidth - statsSegmentGap;

                // Calculate how much of this segment is filled (0.0 to 1.0)
                float segmentFill = Math.Max(0f, Math.Min(1f, fillAmount - i));
                float previewSegmentFill = previewFillAmount >= 0 ? Math.Max(0f, Math.Min(1f, previewFillAmount - i)) : -1f;

                // Draw empty background first (gray)
                Function.Call(Hash.DRAW_RECT, segCenterX, segmentY, actualSegWidth, statsSegmentHeight, 100, 100, 100, 200);

                if (previewFillAmount >= 0)
                {
                    // Preview mode - show current fill, then overlay preview changes
                    float currentFill = segmentFill;
                    float newFill = previewSegmentFill;

                    if (currentFill > 0)
                    {
                        // Draw current fill (white) - partial width from left
                        float fillWidth = actualSegWidth * currentFill;
                        float fillX = segLeftX + (statsSegmentGap / 2f) + fillWidth / 2f;
                        Function.Call(Hash.DRAW_RECT, fillX, segmentY, fillWidth, statsSegmentHeight, 255, 255, 255, 255);
                    }

                    if (newFill > currentFill)
                    {
                        // Gaining - draw blue for the gain portion
                        float gainStart = currentFill;
                        float gainEnd = newFill;
                        float gainWidth = actualSegWidth * (gainEnd - gainStart);
                        float gainX = segLeftX + (statsSegmentGap / 2f) + (actualSegWidth * gainStart) + gainWidth / 2f;
                        Function.Call(Hash.DRAW_RECT, gainX, segmentY, gainWidth, statsSegmentHeight, 0, 150, 255, 255);
                    }
                    else if (newFill < currentFill)
                    {
                        // Losing - draw red for the loss portion (replacing part of white)
                        float lossStart = newFill;
                        float lossEnd = currentFill;
                        float lossWidth = actualSegWidth * (lossEnd - lossStart);
                        float lossX = segLeftX + (statsSegmentGap / 2f) + (actualSegWidth * lossStart) + lossWidth / 2f;
                        Function.Call(Hash.DRAW_RECT, lossX, segmentY, lossWidth, statsSegmentHeight, 255, 50, 50, 255);
                    }
                }
                else
                {
                    // Normal mode - draw partial white fill
                    if (segmentFill > 0)
                    {
                        float fillWidth = actualSegWidth * segmentFill;
                        float fillX = segLeftX + (statsSegmentGap / 2f) + fillWidth / 2f;
                        Function.Call(Hash.DRAW_RECT, fillX, segmentY, fillWidth, statsSegmentHeight, 255, 255, 255, 255);
                    }
                }
            }
        }

        /// <summary>
        /// Sprite browser for finding icons - toggle with F6
        /// </summary>
        private void DrawSpriteBrowser()
        {
            if (!(activeDebugMode == DebugMode.SpriteBrowser)) return;

            var (dictName, sprites) = SpriteDictionaries[spriteBrowserDictIndex];

            // Request texture dictionary
            if (!Function.Call<bool>(Hash.HAS_STREAMED_TEXTURE_DICT_LOADED, dictName))
            {
                Function.Call(Hash.REQUEST_STREAMED_TEXTURE_DICT, dictName, false);
            }

            // Draw background
            Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 0.5f, 0.6f, 0, 0, 0, 200);

            // Draw title
            Function.Call(Hash.SET_TEXT_FONT, 1);
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.6f);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 100, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, $"SPRITE BROWSER - {dictName} ({spriteBrowserDictIndex + 1}/{SpriteDictionaries.Length})");
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.22f);

            // Draw controls hint
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.35f);
            Function.Call(Hash.SET_TEXT_COLOUR, 200, 200, 200, 255);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, "PgUp/PgDn: pages | Home/End: dictionaries | F6: close");
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.26f);

            // Calculate page info
            int startIdx = spriteBrowserPage * 10;
            int endIdx = Math.Min(startIdx + 10, sprites.Length);
            int totalPages = (sprites.Length + 9) / 10;

            // Draw page info
            Function.Call(Hash.SET_TEXT_COLOUR, 150, 150, 150, 255);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, $"Page {spriteBrowserPage + 1}/{totalPages}");
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.74f);

            // Draw sprites in a grid (2 columns, 5 rows)
            float startY = 0.32f;
            float rowHeight = 0.08f;
            float col1X = 0.35f;
            float col2X = 0.65f;

            for (int i = startIdx; i < endIdx; i++)
            {
                int localIdx = i - startIdx;
                int row = localIdx / 2;
                int col = localIdx % 2;
                float x = col == 0 ? col1X : col2X;
                float y = startY + row * rowHeight;

                string spriteName = sprites[i];

                // Draw sprite
                Function.Call(Hash.DRAW_SPRITE,
                    dictName,
                    spriteName,
                    x - 0.08f,
                    y,
                    0.04f,
                    0.04f,
                    0f,
                    255, 255, 255, 255);

                // Draw index and name
                Function.Call(Hash.SET_TEXT_FONT, 0);
                Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.3f);
                Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 255, 255);
                Function.Call(Hash.SET_TEXT_CENTRE, false);
                Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, $"[{i}] {spriteName}");
                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x - 0.05f, y - 0.012f);
            }
        }

        private void AdjustMenuPosition()
        {
            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = screenW / screenH;

            var offset = mainMenu.Offset;

            // Movement controls only in debug mode
            if ((activeDebugMode == DebugMode.MenuPosition))
            {
                float fastSpeed = 2f;   // Left stick
                float fineSpeed = 0.5f; // D-pad

                // Left stick - fast movement
                float lsX = Game.GetControlValueNormalized(GTA.Control.ScriptLeftAxisX);
                float lsY = Game.GetControlValueNormalized(GTA.Control.ScriptLeftAxisY);
                if (Math.Abs(lsX) > 0.1f) offset.X += lsX * fastSpeed;
                if (Math.Abs(lsY) > 0.1f) offset.Y += lsY * fastSpeed;

                // D-pad - fine tuning
                if (Game.IsControlPressed(GTA.Control.FrontendUp)) offset.Y -= fineSpeed;
                if (Game.IsControlPressed(GTA.Control.FrontendDown)) offset.Y += fineSpeed;
                if (Game.IsControlPressed(GTA.Control.FrontendLeft)) offset.X -= fineSpeed;
                if (Game.IsControlPressed(GTA.Control.FrontendRight)) offset.X += fineSpeed;

                mainMenu.Offset = offset;
            }

            // Y button - log position
            if (Game.IsControlJustPressed(GTA.Control.FrontendY))
            {
                var bannerPos = mainMenu.Banner?.Position ?? PointF.Empty;
                var bannerSize = mainMenu.Banner?.Size ?? SizeF.Empty;

                // Get actual screen resolution via native
                var resW = new OutputArgument();
                var resH = new OutputArgument();
                Function.Call(Hash.GET_ACTUAL_SCREEN_RESOLUTION, resW, resH);
                int actualW = resW.GetResult<int>();
                int actualH = resH.GetResult<int>();
                float actualAspect = actualH > 0 ? (float)actualW / actualH : 0;

                // Calculate base position (Banner - Offset)
                float baseX = bannerPos.X - offset.X;

                Log($"========== MENU POSITION LOG ==========");
                Log($"UI Screen: {screenW}x{screenH} (aspect: {aspectRatio:F4})");
                Log($"Actual Screen: {actualW}x{actualH} (aspect: {actualAspect:F4})");
                Log($"Menu Offset: X={offset.X:F2}, Y={offset.Y:F2}");
                Log($"Banner Position: X={bannerPos.X:F2}, Y={bannerPos.Y:F2}");
                Log($"Banner Base (no offset): X={baseX:F2}");
                Log($"Banner Size: W={bannerSize.Width:F2}, H={bannerSize.Height:F2}");
                Log($"========================================");

                GTA.UI.Notification.Show($"Logged! Offset: X={offset.X:F1} Y={offset.Y:F1}");
            }

            // RB button - reset offset to 0
            if (Game.IsControlJustPressed(GTA.Control.FrontendRb))
            {
                offset = PointF.Empty;
                mainMenu.Offset = offset;
                GTA.UI.Notification.Show("Offset reset to 0,0");
            }

            // Position adjustment is now handled via Debug Menu (F7)
        }

        // Cached actual screen resolution
        private int cachedScreenW = 0;
        private int cachedScreenH = 0;

        private void UpdateCachedScreenResolution()
        {
            var resW = new OutputArgument();
            var resH = new OutputArgument();
            Function.Call(Hash.GET_ACTUAL_SCREEN_RESOLUTION, resW, resH);
            cachedScreenW = resW.GetResult<int>();
            cachedScreenH = resH.GetResult<int>();
        }

        /// <summary>
        /// Converts LemonUI position coordinates to CustomSprite's 1280x720 base.
        /// Works on any resolution including ultrawide.
        /// </summary>
        private PointF LemonUIToCustomSprite(PointF lemonPos)
        {
            // Use actual screen width for accurate conversion
            float actualWidth = cachedScreenW > 0 ? cachedScreenW : 1920f;

            float widthRatio = 1280f / actualWidth;
            float heightRatio = 720f / 1080f;

            return new PointF(lemonPos.X * widthRatio, lemonPos.Y * heightRatio);
        }

        /// <summary>
        /// Converts LemonUI size to CustomSprite's 1280x720 base.
        /// Works on any resolution including ultrawide.
        /// </summary>
        private SizeF LemonUIToCustomSpriteSize(SizeF lemonSize)
        {
            // Use actual screen width for accurate conversion
            float actualWidth = cachedScreenW > 0 ? cachedScreenW : 1920f;

            float widthRatio = 1280f / actualWidth;
            float heightRatio = 720f / 1080f;

            return new SizeF(lemonSize.Width * widthRatio, lemonSize.Height * heightRatio);
        }

        /// <summary>
        /// Calculates and applies the menu offset to align with native LSC menu
        /// based on actual screen aspect ratio. Derived from testing at 16:9 and 32:9.
        /// </summary>
        private void ApplyAutoOffset()
        {
            if ((activeDebugMode == DebugMode.MenuPosition)) return; // Don't auto-offset in debug mode

            // Get actual screen resolution
            var resW = new OutputArgument();
            var resH = new OutputArgument();
            Function.Call(Hash.GET_ACTUAL_SCREEN_RESOLUTION, resW, resH);
            int actualW = resW.GetResult<int>();
            int actualH = resH.GetResult<int>();

            if (actualW <= 0 || actualH <= 0) return;

            float aspectRatio = (float)actualW / actualH;

            // Calculate offset using derived formula
            float offsetX = BASE_OFFSET_X + OFFSET_SCALE_X * (aspectRatio - BASE_ASPECT);

            mainMenu.Offset = new PointF(offsetX, 0);

            Log($"Auto-offset applied: X={offsetX:F2} for aspect {aspectRatio:F4} ({actualW}x{actualH})");
        }

        private void RebuildMenusForVehicle()
        {
            if (currentVehicle == null) return;

            // CRITICAL: Install mod kit before any mod operations
            // This is REQUIRED for SET_VEHICLE_MOD to work properly
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);

            // Set current vehicle for MenuConfig (used for vehicle-specific overrides)
            MenuConfig.CurrentVehicle = currentVehicle.DisplayName;

            try
            {
                // Clear main menu
                mainMenu.Clear();

                // Clear dynamic menus
                foreach (var menu in modMenusByIndex.Values)
                {
                    menuPool.Remove(menu);
                }
                modMenusByIndex.Clear();
                itemOwnershipStatus.Clear();

                // Clear config-based menus
                foreach (var menu in configMenusByPath.Values)
                {
                    menuPool.Remove(menu);
                }
                configMenusByPath.Clear();
                configItemMetadata.Clear();

                // Clear special menus
                if (hornMenu != null) { menuPool.Remove(hornMenu); hornMenu = null; }
                if (plateMenu != null) { menuPool.Remove(plateMenu); plateMenu = null; }
                if (turboMenu != null) { menuPool.Remove(turboMenu); turboMenu = null; }
                if (headlightsMenu != null) { menuPool.Remove(headlightsMenu); headlightsMenu = null; }
                if (windowTintMenu != null) { menuPool.Remove(windowTintMenu); windowTintMenu = null; }

                // Clear and remove respray/wheels if they exist
                if (resprayMenu != null)
                {
                    menuPool.Remove(resprayMenu);
                    resprayMenu = null;
                }
                if (wheelsMenu != null)
                {
                    menuPool.Remove(wheelsMenu);
                    wheelsMenu = null;
                }

                // Clear dynamic category menus (user-created custom categories)
                foreach (var menu in dynamicCategoryMenus)
                {
                    menuPool.Remove(menu);
                }
                dynamicCategoryMenus.Clear();

                // Build menu with grouped categories like native LSC
                BuildGroupedMainMenu();

                // Add any custom user-created categories from folders
                BuildDynamicMainMenu();

                Log($"Built menu with {mainMenu.Items.Count} categories for {currentVehicle.DisplayName}");
            }
            catch (Exception ex)
            {
                Log($"ERROR in RebuildMenusForVehicle: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Builds the main menu with grouped categories like native LSC.
        /// Groups like "Bumpers" contain sub-items "Front Bumper" and "Rear Bumper".
        /// </summary>
        private void BuildGroupedMainMenu()
        {
            // Helper to add a submenu item with proper back navigation and description
            // When editor mode is enabled: Hold LB/L1 (or Shift) while selecting to edit the description
            // Note: We don't use LemonUI's built-in description - we draw our own below the scroll indicator
            void AddSubmenuItem(string title, NativeMenu submenu)
            {
                var item = new NativeItem(title);
                item.AltTitle = ">>";
                // Don't set Description here - we draw our own in DrawCustomDescription()
                // Note: Edit mode uses LB+X (checked in OnTick), not LB+A
                item.Activated += (s, e) =>
                {
                    // Normal mode - open submenu
                    isNavigatingMenu = true;
                    mainMenu.Visible = false;
                    submenu.Visible = true;
                    isNavigatingMenu = false;
                };
                submenu.Closed += (s, e) => { if (!isNavigatingMenu) mainMenu.Visible = true; };
                mainMenu.Add(item);
            }

            // Helper to check if mod type has options
            int GetModCount(int modIndex) => Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, modIndex);

            // Armor (16)
            if (GetModCount(16) > 0)
            {
                var menu = CreateModMenuByIndex(16, GetModCount(16), "Armor");
                AddSubmenuItem("Armor", menu);
            }

            // Brakes (12)
            if (GetModCount(12) > 0)
            {
                var menu = CreateModMenuByIndex(12, GetModCount(12), "Brakes");
                AddSubmenuItem("Brakes", menu);
            }

            // Bumpers (group: Front 1, Rear 2)
            int frontBumperCount = GetModCount(1);
            int rearBumperCount = GetModCount(2);
            if (frontBumperCount > 0 || rearBumperCount > 0)
            {
                var bumpersMenu = CreateMenu("Bumpers");

                if (frontBumperCount > 0)
                {
                    var frontMenu = CreateModMenuByIndex(1, frontBumperCount, "Front Bumper");
                    frontMenu.Closed += (s, e) => { if (!isNavigatingMenu) bumpersMenu.Visible = true; };
                    var frontItem = new NativeItem("Front Bumper");
                    frontItem.AltTitle = ">>";
                    frontItem.Activated += (s, e) => { isNavigatingMenu = true; bumpersMenu.Visible = false; frontMenu.Visible = true; isNavigatingMenu = false; };
                    bumpersMenu.Add(frontItem);
                }
                if (rearBumperCount > 0)
                {
                    var rearMenu = CreateModMenuByIndex(2, rearBumperCount, "Rear Bumper");
                    rearMenu.Closed += (s, e) => { if (!isNavigatingMenu) bumpersMenu.Visible = true; };
                    var rearItem = new NativeItem("Rear Bumper");
                    rearItem.AltTitle = ">>";
                    rearItem.Activated += (s, e) => { isNavigatingMenu = true; bumpersMenu.Visible = false; rearMenu.Visible = true; isNavigatingMenu = false; };
                    bumpersMenu.Add(rearItem);
                }
                AddSubmenuItem("Bumpers", bumpersMenu);
            }

            // Engine (11)
            if (GetModCount(11) > 0)
            {
                var menu = CreateModMenuByIndex(11, GetModCount(11), "Engine");
                AddSubmenuItem("Engine", menu);
            }

            // Exhaust (4)
            if (GetModCount(4) > 0)
            {
                var menu = CreateModMenuByIndex(4, GetModCount(4), "Exhaust");
                AddSubmenuItem("Exhaust", menu);
            }

            // Extras (Side Course feature - toggle vehicle extras)
            if (ModSettings.ExtendedCategories)
            {
                var extrasMenu = CreateExtrasMenu();
                if (extrasMenu != null && extrasMenu.Items.Count > 0)
                {
                    AddSubmenuItem("Extras", extrasMenu);
                }
            }

            // Fenders (index 8 - left fender, index 9 - right fender)
            // Most vehicles only have left fender mods, show directly as "FENDERS"
            int leftFenderCount = GetModCount(8);
            int rightFenderCount = GetModCount(9);
            if (leftFenderCount > 0)
            {
                // Create fender menu directly with left fender mods (most common case)
                var menu = CreateModMenuByIndex(8, leftFenderCount, "FENDERS");
                AddSubmenuItem("Fender", menu);
            }
            // Right fender is rare - add as separate category if it exists
            if (rightFenderCount > 0)
            {
                var menu = CreateModMenuByIndex(9, rightFenderCount, "FENDERS (RIGHT)");
                AddSubmenuItem("Fender (Right)", menu);
            }

            // Grille (6)
            if (GetModCount(6) > 0)
            {
                var menu = CreateModMenuByIndex(6, GetModCount(6), "Grille");
                AddSubmenuItem("Grille", menu);
            }

            // Hood (7)
            if (GetModCount(7) > 0)
            {
                var menu = CreateModMenuByIndex(7, GetModCount(7), "Hood");
                AddSubmenuItem("Hood", menu);
            }

            // Horn (14)
            hornMenu = CreateHornMenu();
            if (hornMenu.Items.Count > 0)
            {
                AddSubmenuItem("Horn", hornMenu);
            }

            // Lights - full submenu structure like native LSC
            {
                var lightsMenu = CreateMenu("Lights");

                // Light Mode selector - cycles through different lighting states
                // Based on Menyoo source: SET_VEHICLE_LIGHTS 3=on, 4=off
                // SET_VEHICLE_INDICATOR_LIGHTS: index 0=right, 1=left
                var lightModes = new List<string> { "Off", "Headlights", "High Beams", "Left Indicator", "Right Indicator", "Hazards", "Brake Lights", "Interior" };
                var lightModeItem = new NativeListItem<string>("Light Mode", lightModes.ToArray());
                lightModeItem.SelectedIndex = 0; // Start at Off

                lightModeItem.ItemChanged += (s, e) =>
                {
                    if (currentVehicle == null || !currentVehicle.Exists()) return;

                    // Track current mode for continuous application
                    currentLightMode = e.Index;

                    // Reset all lights first
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 4); // Off (Menyoo uses 4)
                    Function.Call(Hash.SET_VEHICLE_FULLBEAM, currentVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 1, false); // Left off
                    Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 0, false); // Right off
                    Function.Call(Hash.SET_VEHICLE_INTERIORLIGHT, currentVehicle, false);
                    Function.Call(Hash.SET_VEHICLE_BRAKE_LIGHTS, currentVehicle, false);

                    // Apply the selected mode (high beams/brake applied continuously in OnTick)
                    switch (e.Index)
                    {
                        case 0: // Off
                            Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 4);
                            break;
                        case 1: // Headlights
                            Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 3); // On (Menyoo uses 3)
                            break;
                        case 2: // High Beams - applied every frame in OnTick
                            Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 3);
                            Function.Call(Hash.SET_VEHICLE_FULLBEAM, currentVehicle, true);
                            break;
                        case 3: // Left Indicator
                            Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 1, true);
                            break;
                        case 4: // Right Indicator
                            Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 0, true);
                            break;
                        case 5: // Hazards
                            Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 1, true);
                            Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 0, true);
                            break;
                        case 6: // Brake Lights - applied every frame in OnTick
                            break;
                        case 7: // Interior
                            Function.Call(Hash.SET_VEHICLE_INTERIORLIGHT, currentVehicle, true);
                            break;
                    }
                };
                lightsMenu.Add(lightModeItem);

                // Headlights submenu
                headlightsMenu = CreateMenu("Headlights");
                headlightsMenu.Closed += (s, e) => { if (!isNavigatingMenu) lightsMenu.Visible = true; };

                bool hasXenon = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22);

                var stockLightsItem = new NativeItem("Stock Lights");
                if (!hasXenon)
                {
                    stockLightsItem.AltTitle = "";
                    itemOwnershipStatus[stockLightsItem] = STATUS_INSTALLED;
                }
                else
                {
                    stockLightsItem.AltTitle = "Free";
                }
                stockLightsItem.Activated += (s, e) => SetXenon(false);
                headlightsMenu.Add(stockLightsItem);

                var xenonLightsItem = new NativeItem("Xenon Lights");
                bool xenonOwned = VehicleSaveData.IsXenonOwned(currentVehicle.DisplayName);
                if (hasXenon)
                {
                    xenonLightsItem.AltTitle = "";
                    itemOwnershipStatus[xenonLightsItem] = STATUS_INSTALLED;
                    if (!xenonOwned) VehicleSaveData.SetXenonOwned(currentVehicle.DisplayName);
                }
                else if (xenonOwned)
                {
                    xenonLightsItem.AltTitle = "";
                    itemOwnershipStatus[xenonLightsItem] = STATUS_OWNED;
                }
                else
                {
                    xenonLightsItem.AltTitle = $"${ModPricing.XenonLightsPrice}";
                }
                xenonLightsItem.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsXenonOwned(currentVehicle.DisplayName);
                    if (!TryPurchase(ModPricing.XenonLightsPrice, owned)) return;

                    SetXenon(true);
                    if (!owned)
                    {
                        VehicleSaveData.SetXenonOwned(currentVehicle.DisplayName);
                        VehicleSaveData.Save();
                    }
                };
                headlightsMenu.Add(xenonLightsItem);

                // Headlight Color submenu (Side Course feature - requires xenon)
                if (ModSettings.LightCustomization)
                {
                    var headlightColorMenu = CreateHeadlightColorMenu();
                    headlightColorMenu.Closed += (s, e) => { if (!isNavigatingMenu) headlightsMenu.Visible = true; };

                    var headlightColorNavItem = new NativeItem("Headlight Color");
                    headlightColorNavItem.AltTitle = ">>";
                    headlightColorNavItem.Description = "Requires Xenon Lights";
                    headlightColorNavItem.Activated += (s, e) =>
                    {
                        // Check if xenon is installed
                        if (!Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22))
                        {
                            ShowNotification("~r~Install Xenon Lights first!");
                            return;
                        }
                        isNavigatingMenu = true;
                        headlightsMenu.Visible = false;
                        headlightColorMenu.Visible = true;
                        isNavigatingMenu = false;
                    };
                    headlightsMenu.Add(headlightColorNavItem);
                }

                var headlightsNavItem = new NativeItem("Headlights");
                headlightsNavItem.AltTitle = ">>";
                headlightsNavItem.Activated += (s, e) => { isNavigatingMenu = true; lightsMenu.Visible = false; headlightsMenu.Visible = true; isNavigatingMenu = false; };
                lightsMenu.Add(headlightsNavItem);

                // Neon Kits submenu
                var neonKitsMenu = CreateMenu("Neon Kits");
                neonKitsMenu.Closed += (s, e) => { if (!isNavigatingMenu) lightsMenu.Visible = true; };

                // Neon Layout submenu - Individual side toggles (like Menyoo)
                var neonLayoutMenu = CreateMenu("NEON LAYOUT");
                neonLayoutMenu.Closed += (s, e) => { if (!isNavigatingMenu) neonKitsMenu.Visible = true; };

                // Neon indices: 0=Left, 1=Right, 2=Front, 3=Back
                var neonSides = new[] {
                    (name: "Front", index: 2),
                    (name: "Back", index: 3),
                    (name: "Left", index: 0),
                    (name: "Right", index: 1)
                };

                foreach (var (sideName, sideIndex) in neonSides)
                {
                    bool isEnabled = GetNeonEnabled(sideIndex);
                    var sideItem = new NativeItem(sideName);

                    // Show sprite if enabled, price if not
                    if (isEnabled)
                    {
                        sideItem.AltTitle = "";
                        itemOwnershipStatus[sideItem] = STATUS_INSTALLED;
                    }
                    else
                    {
                        sideItem.AltTitle = "$500";
                    }

                    int idx = sideIndex; // Capture for closure
                    string name = sideName;
                    sideItem.Activated += (s, e) =>
                    {
                        bool currentState = GetNeonEnabled(idx);

                        // Only charge when turning ON
                        if (!currentState && !TryPurchase(500, false)) return;

                        // Toggle the neon
                        SetNeonEnabled(idx, !currentState);

                        // Update the item display
                        bool newState = !currentState;
                        sideItem.AltTitle = newState ? "" : "$500";
                        if (newState)
                            itemOwnershipStatus[sideItem] = STATUS_INSTALLED;
                        else
                            itemOwnershipStatus.Remove(sideItem);

                        if (activeDebugMode != DebugMode.None)
                            ShowNotification($"~g~{name} neon {(newState ? "installed" : "removed")}!");
                        MechanicSpeak();
                    };
                    neonLayoutMenu.Add(sideItem);
                }

                var neonLayoutNavItem = new NativeItem("Neon Layout");
                neonLayoutNavItem.AltTitle = ">>";
                neonLayoutNavItem.Activated += (s, e) => { isNavigatingMenu = true; neonKitsMenu.Visible = false; neonLayoutMenu.Visible = true; isNavigatingMenu = false; };
                neonKitsMenu.Add(neonLayoutNavItem);

                // Neon Color submenu - uses MenuConfig
                var neonColorMenu = CreateNeonColorMenuFromConfig();
                if (neonColorMenu != null)
                {
                    neonColorMenu.Closed += (s, e) => { if (!isNavigatingMenu) neonKitsMenu.Visible = true; };
                    var neonColorNavItem = new NativeItem("Neon Color");
                    neonColorNavItem.AltTitle = ">>";
                    neonColorNavItem.Activated += (s, e) => { isNavigatingMenu = true; neonKitsMenu.Visible = false; neonColorMenu.Visible = true; isNavigatingMenu = false; };
                    neonKitsMenu.Add(neonColorNavItem);
                }

                var neonKitsNavItem = new NativeItem("Neon Kits");
                neonKitsNavItem.AltTitle = ">>";
                neonKitsNavItem.Activated += (s, e) => { isNavigatingMenu = true; lightsMenu.Visible = false; neonKitsMenu.Visible = true; isNavigatingMenu = false; };
                lightsMenu.Add(neonKitsNavItem);

                // Turn off all lights when leaving the Lights menu
                lightsMenu.Closed += (s, e) =>
                {
                    currentLightMode = 0; // Reset light mode tracking
                    if (currentVehicle != null && currentVehicle.Exists())
                    {
                        Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 4); // Off
                        Function.Call(Hash.SET_VEHICLE_FULLBEAM, currentVehicle, false);
                        Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 1, false); // Left off
                        Function.Call(Hash.SET_VEHICLE_INDICATOR_LIGHTS, currentVehicle, 0, false); // Right off
                        Function.Call(Hash.SET_VEHICLE_INTERIORLIGHT, currentVehicle, false);
                        Function.Call(Hash.SET_VEHICLE_BRAKE_LIGHTS, currentVehicle, false);
                    }
                };

                AddSubmenuItem("Lights", lightsMenu);
            }

            // Livery - check both native livery system AND mod index 48
            int nativeLiveryCount = Function.Call<int>(Hash.GET_VEHICLE_LIVERY_COUNT, currentVehicle);
            int modLiveryCount = GetModCount(48); // Mod-based liveries
            int liveryCount = Math.Max(nativeLiveryCount, modLiveryCount);
            if (liveryCount > 0)
            {
                var liveryMenu = CreateLiveryMenu(liveryCount, nativeLiveryCount > 0);
                AddSubmenuItem("Livery", liveryMenu);
            }

            // Plate
            plateMenu = CreatePlateMenu();
            AddSubmenuItem("Plate", plateMenu);

            // Packages (Side Course feature - save/load mod presets)
            if (ModSettings.VehiclePackages)
            {
                var packagesMenu = CreatePackagesMenu();
                AddSubmenuItem("Packages", packagesMenu);
            }

            // Respray
            resprayMenu = BuildResprayMenu();
            AddSubmenuItem("Respray", resprayMenu);

            // Roll Cage (5)
            if (GetModCount(5) > 0)
            {
                var menu = CreateModMenuByIndex(5, GetModCount(5), "Roll Cage");
                AddSubmenuItem("Roll Cage", menu);
            }

            // Roof (10)
            if (GetModCount(10) > 0)
            {
                var menu = CreateModMenuByIndex(10, GetModCount(10), "Roof");
                AddSubmenuItem("Roof", menu);
            }

            // Skirts (3)
            if (GetModCount(3) > 0)
            {
                var menu = CreateModMenuByIndex(3, GetModCount(3), "Skirts");
                AddSubmenuItem("Skirts", menu);
            }

            // Spoiler (0)
            if (GetModCount(0) > 0)
            {
                var menu = CreateModMenuByIndex(0, GetModCount(0), "Spoiler");
                AddSubmenuItem("Spoiler", menu);
            }

            // Suspension (15)
            if (GetModCount(15) > 0)
            {
                var menu = CreateModMenuByIndex(15, GetModCount(15), "Suspension");
                AddSubmenuItem("Suspension", menu);
            }

            // Transmission (13)
            if (GetModCount(13) > 0)
            {
                var menu = CreateModMenuByIndex(13, GetModCount(13), "Transmission");

                // Add Manual Transmission option at the end
                AddManualTransmissionOption(menu);

                AddSubmenuItem("Transmission", menu);
            }

            // Turbo (18) - submenu like native LSC
            {
                turboMenu = CreateMenu("Turbo");

                bool hasTurbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 18);
                bool turboOwned = VehicleSaveData.IsTurboOwned(currentVehicle.DisplayName);

                var noneItem = new NativeItem("None");
                if (!hasTurbo)
                {
                    noneItem.AltTitle = "";
                    itemOwnershipStatus[noneItem] = STATUS_INSTALLED;
                }
                else
                {
                    noneItem.AltTitle = "Free";
                }
                noneItem.Activated += (s, e) => { SetTurbo(false); };
                turboMenu.Add(noneItem);

                var turboTuningItem = new NativeItem("Turbo Tuning");
                if (hasTurbo)
                {
                    turboTuningItem.AltTitle = "";
                    itemOwnershipStatus[turboTuningItem] = STATUS_INSTALLED;
                    if (!turboOwned) VehicleSaveData.SetTurboOwned(currentVehicle.DisplayName);
                }
                else if (turboOwned)
                {
                    turboTuningItem.AltTitle = "";
                    itemOwnershipStatus[turboTuningItem] = STATUS_OWNED;
                }
                else
                {
                    turboTuningItem.AltTitle = $"${ModPricing.TurboPrice}";
                }
                turboTuningItem.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsTurboOwned(currentVehicle.DisplayName);
                    if (!TryPurchase(ModPricing.TurboPrice, owned)) return;

                    SetTurbo(true);
                    if (!owned)
                    {
                        VehicleSaveData.SetTurboOwned(currentVehicle.DisplayName);
                        VehicleSaveData.Save();
                    }
                };
                turboMenu.Add(turboTuningItem);

                AddSubmenuItem("Turbo", turboMenu);
            }

            // Wheels
            wheelsMenu = BuildWheelsMenu();
            AddSubmenuItem("Wheels", wheelsMenu);

            // Windows (tint) - uses MenuConfig
            windowTintMenu = CreateWindowTintMenuFromConfig();
            if (windowTintMenu != null)
            {
                AddSubmenuItem("Windows", windowTintMenu);
            }
        }

        private NativeMenu BuildWheelsMenu()
        {
            var menu = CreateMenu("Wheels");

            // Wheel Type submenu
            var wheelTypeMenu = CreateMenu("Wheel Types");
            wheelTypeMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };

            // Wheel types in LSC order: High End, Lowrider, Muscle, Off-Road, Sport, SUV, Tuner
            var wheelTypes = new[] {
                (6, "High End"),
                (2, "Lowrider"),
                (1, "Muscle"),
                (4, "Off-Road"),
                (0, "Sport"),
                (3, "SUV"),
                (5, "Tuner")
            };

            foreach (var (typeIndex, typeName) in wheelTypes)
            {
                var typeWheelsMenu = CreateWheelTypeMenu(typeIndex);
                if (typeWheelsMenu != null)
                {
                    typeWheelsMenu.Name = typeName;
                    typeWheelsMenu.Closed += (s, e) => { if (!isNavigatingMenu) wheelTypeMenu.Visible = true; };
                    var typeItem = new NativeItem(typeName);
                    typeItem.AltTitle = ">>";
                    int idx = typeIndex;
                    typeItem.Activated += (s, e) => { isNavigatingMenu = true; wheelTypeMenu.Visible = false; typeWheelsMenu.Visible = true; isNavigatingMenu = false; };
                    wheelTypeMenu.Add(typeItem);
                }
            }

            var wheelTypeNavItem = new NativeItem("Wheel Type");
            wheelTypeNavItem.AltTitle = ">>";
            wheelTypeNavItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; wheelTypeMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(wheelTypeNavItem);

            // Wheel Color submenu
            var wheelColorMenu = CreateWheelColorMenu();
            wheelColorMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var wheelColorNavItem = new NativeItem("Wheel Color");
            wheelColorNavItem.AltTitle = ">>";
            wheelColorNavItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; wheelColorMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(wheelColorNavItem);

            // Tires submenu
            var tiresMenu = CreateMenu("Tires");
            tiresMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };

            // Tire Design - links to wheel type selection (same as above)
            var tireDesignNavItem = new NativeItem("Tire Design");
            tireDesignNavItem.AltTitle = ">>";
            tireDesignNavItem.Activated += (s, e) => { isNavigatingMenu = true; tiresMenu.Visible = false; wheelTypeMenu.Visible = true; isNavigatingMenu = false; };
            tiresMenu.Add(tireDesignNavItem);

            // Tire Enhancements submenu - uses MenuConfig
            var tireEnhancementsMenu = CreateTireEnhancementsMenuFromConfig();
            if (tireEnhancementsMenu != null)
            {
                tireEnhancementsMenu.Closed += (s, e) => { if (!isNavigatingMenu) tiresMenu.Visible = true; };
                var tireEnhancementsNavItem = new NativeItem("Tire Enhancements");
                tireEnhancementsNavItem.AltTitle = ">>";
                tireEnhancementsNavItem.Activated += (s, e) => { isNavigatingMenu = true; tiresMenu.Visible = false; tireEnhancementsMenu.Visible = true; isNavigatingMenu = false; };
                tiresMenu.Add(tireEnhancementsNavItem);
            }

            // Tire Smoke submenu - uses MenuConfig
            var tireSmokeMenu = CreateTireSmokeMenuFromConfig();
            if (tireSmokeMenu != null)
            {
                tireSmokeMenu.Closed += (s, e) => { if (!isNavigatingMenu) tiresMenu.Visible = true; };
                var tireSmokeNavItem = new NativeItem("Tire Smoke");
                tireSmokeNavItem.AltTitle = ">>";
                tireSmokeNavItem.Activated += (s, e) => { isNavigatingMenu = true; tiresMenu.Visible = false; tireSmokeMenu.Visible = true; isNavigatingMenu = false; };
                tiresMenu.Add(tireSmokeNavItem);
            }

            var tiresNavItem = new NativeItem("Tires");
            tiresNavItem.AltTitle = ">>";
            tiresNavItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; tiresMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(tiresNavItem);

            // Wheel Fitment submenu (VStancer-style adjustments)
            GTA.UI.Notification.Show($"~b~DEBUG~w~: VStancerIntegration={ModSettings.VStancerIntegration}");
            if (ModSettings.VStancerIntegration)
            {
                GTA.UI.Notification.Show("~g~Building Wheel Fitment menu...");
                wheelFitmentParent = menu;   // so BuildWheelFitmentMenu can restore this parent on close
                var fitmentMenu = BuildWheelFitmentMenu();
                if (fitmentMenu != null)
                {
                    // NOTE: the parent-restore Closed handler is wired INSIDE BuildWheelFitmentMenu (using
                    // wheelFitmentParent) so that rebuilt instances (Reset/Purchase) restore the parent too.
                    var fitmentNavItem = new NativeItem("Wheel Fitment");
                    fitmentNavItem.AltTitle = ">>";
                    fitmentNavItem.Description = "Adjust camber, track width, and ride height";
                    fitmentNavItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; fitmentMenu.Visible = true; isNavigatingMenu = false; };
                    menu.Add(fitmentNavItem);
                    GTA.UI.Notification.Show("~g~Wheel Fitment menu added!");
                }
                else
                {
                    GTA.UI.Notification.Show("~r~BuildWheelFitmentMenu returned null!");
                }
            }

            return menu;
        }

        private NativeMenu BuildWheelFitmentMenu()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return null;

            var menu = CreateMenu("Wheel Fitment");

            // Restore the parent (Wheels) menu when this menu closes. Wired here — not at the call site —
            // so EVERY built instance (including the ones rebuilt by Reset/Purchase) navigates back
            // correctly instead of vanishing and locking up.
            menu.Closed += (s, e) => { if (!isNavigatingMenu && wheelFitmentParent != null) wheelFitmentParent.Visible = true; };

            // Initialize fitment for current vehicle
            if (!wheelFitment.IsInitialized || lastFitmentVehicle != currentVehicle)
            {
                wheelFitment.Initialize(currentVehicle);
                lastFitmentVehicle = currentVehicle;

                // Load saved fitment if owned
                string vehName = currentVehicle.DisplayName;
                if (VehicleSaveData.IsWheelFitmentOwned(vehName))
                {
                    var saved = VehicleSaveData.GetFitmentData(vehName);
                    wheelFitment.FrontCamber = saved.FrontCamber;
                    wheelFitment.RearCamber = saved.RearCamber;
                    wheelFitment.FrontTrackWidth = saved.FrontTrackWidth;
                    wheelFitment.RearTrackWidth = saved.RearTrackWidth;
                    wheelFitment.FrontHeight = saved.FrontHeight;
                    wheelFitment.RearHeight = saved.RearHeight;
                }
            }

            // Check if fitment is already owned
            bool isOwned = VehicleSaveData.IsWheelFitmentOwned(currentVehicle.DisplayName);
            int fitmentPrice = 2500; // Base price for fitment service

            // Purchase/Status item
            var purchaseItem = new NativeItem(isOwned ? "Fitment Service" : "Purchase Fitment", isOwned ? "Installed" : $"${fitmentPrice:N0}");
            purchaseItem.Description = isOwned ? "Adjust your wheel fitment below" : "Purchase to enable wheel fitment adjustments";
            if (!isOwned)
            {
                purchaseItem.Activated += (s, e) =>
                {
                    int playerMoney = Game.Player.Money;
                    if (playerMoney >= fitmentPrice)
                    {
                        Game.Player.Money -= fitmentPrice;
                        VehicleSaveData.SetWheelFitmentOwned(currentVehicle.DisplayName, true);
                        VehicleSaveData.Save();
                        transactionAmount = fitmentPrice;
                        transactionStartTime = Game.GameTime;
                        GTA.UI.Notification.Show("Wheel Fitment service purchased!");
                        // Rebuild menu to show sliders
                        isNavigatingMenu = true;
                        menu.Visible = false;
                        var newMenu = BuildWheelFitmentMenu();
                        newMenu.Visible = true;
                        isNavigatingMenu = false;
                    }
                    else
                    {
                        GTA.UI.Notification.Show("~r~Not enough money!");
                    }
                };
            }
            menu.Add(purchaseItem);

            // Only show adjustment options if owned
            if (isOwned)
            {
                menu.Add(new NativeItem("") { Enabled = false }); // Spacer

                if (ModSettings.WheelFitmentProMode)
                {
                // ============ PRO MODE: raw per-axle controls ============
                // Front Camber slider
                var frontCamberValues = new List<float>();
                for (float v = WheelFitment.WheelFitment.MIN_CAMBER; v <= WheelFitment.WheelFitment.MAX_CAMBER + 0.001f; v += 0.01f)
                    frontCamberValues.Add((float)Math.Round(v, 2));
                int fcIdx = frontCamberValues.FindIndex(v => Math.Abs(v - wheelFitment.FrontCamber) < 0.005f);
                if (fcIdx < 0) fcIdx = frontCamberValues.Count / 2;
                var frontCamberItem = new NativeListItem<float>("Front Camber", frontCamberValues.ToArray()) { SelectedIndex = fcIdx };
                frontCamberItem.ItemChanged += (s, e) => {
                    wheelFitment.FrontCamber = e.Object;
                    GTA.UI.Notification.Show($"~y~Front Camber~w~: {e.Object:F2} rad ({WheelFitment.WheelFitment.ToDegrees(e.Object):F1}°)");
                };
                frontCamberItem.Description = $"Tilt front wheels ({WheelFitment.WheelFitment.ToDegrees(WheelFitment.WheelFitment.MIN_CAMBER):F0}° to {WheelFitment.WheelFitment.ToDegrees(WheelFitment.WheelFitment.MAX_CAMBER):F0}°)";
                menu.Add(frontCamberItem);

                // Rear Camber slider
                var rearCamberValues = new List<float>();
                for (float v = WheelFitment.WheelFitment.MIN_CAMBER; v <= WheelFitment.WheelFitment.MAX_CAMBER + 0.001f; v += 0.01f)
                    rearCamberValues.Add((float)Math.Round(v, 2));
                int rcIdx = rearCamberValues.FindIndex(v => Math.Abs(v - wheelFitment.RearCamber) < 0.005f);
                if (rcIdx < 0) rcIdx = rearCamberValues.Count / 2;
                var rearCamberItem = new NativeListItem<float>("Rear Camber", rearCamberValues.ToArray()) { SelectedIndex = rcIdx };
                rearCamberItem.ItemChanged += (s, e) => { wheelFitment.RearCamber = e.Object; };
                rearCamberItem.Description = $"Tilt rear wheels ({WheelFitment.WheelFitment.ToDegrees(WheelFitment.WheelFitment.MIN_CAMBER):F0}° to {WheelFitment.WheelFitment.ToDegrees(WheelFitment.WheelFitment.MAX_CAMBER):F0}°)";
                menu.Add(rearCamberItem);

                // Front Track Width slider
                var frontTrackValues = new List<float>();
                for (float v = WheelFitment.WheelFitment.MIN_TRACK_WIDTH; v <= WheelFitment.WheelFitment.MAX_TRACK_WIDTH + 0.001f; v += 0.01f)
                    frontTrackValues.Add((float)Math.Round(v, 2));
                int ftIdx = frontTrackValues.FindIndex(v => Math.Abs(v - wheelFitment.FrontTrackWidth) < 0.005f);
                if (ftIdx < 0) ftIdx = frontTrackValues.FindIndex(v => Math.Abs(v) < 0.005f);
                var frontTrackItem = new NativeListItem<float>("Front Track Width", frontTrackValues.ToArray()) { SelectedIndex = ftIdx };
                frontTrackItem.ItemChanged += (s, e) => {
                    wheelFitment.FrontTrackWidth = e.Object;
                    GTA.UI.Notification.Show($"~y~Front Track~w~: {e.Object:F2}m");
                };
                frontTrackItem.Description = "Widen or narrow front wheels";
                menu.Add(frontTrackItem);

                // Rear Track Width slider
                var rearTrackValues = new List<float>();
                for (float v = WheelFitment.WheelFitment.MIN_TRACK_WIDTH; v <= WheelFitment.WheelFitment.MAX_TRACK_WIDTH + 0.001f; v += 0.01f)
                    rearTrackValues.Add((float)Math.Round(v, 2));
                int rtIdx = rearTrackValues.FindIndex(v => Math.Abs(v - wheelFitment.RearTrackWidth) < 0.005f);
                if (rtIdx < 0) rtIdx = rearTrackValues.FindIndex(v => Math.Abs(v) < 0.005f);
                var rearTrackItem = new NativeListItem<float>("Rear Track Width", rearTrackValues.ToArray()) { SelectedIndex = rtIdx };
                rearTrackItem.ItemChanged += (s, e) => { wheelFitment.RearTrackWidth = e.Object; };
                rearTrackItem.Description = "Widen or narrow rear wheels";
                menu.Add(rearTrackItem);

                // Front Height slider
                var frontHeightValues = new List<float>();
                for (float v = WheelFitment.WheelFitment.MIN_HEIGHT; v <= WheelFitment.WheelFitment.MAX_HEIGHT + 0.001f; v += 0.01f)
                    frontHeightValues.Add((float)Math.Round(v, 2));
                int fhIdx = frontHeightValues.FindIndex(v => Math.Abs(v - wheelFitment.FrontHeight) < 0.005f);
                if (fhIdx < 0) fhIdx = frontHeightValues.FindIndex(v => Math.Abs(v) < 0.005f);
                var frontHeightItem = new NativeListItem<float>("Front Height", frontHeightValues.ToArray()) { SelectedIndex = fhIdx };
                frontHeightItem.ItemChanged += (s, e) => { wheelFitment.FrontHeight = e.Object; };
                frontHeightItem.Description = "Raise or lower front suspension";
                menu.Add(frontHeightItem);

                // Rear Height slider
                var rearHeightValues = new List<float>();
                for (float v = WheelFitment.WheelFitment.MIN_HEIGHT; v <= WheelFitment.WheelFitment.MAX_HEIGHT + 0.001f; v += 0.01f)
                    rearHeightValues.Add((float)Math.Round(v, 2));
                int rhIdx = rearHeightValues.FindIndex(v => Math.Abs(v - wheelFitment.RearHeight) < 0.005f);
                if (rhIdx < 0) rhIdx = rearHeightValues.FindIndex(v => Math.Abs(v) < 0.005f);
                var rearHeightItem = new NativeListItem<float>("Rear Height", rearHeightValues.ToArray()) { SelectedIndex = rhIdx };
                rearHeightItem.ItemChanged += (s, e) => { wheelFitment.RearHeight = e.Object; };
                rearHeightItem.Description = "Raise or lower rear suspension";
                menu.Add(rearHeightItem);

                // ---- Visual wheel size/width (reverse-engineered + verified, build 3788) ----
                if (wheelFitment.HasVisualWheels)
                {
                    var visSizeValues = new List<float>();
                    for (float v = WheelFitment.WheelFitment.MIN_VISUAL; v <= WheelFitment.WheelFitment.MAX_VISUAL + 0.001f; v += 0.05f)
                        visSizeValues.Add((float)Math.Round(v, 2));
                    int vsIdx = visSizeValues.FindIndex(v => Math.Abs(v - wheelFitment.VisualSize) < 0.025f);
                    if (vsIdx < 0) vsIdx = visSizeValues.FindIndex(v => Math.Abs(v - 1f) < 0.025f);
                    var visSizeItem = new NativeListItem<float>("Wheel Size", visSizeValues.ToArray()) { SelectedIndex = vsIdx };
                    visSizeItem.ItemChanged += (s, e) => {
                        wheelFitment.VisualSize = e.Object;
                        GTA.UI.Notification.Show($"~y~Wheel Size~w~: {e.Object:F2}x");
                    };
                    visSizeItem.Description = "Bigger/smaller wheels (visual + collision)";
                    menu.Add(visSizeItem);

                    var visWidthValues = new List<float>();
                    for (float v = WheelFitment.WheelFitment.MIN_VISUAL; v <= WheelFitment.WheelFitment.MAX_VISUAL + 0.001f; v += 0.05f)
                        visWidthValues.Add((float)Math.Round(v, 2));
                    int vwIdx = visWidthValues.FindIndex(v => Math.Abs(v - wheelFitment.VisualWidth) < 0.025f);
                    if (vwIdx < 0) vwIdx = visWidthValues.FindIndex(v => Math.Abs(v - 1f) < 0.025f);
                    var visWidthItem = new NativeListItem<float>("Wheel Width", visWidthValues.ToArray()) { SelectedIndex = vwIdx };
                    visWidthItem.ItemChanged += (s, e) => {
                        wheelFitment.VisualWidth = e.Object;
                        GTA.UI.Notification.Show($"~y~Wheel Width~w~: {e.Object:F2}x");
                    };
                    visWidthItem.Description = "Wider/narrower wheels (visual + collision)";
                    menu.Add(visWidthItem);
                }
                else
                {
                    var noVisualItem = new NativeItem("Wheel Size / Width", "Fit custom wheels") { Enabled = false };
                    noVisualItem.Description = "Install aftermarket wheels (LSC) to adjust visual size/width";
                    menu.Add(noVisualItem);
                }
                }
                else
                {
                    // ============ SIMPLE MODE: real-world units, one slider per concept ============
                    BuildSimpleFitmentItems(menu);
                }

                // Pro Mode toggle (persisted to INI)
                var proItem = new NativeCheckboxItem("Pro Mode", "Unlock per-axle camber, track width and height controls", ModSettings.WheelFitmentProMode);
                proItem.CheckboxChanged += (s, e) =>
                {
                    ModSettings.WheelFitmentProMode = proItem.Checked;
                    ModSettings.Save();
                    // Rebuild so the item set matches the mode
                    isNavigatingMenu = true;
                    menu.Visible = false;
                    var newMenu = BuildWheelFitmentMenu();
                    newMenu.Visible = true;
                    isNavigatingMenu = false;
                };
                menu.Add(proItem);

                menu.Add(new NativeItem("") { Enabled = false }); // Spacer

                // Save button
                var saveItem = new NativeItem("Save Fitment");
                saveItem.Description = "Save current fitment settings";
                saveItem.Activated += (s, e) =>
                {
                    SaveCurrentFitment();
                    GTA.UI.Notification.Show("Fitment saved!");
                };
                menu.Add(saveItem);

                // Reset button
                var resetItem = new NativeItem("Reset to Stock");
                resetItem.Description = "Reset all fitment to factory settings";
                resetItem.Activated += (s, e) =>
                {
                    wheelFitment.Reset();
                    SaveCurrentFitment();
                    GTA.UI.Notification.Show("Fitment reset to stock");
                    // Rebuild menu to update slider positions
                    isNavigatingMenu = true;
                    menu.Visible = false;
                    var newMenu = BuildWheelFitmentMenu();
                    newMenu.Visible = true;
                    isNavigatingMenu = false;
                };
                menu.Add(resetItem);

            }

            return menu;
        }

        /// <summary>
        /// Simple-mode fitment items: real-world units (inches / degrees / cm), one slider per concept,
        /// applied to all wheels. Auto-adapt (big wheels lift the body) and easing make it feel natural.
        /// </summary>
        private void BuildSimpleFitmentItems(NativeMenu menu)
        {
            // ---- Wheel Size (inches — the render size field is the wheel's diameter in meters) ----
            if (wheelFitment.HasVisualWheels)
            {
                // Range capped at THIS vehicle's stability budget (suspension travel) — the largest
                // size that rides cleanly with matching collision. Varies per car: trucks allow more.
                float maxMult = wheelFitment.MaxSizeMult;
                var sizeMults = new List<float>();
                var sizeLabels = new List<string>();
                for (float m = 0.7f; m <= maxMult + 0.001f; m += 0.05f)
                {
                    float mult = (float)Math.Round(Math.Min(m, maxMult), 3);
                    if (sizeMults.Count > 0 && mult <= sizeMults[sizeMults.Count - 1]) break;
                    sizeMults.Add(mult);
                    float inches = wheelFitment.BaseVisualDiameter * mult / 0.0254f;
                    sizeLabels.Add(Math.Abs(mult - 1f) < 0.001f ? $"{inches:F1}\" (stock)" : $"{inches:F1}\"");
                }
                int sIdx = 0;
                for (int i = 0; i < sizeMults.Count; i++)
                    if (Math.Abs(sizeMults[i] - wheelFitment.VisualSize) < 0.026f) { sIdx = i; break; }
                var sizeItem = new NativeListItem<string>("Wheel Size", sizeLabels.ToArray()) { SelectedIndex = sIdx };
                sizeItem.Description = "Overall wheel diameter. Oversized wheels lift the body automatically.";
                sizeItem.ItemChanged += (s, e) => { wheelFitment.VisualSize = sizeMults[e.Index]; };
                menu.Add(sizeItem);

                // ---- Tire Width (%) ----
                var widthMults = new List<float>();
                var widthLabels = new List<string>();
                for (float m = 0.6f; m <= 1.601f; m += 0.05f)
                {
                    float mult = (float)Math.Round(m, 2);
                    widthMults.Add(mult);
                    widthLabels.Add(Math.Abs(mult - 1f) < 0.001f ? "100% (stock)" : $"{mult * 100f:F0}%");
                }
                int wIdx = 0;
                for (int i = 0; i < widthMults.Count; i++)
                    if (Math.Abs(widthMults[i] - wheelFitment.VisualWidth) < 0.026f) { wIdx = i; break; }
                var widthItem = new NativeListItem<string>("Tire Width", widthLabels.ToArray()) { SelectedIndex = wIdx };
                widthItem.Description = "Fat meats or stretched rubber";
                widthItem.ItemChanged += (s, e) => { wheelFitment.VisualWidth = widthMults[e.Index]; };
                menu.Add(widthItem);
            }
            else
            {
                var hint = new NativeItem("Wheel Size / Width", "Fit custom wheels") { Enabled = false };
                hint.Description = "Install aftermarket wheels to unlock wheel sizing";
                menu.Add(hint);
            }

            // ---- Camber (degrees, all wheels) ----
            var camberDegs = new List<int>();
            var camberLabels = new List<string>();
            for (int d = -8; d <= 24; d++)
            {
                camberDegs.Add(d);
                camberLabels.Add(d == 0 ? "0° (stock)" : $"{d}°");
            }
            int cIdx = 8; // 0°
            float curDeg = WheelFitment.WheelFitment.ToDegrees(wheelFitment.FrontCamber);
            for (int i = 0; i < camberDegs.Count; i++)
                if (Math.Abs(camberDegs[i] - curDeg) < 0.51f) { cIdx = i; break; }
            var camberItem = new NativeListItem<string>("Camber", camberLabels.ToArray()) { SelectedIndex = cIdx };
            camberItem.Description = "Tilt the wheels in for that stanced look";
            camberItem.ItemChanged += (s, e) =>
            {
                float rad = WheelFitment.WheelFitment.ToRadians(camberDegs[e.Index]);
                wheelFitment.FrontCamber = rad;
                wheelFitment.RearCamber = rad;
            };
            menu.Add(camberItem);

            // ---- Ride Height (cm; + lift / - slam). Internal sign is inverted: +Z moves the WHEEL up
            //      relative to the body, which sits the body LOWER — so internal = -cm/100. ----
            var heightCms = new List<int>();
            var heightLabels = new List<string>();
            for (int cm = -14; cm <= 14; cm++)
            {
                heightCms.Add(cm);
                heightLabels.Add(cm == 0 ? "0 cm (stock)" : (cm > 0 ? $"+{cm} cm" : $"{cm} cm"));
            }
            int hIdx = 14; // 0 cm
            float curCm = -wheelFitment.FrontHeight * 100f;
            for (int i = 0; i < heightCms.Count; i++)
                if (Math.Abs(heightCms[i] - curCm) < 0.51f) { hIdx = i; break; }
            var heightItem = new NativeListItem<string>("Ride Height", heightLabels.ToArray()) { SelectedIndex = hIdx };
            heightItem.Description = "Lift (+) or slam (-) the whole car";
            heightItem.ItemChanged += (s, e) =>
            {
                float h = -heightCms[e.Index] / 100f;
                wheelFitment.FrontHeight = h;
                wheelFitment.RearHeight = h;
            };
            menu.Add(heightItem);

            // ---- Wheel Poke (track width, cm out from the body) ----
            var trackCms = new List<int>();
            var trackLabels = new List<string>();
            for (int cm = -5; cm <= 20; cm++)
            {
                trackCms.Add(cm);
                trackLabels.Add(cm == 0 ? "0 cm (stock)" : (cm > 0 ? $"+{cm} cm" : $"{cm} cm"));
            }
            int tIdx = 5; // 0 cm
            float curTcm = wheelFitment.FrontTrackWidth * 100f;
            for (int i = 0; i < trackCms.Count; i++)
                if (Math.Abs(trackCms[i] - curTcm) < 0.51f) { tIdx = i; break; }
            var trackItem = new NativeListItem<string>("Wheel Poke", trackLabels.ToArray()) { SelectedIndex = tIdx };
            trackItem.Description = "Push the wheels out toward the fenders";
            trackItem.ItemChanged += (s, e) =>
            {
                float t = trackCms[e.Index] / 100f;
                wheelFitment.FrontTrackWidth = t;
                wheelFitment.RearTrackWidth = t;
            };
            menu.Add(trackItem);
        }

        private void SaveCurrentFitment()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            var fitmentData = new VehicleSaveData.FitmentData
            {
                FrontCamber = wheelFitment.FrontCamber,
                RearCamber = wheelFitment.RearCamber,
                FrontTrackWidth = wheelFitment.FrontTrackWidth,
                RearTrackWidth = wheelFitment.RearTrackWidth,
                FrontHeight = wheelFitment.FrontHeight,
                RearHeight = wheelFitment.RearHeight
            };
            VehicleSaveData.SetFitmentData(currentVehicle.DisplayName, fitmentData);
            VehicleSaveData.Save();

            // Also record this SPECIFIC car (model + plate + fingerprint + decorator tag) so the stance
            // auto-applies when the player later walks up to THIS car (and not other cars of the model).
            if (_stanceMgrInit)
            {
                if (stanceManager.RecordCar(currentVehicle, wheelFitment, out string stMsg))
                    Log($"[Stance] {stMsg}");
                else
                    GTA.UI.Notification.Show($"~o~Stance not saved per-car: {stMsg}");
            }
        }

        private NativeMenu BuildResprayMenu()
        {
            var menu = CreateMenu("Respray");
            // Primary color
            var primaryMenu = CreateColorMenu(true);
            primaryMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var primaryItem = new NativeItem("Primary Color");
            primaryItem.AltTitle = ">>";
            primaryItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; primaryMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(primaryItem);

            // Secondary color
            var secondaryMenu = CreateColorMenu(false);
            secondaryMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var secondaryItem = new NativeItem("Secondary Color");
            secondaryItem.AltTitle = ">>";
            secondaryItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; secondaryMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(secondaryItem);

            // Pearlescent
            var pearlMenu = CreatePearlescentMenu();
            pearlMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var pearlItem = new NativeItem("Pearlescent");
            pearlItem.AltTitle = ">>";
            pearlItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; pearlMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(pearlItem);

            // Wheel color
            var wheelColorMenu = CreateWheelColorMenu();
            wheelColorMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            var wheelColorItem = new NativeItem("Wheel Color");
            wheelColorItem.AltTitle = ">>";
            wheelColorItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; wheelColorMenu.Visible = true; isNavigatingMenu = false; };
            menu.Add(wheelColorItem);

            return menu;
        }

        /// <summary>
        /// Recursively builds a menu from a MenuConfig Category.
        /// Handles arbitrary nesting depth with proper navigation (no stacking).
        /// </summary>
        /// <param name="category">The category to build a menu from</param>
        /// <param name="parentMenu">The parent menu to return to when closing</param>
        /// <returns>The built menu</returns>
        private NativeMenu BuildCategoryMenu(MenuConfig.Category category, NativeMenu parentMenu)
        {
            var menu = CreateMenu(category.DisplayName);
            dynamicCategoryMenus.Add(menu); // Track for cleanup on rebuild

            // Set up back navigation to parent
            menu.Closed += (s, e) =>
            {
                if (!isNavigatingMenu && parentMenu != null)
                    parentMenu.Visible = true;
            };

            // Add items if the category has any
            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);

                if (!string.IsNullOrEmpty(item.Description))
                {
                    menuItem.Description = item.Description;
                }

                // Handle pricing/installed status
                menuItem.AltTitle = item.Price == 0 ? "Free" : $"${item.Price}";

                var capturedItem = item;
                menuItem.Activated += (s, e) =>
                {
                    // Handle different item types
                    if (item.Type == "color" && item.R.HasValue && item.G.HasValue && item.B.HasValue)
                    {
                        // Color item - could be neon, tire smoke, etc.
                        Log($"[Dynamic] Color selected: {item.Name} ({item.R},{item.G},{item.B})");
                    }
                    else if (item.Type == "toggle")
                    {
                        // Toggle item
                        Log($"[Dynamic] Toggle selected: {item.Name} = {item.Value}");
                    }
                    else if (item.Value >= 0)
                    {
                        // Standard mod item with value
                        Log($"[Dynamic] Item selected: {item.Name} = {item.Value}");
                    }
                };

                menu.Add(menuItem);
            }

            // Add subcategory navigation items
            foreach (var subCategory in category.SubCategories)
            {
                var subMenu = BuildCategoryMenu(subCategory, menu);

                var navItem = new NativeItem(subCategory.DisplayName);
                navItem.AltTitle = ">>";

                if (!string.IsNullOrEmpty(subCategory.Description))
                {
                    navItem.Description = subCategory.Description;
                }

                // Capture for closure
                var capturedSubMenu = subMenu;
                var capturedMenu = menu;
                navItem.Activated += (s, e) =>
                {
                    isNavigatingMenu = true;
                    capturedMenu.Visible = false;
                    capturedSubMenu.Visible = true;
                    isNavigatingMenu = false;
                };

                menu.Add(navItem);
            }

            return menu;
        }

        /// <summary>
        /// Adds a dynamically loaded category to the main menu.
        /// </summary>
        /// <param name="category">The category to add</param>
        private void AddDynamicCategory(MenuConfig.Category category)
        {
            // Build the category's submenu
            var categoryMenu = BuildCategoryMenu(category, mainMenu);

            // Add navigation item to main menu
            var navItem = new NativeItem(category.DisplayName);
            navItem.AltTitle = ">>";

            if (!string.IsNullOrEmpty(category.Description))
            {
                navItem.Description = category.Description;
            }

            var capturedMenu = categoryMenu;
            navItem.Activated += (s, e) =>
            {
                isNavigatingMenu = true;
                mainMenu.Visible = false;
                capturedMenu.Visible = true;
                isNavigatingMenu = false;
            };

            categoryMenu.Closed += (s, e) =>
            {
                if (!isNavigatingMenu)
                    mainMenu.Visible = true;
            };

            mainMenu.Add(navItem);
        }

        /// <summary>
        /// Builds the main menu dynamically from MenuConfig folders.
        /// Custom categories appear alongside built-in ones.
        /// </summary>
        private void BuildDynamicMainMenu()
        {
            // Get all root categories from config
            var categories = MenuConfig.GetRootCategories();

            Log($"[Dynamic] Found {categories.Count} root categories");

            foreach (var category in categories)
            {
                // Skip categories that are handled specially by the hardcoded menu
                // These have special game logic (mod indices, colors, etc.)
                var specialCategories = new[] {
                    "Armor", "Brakes", "Bumpers", "Engine", "Exhaust", "Fenders",
                    "Grille", "Hood", "Horn", "Lights", "Livery", "Plate",
                    "Respray", "Roll Cage", "Roof", "Skirts", "Spoiler",
                    "Suspension", "Transmission", "Turbo", "Wheels", "Windows"
                };

                bool isSpecial = false;
                foreach (var special in specialCategories)
                {
                    if (string.Equals(category.Name, special, StringComparison.OrdinalIgnoreCase))
                    {
                        isSpecial = true;
                        break;
                    }
                }

                if (!isSpecial)
                {
                    // This is a user-created custom category
                    Log($"[Dynamic] Adding custom category: {category.Name}");
                    AddDynamicCategory(category);
                }
            }
        }

        #endregion

        #region MenuConfig-Based Menu Creators

        /// <summary>
        /// Create a menu from MenuConfig items.json with full ownership tracking
        /// </summary>
        private NativeMenu CreateMenuFromConfig(string categoryPath, Func<int> getCurrentValue, Action<MenuConfig.MenuItem> onActivate)
        {
            var category = MenuConfig.LoadCategory(categoryPath);
            if (category == null)
            {
                Log($"[MenuConfig] Category not found: {categoryPath}");
                return null;
            }

            var menu = CreateMenu(category.DisplayName);
            int currentValue = getCurrentValue?.Invoke() ?? -1;
            string vehicleName = currentVehicle?.DisplayName ?? "";

            foreach (var item in category.Items)
            {
                bool isInstalled = item.Value == currentValue;
                bool isOwned = item.Value >= 0 && VehicleSaveData.IsCustomItemOwned(vehicleName, categoryPath, item.Value);
                var menuItem = new NativeItem(item.Name);

                if (!string.IsNullOrEmpty(item.Description))
                {
                    menuItem.Description = item.Description;
                }

                // Store metadata for refresh capability
                configItemMetadata[menuItem] = (categoryPath, item.Value, item.Price);

                if (isInstalled)
                {
                    menuItem.AltTitle = "";
                    itemOwnershipStatus[menuItem] = STATUS_INSTALLED;
                    // Mark as owned if currently installed
                    if (item.Value >= 0 && !isOwned)
                    {
                        VehicleSaveData.SetCustomItemOwned(vehicleName, categoryPath, item.Value);
                    }
                }
                else if (isOwned)
                {
                    menuItem.AltTitle = "";
                    itemOwnershipStatus[menuItem] = STATUS_OWNED;
                }
                else
                {
                    menuItem.AltTitle = item.Price == 0 ? "Free" : $"${item.Price}";
                }

                var capturedItem = item;
                var capturedCategoryPath = categoryPath;
                int capturedPrice = item.Price;
                menuItem.Activated += (s, e) =>
                {
                    if (currentVehicle == null) return;

                    bool owned = capturedItem.Value >= 0 && VehicleSaveData.IsCustomItemOwned(currentVehicle.DisplayName, capturedCategoryPath, capturedItem.Value);
                    if (!TryPurchase(capturedPrice, owned)) return;

                    onActivate(capturedItem);
                    // Mark as owned when purchased
                    if (capturedItem.Value >= 0 && !owned)
                    {
                        VehicleSaveData.SetCustomItemOwned(currentVehicle.DisplayName, capturedCategoryPath, capturedItem.Value);
                        VehicleSaveData.Save();
                    }
                    // Refresh the menu to show updated icons
                    RefreshConfigMenuStatus(capturedCategoryPath, capturedItem.Value);
                };
                menu.Add(menuItem);
            }

            // Store menu reference for refresh
            configMenusByPath[categoryPath] = menu;

            return menu;
        }

        /// <summary>
        /// Refresh the ownership status icons for a config-based menu after applying an item
        /// </summary>
        private void RefreshConfigMenuStatus(string categoryPath, int newInstalledValue)
        {
            if (!configMenusByPath.TryGetValue(categoryPath, out var menu)) return;
            string vehicleName = currentVehicle?.DisplayName ?? "";

            foreach (var menuItemBase in menu.Items)
            {
                var menuItem = menuItemBase as NativeItem;
                if (menuItem == null) continue;

                if (!configItemMetadata.TryGetValue(menuItem, out var metadata)) continue;
                if (metadata.categoryPath != categoryPath) continue;

                bool isNowInstalled = (metadata.itemValue == newInstalledValue);
                bool isOwned = metadata.itemValue >= 0 && VehicleSaveData.IsCustomItemOwned(vehicleName, categoryPath, metadata.itemValue);

                itemOwnershipStatus.Remove(menuItem);

                if (isNowInstalled)
                {
                    menuItem.AltTitle = "";
                    itemOwnershipStatus[menuItem] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    menuItem.AltTitle = "";
                    itemOwnershipStatus[menuItem] = STATUS_OWNED;
                }
                else
                {
                    menuItem.AltTitle = metadata.price == 0 ? "Free" : $"${metadata.price}";
                }
            }
        }

        /// <summary>
        /// Create Window Tint menu from MenuConfig
        /// </summary>
        private NativeMenu CreateWindowTintMenuFromConfig()
        {
            return CreateMenuFromConfig(
                "Windows",
                () => (int)currentVehicle.Mods.WindowTint,
                (item) => ApplyWindowTint(item.Value)
            );
        }

        /// <summary>
        /// Create Neon Color menu from MenuConfig
        /// </summary>
        private NativeMenu CreateNeonColorMenuFromConfig()
        {
            var category = MenuConfig.LoadCategory("Lights/Neon Kits/Neon Color");
            if (category == null) return null;

            var menu = CreateMenu("Neon Color");

            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);
                menuItem.AltTitle = item.Price == 0 ? "Free" : $"${item.Price}";

                int r = item.R ?? 255;
                int g = item.G ?? 255;
                int b = item.B ?? 255;
                int price = item.Price;
                menuItem.Activated += (s, e) =>
                {
                    if (!TryPurchase(price, false)) return;
                    ApplyNeonColor(r, g, b);
                };
                menu.Add(menuItem);
            }

            return menu;
        }

        /// <summary>
        /// Create Tire Smoke menu from MenuConfig
        /// </summary>
        private NativeMenu CreateTireSmokeMenuFromConfig()
        {
            var category = MenuConfig.LoadCategory("Wheels/Tires/Tire Smoke");
            if (category == null) return null;

            var menu = CreateMenu("Tire Smoke");

            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);
                menuItem.AltTitle = item.Price == 0 ? "Free" : $"${item.Price}";

                int r = item.R ?? 255;
                int g = item.G ?? 255;
                int b = item.B ?? 255;
                int price = item.Price;
                menuItem.Activated += (s, e) =>
                {
                    if (!TryPurchase(price, false)) return;
                    ApplyTireSmoke(r, g, b);
                };
                menu.Add(menuItem);
            }

            return menu;
        }

        /// <summary>
        /// Create Tire Enhancements menu from MenuConfig
        /// </summary>
        private NativeMenu CreateTireEnhancementsMenuFromConfig()
        {
            return CreateMenuFromConfig(
                "Wheels/Tires/Tire Enhancements",
                () => currentVehicle.CanTiresBurst == false ? 1 : 0, // 0 = standard, 1 = bulletproof
                (item) => SetBulletproofTires(item.Value == 1)
            );
        }

        /// <summary>
        /// Get description from MenuConfig
        /// </summary>
        private string GetDescriptionFromConfig(string categoryPath)
        {
            return MenuConfig.GetDescription(categoryPath);
        }

        /// <summary>
        /// Get the actual description to display for an item.
        /// Checks item's built-in Description first, then falls back to MenuConfig.
        /// </summary>
        private string GetItemDescription(NativeItem item)
        {
            if (item == null) return null;

            // First check the item's built-in Description property (set directly on item)
            if (!string.IsNullOrEmpty(item.Description))
                return item.Description;

            // Fall back to MenuConfig lookup by title
            return GetDescriptionFromConfig(item.Title);
        }

        /// <summary>
        /// Exit current debug mode and log any relevant settings
        /// </summary>
        private void ExitDebugMode()
        {
            // Log settings based on which mode was active
            if (activeDebugMode == DebugMode.Resizer)
            {
                Log($"=== RESIZER SETTINGS ===");
                Log($"Transaction (-$) Scale: {transactionTextScale:F3}");
                Log($"Description Scale: {descriptionTextScale:F3}");
                Log($"Sprite Scale: {spriteScale:F3}");
                Log($"Stats Row Height: {statsRowHeight:F4}");
                Log($"Stats Segment Height: {statsSegmentHeight:F4}");
                Log($"Stats Segment Gap: {statsSegmentGap:F4}");
                Log($"Stats Label Scale: {statsLabelScale:F3}");
                Log($"Stats Panel Padding: {statsPanelPadding:F4}");
                Log($"========================");
            }
            else if (activeDebugMode == DebugMode.InputTiming)
            {
                Log($"=== INPUT TIMING SETTINGS ===");
                Log($"Initial Delay: {inputInitialDelay}ms");
                Log($"Slow Repeat: {inputSlowRepeat}ms");
                Log($"Fast Repeat: {inputFastRepeat}ms");
                Log($"Accel Threshold: {inputAccelThreshold}");
                Log($"=============================");
            }
            else if (activeDebugMode == DebugMode.MenuPosition)
            {
                Log($"=== MENU POSITION SETTINGS ===");
                Log($"Menu Offset: {mainMenu.Offset}");
                Log($"Scroll Arrows Offset: X={scrollArrowsOffsetX:F4}, Y={scrollArrowsOffsetY:F4}");
                Log($"Description Offset: X={descriptionOffsetX:F4}, Y={descriptionOffsetY:F4}");
                Log($"Stats Panel Offset: X={statsPanelOffsetX:F4}, Y={statsPanelOffsetY:F4}");
                Log($"===============================");
            }

            // Re-enable menu input if it was disabled
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu)
                {
                    menu.AcceptsInput = true;
                }
            }

            activeDebugMode = DebugMode.None;
            debugGameInputMode = false;  // Reset game input mode
            ShowNotification("Debug Mode ~r~OFF");
        }

        #endregion

        #region Dynamic Menu Creators

        private NativeMenu CreateModMenuByIndex(int modIndex, int count, string displayName)
        {
            // Use SubMenuTitle from ModCategories if available
            string menuTitle = displayName;
            bool noStockOption = false;
            if (ModCategories.AllCategories.TryGetValue(modIndex, out var category))
            {
                menuTitle = category.SubMenuTitle;
                noStockOption = category.NoStockOption;
            }

            var menu = CreateMenu(menuTitle);
            modMenusByIndex[modIndex] = menu;

            int currentMod = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, modIndex);
            string vehicleName = currentVehicle.DisplayName;
            int idx = modIndex; // Capture for closure
            bool hasStock = !noStockOption; // Capture for closure

            // Preview system: store original when menu opens
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    previewModIndex = idx;
                    previewOriginalValue = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, idx);
                    isPreviewingMod = true;

                    // Store original stats for performance mod preview comparison
                    // Performance mod indices: Engine=11, Brakes=12, Transmission=13, Suspension=15, Armor=16, Turbo=18
                    if (idx == 11 || idx == 12 || idx == 13 || idx == 15 || idx == 16 || idx == 18)
                    {
                        originalTopSpeed = Function.Call<float>(Hash.GET_VEHICLE_ESTIMATED_MAX_SPEED, currentVehicle);
                        originalAcceleration = Function.Call<float>(Hash.GET_VEHICLE_ACCELERATION, currentVehicle);
                        originalBraking = Function.Call<float>(Hash.GET_VEHICLE_MAX_BRAKING, currentVehicle);
                        originalTraction = Function.Call<float>(Hash.GET_VEHICLE_MAX_TRACTION, currentVehicle);
                        hasStoredOriginalStats = true;
                    }
                }
            };

            // Preview system: revert to original when menu closes
            menu.Closed += (s, e) =>
            {
                if (isPreviewingMod && currentVehicle != null && currentVehicle.Exists() && previewModIndex == idx)
                {
                    // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, idx, previewOriginalValue, false);
                    isPreviewingMod = false;
                    previewModIndex = -1;
                    hasStoredOriginalStats = false;
                }
            };

            // Preview system: preview mod on selection change
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    // If no stock option: Index 0 = mod 0, Index 1 = mod 1, etc.
                    // If has stock: Index 0 = Stock (-1), Index 1+ = mod value (index - 1)
                    int previewValue = hasStock ? (e.Index == 0 ? -1 : e.Index - 1) : e.Index;
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, idx, previewValue, false);
                }
            };

            // Stock/None option - skip for performance mods that don't have stock
            if (!noStockOption)
            {
                string stockLabel;
                switch (modIndex)
                {
                    case 16: stockLabel = "None"; break;           // Armor
                    default: stockLabel = "Stock"; break;
                }
                var stockItem = new NativeItem(stockLabel);
                if (currentMod == -1)
                {
                    stockItem.AltTitle = "";
                    itemOwnershipStatus[stockItem] = STATUS_INSTALLED;
                }
                else
                {
                    stockItem.AltTitle = "Free";
                }
                stockItem.Activated += (s, e) =>
                {
                    // Purchasing stock - clear preview state first
                    isPreviewingMod = false;
                    previewModIndex = -1;
                    ApplyModByIndex(idx, -1);
                };
                menu.Add(stockItem);
            }

            // Mod options
            for (int i = 0; i < count; i++)
            {
                int price = ModPricing.GetModPriceByIndex(modIndex, i);
                string name = ModPricing.IsPerformanceModIndex(modIndex)
                    ? ModPricing.GetPerformanceLevelNameByIndex(modIndex, i)
                    : GetModNameByIndex(modIndex, i);

                bool isInstalled = currentMod == i;
                bool isOwned = VehicleSaveData.IsModOwned(vehicleName, modIndex, i);

                var item = new NativeItem(name);

                // Set AltTitle and track ownership status for icon display
                if (isInstalled)
                {
                    item.AltTitle = ""; // Empty - icon will be drawn
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    // Mark as owned since it's installed
                    if (!isOwned) VehicleSaveData.SetModOwned(vehicleName, modIndex, i);
                }
                else if (isOwned)
                {
                    item.AltTitle = ""; // Empty - icon will be drawn
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = $"${price:N0}";
                }

                int modValue = i; // Capture for closure
                int capturedPrice = price;
                item.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsModOwned(currentVehicle.DisplayName, idx, modValue);
                    if (!TryPurchase(capturedPrice, owned)) return;

                    // Purchasing - clear preview state first
                    isPreviewingMod = false;
                    previewModIndex = -1;

                    ApplyModByIndex(idx, modValue);
                    // Mark as owned when purchased
                    if (!owned)
                    {
                        VehicleSaveData.SetModOwned(currentVehicle.DisplayName, idx, modValue);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateModMenu(VehicleModType modType, int count)
        {
            return CreateModMenuByIndex((int)modType, count, ModPricing.GetModTypeName(modType));
        }

        private NativeMenu CreateWheelTypeMenu(int wheelType)
        {
            // Set wheel type temporarily to count wheels
            int origType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);

            int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, 23); // 23 = front wheels

            // Restore original
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, origType);

            if (count <= 0) return null;

            var menu = CreateMenu(ModPricing.GetWheelTypeName(wheelType));

            for (int i = 0; i < count; i++)
            {
                int price = ModPricing.GetWheelPrice(wheelType, i);
                var item = new NativeItem($"Wheel {i + 1}");
                item.AltTitle = $"${price:N0}";

                int wheelIndex = i;
                int wType = wheelType;
                int wheelPrice = price;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(wheelPrice, false)) return;
                    ApplyWheel(wType, wheelIndex);
                };
                menu.Add(item);
            }

            return menu;
        }

        /// <summary>
        /// Create menu for toggling vehicle extras (Side Course feature)
        /// Vehicle extras are additional parts that can be shown/hidden (0-14)
        /// </summary>
        private NativeMenu CreateExtrasMenu()
        {
            var menu = CreateMenu("EXTRAS");

            if (currentVehicle == null || !currentVehicle.Exists())
                return menu;

            // Check each extra slot (0-14) to see if vehicle has it
            int extrasFound = 0;
            for (int i = 0; i < 15; i++)
            {
                // Check if this extra exists on the vehicle
                // GET_VEHICLE_MOD_KIT returns -1 if extra doesn't exist
                bool extraExists = Function.Call<bool>(Hash.DOES_EXTRA_EXIST, currentVehicle, i);

                if (extraExists)
                {
                    extrasFound++;
                    int extraIndex = i; // Capture for closure

                    // Get current state
                    bool isEnabled = Function.Call<bool>(Hash.IS_VEHICLE_EXTRA_TURNED_ON, currentVehicle, extraIndex);

                    var item = new NativeItem($"Extra {extraIndex}");
                    item.AltTitle = isEnabled ? "~g~ON" : "~r~OFF";

                    item.Activated += (s, e) =>
                    {
                        // Toggle the extra
                        bool currentState = Function.Call<bool>(Hash.IS_VEHICLE_EXTRA_TURNED_ON, currentVehicle, extraIndex);
                        Function.Call(Hash.SET_VEHICLE_EXTRA, currentVehicle, extraIndex, currentState); // true = OFF, false = ON (inverted!)

                        // Update display
                        bool newState = Function.Call<bool>(Hash.IS_VEHICLE_EXTRA_TURNED_ON, currentVehicle, extraIndex);
                        item.AltTitle = newState ? "~g~ON" : "~r~OFF";

                        if (activeDebugMode != DebugMode.None)
                            ShowNotification($"~g~Extra {extraIndex} {(newState ? "enabled" : "disabled")}!");
                    };

                    menu.Add(item);
                }
            }

            if (extrasFound == 0)
            {
                var noExtrasItem = new NativeItem("No extras available");
                noExtrasItem.Enabled = false;
                menu.Add(noExtrasItem);
            }

            return menu;
        }

        /// <summary>
        /// Create menu for selecting xenon headlight color (Side Course feature)
        /// Colors 0-12 are available when xenon lights are installed
        /// </summary>
        private NativeMenu CreateHeadlightColorMenu()
        {
            var menu = CreateMenu("HEADLIGHT COLOR");

            // Xenon headlight color names (indices 0-12)
            var colors = new[]
            {
                (0, "Default White"),
                (1, "White"),
                (2, "Blue"),
                (3, "Electric Blue"),
                (4, "Mint Green"),
                (5, "Lime Green"),
                (6, "Yellow"),
                (7, "Golden Shower"),
                (8, "Orange"),
                (9, "Red"),
                (10, "Pony Pink"),
                (11, "Hot Pink"),
                (12, "Purple")
            };

            // Get current color
            int currentColor = Function.Call<int>(Hash.GET_VEHICLE_XENON_LIGHT_COLOR_INDEX, currentVehicle);

            foreach (var (colorIndex, colorName) in colors)
            {
                var item = new NativeItem(colorName);
                int idx = colorIndex; // Capture for closure

                if (currentColor == colorIndex)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else
                {
                    item.AltTitle = colorIndex == 0 ? "Free" : "$250";
                }

                item.Activated += (s, e) =>
                {
                    // Check if xenon is installed
                    if (!Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22))
                    {
                        ShowNotification("~r~Install Xenon Lights first!");
                        return;
                    }

                    // Purchase if not free and not already installed
                    if (idx > 0 && currentColor != idx)
                    {
                        if (!TryPurchase(250, false)) return;
                    }

                    // Apply color
                    Function.Call(Hash.SET_VEHICLE_XENON_LIGHT_COLOR_INDEX, currentVehicle, idx);
                    if (activeDebugMode != DebugMode.None)
                        ShowNotification($"~g~{colorName} headlights installed!");

                    // Update menu items
                    RefreshHeadlightColorMenu(menu, idx);
                };

                menu.Add(item);
            }

            // Turn on headlights when menu opens so user can see colors
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    // Force headlights on (2 = always on)
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 2);
                }
            };

            // Preview on selection change
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists() &&
                    Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22))
                {
                    Function.Call(Hash.SET_VEHICLE_XENON_LIGHT_COLOR_INDEX, currentVehicle, e.Index);
                }
            };

            // Store original color for revert on close
            int originalColor = currentColor;
            menu.Closed += (s, e) =>
            {
                // Turn headlights back to normal mode (0 = auto)
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 0);
                }
            };

            return menu;
        }

        /// <summary>
        /// Refresh headlight color menu after purchase
        /// </summary>
        private void RefreshHeadlightColorMenu(NativeMenu menu, int newColorIndex)
        {
            for (int i = 0; i < menu.Items.Count; i++)
            {
                var item = menu.Items[i] as NativeItem;
                if (item == null) continue;

                itemOwnershipStatus.Remove(item);

                if (i == newColorIndex)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else
                {
                    item.AltTitle = i == 0 ? "Free" : "$250";
                }
            }
        }

        #region Vehicle Packages (Side Course)

        private string PackagesDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "Packages");

        /// <summary>
        /// Create menu for saving/loading vehicle mod packages (Side Course feature)
        /// </summary>
        private NativeMenu CreatePackagesMenu()
        {
            var menu = CreateMenu("PACKAGES");

            // Save current mods option
            var saveItem = new NativeItem("Save Current Mods");
            saveItem.Description = "Save all current modifications to a package file";
            saveItem.Activated += (s, e) =>
            {
                SaveVehiclePackage();
            };
            menu.Add(saveItem);

            menu.Add(new NativeSeparatorItem());

            // Load saved packages
            LoadPackageItems(menu);

            return menu;
        }

        /// <summary>
        /// Load package files and add them to the menu
        /// </summary>
        private void LoadPackageItems(NativeMenu menu)
        {
            try
            {
                if (!Directory.Exists(PackagesDirectory))
                {
                    var noPackagesItem = new NativeItem("No saved packages");
                    noPackagesItem.Enabled = false;
                    menu.Add(noPackagesItem);
                    return;
                }

                string vehicleModel = currentVehicle?.Model.ToString() ?? "";
                string[] packageFiles = Directory.GetFiles(PackagesDirectory, "*.json");

                int packagesFound = 0;
                foreach (string file in packageFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);

                    // Package format: VehicleModel_PackageName.json
                    // Only show packages for this vehicle model (or universal packages starting with "All_")
                    if (!fileName.StartsWith(vehicleModel + "_") && !fileName.StartsWith("All_"))
                        continue;

                    packagesFound++;
                    string displayName = fileName.Contains("_") ? fileName.Substring(fileName.IndexOf("_") + 1) : fileName;
                    string filePath = file; // Capture for closure

                    var packageItem = new NativeItem(displayName);
                    packageItem.Description = $"Load {displayName} package";
                    packageItem.AltTitle = ">>";
                    packageItem.Activated += (s, e) =>
                    {
                        LoadVehiclePackage(filePath);
                    };
                    menu.Add(packageItem);
                }

                if (packagesFound == 0)
                {
                    var noPackagesItem = new NativeItem("No packages for this vehicle");
                    noPackagesItem.Enabled = false;
                    menu.Add(noPackagesItem);
                }
            }
            catch (Exception ex)
            {
                Log($"Error loading packages: {ex.Message}");
                var errorItem = new NativeItem("Error loading packages");
                errorItem.Enabled = false;
                menu.Add(errorItem);
            }
        }

        /// <summary>
        /// Save current vehicle mods to a package file
        /// </summary>
        private void SaveVehiclePackage()
        {
            if (currentVehicle == null || !currentVehicle.Exists())
            {
                ShowNotification("~r~No vehicle to save!");
                return;
            }

            try
            {
                // Create packages directory if needed
                if (!Directory.Exists(PackagesDirectory))
                    Directory.CreateDirectory(PackagesDirectory);

                string vehicleModel = currentVehicle.Model.ToString();
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"{vehicleModel}_{timestamp}.json";
                string filePath = Path.Combine(PackagesDirectory, fileName);

                // Collect current mods
                var package = new System.Text.StringBuilder();
                package.AppendLine("{");
                package.AppendLine($"  \"vehicle\": \"{vehicleModel}\",");
                package.AppendLine($"  \"created\": \"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",");
                package.AppendLine("  \"mods\": {");

                // Save all mod slots (0-48)
                bool first = true;
                for (int modIndex = 0; modIndex <= 48; modIndex++)
                {
                    int modValue = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, modIndex);
                    if (modValue >= 0) // Only save if mod is installed
                    {
                        if (!first) package.AppendLine(",");
                        package.Append($"    \"{modIndex}\": {modValue}");
                        first = false;
                    }
                }
                package.AppendLine();
                package.AppendLine("  },");

                // Save colors
                int primary, secondary;
                unsafe
                {
                    Function.Call(Hash.GET_VEHICLE_COLOURS, currentVehicle, &primary, &secondary);
                }
                package.AppendLine($"  \"primaryColor\": {primary},");
                package.AppendLine($"  \"secondaryColor\": {secondary},");

                // Save wheel type
                int wheelType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
                package.AppendLine($"  \"wheelType\": {wheelType},");

                // Save turbo
                bool hasTurbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 18);
                package.AppendLine($"  \"turbo\": {hasTurbo.ToString().ToLower()},");

                // Save xenon
                bool hasXenon = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22);
                package.AppendLine($"  \"xenon\": {hasXenon.ToString().ToLower()}");

                package.AppendLine("}");

                File.WriteAllText(filePath, package.ToString());
                ShowNotification($"~g~Package saved: {timestamp}");
                Log($"Package saved to: {filePath}");
            }
            catch (Exception ex)
            {
                ShowNotification("~r~Failed to save package!");
                Log($"Error saving package: {ex.Message}");
            }
        }

        /// <summary>
        /// Load and apply a vehicle package
        /// </summary>
        private void LoadVehiclePackage(string filePath)
        {
            if (currentVehicle == null || !currentVehicle.Exists())
            {
                ShowNotification("~r~No vehicle!");
                return;
            }

            try
            {
                string json = File.ReadAllText(filePath);

                // Simple JSON parsing (avoid external dependencies)
                // Parse mods section
                int modsStart = json.IndexOf("\"mods\":");
                if (modsStart >= 0)
                {
                    int modsObjStart = json.IndexOf("{", modsStart);
                    int modsObjEnd = json.IndexOf("}", modsObjStart);
                    string modsSection = json.Substring(modsObjStart + 1, modsObjEnd - modsObjStart - 1);

                    // Ensure mod kit is installed
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);

                    // Parse each mod
                    string[] modEntries = modsSection.Split(',');
                    foreach (string entry in modEntries)
                    {
                        string trimmed = entry.Trim().Trim('"');
                        if (string.IsNullOrEmpty(trimmed)) continue;

                        int colonIndex = trimmed.IndexOf("\":");
                        if (colonIndex > 0)
                        {
                            string modIndexStr = trimmed.Substring(0, colonIndex).Trim().Trim('"');
                            string modValueStr = trimmed.Substring(colonIndex + 2).Trim();

                            if (int.TryParse(modIndexStr, out int modIndex) && int.TryParse(modValueStr, out int modValue))
                            {
                                Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, modIndex, modValue, false);
                            }
                        }
                    }
                }

                // Parse turbo
                if (json.Contains("\"turbo\": true"))
                {
                    Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 18, true);
                }

                // Parse xenon
                if (json.Contains("\"xenon\": true"))
                {
                    Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 22, true);
                }

                ShowNotification("~g~Package loaded!");
                Log($"Package loaded from: {filePath}");
            }
            catch (Exception ex)
            {
                ShowNotification("~r~Failed to load package!");
                Log($"Error loading package: {ex.Message}");
            }
        }

        #endregion

        #region Manual Transmission Integration (ELSC Built-in)

        /// <summary>
        /// Add Manual Transmission option to a transmission menu
        /// Simple: select to enable and bind keys, select again to disable
        /// </summary>
        private void AddManualTransmissionOption(NativeMenu menu)
        {
            if (!ModSettings.ManualTransmission) return;
            if (currentVehicle == null || !currentVehicle.Exists()) return;

            // Check if ELSC MT is available
            if (!ELSCTransmission.IsAvailable)
            {
                var notAvailableItem = new NativeItem("Manual Transmission");
                notAvailableItem.AltTitle = "Unavailable";
                notAvailableItem.Description = "Manual transmission requires memory access. Check log for details.";
                notAvailableItem.Enabled = false;
                menu.Add(notAvailableItem);
                return;
            }

            // Check if MT was previously purchased for this vehicle
            bool isOwned = VehicleSaveData.IsManualTransmissionOwned(currentVehicle.DisplayName);

            // If owned but not currently enabled, auto-enable it
            if (isOwned && !elscTransmission.IsEnabled)
            {
                elscTransmission.SetVehicle(currentVehicle);
                elscTransmission.Enable();
            }

            var mtItem = new NativeItem("Manual Transmission");
            mtItem.AltTitle = elscTransmission.IsEnabled ? "" : "$0"; // Empty when installed - sprite shows instead
            mtItem.Description = elscTransmission.IsEnabled
                ? $"Shift Up: {ModSettings.ShiftUpKey} | Shift Down: {ModSettings.ShiftDownKey} | Select to disable"
                : "Select to enable and configure shift buttons";
            if (elscTransmission.IsEnabled)
                itemOwnershipStatus[mtItem] = STATUS_INSTALLED;

            mtItem.Activated += (s, e) =>
            {
                if (elscTransmission.IsEnabled)
                {
                    // Disable manual transmission and save state
                    elscTransmission.Disable();
                    VehicleSaveData.SetManualTransmissionOwned(currentVehicle.DisplayName, false);
                    VehicleSaveData.Save();
                    mtItem.AltTitle = "$0";
                    mtItem.Description = "Select to enable and configure shift buttons";
                    itemOwnershipStatus.Remove(mtItem);
                    ShowNotification("~y~Manual Transmission disabled");
                }
                else
                {
                    // Start key binding sequence - store item reference for updating later
                    mtBindingState = 1;
                    mtBindingItem = mtItem;
                }
            };
            menu.Add(mtItem);
        }

        #endregion

        private NativeMenu CreateLiveryMenu(int count, bool useNativeLivery)
        {
            var menu = CreateMenu("LIVERY");
            bool isNative = useNativeLivery;
            string vehicleName = currentVehicle.DisplayName;

            // Get current livery based on system used
            int currentLivery;
            if (useNativeLivery)
            {
                currentLivery = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, currentVehicle);
            }
            else
            {
                currentLivery = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 48);
            }

            // Store original livery for preview revert
            int originalLivery = currentLivery;

            // Preview system: revert to original when menu closes
            menu.Closed += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    if (isNative)
                        Function.Call(Hash.SET_VEHICLE_LIVERY, currentVehicle, originalLivery);
                    else
                    {
                        Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                        Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 48, originalLivery, false);
                    }
                }
            };

            // Preview system: preview livery on selection change
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    // Index 0 = None (-1), Index 1+ = livery (index - 1)
                    int previewLivery = e.Index == 0 ? -1 : e.Index - 1;
                    if (isNative)
                        Function.Call(Hash.SET_VEHICLE_LIVERY, currentVehicle, previewLivery);
                    else
                    {
                        Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                        Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 48, previewLivery, false);
                    }
                }
            };

            // None/Stock option
            var noneItem = new NativeItem("None");
            if (currentLivery == -1)
            {
                noneItem.AltTitle = "";
                itemOwnershipStatus[noneItem] = STATUS_INSTALLED;
            }
            else
            {
                noneItem.AltTitle = "Free";
            }
            noneItem.Activated += (s, e) =>
            {
                originalLivery = -1; // Update original so close doesn't revert
                ApplyLivery(-1, isNative);
            };
            menu.Add(noneItem);

            for (int i = 0; i < count; i++)
            {
                int price = ModPricing.GetModPriceByIndex(48, i);
                bool isInstalled = currentLivery == i;
                bool isOwned = VehicleSaveData.IsModOwned(vehicleName, 48, i);

                // Try to get livery name from game
                string liveryName = $"Livery {i + 1}";
                if (!useNativeLivery)
                {
                    string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, 48, i);
                    if (!string.IsNullOrEmpty(label) && label != "NULL")
                    {
                        string name = Game.GetLocalizedString(label);
                        if (!string.IsNullOrEmpty(name))
                            liveryName = name;
                    }
                }

                var item = new NativeItem(liveryName);

                if (isInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = $"${price:N0}";
                }

                int liveryIndex = i;
                int liveryPrice = price;
                item.Activated += (s, e) =>
                {
                    bool alreadyOwned = VehicleSaveData.IsModOwned(vehicleName, 48, liveryIndex);
                    if (!alreadyOwned && !TryPurchase(liveryPrice, false)) return;
                    if (!alreadyOwned) VehicleSaveData.SetModOwned(vehicleName, 48, liveryIndex);
                    originalLivery = liveryIndex; // Update original so close doesn't revert
                    ApplyLivery(liveryIndex, isNative);
                    RefreshLiveryMenuStatus(liveryIndex);
                };
                menu.Add(item);
            }

            return menu;
        }

        /// <summary>
        /// Refresh the livery menu ownership status after applying a livery
        /// </summary>
        private void RefreshLiveryMenuStatus(int newInstalledIndex)
        {
            // Find the livery menu in the menu pool
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu && menu.Name == "LIVERY")
                {
                    string vehicleName = currentVehicle?.DisplayName ?? "";

                    for (int i = 0; i < menu.Items.Count; i++)
                    {
                        var item = menu.Items[i] as NativeItem;
                        if (item == null) continue;

                        int itemValue = i - 1; // None is at index 0 with value -1
                        bool isNowInstalled = (itemValue == newInstalledIndex);
                        bool isOwned = itemValue >= 0 && VehicleSaveData.IsModOwned(vehicleName, 48, itemValue);

                        itemOwnershipStatus.Remove(item);

                        if (isNowInstalled)
                        {
                            item.AltTitle = "";
                            itemOwnershipStatus[item] = STATUS_INSTALLED;
                        }
                        else if (isOwned)
                        {
                            item.AltTitle = "";
                            itemOwnershipStatus[item] = STATUS_OWNED;
                        }
                        else
                        {
                            int price = ModPricing.GetModPriceByIndex(48, itemValue);
                            item.AltTitle = itemValue == -1 ? "Free" : $"${price:N0}";
                        }
                    }
                    break;
                }
            }
        }

        // Color preview state
        private bool isPreviewingColor = false;
        private int originalPrimaryColor = -1;
        private int originalSecondaryColor = -1;
        private int originalPearlescent = -1;

        private NativeMenu CreateColorMenu(bool isPrimary)
        {
            var menu = CreateMenu(isPrimary ? "Primary" : "Secondary");

            // Create category submenus
            var categories = new (string name, VehicleColors.ColorInfo[] colors, int paintType, int price)[]
            {
                ("Classic", VehicleColors.ClassicColors, 0, ModPricing.ClassicPrice),
                ("Metallic", VehicleColors.MetallicColors, 1, ModPricing.MetallicPrice),
                ("Matte", VehicleColors.MatteColors, 3, ModPricing.MattePrice),
                ("Metal", VehicleColors.MetalFinishes, 4, ModPricing.MetalPrice),
                ("Chrome", VehicleColors.ChromeColors, 5, ModPricing.ChromePrice),
            };

            foreach (var (categoryName, colors, paintType, basePrice) in categories)
            {
                var categoryMenu = CreateMenu(categoryName);
                bool capturedIsPrimary = isPrimary;
                int capturedPaintType = paintType;
                var capturedColors = colors;

                // Store original color when menu opens and preview selected color
                categoryMenu.Shown += (s, e) =>
                {
                    if (currentVehicle != null && currentVehicle.Exists())
                    {
                        unsafe
                        {
                            int p, sec;
                            Function.Call(Hash.GET_VEHICLE_COLOURS, currentVehicle, &p, &sec);
                            originalPrimaryColor = p;
                            originalSecondaryColor = sec;
                            int pearl, wheel;
                            Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &pearl, &wheel);
                            originalPearlescent = pearl;
                        }
                        isPreviewingColor = true;

                        // Preview the currently selected color (not always index 0)
                        int selectedIdx = categoryMenu.SelectedIndex;
                        if (selectedIdx >= 0 && selectedIdx < capturedColors.Length)
                        {
                            var selectedColor = capturedColors[selectedIdx];
                            if (capturedIsPrimary)
                            {
                                currentVehicle.Mods.PrimaryColor = (VehicleColor)selectedColor.ColorIndex;
                                if (selectedColor.PearlescentSpec > 0)
                                    currentVehicle.Mods.PearlescentColor = (VehicleColor)selectedColor.PearlescentSpec;
                            }
                            else
                            {
                                currentVehicle.Mods.SecondaryColor = (VehicleColor)selectedColor.ColorIndex;
                            }
                        }
                    }
                };

                // Revert to original when menu closes
                categoryMenu.Closed += (s, e) =>
                {
                    if (isPreviewingColor && currentVehicle != null && currentVehicle.Exists())
                    {
                        // Restore original colors
                        Function.Call(Hash.SET_VEHICLE_COLOURS, currentVehicle, originalPrimaryColor, originalSecondaryColor);
                        Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, originalPearlescent, 0);
                        isPreviewingColor = false;
                    }
                    if (!isNavigatingMenu) menu.Visible = true;
                };

                // Preview color on selection change
                categoryMenu.SelectedIndexChanged += (s, e) =>
                {
                    if (currentVehicle != null && currentVehicle.Exists() && e.Index >= 0 && e.Index < capturedColors.Length)
                    {
                        var color = capturedColors[e.Index];
                        // Preview using direct color set (like Menyoo does)
                        // SET_VEHICLE_MOD_COLOR_1's 3rd param is paint INDEX in category, not raw color ID
                        // So we use SET_VEHICLE_COLOURS for raw color IDs
                        if (capturedIsPrimary)
                        {
                            currentVehicle.Mods.PrimaryColor = (VehicleColor)color.ColorIndex;
                            if (color.PearlescentSpec > 0)
                                currentVehicle.Mods.PearlescentColor = (VehicleColor)color.PearlescentSpec;
                        }
                        else
                        {
                            currentVehicle.Mods.SecondaryColor = (VehicleColor)color.ColorIndex;
                        }
                    }
                };

                // Track items for ownership sprite
                string priceText = $"${basePrice}";
                var colorItems = new List<(NativeItem item, VehicleColors.ColorInfo color)>();

                foreach (var color in colors)
                {
                    var item = new NativeItem(color.DisplayName);
                    item.AltTitle = priceText;
                    var capturedColor = color;
                    int capturedPrice = basePrice;
                    item.Activated += (s, e) =>
                    {
                        if (!TryPurchase(capturedPrice, false)) return;
                        isPreviewingColor = false;
                        ApplyPaintColor(capturedColor.ColorIndex, capturedColor.PearlescentSpec, capturedPaintType, capturedIsPrimary);

                        // Update sprite - mark this as installed, clear others
                        foreach (var (otherItem, _) in colorItems)
                        {
                            itemOwnershipStatus.Remove(otherItem);
                            otherItem.AltTitle = priceText;
                        }
                        itemOwnershipStatus[item] = STATUS_INSTALLED;
                        item.AltTitle = "";
                    };
                    categoryMenu.Add(item);
                    colorItems.Add((item, color));
                }

                var capturedColorItems = colorItems;
                string capturedPriceText = priceText;

                // Update sprite when menu opens based on current vehicle color
                categoryMenu.Shown += (s, e) =>
                {
                    if (currentVehicle == null || !currentVehicle.Exists()) return;

                    int currentPaintType, currentColorIndex;
                    unsafe
                    {
                        int pt, ci, pl;
                        if (capturedIsPrimary)
                            Function.Call(Hash.GET_VEHICLE_MOD_COLOR_1, currentVehicle, &pt, &ci, &pl);
                        else
                            Function.Call(Hash.GET_VEHICLE_MOD_COLOR_2, currentVehicle, &pt, &ci);
                        currentPaintType = pt;
                        currentColorIndex = ci;
                    }

                    // Restore prices and clear sprites
                    foreach (var (item, _) in capturedColorItems)
                    {
                        itemOwnershipStatus.Remove(item);
                        item.AltTitle = capturedPriceText;
                    }

                    // Mark current as installed (hide price, show sprite)
                    if (currentPaintType == capturedPaintType)
                    {
                        foreach (var (item, color) in capturedColorItems)
                        {
                            if (color.ColorIndex == currentColorIndex)
                            {
                                itemOwnershipStatus[item] = STATUS_INSTALLED;
                                item.AltTitle = "";
                                break;
                            }
                        }
                    }
                };

                var navItem = new NativeItem(categoryName);
                navItem.AltTitle = ">>";
                navItem.Description = $"{colors.Length} colors";
                var capturedMenu = categoryMenu;
                navItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; capturedMenu.Visible = true; isNavigatingMenu = false; };
                menu.Add(navItem);
            }

            return menu;
        }

        private NativeMenu CreatePearlescentMenu()
        {
            var menu = CreateMenu("Pearlescent");
            var pearlColors = VehicleColors.PearlescentColors;

            // Store original pearlescent when menu opens and preview selected color
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    int wheel;
                    unsafe
                    {
                        int pearl, w;
                        Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &pearl, &w);
                        originalPearlescent = pearl;
                        wheel = w;
                    }
                    isPreviewingColor = true;

                    // Preview the currently selected pearlescent color (not always index 0)
                    int selectedIdx = menu.SelectedIndex;
                    if (selectedIdx >= 0 && selectedIdx < pearlColors.Length)
                    {
                        Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, pearlColors[selectedIdx].ColorIndex, wheel);
                    }
                }
            };

            // Revert to original when menu closes
            menu.Closed += (s, e) =>
            {
                if (isPreviewingColor && currentVehicle != null && currentVehicle.Exists())
                {
                    int wheel;
                    unsafe
                    {
                        int p, w;
                        Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &p, &w);
                        wheel = w;
                    }
                    Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, originalPearlescent, wheel);
                    isPreviewingColor = false;
                }
            };

            // Preview pearlescent on selection change
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists() && e.Index >= 0 && e.Index < pearlColors.Length)
                {
                    int wheel;
                    unsafe
                    {
                        int p, w;
                        Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &p, &w);
                        wheel = w;
                    }
                    Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, pearlColors[e.Index].ColorIndex, wheel);
                }
            };

            // Track items for ownership sprite
            string pearlPriceText = $"${ModPricing.PearlescentPrice}";
            var pearlItems = new List<(NativeItem item, int colorIndex)>();

            foreach (var color in pearlColors)
            {
                var item = new NativeItem(color.DisplayName);
                item.AltTitle = pearlPriceText;
                int colorId = color.ColorIndex;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(ModPricing.PearlescentPrice, false)) return;
                    isPreviewingColor = false;
                    ApplyPearlescent(colorId);

                    foreach (var (otherItem, _) in pearlItems)
                    {
                        itemOwnershipStatus.Remove(otherItem);
                        otherItem.AltTitle = pearlPriceText;
                    }
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    item.AltTitle = "";
                };
                menu.Add(item);
                pearlItems.Add((item, colorId));
            }

            var capturedPearlItems = pearlItems;
            menu.Shown += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;

                // Get current paint type and pearlescent
                int currentPaintType, currentPearl;
                unsafe
                {
                    int pt, ci, pl, w;
                    Function.Call(Hash.GET_VEHICLE_MOD_COLOR_1, currentVehicle, &pt, &ci, &pl);
                    Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &pl, &w);
                    currentPaintType = pt;
                    currentPearl = pl;
                }

                // Reset all items
                foreach (var (item, colorIndex) in capturedPearlItems)
                {
                    itemOwnershipStatus.Remove(item);
                    item.AltTitle = pearlPriceText;
                }

                // Only show installed sprite if paint type supports pearlescent (Metallic=1 or Pearl=2)
                if (currentPaintType == 1 || currentPaintType == 2)
                {
                    foreach (var (item, colorIndex) in capturedPearlItems)
                    {
                        if (colorIndex == currentPearl)
                        {
                            itemOwnershipStatus[item] = STATUS_INSTALLED;
                            item.AltTitle = "";
                            break;
                        }
                    }
                }
            };

            return menu;
        }

        private NativeMenu CreateWheelColorMenu()
        {
            var menu = CreateMenu("Wheel Color");
            var wheelColors = VehicleColors.WheelColors;
            int originalWheelColor = 0;

            // Store original wheel color when menu opens and preview selected color
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    int pearl;
                    unsafe
                    {
                        int p, wheel;
                        Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &p, &wheel);
                        originalWheelColor = wheel;
                        pearl = p;
                    }
                    isPreviewingColor = true;

                    // Preview the currently selected wheel color (not always index 0)
                    int selectedIdx = menu.SelectedIndex;
                    if (selectedIdx >= 0 && selectedIdx < wheelColors.Length)
                    {
                        Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, pearl, wheelColors[selectedIdx].ColorIndex);
                    }
                }
            };

            // Revert to original when menu closes
            menu.Closed += (s, e) =>
            {
                if (isPreviewingColor && currentVehicle != null && currentVehicle.Exists())
                {
                    int pearl;
                    unsafe
                    {
                        int p, w;
                        Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &p, &w);
                        pearl = p;
                    }
                    Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, pearl, originalWheelColor);
                    isPreviewingColor = false;
                }
            };

            // Preview wheel color on selection change
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists() && e.Index >= 0 && e.Index < wheelColors.Length)
                {
                    int pearl;
                    unsafe
                    {
                        int p, w;
                        Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &p, &w);
                        pearl = p;
                    }
                    Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, pearl, wheelColors[e.Index].ColorIndex);
                }
            };

            // Track items for ownership sprite
            string wheelPriceText = $"${ModPricing.WheelColorPrice}";
            var wheelColorItems = new List<(NativeItem item, int colorIndex)>();

            foreach (var color in wheelColors)
            {
                var item = new NativeItem(color.DisplayName);
                item.AltTitle = wheelPriceText;
                int colorId = color.ColorIndex;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(ModPricing.WheelColorPrice, false)) return;
                    isPreviewingColor = false;
                    ApplyWheelColor(colorId);

                    foreach (var (otherItem, _) in wheelColorItems)
                    {
                        itemOwnershipStatus.Remove(otherItem);
                        otherItem.AltTitle = wheelPriceText;
                    }
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    item.AltTitle = "";
                };
                menu.Add(item);
                wheelColorItems.Add((item, colorId));
            }

            var capturedWheelColorItems = wheelColorItems;
            menu.Shown += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;

                int currentWheelColor;
                unsafe
                {
                    int p, w;
                    Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &p, &w);
                    currentWheelColor = w;
                }

                foreach (var (item, colorIndex) in capturedWheelColorItems)
                {
                    itemOwnershipStatus.Remove(item);
                    item.AltTitle = wheelPriceText;
                    if (colorIndex == currentWheelColor)
                    {
                        itemOwnershipStatus[item] = STATUS_INSTALLED;
                        item.AltTitle = "";
                    }
                }
            };

            return menu;
        }

        private NativeMenu CreateTireSmokeMenu()
        {
            var menu = CreateMenu("Tire Smoke");

            var smokeColors = new[] {
                ("White", 255, 255, 255),
                ("Black", 20, 20, 20),
                ("Red", 255, 0, 0),
                ("Green", 0, 255, 0),
                ("Blue", 0, 0, 255),
                ("Yellow", 255, 255, 0),
                ("Orange", 255, 128, 0),
                ("Pink", 255, 0, 255),
                ("Purple", 128, 0, 255)
            };

            foreach (var (name, r, g, b) in smokeColors)
            {
                var item = new NativeItem(name);
                item.AltTitle = "$500";
                int cr = r, cg = g, cb = b;
                item.Activated += (s, e) =>
                {
                    if (!TryPurchase(500, false)) return;
                    ApplyTireSmoke(cr, cg, cb);
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateNeonMenu()
        {
            var menu = CreateMenu("Neon");

            // Toggle positions
            string[] positions = { "Front", "Back", "Left", "Right" };
            for (int i = 0; i < 4; i++)
            {
                bool isOn = Function.Call<bool>((Hash)0x8C4B92553E4571EC, currentVehicle, i);
                var item = new NativeItem(positions[i]);
                item.AltTitle = isOn ? "~g~On" : "Off - $500";
                int pos = i;
                bool wasOn = isOn;
                item.Activated += (s, e) =>
                {
                    if (!wasOn && !TryPurchase(500, false)) return;
                    ToggleNeon(pos);
                };
                menu.Add(item);
            }

            menu.Add(new NativeSeparatorItem());

            // Neon colors - load from MenuConfig
            var neonCategory = MenuConfig.LoadCategory("Lights/Neon Kits/Neon Color");
            if (neonCategory != null)
            {
                foreach (var colorItem in neonCategory.Items)
                {
                    var item = new NativeItem(colorItem.Name);
                    item.AltTitle = colorItem.Price == 0 ? "Free" : $"${colorItem.Price}";
                    int r = colorItem.R ?? 255;
                    int g = colorItem.G ?? 255;
                    int b = colorItem.B ?? 255;
                    int price = colorItem.Price;
                    item.Activated += (s, e) =>
                    {
                        if (!TryPurchase(price, false)) return;
                        ApplyNeonColor(r, g, b);
                    };
                    menu.Add(item);
                }
            }

            return menu;
        }

        private NativeMenu CreateHornMenu()
        {
            var menu = CreateMenu("Horn");

            int currentHorn = currentVehicle.Mods[VehicleModType.Horns].Index;
            int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, (int)VehicleModType.Horns);

            string[] hornNames = {
                "Stock Horn", "Star Spangled Banner", "Classical Horn 1", "Classical Horn 2",
                "Classical Horn 3", "Classical Horn 4", "Classical Horn 5", "Classical Horn 6",
                "Classical Horn 7", "Scale: Do", "Scale: Re", "Scale: Mi", "Scale: Fa",
                "Scale: Sol", "Scale: La", "Scale: Ti", "Scale: Do (High)",
                "Jazz Horn 1", "Jazz Horn 2", "Jazz Horn 3", "Jazz Loop",
                "Classical Loop", "San Andreas Loop", "Liberty City Loop"
            };

            string vehicleName = currentVehicle.DisplayName;
            for (int i = -1; i < count && i < hornNames.Length - 1; i++)
            {
                string name = i == -1 ? hornNames[0] : hornNames[Math.Min(i + 1, hornNames.Length - 1)];
                bool isInstalled = currentHorn == i;
                bool isOwned = i >= 0 && VehicleSaveData.IsHornOwned(vehicleName, i);
                var item = new NativeItem(name);

                if (isInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    if (i >= 0 && !isOwned) VehicleSaveData.SetHornOwned(vehicleName, i);
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = i == -1 ? "Free" : "$100";
                }

                int hornIndex = i;
                int hornPrice = i == -1 ? 0 : 100; // Stock is free, others $100
                item.Activated += (s, e) =>
                {
                    bool owned = hornIndex >= 0 && VehicleSaveData.IsHornOwned(currentVehicle.DisplayName, hornIndex);
                    if (!TryPurchase(hornPrice, owned)) return;

                    ApplyHorn(hornIndex);
                    if (hornIndex >= 0 && !owned)
                    {
                        VehicleSaveData.SetHornOwned(currentVehicle.DisplayName, hornIndex);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateWindowTintMenu()
        {
            var menu = CreateMenu("Window Tint");

            // Order matches real LSC: None, Light Smoke, Dark Smoke, Limo
            // Values map to VehicleWindowTint enum
            var tintOptions = new (string Name, int Value)[]
            {
                ("None", 0),        // VehicleWindowTint.None
                ("Light Smoke", 3), // VehicleWindowTint.LightSmoke
                ("Dark Smoke", 2),  // VehicleWindowTint.DarkSmoke
                ("Limo", 5)         // VehicleWindowTint.Limo
            };

            int currentTint = (int)currentVehicle.Mods.WindowTint;

            string vehicleName = currentVehicle.DisplayName;
            for (int i = 0; i < tintOptions.Length; i++)
            {
                var (name, tintValue) = tintOptions[i];
                int price = i == 0 ? 0 : 500; // None is free, others cost $500
                bool isInstalled = currentTint == tintValue;
                bool isOwned = tintValue > 0 && VehicleSaveData.IsTintOwned(vehicleName, tintValue);
                var item = new NativeItem(name);

                if (isInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    if (tintValue > 0 && !isOwned) VehicleSaveData.SetTintOwned(vehicleName, tintValue);
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = price == 0 ? "Free" : $"${price}";
                }

                int idx = tintValue;
                item.Activated += (s, e) =>
                {
                    ApplyWindowTint(idx);
                    if (idx > 0)
                    {
                        VehicleSaveData.SetTintOwned(currentVehicle.DisplayName, idx);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreatePlateMenu()
        {
            var menu = CreateMenu("License Plate");

            string[] plates = { "Blue on White 1", "Yellow on Black", "Yellow on Blue", "Blue on White 2", "Blue on White 3", "Yankton" };
            int currentPlate = (int)currentVehicle.Mods.LicensePlateStyle;

            string vehicleName = currentVehicle.DisplayName;
            for (int i = 0; i < plates.Length; i++)
            {
                bool isInstalled = currentPlate == i;
                bool isOwned = VehicleSaveData.IsPlateOwned(vehicleName, i);
                var item = new NativeItem(plates[i]);

                if (isInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    if (!isOwned) VehicleSaveData.SetPlateOwned(vehicleName, i);
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = "$200";
                }

                int plateIndex = i;
                int platePrice = 200;
                item.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsPlateOwned(currentVehicle.DisplayName, plateIndex);
                    if (!TryPurchase(platePrice, owned)) return;

                    ApplyPlateStyle(plateIndex);
                    if (!owned)
                    {
                        VehicleSaveData.SetPlateOwned(currentVehicle.DisplayName, plateIndex);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private string GetModName(VehicleModType modType, int index)
        {
            return GetModNameByIndex((int)modType, index);
        }

        private string GetModNameByIndex(int modIndex, int valueIndex)
        {
            // Try to get the localized name from game
            string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, modIndex, valueIndex);
            if (!string.IsNullOrEmpty(label) && label != "NULL")
            {
                string name = Game.GetLocalizedString(label);
                if (!string.IsNullOrEmpty(name))
                    return name;
            }

            return $"{ModCategories.GetDisplayName(modIndex)} {valueIndex + 1}";
        }

        #endregion

        #region Mod Application

        private void ApplyModByIndex(int modIndex, int valueIndex)
        {
            if (currentVehicle == null) return;

            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, modIndex, valueIndex, false);
            if (activeDebugMode != DebugMode.None)
                ShowNotification($"~g~{ModCategories.GetDisplayName(modIndex)} installed!");
            MechanicSpeak();
            Log($"Applied mod index {modIndex} value {valueIndex}");

            // Refresh menu item statuses to show updated icons
            RefreshModMenuStatus(modIndex, valueIndex);
        }

        /// <summary>
        /// Refresh the ownership status icons for a mod menu after applying a mod
        /// </summary>
        private void RefreshModMenuStatus(int modIndex, int newInstalledValue)
        {
            if (!modMenusByIndex.TryGetValue(modIndex, out var menu)) return;

            string vehicleName = currentVehicle?.DisplayName ?? "";

            // Check if this mod type has no stock option (performance mods)
            bool noStockOption = false;
            if (ModCategories.AllCategories.TryGetValue(modIndex, out var category))
            {
                noStockOption = category.NoStockOption;
            }

            // Iterate through menu items and update their status
            // If has stock: Item 0 is "Stock" with value -1, rest are mods with values 0, 1, 2, etc.
            // If no stock: Item 0 is mod 0, Item 1 is mod 1, etc.
            for (int i = 0; i < menu.Items.Count; i++)
            {
                var item = menu.Items[i] as NativeItem;
                if (item == null) continue;

                int itemValue = noStockOption ? i : i - 1;

                bool isNowInstalled = (itemValue == newInstalledValue);
                bool isOwned = itemValue >= 0 && VehicleSaveData.IsModOwned(vehicleName, modIndex, itemValue);

                // Remove old status
                itemOwnershipStatus.Remove(item);

                if (isNowInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    // Show price for items that aren't owned
                    int price = ModPricing.GetModPriceByIndex(modIndex, itemValue);
                    item.AltTitle = itemValue == -1 ? "Free" : $"${price:N0}";
                }
            }
        }

        /// <summary>
        /// Refresh the horn menu ownership status after applying a horn
        /// </summary>
        private void RefreshHornMenuStatus(int newInstalledIndex)
        {
            if (hornMenu == null) return;
            string vehicleName = currentVehicle?.DisplayName ?? "";

            for (int i = 0; i < hornMenu.Items.Count; i++)
            {
                var item = hornMenu.Items[i] as NativeItem;
                if (item == null) continue;

                int hornIndex = i - 1; // Stock is at index 0 with value -1
                bool isNowInstalled = (hornIndex == newInstalledIndex);
                bool isOwned = hornIndex >= 0 && VehicleSaveData.IsHornOwned(vehicleName, hornIndex);

                itemOwnershipStatus.Remove(item);

                if (isNowInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = hornIndex == -1 ? "Free" : "$100";
                }
            }
        }

        /// <summary>
        /// Refresh the plate menu ownership status after applying a plate style
        /// </summary>
        private void RefreshPlateMenuStatus(int newInstalledIndex)
        {
            if (plateMenu == null) return;
            string vehicleName = currentVehicle?.DisplayName ?? "";

            for (int i = 0; i < plateMenu.Items.Count; i++)
            {
                var item = plateMenu.Items[i] as NativeItem;
                if (item == null) continue;

                bool isNowInstalled = (i == newInstalledIndex);
                bool isOwned = VehicleSaveData.IsPlateOwned(vehicleName, i);

                itemOwnershipStatus.Remove(item);

                if (isNowInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = "$200";
                }
            }
        }

        /// <summary>
        /// Refresh the window tint menu ownership status after applying a tint
        /// </summary>
        private void RefreshWindowTintMenuStatus(int newInstalledTintValue)
        {
            // Window tint now uses config-based menu, delegate to config refresh
            RefreshConfigMenuStatus("Windows", newInstalledTintValue);
        }

        /// <summary>
        /// Refresh the turbo menu ownership status after toggling turbo
        /// </summary>
        private void RefreshTurboMenuStatus(bool turboEnabled)
        {
            if (turboMenu == null || turboMenu.Items.Count < 2) return;
            string vehicleName = currentVehicle?.DisplayName ?? "";
            bool turboOwned = VehicleSaveData.IsTurboOwned(vehicleName);

            // Item 0 is "None", Item 1 is "Turbo Tuning"
            var noneItem = turboMenu.Items[0] as NativeItem;
            var turboItem = turboMenu.Items[1] as NativeItem;

            if (noneItem != null)
            {
                itemOwnershipStatus.Remove(noneItem);
                if (!turboEnabled)
                {
                    noneItem.AltTitle = "";
                    itemOwnershipStatus[noneItem] = STATUS_INSTALLED;
                }
                else
                {
                    noneItem.AltTitle = "Free";
                }
            }

            if (turboItem != null)
            {
                itemOwnershipStatus.Remove(turboItem);
                if (turboEnabled)
                {
                    turboItem.AltTitle = "";
                    itemOwnershipStatus[turboItem] = STATUS_INSTALLED;
                }
                else if (turboOwned)
                {
                    turboItem.AltTitle = "";
                    itemOwnershipStatus[turboItem] = STATUS_OWNED;
                }
                else
                {
                    turboItem.AltTitle = $"${ModPricing.TurboPrice}";
                }
            }
        }

        /// <summary>
        /// Refresh the headlights menu ownership status after toggling xenon
        /// </summary>
        private void RefreshHeadlightsMenuStatus(bool xenonEnabled)
        {
            if (headlightsMenu == null || headlightsMenu.Items.Count < 2) return;
            string vehicleName = currentVehicle?.DisplayName ?? "";
            bool xenonOwned = VehicleSaveData.IsXenonOwned(vehicleName);

            // Item 0 is "Stock Lights", Item 1 is "Xenon Lights"
            var stockItem = headlightsMenu.Items[0] as NativeItem;
            var xenonItem = headlightsMenu.Items[1] as NativeItem;

            if (stockItem != null)
            {
                itemOwnershipStatus.Remove(stockItem);
                if (!xenonEnabled)
                {
                    stockItem.AltTitle = "";
                    itemOwnershipStatus[stockItem] = STATUS_INSTALLED;
                }
                else
                {
                    stockItem.AltTitle = "Free";
                }
            }

            if (xenonItem != null)
            {
                itemOwnershipStatus.Remove(xenonItem);
                if (xenonEnabled)
                {
                    xenonItem.AltTitle = "";
                    itemOwnershipStatus[xenonItem] = STATUS_INSTALLED;
                }
                else if (xenonOwned)
                {
                    xenonItem.AltTitle = "";
                    itemOwnershipStatus[xenonItem] = STATUS_OWNED;
                }
                else
                {
                    xenonItem.AltTitle = $"${ModPricing.XenonLightsPrice}";
                }
            }
        }

        private void ApplyMod(VehicleModType modType, int index)
        {
            ApplyModByIndex((int)modType, index);
        }

        private void ApplyWheel(int wheelType, int wheelIndex)
        {
            if (currentVehicle == null) return;

            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, wheelIndex, false); // Front wheels
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, wheelIndex, false); // Back wheels

            ShowNotification("~g~Wheels installed!");
            MechanicSpeak();
            Log($"Applied wheel type {wheelType} index {wheelIndex}");
        }

        private void ApplyLivery(int index, bool useNativeLivery)
        {
            if (currentVehicle == null) return;

            if (useNativeLivery)
            {
                Function.Call(Hash.SET_VEHICLE_LIVERY, currentVehicle, index);
            }
            else
            {
                // Mod-based livery (mod index 48)
                // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
                Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 48, index, false);
            }
            ShowNotification("~g~Livery applied!");
            MechanicSpeak();
        }

        private void ApplyColor(int colorIndex, bool isPrimary)
        {
            if (currentVehicle == null) return;

            int primary, secondary;
            unsafe
            {
                Function.Call(Hash.GET_VEHICLE_COLOURS, currentVehicle, &primary, &secondary);
            }

            if (isPrimary)
                Function.Call(Hash.SET_VEHICLE_COLOURS, currentVehicle, colorIndex, secondary);
            else
                Function.Call(Hash.SET_VEHICLE_COLOURS, currentVehicle, primary, colorIndex);

            if (activeDebugMode != DebugMode.None)
                ShowNotification($"~g~{(isPrimary ? "Primary" : "Secondary")} color applied!");
            MechanicSpeak();
        }

        /// <summary>
        /// Apply paint color with proper paint type (Classic=0, Metallic=1, Pearl=2, Matte=3, Metal=4, Chrome=5)
        /// </summary>
        private void ApplyPaintColor(int colorIndex, int pearlescentSpec, int paintType, bool isPrimary)
        {
            if (currentVehicle == null) return;

            // Use direct color setting (like Menyoo does) instead of SET_VEHICLE_MOD_COLOR_1
            // The raw color index already encodes the paint type in carcols.meta
            if (isPrimary)
            {
                currentVehicle.Mods.PrimaryColor = (VehicleColor)colorIndex;
                if (pearlescentSpec > 0)
                    currentVehicle.Mods.PearlescentColor = (VehicleColor)pearlescentSpec;
            }
            else
            {
                currentVehicle.Mods.SecondaryColor = (VehicleColor)colorIndex;
            }

            string paintTypeName = paintType switch
            {
                0 => "Classic",
                1 => "Metallic",
                2 => "Pearl",
                3 => "Matte",
                4 => "Metal",
                5 => "Chrome",
                _ => ""
            };
            if (activeDebugMode != DebugMode.None)
                ShowNotification($"~g~{paintTypeName} {(isPrimary ? "primary" : "secondary")} color applied!");
            MechanicSpeak();
        }

        private void ApplyPearlescent(int colorIndex)
        {
            if (currentVehicle == null) return;

            int pearl, wheel;
            unsafe
            {
                Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &pearl, &wheel);
            }
            Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, colorIndex, wheel);

            ShowNotification("~g~Pearlescent applied!");
            MechanicSpeak();
        }

        private void ApplyWheelColor(int colorIndex)
        {
            if (currentVehicle == null) return;

            int pearl, wheel;
            unsafe
            {
                Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &pearl, &wheel);
            }
            Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, pearl, colorIndex);

            ShowNotification("~g~Wheel color applied!");
            MechanicSpeak();
        }

        private void ApplyTireSmoke(int r, int g, int b)
        {
            if (currentVehicle == null) return;

            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 20, true); // Enable tire smoke
            Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, currentVehicle, r, g, b);

            ShowNotification("~g~Tire smoke applied!");
            MechanicSpeak();
        }

        private void ApplyHeadlightColor(int colorIndex)
        {
            if (currentVehicle == null) return;

            Function.Call(Hash.SET_VEHICLE_XENON_LIGHT_COLOR_INDEX, currentVehicle, colorIndex);
            ShowNotification("~g~Headlight color applied!");
            MechanicSpeak();
        }

        private void ToggleNeon(int position)
        {
            if (currentVehicle == null) return;

            bool isOn = Function.Call<bool>((Hash)0x8C4B92553E4571EC, currentVehicle, position);
            Function.Call((Hash)0x2AA720E4287BF269, currentVehicle, position, !isOn);

            if (activeDebugMode != DebugMode.None)
                ShowNotification($"~g~Neon {(!isOn ? "enabled" : "disabled")}!");
            MechanicSpeak();
        }

        private void ApplyNeonColor(int r, int g, int b)
        {
            if (currentVehicle == null) return;

            Function.Call((Hash)0x8E0A582209A62695, currentVehicle, r, g, b);
            ShowNotification("~g~Neon color applied!");
            MechanicSpeak();
        }

        private void ApplyHorn(int index)
        {
            if (currentVehicle == null) return;

            currentVehicle.Mods[VehicleModType.Horns].Index = index;
            ShowNotification("~g~Horn installed!");
            MechanicSpeak();
            RefreshHornMenuStatus(index);
        }

        private void ApplyWindowTint(int index)
        {
            if (currentVehicle == null) return;

            currentVehicle.Mods.WindowTint = (VehicleWindowTint)index;
            ShowNotification("~g~Window tint applied!");
            MechanicSpeak();
            RefreshWindowTintMenuStatus(index);
        }

        private void ApplyPlateStyle(int index)
        {
            if (currentVehicle == null) return;

            currentVehicle.Mods.LicensePlateStyle = (LicensePlateStyle)index;
            ShowNotification("~g~License plate style applied!");
            MechanicSpeak();
            RefreshPlateMenuStatus(index);
        }

        private void ToggleTurbo()
        {
            if (currentVehicle == null) return;

            bool hasTurbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 18);
            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 18, !hasTurbo);

            ShowNotification(hasTurbo ? "~r~Turbo removed!" : "~g~Turbo installed!");
            MechanicSpeak();
        }

        private void SetTurbo(bool enabled)
        {
            if (currentVehicle == null) return;

            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 18, enabled);
            ShowNotification(enabled ? "~g~Turbo Tuning installed!" : "~g~Turbo removed!");
            MechanicSpeak();
            RefreshTurboMenuStatus(enabled);
        }

        private void ToggleXenon()
        {
            if (currentVehicle == null) return;

            bool hasXenon = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22);
            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 22, !hasXenon);

            ShowNotification(hasXenon ? "~r~Xenon removed!" : "~g~Xenon headlights installed!");
            MechanicSpeak();
        }

        private void SetXenon(bool enabled)
        {
            if (currentVehicle == null) return;

            Function.Call(Hash.TOGGLE_VEHICLE_MOD, currentVehicle, 22, enabled);
            ShowNotification(enabled ? "~g~Xenon Lights installed!" : "~g~Stock Lights installed!");
            MechanicSpeak();
            RefreshHeadlightsMenuStatus(enabled);
        }

        private bool GetNeonEnabled(int index)
        {
            if (currentVehicle == null) return false;
            try
            {
                // Use SHVDN's built-in neon check via Vehicle.Mods
                var neonLight = (VehicleNeonLight)index;
                return currentVehicle.Mods.IsNeonLightsOn(neonLight);
            }
            catch
            {
                return false;
            }
        }

        private void SetNeonEnabled(int index, bool enabled)
        {
            if (currentVehicle == null) return;
            try
            {
                var neonLight = (VehicleNeonLight)index;
                currentVehicle.Mods.SetNeonLightsOn(neonLight, enabled);
            }
            catch (Exception ex)
            {
                Log($"Error setting neon {index}: {ex.Message}");
            }
        }

        private void ApplyNeonLayout(bool front, bool back, bool left, bool right)
        {
            if (currentVehicle == null) return;

            // Neon indices: 0=left, 1=right, 2=front, 3=back
            SetNeonEnabled(0, left);
            SetNeonEnabled(1, right);
            SetNeonEnabled(2, front);
            SetNeonEnabled(3, back);

            ShowNotification("~g~Neon layout applied!");
            MechanicSpeak();
        }

        private void ToggleBulletproofTires()
        {
            if (currentVehicle == null) return;

            currentVehicle.CanTiresBurst = !currentVehicle.CanTiresBurst;
            ShowNotification(currentVehicle.CanTiresBurst ? "~r~Bulletproof tires removed!" : "~g~Bulletproof tires installed!");
            MechanicSpeak();
        }

        private void SetBulletproofTires(bool bulletproof)
        {
            if (currentVehicle == null) return;

            currentVehicle.CanTiresBurst = !bulletproof;
            ShowNotification(bulletproof ? "~g~Bulletproof Tires installed!" : "~g~Standard Tires installed!");
            MechanicSpeak();
        }

        private void RepairVehicle()
        {
            if (currentVehicle == null) return;

            currentVehicle.Repair();
            ShowNotification("~g~Vehicle repaired!");
            MechanicSpeak();
        }

        private void CleanVehicle()
        {
            if (currentVehicle == null) return;

            currentVehicle.Wash();
            ShowNotification("~g~Vehicle cleaned!");
            MechanicSpeak();
        }

        #endregion

        #region Menu Input Handling

        /// <summary>
        /// Custom menu input handling with native GTA-style acceleration.
        /// Initial delay before repeat, slow repeat for first few, then fast repeat.
        /// </summary>
        private void HandleMenuInput()
        {
            // Handle debug mode for timing adjustment (R3 to toggle, L3 to cycle settings)
            HandleInputTimingDebug();

            // Only handle input when a menu is visible
            NativeMenu visibleMenu = GetVisibleMenu();
            if (visibleMenu == null)
            {
                ResetInputState();
                return;
            }

            // In debug mode, left/right adjust timing instead of menu navigation
            if ((activeDebugMode == DebugMode.InputTiming))
            {
                return; // Debug mode handles its own input
            }

            int now = Game.GameTime;
            bool upHeld = Game.IsControlPressed(GTA.Control.FrontendUp) || Game.IsControlPressed(GTA.Control.PhoneUp);
            bool downHeld = Game.IsControlPressed(GTA.Control.FrontendDown) || Game.IsControlPressed(GTA.Control.PhoneDown);
            bool leftHeld = Game.IsControlPressed(GTA.Control.FrontendLeft) || Game.IsControlPressed(GTA.Control.PhoneLeft);
            bool rightHeld = Game.IsControlPressed(GTA.Control.FrontendRight) || Game.IsControlPressed(GTA.Control.PhoneRight);

            // Determine current held direction (using FrontendPause as "none" since there's no Control.None)
            GTA.Control currentDirection = GTA.Control.FrontendPause;
            if (upHeld) currentDirection = GTA.Control.FrontendUp;
            else if (downHeld) currentDirection = GTA.Control.FrontendDown;
            else if (leftHeld) currentDirection = GTA.Control.FrontendLeft;
            else if (rightHeld) currentDirection = GTA.Control.FrontendRight;

            // If direction changed or released, reset state
            if (currentDirection != inputHeldDirection)
            {
                inputHeldDirection = currentDirection;
                inputHeldSince = now;
                inputLastRepeat = 0;
                inputRepeatCount = 0;
                return; // First press is handled by LemonUI
            }

            // If no direction held, nothing to do
            if (currentDirection == GTA.Control.FrontendPause) return;

            // Check if we should trigger a repeat
            int holdDuration = now - inputHeldSince;

            // Haven't passed initial delay yet
            if (holdDuration < inputInitialDelay) return;

            // Determine repeat interval based on how many times we've repeated
            int repeatInterval = inputRepeatCount < inputAccelThreshold ? inputSlowRepeat : inputFastRepeat;

            // Check if enough time has passed since last repeat
            int timeSinceLastRepeat = now - inputLastRepeat;
            if (inputLastRepeat == 0)
            {
                // First repeat after initial delay
                timeSinceLastRepeat = holdDuration - inputInitialDelay;
            }

            if (timeSinceLastRepeat >= repeatInterval)
            {
                // Trigger repeat action by changing SelectedIndex
                int itemCount = visibleMenu.Items.Count;
                if (itemCount == 0) return;

                switch (currentDirection)
                {
                    case GTA.Control.FrontendUp:
                        visibleMenu.SelectedIndex = (visibleMenu.SelectedIndex - 1 + itemCount) % itemCount;
                        break;
                    case GTA.Control.FrontendDown:
                        visibleMenu.SelectedIndex = (visibleMenu.SelectedIndex + 1) % itemCount;
                        break;
                    case GTA.Control.FrontendLeft:
                    case GTA.Control.FrontendRight:
                        // Left/right handled by individual items (sliders, etc.)
                        // We don't need to handle these for basic navigation
                        break;
                }

                inputLastRepeat = now;
                inputRepeatCount++;
            }
        }

        /// <summary>
        /// Debug mode for adjusting input timing values in real-time.
        /// DISABLED - timing values have been tuned and saved.
        /// </summary>
        private void HandleInputTimingDebug()
        {
            // Input timing debug is now handled via the unified Debug Menu (F7)
        }

        private void ShowCurrentTimingSetting()
        {
            int value = GetCurrentTimingValue();
            string unit = inputTimingDebugSetting == 3 ? "\u200B" : "ms";
            ShowNotification($"~y~{inputTimingSettingNames[inputTimingDebugSetting]}: {value}{unit}");
        }

        private int GetCurrentTimingValue()
        {
            switch (inputTimingDebugSetting)
            {
                case 0: return inputInitialDelay;
                case 1: return inputSlowRepeat;
                case 2: return inputFastRepeat;
                case 3: return inputAccelThreshold;
                default: return 0;
            }
        }

        private void ShowTimingDebugOverlay()
        {
            // Draw debug info on screen
            string[] labels = { "Initial", "Slow", "Fast", "Accel" };
            int[] values = { inputInitialDelay, inputSlowRepeat, inputFastRepeat, inputAccelThreshold };

            string display = "~y~TIMING DEBUG~w~\n";
            for (int i = 0; i < 4; i++)
            {
                string marker = i == inputTimingDebugSetting ? "~g~> " : "  ";
                string unit = i == 3 ? "\u200B" : "ms";
                display += $"{marker}{labels[i]}: {values[i]}{unit}~w~\n";
            }
            display += "\n~c~L3=Cycle | Left/Right=Adjust | R3=Save~w~";

            GTA.UI.Screen.ShowSubtitle(display, 100);
        }

        private NativeMenu GetVisibleMenu()
        {
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu && menu.Visible)
                {
                    return menu;
                }
            }
            return null;
        }

        private void ResetInputState()
        {
            inputHeldDirection = GTA.Control.FrontendPause;
            inputHeldSince = 0;
            inputLastRepeat = 0;
            inputRepeatCount = 0;
        }

        #endregion

        #region Core Loop

        private void OnTick(object sender, EventArgs e)
        {
            // Handle description editor first (on-screen keyboard)
            UpdateDescriptionEditor();

            // Check for X to edit/cancel description (editor mode)
            CheckDescriptionEditInput();

            // Skip input processing while editing description (keyboard is open)
            // But still draw the menu and custom UI
            if (isEditingDescription)
            {
                Game.DisableAllControlsThisFrame();
                menuPool.Process();
                DrawCustomBanner(); // Keep custom UI visible
                return;
            }

            // Manual transmission key binding mode - block input and draw overlay
            if (mtBindingState > 0)
            {
                Game.DisableAllControlsThisFrame();
                menuPool.Process();
                DrawCustomBanner();
                DrawMTBindingOverlay();
                CheckControllerButtonBinding();
                return; // Skip normal input processing
            }

            // Reset B button block flag each frame
            debugBlockBButton = false;

            // R3 toggle for game input mode (allows opening real LSC menu for comparison)
            // This check must happen BEFORE any debug mode handling
            if (activeDebugMode != DebugMode.None)
            {
                // When in game input mode, block our controls FIRST before anything else processes them
                if (debugGameInputMode)
                {
                    // Block B button completely - set flag AND disable control
                    debugBlockBButton = true;
                    Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                    // B button is blocked in game input mode (ScriptRRight conflicts with B, so we only use FrontendRs for R3)

                    // Hide all our menus completely so they don't react to any input
                    foreach (var obj in menuPool)
                    {
                        if (obj is NativeMenu menu)
                        {
                            menu.Visible = false;
                            menu.AcceptsInput = false;
                        }
                    }

                    // Only R3 can exit game input mode (FrontendRs only - ScriptRRight conflicts with B button)
                    if (Game.IsControlJustPressed(GTA.Control.FrontendRs))
                    {
                        debugGameInputMode = false;
                        // Restore menu visibility and input
                        if (mainMenu != null)
                        {
                            mainMenu.Visible = true;
                            mainMenu.AcceptsInput = true;
                        }
                        ShowNotification("~g~DEBUG MODE~w~ - Unlocked via R3");
                        // Don't return - let the code continue to process the debug mode and draw UI
                        // But skip the enter check below by falling through
                    }
                    else
                    {
                        // Still in game input mode - draw indicator and return
                        DrawDebugText("GAME INPUT MODE", 0.5f, 0.02f, 255, 255, 0);
                        DrawDebugText("Press R3 to return to debug", 0.5f, 0.045f, 200, 200, 200);
                        return;
                    }
                }
                else
                {
                    // R3 (right stick click) enters game input mode (FrontendRs only - ScriptRRight conflicts with B button)
                    // Only check this if we weren't already in game input mode (to avoid re-entering immediately after exiting)
                    if (Game.IsControlJustPressed(GTA.Control.FrontendRs))
                    {
                        debugGameInputMode = true;
                        debugBlockBButton = true;  // Also block B this frame when entering
                        ShowNotification("~y~GAME INPUT MODE~w~ - Locked via R3");
                    }
                }
            }

            // Debug Menu - tool selector (F7 to open)
            if (activeDebugMode == DebugMode.Menu)
            {
                // Skip all processing if in game input mode (should have returned earlier, but safety check)
                if (debugGameInputMode) return;

                // Disable menu input
                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // Block vehicle controls
                Game.DisableControlThisFrame(GTA.Control.VehicleAccelerate);
                Game.DisableControlThisFrame(GTA.Control.VehicleBrake);
                Game.DisableControlThisFrame(GTA.Control.VehicleMoveLeftRight);

                // B exits debug menu (only if not in game input mode)
                if (!debugGameInputMode && !debugBlockBButton && Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    ExitDebugMode();
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // D-pad navigation
                if (Game.IsControlJustPressed(GTA.Control.FrontendUp))
                    debugMenuSelection = (debugMenuSelection - 1 + debugMenuOptions.Length) % debugMenuOptions.Length;
                if (Game.IsControlJustPressed(GTA.Control.FrontendDown))
                    debugMenuSelection = (debugMenuSelection + 1) % debugMenuOptions.Length;

                // A button selects
                if (Game.IsControlJustPressed(GTA.Control.FrontendAccept))
                {
                    switch (debugMenuSelection)
                    {
                        case 0: activeDebugMode = DebugMode.Resizer; break;
                        case 1: activeDebugMode = DebugMode.SpriteBrowser; spriteBrowserPage = 0; break;
                        case 2: activeDebugMode = DebugMode.MenuPosition; selectedUIElement = UIElement.Menu; break;
                        case 3: activeDebugMode = DebugMode.InputTiming; break;
                        case 4: activeDebugMode = DebugMode.StatsCalibration; break;
                        case 5: ExitDebugMode(); return;
                    }
                    ShowNotification($"~g~{debugMenuOptions[debugMenuSelection]}~w~ mode active");
                }

                // Draw menu
                menuPool.Process();
                DrawCustomBanner();

                // Draw debug menu overlay
                string menuText = "~y~DEBUG MENU~w~\n";
                for (int i = 0; i < debugMenuOptions.Length; i++)
                {
                    menuText += (i == debugMenuSelection ? "~g~> " : "  ") + debugMenuOptions[i] + (i == debugMenuSelection ? "~w~" : "") + "\n";
                }
                GTA.UI.Screen.ShowSubtitle(menuText.TrimEnd('\n'), 1);
                return;
            }

            // Resizer debug mode - scale text, sprites, and UI elements
            if (activeDebugMode == DebugMode.Resizer)
            {
                // Skip all processing if in game input mode (should have returned earlier, but safety check)
                if (debugGameInputMode) return;

                // Disable all input on menus and vehicle
                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);
                Game.DisableControlThisFrame(GTA.Control.VehicleAccelerate);
                Game.DisableControlThisFrame(GTA.Control.VehicleBrake);
                Game.DisableControlThisFrame(GTA.Control.VehicleMoveLeftRight);
                Game.DisableControlThisFrame(GTA.Control.VehicleMoveUpDown);
                Game.DisableControlThisFrame(GTA.Control.VehicleHandbrake);
                Game.DisableControlThisFrame(GTA.Control.VehicleExit);

                // Build list of available fields based on what's visible
                activeResizerFields.Clear();
                var visibleMenu = GetVisibleMenu();

                // Always available
                activeResizerFields.Add("Transaction Text");
                activeResizerFields.Add("Sprite Scale");
                activeResizerFields.Add("Arrow Sprite");

                // Description available if menu has description
                if (visibleMenu != null && visibleMenu.SelectedIndex >= 0 && visibleMenu.SelectedIndex < visibleMenu.Items.Count)
                {
                    activeResizerFields.Add("Description Text");
                }

                // Stats bar options always available when stats are shown
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    activeResizerFields.Add("Stats Row Height");
                    activeResizerFields.Add("Stats Bar Height");
                    activeResizerFields.Add("Stats Bar Gap");
                    activeResizerFields.Add("Stats Label Scale");
                    activeResizerFields.Add("Stats Padding");
                    activeResizerFields.Add("Stats Content Y");
                    activeResizerFields.Add("Stats Bar Inset");
                    activeResizerFields.Add("Stats Text Left");
                    activeResizerFields.Add("Stats Bar Y");
                }

                if (resizerFieldIndex >= activeResizerFields.Count)
                    resizerFieldIndex = 0;

                string currentFieldName = activeResizerFields.Count > 0 ? activeResizerFields[resizerFieldIndex] : "None";

                // B button goes back to debug menu (only if not in game input mode)
                if (!debugGameInputMode && !debugBlockBButton && Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    activeDebugMode = DebugMode.Menu;
                    return;
                }

                // U/D to cycle fields, L/R to adjust
                if (Game.IsControlJustPressed(GTA.Control.FrontendUp))
                    resizerFieldIndex = (resizerFieldIndex - 1 + activeResizerFields.Count) % activeResizerFields.Count;
                if (Game.IsControlJustPressed(GTA.Control.FrontendDown))
                    resizerFieldIndex = (resizerFieldIndex + 1) % activeResizerFields.Count;

                // L/R to adjust values
                float step = 0.001f;
                float bigStep = 0.01f;
                bool left = Game.IsControlJustPressed(GTA.Control.FrontendLeft);
                bool right = Game.IsControlJustPressed(GTA.Control.FrontendRight);

                if (left || right)
                {
                    float dir = right ? 1f : -1f;
                    switch (currentFieldName)
                    {
                        case "Transaction Text": transactionTextScale = Math.Max(0.1f, transactionTextScale + dir * bigStep); break;
                        case "Sprite Scale": spriteScale = Math.Max(0.5f, spriteScale + dir * 0.1f); break;
                        case "Arrow Sprite": arrowSpriteScale = Math.Max(0.1f, arrowSpriteScale + dir * 0.1f); break;
                        case "Description Text": descriptionTextScale = Math.Max(0.1f, descriptionTextScale + dir * bigStep); break;
                        case "Stats Row Height": statsRowHeight = Math.Max(0.01f, statsRowHeight + dir * step); break;
                        case "Stats Bar Height": statsSegmentHeight = Math.Max(0.005f, statsSegmentHeight + dir * step); break;
                        case "Stats Bar Gap": statsSegmentGap = Math.Max(0.001f, statsSegmentGap + dir * step); break;
                        case "Stats Label Scale": statsLabelScale = Math.Max(0.1f, statsLabelScale + dir * bigStep); break;
                        case "Stats Padding": statsPanelPadding = Math.Max(0.005f, statsPanelPadding + dir * step); break;
                        case "Stats Content Y": statsContentOffsetY += dir * step; break;
                        case "Stats Bar Inset": statsBarInset = Math.Max(0f, statsBarInset + dir * step); break;
                        case "Stats Text Left": statsTextLeftPadding = Math.Max(0f, statsTextLeftPadding + dir * step); break;
                        case "Stats Bar Y": statsBarOffsetY += dir * step; break;
                    }
                }

                // X to log current values
                if (Game.IsControlJustPressed(GTA.Control.FrontendX))
                {
                    LogResizerValues();
                    ShowNotification("~g~Resizer values logged");
                }

                // Draw everything
                menuPool.Process();
                DrawCustomBanner();

                // Draw yellow highlight around element being resized
                if (resizerFieldIndex < activeResizerFields.Count)
                {
                    string currentField = activeResizerFields[resizerFieldIndex];
                    if (currentField.StartsWith("Stats"))
                    {
                        DrawElementHighlight(UIElement.StatsPanel);
                    }
                    else if (currentField == "Description Text")
                    {
                        DrawElementHighlight(UIElement.Description);
                    }
                    else if (currentField == "Arrow Sprite")
                    {
                        DrawElementHighlight(UIElement.ScrollArrows);
                    }
                }

                // Draw debug panel on right side
                float rightX = 0.72f;
                float startY = 0.12f;
                float lineHeight = 0.022f;
                float panelWidth = 0.26f;
                float panelHeight = lineHeight * (activeResizerFields.Count + 2) + 0.02f;

                Function.Call(Hash.DRAW_RECT, rightX + panelWidth / 2f - 0.01f, startY + panelHeight / 2f - 0.01f, panelWidth, panelHeight, 0, 0, 0, 200);

                DrawDebugText("RESIZER", rightX, startY, 255, 255, 0);
                DrawDebugText("U/D=field, L/R=adjust, X=log, B=back", rightX, startY + lineHeight, 180, 180, 180);

                for (int i = 0; i < activeResizerFields.Count; i++)
                {
                    string field = activeResizerFields[i];
                    float value = GetResizerValue(field);
                    string prefix = (i == resizerFieldIndex) ? "> " : "  ";
                    string text = $"{prefix}{field}: {value:F3}";
                    // Selected item in bright yellow
                    int r = (i == resizerFieldIndex) ? 255 : 200;
                    int g = (i == resizerFieldIndex) ? 255 : 200;
                    int b = (i == resizerFieldIndex) ? 0 : 200;
                    DrawDebugText(text, rightX, startY + lineHeight * (2 + i), r, g, b);
                }

                return;
            }

            // Sprite Browser debug mode
            if (activeDebugMode == DebugMode.SpriteBrowser)
            {
                // Skip all processing if in game input mode (should have returned earlier, but safety check)
                if (debugGameInputMode) return;

                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // B goes back to debug menu (only if not in game input mode)
                if (!debugGameInputMode && !debugBlockBButton && Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    activeDebugMode = DebugMode.Menu;
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // D-pad navigation for pages
                if (Game.IsControlJustPressed(GTA.Control.FrontendUp))
                {
                    spriteBrowserPage--;
                    var sprites = SpriteDictionaries[spriteBrowserDictIndex].sprites;
                    if (spriteBrowserPage < 0)
                        spriteBrowserPage = (sprites.Length - 1) / 10;
                }
                if (Game.IsControlJustPressed(GTA.Control.FrontendDown))
                {
                    spriteBrowserPage++;
                    var sprites = SpriteDictionaries[spriteBrowserDictIndex].sprites;
                    int maxPage = (sprites.Length - 1) / 10;
                    if (spriteBrowserPage > maxPage) spriteBrowserPage = 0;
                }
                // L/R for dictionaries
                if (Game.IsControlJustPressed(GTA.Control.FrontendLeft))
                {
                    spriteBrowserDictIndex = (spriteBrowserDictIndex - 1 + SpriteDictionaries.Length) % SpriteDictionaries.Length;
                    spriteBrowserPage = 0;
                }
                if (Game.IsControlJustPressed(GTA.Control.FrontendRight))
                {
                    spriteBrowserDictIndex = (spriteBrowserDictIndex + 1) % SpriteDictionaries.Length;
                    spriteBrowserPage = 0;
                }

                menuPool.Process();
                DrawCustomBanner();
                DrawSpriteBrowser();

                GTA.UI.Screen.ShowSubtitle("~y~Sprite Browser~w~ - D-Pad: U/D=pages, L/R=dicts, B=back", 1);
                return;
            }

            // Menu Position debug mode
            if (activeDebugMode == DebugMode.MenuPosition)
            {
                // Skip all processing if in game input mode (should have returned earlier, but safety check)
                if (debugGameInputMode) return;

                // Disable all input
                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);
                Game.DisableControlThisFrame(GTA.Control.VehicleAccelerate);
                Game.DisableControlThisFrame(GTA.Control.VehicleBrake);
                Game.DisableControlThisFrame(GTA.Control.VehicleMoveLeftRight);
                Game.DisableControlThisFrame(GTA.Control.VehicleMoveUpDown);
                Game.DisableControlThisFrame(GTA.Control.VehicleHandbrake);
                Game.DisableControlThisFrame(GTA.Control.VehicleExit);

                // B goes back to debug menu (only if not in game input mode)
                if (!debugGameInputMode && !debugBlockBButton && Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    LogMenuPositionValues();
                    activeDebugMode = DebugMode.Menu;
                    return;
                }

                // LB/RB to cycle through UI elements
                if (Game.IsControlJustPressed(GTA.Control.FrontendLb))
                    selectedUIElement = (UIElement)(((int)selectedUIElement - 1 + uiElementNames.Length) % uiElementNames.Length);
                if (Game.IsControlJustPressed(GTA.Control.FrontendRb))
                    selectedUIElement = (UIElement)(((int)selectedUIElement + 1) % uiElementNames.Length);

                // Y to toggle position/scale mode
                if (Game.IsControlJustPressed(GTA.Control.FrontendY))
                {
                    menuPositionScaleMode = !menuPositionScaleMode;
                    ShowNotification(menuPositionScaleMode ? "~y~SCALE MODE" : "~g~POSITION MODE");
                }

                // Right stick Y-axis to adjust speed (up = faster, down = slower)
                // Control 2 = Right Stick Y in input group 0
                // Use GET_DISABLED_CONTROL_NORMAL to read even when controls are disabled
                float rsY = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, 2);

                if (Math.Abs(rsY) > 0.3f)
                {
                    // rsY is negative when pushing up, positive when pushing down
                    if (rsY < -0.3f) // Pushing up = faster
                        menuPositionSpeed = Math.Min(5f, menuPositionSpeed + 0.02f);
                    else if (rsY > 0.3f) // Pushing down = slower
                        menuPositionSpeed = Math.Max(0.1f, menuPositionSpeed - 0.02f);
                }

                // L3 to toggle uniform scaling (both axes together)
                if (Game.IsControlJustPressed(GTA.Control.ScriptRLeft) || Game.IsControlJustPressed(GTA.Control.FrontendLs))
                {
                    menuPositionUniformScale = !menuPositionUniformScale;
                    ShowNotification(menuPositionUniformScale ? "~g~UNIFORM SCALE (both axes)" : "~y~INDEPENDENT SCALE (per axis)");
                }

                // D-pad to move/scale selected element
                float baseStep = 0.001f * menuPositionSpeed;
                float scaleStep = 0.01f * menuPositionSpeed;
                float menuStep = 5f * menuPositionSpeed;  // Menu uses pixel-based offset
                bool up = Game.IsControlPressed(GTA.Control.FrontendUp);
                bool down = Game.IsControlPressed(GTA.Control.FrontendDown);
                bool left = Game.IsControlPressed(GTA.Control.FrontendLeft);
                bool right = Game.IsControlPressed(GTA.Control.FrontendRight);

                if (menuPositionScaleMode)
                {
                    // Scale mode: uniform = all directions scale both, independent = up/down=H, left/right=W
                    float scaleChange = 0f;
                    if (menuPositionUniformScale)
                    {
                        // Any direction scales both axes
                        if (up || left) scaleChange = -scaleStep;
                        if (down || right) scaleChange = scaleStep;
                    }

                    switch (selectedUIElement)
                    {
                        case UIElement.Menu:
                            // Menu doesn't have scale (it's LemonUI controlled)
                            break;
                        case UIElement.ScrollArrows:
                            if (menuPositionUniformScale)
                            {
                                scrollArrowsScaleW += scaleChange;
                                scrollArrowsScaleH += scaleChange;
                            }
                            else
                            {
                                if (up) scrollArrowsScaleH -= scaleStep;
                                if (down) scrollArrowsScaleH += scaleStep;
                                if (left) scrollArrowsScaleW -= scaleStep;
                                if (right) scrollArrowsScaleW += scaleStep;
                            }
                            break;
                        case UIElement.Description:
                            if (menuPositionUniformScale)
                            {
                                descriptionScaleW += scaleChange;
                                descriptionScaleH += scaleChange;
                            }
                            else
                            {
                                if (up) descriptionScaleH -= scaleStep;
                                if (down) descriptionScaleH += scaleStep;
                                if (left) descriptionScaleW -= scaleStep;
                                if (right) descriptionScaleW += scaleStep;
                            }
                            break;
                        case UIElement.StatsPanel:
                            if (menuPositionUniformScale)
                            {
                                statsPanelScaleW += scaleChange;
                                statsPanelScaleH += scaleChange;
                            }
                            else
                            {
                                if (up) statsPanelScaleH -= scaleStep;
                                if (down) statsPanelScaleH += scaleStep;
                                if (left) statsPanelScaleW -= scaleStep;
                                if (right) statsPanelScaleW += scaleStep;
                            }
                            break;
                    }
                }
                else
                {
                    // Position mode
                    switch (selectedUIElement)
                    {
                        case UIElement.Menu:
                            var offset = mainMenu.Offset;
                            if (up) offset.Y -= menuStep;
                            if (down) offset.Y += menuStep;
                            if (left) offset.X -= menuStep;
                            if (right) offset.X += menuStep;
                            mainMenu.Offset = offset;
                            break;
                        case UIElement.ScrollArrows:
                            if (up) scrollArrowsOffsetY -= baseStep;
                            if (down) scrollArrowsOffsetY += baseStep;
                            if (left) scrollArrowsOffsetX -= baseStep;
                            if (right) scrollArrowsOffsetX += baseStep;
                            break;
                        case UIElement.Description:
                            if (up) descriptionOffsetY -= baseStep;
                            if (down) descriptionOffsetY += baseStep;
                            if (left) descriptionOffsetX -= baseStep;
                            if (right) descriptionOffsetX += baseStep;
                            break;
                        case UIElement.StatsPanel:
                            if (up) statsPanelOffsetY -= baseStep;
                            if (down) statsPanelOffsetY += baseStep;
                            if (left) statsPanelOffsetX -= baseStep;
                            if (right) statsPanelOffsetX += baseStep;
                            break;
                    }
                }

                // X to log
                if (Game.IsControlJustPressed(GTA.Control.FrontendX))
                {
                    LogMenuPositionValues();
                    ShowNotification("~g~Position values logged");
                }

                menuPool.Process();
                DrawCustomBanner();

                // Draw yellow highlight around selected element
                DrawElementHighlight(selectedUIElement);

                // Draw debug panel
                float rightX = 0.72f;
                float startY = 0.12f;
                float lineHeight = 0.022f;

                Function.Call(Hash.DRAW_RECT, rightX + 0.12f, startY + 0.13f, 0.26f, 0.28f, 0, 0, 0, 200);

                string modeStr = menuPositionScaleMode ? "SCALE" : "POSITION";
                string uniformStr = menuPositionUniformScale ? "Uniform" : "Independent";
                DrawDebugText($"MENU {modeStr} (Y=toggle)", rightX, startY, 255, 255, 0);
                DrawDebugText($"Speed: {menuPositionSpeed:F2}x (RS up/down)", rightX, startY + lineHeight, 180, 180, 180);
                if (menuPositionScaleMode)
                    DrawDebugText($"Axis: {uniformStr} (L3=toggle)", rightX, startY + lineHeight * 2, 180, 180, 180);
                else
                    DrawDebugText("LB/RB=element, D-Pad=move", rightX, startY + lineHeight * 2, 150, 150, 150);
                DrawDebugText("X=log, B=back", rightX, startY + lineHeight * 3, 150, 150, 150);

                for (int i = 0; i < uiElementNames.Length; i++)
                {
                    string prefix = (i == (int)selectedUIElement) ? "> " : "  ";
                    string offsetStr = GetElementOffsetString((UIElement)i);
                    string scaleStr = GetElementScaleString((UIElement)i);
                    string text = $"{prefix}{uiElementNames[i]}: {offsetStr}";
                    if (i > 0) text += $" | {scaleStr}";  // Menu doesn't have scale
                    int r = (i == (int)selectedUIElement) ? 255 : 200;
                    int g = (i == (int)selectedUIElement) ? 255 : 200;
                    int b = (i == (int)selectedUIElement) ? 0 : 200;
                    DrawDebugText(text, rightX, startY + lineHeight * (4 + i), r, g, b);
                }

                return;
            }

            // Input Timing debug mode
            if (activeDebugMode == DebugMode.InputTiming)
            {
                // Skip all processing if in game input mode (should have returned earlier, but safety check)
                if (debugGameInputMode) return;

                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // B goes back to debug menu (only if not in game input mode)
                if (!debugGameInputMode && !debugBlockBButton && Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    activeDebugMode = DebugMode.Menu;
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // U/D to cycle settings
                if (Game.IsControlJustPressed(GTA.Control.FrontendUp))
                    inputTimingDebugSetting = (inputTimingDebugSetting - 1 + 4) % 4;
                if (Game.IsControlJustPressed(GTA.Control.FrontendDown))
                    inputTimingDebugSetting = (inputTimingDebugSetting + 1) % 4;

                // L/R to adjust values
                int step = 10;
                if (Game.IsControlJustPressed(GTA.Control.FrontendLeft))
                {
                    switch (inputTimingDebugSetting)
                    {
                        case 0: inputInitialDelay = Math.Max(10, inputInitialDelay - step); break;
                        case 1: inputSlowRepeat = Math.Max(10, inputSlowRepeat - step); break;
                        case 2: inputFastRepeat = Math.Max(10, inputFastRepeat - step); break;
                        case 3: inputAccelThreshold = Math.Max(1, inputAccelThreshold - 1); break;
                    }
                }
                if (Game.IsControlJustPressed(GTA.Control.FrontendRight))
                {
                    switch (inputTimingDebugSetting)
                    {
                        case 0: inputInitialDelay += step; break;
                        case 1: inputSlowRepeat += step; break;
                        case 2: inputFastRepeat += step; break;
                        case 3: inputAccelThreshold++; break;
                    }
                }

                menuPool.Process();
                DrawCustomBanner();

                int[] values = { inputInitialDelay, inputSlowRepeat, inputFastRepeat, inputAccelThreshold };
                string debugText = $"~y~Input Timing~w~ - U/D=setting, L/R=adjust\n";
                for (int i = 0; i < 4; i++)
                {
                    debugText += (i == inputTimingDebugSetting ? "~g~> " : "  ") + inputTimingSettingNames[i] + ": " + values[i] + (i == inputTimingDebugSetting ? "~w~" : "") + "\n";
                }
                GTA.UI.Screen.ShowSubtitle(debugText.TrimEnd('\n'), 1);
                return;
            }

            // Stats Calibration debug mode
            if (activeDebugMode == DebugMode.StatsCalibration)
            {
                // Skip all processing if in game input mode (should have returned earlier, but safety check)
                if (debugGameInputMode) return;

                foreach (var obj in menuPool)
                {
                    if (obj is NativeMenu menu)
                        menu.AcceptsInput = false;
                }

                // B goes back to debug menu (only if not in game input mode)
                if (!debugGameInputMode && !debugBlockBButton && Game.IsControlJustPressed(GTA.Control.FrontendCancel))
                {
                    // Log final values before exiting
                    LogStatsCalibrationValues();
                    activeDebugMode = DebugMode.Menu;
                    return;
                }
                Game.DisableControlThisFrame(GTA.Control.FrontendCancel);

                // U/D to cycle settings (8 total: 4 stats x 2 values each)
                if (Game.IsControlJustPressed(GTA.Control.FrontendUp))
                    statsCalibrationSetting = (statsCalibrationSetting - 1 + 8) % 8;
                if (Game.IsControlJustPressed(GTA.Control.FrontendDown))
                    statsCalibrationSetting = (statsCalibrationSetting + 1) % 8;

                // L/R to adjust values
                float divStep = 1f;
                float multStep = 0.01f;
                bool isDiv = (statsCalibrationSetting % 2 == 0);  // Even = divisor, Odd = multiplier

                if (Game.IsControlJustPressed(GTA.Control.FrontendLeft))
                {
                    switch (statsCalibrationSetting)
                    {
                        case 0: statsTopSpeedDiv = Math.Max(1f, statsTopSpeedDiv - divStep); break;
                        case 1: statsTopSpeedMult = Math.Max(0.1f, statsTopSpeedMult - multStep); break;
                        case 2: statsAccelDiv = Math.Max(0.01f, statsAccelDiv - 0.01f); break;
                        case 3: statsAccelMult = Math.Max(0.1f, statsAccelMult - multStep); break;
                        case 4: statsBrakingDiv = Math.Max(0.1f, statsBrakingDiv - 0.1f); break;
                        case 5: statsBrakingMult = Math.Max(0.1f, statsBrakingMult - multStep); break;
                        case 6: statsTractionDiv = Math.Max(0.1f, statsTractionDiv - 0.1f); break;
                        case 7: statsTractionMult = Math.Max(0.1f, statsTractionMult - multStep); break;
                    }
                }
                if (Game.IsControlJustPressed(GTA.Control.FrontendRight))
                {
                    switch (statsCalibrationSetting)
                    {
                        case 0: statsTopSpeedDiv += divStep; break;
                        case 1: statsTopSpeedMult = Math.Min(1f, statsTopSpeedMult + multStep); break;
                        case 2: statsAccelDiv += 0.01f; break;
                        case 3: statsAccelMult = Math.Min(1f, statsAccelMult + multStep); break;
                        case 4: statsBrakingDiv += 0.1f; break;
                        case 5: statsBrakingMult = Math.Min(1f, statsBrakingMult + multStep); break;
                        case 6: statsTractionDiv += 0.1f; break;
                        case 7: statsTractionMult = Math.Min(1f, statsTractionMult + multStep); break;
                    }
                }

                // X button to log current values
                if (Game.IsControlJustPressed(GTA.Control.FrontendX))
                {
                    LogStatsCalibrationValues();
                    ShowNotification("~g~Stats values logged to console");
                }

                menuPool.Process();
                DrawCustomBanner();

                // Get current raw values for display
                float rawTopSpeed = 0f, rawAccel = 0f, rawBraking = 0f, rawTraction = 0f;
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    rawTopSpeed = Function.Call<float>(Hash.GET_VEHICLE_ESTIMATED_MAX_SPEED, currentVehicle);
                    rawAccel = Function.Call<float>(Hash.GET_VEHICLE_ACCELERATION, currentVehicle);
                    rawBraking = Function.Call<float>(Hash.GET_VEHICLE_MAX_BRAKING, currentVehicle);
                    rawTraction = Function.Call<float>(Hash.GET_VEHICLE_MAX_TRACTION, currentVehicle);
                }

                // Draw debug panel on right side of screen using native text (avoids subtitle flashing)
                float rightX = 0.75f;  // Right side of screen
                float startY = 0.15f;
                float lineHeight = 0.025f;
                float panelWidth = 0.22f;
                float panelHeight = lineHeight * 12 + 0.02f;

                // Draw background panel
                Function.Call(Hash.DRAW_RECT, rightX + panelWidth / 2f - 0.01f, startY + panelHeight / 2f - 0.01f, panelWidth, panelHeight, 0, 0, 0, 180);

                // Draw title
                DrawDebugText("STATS CALIBRATION", rightX, startY, 255, 255, 0);
                DrawDebugText("U/D=setting, L/R=adjust, X=log, B=back", rightX, startY + lineHeight, 200, 200, 200);
                DrawDebugText($"Raw: Spd={rawTopSpeed:F1} Acc={rawAccel:F3} Brk={rawBraking:F2} Trc={rawTraction:F2}", rightX, startY + lineHeight * 2, 180, 180, 180);

                float[] values = { statsTopSpeedDiv, statsTopSpeedMult, statsAccelDiv, statsAccelMult, statsBrakingDiv, statsBrakingMult, statsTractionDiv, statsTractionMult };
                for (int i = 0; i < 8; i++)
                {
                    string format = (i % 2 == 0) ? "F1" : "F2";
                    if (i == 2 || i == 3) format = "F2";
                    string prefix = (i == statsCalibrationSetting) ? "> " : "  ";
                    string text = prefix + statsCalibrationNames[i] + ": " + values[i].ToString(format);
                    int r = (i == statsCalibrationSetting) ? 100 : 255;
                    int g = 255;
                    int b = (i == statsCalibrationSetting) ? 100 : 255;
                    DrawDebugText(text, rightX, startY + lineHeight * (3 + i), r, g, b);
                }
                return;
            }

            // Debug: Log controller input (only when not editing)
            DebugLogControllerInput();

            // Handle button mapping for manual transmission (runs even in menu)
            HandleButtonMapping();

            // Update manual transmission when driving (not in menu)
            if (!isMenuActive)
            {
                UpdateManualTransmission();
            }

            // Wheel fitment updates EVERY tick (in AND out of the menu) so height/camber edits re-settle
            // immediately while the player is adjusting them, instead of floating until they drive off.
            UpdateWheelFitment();

            // Auto-apply saved stances to specific cars within range (skips the player's current car).
            if (_stanceMgrInit && ModSettings.VStancerIntegration)
                stanceManager.Update();

            // Handle custom menu input with native GTA-style acceleration
            HandleMenuInput();

            menuPool.Process();

            // Draw custom banner overlay
            DrawCustomBanner();

            // Draw sprite browser if enabled (F6 to toggle)
            DrawSpriteBrowser();

            // White-screen UI preview harness (dev tool) — drawn last so it overlays everything.
            dragHUD.DrawPreviewIfRequested();

            // Keep handbrake on while menu is active (allows rev with just RT)
            // Keep handbrake on while menu is active (allows rev with just RT)
            if (isMenuActive && currentVehicle != null && currentVehicle.Exists() && !isWalkAroundActive)
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, currentVehicle, true);

                // Continuously apply lights that get reset by game each frame
                if (currentLightMode == 2) // High Beams
                {
                    Function.Call(Hash.SET_VEHICLE_LIGHTS, currentVehicle, 3);
                    Function.Call(Hash.SET_VEHICLE_FULLBEAM, currentVehicle, true);
                }
                else if (currentLightMode == 6) // Brake Lights
                {
                    Function.Call(Hash.SET_VEHICLE_BRAKE_LIGHTS, currentVehicle, true);
                }

                // Show RPM when revving (outside camera mode)
                if (ModSettings.ShowRPMWhileRevving)
                {
                    float rpm = currentVehicle.CurrentRPM;
                    if (rpm > 0.3f)
                    {
                        GTA.UI.Screen.ShowSubtitle($"RPM: {rpm:F2}", 1);
                    }
                }
            }

            // Check mechanic cleanup
            CheckMechanicCleanup();

            // Make mechanic face player
            UpdateMechanicBehavior();

            // Walk-around camera mode
            if (isWalkAroundActive)
            {
                UpdateWalkAround();
            }

            // Cooldown timer for camera mode
            if (cameraModeCooldown > 0)
                cameraModeCooldown--;

            // Controller input for walk-around (Y button) - only when menu is open and custom camera enabled
            if (ModSettings.CustomCamera && isMenuActive && !isWalkAroundActive && cameraModeCooldown == 0)
            {
                if (Game.IsControlJustPressed(GTA.Control.VehicleExit)) // Y button
                {
                    EnterWalkAround();
                }
            }

            // Controller input to open menu (RB + B)
            if (!isMenuActive && !isWalkAroundActive)
            {
                bool rbHeld = Game.IsControlPressed(GTA.Control.Cover);
                bool bPressed = Game.IsControlJustPressed(GTA.Control.FrontendCancel);

                if (rbHeld && bPressed)
                {
                    TryOpenMenu();
                }
            }

            // Block input while menu active (but allow movement in walk-around)
            if (isMenuActive)
            {
                // Show cash HUD every frame
                Function.Call((Hash)0x96DEC8D5430208B7, true); // DISPLAY_CASH

                // Draw transaction amount (-$XXX) if active
                if (transactionAmount > 0 && transactionStartTime > 0)
                {
                    int elapsed = Game.GameTime - transactionStartTime;
                    if (elapsed < TRANSACTION_DISPLAY_DURATION)
                    {
                        string text = $"-${transactionAmount:N0}";

                        Function.Call(Hash.SET_TEXT_FONT, 7);
                        Function.Call(Hash.SET_TEXT_SCALE, 0.0f, transactionTextScale);
                        Function.Call(Hash.SET_TEXT_COLOUR, 224, 64, 64, 255);
                        Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
                        Function.Call(Hash.SET_TEXT_WRAP, 0.0f, 0.985f);
                        Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
                        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
                        Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.985f, 0.06f);
                    }
                    else
                    {
                        transactionAmount = 0;
                        transactionStartTime = 0;
                    }
                }

                if (isWalkAroundActive)
                {
                    // In walk-around: allow movement + menu controls
                    Game.EnableControlThisFrame(GTA.Control.FrontendUp);
                    Game.EnableControlThisFrame(GTA.Control.FrontendDown);
                    Game.EnableControlThisFrame(GTA.Control.FrontendLeft);
                    Game.EnableControlThisFrame(GTA.Control.FrontendRight);
                    Game.EnableControlThisFrame(GTA.Control.FrontendAccept);
                    Game.EnableControlThisFrame(GTA.Control.FrontendCancel);
                    Game.EnableControlThisFrame(GTA.Control.PhoneCancel);
                    // Movement and look controls stay enabled by default
                }
                else
                {
                    // In vehicle: block all except menu controls
                    Game.DisableAllControlsThisFrame();
                    Game.EnableControlThisFrame(GTA.Control.FrontendUp);
                    Game.EnableControlThisFrame(GTA.Control.FrontendDown);
                    Game.EnableControlThisFrame(GTA.Control.FrontendLeft);
                    Game.EnableControlThisFrame(GTA.Control.FrontendRight);
                    Game.EnableControlThisFrame(GTA.Control.FrontendAccept);
                    Game.EnableControlThisFrame(GTA.Control.FrontendCancel);
                    Game.EnableControlThisFrame(GTA.Control.PhoneCancel);
                    Game.EnableControlThisFrame(GTA.Control.LookLeftRight);
                    Game.EnableControlThisFrame(GTA.Control.LookUpDown);
                    Game.EnableControlThisFrame(GTA.Control.VehicleExit); // Y for walk-around
                    Game.EnableControlThisFrame(GTA.Control.Aim); // LB for edit mode
                    Game.EnableControlThisFrame(GTA.Control.Sprint); // Shift for edit mode (keyboard)
                    Game.EnableControlThisFrame(GTA.Control.VehicleDuck); // X for edit description
                }
            }

            // LSC detection
            CheckLSCEntry();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Manual transmission key binding capture
            if (mtBindingState > 0)
            {
                // Cancel on Escape
                if (e.KeyCode == Keys.Escape)
                {
                    mtBindingState = 0;
                    mtBindingItem = null;
                    ShowNotification("~r~Manual Transmission setup cancelled");
                    return;
                }

                // Don't allow binding Enter/Return (used to activate menu items)
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return)
                {
                    return;
                }

                if (mtBindingState == 1)
                {
                    // Binding shift up
                    ModSettings.ShiftUpKey = e.KeyCode;
                    mtBindingState = 2;
                }
                else if (mtBindingState == 2)
                {
                    // Binding shift down - complete the setup
                    ModSettings.ShiftDownKey = e.KeyCode;
                    ModSettings.Save();
                    FinishMTBinding();
                }
                return;
            }

            if (e.KeyCode == menuKey && !isMenuActive)
            {
                TryOpenMenu();
            }

            // Manual transmission shifting (when not in menu)
            if (!isMenuActive)
            {
                HandleManualTransmissionKeyPress(e.KeyCode);
            }

            // Debug Menu (F7 to open/close) - unified debug tool selector
            // F7 resets values to defaults and exits, B just exits keeping current values
            if (e.KeyCode == Keys.F7 && isMenuActive)
            {
                if (activeDebugMode == DebugMode.None)
                {
                    // Open debug menu
                    activeDebugMode = DebugMode.Menu;
                    debugMenuSelection = 0;
                    ShowNotification("~y~Debug Menu~w~ - D-Pad: navigate, A: confirm, B: exit, F7: reset & exit");
                }
                else
                {
                    // Reset all debug values to defaults
                    transactionTextScale = 0.5f;
                    descriptionTextScale = 0.35f;
                    spriteScale = 1.8f;
                    inputInitialDelay = 82;
                    inputSlowRepeat = 255;
                    inputFastRepeat = 83;
                    inputAccelThreshold = 3;

                    ShowNotification("~y~Debug values reset to defaults");
                    ExitDebugMode();
                }
            }
        }

        #endregion

        #region Menu Control

        private void TryOpenMenu()
        {
            try
            {
                var player = Game.Player.Character;

                if (!player.IsInVehicle())
                {
                    ShowNotification("~r~You must be in a vehicle!");
                    return;
                }

                currentVehicle = player.CurrentVehicle;
                currentVehicle.Mods.InstallModKit();

                // Cache screen resolution for coordinate conversion
                UpdateCachedScreenResolution();

                // Rebuild menus for this vehicle
                RebuildMenusForVehicle();

                // Apply auto-offset for current resolution (unless in debug mode)
                ApplyAutoOffset();

                isMenuActive = true;
                mainMenu.Visible = true;

                // Force handbrake on so player can rev with just RT
                currentVehicle.IsEngineRunning = true;
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, currentVehicle, true);

                Log($"Menu opened for vehicle: {currentVehicle.DisplayName}");
            }
            catch (Exception ex)
            {
                Log($"ERROR in TryOpenMenu: {ex.Message}\n{ex.StackTrace}");
                ShowNotification("~r~Menu error - check log");
            }
        }

        private void CloseMenu()
        {
            // Don't actually close if we're just hiding for game input mode comparison
            if (debugGameInputMode) return;

            // Exit camera mode if active
            if (isWalkAroundActive)
            {
                ExitWalkAround();
            }

            // Release handbrake when menu closes
            if (currentVehicle != null && currentVehicle.Exists())
            {
                Function.Call(Hash.SET_VEHICLE_HANDBRAKE, currentVehicle, false);
            }

            // On exit, snapshot this specific car (plate + fingerprint + decorator) so its stance
            // auto-applies next time the player approaches it. RecordCar clears the record if it's stock.
            if (_stanceMgrInit && currentVehicle != null && currentVehicle.Exists() && wheelFitment.IsInitialized)
            {
                try { stanceManager.RecordCar(currentVehicle, wheelFitment, out _); } catch { }
            }

            // Save vehicle purchase data
            VehicleSaveData.Save();

            isMenuActive = false;
            currentVehicle = null;
            Log("Menu closed");
        }

        #endregion

        #region LSC Detection

        private void CheckLSCEntry()
        {
            var player = Game.Player.Character;
            int interior = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player);

            if (interior != lastInterior)
            {
                Log($"Interior changed: {lastInterior} -> {interior}");

                bool enteredLSC = Array.Exists(LSC_INTERIORS, id => id == interior);
                bool leftLSC = Array.Exists(LSC_INTERIORS, id => id == lastInterior);

                if (enteredLSC && player.IsInVehicle())
                {
                    Log("Entered Los Santos Customs - waiting for vehicle to stop");
                    isInLSC = true;
                    waitingForVehicleStop = true;
                }
                else if (leftLSC)
                {
                    Log("Left Los Santos Customs - closing menu");
                    isInLSC = false;

                    Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);

                    if (isMenuActive)
                    {
                        mainMenu.Visible = false;
                        CloseMenu();
                    }
                }

                lastInterior = interior;
            }

            // Detect service position
            if (waitingForVehicleStop && isInLSC)
            {
                var ped = Game.Player.Character;
                if (ped.IsInVehicle())
                {
                    var vehicle = ped.CurrentVehicle;
                    var pos = vehicle.Position;
                    float speed = vehicle.Speed;
                    float posDiff = lastVehiclePos != Vector3.Zero ? pos.DistanceTo(lastVehiclePos) : 0;

                    if (speed < 0.5f && posDiff > 0.3f && posDiff < 1.5f)
                    {
                        Log($"Service position teleport detected! Pos diff: {posDiff:F2}");
                        waitingForVehicleStop = false;
                        lastVehiclePos = Vector3.Zero;

                        DeleteAndRespawnMechanic(pos);
                        TryOpenMenu();

                        Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                        Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
                    }

                    lastVehiclePos = pos;
                }
            }

            if (isInLSC && isMenuActive)
            {
                Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
            }
        }

        #endregion

        #region Mechanic Management

        private void DeleteAndRespawnMechanic(Vector3 position)
        {
            if (!ENABLE_MECHANIC_REPLACEMENT) return;

            DeleteCustomMechanic();

            Ped[] nearbyPeds = World.GetNearbyPeds(position, 10f);

            foreach (var ped in nearbyPeds)
            {
                if (ped == Game.Player.Character) continue;

                Model model = ped.Model;
                Vector3 pos = ped.Position;
                float heading = ped.Heading;

                int[] drawables = new int[12];
                int[] textures = new int[12];
                for (int i = 0; i < 12; i++)
                {
                    drawables[i] = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, ped, i);
                    textures[i] = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, ped, i);
                }

                Log($"Deleting mechanic - Model: {model.Hash}, Pos: {pos}");
                ped.Delete();

                model.Request(1000);
                if (model.IsLoaded)
                {
                    Ped newMechanic = World.CreatePed(model, pos, heading);
                    if (newMechanic != null)
                    {
                        for (int i = 0; i < 12; i++)
                        {
                            Function.Call(Hash.SET_PED_COMPONENT_VARIATION, newMechanic, i, drawables[i], textures[i], 0);
                        }

                        newMechanic.BlockPermanentEvents = true;
                        customMechanic = newMechanic;
                        customMechanicPos = pos;
                        Log($"Respawned mechanic with original outfit at {pos}");
                    }
                    model.MarkAsNoLongerNeeded();
                }

                break;
            }
        }

        private void DeleteCustomMechanic()
        {
            if (customMechanic != null && customMechanic.Exists())
            {
                Log("Deleting custom mechanic");
                customMechanic.Delete();
            }
            customMechanic = null;
            customMechanicPos = Vector3.Zero;
        }

        private void CheckMechanicCleanup()
        {
            if (customMechanic == null || !customMechanic.Exists()) return;
            if (isInLSC) return;

            Ped[] nearbyPeds = World.GetNearbyPeds(customMechanicPos, 3f);
            foreach (var ped in nearbyPeds)
            {
                if (ped == Game.Player.Character) continue;
                if (ped == customMechanic) continue;

                float playerDistance = Game.Player.Character.Position.DistanceTo(customMechanicPos);
                Log($"New mechanic spawned at distance {playerDistance:F1}m - deleting custom mechanic");
                DeleteCustomMechanic();
                return;
            }
        }

        private void UpdateMechanicBehavior()
        {
            if (customMechanic == null || !customMechanic.Exists()) return;

            Vector3 playerPos = Game.Player.Character.Position;
            Vector3 mechPos = customMechanic.Position;
            Vector3 direction = playerPos - mechPos;
            float heading = (float)(Math.Atan2(direction.Y, direction.X) * (180.0 / Math.PI)) - 90f;
            customMechanic.Heading = heading;
        }

        private void MechanicSpeak()
        {
            if (customMechanic == null || !customMechanic.Exists()) return;

            string[] speeches = { "GENERIC_THANKS", "GENERIC_BYE", "CHAT_STATE", "CHAT_RESP" };
            string speech = speeches[new Random().Next(speeches.Length)];

            Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, customMechanic, speech, "SPEECH_PARAMS_FORCE_NORMAL");
        }

        #endregion

        #region Walk-Around Camera

        private void EnterWalkAround()
        {
            try
            {
                var player = Game.Player.Character;
                if (currentVehicle == null || !currentVehicle.Exists())
                {
                    Log("Cannot enter walk-around: no current vehicle");
                    return;
                }

                Log("Entering walk-around camera mode");
                isWalkAroundActive = true;
                walkAroundVehicle = currentVehicle;

                // Calculate camera distances based on vehicle size
                CalculateVehicleCameraDistances(walkAroundVehicle);

                // Build available presets based on vehicle's mod options
                BuildAvailablePresets();

                // Initialize camera orbit position (start at left side of vehicle)
                cameraOrbitPos = walkAroundVehicle.GetOffsetPosition(new Vector3(-vehicleCamStartDist, 0, 0));
                smoothCamPos = cameraOrbitPos;  // Initialize smoothed position to starting position
                smoothCamHeight = cameraHeight;

                // Create scripted camera
                Vector3 camStartPos = new Vector3(cameraOrbitPos.X, cameraOrbitPos.Y, walkAroundVehicle.Position.Z + cameraHeight);
                walkAroundCam = World.CreateCamera(camStartPos, Vector3.Zero, 50f);
                walkAroundCam.PointAt(walkAroundVehicle);
                World.RenderingCamera = walkAroundCam;

                // Reset look offset and preset mode
                lookOffsetH = 0f;
                currentPresetIndex = 0;
                isInPresetMode = false;

                ShowNotification("~b~Camera Mode~w~\nLB/RB - Cycle Parts | A - Open/Close\nLS/RS - Free Roam | Y - Exit");
                Log("Walk-around camera mode activated");
            }
            catch (Exception ex)
            {
                Log($"ERROR in EnterWalkAround: {ex.Message}");
                ExitWalkAround();
            }
        }

        private void ExitWalkAround()
        {
            try
            {
                Log("Exiting walk-around camera mode");

                // Restore normal camera
                World.RenderingCamera = null;

                // Destroy scripted camera
                if (walkAroundCam != null && walkAroundCam.Exists())
                {
                    walkAroundCam.Delete();
                }
                walkAroundCam = null;

                // Release handbrake and close doors
                if (walkAroundVehicle != null && walkAroundVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_HANDBRAKE, walkAroundVehicle, false);

                    for (int i = 0; i < 6; i++)
                    {
                        try
                        {
                            Function.Call(Hash.SET_VEHICLE_DOOR_SHUT, walkAroundVehicle, i, false);
                        }
                        catch { }
                    }
                    currentVehicle = walkAroundVehicle;
                }

                isWalkAroundActive = false;
                walkAroundVehicle = null;
                cameraOrbitPos = Vector3.Zero;
                cameraModeCooldown = 30; // Half second cooldown to prevent re-entry

                Log("Walk-around camera mode deactivated");
            }
            catch (Exception ex)
            {
                Log($"ERROR in ExitWalkAround: {ex.Message}");
                isWalkAroundActive = false;
                World.RenderingCamera = null;
            }
        }

        private void UpdateWalkAround()
        {
            if (walkAroundVehicle == null || !walkAroundVehicle.Exists())
            {
                ExitWalkAround();
                return;
            }

            if (walkAroundCam == null || !walkAroundCam.Exists())
            {
                ExitWalkAround();
                return;
            }

            // DISABLE vehicle controls (but NOT accelerate - allow revving)
            Game.DisableControlThisFrame(GTA.Control.VehicleExit);
            Game.DisableControlThisFrame(GTA.Control.VehicleMoveLeftRight);
            Game.DisableControlThisFrame(GTA.Control.VehicleMoveUpDown);
            // VehicleAccelerate NOT disabled - player can rev with RT (handbrake is on)
            Game.DisableControlThisFrame(GTA.Control.VehicleBrake);
            Game.DisableControlThisFrame(GTA.Control.VehicleHandbrake);
            Game.DisableControlThisFrame(GTA.Control.VehicleCinCam);
            Game.DisableControlThisFrame(GTA.Control.VehicleHorn);
            Game.DisableControlThisFrame(GTA.Control.VehicleLookBehind);

            // Y button to exit (read disabled control)
            bool yPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.VehicleExit);

            if (yPressed)
            {
                Log("Y pressed - exiting camera mode");
                ExitWalkAround();
                return;
            }

            // Walk around - movement relative to camera-to-car direction
            float moveSpeed = 0.04f; // Walking pace speed

            // Get direction from camera to car (this is our "forward")
            Vector3 toCar = walkAroundVehicle.Position - cameraOrbitPos;
            toCar.Z = 0;
            float dist = toCar.Length();
            if (dist > 0.01f)
            {
                toCar = toCar / dist; // Normalize
            }
            else
            {
                toCar = new Vector3(0, 1, 0);
            }

            // Right is perpendicular to forward (rotate 90 degrees)
            Vector3 right = new Vector3(toCar.Y, -toCar.X, 0);

            // LB/RB to cycle through preset camera positions
            bool lbPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Aim); // LB
            bool rbPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Cover); // RB

            if (lbPressed && availablePresets.Count > 0)
            {
                // Previous preset
                currentPresetIndex--;
                if (currentPresetIndex < 0) currentPresetIndex = availablePresets.Count - 1;
                GoToPreset(currentPresetIndex);

                // Safety check - GoToPreset might have triggered something that invalidated state
                if (walkAroundVehicle == null || !isWalkAroundActive) return;
            }
            else if (rbPressed && availablePresets.Count > 0)
            {
                // Next preset
                currentPresetIndex++;
                if (currentPresetIndex >= availablePresets.Count) currentPresetIndex = 0;
                GoToPreset(currentPresetIndex);

                // Safety check - GoToPreset might have triggered something that invalidated state
                if (walkAroundVehicle == null || !isWalkAroundActive) return;
            }

            // Left stick input (read disabled VEHICLE controls) - MOVEMENT
            float inputForward = -Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.VehicleMoveUpDown);
            float inputRight = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.VehicleMoveLeftRight);

            // Right stick input
            float lookInputH = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookLeftRight);
            float lookInputV = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookUpDown);

            // If any stick is moved, exit preset mode and return to free roam
            bool stickMoved = Math.Abs(inputForward) > 0.2f || Math.Abs(inputRight) > 0.2f ||
                              Math.Abs(lookInputH) > 0.2f || Math.Abs(lookInputV) > 0.2f;

            if (stickMoved && isInPresetMode)
            {
                isInPresetMode = false;
                // Camera will smoothly transition back due to lerp
            }

            // Only allow manual movement when not in preset mode (or transitioning out)
            if (!isInPresetMode || stickMoved)
            {
                if (Math.Abs(inputForward) > 0.1f || Math.Abs(inputRight) > 0.1f)
                {
                    Vector3 movement = (toCar * inputForward + right * inputRight) * moveSpeed;
                    cameraOrbitPos = cameraOrbitPos + movement;
                    isInPresetMode = false;
                }

                // Right stick left/right - horizontal look
                if (Math.Abs(lookInputH) > 0.1f)
                {
                    lookOffsetH -= lookInputH * 0.6f;
                    lookOffsetH = Math.Max(-45f, Math.Min(45f, lookOffsetH));
                }

                // Right stick up/down - adjust camera height
                if (Math.Abs(lookInputV) > 0.1f)
                {
                    cameraHeight -= lookInputV * 0.05f;
                    cameraHeight = Math.Max(0.3f, Math.Min(4f, cameraHeight));
                }
            }

            // Clamp to box path around vehicle (tighter on sides, more room front/back)
            // Get camera position in vehicle-local coordinates
            Vector3 localPos = walkAroundVehicle.GetPositionOffset(cameraOrbitPos);

            float clampedX = localPos.X;  // Side (+ = right of vehicle)
            float clampedY = localPos.Y;  // Front/back (+ = front of vehicle)

            // Clamp sides (width) - tighter
            if (Math.Abs(clampedX) > vehicleHalfWidth)
            {
                clampedX = Math.Sign(clampedX) * vehicleHalfWidth;
            }
            // Clamp front/back (length) - more room
            if (Math.Abs(clampedY) > vehicleHalfLength)
            {
                clampedY = Math.Sign(clampedY) * vehicleHalfLength;
            }

            // Minimum distance from center (don't get too close)
            float localDist = (float)Math.Sqrt(clampedX * clampedX + clampedY * clampedY);
            if (localDist < vehicleCamMinDist && localDist > 0.01f)
            {
                float scale = vehicleCamMinDist / localDist;
                clampedX *= scale;
                clampedY *= scale;
            }

            // Update orbit position with clamped values
            cameraOrbitPos = walkAroundVehicle.GetOffsetPosition(new Vector3(clampedX, clampedY, 0));

            // Smooth the camera position using lerp
            smoothCamPos = Vector3.Lerp(smoothCamPos, cameraOrbitPos, CAM_SMOOTH_SPEED);
            smoothCamHeight = smoothCamHeight + (cameraHeight - smoothCamHeight) * CAM_SMOOTH_SPEED;

            // Calculate target camera position
            Vector3 camPos = new Vector3(smoothCamPos.X, smoothCamPos.Y, walkAroundVehicle.Position.Z + smoothCamHeight);

            // Simple collision check - ensure camera stays outside vehicle bounding box
            Vector3 localCamPos = walkAroundVehicle.GetPositionOffset(camPos);

            // Get minimum distance from vehicle center (half width/length with small margin)
            float minDistX = (vehicleHalfWidth - 1.5f) + 0.3f;  // Remove the margin we added, add small buffer
            float minDistY = (vehicleHalfLength - 2.0f) + 0.3f;

            // If camera is inside the vehicle bounds, push it out RADIALLY (not to nearest edge)
            if (Math.Abs(localCamPos.X) < minDistX && Math.Abs(localCamPos.Y) < minDistY)
            {
                // Calculate current angle from center
                float angle = (float)Math.Atan2(localCamPos.Y, localCamPos.X);

                // Find the distance to the edge of the bounding box at this angle
                // For a rectangle, the distance varies with angle
                float cosAngle = (float)Math.Abs(Math.Cos(angle));
                float sinAngle = (float)Math.Abs(Math.Sin(angle));

                // Distance to edge at this angle (rectangular boundary)
                float edgeDistX = cosAngle > 0.001f ? minDistX / cosAngle : float.MaxValue;
                float edgeDistY = sinAngle > 0.001f ? minDistY / sinAngle : float.MaxValue;
                float edgeDist = Math.Min(edgeDistX, edgeDistY);

                // Push camera out to the edge along the same angle
                localCamPos.X = (float)Math.Cos(angle) * edgeDist;
                localCamPos.Y = (float)Math.Sin(angle) * edgeDist;

                // Convert back to world position
                Vector3 pushedPos = walkAroundVehicle.GetOffsetPosition(localCamPos);
                camPos.X = pushedPos.X;
                camPos.Y = pushedPos.Y;
            }

            walkAroundCam.Position = camPos;

            // Calculate look target - automatically tilt based on camera height
            // Base target is vehicle center at a comfortable viewing height
            float vehicleCenterHeight = 0.6f; // Approximate center of most vehicles
            Vector3 vehicleCenter = walkAroundVehicle.Position;
            vehicleCenter.Z += vehicleCenterHeight;

            // Base direction to car center
            Vector3 baseLookDir = vehicleCenter - camPos;

            // Apply horizontal offset (rotate around vertical axis)
            float offsetRadH = lookOffsetH * (float)Math.PI / 180f;
            Vector3 lookDir = new Vector3(
                baseLookDir.X * (float)Math.Cos(offsetRadH) - baseLookDir.Y * (float)Math.Sin(offsetRadH),
                baseLookDir.X * (float)Math.Sin(offsetRadH) + baseLookDir.Y * (float)Math.Cos(offsetRadH),
                baseLookDir.Z // Vertical tilt is automatic based on height difference
            );

            Vector3 lookTarget = camPos + lookDir;
            walkAroundCam.PointAt(lookTarget);

            // Show help text based on mode
            if (isInPresetMode && currentPresetIndex >= 0 && currentPresetIndex < availablePresets.Count)
            {
                string presetName = availablePresets[currentPresetIndex].Name;
                int presetNum = currentPresetIndex + 1;
                int totalPresets = availablePresets.Count;
                GTA.UI.Screen.ShowHelpTextThisFrame($"~b~{presetName}~w~ ({presetNum}/{totalPresets})\nLB/RB - Cycle | Stick - Free Roam");
            }
            else
            {
                GTA.UI.Screen.ShowHelpTextThisFrame($"~w~Free Roam~w~\nLB/RB - Part Views ({availablePresets.Count}) | Y - Exit");
            }

            // Display RPM when revving
            float rpm = walkAroundVehicle.CurrentRPM;
            if (rpm > 0.3f)
            {
                GTA.UI.Screen.ShowSubtitle($"RPM: {rpm:F2}", 1);
            }

        }

        private int GetClosestDoorIndex(Vector3 localCamPos)
        {
            // localCamPos: X = side (negative = left, positive = right)
            //              Y = front/back (negative = back, positive = front)

            bool isLeft = localCamPos.X < 0;
            bool isFront = localCamPos.Y > 0;

            // Determine if we're at the very front (hood) or very back (trunk)
            float sideThreshold = 0.3f; // How centered we need to be for hood/trunk
            bool isCentered = Math.Abs(localCamPos.X) < Math.Abs(localCamPos.Y) * sideThreshold + 0.5f;

            // At the front and centered = hood
            if (isFront && isCentered && localCamPos.Y > 1.0f)
            {
                return 4; // Hood
            }

            // At the back and centered = trunk
            if (!isFront && isCentered && localCamPos.Y < -1.0f)
            {
                return 5; // Trunk
            }

            // Otherwise determine door based on quadrant
            if (isLeft && isFront)
                return 0; // Front Left Door
            if (!isLeft && isFront)
                return 1; // Front Right Door
            if (isLeft && !isFront)
                return 2; // Rear Left Door

            return 3; // Rear Right Door
        }

        private void ToggleDoor(int doorIndex, string doorName)
        {
            if (walkAroundVehicle == null) return;

            try
            {
                var door = walkAroundVehicle.Doors[(VehicleDoorIndex)doorIndex];
                if (door != null)
                {
                    if (door.IsOpen)
                    {
                        Function.Call(Hash.SET_VEHICLE_DOOR_SHUT, walkAroundVehicle, doorIndex, false);
                        ShowNotification($"~w~Closed {doorName}");
                    }
                    else
                    {
                        Function.Call(Hash.SET_VEHICLE_DOOR_OPEN, walkAroundVehicle, doorIndex, false, false);
                        ShowNotification($"~w~Opened {doorName}");
                    }
                }
            }
            catch { }
        }

        private Vector3 GetPresetCameraPosition(int presetIndex)
        {
            if (walkAroundVehicle == null || presetIndex < 0 || presetIndex >= availablePresets.Count)
                return Vector3.Zero;

            // Get the preset's relative offset (normalized -1 to 1 range)
            Vector3 relOffset = availablePresets[presetIndex].LocalCameraOffset;

            // Scale by vehicle dimensions
            float x = relOffset.X * vehicleHalfWidth;
            float y = relOffset.Y * vehicleHalfLength;
            float z = relOffset.Z; // Z is absolute offset for height variation

            return new Vector3(x, y, z);
        }

        private void GoToPreset(int presetIndex)
        {
            if (walkAroundVehicle == null || availablePresets.Count == 0) return;

            currentPresetIndex = presetIndex;
            isInPresetMode = true;

            // Get target position in world space
            Vector3 localPos = GetPresetCameraPosition(presetIndex);
            cameraOrbitPos = walkAroundVehicle.GetOffsetPosition(localPos);

            // Reset look offset to face the vehicle
            lookOffsetH = 0f;

            // Navigate menu to highlight the corresponding mod category
            NavigateMenuToModType(availablePresets[presetIndex].ModType);
        }

        private void NavigateMenuToModType(VehicleModType modType)
        {
            try
            {
                if (mainMenu == null || mainMenu.Items.Count == 0) return;

                // Get the display name for this mod type from ModCategories
                int modIndex = (int)modType;
                string targetName = ModCategories.GetDisplayName(modIndex);

                // Find the item with this display name in the flat alphabetical menu
                for (int i = 0; i < mainMenu.Items.Count; i++)
                {
                    if (mainMenu.Items[i].Title == targetName)
                    {
                        mainMenu.SelectedIndex = i;
                        Log($"Navigated to menu item: {targetName} (index {i})");
                        return;
                    }
                }

                Log($"Could not find menu item for: {targetName}");
            }
            catch (Exception ex)
            {
                Log($"Error navigating menu: {ex.Message}");
            }
        }

        private void BuildAvailablePresets()
        {
            availablePresets.Clear();

            if (walkAroundVehicle == null) return;

            // Check which mod types are available for this vehicle
            foreach (var preset in AllModPresets)
            {
                int modCount = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, walkAroundVehicle, (int)preset.ModType);
                if (modCount > 0)
                {
                    availablePresets.Add(preset);
                    Log($"Added preset: {preset.Name} ({modCount} mods available)");
                }
            }

            // Always add a general "Overview" preset if we have any mods
            if (availablePresets.Count > 0)
            {
                Log($"Built {availablePresets.Count} camera presets for vehicle");
            }
            else
            {
                // Fallback - add basic presets even if no mods detected
                availablePresets.Add(new ModCameraPreset("Front", VehicleModType.FrontBumper, new Vector3(0, 1f, 0)));
                availablePresets.Add(new ModCameraPreset("Side", VehicleModType.SideSkirt, new Vector3(-1f, 0, 0)));
                availablePresets.Add(new ModCameraPreset("Rear", VehicleModType.RearBumper, new Vector3(0, -1f, 0)));
                Log("No mods detected, using fallback presets");
            }
        }

        private void CalculateVehicleCameraDistances(Vehicle vehicle)
        {
            try
            {
                // GET_MODEL_DIMENSIONS returns min and max corners of bounding box
                OutputArgument min = new OutputArgument();
                OutputArgument max = new OutputArgument();
                Function.Call(Hash.GET_MODEL_DIMENSIONS, vehicle.Model.Hash, min, max);
                Vector3 minVec = min.GetResult<Vector3>();
                Vector3 maxVec = max.GetResult<Vector3>();

                // Calculate vehicle size (length and width)
                float length = maxVec.Y - minVec.Y;
                float width = maxVec.X - minVec.X;
                float height = maxVec.Z - minVec.Z;

                // Store half dimensions for box path clamping (with margin for camera)
                float sideMargin = 1.5f;   // How far from side of car
                float frontBackMargin = 2.0f; // How far from front/back of car
                vehicleHalfWidth = (width / 2f) + sideMargin;
                vehicleHalfLength = (length / 2f) + frontBackMargin;

                // Min distance is for when camera gets too close to center
                vehicleCamMinDist = Math.Max(1.2f, Math.Min(vehicleHalfWidth, vehicleHalfLength) * 0.8f);
                vehicleCamMaxDist = Math.Max(vehicleHalfLength, vehicleHalfWidth); // Not really used with box path
                vehicleCamStartDist = vehicleHalfWidth; // Start at side of vehicle
                cameraHeight = Math.Max(1.2f, height * 0.6f + 0.5f); // Half vehicle height + offset

                Log($"Vehicle dimensions: {length:F1}x{width:F1}x{height:F1} - Box path: halfW={vehicleHalfWidth:F1}, halfL={vehicleHalfLength:F1}, height={cameraHeight:F1}");
            }
            catch (Exception ex)
            {
                // Fall back to defaults if something goes wrong
                vehicleCamMinDist = 1.5f;
                vehicleCamMaxDist = 4f;
                vehicleCamStartDist = 3f;
                vehicleHalfWidth = 2f;
                vehicleHalfLength = 3f;
                cameraHeight = 1.5f;
                Log($"ERROR calculating vehicle camera distances: {ex.Message}");
            }
        }

        #endregion

        #region Utility

        private void ShowNotification(string message)
        {
            GTA.UI.Notification.Show(message);
        }

        /// <summary>
        /// Draw debug text at a screen position (used for debug overlays that need to avoid subtitle conflicts)
        /// </summary>
        private void DrawDebugText(string text, float x, float y, int r = 255, int g = 255, int b = 255)
        {
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.3f);
            Function.Call(Hash.SET_TEXT_COLOUR, r, g, b, 255);
            Function.Call(Hash.SET_TEXT_DROPSHADOW, 1, 0, 0, 0, 255);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y);
        }

        /// <summary>
        /// Draw a 50% transparent yellow overlay on a UI element
        /// </summary>
        private void DrawElementHighlight(UIElement element)
        {
            var visibleMenu = GetVisibleMenu();
            if (visibleMenu == null) return;

            float screenW = GTA.UI.Screen.Width;
            float screenH = GTA.UI.Screen.Height;
            float aspectRatio = screenW / screenH;
            float lemonXBase = 1080f * aspectRatio;
            float lemonYBase = 1080f;

            var bannerPos = visibleMenu.Banner.Position;
            var bannerSize = visibleMenu.Banner.Size;

            float menuLeftX = bannerPos.X / lemonXBase;
            float menuTopY = bannerPos.Y / lemonYBase;
            float menuWidth = bannerSize.Width / lemonXBase;
            float bannerHeight = bannerSize.Height / lemonYBase;

            int totalItems = visibleMenu.Items.Count;
            int maxVisible = visibleMenu.MaxItems;
            bool hasScrollIndicator = totalItems > maxVisible;

            float subtitleHeight = 38f / lemonYBase;
            float itemHeight = 38f / lemonYBase;
            float visibleItemsHeight = itemHeight * Math.Min(totalItems, maxVisible);
            float scrollIndicatorHeight = hasScrollIndicator ? 0.035f : 0f;

            // 50% transparent yellow (128 alpha out of 255)
            int yellow_r = 255, yellow_g = 255, yellow_b = 0, yellow_a = 128;

            switch (element)
            {
                case UIElement.Menu:
                    // Highlight entire menu with yellow overlay
                    float menuTotalHeight = bannerHeight + subtitleHeight + visibleItemsHeight + (hasScrollIndicator ? scrollIndicatorHeight : 0f) + 0.05f;
                    DrawHighlightOverlay(menuLeftX, menuTopY, menuWidth, menuTotalHeight, yellow_r, yellow_g, yellow_b, yellow_a);
                    break;

                case UIElement.ScrollArrows:
                    if (hasScrollIndicator)
                    {
                        float arrowY = menuTopY + bannerHeight + subtitleHeight + visibleItemsHeight + scrollArrowsOffsetY;
                        float arrowX = menuLeftX + scrollArrowsOffsetX;
                        float arrowW = menuWidth * scrollArrowsScaleW;
                        float arrowH = scrollIndicatorHeight * scrollArrowsScaleH;
                        DrawHighlightOverlay(arrowX, arrowY, arrowW, arrowH, yellow_r, yellow_g, yellow_b, yellow_a);
                    }
                    break;

                case UIElement.Description:
                    float descY = menuTopY + bannerHeight + subtitleHeight + visibleItemsHeight + descriptionOffsetY;
                    if (hasScrollIndicator) descY += scrollIndicatorHeight + 0.01f;
                    float descX = menuLeftX + descriptionOffsetX;
                    float descW = menuWidth * descriptionScaleW;
                    float descH = GetDescriptionHeight(visibleMenu, menuWidth, menuLeftX);
                    if (descH == 0f) descH = 0.045f * descriptionScaleH;
                    DrawHighlightOverlay(descX, descY, descW, descH, yellow_r, yellow_g, yellow_b, yellow_a);
                    break;

                case UIElement.StatsPanel:
                    float statsDescY = menuTopY + bannerHeight + subtitleHeight + visibleItemsHeight;
                    if (hasScrollIndicator) statsDescY += scrollIndicatorHeight + 0.01f;
                    float dynDescH = GetDescriptionHeight(visibleMenu, menuWidth, menuLeftX);
                    if (dynDescH == 0f) dynDescH = 0.045f * descriptionScaleH;
                    float statsY = statsDescY + dynDescH + descriptionOffsetY + 0.005f + statsPanelOffsetY;
                    float statsX = menuLeftX + statsPanelOffsetX;
                    float statsPanelW = menuWidth * statsPanelScaleW;
                    float statsPanelH = (statsRowHeight * 4 + statsPanelPadding) * statsPanelScaleH;
                    DrawHighlightOverlay(statsX, statsY, statsPanelW, statsPanelH, yellow_r, yellow_g, yellow_b, yellow_a);
                    break;
            }
        }

        /// <summary>
        /// Draw a filled semi-transparent rectangle overlay
        /// </summary>
        private void DrawHighlightOverlay(float x, float y, float width, float height, int r, int g, int b, int a)
        {
            float centerX = x + width / 2f;
            float centerY = y + height / 2f;
            Function.Call(Hash.DRAW_RECT, centerX, centerY, width, height, r, g, b, a);
        }

        /// <summary>
        /// Get current value for a resizer field
        /// </summary>
        private float GetResizerValue(string fieldName)
        {
            switch (fieldName)
            {
                case "Transaction Text": return transactionTextScale;
                case "Sprite Scale": return spriteScale;
                case "Arrow Sprite": return arrowSpriteScale;
                case "Description Text": return descriptionTextScale;
                case "Stats Row Height": return statsRowHeight;
                case "Stats Bar Height": return statsSegmentHeight;
                case "Stats Bar Gap": return statsSegmentGap;
                case "Stats Label Scale": return statsLabelScale;
                case "Stats Padding": return statsPanelPadding;
                case "Stats Content Y": return statsContentOffsetY;
                case "Stats Bar Inset": return statsBarInset;
                case "Stats Text Left": return statsTextLeftPadding;
                case "Stats Bar Y": return statsBarOffsetY;
                default: return 0f;
            }
        }

        /// <summary>
        /// Log all resizer values to debug log
        /// </summary>
        private void LogResizerValues()
        {
            string log = "\n========== RESIZER VALUES ==========\n";
            log += $"transactionTextScale = {transactionTextScale:F3}f;\n";
            log += $"descriptionTextScale = {descriptionTextScale:F3}f;\n";
            log += $"spriteScale = {spriteScale:F2}f;\n";
            log += $"arrowSpriteScale = {arrowSpriteScale:F2}f;\n";
            log += $"statsRowHeight = {statsRowHeight:F4}f;\n";
            log += $"statsSegmentHeight = {statsSegmentHeight:F4}f;\n";
            log += $"statsSegmentGap = {statsSegmentGap:F4}f;\n";
            log += $"statsLabelScale = {statsLabelScale:F3}f;\n";
            log += $"statsPanelPadding = {statsPanelPadding:F4}f;\n";
            log += $"statsContentOffsetY = {statsContentOffsetY:F4}f;\n";
            log += $"statsBarInset = {statsBarInset:F4}f;\n";
            log += $"statsTextLeftPadding = {statsTextLeftPadding:F4}f;\n";
            log += $"statsBarOffsetY = {statsBarOffsetY:F4}f;\n";
            log += "=====================================\n";

            System.IO.File.AppendAllText("scripts/ExtendedLSC_debug.log", log);
        }

        /// <summary>
        /// Get offset string for a UI element
        /// </summary>
        private string GetElementOffsetString(UIElement element)
        {
            switch (element)
            {
                case UIElement.Menu: return $"({mainMenu.Offset.X:F0}, {mainMenu.Offset.Y:F0})";
                case UIElement.ScrollArrows: return $"({scrollArrowsOffsetX:F3}, {scrollArrowsOffsetY:F3})";
                case UIElement.Description: return $"({descriptionOffsetX:F3}, {descriptionOffsetY:F3})";
                case UIElement.StatsPanel: return $"({statsPanelOffsetX:F3}, {statsPanelOffsetY:F3})";
                default: return "(0, 0)";
            }
        }

        private string GetElementScaleString(UIElement element)
        {
            switch (element)
            {
                case UIElement.Menu: return "N/A";
                case UIElement.ScrollArrows: return $"W:{scrollArrowsScaleW:F2} H:{scrollArrowsScaleH:F2}";
                case UIElement.Description: return $"W:{descriptionScaleW:F2} H:{descriptionScaleH:F2}";
                case UIElement.StatsPanel: return $"W:{statsPanelScaleW:F2} H:{statsPanelScaleH:F2}";
                default: return "N/A";
            }
        }

        /// <summary>
        /// Log all menu position values to debug log
        /// </summary>
        private void LogMenuPositionValues()
        {
            string log = "\n========== MENU POSITION VALUES ==========\n";
            log += $"// Menu (LemonUI pixel offset)\n";
            log += $"mainMenu.Offset = new PointF({mainMenu.Offset.X:F0}f, {mainMenu.Offset.Y:F0}f);\n\n";
            log += $"// Scroll Arrows (normalized screen coords)\n";
            log += $"scrollArrowsOffsetX = {scrollArrowsOffsetX:F4}f;\n";
            log += $"scrollArrowsOffsetY = {scrollArrowsOffsetY:F4}f;\n";
            log += $"scrollArrowsScaleW = {scrollArrowsScaleW:F2}f;\n";
            log += $"scrollArrowsScaleH = {scrollArrowsScaleH:F2}f;\n\n";
            log += $"// Description (normalized screen coords)\n";
            log += $"descriptionOffsetX = {descriptionOffsetX:F4}f;\n";
            log += $"descriptionOffsetY = {descriptionOffsetY:F4}f;\n";
            log += $"descriptionScaleW = {descriptionScaleW:F2}f;\n";
            log += $"descriptionScaleH = {descriptionScaleH:F2}f;\n\n";
            log += $"// Stats Panel (normalized screen coords)\n";
            log += $"statsPanelOffsetX = {statsPanelOffsetX:F4}f;\n";
            log += $"statsPanelOffsetY = {statsPanelOffsetY:F4}f;\n";
            log += $"statsPanelScaleW = {statsPanelScaleW:F2}f;\n";
            log += $"statsPanelScaleH = {statsPanelScaleH:F2}f;\n";
            log += "===========================================\n";

            System.IO.File.AppendAllText("scripts/ExtendedLSC_debug.log", log);
        }

        /// <summary>
        /// Log stats calibration values for copy/paste into code
        /// </summary>
        private void LogStatsCalibrationValues()
        {
            float rawTopSpeed = 0f, rawAccel = 0f, rawBraking = 0f, rawTraction = 0f;
            if (currentVehicle != null && currentVehicle.Exists())
            {
                rawTopSpeed = Function.Call<float>(Hash.GET_VEHICLE_ESTIMATED_MAX_SPEED, currentVehicle);
                rawAccel = Function.Call<float>(Hash.GET_VEHICLE_ACCELERATION, currentVehicle);
                rawBraking = Function.Call<float>(Hash.GET_VEHICLE_MAX_BRAKING, currentVehicle);
                rawTraction = Function.Call<float>(Hash.GET_VEHICLE_MAX_TRACTION, currentVehicle);
            }

            string log = "\n========== STATS CALIBRATION VALUES ==========\n";
            log += $"Vehicle: {currentVehicle?.DisplayName ?? "None"}\n";
            log += $"Raw Values: TopSpeed={rawTopSpeed:F2}, Accel={rawAccel:F4}, Braking={rawBraking:F3}, Traction={rawTraction:F3}\n\n";
            log += "// Copy these values to DrawVehicleStats:\n";
            log += $"float topSpeedPct = (float)Math.Sqrt(Math.Min(1f, topSpeed / {statsTopSpeedDiv:F1}f)) * {statsTopSpeedMult:F2}f;\n";
            log += $"float accelPct = (float)Math.Sqrt(Math.Min(1f, acceleration / {statsAccelDiv:F2}f)) * {statsAccelMult:F2}f;\n";
            log += $"float brakingPct = (float)Math.Sqrt(Math.Min(1f, braking / {statsBrakingDiv:F1}f)) * {statsBrakingMult:F2}f;\n";
            log += $"float tractionPct = (float)Math.Sqrt(Math.Min(1f, traction / {statsTractionDiv:F1}f)) * {statsTractionMult:F2}f;\n";
            log += "===============================================\n";

            System.IO.File.AppendAllText("scripts/ExtendedLSC_debug.log", log);
            GTA.UI.Notification.Show("~g~Stats calibration logged to ExtendedLSC_debug.log");
        }

        /// <summary>
        /// Get the player's current cash
        /// </summary>
        private int GetPlayerCash()
        {
            return Game.Player.Money;
        }

        /// <summary>
        /// Check if player can afford the price
        /// </summary>
        private bool CanAfford(int price)
        {
            return Game.Player.Money >= price;
        }

        /// <summary>
        /// Subtract cash from player. Returns true if successful.
        /// Sets transaction display to show -$XXX on screen.
        /// </summary>
        private bool SubtractCash(int amount)
        {
            if (amount <= 0) return true; // Free items always succeed
            if (Game.Player.Money < amount) return false;

            Game.Player.Money -= amount;

            // Set transaction display
            transactionAmount = amount;
            transactionStartTime = Game.GameTime;

            return true;
        }

        /// <summary>
        /// Try to purchase - checks if owned or can afford, subtracts cash if needed
        /// Returns true if purchase successful (owned or paid)
        /// </summary>
        private bool TryPurchase(int price, bool alreadyOwned)
        {
            if (alreadyOwned || price <= 0) return true;

            if (!CanAfford(price))
            {
                // Show "not enough cash" in description area for 5 seconds
                showNotEnoughCash = true;
                notEnoughCashStartTime = Game.GameTime;
                return false;
            }

            SubtractCash(price);
            return true;
        }

        private void Log(string message)
        {
            if (!ModSettings.DebugLogging) return;

            try
            {
                // NOTE: do NOT use Assembly.Location here — SHVDN loads script DLLs from bytes (so the
                // file can be hot-swapped), which makes Location an empty string and the write throw.
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC.log");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                System.IO.File.AppendAllText(logPath, $"[{timestamp}] {message}\n");
            }
            catch { }
        }

        #endregion

        #region Description Editor

        /// <summary>
        /// Start editing the description for a category using the on-screen keyboard
        /// </summary>
        private void StartEditDescription(string categoryName)
        {
            if (isEditingDescription) return;

            string currentDesc = MenuConfig.GetDescription(categoryName);
            editingCategoryName = categoryName;
            isEditingDescription = true;

            // Show on-screen keyboard
            // Parameters: type, windowTitle, defaultText, defaultText2, defaultText3, defaultText4, defaultText5, maxLength
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD,
                0,                          // KEYBOARD_TYPE_DEFAULT
                "FMMC_KEY_TIP8",            // Window title (generic "Enter Text")
                "",                         // Empty placeholder
                currentDesc,                // Default text (current description)
                "",                         // Empty
                "",                         // Empty
                "",                         // Empty
                256);                       // Max length

            Log($"[DescEditor] Started editing description for '{categoryName}'");
            ShowNotification($"~y~Editing: {categoryName}~n~Press A to save, B to cancel");
        }

        /// <summary>
        /// Check if the on-screen keyboard has finished and process the result
        /// </summary>
        private void UpdateDescriptionEditor()
        {
            if (!isEditingDescription) return;

            // Cooldown to prevent rapid checks
            if (keyboardCheckCooldown > 0)
            {
                keyboardCheckCooldown--;
                return;
            }
            keyboardCheckCooldown = 5; // Check every 5 frames

            // Check keyboard status
            // Returns: -1 = not active, 0 = typing, 1 = finished/accepted, 2 = cancelled
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);

            // Log status for debugging
            if (status != 0)
            {
                Log($"[DescEditor] Keyboard status: {status}");
            }

            if (status == 1) // Finished - user pressed Enter/Accept
            {
                string result = Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT);

                if (!string.IsNullOrEmpty(result))
                {
                    MenuConfig.SetDescription(editingCategoryName, result);
                    ShowNotification($"~g~Description saved for: {editingCategoryName}");
                    Log($"[DescEditor] Saved description for '{editingCategoryName}': {result}");

                    // Rebuild menus to show updated description
                    if (currentVehicle != null)
                    {
                        RebuildMenusForVehicle();
                        mainMenu.Visible = true;
                    }
                }
                else
                {
                    ShowNotification($"~r~Description not saved (empty text)");
                }

                isEditingDescription = false;
                editingCategoryName = null;
            }
            else if (status == 2) // Cancelled
            {
                ShowNotification($"~y~Description edit cancelled");
                isEditingDescription = false;
                editingCategoryName = null;
            }
        }

        /// <summary>
        /// Check for X input to edit category description (hold X for 500ms)
        /// </summary>
        private void CheckDescriptionEditInput()
        {
            // X button = FrontendX or Duck (depends on context)
            bool xHeld = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendX) ||
                         Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Duck);

            if (!ModSettings.EditorModeEnabled) return;

            // Don't process X input while keyboard is open (A=save, B=cancel handled by GTA)
            if (isEditingDescription) return;

            // Find the currently visible menu
            var visibleMenu = GetVisibleMenu();
            if (visibleMenu == null || visibleMenu.Items.Count == 0) return;

            if (xHeld)
            {
                if (lastLbHeldTime == DateTime.MinValue)
                {
                    lastLbHeldTime = DateTime.Now;
                }
                else
                {
                    double elapsed = (DateTime.Now - lastLbHeldTime).TotalMilliseconds;
                    if (elapsed >= 500)
                    {
                        Log($"[DescEditor] X held for {elapsed:F0}ms - triggering edit!");
                        int selectedIndex = visibleMenu.SelectedIndex;
                        if (selectedIndex >= 0 && selectedIndex < visibleMenu.Items.Count)
                        {
                            string categoryName = visibleMenu.Items[selectedIndex].Title;
                            lastLbHeldTime = DateTime.MinValue;
                            StartEditDescription(categoryName);
                        }
                    }
                }
            }
            else
            {
                lastLbHeldTime = DateTime.MinValue;
            }
        }

        /// <summary>
        /// Check if edit mode key is held (LB/L1 on controller, Left Shift on keyboard)
        /// </summary>
        private bool IsEditModeKeyHeld()
        {
            // LB on controller (FrontendLb/ScriptLB) or Left Shift (Sprint) for keyboard
            return Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLb) ||
                   Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptLB) ||
                   Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Sprint);
        }

        /// <summary>
        /// Debug: Log controller button inputs with timing
        /// </summary>
        private void DebugLogControllerInput()
        {
            debugTickCounter++;

            // Controller debug logging is disabled
            return;

            // Kept for reference - only log when menu is active
            if (!isMenuActive) return;

            // Check ALL frontend/script controls for complete button mapping
            bool frontLB = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLb);
            bool frontRB = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendRb);
            bool frontLT = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLt);
            bool frontRT = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendRt);
            bool frontLS = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLs);
            bool frontRS = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendRs);
            bool frontAccept = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendAccept);
            bool frontCancel = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendCancel);
            bool frontX = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendX);
            bool frontY = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendY);
            bool scriptLB = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptLB);
            bool scriptRB = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptRB);
            bool scriptLT = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptLT);
            bool scriptRT = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.ScriptRT);
            bool duck = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Duck);
            bool jump = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Jump);
            bool context = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Context);
            bool detonate = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Detonate);
            bool vehicleExit = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.VehicleExit);

            // Log any button press with clear labels
            if (frontLB || frontRB || frontLT || frontRT || frontLS || frontRS || frontAccept || frontCancel ||
                frontX || frontY || scriptLB || scriptRB || scriptLT || scriptRT || duck || jump || context || detonate || vehicleExit)
            {
                Log($"[INPUT] FrontLB={frontLB} FrontRB={frontRB} FrontLT={frontLT} FrontRT={frontRT} FrontLS={frontLS} FrontRS={frontRS} Accept={frontAccept} Cancel={frontCancel} FrontX={frontX} FrontY={frontY} ScriptLB={scriptLB} ScriptRB={scriptRB} ScriptLT={scriptLT} ScriptRT={scriptRT} Duck={duck} Jump={jump} Context={context} Detonate={detonate} VehExit={vehicleExit}");
            }

            // Legacy tracking variables
            bool lbPressed = frontLB || scriptLB;
            bool xPressed = duck || frontX;
            bool aPressed = frontAccept || jump;
            bool bPressed = frontCancel;
            bool yPressed = vehicleExit || frontY;
            bool reloadPressed = false;
            bool coverPressed = false;
            bool contextPressed = context;
            bool sprintPressed = false;

            // LB button
            if (lbPressed && !wasLbPressed)
            {
                lbPressStart = DateTime.Now;
                Log($"[INPUT] LB PRESSED");
            }
            else if (!lbPressed && wasLbPressed)
            {
                var duration = DateTime.Now - lbPressStart;
                Log($"[INPUT] LB RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasLbPressed = lbPressed;

            // X button (VehicleDuck)
            if (xPressed && !wasXPressed)
            {
                xPressStart = DateTime.Now;
                Log($"[INPUT] X (VehicleDuck) PRESSED | LB={lbPressed} Sprint={sprintPressed}");
            }
            else if (!xPressed && wasXPressed)
            {
                var duration = DateTime.Now - xPressStart;
                Log($"[INPUT] X (VehicleDuck) RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasXPressed = xPressed;

            // A button
            if (aPressed && !wasAPressed)
            {
                aPressStart = DateTime.Now;
                Log($"[INPUT] A PRESSED");
            }
            else if (!aPressed && wasAPressed)
            {
                var duration = DateTime.Now - aPressStart;
                Log($"[INPUT] A RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasAPressed = aPressed;

            // B button
            if (bPressed && !wasBPressed)
            {
                bPressStart = DateTime.Now;
                Log($"[INPUT] B PRESSED");
            }
            else if (!bPressed && wasBPressed)
            {
                var duration = DateTime.Now - bPressStart;
                Log($"[INPUT] B RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasBPressed = bPressed;

            // Y button
            if (yPressed && !wasYPressed)
            {
                yPressStart = DateTime.Now;
                Log($"[INPUT] Y PRESSED");
            }
            else if (!yPressed && wasYPressed)
            {
                var duration = DateTime.Now - yPressStart;
                Log($"[INPUT] Y RELEASED (held {duration.TotalMilliseconds:F0}ms)");
            }
            wasYPressed = yPressed;

            // Log alternative controls if pressed (one-time per press cycle)
            if (reloadPressed && Game.IsControlJustPressed(GTA.Control.Reload))
                Log($"[INPUT] Reload control PRESSED");
            if (coverPressed && Game.IsControlJustPressed(GTA.Control.Cover))
                Log($"[INPUT] Cover (RB) control PRESSED");
            if (contextPressed && Game.IsControlJustPressed(GTA.Control.Context))
                Log($"[INPUT] Context control PRESSED");
        }

        #endregion

        #region Manual Transmission Methods

        // =========================================================================
        // ELSC Built-in Manual Transmission
        // Our own implementation using direct memory access - no external dependencies
        // =========================================================================

        /// <summary>
        /// Check if manual transmission is available (ELSC built-in)
        /// </summary>
        private bool HasManualTransmission(Vehicle vehicle) => ELSCTransmission.IsAvailable;

        /// <summary>
        /// Enable manual transmission for current vehicle
        /// </summary>
        private bool PurchaseManualTransmission(Vehicle vehicle)
        {
            if (!ELSCTransmission.IsAvailable) return false;
            elscTransmission.SetVehicle(vehicle);
            elscTransmission.Enable();
            return true;
        }

        /// <summary>
        /// Update manual transmission state each tick
        /// </summary>
        private void UpdateManualTransmission()
        {
            Ped player = Game.Player.Character;
            if (player == null || !player.IsInVehicle())
            {
                if (elscTransmission.IsEnabled)
                {
                    elscTransmission.Disable();
                    lastTransmissionVehicle = null;
                }
                return;
            }

            Vehicle vehicle = player.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists()) return;

            // Check if vehicle changed
            if (vehicle != lastTransmissionVehicle)
            {
                lastTransmissionVehicle = vehicle;

                // Check if this vehicle has MT purchased - auto-enable if so
                if (VehicleSaveData.IsManualTransmissionOwned(vehicle.DisplayName))
                {
                    elscTransmission.SetVehicle(vehicle);
                    elscTransmission.Enable();
                }
                else if (elscTransmission.IsEnabled)
                {
                    // Vehicle doesn't have MT - disable
                    elscTransmission.Disable();
                }
            }

            // Update transmission state
            if (elscTransmission.IsEnabled)
            {
                elscTransmission.Update();

                // Draw HUD — NFS-style drag gauge (replaces the old corner readout)
                if (ModSettings.DragHudEnabled)
                {
                    float rpm = elscTransmission.GetCurrentRPM();
                    int gearDisp = elscTransmission.InReverse ? -1 : (elscTransmission.InNeutral ? 0 : elscTransmission.CurrentGear);
                    dragHUD.Draw(rpm, gearDisp, vehicle.Speed * 2.23694f,
                        elscTransmission.NosLevel, elscTransmission.NosInstalled, elscTransmission.IsInRedline());
                }
                else
                {
                    transmissionHUD?.Draw(vehicle);
                }
            }

            // NOTE: wheel fitment is updated separately in UpdateWheelFitment(), called from OnTick
            // UNCONDITIONALLY (in AND out of the LSC menu) — its per-frame re-apply and the physics
            // re-settle must run while the menu is open, which is exactly when the player is adjusting
            // height/camber. (This method only runs when !isMenuActive, so it can't host the fitment update.)
        }

        /// <summary>
        /// Per-frame wheel fitment update. Runs EVERY tick — including while the LSC menu is open — so
        /// camber/track/height stay applied and the physics re-settle (anti-float) fires immediately
        /// after a change instead of only once the player drives off.
        /// </summary>
        private void UpdateWheelFitment()
        {
            if (!(ModSettings.VStancerIntegration && _fitmentInitAllowed)) return;

            Vehicle vehicle = Game.Player.Character?.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists()) return;

            try
            {
                if (!wheelFitment.IsInitialized || lastFitmentVehicle != vehicle)
                {
                    // Only attempt initialization after the game is fully loaded
                    if (Game.GameTime > 10000)
                    {
                        wheelFitment.Initialize(vehicle);
                        lastFitmentVehicle = vehicle;

                        // Prefer this SPECIFIC car's saved stance (per-car identity). Fall back to the
                        // legacy per-model saved fitment only if this exact car has no stance recorded.
                        bool loaded = _stanceMgrInit && stanceManager.LoadInto(vehicle, wheelFitment);
                        if (!loaded)
                        {
                            string vehName = vehicle.DisplayName;
                            if (VehicleSaveData.IsWheelFitmentOwned(vehName))
                            {
                                var saved = VehicleSaveData.GetFitmentData(vehName);
                                wheelFitment.FrontCamber = saved.FrontCamber;
                                wheelFitment.RearCamber = saved.RearCamber;
                                wheelFitment.FrontTrackWidth = saved.FrontTrackWidth;
                                wheelFitment.RearTrackWidth = saved.RearTrackWidth;
                                wheelFitment.FrontHeight = saved.FrontHeight;
                                wheelFitment.RearHeight = saved.RearHeight;
                            }
                        }
                    }
                }

                if (wheelFitment.IsInitialized)
                {
                    wheelFitment.Update();
                }
            }
            catch (Exception ex)
            {
                Log($"[WheelFitment] OnTick error: {ex.Message}");
                _fitmentInitAllowed = false;
                Log("[WheelFitment] Disabled due to errors");
            }
        }

        /// <summary>
        /// Handle keyboard input for manual transmission
        /// </summary>
        private void HandleManualTransmissionKeyPress(Keys key)
        {
            if (!elscTransmission.IsEnabled) return;

            // Use configurable key bindings from ModSettings
            if (key == ModSettings.ShiftUpKey)
            {
                elscTransmission.ShiftUp();
            }
            else if (key == ModSettings.ShiftDownKey)
            {
                elscTransmission.ShiftDown();
            }
            else if (key == ModSettings.NeutralKey)
            {
                elscTransmission.ToggleNeutral();
            }
        }

        /// <summary>
        /// Check for controller button presses during MT key binding setup
        /// </summary>
        private void CheckControllerButtonBinding()
        {
            // Common controller buttons to check
            var buttonsToCheck = new (GTA.Control control, string name)[]
            {
                (GTA.Control.FrontendAccept, "A"),
                (GTA.Control.FrontendCancel, "B"),
                (GTA.Control.FrontendX, "X"),
                (GTA.Control.FrontendY, "Y"),
                (GTA.Control.FrontendLb, "LB"),
                (GTA.Control.FrontendRb, "RB"),
                (GTA.Control.FrontendLt, "LT"),
                (GTA.Control.FrontendRt, "RT"),
                (GTA.Control.FrontendUp, "D-Up"),
                (GTA.Control.FrontendDown, "D-Down"),
                (GTA.Control.FrontendLeft, "D-Left"),
                (GTA.Control.FrontendRight, "D-Right"),
                (GTA.Control.FrontendLs, "L3"),
                (GTA.Control.FrontendRs, "R3"),
            };

            // Check for B button to cancel
            if (Game.IsControlJustPressed(GTA.Control.FrontendCancel))
            {
                mtBindingState = 0;
                mtBindingItem = null;
                ShowNotification("~r~Manual Transmission setup cancelled");
                return;
            }

            foreach (var (control, name) in buttonsToCheck)
            {
                // Skip B button - it's used for cancel
                if (control == GTA.Control.FrontendCancel) continue;

                if (Game.IsControlJustPressed(control))
                {
                    int controlIndex = (int)control;

                    if (mtBindingState == 1)
                    {
                        // Binding shift up
                        ModSettings.ShiftUpButton = controlIndex;
                        mtBindingState = 2;
                    }
                    else if (mtBindingState == 2)
                    {
                        // Binding shift down - complete the setup
                        ModSettings.ShiftDownButton = controlIndex;
                        ModSettings.Save();
                        FinishMTBinding();
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// Complete the manual transmission binding and update the menu item
        /// </summary>
        private void FinishMTBinding()
        {
            // Enable manual transmission
            elscTransmission.SetVehicle(currentVehicle);
            elscTransmission.Enable();

            // Save to vehicle data so it persists across script reloads
            if (currentVehicle != null && currentVehicle.Exists())
            {
                VehicleSaveData.SetManualTransmissionOwned(currentVehicle.DisplayName, true);
                VehicleSaveData.Save();
            }

            // Update the menu item in place
            if (mtBindingItem != null)
            {
                mtBindingItem.AltTitle = ""; // Empty - sprite shows instead
                mtBindingItem.Description = $"Shift Up: {ModSettings.ShiftUpKey} | Shift Down: {ModSettings.ShiftDownKey} | Select to disable";
                itemOwnershipStatus[mtBindingItem] = STATUS_INSTALLED;
            }

            mtBindingState = 0;
            mtBindingItem = null;

            ShowNotification("~g~Manual Transmission installed!");
        }

        /// <summary>
        /// Get display name for a GTA control index
        /// </summary>
        private string GetControlName(int controlIndex)
        {
            return controlIndex switch
            {
                201 => "A",
                202 => "B",
                203 => "X",
                204 => "Y",
                205 => "LB",
                206 => "RB",
                207 => "LT",
                208 => "RT",
                187 => "D-Up",
                188 => "D-Down",
                189 => "D-Left",
                190 => "D-Right",
                216 => "L3",
                217 => "R3",
                _ => $"Button {controlIndex}"
            };
        }

        /// <summary>
        /// Handle controller input for manual transmission
        /// </summary>
        private void HandleButtonMapping()
        {
            if (!elscTransmission.IsEnabled) return;

            // Use configured buttons (when not in menu)
            if (!isMenuActive)
            {
                // Check shift up button
                if (Game.IsControlJustPressed((GTA.Control)ModSettings.ShiftUpButton))
                {
                    elscTransmission.ShiftUp();
                }
                // Check shift down button
                if (Game.IsControlJustPressed((GTA.Control)ModSettings.ShiftDownButton))
                {
                    elscTransmission.ShiftDown();
                }
            }
        }

        #endregion
    }
}
