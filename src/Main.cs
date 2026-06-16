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
using ExtendedLSC.WindowTint;

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

        // Custom window-glass color (requires the dlc_elsc clear-glass texture)
        private WindowTint.WindowTintManager windowTint = new WindowTint.WindowTintManager();
        private VehicleSnapshot.VehicleSnapshotManager vehicleSnapshots = new VehicleSnapshot.VehicleSnapshotManager();
        private bool _snapMenuWasOpen = false;
        private int wheelApplyAxle = 0;   // wheel design target: 0 = Front + Rear, 1 = Front only, 2 = Rear only
        private bool _fitmentInitAllowed = true; // Disable if crashes occur
        // Pause the live fitment while previewing wheels — otherwise it keeps re-applying the OLD wheel's
        // camber/track/height offsets to the previewed wheel, making rims float/raise. Re-baselined on close.
        private bool suspendWheelFitment = false;
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
        private enum DebugMode { None, Menu, Resizer, SpriteBrowser, MenuPosition, InputTiming }
        private DebugMode activeDebugMode = DebugMode.None;
        private bool showCollisionDebug = false;   // debug menu: green body collision cloud + blue wheel colliders
        private readonly System.Collections.Generic.List<Vector3> _bodyHits = new System.Collections.Generic.List<Vector3>();
        private int _bodyScanHandle = 0;            // vehicle handle the body cloud was scanned for
        private Vector3 _bodyMin, _bodyMax;         // local-space AABB of the body-only hits (extents box)
        private bool _bodyBoxValid = false;
        private int debugMenuSelection = 0;
        private readonly string[] debugMenuOptions = { "Resizer", "Sprite Browser", "Menu Position", "Input Timing", "Collision Wireframe", "Exit Debug" };
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
        private float originalTopSpeed = 0f;        // now stores RAW handling fInitialDriveMaxFlatVel
        private float originalAcceleration = 0f;    // raw fInitialDriveForce
        private float originalBraking = 0f;         // raw fBrakeForce
        private float originalTraction = 0f;        // raw fTractionCurveMax
        private bool hasStoredOriginalStats = false;

        // Engine swap state. activeEngineSwap = what's installed on currentVehicle (display bonuses folded into
        // the stat bars/PI). isPreviewingSwap + the preview bonuses drive the blue/red bar preview on hover.
        private EngineSwap activeEngineSwap = null;
        private NativeMenu engineSwapMenu = null;   // reference so preview state can track its visibility
        private bool isPreviewingSwap = false;
        private float swapPreviewAccelBonus = 0f;
        private float swapPreviewSpeedBonus = 0f;
        // Engine-audio swaps hijack the radio; re-assert the captured station until this GameTime.
        private string radioRestoreStation = null;
        private int radioRestoreUntil = 0;

        // LSC stat-bar formulas DERIVED empirically from 107 real cars (handling.meta fields vs the displayed
        // in-game LSC stats, R²≈0.88-0.94 — residual is the reference data's rounding to 5s). Each bar is ~linear
        // in ONE handling field. Returns RAW fraction (can exceed 1 — engine swaps — for the Forza-style class
        // chip); use StatPct() for the clamped [0,1] bar fill.
        private enum LSCStat { TopSpeed, Accel, Braking, Traction }
        private static float RawStatPct(LSCStat s, float v)
        {
            switch (s)
            {
                case LSCStat.TopSpeed: return (2.075f * v + 10.43f) / 340f;   // fInitialDriveMaxFlatVel, full at ~159
                case LSCStat.Accel:    return (251.06f * v - 0.56f) / 100f;   // fInitialDriveForce,       full at ~0.40
                case LSCStat.Braking:  return (31.05f * v + 1.79f) / 100f;    // fBrakeForce,              full at ~3.16
                default:               return (29.85f * v + 0.66f) / 100f;    // fTractionCurveMax,        full at ~3.33
            }
        }
        private static float StatPct(LSCStat s, float v)
        {
            float p = RawStatPct(s, v);
            return p < 0f ? 0f : (p > 1f ? 1f : p);
        }
        private static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);

        // Forza-style Performance Index (0-999) from the four RAW stat fractions (uncapped so upgrades/engine
        // swaps push a car past its class). Weighting + class boundaries calibrated against the GTA roster
        // (176 cars): mopeds/buses ~400 (D), compacts ~620 (B), sports ~674 (B), supercars ~750-790 (A),
        // hypercars ~820 (S), race ~950 (X). See [[gtav-lsc-stat-formula]].
        private static int ComputePI(float spd, float acc, float brk, float trc)
        {
            float pi = (spd * 0.30f + acc * 0.30f + trc * 0.25f + brk * 0.15f) * 999f;
            if (pi < 0f) pi = 0f;
            if (pi > 999f) pi = 999f;
            return (int)(pi + 0.5f);
        }

        // Map a PI to its class letter + chip colour (D light-blue, C green, B yellow, A orange, S red, X purple).
        private static void GetVehicleClassInfo(int pi, out string name, out int r, out int g, out int b)
        {
            if (pi < 500)      { name = "D"; r = 92;  g = 178; b = 255; }
            else if (pi < 600) { name = "C"; r = 86;  g = 212; b = 99;  }
            else if (pi < 700) { name = "B"; r = 240; g = 216; b = 58;  }
            else if (pi < 800) { name = "A"; r = 255; g = 146; b = 38;  }
            else if (pi < 900) { name = "S"; r = 240; g = 58;  b = 58;  }
            else               { name = "X"; r = 190; g = 86;  b = 240; }
        }

        // Config
        private Keys menuKey = Keys.F5;
        // debugLogging is now in ModSettings

        // LSC detection
        private int lastInterior = 0;
        // All 5 SP Los Santos Customs interior IDs (captured via GET_INTERIOR_AT_COORDS):
        // 39938 Burton, 37890 La Mesa, 9474 Route 68/Harmony, 115714 Paleto Bay, 93442 Grand Senora.
        private static readonly int[] LSC_INTERIORS = { 39938, 37890, 9474, 115714, 93442 };
        private bool isInLSC = false;
        private bool waitingForVehicleStop = false;
        private Vector3 lastVehiclePos = Vector3.Zero;
        private Vector3 lscWorldPos = Vector3.Zero;   // remembered LSC location, to suppress the "closed" help nearby too
        private Vector3 customizeSpot = Vector3.Zero; // the exact spot the native teleport dropped the car (marker + 5m activation)
        private int lscSettleSince = 0;               // when the first-entry cinematic finished parking the car
        private bool lscSwapped = false;              // have we swapped the native mechanic this visit (suppress once)
        private int lscSuppressUntil = 0;             // keep deleting the bay mechanic each frame until here (kills the vanilla menu flash)
        private bool lscWentFar = false;              // player left the shop area since the last visit (=> carmod_shop reset => arm fresh)
        // Default follow-camera framing applied on menu open (relative to the vehicle). Captured live to match
        // the LSC-style "see what you're working on" angle. Default cam stays active; player can still move it.
        private const float MENU_CAM_HEADING = -149.08f;
        private const float MENU_CAM_PITCH = 13.44f;
        private int menuCamUntil = 0;                 // re-assert the menu framing each frame until here (survives the LSC cinematic-end / follow-cam re-center)
        private bool _recordVanillaLSC = false;       // TEMP (timing capture): stand down LSC hijack so the vanilla animation + menu run

        // Mechanic management
        private const bool ENABLE_MECHANIC_REPLACEMENT = true; // Seamless swap halts carmod_shop's menu (no script termination)
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
        private NativeMenu alignmentParent = null;    // Parent (Suspension) menu to restore when Camber & Ride Height closes
        private bool _hoveringCustomSuspension = false; // true while the "Custom Suspension and Camber" nav item is hovered (show OUR stance, not the game preview)
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

        // Custom license-plate-text editor (on-screen keyboard + uniqueness conflict prompt)
        private bool isEditingPlate = false;
        private int plateKbCooldown = 0;
        private string pendingPlateText = null;        // entered text awaiting conflict resolution
        private string plateBeforeEdit = null;         // the car's plate when editing began (for color migration)
        private NativeMenu plateConflictMenu = null;

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

            // Let the window-tint manager surface player-facing messages (e.g. auto-plate on a collision).
            windowTint.Notify = ShowNotification;

            // Full per-vehicle snapshot save/restore (mods, paint, tint, wheels, …) keyed by model+plate.
            vehicleSnapshots.Log = Log;

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

            // Track engine-swap preview purely from the swap menu's live visibility (no sticky flags to get
            // stranded). When that menu isn't the one on screen, there is no swap preview.
            isPreviewingSwap = engineSwapMenu != null && menu == engineSwapMenu;
            if (isPreviewingSwap) UpdateSwapPreview(menu.SelectedIndex);

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
            // Footer band holds the Forza-style class chip below the 4 stat rows.
            float chipBandHeight = statsRowHeight * 1.05f * statsPanelScaleH;
            float statPanelHeight = (statsRowHeight * 4 + statsPanelPadding) * statsPanelScaleH + chipBandHeight;
            float statPanelWidth = menuWidth * statsPanelScaleW;

            // Draw stats background panel
            Function.Call(Hash.DRAW_RECT, menuCenterX, statsStartY + statPanelHeight / 2f, statPanelWidth, statPanelHeight, 0, 0, 0, 200);

            // Get vehicle stats
            // SPEED: raw fInitialDriveMaxFlatVel from memory (engine/brake/trans mods don't change top speed —
            // verified). ACCEL/BRAKING/TRACTION: the NATIVES return the base handling field multiplied by
            // installed performance-mod effects (and reflect live base-handling tuning too), so the bars MOVE and
            // the blue/red purchase preview works. At stock the natives EQUAL the raw fields (verified), so the
            // empirically-derived formula is calibrated for them.
            float hMaxVel = WheelFitment.WheelMemory.GetHandlingFloat(currentVehicle, WheelFitment.WheelMemory.HOFF_MAX_FLAT_VEL);
            float nAccel = Function.Call<float>(Hash.GET_VEHICLE_ACCELERATION, currentVehicle);
            float nBrake = Function.Call<float>(Hash.GET_VEHICLE_MAX_BRAKING, currentVehicle);
            float nTraction = Function.Call<float>(Hash.GET_VEHICLE_MAX_TRACTION, currentVehicle);

            // Installed engine-swap display bonuses (EnginePowerMultiplier physics isn't read by the natives).
            float instAcc = activeEngineSwap != null ? activeEngineSwap.AccelBonus : 0f;
            float instSpd = activeEngineSwap != null ? activeEngineSwap.SpeedBonus : 0f;

            // RAW (uncapped) fractions. base* = the white "installed" bars; live* = the blue/red preview bars.
            float baseSpeed = RawStatPct(LSCStat.TopSpeed, hMaxVel) + instSpd;
            float baseAccel = RawStatPct(LSCStat.Accel, nAccel) + instAcc;
            float baseBrake = RawStatPct(LSCStat.Braking, nBrake);
            float baseTrac = RawStatPct(LSCStat.Traction, nTraction);
            float liveSpeed = baseSpeed, liveAccel = baseAccel, liveBrake = baseBrake, liveTrac = baseTrac;

            bool statPreviewing = false;
            if (isPreviewingSwap)
            {
                // White = currently installed swap (base*), blue/red = hovered swap. Brakes/traction unchanged.
                statPreviewing = true;
                float nsSpeed = RawStatPct(LSCStat.TopSpeed, hMaxVel);
                float nsAccel = RawStatPct(LSCStat.Accel, nAccel);
                liveSpeed = nsSpeed + swapPreviewSpeedBonus;
                liveAccel = nsAccel + swapPreviewAccelBonus;
            }
            else if (isPreviewingMod && previewModIndex >= 0 && hasStoredOriginalStats)
            {
                // Performance mod indices: Engine=11, Brakes=12, Transmission=13, Suspension=15, Armor=16, Turbo=18
                bool isPerformanceMod = previewModIndex == 11 || previewModIndex == 12 ||
                                         previewModIndex == 13 || previewModIndex == 15 ||
                                         previewModIndex == 16 || previewModIndex == 18;
                if (isPerformanceMod)
                {
                    statPreviewing = true;
                    // live (blue/red) = current natives (with the previewed mod) + installed swap (already base*)
                    // base (white) = stored originals + installed swap bonus
                    baseSpeed = RawStatPct(LSCStat.TopSpeed, originalTopSpeed) + instSpd;
                    baseAccel = RawStatPct(LSCStat.Accel, originalAcceleration) + instAcc;
                    baseBrake = RawStatPct(LSCStat.Braking, originalBraking);
                    baseTrac = RawStatPct(LSCStat.Traction, originalTraction);
                }
            }

            float topSpeedPct = Clamp01(baseSpeed);
            float accelPct = Clamp01(baseAccel);
            float brakingPct = Clamp01(baseBrake);
            float tractionPct = Clamp01(baseTrac);
            float previewTopSpeedPct = statPreviewing ? Clamp01(liveSpeed) : -1f;
            float previewAccelPct = statPreviewing ? Clamp01(liveAccel) : -1f;
            float previewBrakingPct = statPreviewing ? Clamp01(liveBrake) : -1f;
            float previewTractionPct = statPreviewing ? Clamp01(liveTrac) : -1f;

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

            // Forza-style class chip (footer). PI uses RAW uncapped stats (incl. engine-swap bonuses) so
            // upgrades/swaps can bump the class. base* = installed, live* = previewed → chip shows "B -> A".
            int basePI = ComputePI(baseSpeed, baseAccel, baseBrake, baseTrac);
            int livePI = statPreviewing ? ComputePI(liveSpeed, liveAccel, liveBrake, liveTrac) : basePI;
            float chipCenterY = statsStartY + statPanelHeight - chipBandHeight / 2f;
            DrawClassChip(menuLeftX, statPanelWidth, menuCenterX, chipCenterY, chipBandHeight,
                          basePI, livePI, statPreviewing);
        }

        /// <summary>
        /// Draw the Forza-style class chip: a coloured class-letter badge + "PERFORMANCE" label on the left and
        /// the PI number on the right. When previewing a performance mod it shows the class/PI transition (the
        /// resulting class colours the badge; the PI is drawn blue for a gain / red for a loss).
        /// </summary>
        private void DrawClassChip(float panelLeftX, float panelWidth, float panelCenterX, float centerY,
                                   float bandHeight, int basePI, int newPI, bool previewing)
        {
            bool changed = previewing && newPI != basePI;
            int shownPI = previewing ? newPI : basePI;

            string baseName; int br, bg, bb; GetVehicleClassInfo(basePI, out baseName, out br, out bg, out bb);
            string newName; int nr, ng, nb; GetVehicleClassInfo(shownPI, out newName, out nr, out ng, out nb);

            float pad = 0.007f * statsPanelScaleW;

            // --- Class badge (coloured square with the class letter) ---
            float boxSize = bandHeight * 0.62f;
            float boxCenterX = panelLeftX + pad + boxSize / 2f;
            Function.Call(Hash.DRAW_RECT, boxCenterX, centerY, boxSize, boxSize, nr, ng, nb, 255);
            // class letter centred in the badge (GTA text Y is the glyph top, so raise it ~0.62*box to centre)
            Function.Call(Hash.SET_TEXT_FONT, 1);
            Function.Call(Hash.SET_TEXT_SCALE, 0f, bandHeight * 11.5f);
            Function.Call(Hash.SET_TEXT_COLOUR, 10, 10, 10, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, newName);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, boxCenterX, centerY - boxSize * 0.62f);

            // --- "CLASS" / transition label to the right of the badge ---
            float textX = boxCenterX + boxSize / 2f + pad;
            float labelY = centerY - bandHeight * 0.30f;
            string classLabel = changed ? (baseName + " → " + newName) : (newName + "-CLASS");
            Function.Call(Hash.SET_TEXT_FONT, 0);
            Function.Call(Hash.SET_TEXT_SCALE, 0f, statsLabelScale);
            if (changed) Function.Call(Hash.SET_TEXT_COLOUR, nr, ng, nb, 255);
            else Function.Call(Hash.SET_TEXT_COLOUR, 235, 235, 235, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, false);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, classLabel);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, textX, labelY);

            // --- PI number, right-aligned in the band ---
            int piColR = 235, piColG = 235, piColB = 235;
            if (changed) { if (newPI > basePI) { piColR = 0; piColG = 150; piColB = 255; } else { piColR = 255; piColG = 50; piColB = 50; } }
            string piText = (changed ? (basePI + " → " + newPI) : shownPI.ToString()) + " PI";
            float rightX = panelLeftX + panelWidth - pad;
            Function.Call(Hash.SET_TEXT_FONT, 4);
            Function.Call(Hash.SET_TEXT_SCALE, 0f, statsLabelScale);
            Function.Call(Hash.SET_TEXT_COLOUR, piColR, piColG, piColB, 255);
            Function.Call(Hash.SET_TEXT_WRAP, 0f, rightX);
            Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, piText);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0f, labelY);
            Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, false);
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

            // Load + re-assert this vehicle's saved engine swap (EnginePowerMultiplier/forced audio reset when
            // the entity restreams, so re-apply whenever we rebuild the menu for this car).
            activeEngineSwap = EngineSwaps.Lookup(VehicleSaveData.GetEngineSwapId(currentVehicle.DisplayName));
            if (activeEngineSwap != null) ApplyEngineSwap(activeEngineSwap);

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
                    var frontItem = new NativeItem(SlotName(1, "Front Bumper"));
                    frontItem.AltTitle = ">>";
                    frontItem.Activated += (s, e) => { isNavigatingMenu = true; bumpersMenu.Visible = false; frontMenu.Visible = true; isNavigatingMenu = false; };
                    bumpersMenu.Add(frontItem);
                }
                if (rearBumperCount > 0)
                {
                    var rearMenu = CreateModMenuByIndex(2, rearBumperCount, "Rear Bumper");
                    rearMenu.Closed += (s, e) => { if (!isNavigatingMenu) bumpersMenu.Visible = true; };
                    var rearItem = new NativeItem(SlotName(2, "Rear Bumper"));
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

            // Engine Swap (custom feature: engine sound + power packages)
            {
                var swapMenu = CreateEngineSwapMenu();
                AddSubmenuItem("Engine Swap", swapMenu);
            }

            // Exhaust (4)
            if (GetModCount(4) > 0)
            {
                var menu = CreateModMenuByIndex(4, GetModCount(4), "Exhaust");
                AddSubmenuItem(SlotName(4, "Exhaust"), menu);
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
                AddSubmenuItem(SlotName(8, "Fender"), menu);
            }
            // Right fender is rare - add as separate category if it exists
            if (rightFenderCount > 0)
            {
                var menu = CreateModMenuByIndex(9, rightFenderCount, "FENDERS (RIGHT)");
                AddSubmenuItem(SlotName(9, "Fender (Right)"), menu);
            }

            // Grille (6)
            if (GetModCount(6) > 0)
            {
                var menu = CreateModMenuByIndex(6, GetModCount(6), "Grille");
                AddSubmenuItem(SlotName(6, "Grille"), menu);
            }

            // Hood (7)
            if (GetModCount(7) > 0)
            {
                var menu = CreateModMenuByIndex(7, GetModCount(7), "Hood");
                AddSubmenuItem(SlotName(7, "Hood"), menu);
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
                AddSubmenuItem(SlotName(5, "Roll Cage"), menu);
            }

            // Roof (10)
            if (GetModCount(10) > 0)
            {
                var menu = CreateModMenuByIndex(10, GetModCount(10), "Roof");
                AddSubmenuItem(SlotName(10, "Roof"), menu);
            }

            // Skirts (3)
            if (GetModCount(3) > 0)
            {
                var menu = CreateModMenuByIndex(3, GetModCount(3), "Skirts");
                AddSubmenuItem(SlotName(3, "Skirts"), menu);
            }

            // Spoiler (0)
            if (GetModCount(0) > 0)
            {
                var menu = CreateModMenuByIndex(0, GetModCount(0), "Spoiler");
                AddSubmenuItem(SlotName(0, "Spoiler"), menu);
            }

            // Suspension (15) — native lowering levels PLUS the custom Camber & Ride Height (alignment),
            // because in real life camber + ride height ARE suspension. Always shown so alignment is
            // available even on vehicles without native suspension mods.
            {
                var menu = GetModCount(15) > 0
                    ? CreateModMenuByIndex(15, GetModCount(15), "Suspension")
                    : CreateMenu("Suspension");
                if (menu != null)
                {
                    alignmentParent = menu;   // BuildAlignmentMenu restores to this on close
                    var alignNav = new NativeItem("Custom Suspension and Camber", FitmentSummary());
                    alignNav.AltTitle = ">>";
                    // Refresh the saved-value summary each time the Suspension menu opens.
                    menu.Shown += (s, e) => { alignNav.Description = FitmentSummary(); _hoveringCustomSuspension = false; };
                    // Hovering our custom item should show OUR stance, not a game-level preview: undo the
                    // generic preview (back to the original installed level) and let our ride-height re-apply.
                    menu.SelectedIndexChanged += (s, e) =>
                    {
                        bool onNav = menu.SelectedItem == alignNav;
                        _hoveringCustomSuspension = onNav;
                        if (onNav && currentVehicle != null && currentVehicle.Exists())
                        {
                            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 15, previewOriginalValue, false);
                            // Re-apply our stance on THIS frame so there's no one-frame gap at the base.
                            wheelFitment.SuspendRideHeight = false;
                            wheelFitment.ApplyRideHeightNow();
                        }
                    };
                    // Rebuild fresh on open so it reflects current wheels / owned / pro-mode state.
                    alignNav.Activated += (s, e) =>
                    {
                        var sub = BuildAlignmentMenu();
                        if (sub == null) return;
                        isNavigatingMenu = true; menu.Visible = false; sub.Visible = true; isNavigatingMenu = false;
                    };
                    menu.Add(alignNav);
                    AddSubmenuItem("Suspension", menu);
                }
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

            // Benny's / interior / engine-bay categories (Engine Block, Air Filter, Struts, Arch Cover, Seats,
            // Dashboard, Door Speakers, Steering Wheel, Hydraulics, Trunk, etc.). These have ModCategories
            // entries but weren't built by the hardcoded blocks above, so the car's Benny's parts never showed.
            // Add any the current car actually has. (46 Windows is skipped — the tint item already uses that
            // name; 48 Livery is already built above.)
            int[] bennysIndices = { 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45 };
            foreach (int mi in bennysIndices)
            {
                if (mi == 24 && !currentVehicle.Model.IsBike) continue;     // rear wheels: motorcycles only
                if (GetModCount(mi) <= 0) continue;
                if (!ModCategories.AllCategories.TryGetValue(mi, out var cat)) continue;
                var bm = CreateModMenuByIndex(mi, GetModCount(mi), cat.DisplayName);
                if (bm != null) AddSubmenuItem(cat.DisplayName, bm);
            }
        }

        /// <summary>True for motorcycles/bikes — they use the bike wheel type (6) and can't be stanced.</summary>
        private bool IsBike(Vehicle v)
        {
            if (v == null || !v.Exists()) return false;
            try { return Function.Call<bool>(Hash.IS_THIS_MODEL_A_BIKE, v.Model.Hash); } catch { return false; }
        }

        /// <summary>Stancing is only allowed on 4-wheel vehicles (excludes bikes, trikes, 6-wheelers, etc.).</summary>
        private bool CanStance(Vehicle v)
        {
            if (v == null || !v.Exists()) return false;
            try { return WheelFitment.WheelMemory.GetWheelCount(v) == 4; } catch { return false; }
        }

        private NativeMenu BuildWheelsMenu()
        {
            var menu = CreateMenu("Wheels");

            // Wheel Type submenu
            var wheelTypeMenu = CreateMenu("Wheel Types");
            wheelTypeMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };

            // Stock Wheels — revert to the car's factory wheels (the per-type lists have no -1 "stock"
            // entry like generic mod menus do). Sits at the top of the wheel-type list.
            var stockWheelsItem = new NativeItem("Stock Wheels", "Revert to the factory wheels");
            stockWheelsItem.Activated += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, -1, false); // front -> stock
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, -1, false); // back -> stock
                    ShowNotification("~g~Reverted to stock wheels");
                }
            };
            wheelTypeMenu.Add(stockWheelsItem);

            // Apply-To axle selector — lets the player run staggered setups (different front/rear rim designs).
            // The wheel TYPE is shared across all wheels (GTA limit), but the front (mod 23) / rear (mod 24)
            // DESIGN can differ. Not shown on bikes.
            if (!IsBike(currentVehicle))
            {
                var axleItem = new NativeListItem<string>("Apply To", "Front + Rear", "Front Only", "Rear Only")
                {
                    SelectedIndex = wheelApplyAxle,
                    Description = "Choose which wheels a design applies to (for staggered setups)"
                };
                axleItem.ItemChanged += (s, e) => { wheelApplyAxle = e.Index; };
                NoWrap(axleItem);
                wheelTypeMenu.Add(axleItem);
            }

            // Wheel types (indices match the GTA enum). High End is 7 (NOT 6 — 6 is BikeWheels). Benny's
            // (8/9) + Street/Track/Open Wheel (10-12) were missing entirely. Empty types return null and are
            // skipped, so a car without a given type just won't list it.
            // Motorcycles only have the Bike Wheels type (6); the car types are empty for them, so show ONLY
            // bike wheels on a bike and ONLY the car types on a car.
            var wheelTypes = IsBike(currentVehicle)
                ? new[] { (6, "Bike Wheels") }
                : new[] {
                    (7, "High End"),
                    (2, "Lowrider"),
                    (1, "Muscle"),
                    (4, "Off-Road"),
                    (0, "Sport"),
                    (3, "SUV"),
                    (5, "Tuner"),
                    (8, "Benny's Originals"),
                    (9, "Benny's Bespoke"),
                    (11, "Street"),
                    (12, "Track"),
                    (10, "Open Wheel")
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

            // Wheel Fitment submenu (VStancer-style adjustments). Stancing is 4-wheel only — bikes and other
            // non-4-wheel vehicles don't get the Fitment menu at all.
            if (ModSettings.VStancerIntegration && CanStance(currentVehicle))
            {
                wheelFitmentParent = menu;   // so BuildWheelFitmentMenu can restore this parent on close
                var fitmentMenu = BuildWheelFitmentMenu();
                if (fitmentMenu != null)
                {
                    // NOTE: the parent-restore Closed handler is wired INSIDE BuildWheelFitmentMenu (using
                    // wheelFitmentParent) so that rebuilt instances (Reset/Purchase) restore the parent too.
                    var fitmentNavItem = new NativeItem("Wheel Fitment");
                    fitmentNavItem.AltTitle = ">>";
                    fitmentNavItem.Description = "Adjust camber, track width, and ride height";
                    // REBUILD on open so the size/width sliders unlock the moment aftermarket wheels are on
                    // (HasVisualWheels is read at build time; the menu was previously built once with stock
                    // wheels and never refreshed).
                    fitmentNavItem.Activated += (s, e) =>
                    {
                        isNavigatingMenu = true;
                        menu.Visible = false;
                        var fresh = BuildWheelFitmentMenu() ?? fitmentMenu;
                        fresh.Visible = true;
                        isNavigatingMenu = false;
                    };
                    menu.Add(fitmentNavItem);
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

            // Simplified: titled "Fitment Service", Pro toggle on top, then Wheel Width / Size / Poke,
            // then Reset. No purchase gate, no Save button — it auto-saves when you leave the menu.
            var menu = CreateMenu("Fitment Service");
            menu.Closed += (s, e) =>
            {
                SaveCurrentFitment();   // auto-save on leave
                if (!isNavigatingMenu && wheelFitmentParent != null) wheelFitmentParent.Visible = true;
            };

            _fitmentResync.Clear();
            InitFitmentForVehicle();
            AddProModeToggle(menu, BuildWheelFitmentMenu);
            AddSizeWidthItems(menu);   // Wheel Width, Wheel Size (wheel-specific fitment)
            AddPokeItems(menu);        // Wheel Poke (or Front/Rear Poke in Pro)
            AddResetItem(menu, BuildWheelFitmentMenu);
            return menu;
        }

        // ============================================================================================
        // Fitment slider GROUPS — shared by the Wheel Fitment menu (Wheels) and the Camber & Ride Height
        // submenu (Suspension). Each group respects Pro Mode internally (combined vs per-axle).
        // ============================================================================================

        // Generic per-axle float slider (radians/metres) used by the Pro-mode camber/height/poke controls.
        // Make a value slider STOP at its ends instead of wrapping (+max -> -min). Reverts a user left/right
        // that wrapped the index past an end. Direction is Unknown for programmatic SelectedIndex sets (e.g.
        // the auto-recovery resync), so those are ignored. Use on numeric value sliders, not choice-lists.
        private void NoWrap<T>(NativeListItem<T> item)
        {
            item.ItemChanged += (s, e) =>
            {
                int last = item.Items.Count - 1;
                if (last <= 0) return;
                if (e.Direction == Direction.Right && e.Index == 0) item.SelectedIndex = last;
                else if (e.Direction == Direction.Left && e.Index == last) item.SelectedIndex = 0;
            };
        }

        private NativeListItem<float> MakeFitSlider(string label, float min, float max, float step,
                                                    Func<float> get, Action<float> set, string desc)
        {
            var vals = new List<float>();
            for (float v = min; v <= max + 0.001f; v += step) vals.Add((float)Math.Round(v, 2));
            int idx = vals.FindIndex(v => Math.Abs(v - get()) < step * 0.55f);
            if (idx < 0) idx = vals.FindIndex(v => Math.Abs(v) < step * 0.55f);
            if (idx < 0) idx = vals.Count / 2;
            var item = new NativeListItem<float>(label, vals.ToArray()) { SelectedIndex = idx };
            item.Description = desc;
            item.ItemChanged += (s, e) => set(e.Object);
            _fitmentResync.Add(() =>
            {
                int j = vals.FindIndex(v => Math.Abs(v - get()) < step * 0.55f);
                if (j < 0) j = vals.FindIndex(v => Math.Abs(v) < step * 0.55f);
                if (j < 0) j = vals.Count / 2;
                item.SelectedIndex = j;
            });
            NoWrap(item);
            return item;
        }

        // ---- Wheel Width + Wheel Size (custom rims only). Width first, per the requested order. ----
        private void AddSizeWidthItems(NativeMenu menu)
        {
            if (wheelFitment.HasVisualWheels)
            {
                // Wheel Width (%) — up to 500% for wide-tire / deep-dish stance looks.
                var widthMults = new List<float>();
                var widthLabels = new List<string>();
                for (float m = 0.3f; m <= 5.001f; m += 0.05f)
                {
                    float mult = (float)Math.Round(m, 2);
                    widthMults.Add(mult);
                    widthLabels.Add(Math.Abs(mult - 1f) < 0.001f ? "100% (stock)" : $"{mult * 100f:F0}%");
                }
                int wIdx = 0;
                for (int i = 0; i < widthMults.Count; i++)
                    if (Math.Abs(widthMults[i] - wheelFitment.VisualWidth) < 0.026f) { wIdx = i; break; }
                var widthItem = new NativeListItem<string>("Wheel Width", widthLabels.ToArray()) { SelectedIndex = wIdx };
                widthItem.Description = "Fat meats or stretched rubber";
                widthItem.ItemChanged += (s, e) => { wheelFitment.VisualWidth = widthMults[e.Index]; };
                NoWrap(widthItem);
                menu.Add(widthItem);
                _fitmentResync.Add(() =>
                {
                    int j = 0;
                    for (int i = 0; i < widthMults.Count; i++)
                        if (Math.Abs(widthMults[i] - wheelFitment.VisualWidth) < 0.026f) { j = i; break; }
                    widthItem.SelectedIndex = j;
                });

                // Wheel Size (inches)
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
                sizeItem.Description = "Overall wheel diameter";
                sizeItem.ItemChanged += (s, e) => { wheelFitment.VisualSize = sizeMults[e.Index]; };
                NoWrap(sizeItem);
                menu.Add(sizeItem);
                _fitmentResync.Add(() =>
                {
                    int j = 0;
                    for (int i = 0; i < sizeMults.Count; i++)
                        if (Math.Abs(sizeMults[i] - wheelFitment.VisualSize) < 0.026f) { j = i; break; }
                    sizeItem.SelectedIndex = j;
                });
            }
            else
            {
                var hint = new NativeItem("Wheel Size / Width", "Fit custom wheels") { Enabled = false };
                hint.Description = "Install aftermarket wheels to unlock wheel sizing";
                menu.Add(hint);
            }
        }

        // ---- Camber (alignment — Suspension) ----
        private void AddCamberItems(NativeMenu menu)
        {
            if (ModSettings.WheelFitmentProMode)
            {
                menu.Add(MakeFitSlider("Front Camber", WheelFitment.WheelFitment.MIN_CAMBER, WheelFitment.WheelFitment.MAX_CAMBER, 0.01f,
                    () => wheelFitment.FrontCamber, v => wheelFitment.FrontCamber = v, "Tilt the front wheels in/out"));
                menu.Add(MakeFitSlider("Rear Camber", WheelFitment.WheelFitment.MIN_CAMBER, WheelFitment.WheelFitment.MAX_CAMBER, 0.01f,
                    () => wheelFitment.RearCamber, v => wheelFitment.RearCamber = v, "Tilt the rear wheels in/out"));
            }
            else
            {
                var degs = new List<int>(); var labels = new List<string>();
                for (int d = -45; d <= 45; d++) { degs.Add(d); labels.Add(d == 0 ? "0° (stock)" : $"{d}°"); }
                int idx = 45; float cur = WheelFitment.WheelFitment.ToDegrees(wheelFitment.FrontCamber);
                for (int i = 0; i < degs.Count; i++) if (Math.Abs(degs[i] - cur) < 0.51f) { idx = i; break; }
                var item = new NativeListItem<string>("Camber", labels.ToArray()) { SelectedIndex = idx };
                item.Description = "Tilt the wheels in for that stanced look";
                item.ItemChanged += (s, e) =>
                {
                    float rad = WheelFitment.WheelFitment.ToRadians(degs[e.Index]);
                    wheelFitment.FrontCamber = rad; wheelFitment.RearCamber = rad;
                };
                NoWrap(item);
                menu.Add(item);
                _fitmentResync.Add(() =>
                {
                    int j = degs.IndexOf(0); float c = WheelFitment.WheelFitment.ToDegrees(wheelFitment.FrontCamber);
                    for (int i = 0; i < degs.Count; i++) if (Math.Abs(degs[i] - c) < 0.51f) { j = i; break; }
                    item.SelectedIndex = j;
                });
            }
        }

        // ---- Ride Height (Suspension). Now drives the STABLE handling body-lift (fSuspensionRaise) — a single
        // whole-body value, so there is no front/rear split even in Pro mode. Range matches the raise clamp. ----
        private void AddHeightItems(NativeMenu menu)
        {
            int lo = (int)Math.Round(WheelFitment.WheelFitment.MIN_RAISE * 100f);   // e.g. -30 cm (slam)
            int hi = (int)Math.Round(WheelFitment.WheelFitment.MAX_RAISE * 100f);   // e.g. +45 cm (lift)
            var cms = new List<int>(); var labels = new List<string>();
            for (int cm = lo; cm <= hi; cm++) { cms.Add(cm); labels.Add(cm == 0 ? "0 cm (stock)" : (cm > 0 ? $"+{cm} cm" : $"{cm} cm")); }
            int idx = cms.IndexOf(0); float cur = -wheelFitment.FrontHeight * 100f;
            for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - cur) < 0.51f) { idx = i; break; }
            var item = new NativeListItem<string>("Ride Height", labels.ToArray()) { SelectedIndex = idx };
            item.Description = "Lift (+) or slam (-) the whole car";
            item.ItemChanged += (s, e) =>
            {
                float h = -cms[e.Index] / 100f;
                wheelFitment.FrontHeight = h; wheelFitment.RearHeight = h;
            };
            NoWrap(item);
            menu.Add(item);
            _fitmentResync.Add(() =>
            {
                int j = cms.IndexOf(0); float c = -wheelFitment.FrontHeight * 100f;
                for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - c) < 0.51f) { j = i; break; }
                item.SelectedIndex = j;
            });
        }

        // ---- Rake (front/rear tilt — Suspension). + lowers the front, - lowers the rear. ----
        private void AddRakeItems(NativeMenu menu)
        {
            int lo = (int)Math.Round(WheelFitment.WheelFitment.MIN_RAKE * 100f);   // - = rear low
            int hi = (int)Math.Round(WheelFitment.WheelFitment.MAX_RAKE * 100f);   // + = front low
            var cms = new List<int>(); var labels = new List<string>();
            for (int cm = lo; cm <= hi; cm++)
                cms.Add(cm);
            foreach (int cm in cms)
                labels.Add(cm == 0 ? "0 (level)" : (cm > 0 ? $"+{cm} (front low)" : $"{cm} (rear low)"));
            int idx = cms.IndexOf(0); float cur = wheelFitment.Rake * 100f;
            for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - cur) < 0.51f) { idx = i; break; }
            var item = new NativeListItem<string>("Rake", labels.ToArray()) { SelectedIndex = idx };
            item.Description = "Tilt the car front/back: + drops the front, - drops the rear";
            item.ItemChanged += (s, e) => { wheelFitment.Rake = cms[e.Index] / 100f; };
            NoWrap(item);
            menu.Add(item);
            _fitmentResync.Add(() =>
            {
                int j = cms.IndexOf(0); float c = wheelFitment.Rake * 100f;
                for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - c) < 0.51f) { j = i; break; }
                item.SelectedIndex = j;
            });
        }

        // ---- Wheel Poke / track width (wheel fitment — Wheels) ----
        private void AddPokeItems(NativeMenu menu)
        {
            if (ModSettings.WheelFitmentProMode)
            {
                menu.Add(MakeFitSlider("Front Poke", WheelFitment.WheelFitment.MIN_TRACK_WIDTH, WheelFitment.WheelFitment.MAX_TRACK_WIDTH, 0.01f,
                    () => wheelFitment.FrontTrackWidth, v => wheelFitment.FrontTrackWidth = v, "Push front wheels out"));
                menu.Add(MakeFitSlider("Rear Poke", WheelFitment.WheelFitment.MIN_TRACK_WIDTH, WheelFitment.WheelFitment.MAX_TRACK_WIDTH, 0.01f,
                    () => wheelFitment.RearTrackWidth, v => wheelFitment.RearTrackWidth = v, "Push rear wheels out"));
            }
            else
            {
                int lo = (int)Math.Round(WheelFitment.WheelFitment.MIN_TRACK_WIDTH * 100f);   // inward (tuck)
                int hi = (int)Math.Round(WheelFitment.WheelFitment.MAX_TRACK_WIDTH * 100f);   // outward (poke)
                var cms = new List<int>(); var labels = new List<string>();
                for (int cm = lo; cm <= hi; cm++) { cms.Add(cm); labels.Add(cm == 0 ? "0 cm (stock)" : (cm > 0 ? $"+{cm} cm" : $"{cm} cm")); }
                int idx = cms.IndexOf(0); float cur = wheelFitment.FrontTrackWidth * 100f;
                for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - cur) < 0.51f) { idx = i; break; }
                var item = new NativeListItem<string>("Wheel Poke", labels.ToArray()) { SelectedIndex = idx };
                item.Description = "Push the wheels out toward the fenders";
                item.ItemChanged += (s, e) =>
                {
                    float t = cms[e.Index] / 100f;
                    wheelFitment.FrontTrackWidth = t; wheelFitment.RearTrackWidth = t;
                };
                NoWrap(item);
                menu.Add(item);
                _fitmentResync.Add(() =>
                {
                    int j = cms.IndexOf(0); float c = wheelFitment.FrontTrackWidth * 100f;
                    for (int i = 0; i < cms.Count; i++) if (Math.Abs(cms[i] - c) < 0.51f) { j = i; break; }
                    item.SelectedIndex = j;
                });
            }
        }

        // Init + load saved fitment for the current vehicle (shared by both fitment menus).
        private void InitFitmentForVehicle()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            if (!wheelFitment.IsInitialized || lastFitmentVehicle != currentVehicle)
            {
                // Hand off cleanly: restore the OLD car's model-shared suspension raise before rebinding.
                if (wheelFitment.IsInitialized && lastFitmentVehicle != currentVehicle)
                    try { wheelFitment.RestoreSuspension(); } catch { }
                wheelFitment.Initialize(currentVehicle);
                lastFitmentVehicle = currentVehicle;
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
                    wheelFitment.Rake = saved.Rake;
                    wheelFitment.StockFake = saved.StockFake;
                    wheelFitment.VisualSize = saved.VisualSize;
                    wheelFitment.VisualWidth = saved.VisualWidth;
                }
            }
        }

        // Shared "service" controls (Pro Mode toggle + Save + Reset). rebuild() rebuilds the host menu.
        // Pro Mode toggle (top of each fitment menu) — rebuilds the host menu so the item set matches.
        private void AddProModeToggle(NativeMenu menu, Func<NativeMenu> rebuild)
        {
            var proItem = new NativeCheckboxItem("Pro Mode", "Per-axle (front/rear separate) controls", ModSettings.WheelFitmentProMode);
            proItem.CheckboxChanged += (s, e) =>
            {
                ModSettings.WheelFitmentProMode = proItem.Checked;
                ModSettings.Save();
                isNavigatingMenu = true; menu.Visible = false;
                var nm = rebuild(); if (nm != null) nm.Visible = true;
                isNavigatingMenu = false;
            };
            menu.Add(proItem);
        }

        // Reset-to-stock (bottom of each fitment menu). No separate Save — menus auto-save on close.
        private void AddResetItem(NativeMenu menu, Func<NativeMenu> rebuild)
        {
            var resetItem = new NativeItem("Reset to Stock", "Reset all fitment to factory settings");
            resetItem.Activated += (s, e) =>
            {
                wheelFitment.Reset();
                SaveCurrentFitment();
                GTA.UI.Notification.Show("Fitment reset to stock");
                isNavigatingMenu = true; menu.Visible = false;
                var nm = rebuild(); if (nm != null) nm.Visible = true;
                isNavigatingMenu = false;
            };
            menu.Add(resetItem);
        }

        // Camber & Ride Height submenu — lives under the SUSPENSION category (alignment + springs are
        // suspension in real life). Pro toggle on top, then sliders, then Reset; auto-saves on close.
        private NativeMenu BuildAlignmentMenu()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return null;
            var menu = CreateMenu("Custom Suspension and Camber");
            menu.Closed += (s, e) =>
            {
                SaveCurrentFitment();   // auto-save on leave
                if (!isNavigatingMenu && alignmentParent != null) alignmentParent.Visible = true;
            };
            _fitmentResync.Clear();
            InitFitmentForVehicle();
            AddProModeToggle(menu, BuildAlignmentMenu);
            AddCamberItems(menu);
            AddHeightItems(menu);
            AddRakeItems(menu);
            AddResetItem(menu, BuildAlignmentMenu);
            return menu;
        }

        // Resync callbacks for the currently-built fitment sliders. Each one re-reads its wheelFitment value
        // and corrects its SelectedIndex in place. Rebuilt whenever a fitment menu is built.
        private readonly List<Action> _fitmentResync = new List<Action>();

        // When the stability monitor reverts an unstable setup, snap the OPEN fitment sliders to the reverted
        // values IN PLACE (correct each item's SelectedIndex). We don't rebuild the menu — that left the old
        // menu object still receiving input, so the slider would jump back to its pre-revert index on the next
        // press. Resyncing the live items keeps the player on the same menu they're navigating.
        private void RebuildOpenFitmentMenu()
        {
            try { foreach (var a in _fitmentResync) a(); }
            catch { }
        }

        // One-line summary of the current custom suspension/camber values, shown as the nav-item description.
        private string FitmentSummary()
        {
            try
            {
                if (!wheelFitment.IsInitialized) return "Ride height, camber, rake & poke";
                var parts = new List<string>();
                int h = (int)Math.Round(-wheelFitment.FrontHeight * 100f);   // +cm = lift, -cm = slam
                if (Math.Abs(h) >= 1) parts.Add($"Height {(h > 0 ? "+" : "")}{h}cm");
                int camDeg = (int)Math.Round(WheelFitment.WheelFitment.ToDegrees(wheelFitment.FrontCamber));
                if (Math.Abs(camDeg) >= 1) parts.Add($"Camber {(camDeg > 0 ? "+" : "")}{camDeg}°");
                int rake = (int)Math.Round(wheelFitment.Rake * 100f);
                if (Math.Abs(rake) >= 1) parts.Add($"Rake {(rake > 0 ? "+" : "")}{rake}");
                int poke = (int)Math.Round(wheelFitment.FrontTrackWidth * 100f);
                if (Math.Abs(poke) >= 1) parts.Add($"Poke {(poke > 0 ? "+" : "")}{poke}cm");
                return parts.Count == 0 ? "Stock — nothing set" : string.Join("  ·  ", parts);
            }
            catch { return "Ride height, camber, rake & poke"; }
        }

        // The player chose one of the game's native Suspension levels (Stock/Lowered/Street/Sport/Competition).
        // Hand ride height over to the game: re-base our fake-lowering off the new level and zero our custom
        // offset so the two don't stack. Camber/poke/rake/size are independent and stay.
        private void OnGameSuspensionChosen()
        {
            try
            {
                if (!wheelFitment.IsInitialized) return;
                wheelFitment.SuspendRideHeight = false;
                wheelFitment.RefreshStockFake();   // the game just set the new level's ride height
                wheelFitment.FrontHeight = 0f;     // disable our custom ride-height (game's level takes over)
                wheelFitment.RearHeight = 0f;
                SaveCurrentFitment();
                RebuildOpenFitmentMenu();           // reflect the zeroed ride-height slider if it's open
            }
            catch { }
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
                RearHeight = wheelFitment.RearHeight,
                Rake = wheelFitment.Rake,
                StockFake = wheelFitment.StockFake,
                VisualSize = wheelFitment.VisualSize,
                VisualWidth = wheelFitment.VisualWidth
            };
            VehicleSaveData.SetFitmentData(currentVehicle.DisplayName, fitmentData);
            // Mark "owned" so InitFitmentForVehicle re-loads this on re-entry (the purchase gate that used
            // to set this was removed — fitment is now free, and saving is what flags a car as customised).
            VehicleSaveData.SetWheelFitmentOwned(currentVehicle.DisplayName, true);
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
            var menu = CreateMenuFromConfig(
                "Windows",
                () => (int)currentVehicle.Mods.WindowTint,
                (item) => { windowTint.ClearCustomColor(currentVehicle); ApplyWindowTint(item.Value); }
            );
            if (menu == null) return null;

            // Custom Color picker (requires the dlc_elsc clear-glass texture). Choosing a preset above clears it.
            var colorMenu = CreateCustomWindowColorMenu();
            var openItem = new NativeItem("Custom Color") { AltTitle = ">>" };
            openItem.Activated += (s, e) =>
            {
                isNavigatingMenu = true; menu.Visible = false; colorMenu.Visible = true; isNavigatingMenu = false;
            };
            colorMenu.Closed += (s, e) => { if (!isNavigatingMenu) menu.Visible = true; };
            menu.Add(openItem);
            return menu;
        }

        /// <summary>RGB picker for custom window glass color. Live-previews on change; saves on Apply/close.</summary>
        private NativeMenu CreateCustomWindowColorMenu()
        {
            var menu = CreateMenu("Custom Window Color");

            // R/G/B: 0..255 in steps of 5. Intensity: 5%..100% in steps of 5.
            var chVals = new List<int>(); for (int v = 0; v <= 255; v += 5) chVals.Add(v);
            var intList = new List<int>(); for (int v = 5; v <= 100; v += 5) intList.Add(v);
            var intVals = intList.ToArray();

            int saved = windowTint.CurrentColor(currentVehicle);
            // Fresh start: R/G/B = 0, intensity 25% (alpha ~64). Existing color loads its saved values.
            int def = saved != 0 ? saved : WindowTintManager.PackArgb(64, 0, 0, 0);
            WindowTintManager.UnpackArgb(def, out int da, out int dr, out int dg, out int db);

            int Nearest(List<int> list, int val)
            {
                int best = 0, bd = int.MaxValue;
                for (int i = 0; i < list.Count; i++) { int d = Math.Abs(list[i] - val); if (d < bd) { bd = d; best = i; } }
                return best;
            }
            int NearestArr(int[] list, int val)
            {
                int best = 0, bd = int.MaxValue;
                for (int i = 0; i < list.Length; i++) { int d = Math.Abs(list[i] - val); if (d < bd) { bd = d; best = i; } }
                return best;
            }

            var rItem = new NativeListItem<int>("Red", chVals.ToArray()) { SelectedIndex = Nearest(chVals, dr) };
            var gItem = new NativeListItem<int>("Green", chVals.ToArray()) { SelectedIndex = Nearest(chVals, dg) };
            var bItem = new NativeListItem<int>("Blue", chVals.ToArray()) { SelectedIndex = Nearest(chVals, db) };
            var iItem = new NativeListItem<int>("Intensity %", intVals) { SelectedIndex = NearestArr(intVals, (int)Math.Round(da / 2.55)) };

            Func<int> build = () =>
            {
                int a = (int)Math.Round(iItem.SelectedItem * 2.55);
                return WindowTintManager.PackArgb(a, rItem.SelectedItem, gItem.SelectedItem, bItem.SelectedItem);
            };
            Action preview = () =>
            {
                if (currentVehicle == null) return;
                if (!windowTint.CanPreview) { ShowNotification("~y~Initializing window-color table… try again in a moment."); return; }
                windowTint.PreviewColor(currentVehicle, build());
            };
            rItem.ItemChanged += (s, e) => preview();
            gItem.ItemChanged += (s, e) => preview();
            bItem.ItemChanged += (s, e) => preview();
            iItem.ItemChanged += (s, e) => preview();
            menu.Add(rItem); menu.Add(gItem); menu.Add(bItem); menu.Add(iItem);

            // While the picker is open, stop the per-tick assertion from re-stamping the saved color over the
            // live slider preview, and show the starting color immediately.
            menu.Shown += (s, e) => { windowTint.BeginPreview(currentVehicle); preview(); };

            // Backing out saves the previewed color (to clear a custom color, pick None in the parent menu).
            menu.Closed += (s, e) =>
            {
                if (currentVehicle != null && windowTint.Ready) windowTint.ApplyCustomColor(currentVehicle, build());
                windowTint.EndPreview();
            };
            return menu;
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

            // Body/visual slots (0-10): use the vehicle-specific LSC name (e.g. Skirts -> "Truck Bed").
            if (modIndex >= 0 && modIndex <= 10)
            {
                string slot = SlotName(modIndex, null);
                if (!string.IsNullOrEmpty(slot)) menuTitle = slot.ToUpper();
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
                        originalTopSpeed = WheelFitment.WheelMemory.GetHandlingFloat(currentVehicle, WheelFitment.WheelMemory.HOFF_MAX_FLAT_VEL);
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
                    if (idx == 15) OnGameSuspensionChosen();
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
                    if (idx == 15) OnGameSuspensionChosen();
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

        // ---------- Engine Swap (custom feature: FORCE_VEHICLE_ENGINE_AUDIO sound + EnginePowerMultiplier power) ----------

        /// <summary>Apply (or, with null, revert) an engine swap to the current vehicle.</summary>
        private void ApplyEngineSwap(EngineSwap swap)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            activeEngineSwap = swap;

            // FORCE_VEHICLE_ENGINE_AUDIO has a side effect of switching/enabling the radio. Capture the player's
            // current station first and re-assert it for a short window afterwards (the audio engine applies the
            // radio change asynchronously, so a one-shot restore can get overwritten).
            string preStation = Function.Call<string>(Hash.GET_PLAYER_RADIO_STATION_NAME);

            if (swap == null)
            {
                currentVehicle.EnginePowerMultiplier = 1.0f; // ~stock power
                string spawn = GetVehicleSpawnName(currentVehicle);
                if (!string.IsNullOrEmpty(spawn))
                    Function.Call((Hash)5695999342423706424L, currentVehicle, spawn); // FORCE_VEHICLE_ENGINE_AUDIO — best-effort sound revert
            }
            else
            {
                Function.Call((Hash)5695999342423706424L, currentVehicle, swap.AudioName);
                currentVehicle.EnginePowerMultiplier = swap.PowerMult;
            }

            radioRestoreStation = preStation;
            radioRestoreUntil = Game.GameTime + 500;
            RestoreRadioStation();
        }

        /// <summary>Re-assert the captured radio station so the engine-audio swap doesn't hijack it.</summary>
        private void RestoreRadioStation()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            string st = radioRestoreStation;
            if (string.IsNullOrEmpty(st) || st == "OFF" || st == "OFF_RADIO")
                Function.Call(Hash.SET_VEH_RADIO_STATION, currentVehicle, "OFF");
            else
                Function.Call(Hash.SET_RADIO_TO_STATION_NAME, st);
        }

        /// <summary>Best-effort spawn/audio name for a vehicle (its model label token, lowercased).</summary>
        private string GetVehicleSpawnName(Vehicle v)
        {
            if (v == null || !v.Exists()) return null;
            string token = Function.Call<string>(Hash.GET_DISPLAY_NAME_FROM_VEHICLE_MODEL, (uint)v.Model.Hash);
            return string.IsNullOrEmpty(token) ? null : token.ToLowerInvariant();
        }

        /// <summary>Set the hovered-swap preview bonuses for the blue/red stat preview (index 0 = Stock).</summary>
        private void UpdateSwapPreview(int index)
        {
            if (index <= 0) { swapPreviewAccelBonus = 0f; swapPreviewSpeedBonus = 0f; return; }
            int si = index - 1;
            if (si >= 0 && si < EngineSwaps.All.Count)
            {
                swapPreviewAccelBonus = EngineSwaps.All[si].AccelBonus;
                swapPreviewSpeedBonus = EngineSwaps.All[si].SpeedBonus;
            }
        }

        private NativeMenu CreateEngineSwapMenu()
        {
            var menu = CreateMenu("ENGINE SWAP");
            engineSwapMenu = menu;

            // Refresh each item's price/installed icon from the active swap.
            void RefreshIcons()
            {
                string id = activeEngineSwap?.Id;
                var stock = menu.Items[0];
                if (id == null) { stock.AltTitle = ""; itemOwnershipStatus[stock] = STATUS_INSTALLED; }
                else { stock.AltTitle = "Free"; itemOwnershipStatus.Remove(stock); }
                for (int i = 0; i < EngineSwaps.All.Count; i++)
                {
                    var it = menu.Items[i + 1];
                    var sw = EngineSwaps.All[i];
                    if (id == sw.Id) { it.AltTitle = ""; itemOwnershipStatus[it] = STATUS_INSTALLED; }
                    else { it.AltTitle = $"${sw.Price:N0}"; itemOwnershipStatus.Remove(it); }
                }
            }

            // Preview state is driven by the menu's live visibility in DrawVehicleStats (sticky Shown/Closed
            // flags could get left set if the whole pool closes at once, freezing the chip). Keep this only
            // for immediate responsiveness on hover.
            menu.SelectedIndexChanged += (s, e) => UpdateSwapPreview(e.Index);

            // Stock (index 0) — revert
            var stockItem = new NativeItem("Stock Engine");
            stockItem.Description = "Factory engine and sound. Reverts any swap.";
            stockItem.Activated += (s, e) =>
            {
                isPreviewingSwap = false;
                ApplyEngineSwap(null);
                VehicleSaveData.SetEngineSwapId(currentVehicle.DisplayName, null);
                VehicleSaveData.Save();
                RefreshIcons();
            };
            menu.Add(stockItem);

            // One item per swap
            foreach (var swap in EngineSwaps.All)
            {
                var captured = swap;
                var item = new NativeItem(swap.Name);
                item.Description = swap.Description;
                item.Activated += (s, e) =>
                {
                    bool owned = activeEngineSwap != null && activeEngineSwap.Id == captured.Id;
                    if (!TryPurchase(captured.Price, owned)) return;
                    isPreviewingSwap = false;
                    ApplyEngineSwap(captured);
                    VehicleSaveData.SetEngineSwapId(currentVehicle.DisplayName, captured.Id);
                    VehicleSaveData.Save();
                    RefreshIcons();
                };
                menu.Add(item);
            }

            RefreshIcons();
            return menu;
        }

        private NativeMenu CreateModMenu(VehicleModType modType, int count)
        {
            return CreateModMenuByIndex((int)modType, count, ModPricing.GetModTypeName(modType));
        }

        private NativeMenu CreateWheelTypeMenu(int wheelType)
        {
            // Set wheel type temporarily to count wheels AND read their real LSC names. The wheel label
            // only resolves under the matching wheel type, so we must fetch names inside this window.
            // CRITICAL: changing the wheel TYPE resets the current wheel MOD index, so we capture the full
            // wheel state (type + front/back mod + custom-tire flag) and restore ALL of it afterwards —
            // otherwise just opening the menu (which builds all 7 type menus) reverts the player's rims.
            int origType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
            int origFrontMod = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
            int origBackMod = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);
            bool origCustomTires = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, currentVehicle, 23);
            // The fitment's visual size/width (StreamRenderGfx) also gets reset by the type juggle + mod
            // re-apply below, and the fitment caches "last applied" so it won't re-write an unchanged value
            // (the wheels render the wrong width until the slider is nudged). Capture + restore it too.
            float origVisSize = WheelMemory.GetVisualSize(currentVehicle);
            float origVisWidth = WheelMemory.GetVisualWidth(currentVehicle);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);

            int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, 23); // 23 = front wheels

            var wheelNames = new string[count > 0 ? count : 0];
            for (int i = 0; i < count; i++)
            {
                string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, 23, i);
                string name = (!string.IsNullOrEmpty(label) && label != "NULL") ? Game.GetLocalizedString(label) : null;
                wheelNames[i] = string.IsNullOrEmpty(name) ? $"Wheel {i + 1}" : name;
            }

            // Restore the FULL original wheel state (type AND mod), then re-stamp the visual size/width the
            // mod re-apply just cleared, so the rims keep their fitment width when the menu opens.
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, origType);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, origFrontMod, origCustomTires);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, origBackMod, origCustomTires);
            if (origVisSize > 0.1f && origVisSize < 5f) WheelMemory.SetVisualSize(currentVehicle, origVisSize);
            if (origVisWidth > 0.1f && origVisWidth < 5f) WheelMemory.SetVisualWidth(currentVehicle, origVisWidth);

            if (count <= 0) return null;

            var menu = CreateMenu(ModPricing.GetWheelTypeName(wheelType));
            int wType = wheelType; // capture for closures

            // Preview state (menu-scoped): captured ONCE when the menu opens, restored on close unless bought.
            // Wheels need SET_VEHICLE_WHEEL_TYPE in addition to SET_VEHICLE_MOD, which is why the generic
            // mod-menu preview path didn't cover them and rims only showed after purchase.
            int previewOrigWheelType = origType;
            int previewOrigWheelMod = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
            int previewOrigBackMod = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);
            bool wheelPurchased = false;
            bool captured = false; // guard: don't re-capture the "original" once previews have started
            var wheelItems = new List<(NativeItem item, int wheelIdx)>();

            // Preview a wheel index under this menu's wheel type, on the axle(s) the player picked (staggered).
            void PreviewWheel(int wheelIdx)
            {
                if (currentVehicle == null || !currentVehicle.Exists() || wheelIdx < 0) return;
                Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wType);   // type is shared across wheels
                if (wheelApplyAxle != 2) Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, wheelIdx, false); // Front
                if (wheelApplyAxle != 1) Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, wheelIdx, false); // Back
            }

            // Mark the item matching the player's ORIGINAL wheel as installed (not the previewed one),
            // and any PREVIOUSLY-BOUGHT wheel of this type as owned (icon + free re-install).
            void MarkInstalled()
            {
                bool currentTypeMatches = previewOrigWheelType == wType;
                // Highlight the rim installed on the axle currently being edited (rear when "Rear Only").
                int installedMod = wheelApplyAxle == 2 ? previewOrigBackMod : previewOrigWheelMod;
                string vName = currentVehicle != null && currentVehicle.Exists() ? currentVehicle.DisplayName : null;
                foreach (var (item, wi) in wheelItems)
                {
                    itemOwnershipStatus.Remove(item);
                    bool owned = vName != null && VehicleSaveData.IsModOwned(vName, 23, WheelOwnKey(wType, wi));
                    if (currentTypeMatches && wi == installedMod)
                    {
                        itemOwnershipStatus[item] = STATUS_INSTALLED;
                        item.AltTitle = "";
                    }
                    else if (owned)
                    {
                        itemOwnershipStatus[item] = STATUS_OWNED;
                        item.AltTitle = "";
                    }
                    else
                    {
                        item.AltTitle = $"${ModPricing.GetWheelPrice(wType, wi):N0}";
                    }
                }
            }

            // Store the player's current wheels when the menu opens (once), then preview the highlighted one.
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    if (!captured)
                    {
                        previewOrigWheelType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
                        previewOrigWheelMod = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
                        previewOrigBackMod = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);
                        captured = true;
                    }
                    // Pause the live fitment and clear its applied offsets so previewed wheels sit naturally
                    // instead of inheriting the old wheel's stance (the "raises/lowers off the ground" issue).
                    suspendWheelFitment = true;
                    try { wheelFitment.RestoreSuspension(); } catch { }
                    wheelPurchased = false;
                    MarkInstalled();
                    PreviewWheel(menu.SelectedIndex);
                }
            };

            // Preview the wheel as the player scrolls (the fix — rims now show before buying).
            menu.SelectedIndexChanged += (s, e) =>
            {
                PreviewWheel(e.Index);
            };

            // Revert to the original wheels when closing without buying.
            menu.Closed += (s, e) =>
            {
                if (!wheelPurchased && captured && currentVehicle != null && currentVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, previewOrigWheelType);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, previewOrigWheelMod, false);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, previewOrigBackMod, false); // keep staggered rear
                }
                captured = false; // allow a fresh capture next time the menu opens
                // Resume the live fitment. The wheel-key change triggers RefreshBaseline (seating + baseline)
                // on its own next tick — do NOT force a full re-init here (lastFitmentVehicle=null), which
                // would WIPE the player's live camber/track/height values back to saved/stock.
                suspendWheelFitment = false;
            };

            for (int i = 0; i < count; i++)
            {
                int price = ModPricing.GetWheelPrice(wheelType, i);
                var item = new NativeItem(wheelNames[i]);

                // Show the owned icon (and make it free) if this wheel was bought before — matches every
                // other mod category, which the wheel menu previously didn't do (wheels weren't saved).
                bool ownedAtBuild = currentVehicle != null && currentVehicle.Exists()
                    && VehicleSaveData.IsModOwned(currentVehicle.DisplayName, 23, WheelOwnKey(wType, i));
                if (ownedAtBuild)
                {
                    item.AltTitle = ""; // icon drawn instead of price
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = $"${price:N0}";
                }

                int wheelIndex = i;
                int wheelPrice = price;
                item.Activated += (s, e) =>
                {
                    bool owned = VehicleSaveData.IsModOwned(currentVehicle.DisplayName, 23, WheelOwnKey(wType, wheelIndex));
                    if (!TryPurchase(wheelPrice, owned)) return;
                    wheelPurchased = true; // keep the preview, don't revert on close
                    ApplyWheelAxle(wType, wheelIndex, wheelApplyAxle);
                    // Save wheel ownership so it's free to re-install later and shows the owned icon.
                    if (!owned)
                    {
                        VehicleSaveData.SetModOwned(currentVehicle.DisplayName, 23, WheelOwnKey(wType, wheelIndex));
                        VehicleSaveData.Save();
                    }
                    // This wheel is now installed on the chosen axle(s) — update the revert baseline + icon.
                    previewOrigWheelType = wType;
                    if (wheelApplyAxle != 2) previewOrigWheelMod = wheelIndex;
                    if (wheelApplyAxle != 1) previewOrigBackMod = wheelIndex;
                    MarkInstalled();
                };
                menu.Add(item);
                wheelItems.Add((item, wheelIndex));
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

        // Read the vehicle's current pearlescent colour (wheel colour is the 2nd extra-colour slot).
        private int GetCurrentPearl()
        {
            int pearl = 0;
            if (currentVehicle != null && currentVehicle.Exists())
                unsafe { int p, w; Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &p, &w); pearl = p; }
            return pearl;
        }

        private int GetCurrentWheelColor()
        {
            int wheel = 0;
            if (currentVehicle != null && currentVehicle.Exists())
                unsafe { int p, w; Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, currentVehicle, &p, &w); wheel = w; }
            return wheel;
        }

        private NativeMenu CreateWheelColorMenu()
        {
            var menu = CreateMenu("Wheel Color");

            // The full GTA palette (was a flat 7-color list) organized into previewable category submenus,
            // mirroring the main paint menu. Wheel colour is any color index 0-159 via SET_VEHICLE_EXTRA_COLOURS.
            // NOTE: Chrome is intentionally excluded — chrome doesn't apply as a wheel colour in-game.
            var categories = new (string name, VehicleColors.ColorInfo[] colors)[]
            {
                ("Classic", VehicleColors.ClassicColors),
                ("Metallic", VehicleColors.MetallicColors),
                ("Matte", VehicleColors.MatteColors),
                ("Metal", VehicleColors.MetalFinishes),
            };

            int price = ModPricing.WheelColorPrice;
            string priceText = $"${price:N0}";

            foreach (var (categoryName, colors) in categories)
            {
                var categoryMenu = CreateMenu(categoryName);
                var capturedColors = colors;
                int origWheelColor = 0;
                bool previewing = false;
                var colorItems = new List<(NativeItem item, int colorIndex)>();

                void PreviewWheelColor(int colorIndex)
                {
                    if (currentVehicle == null || !currentVehicle.Exists()) return;
                    Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, GetCurrentPearl(), colorIndex);
                }

                // On open: snapshot the player's wheel colour, mark the installed one, preview the highlight.
                categoryMenu.Shown += (s, e) =>
                {
                    if (currentVehicle == null || !currentVehicle.Exists()) return;
                    origWheelColor = GetCurrentWheelColor();
                    previewing = true;
                    foreach (var (item, colorIndex) in colorItems)
                    {
                        itemOwnershipStatus.Remove(item);
                        item.AltTitle = priceText;
                        if (colorIndex == origWheelColor) { itemOwnershipStatus[item] = STATUS_INSTALLED; item.AltTitle = ""; }
                    }
                    int sel = categoryMenu.SelectedIndex;
                    if (sel >= 0 && sel < capturedColors.Length) PreviewWheelColor(capturedColors[sel].ColorIndex);
                };

                // Preview as the player scrolls.
                categoryMenu.SelectedIndexChanged += (s, e) =>
                {
                    if (e.Index >= 0 && e.Index < capturedColors.Length) PreviewWheelColor(capturedColors[e.Index].ColorIndex);
                };

                // Revert to the original wheel colour on close unless one was bought.
                categoryMenu.Closed += (s, e) =>
                {
                    if (previewing && currentVehicle != null && currentVehicle.Exists())
                    {
                        PreviewWheelColor(origWheelColor);
                        previewing = false;
                    }
                    if (!isNavigatingMenu) menu.Visible = true;
                };

                foreach (var color in colors)
                {
                    var item = new NativeItem(color.DisplayName);
                    item.AltTitle = priceText;
                    int colorId = color.ColorIndex;
                    item.Activated += (s, e) =>
                    {
                        if (!TryPurchase(price, false)) return;
                        previewing = false; // keep the chosen colour
                        ApplyWheelColor(colorId);
                        foreach (var (otherItem, _) in colorItems)
                        {
                            itemOwnershipStatus.Remove(otherItem);
                            otherItem.AltTitle = priceText;
                        }
                        itemOwnershipStatus[item] = STATUS_INSTALLED;
                        item.AltTitle = "";
                    };
                    categoryMenu.Add(item);
                    colorItems.Add((item, colorId));
                }

                var navItem = new NativeItem(categoryName);
                navItem.AltTitle = ">>";
                navItem.Description = $"{colors.Length} colors";
                var capturedMenu = categoryMenu;
                navItem.Activated += (s, e) => { isNavigatingMenu = true; menu.Visible = false; capturedMenu.Visible = true; isNavigatingMenu = false; };
                menu.Add(navItem);
            }

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

            // --- Custom plate TEXT (ELSC extra: name your car; doubles as its persistent identity) ---
            string curText = GetPlateText(currentVehicle);
            var customTextItem = new NativeItem("Custom Plate Text", "Type your own plate (up to 8 characters)")
            {
                AltTitle = string.IsNullOrEmpty(curText) ? "" : curText
            };
            customTextItem.Activated += (s, e) => StartEditPlate();
            menu.Add(customTextItem);

            var randomTextItem = new NativeItem("Random Plate", "Generate a unique plate automatically");
            randomTextItem.Activated += (s, e) =>
            {
                string p = windowTint.GenerateUniquePlate();
                if (!string.IsNullOrEmpty(p)) ApplyCustomPlate(p, GetPlateText(currentVehicle));
            };
            menu.Add(randomTextItem);

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

        /// <summary>
        /// Vehicle-specific mod-slot name, exactly as Los Santos Customs shows it. The same slot is named
        /// differently per vehicle (e.g. the "Skirts" slot is "Truck Bed" on a truck, "Spoiler" can be
        /// "Wing", etc.). GET_MOD_SLOT_NAME returns the game's own per-vehicle label; we fall back to our
        /// own name only if the game returns nothing. Using this everywhere keeps category names in sync
        /// with LSC for every vehicle automatically.
        /// </summary>
        private string SlotName(int modIndex, string fallback)
        {
            if (currentVehicle != null && currentVehicle.Exists())
            {
                string label = Function.Call<string>(Hash.GET_MOD_SLOT_NAME, currentVehicle, modIndex);
                if (!string.IsNullOrEmpty(label) && label != "NULL")
                {
                    string name = Game.GetLocalizedString(label);
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
            return fallback;
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

        // Ownership key for a wheel: encodes BOTH the wheel type and the rim index into one int, stored under
        // mod index 23. The same rim index means different wheels under different types, so the type must be
        // part of the key (otherwise buying "Sport #0" would mark "Muscle #0" as owned too).
        private static int WheelOwnKey(int wheelType, int wheelIndex) => wheelType * 1000 + wheelIndex;

        /// <summary>Apply a wheel design to the chosen axle(s): 0 = front+rear, 1 = front only, 2 = rear only.
        /// The wheel TYPE is shared across all wheels (GTA limit); only the design index differs per axle.</summary>
        private void ApplyWheelAxle(int wheelType, int wheelIndex, int axle)
        {
            if (currentVehicle == null) return;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            if (axle != 2) Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, wheelIndex, false); // front
            if (axle != 1) Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, wheelIndex, false); // rear
            ShowNotification(axle == 1 ? "~g~Front wheels installed!" : axle == 2 ? "~g~Rear wheels installed!" : "~g~Wheels installed!");
            MechanicSpeak();
            Log($"Applied wheel type {wheelType} index {wheelIndex} axle {axle}");
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
            // Publish live menu state so the bridge (Claude) can read which menu/item is focused
            // in real time and navigate reliably instead of blind key-counting. Throttled internally.
            DumpMenuState();

            // Re-assert the radio station after an engine-audio swap hijacked it (async, so hold it briefly).
            if (radioRestoreUntil != 0)
            {
                if (Game.GameTime < radioRestoreUntil) RestoreRadioStation();
                else radioRestoreUntil = 0;
            }

            // Custom window color: advance the one-time table scan, and re-assert the driven car's color.
            try { windowTint.Tick(Game.Player.Character?.CurrentVehicle); } catch { }

            // Full vehicle snapshot: restore saved cars (sweep), and capture the current car when the player
            // finishes customizing (the whole ELSC menu tree just closed).
            try
            {
                vehicleSnapshots.Tick();
                bool elscOpen = menuPool.AreAnyVisible;
                if (_snapMenuWasOpen && !elscOpen)
                {
                    var cv = Game.Player.Character?.CurrentVehicle;
                    if (cv != null && cv.Exists()) vehicleSnapshots.CaptureCurrent(cv);
                }
                _snapMenuWasOpen = elscOpen;
            }
            catch { }

            // Re-assert the menu's default-camera framing for a short window after open, so it survives the LSC
            // cinematic-end / follow-cam re-centering (a single SET on the open frame gets overridden). Released
            // after the window so the player can move the camera freely. Skipped during orbital Camera Mode.
            if (isMenuActive && !isWalkAroundActive && Game.GameTime < menuCamUntil)
            {
                Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_HEADING, MENU_CAM_HEADING);
                Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_PITCH, MENU_CAM_PITCH, 1.0f);
            }

            // Collision wireframe overlay (F8) — drawn every frame regardless of menu state.
            if (showCollisionDebug) DrawCollisionDebug();

            // Handle description editor first (on-screen keyboard)
            UpdateDescriptionEditor();

            // Handle custom plate-text editor (on-screen keyboard)
            UpdatePlateEditor();

            // Check for X to edit/cancel description (editor mode)
            CheckDescriptionEditInput();

            // Skip input processing while an on-screen keyboard is open (description OR plate text), so button
            // presses go to the text box and don't leak through to the menu underneath.
            // But still draw the menu and custom UI.
            if (isEditingDescription || isEditingPlate)
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
                    bool modeSwitched = true;
                    switch (debugMenuSelection)
                    {
                        case 0: activeDebugMode = DebugMode.Resizer; break;
                        case 1: activeDebugMode = DebugMode.SpriteBrowser; spriteBrowserPage = 0; break;
                        case 2: activeDebugMode = DebugMode.MenuPosition; selectedUIElement = UIElement.Menu; break;
                        case 3: activeDebugMode = DebugMode.InputTiming; break;
                        case 4:
                            showCollisionDebug = !showCollisionDebug;
                            if (showCollisionDebug) _bodyScanHandle = 0;   // force a fresh body scan
                            ShowNotification(showCollisionDebug
                                ? "~g~Collision wireframe ON~w~  (~g~green~w~ body, ~b~blue~w~ wheels)"
                                : "~y~Collision wireframe OFF");
                            modeSwitched = false;
                            break;
                        case 5: ExitDebugMode(); return;
                    }
                    if (modeSwitched)
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

            // Debug: Log controller input (only when not editing)
            DebugLogControllerInput();

            // Handle button mapping for manual transmission (runs even in menu)
            HandleButtonMapping();

            // Update manual transmission when driving (not in menu). Guarded: an exception here must
            // NOT escape OnTick (SHVDN would unload the whole script mid-session). Transient-tolerant.
            if (!isMenuActive)
            {
                try { UpdateManualTransmission(); }
                catch (Exception ex) { Log($"[MT] OnTick error: {ex.Message}"); }
            }

            // Wheel fitment updates EVERY tick (in AND out of the menu) so height/camber edits re-settle
            // immediately while the player is adjusting them, instead of floating until they drive off.
            UpdateWheelFitment();

            // Auto-apply saved stances to specific cars within range (skips the player's current car).
            // Guarded like fitment: on error, disable for the session rather than crash the script.
            if (_stanceMgrInit && ModSettings.VStancerIntegration)
            {
                try { stanceManager.Update(); }
                catch (Exception ex) { Log($"[Stance] OnTick error: {ex.Message}"); _stanceMgrInit = false; Log("[Stance] Disabled due to errors"); }
            }

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

                // Frame the car the moment the menu opens (like vanilla LSC) instead of leaving the camera stuck
                // behind the car looking janky. Swing the DEFAULT follow camera to a fixed relative angle — NOT a
                // script cam — so the player keeps full camera control (orbital Camera Mode on Y is separate). On
                // an LSC AUTO-open the cinematic is just ending / the follow cam re-centers behind the just-parked
                // car, so a single SET here gets overridden — re-assert it for ~0.9s (see OnTick) to win the
                // transition, then release so the player can move it.
                Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_HEADING, MENU_CAM_HEADING);
                Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_PITCH, MENU_CAM_PITCH, 1.0f);
                menuCamUntil = Game.GameTime + 900;

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
            if (_recordVanillaLSC) return;   // TEMP: let vanilla carmod_shop run uninterrupted for timing capture

            var player = Game.Player.Character;
            int interior = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player);

            if (interior != lastInterior)
            {
                Log($"Interior changed: {lastInterior} -> {interior}");

                bool enteredLSC = Array.Exists(LSC_INTERIORS, id => id == interior);
                bool leftLSC = Array.Exists(LSC_INTERIORS, id => id == lastInterior);

                if (enteredLSC && player.IsInVehicle())
                {
                    isInLSC = true;
                    // Arm AUTO-open only on a genuine FRESH setup: first time ever, or we'd left the shop area
                    // (>120m) since the last visit — which is when carmod_shop resets (mechanic respawns +
                    // animation replays). A quick hop out and back stays close -> NOT a fresh setup -> don't
                    // re-arm; the marker (drive within 5m of the customize spot + press) handles re-open.
                    bool freshSetup = lscWorldPos == Vector3.Zero || lscWentFar;
                    if (freshSetup)
                    {
                        Log("Entered LSC (fresh setup) - arming auto-open");
                        waitingForVehicleStop = true;
                        lscSwapped = false;
                        lscWentFar = false;
                    }
                    else
                    {
                        Log("Re-entered LSC (near) - marker re-open only");
                        waitingForVehicleStop = false;
                    }
                }
                else if (leftLSC)
                {
                    Log("Left Los Santos Customs - closing menu");
                    isInLSC = false;
                    waitingForVehicleStop = false;
                    lastVehiclePos = Vector3.Zero;

                    Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);

                    if (isMenuActive)
                    {
                        mainMenu.Visible = false;
                        CloseMenu();
                    }
                }

                lastInterior = interior;
            }

            // Track whether the player has gone FAR from the shop since the last visit — that's what makes
            // carmod_shop reset (respawn its mechanic + replay the animation). A quick hop-out-and-back stays
            // close and does NOT reset, so it must NOT re-arm the auto-open (marker handles re-open).
            if (lscWorldPos != Vector3.Zero && Game.Player.Character.Position.DistanceTo(lscWorldPos) > 120f)
                lscWentFar = true;

            // Detect service position
            if (waitingForVehicleStop && isInLSC)
            {
                var ped = Game.Player.Character;
                if (ped.IsInVehicle())
                {
                    var vehicle = ped.CurrentVehicle;

                    // carmod_shop takes player control (control OFF) for the WHOLE drive-in animation + its menu.
                    // RECORDED TIMING (Burton): control off at ~11.2s, the car is driven automatically at a steady
                    // ~2.96 m/s for ~5s, then parks (speed->0) at ~16.2s — and ONLY THEN does the vanilla menu
                    // appear. The car is moving (speed > 1) for the entire drive-in and only drops to ~0 at the
                    // end, so "stopped while the cinematic is active" cleanly marks the animation finishing.
                    //
                    // So we must NOT touch carmod_shop until that settle point (touching it earlier kills the
                    // animation — the old bug). At settle we swap the bay mechanic (halts carmod_shop's menu before
                    // it can show) and open ours. For an already-visited shop with no drive-in, the car is already
                    // stopped, so this fires immediately — instant open, as expected.
                    // Settle = car stopped while inside the LSC interior. ONE timer, reset ONLY by MOVEMENT (not by
                    // the control toggle), so it survives the control on->off transition cleanly.
                    if (vehicle.Speed < 0.5f)
                    {
                        if (lscSettleSince == 0) lscSettleSince = Game.GameTime;
                    }
                    else lscSettleSince = 0;

                    // Act on the VERY FIRST parked frame to beat the vanilla menu (it appears ~1 frame after the
                    // car parks). The delay is near-zero — that's what kills the split-second vanilla-menu flash.
                    //  - CINEMATIC (control OFF, e.g. Burton 39938 / Airport 93442): the drive-in animation just
                    //    parked the car -> act THIS frame (0ms). The car moves the WHOLE drive-in (speed never
                    //    dips < 0.5 until the end), so this only fires at the park and never preempts the animation.
                    //  - NO cinematic (control ON, e.g. Grand Senora 9474): an animation ALWAYS grabs the car while
                    //    it's still MOVING (control goes off before the car stops), so a stop with control STILL ON
                    //    means no animation is coming -> safe to act with just a 2-frame confirm (~32ms) to ignore
                    //    a momentary slow-roll dip.
                    bool cinematic = !Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player);

                    // DIRECT "vanilla menu just opened" signal: carmod_shop switches the vehicle radio to this
                    // hidden station the instant it shows its menu (state 44, per decompiled carmod_shop.c line
                    // 7209). Catching it = take over the EXACT frame the menu appears (minimal flash) and NEVER
                    // before the drive-in (the menu only opens AFTER the animation finishes).
                    bool vanillaMenuOpen = false;
                    try { vanillaMenuOpen = Function.Call<string>(Hash.GET_PLAYER_RADIO_STATION_NAME) == "HIDDEN_RADIO_09_HIPHOP_OLD"; } catch { }

                    // Cinematic park -> act immediately. Otherwise wait ~800ms of CONTINUOUS stop so a cinematic
                    // (some shops grab a STOPPED car a beat after you park) gets its chance before we conclude
                    // "no animation" — UNLESS the vanilla menu actually opens first, in which case take over then.
                    long needed = cinematic ? 0 : 800;
                    if (lscSettleSince != 0 && ((Game.GameTime - lscSettleSince) >= needed || vanillaMenuOpen))
                    {
                        // Remove the bay mechanic NOW so carmod_shop never gets to show its menu (no-op at shops
                        // without one); open regardless so non-animated shops still auto-open.
                        DeleteAndRespawnMechanic(vehicle.Position);
                        lscSwapped = true;
                        lscWentFar = false;   // start fresh; only a real far-trip re-arms next time
                        // carmod_shop reacts to the missing mechanic over a few frames, and at a no-animation shop
                        // its menu (state 44) can pop the instant the car stops. Keep deleting the mechanic every
                        // frame for ~0.7s so the vanilla menu can't establish/linger while ELSC's menu covers it.
                        lscSuppressUntil = Game.GameTime + 700;
                        Log($"LSC parked ({(cinematic ? "animated" : "no-anim")}) -> instant takeover");
                        waitingForVehicleStop = false;
                        lscSettleSince = 0;
                        customizeSpot = vehicle.Position;
                        TryOpenMenu();
                        Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                        Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
                    }
                }
            }

            // Suppression window: for a short time after takeover, keep deleting any bay mechanic carmod_shop
            // (re)spawns each frame, so its menu can't establish or linger underneath ELSC's. DeleteAndRespawn is
            // a no-op once the native mechanic is already gone, so this is cheap and flicker-free.
            if (isInLSC && Game.GameTime < lscSuppressUntil)
            {
                try { DeleteAndRespawnMechanic(Game.Player.Character.Position); } catch { }
            }

            // We own the shop's help slot in and AROUND the LSC. With carmod_shop's mechanic gone it sits in a
            // "shop closed" state and spams "Los Santos Customs is closed..." help — which it shows in its trigger
            // zone, slightly outside the interior too. Remember the LSC world position while inside, and suppress
            // the message anywhere near it. When parked inside, overwrite the slot with OUR re-open prompt.
            if (isInLSC) lscWorldPos = Game.Player.Character.Position;
            bool nearLsc = isInLSC || (lscWorldPos != Vector3.Zero && Game.Player.Character.Position.DistanceTo(lscWorldPos) < 60f);

            if (nearLsc && !isMenuActive)
            {
                // Show a small marker at the customize spot the whole time we're in the bay, so the player knows
                // where to drive to. Activation is manual: within 5m of it, press the button to open the menu.
                bool atSpot = false;
                if (isInLSC && customizeSpot != Vector3.Zero)
                {
                    Function.Call(Hash.DRAW_MARKER, 1,
                        customizeSpot.X, customizeSpot.Y, customizeSpot.Z - 1.0f,
                        0f, 0f, 0f, 0f, 0f, 0f, 1.6f, 1.6f, 0.6f,
                        80, 160, 255, 110, false, false, 2, false, 0, 0, false);
                    atSpot = Game.Player.Character.Position.DistanceTo(customizeSpot) < 5f;
                }

                if (atSpot)
                {
                    // Native help bubble renders the correct glyph per input device (E on KB, DPad on controller).
                    Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
                    Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, "Press ~INPUT_CONTEXT~ to customize your vehicle.");
                    Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, false, -1);
                    if (Game.IsControlJustPressed(GTA.Control.Context))
                        TryOpenMenu();   // vanilla menu already halted; just reshow ours
                }
                else
                {
                    Function.Call(Hash.CLEAR_HELP, true);   // suppress carmod_shop's "shop closed" help (inside or nearby)
                }
            }

            if (isInLSC && isMenuActive)
            {
                Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
            }
        }

        #endregion

        #region Mechanic Management

        /// <summary>Swap out carmod_shop's mechanic (the nearest bay ped, its Local_757.f_12). Removing the
        /// script's expected ped cleanly halts its menu flow WITHOUT terminating the script — so control, camera,
        /// the moddable state and re-entry all stay intact. We delete + recreate an identical clone in the SAME
        /// tick (no visible gap, no model-load hitch since the model is already resident), then plant the clone so
        /// it stands as scenery and never wanders.</summary>
        /// <summary>Returns true if it actually swapped a native mechanic (so callers can stop retrying).</summary>
        /// <summary>True if carmod_shop's NATIVE bay mechanic is present (a nearby ped that isn't us or our
        /// clone). Non-destructive — used to detect a fresh shop setup so we know to (re)arm the auto-open. After
        /// we take over we delete that mechanic, so this reads false until carmod_shop respawns one on a real
        /// reset (first entry / far-return).</summary>
        private bool IsNativeMechanicPresent(Vector3 position)
        {
            if (!ENABLE_MECHANIC_REPLACEMENT) return false;
            foreach (var ped in World.GetNearbyPeds(position, 25f))
            {
                if (ped == null || !ped.Exists() || ped == Game.Player.Character) continue;
                if (customMechanic != null && customMechanic.Exists() && ped == customMechanic) continue;
                return true;
            }
            return false;
        }

        private bool DeleteAndRespawnMechanic(Vector3 position)
        {
            if (!ENABLE_MECHANIC_REPLACEMENT) return false;

            // Find the nearest NATIVE mechanic to suppress (a bay ped that isn't already our clone). If there's
            // none (we already swapped this session and carmod_shop hasn't respawned one), do NOTHING — its menu
            // is already halted and deleting our clone would leave the bay empty.
            Ped target = null;
            foreach (var ped in World.GetNearbyPeds(position, 25f))
            {
                if (ped == null || !ped.Exists() || ped == Game.Player.Character) continue;
                if (customMechanic != null && customMechanic.Exists() && ped == customMechanic) continue;
                target = ped; break;
            }
            if (target == null) return false;

            Model model = target.Model;
            Vector3 pos = target.Position;
            float heading = target.Heading;
            int[] drawables = new int[12];
            int[] textures = new int[12];
            for (int i = 0; i < 12; i++)
            {
                drawables[i] = Function.Call<int>(Hash.GET_PED_DRAWABLE_VARIATION, target, i);
                textures[i] = Function.Call<int>(Hash.GET_PED_TEXTURE_VARIATION, target, i);
            }

            DeleteCustomMechanic();        // remove the previous clone; we're about to make a fresh one
            target.Delete();               // same-tick swap: gone + recreated before this frame renders
            model.Request(500);
            if (model.IsLoaded)
            {
                Ped clone = World.CreatePed(model, pos, heading);
                if (clone != null)
                {
                    for (int i = 0; i < 12; i++)
                        Function.Call(Hash.SET_PED_COMPONENT_VARIATION, clone, i, drawables[i], textures[i], 0);
                    clone.BlockPermanentEvents = true;
                    Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, clone, true);
                    Function.Call(Hash.TASK_STAND_STILL, clone, -1);   // plant as scenery, never wanders
                    customMechanic = clone;
                    customMechanicPos = pos;
                }
                model.MarkAsNoLongerNeeded();
            }
            return true;
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

        // --- Live menu-state publishing (for bridge-driven, feedback-based navigation) ---
        private string _lastMenuStateJson = "";
        private void DumpMenuState()
        {
            if (!ModSettings.DebugLogging) return; // dev/automation only — keeps release builds from writing the file
            try
            {
                NativeMenu vis = null;
                foreach (var obj in menuPool)
                    if (obj is NativeMenu m && m.Visible) vis = m; // last visible = topmost open submenu

                string json;
                if (vis == null)
                {
                    json = "{\"open\":false}";
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("{\"open\":true,\"menu\":").Append(JsonStr(vis.Name));
                    sb.Append(",\"selectedIndex\":").Append(vis.SelectedIndex);
                    sb.Append(",\"count\":").Append(vis.Items.Count);
                    sb.Append(",\"selected\":").Append(JsonStr(vis.SelectedItem != null ? vis.SelectedItem.Title : ""));
                    sb.Append(",\"selectedValue\":").Append(JsonStr(TryGetListValue(vis.SelectedItem)));
                    sb.Append(",\"items\":[");
                    for (int i = 0; i < vis.Items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(JsonStr(vis.Items[i].Title));
                    }
                    sb.Append("]}");
                    json = sb.ToString();
                }

                if (json != _lastMenuStateJson)
                {
                    _lastMenuStateJson = json;
                    string p = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC_menustate.json");
                    System.IO.File.WriteAllText(p, json);
                }
            }
            catch { }
        }

        private static string TryGetListValue(NativeItem item)
        {
            if (item == null) return "";
            try
            {
                // NativeListItem<T> exposes SelectedItem (the T value); read it generically.
                var prop = item.GetType().GetProperty("SelectedItem");
                if (prop != null && prop.PropertyType != typeof(NativeItem))
                {
                    var val = prop.GetValue(item);
                    if (val != null) return val.ToString();
                }
            }
            catch { }
            return "";
        }

        private static string JsonStr(string v)
        {
            if (v == null) return "\"\"";
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in v)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // ---- Collision wireframe overlay (F8) ----
        // GREEN = body collision box (model extents). BLUE = each wheel's PHYSICAL collider cylinder, built
        // from the live collision radius/width in CWheel memory — so you can see collision vs the rendered
        // wheel (float = visual below collider, sink = visual above it) while re-tuning the raw fitment.
        private void DrawCollisionDebug()
        {
            try
            {
                Vehicle v = currentVehicle;
                if (v == null || !v.Exists())
                {
                    Ped pl = Game.Player.Character;
                    v = (pl != null && pl.Exists() && pl.IsInVehicle()) ? pl.CurrentVehicle : null;
                }
                if (v == null || !v.Exists()) return;

                // GREEN body collision — the ACTUAL collision surface, found by raycasting a grid from all
                // six sides inward and keeping where the rays hit this vehicle. Scanned once per vehicle (the
                // body collision is static) and cached in vehicle-local space, so it tracks the car as it
                // moves. Drawn as a point cloud (small crosses).
                if (_bodyScanHandle != v.Handle)
                {
                    ScanBodyCollision(v);
                    _bodyScanHandle = v.Handle;
                }
                Color green = Color.FromArgb(255, 0, 255, 0);
                foreach (Vector3 lp in _bodyHits)
                {
                    Vector3 w = v.GetOffsetPosition(lp);
                    World.DrawLine(w + new Vector3(-0.03f, 0, 0), w + new Vector3(0.03f, 0, 0), green);
                    World.DrawLine(w + new Vector3(0, -0.03f, 0), w + new Vector3(0, 0.03f, 0), green);
                    World.DrawLine(w + new Vector3(0, 0, -0.03f), w + new Vector3(0, 0, 0.03f), green);
                }
                // Extents box from the body-only hits (lighter green) so the bounds are clear.
                if (_bodyBoxValid)
                    DrawLocalBox(v, _bodyMin, _bodyMax, Color.FromArgb(255, 150, 255, 150));


                // BLUE wheel collider cylinders (actual physical collision).
                string[] bones = { "wheel_lf", "wheel_rf", "wheel_lr", "wheel_rr" };
                Color blue = Color.FromArgb(255, 40, 130, 255);
                int n = WheelMemory.GetWheelCount(v);
                for (int i = 0; i < bones.Length && i < n; i++)
                {
                    int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v, bones[i]);
                    if (bi < 0) continue;
                    Vector3 c = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, v, bi);
                    float r = WheelMemory.GetTyreColliderRadius(v, i);
                    float w = WheelMemory.GetTyreColliderWidth(v, i);
                    if (r < 0.05f || r > 2f) continue;
                    DrawWheelCylinder(v, c, r, w, blue);
                }
            }
            catch { }
        }

        // Wireframe box from a local-space min/max, transformed to world via the entity matrix.
        private void DrawLocalBox(Entity e, Vector3 min, Vector3 max, Color col)
        {
            Vector3 C(float x, float y, float z) =>
                Function.Call<Vector3>(Hash.GET_OFFSET_FROM_ENTITY_IN_WORLD_COORDS, e, x, y, z);
            Vector3[] p =
            {
                C(min.X, min.Y, min.Z), C(max.X, min.Y, min.Z), C(max.X, max.Y, min.Z), C(min.X, max.Y, min.Z),
                C(min.X, min.Y, max.Z), C(max.X, min.Y, max.Z), C(max.X, max.Y, max.Z), C(min.X, max.Y, max.Z)
            };
            int[,] edges = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
            for (int i = 0; i < 12; i++)
                World.DrawLine(p[edges[i, 0]], p[edges[i, 1]], col);
        }

        // Wireframe cylinder for a wheel: two radius circles in the wheel's roll plane (vehicle fwd/up),
        // offset along the axle (vehicle right) by the collider width, plus a few connecting struts.
        private void DrawWheelCylinder(Entity v, Vector3 center, float radius, float width, Color col)
        {
            Vector3 axle = v.RightVector;
            Vector3 fwd = v.ForwardVector;
            Vector3 up = v.UpVector;
            float half = width * 0.5f;
            Vector3 cA = center + axle * half;
            Vector3 cB = center - axle * half;
            const int seg = 18;
            Vector3 prevA = Vector3.Zero, prevB = Vector3.Zero;
            for (int s = 0; s <= seg; s++)
            {
                float a = (float)(s * 2.0 * Math.PI / seg);
                Vector3 dir = fwd * (float)Math.Cos(a) + up * (float)Math.Sin(a);
                Vector3 pA = cA + dir * radius;
                Vector3 pB = cB + dir * radius;
                if (s > 0)
                {
                    World.DrawLine(prevA, pA, col);
                    World.DrawLine(prevB, pB, col);
                    if (s % 3 == 0) World.DrawLine(pA, pB, col);   // sparse cylinder struts
                }
                prevA = pA; prevB = pB;
            }
        }

        // Scan the vehicle's ACTUAL collision surface by raycasting a grid from all six faces of the model
        // box inward; keep the points where the ray hits THIS vehicle. Stored in vehicle-local space so the
        // cloud stays glued to the car. One-shot per vehicle (a brief hitch on first enable is expected).
        private void ScanBodyCollision(Vehicle v)
        {
            _bodyHits.Clear();
            try
            {
                var oMin = new OutputArgument();
                var oMax = new OutputArgument();
                Function.Call(Hash.GET_MODEL_DIMENSIONS, v.Model.Hash, oMin, oMax);
                Vector3 mn = oMin.GetResult<Vector3>();
                Vector3 mx = oMax.GetResult<Vector3>();

                // Build wheel exclusion cylinders (local center + tyre radius + half-width) so we can drop
                // ray hits that landed on a wheel — those are shown separately in blue.
                var wCenter = new System.Collections.Generic.List<Vector3>();
                var wRad = new System.Collections.Generic.List<float>();
                var wHalf = new System.Collections.Generic.List<float>();
                string[] bones = { "wheel_lf", "wheel_rf", "wheel_lr", "wheel_rr" };
                int nW = WheelMemory.GetWheelCount(v);
                for (int i = 0; i < bones.Length && i < nW; i++)
                {
                    int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v, bones[i]);
                    if (bi < 0) continue;
                    Vector3 c = v.GetPositionOffset(Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, v, bi));
                    float r = WheelMemory.GetTyreColliderRadius(v, i);
                    float w = WheelMemory.GetTyreColliderWidth(v, i);
                    wCenter.Add(c);
                    wRad.Add(r > 0.05f && r < 2f ? r : 0.4f);
                    wHalf.Add(w > 0.02f && w < 1f ? w * 0.5f : 0.2f);
                }

                // The body can never sit below the tyre contact line, so anything lower is the ground or a
                // stray low hit — reject it. (No wheels found -> no floor cut.)
                float floorZ = float.MinValue;
                for (int k = 0; k < wCenter.Count; k++)
                {
                    float bottom = wCenter[k].Z - wRad[k];
                    floorZ = (k == 0) ? bottom : Math.Min(floorZ, bottom);
                }

                // In LOCAL space: X = axle (lateral), Y = forward, Z = up. A point is "on a wheel" if it's
                // inside that wheel's cylinder (axially within half-width, radially within tyre radius).
                bool IsWheel(Vector3 p)
                {
                    for (int k = 0; k < wCenter.Count; k++)
                    {
                        Vector3 d = p - wCenter[k];
                        float axial = Math.Abs(d.X);
                        float radial = (float)Math.Sqrt(d.Y * d.Y + d.Z * d.Z);
                        // Generous margins: tyres can poke past the bone-centered collider (stance/camber),
                        // so over-exclude a little rather than leave wheel points in the body cloud.
                        if (axial < wHalf[k] + 0.16f && radial < wRad[k] + 0.14f) return true;
                    }
                    return false;
                }

                void Cast(Vector3 ls, Vector3 le)
                {
                    RaycastResult r = World.Raycast(v.GetOffsetPosition(ls), v.GetOffsetPosition(le), IntersectFlags.Vehicles);
                    if (r.DidHit && r.HitEntity != null && r.HitEntity.Handle == v.Handle)
                    {
                        Vector3 lp = v.GetPositionOffset(r.HitPosition);
                        if (lp.Z < floorZ + 0.02f) return;   // below tyre contact = ground / stray
                        if (!IsWheel(lp)) _bodyHits.Add(lp);
                    }
                }

                const int G = 11;        // grid resolution per face
                const float pad = 0.4f;  // start the ray this far outside the box
                for (int i = 0; i < G; i++)
                {
                    float ti = (float)i / (G - 1);
                    float x = mn.X + (mx.X - mn.X) * ti;
                    float yi = mn.Y + (mx.Y - mn.Y) * ti;
                    for (int j = 0; j < G; j++)
                    {
                        float tj = (float)j / (G - 1);
                        float y = mn.Y + (mx.Y - mn.Y) * tj;
                        float z = mn.Z + (mx.Z - mn.Z) * tj;
                        Cast(new Vector3(x, y, mx.Z + pad), new Vector3(x, y, mn.Z - pad));   // top-down
                        Cast(new Vector3(x, y, mn.Z - pad), new Vector3(x, y, mx.Z + pad));   // bottom-up
                        Cast(new Vector3(mn.X - pad, yi, z), new Vector3(mx.X + pad, yi, z));  // left->right
                        Cast(new Vector3(mx.X + pad, yi, z), new Vector3(mn.X - pad, yi, z));  // right->left
                        Cast(new Vector3(x, mn.Y - pad, z), new Vector3(x, mx.Y + pad, z));    // front->back
                        Cast(new Vector3(x, mx.Y + pad, z), new Vector3(x, mn.Y - pad, z));    // back->front
                    }
                }

                // Dedicated DENSE underside pass (bottom-up) — the floor pan is large and flat, so the main
                // grid under-samples it. Finer grid catches the underside cleanly.
                const int GB = 19;
                for (int i = 0; i < GB; i++)
                {
                    float x = mn.X + (mx.X - mn.X) * i / (GB - 1);
                    for (int j = 0; j < GB; j++)
                    {
                        float y = mn.Y + (mx.Y - mn.Y) * j / (GB - 1);
                        Cast(new Vector3(x, y, mn.Z - pad), new Vector3(x, y, mx.Z + pad));
                    }
                }

                // Extents box (AABB) of the body-only hits, in local space.
                if (_bodyHits.Count > 0)
                {
                    Vector3 lo = _bodyHits[0], hi = _bodyHits[0];
                    foreach (Vector3 p in _bodyHits)
                    {
                        lo = new Vector3(Math.Min(lo.X, p.X), Math.Min(lo.Y, p.Y), Math.Min(lo.Z, p.Z));
                        hi = new Vector3(Math.Max(hi.X, p.X), Math.Max(hi.Y, p.Y), Math.Max(hi.Z, p.Z));
                    }
                    _bodyMin = lo; _bodyMax = hi; _bodyBoxValid = true;
                }
                else _bodyBoxValid = false;

                Log($"[Collision] body scan: {_bodyHits.Count} body hits (wheels excluded) for {v.DisplayName}");
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

        #endregion

        #region Custom Plate Text

        private string GetPlateText(Vehicle v)
        {
            try { return (Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v) ?? "").Trim(); }
            catch { return ""; }
        }

        /// <summary>Open the on-screen keyboard to type a custom plate (max 8 chars), prefilled with the current.</summary>
        private void StartEditPlate()
        {
            if (isEditingPlate || currentVehicle == null || !currentVehicle.Exists()) return;
            isEditingPlate = true;
            plateBeforeEdit = GetPlateText(currentVehicle);
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", plateBeforeEdit, "", "", "", 8);
            ShowNotification("~y~Enter a plate (up to 8 characters)");
        }

        /// <summary>Poll the keyboard; on accept, validate uniqueness and apply (or raise the conflict prompt).</summary>
        private void UpdatePlateEditor()
        {
            if (!isEditingPlate) return;
            if (plateKbCooldown > 0) { plateKbCooldown--; return; }
            plateKbCooldown = 5;

            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1) // accepted
            {
                isEditingPlate = false;
                string text = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(text)) { ShowNotification("~r~Plate not changed (empty)"); return; }
                if (currentVehicle == null || !currentVehicle.Exists()) return;

                // Same as current -> nothing to do.
                if (string.Equals(text, plateBeforeEdit, StringComparison.OrdinalIgnoreCase)) return;

                if (windowTint.PlateUsedBySavedVehicle(currentVehicle, text))
                {
                    pendingPlateText = text;
                    ShowPlateConflict(text);
                }
                else
                {
                    ApplyCustomPlate(text, plateBeforeEdit);
                }
            }
            else if (status == 2) // cancelled
            {
                isEditingPlate = false;
                ShowNotification("~y~Plate edit cancelled");
            }
        }

        /// <summary>Set the plate text and migrate this car's saved window color so the tint follows the rename.</summary>
        private void ApplyCustomPlate(string text, string oldPlate)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            windowTint.MoveColorToPlate(currentVehicle.DisplayName, oldPlate, text);  // keep the tint on the renamed car
            Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, currentVehicle, text);
            ShowNotification($"~g~Plate set to ~b~{text}");
            pendingPlateText = null;
            // Refresh the plate menu so the "Custom Plate Text" AltTitle shows the new value.
            if (plateMenu != null && plateMenu.Visible)
            {
                isNavigatingMenu = true; plateMenu.Visible = false;
                plateMenu = CreatePlateMenu(); plateMenu.Visible = true;
                isNavigatingMenu = false;
            }
        }

        /// <summary>Duplicate-plate prompt: choose a different name or overwrite the other vehicle's identity.</summary>
        private void ShowPlateConflict(string text)
        {
            if (plateConflictMenu == null)
            {
                plateConflictMenu = CreateMenu("Plate In Use");
                var diff = new NativeItem("Choose a Different Name", "Re-open the keyboard to pick a unique plate");
                diff.Activated += (s, e) =>
                {
                    isNavigatingMenu = true; plateConflictMenu.Visible = false; isNavigatingMenu = false;
                    StartEditPlate();
                };
                var over = new NativeItem("Overwrite Anyway", "Use this plate even though another saved vehicle has it");
                over.Activated += (s, e) =>
                {
                    isNavigatingMenu = true; plateConflictMenu.Visible = false;
                    if (plateMenu != null) plateMenu.Visible = true;
                    isNavigatingMenu = false;
                    if (!string.IsNullOrEmpty(pendingPlateText)) ApplyCustomPlate(pendingPlateText, plateBeforeEdit);
                };
                plateConflictMenu.Add(diff);
                plateConflictMenu.Add(over);
            }
            ShowNotification($"~o~Plate ~b~{text}~o~ is already used by another saved vehicle.");
            isNavigatingMenu = true;
            if (plateMenu != null) plateMenu.Visible = false;
            plateConflictMenu.Visible = true;
            isNavigatingMenu = false;
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
                elscTransmission.NosInstalled = ModSettings.NosEnabled;   // test toggle until LSC purchase
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
            if (suspendWheelFitment) return; // paused while previewing wheels (see CreateWheelTypeMenu)

            Vehicle vehicle = Game.Player.Character?.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists()) return;

            // Stancing is 4-wheel only. On a bike / non-4-wheel vehicle, release any prior binding and bail so
            // we never write camber/track/height to wheels that shouldn't be stanced.
            if (!CanStance(vehicle))
            {
                if (wheelFitment.IsInitialized && lastFitmentVehicle != null)
                {
                    try { wheelFitment.RestoreSuspension(); } catch { }
                    lastFitmentVehicle = null;
                }
                return;
            }

            try
            {
                if (!wheelFitment.IsInitialized || lastFitmentVehicle != vehicle)
                {
                    // Only attempt initialization after the game is fully loaded
                    if (Game.GameTime > 10000)
                    {
                        // Hand off cleanly: restore the OLD car's model-shared raise before rebinding.
                        if (wheelFitment.IsInitialized && lastFitmentVehicle != vehicle)
                            try { wheelFitment.RestoreSuspension(); } catch { }
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
                                wheelFitment.Rake = saved.Rake;
                                wheelFitment.StockFake = saved.StockFake;
                                wheelFitment.VisualSize = saved.VisualSize;
                                wheelFitment.VisualWidth = saved.VisualWidth;
                            }
                        }
                    }
                }

                if (wheelFitment.IsInitialized)
                {
                    // While hovering the game's native Suspension (15) levels, suspend our ride-height so the
                    // game's default preview shows. But on our own "Custom Suspension and Camber" item, DON'T
                    // suspend — show our saved stance instead.
                    wheelFitment.SuspendRideHeight = isPreviewingMod && previewModIndex == 15 && !_hoveringCustomSuspension;
                    wheelFitment.Update();
                    // Auto-recovery: if the live stability monitor reverted an unstable setup, warn + persist.
                    string instWarn = wheelFitment.ConsumeInstability();
                    if (instWarn != null)
                    {
                        ShowNotification("~r~" + instWarn);
                        SaveCurrentFitment();
                        RebuildOpenFitmentMenu();   // snap the sliders to the reverted values
                    }
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
