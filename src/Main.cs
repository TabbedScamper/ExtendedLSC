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
using Newtonsoft.Json;
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
        // EVERY submenu made via CreateMenu — hidden + removed on rebuild so stale menus can't linger/overlap.
        private readonly List<NativeMenu> allSubMenus = new List<NativeMenu>();
        // Main-menu category items -> (rename key, isCustom). Used by the F2 rename hotkey in edit mode.
        private readonly Dictionary<NativeItem, (string key, bool isCustom)> mainCatItems = new Dictionary<NativeItem, (string, bool)>();

        // Special menus that need refresh capability
        private NativeMenu hornMenu;
        private NativeMenu nosMenu;   // the Nitrous tier menu (for the context "Preview" hint)
        private NativeMenu repairMenu;   // damage-repair gate shown at menu open before customizing
        private readonly List<int> _hornItemModIndex = new List<int>();  // horn menu position -> mod index
        private NativeMenu windowTintMenu;
        private NativeMenu plateMenu;
        private NativeMenu packagesMenu;
        private bool isNamingPackage = false;
        // Package preview-on-hover: snapshot the car's mods when the Packages menu opens, apply a package on hover
        // (reverting to the snapshot first so each preview is clean), commit on select, revert on close.
        private readonly Dictionary<NativeItem, string> _packageItemPaths = new Dictionary<NativeItem, string>();
        // Full visual snapshot of the car when the Packages menu opened (so a hover preview can be reverted exactly).
        private VehiclePackageData _packageSnapshot;
        private bool _packageCommitted;
        // True whenever the player is entering TEXT (any on-screen keyboard prompt). While set, ALL other ELSC input
        // must stand down so the letters being typed (e.g. "Drifty") can't ALSO trigger gameplay actions like the
        // walk-around toggle. (Key/button BINDING prompts are separate states with their own guards.)
        private bool IsTypingText =>
            isNamingPackage || isEditingDescription || isEditingPlate || isAddingCategory || isNamingPart
            || isPricingPart || isRenamingCategory || isNamingWheel || isAddingWheelCategory
            || isEditingItemName || isEditingItemPrice;
        private NativeMenu turboMenu;
        private NativeMenu headlightsMenu;

        // Config-based menus: Key = category path, Value = menu
        private Dictionary<string, NativeMenu> configMenusByPath = new Dictionary<string, NativeMenu>();

        // Track item ownership status for icon display (key = NativeItem, value = 1=installed, 2=owned-not-installed)
        private Dictionary<NativeItem, int> itemOwnershipStatus = new Dictionary<NativeItem, int>();
        // LemonUI stores each item's EXACT drawn position in NativeItem.lastPosition (protected internal). Read
        // it via reflection so ownership icons align to the real item rows with zero drift on long/scrolled
        // lists, instead of accumulating a hardcoded item-height estimate.
        private static readonly System.Reflection.FieldInfo _itemLastPosField =
            typeof(NativeItem).GetField("lastPosition", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private const float LEMON_ITEM_HEIGHT = 37.4f;   // LemonUI's actual item height (was fudged to 37.5)
        private bool TryGetItemCenterY(NativeItem item, float lemonYBase, out float centerYNorm)
        {
            centerYNorm = 0f;
            if (_itemLastPosField == null || item == null) return false;
            try
            {
                var pos = (System.Drawing.PointF)_itemLastPosField.GetValue(item);
                if (pos.Y <= 0.01f) return false;   // not drawn this frame (off-screen) -> fall back
                centerYNorm = (pos.Y + LEMON_ITEM_HEIGHT / 2f) / lemonYBase;
                return true;
            }
            catch { return false; }
        }
        // Captured native item descriptions (we draw these ourselves, scroll-aware, instead of LemonUI).
        private readonly Dictionary<NativeItem, string> customDescriptions = new Dictionary<NativeItem, string>();
        private const int STATUS_INSTALLED = 1;
        private const int STATUS_OWNED = 2;
        // Sentinel "item value" for the buyable Custom Window Color (kept clear of the preset tint values 0/2/3/5).
        private const int WINDOW_CUSTOM_COLOR_KEY = 9999;

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
        private bool _driveByDisabled = false;   // we disabled drive-by (to stop aim-breaking windows) while MT is on

        // Optional "Customize Radio" — loops the player's own music while the menu is open.
        private CustomizeRadio customizeRadio;
        // Smoothed garage-radio level (0..1) so it crossfades between states instead of cutting out:
        // 1 = normal (menu open in LSC), QUIET = menu closed but still in LSC, 0 = left LSC (fade out then stop).
        private float _radioFade = 0f;
        private const float RADIO_QUIET_LEVEL = 0.30f;

        // ELSC Wheel Fitment (VStancer-style wheel adjustments)
        private WheelFitment.WheelFitment wheelFitment = new WheelFitment.WheelFitment();
        private Vehicle lastFitmentVehicle = null;

        // Per-instance plate identity (model + plate). _plateClaims maps an uppercased plate -> the vehicle Handle
        // that owns it this session, so a freshly-spawned duplicate (same model + same plate as a built car) gets
        // auto-renamed to a unique plate before ELSC reads/applies saved data — keeping it stock per-instance.
        private readonly Dictionary<string, int> _plateClaims = new Dictionary<string, int>();
        private int _lastPlateBoundHandle = 0;   // guard so EnsureUniquePlate runs once per new-car bind, not per frame
        private readonly Random _plateRng = new Random();

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

        // Manual transmission key binding setup state
        private int mtBindingState = 0; // 0=none, 1=waiting for shift up, 2=waiting for shift down
        private NativeItem mtBindingItem = null; // Reference to the menu item to update after binding
        private int nosBindingState = 0; // 0=none, 1=waiting for the nitrous spray key/button
        private NativeItem nosBindingItem = null; // Reference to the NOS menu item to update after binding
        private bool nosColorMenuOpen = false; // true while the NOS FX picker is open (enables hold-to-preview)
        private Color nosPreviewColor = Color.FromArgb(255, 60, 120, 255); // colour the preview spray uses
        private int nosPreviewFx = 0; // which catalog effect the preview spray shows

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

        // Vehicle Tuning (live handling fine-tune; one-time dyno unlock per vehicle). Handling is model-shared,
        // so factory values are cached per model hash and tunes are applied relative to that captured stock.
        private const int TUNING_FEE = 12000;
        private NativeMenu tuningMenu = null;
        private VehicleSaveData.TuningData currentTuning = new VehicleSaveData.TuningData();
        private float[] currentStock = null;  // [driveForce, maxVel, brakeForce, tractionMax, driveBiasFront, brakeBiasFront, steerLockRad]
        private readonly Dictionary<int, float[]> tuningStockCache = new Dictionary<int, float[]>();
        private readonly List<Action> _tuningResync = new List<Action>();
        // Offset aliases (const-from-const) so the apply code stays readable.
        private const int H_FORCE = WheelFitment.WheelMemory.HOFF_DRIVE_FORCE;
        private const int H_MAXVEL = WheelFitment.WheelMemory.HOFF_MAX_FLAT_VEL;
        private const int H_BRAKE = WheelFitment.WheelMemory.HOFF_BRAKE_FORCE;
        private const int H_TRACTION = WheelFitment.WheelMemory.HOFF_TRACTION_MAX;
        private const int H_TRACTION_INV = WheelFitment.WheelMemory.HOFF_TRACTION_MAX_INV;
        private const int H_DRIVEBIAS = WheelFitment.WheelMemory.HOFF_DRIVE_BIAS_FRONT;
        private const int H_BRAKEBIAS_F = WheelFitment.WheelMemory.HOFF_BRAKE_BIAS_FRONT;
        private const int H_BRAKEBIAS_R = WheelFitment.WheelMemory.HOFF_BRAKE_BIAS_REAR;
        private const int H_STEER = WheelFitment.WheelMemory.HOFF_STEER_LOCK;
        private const int H_STEER_INV = WheelFitment.WheelMemory.HOFF_STEER_LOCK_INV;

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
        private Keys menuKey => ModSettings.MenuKey;   // remappable via [Controls] in settings.ini
        // debugLogging is now in ModSettings

        // LSC detection
        private int lastInterior = 0;
        // All 5 SP Los Santos Customs interior IDs (captured via GET_INTERIOR_AT_COORDS):
        // 39938 Burton, 37890 La Mesa, 9474 Route 68/Harmony, 115714 Paleto Bay, 93442 Grand Senora.
        private static readonly int[] LSC_INTERIORS = { 39938, 37890, 9474, 115714, 93442 };
        private bool isInLSC = false;
        private Vector3 lastVehiclePos = Vector3.Zero;
        private Vector3 lscWorldPos = Vector3.Zero;   // remembered LSC location (leave-cleanup / nearby checks)
        private Vector3 customizeSpot = Vector3.Zero; // the spot the car was parked at when ELSC opened
        // Default follow-camera framing applied on menu open (relative to the vehicle). Captured live to match
        // the LSC-style "see what you're working on" angle. Default cam stays active; player can still move it.
        private const float MENU_CAM_HEADING = -149.08f;
        private const float MENU_CAM_PITCH = 13.44f;
        private int menuCamUntil = 0;                 // re-assert the menu framing each frame until here (survives the LSC cinematic-end / follow-cam re-center)
        private bool _recordVanillaLSC = false;       // TEMP (timing capture): stand down LSC hijack so the vanilla animation + menu run
        // Universal eject takeover: when the vanilla customs menu opens, popping the player out of the driver
        // seat and instantly re-seating makes carmod_shop ABORT its menu (control returns) WITHOUT swapping the
        // mechanic. Phase 0 idle, 1 just-ejected (re-seat next frame), 2 waiting for control to open ELSC.
        private int _lscEjectPhase = 0;
        private int _lscEjectSince = 0;
        private Vehicle _lscEjectVeh = null;
        private int _lscMenuMaybeSince = 0;           // when "control off + stopped in vehicle" first appeared
        private Vector3 _lscCarPos = Vector3.Zero;    // car's nice spot from the FIRST drive-in, restored on re-open
        private float _lscCarHeading = 0f;
        private bool _lscCarCaptured = false;         // captured this visit? (reset on leaving the shop)
        private Camera _lscHoldCam = null;            // freezes the drive-in view over the eject (interp source)
        private Camera _lscMenuCam = null;            // interp target = ELSC's menu framing (read off the settled gameplay cam)
        private int _lscHoldCamReleaseAt = 0;         // GameTime to delete the eject cams after the hand-back

        // Inspect-steering hold: turn the front wheels in walk-around (D-pad Left/Right) and keep them from snapping
        // back to center. The global SteeringFix EXE patch handles every vehicle on exit; this per-frame re-assert
        // additionally pins the ELSC-configured car (and is the fallback if the patch can't match the build). Cleared
        // once the car is actually driven so normal steering resumes. See SteeringFix.cs.
        private float _heldSteerDeg = 0f;     // desired steering angle in degrees (0 = centered)
        private int _steerHoldHandle = 0;     // handle of the vehicle whose wheels we're holding (0 = none)
        private const float STEER_HOLD_MAX_DEG = 45f;

        // Walk-around camera
        private bool isWalkAroundActive = false;
        private Vehicle walkAroundVehicle = null;

        // First-person camera (3rd cycle state). Y cycles: Basic -> Walk-Around -> First-Person -> Basic.
        private bool isFirstPersonActive = false;
        private Camera firstPersonCam = null;
        private float _fpYaw = 0f;     // look yaw offset from vehicle forward (deg)
        private float _fpPitch = 0f;   // look pitch (deg)
        private const float FP_LOOK_SENS = 4.0f;
        private bool radarHiddenByMenu = false;   // minimap/GPS hidden while the ELSC menu is open
        private bool editModeActive = false;       // ELSC modder edit mode (gated by ModSettings.EditorMode)
        private Vector3 cameraOrbitPos = Vector3.Zero;  // Target camera orbit position
        private Vector3 smoothCamPos = Vector3.Zero;    // Smoothed/actual camera position
        private Vector3 smoothLookAt = Vector3.Zero;    // Smoothed look target (for clean pans)
        private Vector3 presetCamPos = Vector3.Zero;    // Computed camera pose when framing a part (preset mode)
        private Vector3 presetTargetWorld = Vector3.Zero; // The part's world position the preset camera looks at
        private float smoothCamHeight = 1.5f;           // Smoothed camera height
        private Camera walkAroundCam = null;
        private float cameraHeight = 1.5f;

        // ---- Idle showcase cinematic (attract mode): after ~10s of no menu input, fade out, hide HUD/menu, and
        // slowly orbit the car (8s per side, fading on each switch). Any input snaps back + unhides. ----
        // Phase: 0 inactive | 1 fading out to enter | 2 playing (orbit) | 3 fading out to switch sides.
        private int _idlePhase = 0;
        private Camera _idleCam = null;
        private Camera _idlePrevRenderingCam = null;   // what was rendering before (null = gameplay cam)
        private int _lastInteractionTime = 0;
        private int _idleSideStart = 0;
        private float _idleSideBaseDeg = 0f;
        private Vector3 _idleCenter; private float _idleDist = 6f; private float _idleHeight = 2f;
        // Per-side shot variety (randomized each switch) so it doesn't repeat the same few views.
        private float _idleShotHeight = 1.5f;   // world-Z above the ground (low hero .. high overhead)
        private float _idleShotDistF = 1.12f;   // bounding-box ellipse factor (closer .. pulled back)
        private float _idleShotFov = 45f;
        private int _idleSlideSign = 1;          // slow drift direction (left/right) for this side
        private int _idlePrevSelIndex = -999;
        private float _idlePrevCursorX = -1f, _idlePrevCursorY = -1f;
        private readonly Random _idleRng = new Random();
        private const int IDLE_DELAY_MS = 10000;     // idle time before the cinematic kicks in
        private const int IDLE_PER_SIDE_MS = 8000;   // how long each side is shown before switching
        private const float IDLE_SLIDE_DEG = 22f;    // slow orbit amount across a single side
        // FAKE fade: a script-drawn black overlay whose alpha we animate ourselves, INSTEAD of DO_SCREEN_FADE_OUT/IN.
        // The native fade (a) leaves the screen stuck black if the script reloads mid-fade and (b) ducks game audio
        // on every camera switch. A drawn rect does neither — on reload it simply stops drawing.
        private float _idleFadeAlpha = 0f;    // 0..255 current overlay opacity
        private float _idleFadeTarget = 0f;   // where it's heading (0 = clear, 255 = full black)
        private float _idleFadeRate = 0.51f;  // alpha units per millisecond
        private bool _idleScreenBlack => _idleFadeAlpha >= 254f;
        private bool _idleHideUI => _idlePhase >= 2; // hide HUD/menu only once we've faded to black + switched cams
        private static readonly GTA.Control[] _idleInputCtrls =
        {
            GTA.Control.FrontendUp, GTA.Control.FrontendDown, GTA.Control.FrontendLeft, GTA.Control.FrontendRight,
            GTA.Control.FrontendAccept, GTA.Control.FrontendCancel, GTA.Control.FrontendLb, GTA.Control.FrontendRb,
            GTA.Control.FrontendLt, GTA.Control.FrontendRt, GTA.Control.FrontendX, GTA.Control.FrontendY,
            GTA.Control.FrontendLs, GTA.Control.FrontendRs
        };
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
        private bool isAddingCategory = false;   // edit mode: on-screen keyboard open to name a new category

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
            hintBar = new LemonUI.Scaleform.InstructionalButtons();   // bottom button-hint bar (real device glyphs)

            // FAILSAFE (startup): a previous instance may have been reloaded mid-camera/menu, leaving the game
            // rendering a dead scripted camera (and the radar / drive-by left toggled off) — which traps the
            // player in a weird state. Reset all of that defensively on every startup.
            try
            {
                World.RenderingCamera = null;
                Function.Call(Hash.DISPLAY_RADAR, true);
                Function.Call(Hash.SET_PLAYER_CAN_DO_DRIVE_BY, Game.Player, true);
            }
            catch { }

            // Initialize settings first (needed for logging)
            ModSettings.Log = Log;
            ModSettings.Load();

            // Initialize folder-based menu config
            MenuConfig.Log = Log;
            MenuConfig.Initialize();

            // Initialize vehicle save data (tracks purchased mods)
            VehicleSaveData.Log = Log;
            VehicleSaveData.Initialize();

            // One-shot housekeeping: clear ELSC saves + packages stamped with Menyoo's placeholder plate "menyoo"
            // (those collide in the per-car plate-identity system). No-op once they're gone.
            PurgeMenyooData();

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

            // Speedometer skins (Simple / FASTandSPEEDY) — drawn by ELSC, bought in the LSC menu.
            Speedo.Log = Log;
            Speedo.Initialize(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "speedo"));

            // Customize Radio — loops the player's OWN mp3/wav files (scripts/ExtendedLSC/radio) while the menu is
            // open. Nothing is bundled; the folder is created empty for the player to fill. Guarded so a missing
            // NAudio.dll (or audio device) can't crash the mod.
            try
            {
                CustomizeRadio.Log = Log;
                customizeRadio = new CustomizeRadio();
                customizeRadio.Initialize(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExtendedLSC", "radio"), ModSettings.CustomizeRadioVolume);
            }
            catch (Exception ex) { Log($"[Radio] init failed: {ex.Message}"); customizeRadio = null; }

            // Restore the saved nitrous flame colour + chosen exhaust effect.
            if (elscTransmission != null)
            {
                elscTransmission.NosFlameColor = Color.FromArgb(ModSettings.NosFlameColorArgb);
                elscTransmission.ActiveFxIndex = ModSettings.NosFxIndex;
                elscTransmission.NosBoostFx = ModSettings.NosBoostFx;
            }

            // Per-specific-car stance manager (auto-applies saved stances to cars in range, even parked).
            // The player's CURRENT car is excluded — it's managed live by `wheelFitment`.
            WheelFitment.VehicleStanceManager.Log = Log;
            stanceManager.ExcludeVehicle = () =>
            {
                var pp = Game.Player.Character;
                return (pp != null && pp.Exists() && pp.IsInVehicle()) ? pp.CurrentVehicle : null;
            };
            try { stanceManager.Initialize(); _stanceMgrInit = true; } catch (Exception ex) { Log($"[Stance] init failed: {ex.Message}"); }

            // Steering auto-center fix: NOP the game's "snap wheels back to center on exit" stores so a parked car
            // keeps its wheels turned (also lets the walk-around camera turn them for inspection). Global, Legacy-only,
            // graceful on a signature miss. Gated by the INI toggle.
            SteeringFix.Log = Log;
            if (ModSettings.KeepSteeringAngle)
            {
                try { SteeringFix.Apply(); } catch (Exception ex) { Log($"[SteeringFix] init failed: {ex.Message}"); }
            }

            // Build static menus
            BuildMainMenu();

            // Hook events
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;   // clean up camera/HUD on reload so the player isn't left stuck

            Log("ExtendedLSC initialized");
        }

        /// <summary>FAILSAFE (unload): SHVDN fires this when the script is reloaded/stopped. The scripted camera
        /// and HUD toggles live in GAME state, not script state, so without this the player would be left looking
        /// through a dead walk-around camera (or with the radar/drive-by stuck off) after a reload.</summary>
        private void OnAborted(object sender, EventArgs e)
        {
            try
            {
                if (isWalkAroundActive)
                {
                    try { ExitWalkAround(); } catch { }
                }
                if (isFirstPersonActive)
                {
                    try { ExitFirstPerson(); } catch { }
                }
                World.RenderingCamera = null;
                if (walkAroundCam != null && walkAroundCam.Exists()) walkAroundCam.Delete();
                if (firstPersonCam != null && firstPersonCam.Exists()) firstPersonCam.Delete();
                Function.Call(Hash.DISPLAY_RADAR, true);
                Function.Call(Hash.SET_PLAYER_CAN_DO_DRIVE_BY, Game.Player, true);
            }
            catch { }
            // Un-patch the steering auto-center stores so a reload/unload leaves the EXE in its original state.
            try { SteeringFix.Restore(); } catch { }
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
            // LemonUI mouse OFF: its click activates the highlighted item, but hover-to-highlight doesn't track the
            // cursor over ELSC's custom-drawn menu, so a click anywhere fired the wrong item. Keyboard/controller only.
            menu.UseMouse = false;
            menu.CloseOnInvalidClick = false;  // (moot with mouse off) never let a stray click close the menu
            menuPool.Add(menu);
            allSubMenus.Add(menu);   // tracked so rebuild can hide+remove every submenu (no stale/overlapping menus)

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
            mainMenu.UseMouse = false;
            mainMenu.CloseOnInvalidClick = false;
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

        // Capture + clear the visible menu's selected-item native description before LemonUI's Process() draws
        // it (so the un-offset native description never shows). We redraw it scroll-aware in DrawCustomBanner.
        private void PreClearVisibleDescription()
        {
            foreach (var obj in menuPool)
            {
                if (obj is NativeMenu menu && menu.Visible)
                {
                    if (menu.SelectedIndex >= 0 && menu.SelectedIndex < menu.Items.Count)
                        GetItemDescription(menu.Items[menu.SelectedIndex] as NativeItem);
                    break;
                }
            }
        }

        // ---- Bottom button-hint bar (context-sensitive; real device glyphs that auto-switch Xbox/PS/keyboard) ----
        private LemonUI.Scaleform.InstructionalButtons hintBar;
        private string hintBarSig = "";   // rebuild the scaleform only when the hint set changes (not every frame)
        private LemonUI.Elements.ScaledText editKeyHint;   // yellow keyboard-hotkey strip shown in edit mode

        private LemonUI.Scaleform.InstructionalButton HintBtn(string text, int control)
            => new LemonUI.Scaleform.InstructionalButton(text, (GTA.Control)control);

        /// <summary>Build + draw the context-sensitive hint bar. menu = the visible menu (null in walk-around).</summary>
        private void DrawHintBar(NativeMenu visibleMenu, bool walkAround)
        {
            // Build the menu hint-button set, then draw it on our OWN instanced scaleform at GFX order 7
            // (same technique as the walk-around bar) so carmod_shop's shared bar can't overwrite it.
            bool editing = editModeActive;
            bool onHorn = hornMenu != null && visibleMenu == hornMenu;
            bool onNos = nosMenu != null && visibleMenu == nosMenu;
            // On a package row (Packages menu, highlighted item is a saved package): offer X = Delete.
            bool onPackage = packagesMenu != null && visibleMenu == packagesMenu
                && visibleMenu.SelectedItem is NativeItem pkgSel && _packageItemPaths.ContainsKey(pkgSel);

            var glyphs = new System.Collections.Generic.List<string>();
            var labels = new System.Collections.Generic.List<string>();

            glyphs.Add(Glyph((int)GTA.Control.FrontendAccept)); labels.Add(editing ? "Edit Name / Price" : "Select");
            glyphs.Add(Glyph((int)GTA.Control.FrontendCancel)); labels.Add("Back");
            if (onHorn) { glyphs.Add(Glyph(ModSettings.HornPreviewButton)); labels.Add("Preview"); }
            if (onNos) { glyphs.Add(Glyph(ModSettings.NosButton)); labels.Add("Preview"); }
            if (onPackage) { glyphs.Add(Glyph((int)GTA.Control.FrontendX)); labels.Add("Delete"); }
            if (ModSettings.CustomCamera) { glyphs.Add(Glyph(ModSettings.WalkAroundButton)); labels.Add(isFirstPersonActive ? "Camera: First-Person" : "Camera"); }

            DrawHintsOwn(glyphs.ToArray(), labels.ToArray());

            // Edit mode keyboard hotkeys (keyboard-only, no controller glyph) — a tidy yellow strip above the bar.
            if (editModeActive && !walkAround)
            {
                string txt = $"~y~EDIT MODE   ~w~{ModSettings.EditModeKey}~y~ Exit    ~w~{ModSettings.RenameCategoryKey}~y~ Rename Category    ~w~{ModSettings.DeleteCategoryKey}~y~ Delete / Restore";
                var pos = new System.Drawing.PointF(1080f * GTA.UI.Screen.AspectRatio / 2f, 962f);   // center, just above the hint bar
                if (editKeyHint == null)
                {
                    editKeyHint = new LemonUI.Elements.ScaledText(pos, txt, 0.32f)
                    { Alignment = GTA.UI.Alignment.Center, Color = System.Drawing.Color.White, Outline = true };
                }
                else { editKeyHint.Text = txt; editKeyHint.Position = pos; }
                editKeyHint.Draw();
            }
        }

        // ── Walk-around hint bar on our OWN instanced scaleform ──────────────────────────────
        // Inside the vanilla LSC, carmod_shop owns the SHARED "instructional_buttons" movie and
        // overwrites LemonUI's hint bar (same handle → last SET_DATA_SLOT wins → flicker). We
        // request a SEPARATE instance so our content can't be clobbered, then draw it at GFX
        // order 7 so it renders on top of the game's bar. Fully self-managed, no LemonUI.
        private int _hintSf = -1;   // one instance, reused by the menu + walk-around hint bars (mutually exclusive)

        // Glyph token(s) for a control as shown in instructional-button boxes (current input device).
        private string Glyph(int control)
            => Function.Call<string>(Hash.GET_CONTROL_INSTRUCTIONAL_BUTTONS_STRING, 2, control, true);

        // Draw a button list (pre-built glyph strings; a slot may concatenate two glyphs, e.g. LB+RB) on OUR
        // instanced scaleform at GFX order 7 (on top of carmod_shop's bar).
        private void DrawHintsOwn(string[] glyphs, string[] labels)
        {
            if (_hintSf <= 0)
            {
                _hintSf = Function.Call<int>(Hash.REQUEST_SCALEFORM_MOVIE_INSTANCE, "instructional_buttons");
                return;   // give it a frame to load
            }
            if (!Function.Call<bool>(Hash.HAS_SCALEFORM_MOVIE_LOADED, _hintSf)) return;

            int sf = _hintSf;

            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, sf, "CLEAR_ALL");
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);

            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, sf, "TOGGLE_MOUSE_BUTTONS");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_BOOL, false);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);

            for (int i = 0; i < glyphs.Length; i++)
                SfButton(sf, i, glyphs[i], labels[i]);

            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, sf, "DRAW_INSTRUCTIONAL_BUTTONS");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, 0);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);

            // Render on top of carmod_shop's bar (layer 7), then restore the default layer.
            Function.Call(Hash.SET_SCRIPT_GFX_DRAW_ORDER, 7);
            Function.Call(Hash.DRAW_SCALEFORM_MOVIE_FULLSCREEN, sf, 255, 255, 255, 255, 0);
            Function.Call(Hash.SET_SCRIPT_GFX_DRAW_ORDER, 0);
        }

        private void DrawWalkAroundHints()
        {
            // Rev is RT on controller (VehicleAccelerate) but the keyboard 'R' key (W moves now), so the glyph is
            // device-specific — the VehicleAccelerate glyph would wrongly show "W" on keyboard.
            bool kbm = Game.LastInputMethod == InputMethod.MouseAndKeyboard;
            string revGlyph = kbm ? "R" : Glyph((int)GTA.Control.VehicleAccelerate);

            // "Cycle View" shows BOTH bumpers (LB prev / RB next) as two adjacent slots — concatenating the
            // two control-glyph blobs into one slot renders blank, so each gets its own slot, label on RB.
            // Steering hint (D-pad Left/Right) only when the inspect-steering feature is enabled.
            if (ModSettings.KeepSteeringAngle)
            {
                DrawHintsOwn(
                    new[] { Glyph(ModSettings.CamPrevButton),
                            Glyph(ModSettings.CamNextButton),
                            revGlyph,
                            Glyph((int)GTA.Control.VehicleMoveLeftRight),
                            Glyph((int)GTA.Control.LookLeftRight),
                            Glyph((int)GTA.Control.FrontendLeft),
                            Glyph((int)GTA.Control.FrontendRight),
                            Glyph(ModSettings.DoorButton),
                            Glyph(ModSettings.WalkAroundButton) },
                    new[] { "", "Cycle View", "Rev", "Move", "Look / Height", "", "Turn Wheels", "Open Door", "Next Cam" });
                return;
            }

            DrawHintsOwn(
                new[] { Glyph(ModSettings.CamPrevButton),
                        Glyph(ModSettings.CamNextButton),
                        revGlyph,
                        Glyph((int)GTA.Control.VehicleMoveLeftRight),
                        Glyph((int)GTA.Control.LookLeftRight),
                        Glyph(ModSettings.DoorButton),
                        Glyph(ModSettings.WalkAroundButton) },
                new[] { "", "Cycle View", "Rev", "Move", "Look / Height", "Open Door", "Next Cam" });
        }

        private void SfButton(int sf, int index, string glyph, string label)
        {
            Function.Call(Hash.BEGIN_SCALEFORM_MOVIE_METHOD, sf, "SET_DATA_SLOT");
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_INT, index);
            Function.Call(Hash.SCALEFORM_MOVIE_METHOD_ADD_PARAM_PLAYER_NAME_STRING, glyph);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_SCALEFORM_STRING, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, label);
            Function.Call(Hash.END_TEXT_COMMAND_SCALEFORM_STRING);
            Function.Call(Hash.END_SCALEFORM_MOVIE_METHOD);
        }

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

            // Context-sensitive button-hint bar at the bottom of the screen — drawn LAST because DrawHintsOwn
            // sets the script GFX draw order to 7 (to sit on top of carmod_shop's bar) and resets it to 0.
            // Doing it before the icons/stats would draw THEM at order 0 too, dimming them (the greyed-icon bug).
            // In walk-around, UpdateWalkAround draws its own hint bar; drawing one here too would flip-flop the
            // scaleform signature every frame.
            // Skip the hint bar while the idle cinematic is active — its instructional-button scaleform draws above a
            // plain DRAW_RECT, so it would poke through the fake-fade overlay.
            if (!isWalkAroundActive && _idlePhase == 0) DrawHintBar(visibleMenu, false);

            // Note: Menu position adjustment is handled in OnTick for MenuPosition debug mode
            // with element-specific controls (see switch on selectedUIElement)
        }

        /// <summary>
        /// Draw dark overlay with text for manual transmission key binding
        /// </summary>
        private void DrawMTBindingOverlay()
        {
            if (mtBindingState <= 0 && nosBindingState <= 0) return;
            bool nosBind = nosBindingState > 0;

            // Draw semi-transparent dark background covering most of screen
            Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1.0f, 1.0f, 0, 0, 0, 200);

            // Draw title text
            string title = nosBind ? "NITROUS SETUP" : "MANUAL TRANSMISSION SETUP";
            Function.Call(Hash.SET_TEXT_FONT, 4); // Pricedown font
            Function.Call(Hash.SET_TEXT_SCALE, 0.0f, 0.8f);
            Function.Call(Hash.SET_TEXT_COLOUR, 255, 255, 255, 255);
            Function.Call(Hash.SET_TEXT_CENTRE, true);
            Function.Call(Hash.SET_TEXT_OUTLINE);
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, title);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, 0.5f, 0.35f);

            // Draw instruction text
            string instruction = nosBind
                ? "Press a key or button to SPRAY NITROUS"
                : (mtBindingState == 1 ? "Press a key or button for SHIFT UP" : "Press a key or button for SHIFT DOWN");

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
            float itemHeight = LEMON_ITEM_HEIGHT; // matches LemonUI exactly (fallback only; we use lastPosition)

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
                    // Prefer LemonUI's EXACT drawn position (no drift); fall back to the row estimate.
                    float itemY = TryGetItemCenterY(item, lemonYBase, out float exactY)
                        ? exactY
                        : firstItemY + (i * itemHeight / lemonYBase);
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

            // Resolve the text to draw via the SINGLE renderer so it is always positioned below the scroll
            // arrows: a transient "not enough cash" message overrides; otherwise the item's own description
            // (GetItemDescription captures + clears the native one so LemonUI doesn't draw it un-offset).
            string itemDesc = GetItemDescription(selectedItem);
            string description = showingNotEnoughCash ? "Sorry - you cannot afford this item." : itemDesc;

            // If we have a description OR debugging description, draw our custom box
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

        // ---- Keep the user where they were across a menu rebuild ----------------------------------------
        // A rebuild throws away every NativeMenu and makes new ones, so we can't keep object references.
        // Instead we snapshot the OPEN menu's subtitle (stable identifier) + highlighted index before the
        // rebuild, then re-open the matching new menu at that index afterwards. Entering/leaving edit mode,
        // editing names/prices, deleting a category, etc. all go through RebuildMenusForVehicle, so they all
        // stay put instead of bouncing back to the root category list.
        private string restoreMenuSubtitle = null;
        private int restoreMenuIndex = -1;
        private bool restorePending = false;

        // Snapshot the currently-open menu. indexOverride = -2 keeps the live highlight; pass an explicit
        // index to land somewhere else (e.g. deleting a category -> highlight shifts up one).
        private void CaptureMenuPosition(int indexOverride = -2)
        {
            restoreMenuSubtitle = null; restoreMenuIndex = -1; restorePending = true;
            NativeMenu open = (mainMenu != null && mainMenu.Visible) ? mainMenu : null;
            if (open == null)
            {
                foreach (var sm in allSubMenus)
                {
                    try { if (sm.Visible) { open = sm; break; } } catch { }
                }
            }
            if (open == null) return;   // nothing open (e.g. menu being opened fresh) -> no restore, leave display alone
            restoreMenuSubtitle = open.Name;
            restoreMenuIndex = indexOverride == -2 ? open.SelectedIndex : indexOverride;
        }

        private void RestoreMenuPosition()
        {
            string wantSub = restoreMenuSubtitle; int wantIdx = restoreMenuIndex;
            restoreMenuSubtitle = null; restoreMenuIndex = -1; restorePending = false;
            if (wantSub == null) return;   // nothing was open -> don't force any menu visible

            NativeMenu target = (mainMenu != null && mainMenu.Name == wantSub) ? mainMenu : null;
            if (target == null)
            {
                foreach (var sm in allSubMenus)
                {
                    try { if (sm.Name == wantSub) { target = sm; break; } } catch { }
                }
            }
            if (target == null) { if (mainMenu != null) mainMenu.Visible = true; return; }   // menu gone -> fall back to root

            isNavigatingMenu = true;
            if (mainMenu != null && target != mainMenu) mainMenu.Visible = false;
            target.Visible = true;
            if (target.Items.Count > 0)
            {
                int idx = wantIdx < 0 ? 0 : (wantIdx > target.Items.Count - 1 ? target.Items.Count - 1 : wantIdx);
                target.SelectedIndex = idx;
            }
            isNavigatingMenu = false;
        }

        private void RebuildMenusForVehicle()
        {
            if (currentVehicle == null) { restorePending = false; restoreMenuSubtitle = null; restoreMenuIndex = -1; return; }
            if (!restorePending) CaptureMenuPosition();   // auto-snapshot unless a caller already set an override

            // CRITICAL: Install mod kit before any mod operations
            // This is REQUIRED for SET_VEHICLE_MOD to work properly
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);

            // Set current vehicle for MenuConfig (used for vehicle-specific overrides)
            // Resolve the per-vehicle config folder by model name (stable) or display name (back-compat).
            MenuConfig.SetCurrentVehicle(currentVehicle.DisplayName, unchecked((uint)currentVehicle.Model.Hash));

            // Load + re-assert this vehicle's saved engine swap (EnginePowerMultiplier/forced audio reset when
            // the entity restreams, so re-apply whenever we rebuild the menu for this car).
            activeEngineSwap = EngineSwaps.Lookup(VehicleSaveData.GetEngineSwapId(VehicleKey(currentVehicle)));
            if (activeEngineSwap != null) ApplyEngineSwap(activeEngineSwap);

            // Capture factory handling (once per model) + load/apply this vehicle's saved handling tune.
            if (ModSettings.VehicleTuning) InitTuningForVehicle();

            try
            {
                // Hide + remove EVERY submenu first so no stale menu lingers (was overlapping after edit-mode
                // rebuilds, e.g. the old Wheel Type menu staying drawn behind the new one).
                foreach (var sm in allSubMenus)
                {
                    try { sm.Visible = false; } catch { }
                    menuPool.Remove(sm);
                }
                allSubMenus.Clear();

                // Clear main menu
                mainMenu.Clear();

                // Clear dynamic menus
                foreach (var menu in modMenusByIndex.Values)
                {
                    menuPool.Remove(menu);
                }
                modMenusByIndex.Clear();
                itemOwnershipStatus.Clear();
                customDescriptions.Clear();

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

                // Clear any part-picker menus left from edit mode
                foreach (var menu in partPickerMenus)
                {
                    menuPool.Remove(menu);
                }
                partPickerMenus.Clear();
                mainCatItems.Clear();

                // Clear universal wheel-category menus
                foreach (var menu in universalWheelMenus)
                {
                    menuPool.Remove(menu);
                }
                universalWheelMenus.Clear();

                // Build menu with grouped categories like native LSC
                BuildGroupedMainMenu();

                // Add any custom user-created categories from folders
                BuildDynamicMainMenu();

                Log($"Built menu with {mainMenu.Items.Count} categories for {currentVehicle.DisplayName}");

                // Re-open the menu the user was on (if any) at the same spot, instead of dumping to the root.
                RestoreMenuPosition();
            }
            catch (Exception ex)
            {
                Log($"ERROR in RebuildMenusForVehicle: {ex.Message}\n{ex.StackTrace}");
                restorePending = false; restoreMenuSubtitle = null; restoreMenuIndex = -1;
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
                // Per-vehicle hide of a default category. Hidden in normal play; in edit mode it still shows
                // (marked) so the modder can press Delete to restore it.
                bool hidden = MenuConfig.IsCategoryHidden(title);
                if (hidden && !editModeActive) return;

                // Apply this vehicle's rename override for built-in categories (e.g. Skirts -> Default Skirts).
                string display = MenuConfig.GetCategoryRename(title) ?? title;
                var item = new NativeItem(display);
                mainCatItems[item] = (title, false);   // key = ORIGINAL title so rename keeps working after override
                item.AltTitle = hidden ? "~c~(hidden)" : ">>";
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

            // Count of parts STILL visible in this slot (i.e. not re-shelved into a custom category). When this
            // hits 0, the built-in category is hidden entirely — "remove all parts -> category disappears".
            int VisibleModCount(int modIndex)
            {
                int total = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, modIndex);
                int vis = 0;
                for (int i = 0; i < total; i++) if (!MenuConfig.IsPartHidden(modIndex, i)) vis++;
                return vis;
            }

            // Customize Radio: no in-menu toggle — it follows the game's Music volume (turn music down in GTA
            // settings to quiet/mute it). Just rescan here (when not already playing) so files added before
            // opening the menu are picked up.
            try { if (customizeRadio != null && !customizeRadio.IsActive) customizeRadio.Rescan(); } catch { }

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
            int frontBumperVis = VisibleModCount(1);
            int rearBumperVis = VisibleModCount(2);
            if (frontBumperVis > 0 || rearBumperVis > 0)
            {
                var bumpersMenu = CreateMenu("Bumpers");

                if (frontBumperVis > 0)
                {
                    var frontMenu = CreateModMenuByIndex(1, frontBumperCount, "Front Bumper");
                    frontMenu.Closed += (s, e) => { if (!isNavigatingMenu) bumpersMenu.Visible = true; };
                    var frontItem = new NativeItem(SlotName(1, "Front Bumper"));
                    frontItem.AltTitle = ">>";
                    frontItem.Activated += (s, e) => { isNavigatingMenu = true; bumpersMenu.Visible = false; frontMenu.Visible = true; isNavigatingMenu = false; };
                    bumpersMenu.Add(frontItem);
                }
                if (rearBumperVis > 0)
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

            // Vehicle Tuning (live handling fine-tune; Side Course feature, toggleable in INI)
            if (ModSettings.VehicleTuning && WheelFitment.WheelMemory.HasHandling(currentVehicle))
            {
                var tm = CreateTuningMenu();
                AddSubmenuItem("Vehicle Tuning", tm);
            }

            // Exhaust (4)
            if (VisibleModCount(4) > 0)
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
            if (VisibleModCount(8) > 0)
            {
                // Create fender menu directly with left fender mods (most common case)
                var menu = CreateModMenuByIndex(8, leftFenderCount, "FENDERS");
                AddSubmenuItem(SlotName(8, "Fender"), menu);
            }
            // Right fender is rare - add as separate category if it exists
            if (VisibleModCount(9) > 0)
            {
                var menu = CreateModMenuByIndex(9, rightFenderCount, "FENDERS (RIGHT)");
                AddSubmenuItem(SlotName(9, "Fender (Right)"), menu);
            }

            // Grille (6)
            if (VisibleModCount(6) > 0)
            {
                var menu = CreateModMenuByIndex(6, GetModCount(6), "Grille");
                AddSubmenuItem(SlotName(6, "Grille"), menu);
            }

            // Hood (7)
            if (VisibleModCount(7) > 0)
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
                stockLightsItem.Activated += (s, e) => { if (EditBlockApply()) return; SetXenon(false); };
                headlightsMenu.Add(stockLightsItem);

                var xenonLightsItem = new NativeItem("Xenon Lights");
                bool xenonOwned = VehicleSaveData.IsXenonOwned(VehicleKey(currentVehicle));
                if (hasXenon)
                {
                    xenonLightsItem.AltTitle = "";
                    itemOwnershipStatus[xenonLightsItem] = STATUS_INSTALLED;
                    if (!xenonOwned) VehicleSaveData.SetXenonOwned(VehicleKey(currentVehicle));
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
                    bool owned = VehicleSaveData.IsXenonOwned(VehicleKey(currentVehicle));
                    if (!TryPurchase(ModPricing.XenonLightsPrice, owned)) return;

                    SetXenon(true);
                    if (!owned)
                    {
                        VehicleSaveData.SetXenonOwned(VehicleKey(currentVehicle));
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
                        if (EditBlockApply()) return;
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

            // Speedometer (always available — it's a HUD, not a per-mod category). Wired manually so the
            // preview follows the highlighted style on open/scroll.
            {
                var speedoMenu = CreateSpeedometerMenu();
                speedoMenu.Closed += (s, e) => { if (!isNavigatingMenu) { mainMenu.Visible = true; Speedo.Preview = null; } };
                var speedoNav = new NativeItem("Speedometer") { AltTitle = ">>" };
                speedoNav.Activated += (s, e) => { isNavigatingMenu = true; mainMenu.Visible = false; Speedo.Preview = Speedo.Active; speedoMenu.Visible = true; isNavigatingMenu = false; };
                mainMenu.Add(speedoNav);
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
                packagesMenu = CreatePackagesMenu();
                AddSubmenuItem("Packages", packagesMenu);
            }

            // Respray
            resprayMenu = BuildResprayMenu();
            AddSubmenuItem("Respray", resprayMenu);

            // Roll Cage (5)
            if (VisibleModCount(5) > 0)
            {
                var menu = CreateModMenuByIndex(5, GetModCount(5), "Roll Cage");
                AddSubmenuItem(SlotName(5, "Roll Cage"), menu);
            }

            // Roof (10)
            if (VisibleModCount(10) > 0)
            {
                var menu = CreateModMenuByIndex(10, GetModCount(10), "Roof");
                AddSubmenuItem(SlotName(10, "Roof"), menu);
            }

            // Skirts (3)
            if (VisibleModCount(3) > 0)
            {
                var menu = CreateModMenuByIndex(3, GetModCount(3), "Skirts");
                AddSubmenuItem(SlotName(3, "Skirts"), menu);
            }

            // Spoiler (0)
            if (VisibleModCount(0) > 0)
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
                    // Three states: NOT owned -> price; owned but NOT the active suspension -> owned tick +
                    // "Select to equip"; owned AND equipped -> equipped garage icon + "Select to edit". This makes
                    // the equip mutually exclusive with the vanilla levels (only one shows the garage icon).
                    void RefreshSuspNav()
                    {
                        string vn = VehicleKey(currentVehicle);
                        bool owned = VehicleSaveData.IsWheelFitmentOwned(vn);
                        bool equipped = owned && VehicleSaveData.IsCustomSuspensionEquipped(vn);
                        if (!owned)
                        {
                            itemOwnershipStatus.Remove(alignNav);
                            alignNav.AltTitle = $"${ModPricing.CustomSuspensionPrice}";
                            alignNav.Description = FitmentSummary();
                        }
                        else if (equipped)
                        {
                            alignNav.AltTitle = "";
                            itemOwnershipStatus[alignNav] = STATUS_INSTALLED;
                            alignNav.Description = FitmentSummary() + "  ·  Select to edit";
                        }
                        else
                        {
                            alignNav.AltTitle = "";
                            itemOwnershipStatus[alignNav] = STATUS_OWNED;
                            alignNav.Description = "Select to equip your custom suspension setup.";
                        }
                    }
                    RefreshSuspNav();
                    // Refresh the saved-value summary + owned/equipped state each time the Suspension menu opens.
                    menu.Shown += (s, e) => { RefreshSuspNav(); _hoveringCustomSuspension = false; };
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
                    // Buy once per car to unlock the camber/ride-height sliders (also unlocks the Tire Grip /
                    // Steering Lock tuning sliders). Rebuild fresh on open so it reflects wheels / pro-mode state.
                    // Double-select: first select buys-and-equips (or, if already owned, equips); only once it's
                    // the equipped suspension does a select open the editor. Equipping rebuilds the menu so the
                    // garage icon moves to Custom and off whatever vanilla level was equipped.
                    alignNav.Activated += (s, e) =>
                    {
                        if (currentVehicle == null) return;
                        string vn = VehicleKey(currentVehicle);
                        bool owned = VehicleSaveData.IsWheelFitmentOwned(vn);
                        bool equipped = owned && VehicleSaveData.IsCustomSuspensionEquipped(vn);

                        if (!owned)
                        {
                            if (!TryPurchase(ModPricing.CustomSuspensionPrice, false)) return;
                            VehicleSaveData.SetWheelFitmentOwned(vn, true);
                            EquipCustomSuspension();   // buys + equips; select again to edit
                            return;
                        }
                        if (!equipped)
                        {
                            EquipCustomSuspension();   // equip; select again to edit
                            return;
                        }
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
                bool turboOwned = VehicleSaveData.IsTurboOwned(VehicleKey(currentVehicle));

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
                noneItem.Activated += (s, e) => { if (EditBlockApply()) return; SetTurbo(false); };
                turboMenu.Add(noneItem);

                var turboTuningItem = new NativeItem("Turbo Tuning");
                if (hasTurbo)
                {
                    turboTuningItem.AltTitle = "";
                    itemOwnershipStatus[turboTuningItem] = STATUS_INSTALLED;
                    if (!turboOwned) VehicleSaveData.SetTurboOwned(VehicleKey(currentVehicle));
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
                    bool owned = VehicleSaveData.IsTurboOwned(VehicleKey(currentVehicle));
                    if (!TryPurchase(ModPricing.TurboPrice, owned)) return;

                    SetTurbo(true);
                    if (!owned)
                    {
                        VehicleSaveData.SetTurboOwned(VehicleKey(currentVehicle));
                        VehicleSaveData.Save();
                    }
                };
                turboMenu.Add(turboTuningItem);

                // Nitrous FX — each tier (NOS 1-4) is bought here, rising in cost. Buying any tier installs NOS
                // and (first time) prompts for the spray button. Hold the NOS button while open to preview.
                {
                    var nosFxMenu = CreateNosColorMenu();
                    nosMenu = nosFxMenu;   // for the context "Preview" hint in the hint bar
                    var nosFxNav = new NativeItem("Nitrous (NOS)");
                    nosFxNav.Description = "Buy & equip a nitrous tier (NOS 1-4), then hold Preview to see it on the car.";
                    // Equipped (a tier active) -> garage icon; owned but Off -> owned tick; nothing owned -> ">>".
                    void RefreshNosNav()
                    {
                        string vn = VehicleKey(currentVehicle);
                        int eq = VehicleSaveData.GetNosEquippedTier(vn);
                        int ownedMask = VehicleSaveData.GetNosOwnedTiers(vn);
                        if (eq >= 0) { nosFxNav.AltTitle = ""; itemOwnershipStatus[nosFxNav] = STATUS_INSTALLED; }
                        else if (ownedMask != 0) { nosFxNav.AltTitle = ""; itemOwnershipStatus[nosFxNav] = STATUS_OWNED; }
                        else { nosFxNav.AltTitle = ">>"; itemOwnershipStatus.Remove(nosFxNav); }
                    }
                    RefreshNosNav();
                    nosFxMenu.Closed += (s, e) => { if (!isNavigatingMenu) { turboMenu.Visible = true; nosColorMenuOpen = false; elscTransmission?.StopExhaustFlames(); RefreshNosNav(); } };
                    nosFxNav.Activated += (s, e) => { isNavigatingMenu = true; turboMenu.Visible = false; nosColorMenuOpen = true; nosFxMenu.Visible = true; isNavigatingMenu = false; };
                    turboMenu.Add(nosFxNav);
                }

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
                // VisibleModCount (not raw count): when every part in this slot is re-shelved into a custom
                // category, the built-in one would show only "Stock" — so hide it, same as slots 0-10.
                if (VisibleModCount(mi) <= 0) continue;
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
                if (EditBlockApply()) return;
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, -1, false); // front -> stock
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, -1, false); // back -> stock
                    ShowNotification("~g~Reverted to stock wheels");
                }
            };
            wheelTypeMenu.Add(stockWheelsItem);

            // Apply-To axle selector — staggered front/rear wheels. ONLY motorcycles have a real separate rear
            // wheel slot (mod 24); cars share one slot (mod 23 = all four), so on a car "Rear Only" would change
            // nothing. So the selector is bike-only, and the axle resets to Front+Rear for everything else.
            wheelApplyAxle = 0;
            if (IsBike(currentVehicle))
            {
                var axleItem = new NativeListItem<string>("Apply To", "Front + Rear", "Front Only", "Rear Only")
                {
                    SelectedIndex = wheelApplyAxle,
                    Description = "Choose which wheel a design applies to (for staggered front/rear setups)"
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

            // Universal custom wheel categories live INSIDE Wheel Type (shipped by wheel packs; same on every car).
            foreach (var wcat in MenuConfig.GetUniversalWheelCategories())
            {
                var wcMenu = BuildWheelCategoryMenu(wcat, wheelTypeMenu);
                var nav = new NativeItem(wcat.DisplayName) { AltTitle = ">>" };
                if (!string.IsNullOrEmpty(wcat.Description)) nav.Description = wcat.Description;
                var capMenu = wcMenu;
                nav.Activated += (s, e) => { isNavigatingMenu = true; wheelTypeMenu.Visible = false; capMenu.Visible = true; isNavigatingMenu = false; };
                wheelTypeMenu.Add(nav);
            }
            if (editModeActive)
            {
                var addWheelCat = new NativeItem("~y~+ Add a Wheel Category") { AltTitle = "EDIT" };
                addWheelCat.Description = "Create a custom wheel category (applies to every car).";
                addWheelCat.Activated += (s, e) => StartAddWheelCategory();
                wheelTypeMenu.Add(addWheelCat);
            }

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

            // Tire Design submenu - the aftermarket tire profile (SET_VEHICLE_MOD "variation" flag): Stock vs
            // Custom. Priced + ownership-tracked like the other tire categories (replaces the old dead nav item).
            var tireDesignMenu = CreateTireDesignMenu();
            tireDesignMenu.Closed += (s, e) => { if (!isNavigatingMenu) tiresMenu.Visible = true; };
            var tireDesignNavItem = new NativeItem("Tire Design");
            tireDesignNavItem.AltTitle = ">>";
            tireDesignNavItem.Activated += (s, e) => { isNavigatingMenu = true; tiresMenu.Visible = false; tireDesignMenu.Visible = true; isNavigatingMenu = false; };
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
                string vehName = VehicleKey(currentVehicle);
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
                // A vanilla suspension (Stock or a level) is now the active suspension, so Custom is no longer
                // equipped (it stays OWNED). Rebuild so the equipped icon moves off Custom onto the vanilla pick.
                if (currentVehicle != null && currentVehicle.Exists() &&
                    VehicleSaveData.IsCustomSuspensionEquipped(VehicleKey(currentVehicle)))
                {
                    VehicleSaveData.SetCustomSuspensionEquipped(VehicleKey(currentVehicle), false);
                    VehicleSaveData.Save();
                    CaptureMenuPosition();
                    RebuildMenusForVehicle();
                }

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

        // A vanilla transmission (Stock or a level) was chosen, so Manual Transmission is no longer the active
        // transmission (it stays OWNED). Disable it + rebuild so the equipped icon moves off MT onto the pick.
        private void OnGameTransmissionChosen()
        {
            try
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (!VehicleSaveData.IsManualTransmissionEquipped(VehicleKey(currentVehicle))) return;
                elscTransmission.Disable();
                VehicleSaveData.SetManualTransmissionEquipped(VehicleKey(currentVehicle), false);
                VehicleSaveData.Save();
                CaptureMenuPosition();
                RebuildMenusForVehicle();
            }
            catch { }
        }

        // Equip Manual Transmission as the ACTIVE transmission: drop any vanilla level, flag it equipped, enable
        // manual shifting, then rebuild so the equipped icon lands on MT (vanilla levels drop to owned-tick).
        private void EquipManualTransmission()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            string vn = VehicleKey(currentVehicle);
            isPreviewingMod = false; previewModIndex = -1;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.REMOVE_VEHICLE_MOD, currentVehicle, 13);   // drop vanilla transmission (mutually exclusive)
            previewOriginalValue = -1;
            VehicleSaveData.SetManualTransmissionOwned(vn, true);
            VehicleSaveData.SetManualTransmissionEquipped(vn, true);
            VehicleSaveData.Save();
            elscTransmission.SetVehicle(currentVehicle);
            elscTransmission.Enable();
            // Manual Transmission needs a gauge (gear/RPM readout) — turn one on for THIS car if it has none.
            if (VehicleSaveData.GetSpeedoStyle(vn) == 0)
            {
                VehicleSaveData.SetSpeedoStyle(vn, (int)SpeedoStyle.Simple);
                Speedo.Active = SpeedoStyle.Simple;
                ShowNotification("~g~Speedometer enabled for Manual Transmission");
            }
            ShowNotification("~g~Manual Transmission equipped~w~ — select again to edit keys");
            CaptureMenuPosition();
            RebuildMenusForVehicle();
        }

        // Equip Custom Suspension & Camber as the ACTIVE suspension: drop any vanilla level, flag it equipped,
        // re-apply the saved custom ride-height/camber, then rebuild so the equipped icon lands on Custom.
        private void EquipCustomSuspension()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            string vn = VehicleKey(currentVehicle);
            isPreviewingMod = false; previewModIndex = -1;   // don't let a menu-close revert undo the removal
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.REMOVE_VEHICLE_MOD, currentVehicle, 15);   // strip vanilla Suspension lowering
            previewOriginalValue = -1;
            VehicleSaveData.SetCustomSuspensionEquipped(vn, true);
            VehicleSaveData.Save();
            if (wheelFitment.IsInitialized) { wheelFitment.SuspendRideHeight = false; wheelFitment.ApplyRideHeightNow(); }
            ShowNotification("~g~Custom Suspension equipped~w~ — select again to edit");
            CaptureMenuPosition();
            RebuildMenusForVehicle();
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
            VehicleSaveData.SetFitmentData(VehicleKey(currentVehicle), fitmentData);
            // Mark "owned" so InitFitmentForVehicle re-loads this on re-entry (the purchase gate that used
            // to set this was removed — fitment is now free, and saving is what flags a car as customised).
            VehicleSaveData.SetWheelFitmentOwned(VehicleKey(currentVehicle), true);
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

            // Items to show = the configured items, plus an automatic "None" when this category controls a
            // single game slot (so the stock/remove option lives right here with the parts — universal, any mod).
            var displayItems = DisplayItemsFor(category);

            // Live preview + installed icon + pricing so custom categories behave like the built-in ones.
            // Items can span multiple game slots, so snapshot each slot's value on open and revert on close;
            // a purchase updates that baseline so the chosen part sticks.
            var previewOriginals = new Dictionary<int, int>();
            string vehicleName = VehicleKey(currentVehicle);

            menu.Shown += (s, e) =>
            {
                previewOriginals.Clear();
                if (currentVehicle != null && currentVehicle.Exists())
                    foreach (var it in displayItems)
                        if (it.SourceModType >= 0 && !previewOriginals.ContainsKey(it.SourceModType))
                            previewOriginals[it.SourceModType] = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, it.SourceModType);
            };

            menu.SelectedIndexChanged += (s, e) =>
            {
                if (e.Index >= 0 && e.Index < displayItems.Count && currentVehicle != null && currentVehicle.Exists())
                {
                    var it = displayItems[e.Index];
                    if (it.SourceModType >= 0)
                    {
                        Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                        Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, it.SourceModType, it.Value, false);
                    }
                }
            };

            menu.Closed += (s, e) =>
            {
                // Revert any previewed slots to their (possibly purchase-updated) baseline.
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    foreach (var kv in previewOriginals)
                        Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, kv.Key, kv.Value, false);
                }
                if (!isNavigatingMenu && parentMenu != null)
                    parentMenu.Visible = true;
            };

            // Add items if the category has any
            foreach (var item in displayItems)
            {
                var capturedItem = item;
                var menuItem = new NativeItem(item.Name);
                if (!string.IsNullOrEmpty(item.Description)) menuItem.Description = item.Description;

                bool installed = capturedItem.SourceModType >= 0 && currentVehicle != null && currentVehicle.Exists()
                                 && Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, capturedItem.SourceModType) == capturedItem.Value;
                bool owned = installed || (capturedItem.SourceModType >= 0 && VehicleSaveData.IsModOwned(vehicleName, capturedItem.SourceModType, capturedItem.Value));

                if (installed) { menuItem.AltTitle = ""; itemOwnershipStatus[menuItem] = STATUS_INSTALLED; }
                else if (owned) { menuItem.AltTitle = ""; itemOwnershipStatus[menuItem] = STATUS_OWNED; }
                else menuItem.AltTitle = capturedItem.Price > 0 ? $"${capturedItem.Price:N0}" : "Free";

                menuItem.Activated += (s, e) =>
                {
                    if (editModeActive)
                    {
                        int ei = category.Items.IndexOf(capturedItem);
                        if (ei >= 0) StartEditItem(category.Path, ei, false, capturedItem.Name, capturedItem.Price);
                        // synthetic "None" (ei < 0) isn't editable — just ignore in edit mode
                        return;
                    }
                    if (capturedItem.SourceModType < 0 || currentVehicle == null || !currentVehicle.Exists()) return;
                    bool own = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, capturedItem.SourceModType) == capturedItem.Value
                               || VehicleSaveData.IsModOwned(VehicleKey(currentVehicle), capturedItem.SourceModType, capturedItem.Value);
                    if (!TryPurchase(capturedItem.Price, own)) return;
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, capturedItem.SourceModType, capturedItem.Value, false);
                    previewOriginals[capturedItem.SourceModType] = capturedItem.Value;   // keep it on close
                    if (!own)
                    {
                        VehicleSaveData.SetModOwned(VehicleKey(currentVehicle), capturedItem.SourceModType, capturedItem.Value);
                        VehicleSaveData.Save();
                    }
                    itemOwnershipStatus[menuItem] = STATUS_INSTALLED;
                    menuItem.AltTitle = "";
                    ShowNotification($"~g~Installed: {capturedItem.Name}");
                };

                menu.Add(menuItem);
            }

            // Add subcategory navigation items
            foreach (var subCategory in category.SubCategories)
            {
                if (!CategoryHasContent(subCategory)) continue;   // no empty sub-menus
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

            // Edit mode: full modder toolset for this custom category (add sub-category / add part / edit
            // description / rename), all per-vehicle.
            if (!string.IsNullOrEmpty(category.Path)) AttachEditTools(menu, category.Path, true);

            return menu;
        }

        /// <summary>
        /// Items to show in a custom category = the configured items, plus an automatic "None" entry when the
        /// whole category drives ONE game slot and doesn't already have a stock option. Universal: any vehicle
        /// mod's single-slot category (Fenders, Bumpers, a Body's Seat...) gets a built-in revert-to-stock.
        /// </summary>
        private List<MenuConfig.MenuItem> DisplayItemsFor(MenuConfig.Category category)
        {
            var list = new List<MenuConfig.MenuItem>();
            if (category.Items != null) list.AddRange(category.Items);

            int slot = -1; bool single = false, hasNone = false;
            if (category.Items != null)
                foreach (var it in category.Items)
                {
                    if (it.SourceModType < 0) continue;
                    if (it.Value == -1) hasNone = true;
                    if (slot == -1) { slot = it.SourceModType; single = true; }
                    else if (it.SourceModType != slot) single = false;
                }

            if (single && slot >= 0 && !hasNone)
                list.Insert(0, new MenuConfig.MenuItem
                {
                    Name = "None",
                    Description = "Remove — revert this part to stock.",
                    Price = 0, Value = -1, SourceModType = slot
                });
            return list;
        }

        /// <summary>True if a custom category has any real items, or any descendant subcategory does. Used to
        /// skip rendering empty categories/sub-menus (no clutter). Universal.</summary>
        private bool CategoryHasContent(MenuConfig.Category category)
        {
            if (category == null) return false;
            if (category.Items != null && category.Items.Count > 0) return true;
            if (category.SubCategories != null)
                foreach (var sub in category.SubCategories)
                    if (CategoryHasContent(sub)) return true;
            return false;
        }

        // The body/visual slots a modder pulls custom parts from (VehicleModType, label).
        private static readonly (int slot, string name)[] PartSlots = new[]
        {
            (0, "Spoiler"), (1, "Front Bumper"), (2, "Rear Bumper"), (3, "Skirts"), (4, "Exhaust"),
            (5, "Roll Cage"), (6, "Grille"), (7, "Hood"), (8, "Fender"), (9, "Right Fender"), (10, "Roof")
        };
        private readonly List<NativeMenu> partPickerMenus = new List<NativeMenu>();
        private string pendingPartCategory = null;
        private int pendingPartSlot = -1, pendingPartIndex = -1;
        private bool isNamingPart = false;

        /// <summary>Open the part picker: every body slot that has parts -> its parts. Selecting a part previews
        /// it on the car, then opens the keyboard to name it before adding it to <paramref name="categoryPath"/>.</summary>
        // Batch session: parts the modder checkmarked across the slot submenus, committed together on Done.
        private readonly List<(int slot, int index, string name)> batchPartChecked = new List<(int, int, string)>();

        private void StartAddPart(string categoryPath, NativeMenu returnMenu)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            batchPartChecked.Clear();

            var picker = CreateMenu("Add Parts");
            partPickerMenus.Add(picker);
            picker.Closed += (s, e) => { if (!isNavigatingMenu) returnMenu.Visible = true; };

            var doneTop = new NativeItem("~g~Done — add checked") { AltTitle = "✓" };
            doneTop.Description = "Add every checked part to this category (free, default names).";
            doneTop.Activated += (s, e) => CommitBatchParts(categoryPath);
            picker.Add(doneTop);

            foreach (var (slot, name) in PartSlots)
            {
                int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, slot);
                if (count <= 0) continue;

                var slotMenu = CreateMenu(name);
                partPickerMenus.Add(slotMenu);
                int slotCap = slot, countCap = count;
                int origForSlot = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, slot);
                slotMenu.Shown += (s, e) =>
                {
                    if (currentVehicle != null && currentVehicle.Exists() && slotMenu.SelectedIndex < countCap)
                    { Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0); Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, slotCap, slotMenu.SelectedIndex, false); }
                };
                slotMenu.SelectedIndexChanged += (s, e) =>
                {
                    if (currentVehicle != null && currentVehicle.Exists() && e.Index < countCap)
                    { Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0); Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, slotCap, e.Index, false); }   // preview
                };
                slotMenu.Closed += (s, e) =>
                {
                    if (currentVehicle != null && currentVehicle.Exists())
                        Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, slotCap, origForSlot, false);   // revert preview
                    if (!isNavigatingMenu) picker.Visible = true;
                };

                for (int i = 0; i < count; i++)
                {
                    string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, slot, i);
                    string pn = (!string.IsNullOrEmpty(label) && label != "NULL") ? Game.GetLocalizedString(label) : null;
                    if (string.IsNullOrEmpty(pn)) pn = $"{name} {i + 1}";
                    int s2 = slot, i2 = i; string nm = pn;
                    var cb = new NativeCheckboxItem(pn, batchPartChecked.Exists(x => x.slot == s2 && x.index == i2));
                    cb.CheckboxChanged += (s, e) =>
                    {
                        if (cb.Checked) { if (!batchPartChecked.Exists(x => x.slot == s2 && x.index == i2)) batchPartChecked.Add((s2, i2, nm)); }
                        else batchPartChecked.RemoveAll(x => x.slot == s2 && x.index == i2);
                    };
                    slotMenu.Add(cb);
                }
                var doneSub = new NativeItem("~g~Done — add checked") { AltTitle = "✓" };
                doneSub.Description = "Add every checked part (free, default names).";
                doneSub.Activated += (s, e) => CommitBatchParts(categoryPath);
                slotMenu.Add(doneSub);

                var nav = new NativeItem(name) { AltTitle = $"{count} >>" };
                nav.Activated += (s, e) => { isNavigatingMenu = true; picker.Visible = false; slotMenu.Visible = true; isNavigatingMenu = false; };
                picker.Add(nav);
            }

            isNavigatingMenu = true;
            returnMenu.Visible = false;
            picker.Visible = true;
            isNavigatingMenu = false;
        }

        private void CommitBatchParts(string categoryPath)
        {
            if (batchPartChecked.Count == 0) { ShowNotification("~y~Nothing checked yet."); return; }
            int added = 0;
            foreach (var (slot, idx, name) in batchPartChecked)
            {
                var item = new MenuConfig.MenuItem { Name = name, SourceModType = slot, Value = idx, Price = 0 };
                if (MenuConfig.AddItemToVehicleCategory(categoryPath, item)) added++;
            }
            batchPartChecked.Clear();
            ShowNotification($"~g~Added {added} part(s).");
            if (currentVehicle != null) RebuildMenusForVehicle();
        }

        private void BeginNamePart(string categoryPath, int slot, int index)
        {
            pendingPartCategory = categoryPath; pendingPartSlot = slot; pendingPartIndex = index;
            isNamingPart = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name this part");
        }

        private void UpdateNamePart()
        {
            if (!isNamingPart) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isNamingPart = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Part not added (empty name)."); return; }
                // Next step: ask for a price (blank/cancel = free).
                pendingPartName = result;
                isPricingPart = true; keyboardCheckCooldown = 5;
                Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 9);
                ShowNotification("~y~Set a price (leave blank for Free)");
            }
            else if (status == 2) { isNamingPart = false; }
        }

        private bool isPricingPart = false;
        private string pendingPartName = null;
        private void UpdatePricePart()
        {
            if (!isPricingPart) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1 || status == 2)   // finished or cancelled — cancel/blank = free
            {
                int price = 0;
                if (status == 1)
                {
                    string raw = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                    string digits = "";
                    foreach (char c in raw) if (char.IsDigit(c)) digits += c;
                    if (!string.IsNullOrEmpty(digits)) int.TryParse(digits, out price);
                }
                isPricingPart = false;
                var item = new MenuConfig.MenuItem { Name = pendingPartName, SourceModType = pendingPartSlot, Value = pendingPartIndex, Price = price };
                if (MenuConfig.AddItemToVehicleCategory(pendingPartCategory, item))
                {
                    ShowNotification($"~g~Added: {pendingPartName}" + (price > 0 ? $" (${price:N0})" : " (Free)"));
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~Couldn't add the part.");
            }
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
            mainCatItems[navItem] = (category.Path, true);   // custom category; rename via the folder
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
                    if (!CategoryHasContent(category)) { Log($"[Dynamic] Skipping empty category: {category.Name}"); continue; }
                    // This is a user-created custom category
                    Log($"[Dynamic] Adding custom category: {category.Name}");
                    AddDynamicCategory(category);
                }
            }

            // Edit mode: modder tools at the bottom of the root menu (Add a Category).
            AttachEditTools(mainMenu, "", false);

            // Edit mode: in-game control remapper.
            if (editModeActive)
            {
                var controlsNav = new NativeItem("CONTROLS", "Remap keys & buttons. Changes save to settings.ini.") { AltTitle = ">>" };
                var cm = BuildControlsMenu();
                controlsNav.Activated += (s, e) => { isNavigatingMenu = true; mainMenu.Visible = false; cm.Visible = true; isNavigatingMenu = false; };
                cm.Closed += (s, e) => { if (!isNavigatingMenu) mainMenu.Visible = true; };
                mainMenu.Add(controlsNav);
            }
        }

        // ================= In-game control remapper (edit mode) =================
        private bool isRebindingKey = false, isRebindingButton = false;
        private string rebindLabel = null;
        private Action<Keys> rebindKeySetter = null;
        private Action<int> rebindButtonSetter = null;
        private NativeItem rebindItem = null;
        private Func<string> rebindItemValue = null;
        private LemonUI.Elements.ScaledText rebindPrompt;

        // Friendly names for controller control indices (display only; glyphs in the hint bar are device-accurate).
        private static readonly (int idx, string name)[] ControlButtonNames = new (int, string)[]
        {
            ((int)GTA.Control.FrontendAccept, "A / Cross"),
            ((int)GTA.Control.FrontendCancel, "B / Circle"),
            ((int)GTA.Control.FrontendX,      "X / Square"),
            ((int)GTA.Control.FrontendY,      "Y / Triangle"),
            ((int)GTA.Control.VehicleExit,    "Y / Triangle"),
            ((int)GTA.Control.FrontendLb,     "LB / L1"),
            ((int)GTA.Control.FrontendRb,     "RB / R1"),
            ((int)GTA.Control.Cover,          "RB / R1"),
            ((int)GTA.Control.FrontendLt,     "LT / L2"),
            ((int)GTA.Control.FrontendRt,     "RT / R2"),
            ((int)GTA.Control.FrontendLs,     "L3"),
            ((int)GTA.Control.FrontendRs,     "R3"),
            (37, "LB / L1"),
            (45, "RB / R1"),
            (76, "X / Square"),
        };
        private string ControlName(int idx)
        {
            foreach (var (i, n) in ControlButtonNames) if (i == idx) return n;
            return "#" + idx;
        }

        private NativeMenu BuildControlsMenu()
        {
            var menu = CreateMenu("CONTROLS", "CONTROLS");

            void AddKey(string label, Func<Keys> get, Action<Keys> set)
            {
                var it = new NativeItem(label, "Select to rebind. Saves to settings.ini.") { AltTitle = get().ToString() };
                Func<string> val = () => get().ToString();
                it.Activated += (s, e) => StartRebindKey(label, set, it, val);
                menu.Add(it);
            }
            void AddBtn(string label, Func<int> get, Action<int> set)
            {
                var it = new NativeItem(label, "Select to rebind. Saves to settings.ini.") { AltTitle = ControlName(get()) };
                Func<string> val = () => ControlName(get());
                it.Activated += (s, e) => StartRebindButton(label, set, it, val);
                menu.Add(it);
            }

            // --- Keyboard ---
            AddKey("Open / Close Menu",        () => ModSettings.MenuKey,            k => ModSettings.MenuKey = k);
            AddKey("Edit Mode Toggle",         () => ModSettings.EditModeKey,        k => ModSettings.EditModeKey = k);
            AddKey("Rename Category",          () => ModSettings.RenameCategoryKey,  k => ModSettings.RenameCategoryKey = k);
            AddKey("Delete / Restore Category",() => ModSettings.DeleteCategoryKey,  k => ModSettings.DeleteCategoryKey = k);
            AddKey("Debug Menu",               () => ModSettings.DebugMenuKey,       k => ModSettings.DebugMenuKey = k);
            AddKey("Shift Up (key)",           () => ModSettings.ShiftUpKey,         k => ModSettings.ShiftUpKey = k);
            AddKey("Shift Down (key)",         () => ModSettings.ShiftDownKey,       k => ModSettings.ShiftDownKey = k);
            AddKey("Neutral (key)",            () => ModSettings.NeutralKey,         k => ModSettings.NeutralKey = k);
            AddKey("Nitrous Spray (key)",      () => ModSettings.NosKey,             k => ModSettings.NosKey = k);

            // --- Controller (button glyphs auto-match the player's device) ---
            AddBtn("Walk-Around (button)",     () => ModSettings.WalkAroundButton,   v => ModSettings.WalkAroundButton = v);
            AddBtn("Camera Prev (button)",     () => ModSettings.CamPrevButton,      v => ModSettings.CamPrevButton = v);
            AddBtn("Camera Next (button)",     () => ModSettings.CamNextButton,      v => ModSettings.CamNextButton = v);
            AddBtn("Open Door (button)",       () => ModSettings.DoorButton,         v => ModSettings.DoorButton = v);
            AddBtn("Horn Preview (button)",    () => ModSettings.HornPreviewButton,  v => ModSettings.HornPreviewButton = v);
            AddBtn("Shift Up (button)",        () => ModSettings.ShiftUpButton,      v => ModSettings.ShiftUpButton = v);
            AddBtn("Shift Down (button)",      () => ModSettings.ShiftDownButton,    v => ModSettings.ShiftDownButton = v);
            AddBtn("Nitrous Spray (button)",   () => ModSettings.NosButton,          v => ModSettings.NosButton = v);

            return menu;
        }

        private void StartRebindKey(string label, Action<Keys> setter, NativeItem item, Func<string> value)
        {
            if (isRebindingKey || isRebindingButton) return;
            rebindLabel = label; rebindKeySetter = setter; rebindItem = item; rebindItemValue = value;
            isRebindingKey = true;
            ShowNotification($"~y~Press a key for: ~w~{label}");
        }
        private void StartRebindButton(string label, Action<int> setter, NativeItem item, Func<string> value)
        {
            if (isRebindingKey || isRebindingButton) return;
            rebindLabel = label; rebindButtonSetter = setter; rebindItem = item; rebindItemValue = value;
            isRebindingButton = true;
            ShowNotification($"~y~Press a controller button for: ~w~{label}");
        }
        private void CancelRebind(string why)
        {
            isRebindingKey = false; isRebindingButton = false;
            rebindKeySetter = null; rebindButtonSetter = null; rebindItem = null; rebindItemValue = null; rebindLabel = null;
            if (why != null) ShowNotification(why);
        }
        private void FinishRebind()
        {
            try { if (rebindItem != null && rebindItemValue != null) rebindItem.AltTitle = rebindItemValue(); } catch { }
            ModSettings.Save();
            CancelRebind(null);
        }
        // Controller capture — scanned each frame while the rebind prompt is up.
        private void CheckRebindButtonCapture()
        {
            if (!isRebindingButton) return;
            if (Game.IsControlJustPressed(GTA.Control.FrontendCancel)) { CancelRebind("~r~Rebind cancelled"); return; }
            foreach (var (idx, name) in ControlButtonNames)
            {
                if (idx == (int)GTA.Control.FrontendCancel) continue;   // reserved for cancel
                if (Game.IsControlJustPressed((GTA.Control)idx))
                {
                    rebindButtonSetter?.Invoke(idx);
                    ShowNotification($"~g~{rebindLabel} = {name}");
                    FinishRebind();
                    return;
                }
            }
        }
        private void DrawRebindPrompt()
        {
            string t = isRebindingButton
                ? $"~y~Press a controller button for~n~~w~{rebindLabel}~n~~y~(B / Circle to cancel)"
                : $"~y~Press a key for~n~~w~{rebindLabel}~n~~y~(Esc to cancel)";
            var pos = new System.Drawing.PointF(1080f * GTA.UI.Screen.AspectRatio / 2f, 470f);
            if (rebindPrompt == null) rebindPrompt = new LemonUI.Elements.ScaledText(pos, t, 0.5f) { Alignment = GTA.UI.Alignment.Center, Color = System.Drawing.Color.White, Outline = true };
            else { rebindPrompt.Text = t; rebindPrompt.Position = pos; }
            rebindPrompt.Draw();
        }

        private string addCategoryParent = "";
        private void StartAddCategory(string parentPath = "")
        {
            if (isEditingDescription || isAddingCategory) return;
            addCategoryParent = parentPath ?? "";
            isAddingCategory = true;
            keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name the new category");
        }

        // Reusable edit-mode footer: attach the modder tools to ANY menu, scoped to its config path. Root ("")
        // gets only Add-a-Category; a category path gets Add Category / Add Part / Edit Description (+ Rename if
        // it's a custom per-vehicle category). All changes are written under Vehicles/{model}/ — per-vehicle.
        private void AttachEditTools(NativeMenu menu, string configPath, bool isCustomCategory)
        {
            if (!editModeActive) return;
            bool isRoot = string.IsNullOrEmpty(configPath);

            var addCat = new NativeItem("~y~+ Add a Category") { AltTitle = "EDIT" };
            addCat.Description = isRoot ? "Create a new custom category for this vehicle."
                                       : "Create a custom sub-category here.";
            addCat.Activated += (s, e) => StartAddCategory(configPath);
            menu.Add(addCat);

            if (isRoot) return;

            var thisMenu = menu;
            // "Add a Part" only inside custom categories — parts render there. Built-in submenus get sub-categories
            // (which you then fill with parts), since the built-in part list itself is hardcoded.
            if (isCustomCategory)
            {
                var addPart = new NativeItem("~y~+ Add a Part") { AltTitle = "EDIT" };
                addPart.Description = "Pick a real body part and add it here under your own name.";
                addPart.Activated += (s, e) => StartAddPart(configPath, thisMenu);
                menu.Add(addPart);
            }

            var editDesc = new NativeItem("~y~Edit Description") { AltTitle = "EDIT" };
            editDesc.Description = "Edit this category's description (for this vehicle).";
            editDesc.Activated += (s, e) => StartEditDescriptionVeh(configPath);
            menu.Add(editDesc);

            if (isCustomCategory)
            {
                var rename = new NativeItem("~y~Rename Category") { AltTitle = "EDIT" };
                rename.Description = "Rename this custom category.";
                rename.Activated += (s, e) => StartRenameCategory(configPath);
                menu.Add(rename);
            }
        }

        // Config folder path for a built-in body slot, so custom sub-categories nest under the right place
        // (e.g. a custom category created in the Hood menu lives at Vehicles/{model}/Hood/...). null = not body.
        private string ConfigPathForSlot(int modIndex)
        {
            switch (modIndex)
            {
                case 0: return "Spoiler";
                case 1: return "Bumpers/Front Bumper";
                case 2: return "Bumpers/Rear Bumper";
                case 3: return "Skirts";
                case 4: return "Exhaust";
                case 5: return "Roll Cage";
                case 6: return "Grille";
                case 7: return "Hood";
                case 8: return "Fenders/Left Fender";
                case 9: return "Fenders/Right Fender";
                case 10: return "Roof";
                default: return null;
            }
        }

        // Render this vehicle's custom sub-categories created under <paramref name="parentPath"/> as nav items.
        private void RenderCustomChildren(NativeMenu menu, string parentPath)
        {
            foreach (var child in MenuConfig.GetVehicleSubCategories(parentPath))
            {
                var subMenu = BuildCategoryMenu(child, menu);
                var nav = new NativeItem(child.DisplayName) { AltTitle = ">>" };
                if (!string.IsNullOrEmpty(child.Description)) nav.Description = child.Description;
                var capturedSub = subMenu; var capturedMenu = menu;
                nav.Activated += (s, e) => { isNavigatingMenu = true; capturedMenu.Visible = false; capturedSub.Visible = true; isNavigatingMenu = false; };
                menu.Add(nav);
            }
        }

        // ===== Universal wheel categories (edit mode) =====
        private readonly List<NativeMenu> universalWheelMenus = new List<NativeMenu>();
        private string pendingWheelCategory = null;
        private int pendingWheelType = -1, pendingWheelIndex = -1;
        private bool isNamingWheel = false;
        private bool isAddingWheelCategory = false;
        private static readonly (int type, string name)[] WheelPickerTypes = new[]
        {
            (0,"Sport"),(1,"Muscle"),(2,"Lowrider"),(3,"SUV"),(4,"Off-Road"),(5,"Tuner"),(6,"Bike"),
            (7,"High End"),(8,"Benny's Originals"),(9,"Benny's Bespoke"),(10,"Open Wheel"),(11,"Street"),(12,"Track")
        };

        private void PreviewWheel(int wheelType, int index)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, index, false);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, index, false);
        }

        // Menu for a universal wheel category: each item is a rim (WheelType+Value). Preview on hover, revert on
        // close, installed icon. Apply on select (preview-only in edit mode).
        private NativeMenu BuildWheelCategoryMenu(MenuConfig.Category category, NativeMenu parentMenu)
        {
            var menu = CreateMenu(category.DisplayName);
            universalWheelMenus.Add(menu);

            int[] orig = { -1, -1, -1 };   // wheelType, front(23), back(24)
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    orig[0] = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
                    orig[1] = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
                    orig[2] = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);
                }
            };
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (e.Index >= 0 && e.Index < category.Items.Count)
                {
                    var it = category.Items[e.Index];
                    if (it.WheelType >= 0) PreviewWheel(it.WheelType, it.Value);
                }
            };
            menu.Closed += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists() && orig[0] >= 0)
                {
                    Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
                    Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, orig[0]);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, orig[1], false);
                    Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, orig[2], false);
                }
                if (!isNavigatingMenu && parentMenu != null) parentMenu.Visible = true;
            };

            foreach (var item in category.Items)
            {
                var capturedItem = item;
                var mi = new NativeItem(item.Name);
                if (!string.IsNullOrEmpty(item.Description)) mi.Description = item.Description;
                bool installed = capturedItem.WheelType >= 0 && currentVehicle != null && currentVehicle.Exists()
                    && Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle) == capturedItem.WheelType
                    && Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23) == capturedItem.Value;
                if (installed) { mi.AltTitle = ""; itemOwnershipStatus[mi] = STATUS_INSTALLED; }
                else mi.AltTitle = capturedItem.Price > 0 ? $"${capturedItem.Price:N0}" : "Free";
                mi.Activated += (s, e) =>
                {
                    if (editModeActive) { StartEditItem(category.Name, category.Items.IndexOf(capturedItem), true, capturedItem.Name, capturedItem.Price); return; }
                    if (capturedItem.WheelType < 0 || currentVehicle == null || !currentVehicle.Exists()) return;
                    if (!TryPurchase(capturedItem.Price, false)) return;
                    PreviewWheel(capturedItem.WheelType, capturedItem.Value);
                    orig[0] = capturedItem.WheelType; orig[1] = capturedItem.Value; orig[2] = capturedItem.Value; // commit
                    itemOwnershipStatus[mi] = STATUS_INSTALLED; mi.AltTitle = "";
                    ShowNotification($"~g~Installed: {capturedItem.Name}");
                };
                menu.Add(mi);
            }

            if (editModeActive)
            {
                var thisMenu = menu; string catName = category.Name;
                var addWheel = new NativeItem("~y~+ Add a Wheel") { AltTitle = "EDIT" };
                addWheel.Description = "Pick a rim from any wheel type and add it to this category.";
                addWheel.Activated += (s, e) => StartAddWheel(catName, thisMenu);
                menu.Add(addWheel);
            }
            return menu;
        }

        // Batch session: rims the modder checkmarked across the wheel-type submenus, committed together on Done.
        private readonly List<(int type, int index, string name)> batchWheelChecked = new List<(int, int, string)>();

        // Wheel picker: every wheel type that has rims -> its rims (preview on hover). Checkmark the ones you want
        // and hit Done — they're all added (default names, free). Edit names/prices afterwards or in the config.
        private void StartAddWheel(string categoryName, NativeMenu returnMenu)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            batchWheelChecked.Clear();
            int saveType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, currentVehicle);
            int saveFront = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
            int saveBack = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);

            void Restore()
            {
                Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, saveType);
                Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, saveFront, false);
                Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, saveBack, false);
            }

            var picker = CreateMenu("Add Wheels");
            universalWheelMenus.Add(picker);
            picker.Closed += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists()) Restore();
                if (!isNavigatingMenu) returnMenu.Visible = true;
            };

            var doneTop = new NativeItem("~g~Done — add checked") { AltTitle = "✓" };
            doneTop.Description = "Add every checked wheel to this category (free, default names).";
            doneTop.Activated += (s, e) => CommitBatchWheels(categoryName);
            picker.Add(doneTop);

            foreach (var (wtype, wname) in WheelPickerTypes)
            {
                Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wtype);
                int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, 23);
                if (count <= 0) continue;

                var typeMenu = CreateMenu(wname);
                universalWheelMenus.Add(typeMenu);
                int typeCap = wtype, countCap = count;
                typeMenu.Shown += (s, e) => { if (currentVehicle != null && currentVehicle.Exists() && typeMenu.SelectedIndex < countCap) PreviewWheel(typeCap, typeMenu.SelectedIndex); };
                typeMenu.SelectedIndexChanged += (s, e) => { if (currentVehicle != null && currentVehicle.Exists() && e.Index < countCap) PreviewWheel(typeCap, e.Index); };
                typeMenu.Closed += (s, e) => { if (!isNavigatingMenu) picker.Visible = true; };
                for (int i = 0; i < count; i++)
                {
                    string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, 23, i);
                    string rn = (!string.IsNullOrEmpty(label) && label != "NULL") ? Game.GetLocalizedString(label) : null;
                    if (string.IsNullOrEmpty(rn)) rn = $"{wname} {i + 1}";
                    int wt = wtype, ii = i; string nm = rn;
                    var cb = new NativeCheckboxItem(rn, batchWheelChecked.Exists(x => x.type == wt && x.index == ii));
                    cb.CheckboxChanged += (s, e) =>
                    {
                        if (cb.Checked) { if (!batchWheelChecked.Exists(x => x.type == wt && x.index == ii)) batchWheelChecked.Add((wt, ii, nm)); }
                        else batchWheelChecked.RemoveAll(x => x.type == wt && x.index == ii);
                    };
                    typeMenu.Add(cb);
                }
                var doneSub = new NativeItem("~g~Done — add checked") { AltTitle = "✓" };
                doneSub.Description = "Add every checked wheel (free, default names).";
                doneSub.Activated += (s, e) => CommitBatchWheels(categoryName);
                typeMenu.Add(doneSub);

                var nav = new NativeItem(wname) { AltTitle = $"{count} >>" };
                nav.Activated += (s, e) => { isNavigatingMenu = true; picker.Visible = false; typeMenu.Visible = true; isNavigatingMenu = false; };
                picker.Add(nav);
            }
            Restore();   // leave the car as it was after enumerating types

            isNavigatingMenu = true; returnMenu.Visible = false; picker.Visible = true; isNavigatingMenu = false;
        }

        private void CommitBatchWheels(string categoryName)
        {
            if (batchWheelChecked.Count == 0) { ShowNotification("~y~Nothing checked yet."); return; }
            int added = 0;
            foreach (var (wt, idx, name) in batchWheelChecked)
            {
                var item = new MenuConfig.MenuItem { Name = name, WheelType = wt, Value = idx, Price = 0 };
                if (MenuConfig.AddWheelToUniversalCategory(categoryName, item)) added++;
            }
            batchWheelChecked.Clear();
            ShowNotification($"~g~Added {added} wheel(s) to {categoryName}.");
            if (currentVehicle != null) RebuildMenusForVehicle();
        }

        private void BeginNameWheel(string categoryName, int wheelType, int index)
        {
            pendingWheelCategory = categoryName; pendingWheelType = wheelType; pendingWheelIndex = index;
            isNamingWheel = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name this wheel");
        }

        private void UpdateNameWheel()
        {
            if (!isNamingWheel) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isNamingWheel = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Wheel not added (empty name)."); return; }
                var item = new MenuConfig.MenuItem { Name = result, WheelType = pendingWheelType, Value = pendingWheelIndex, Price = 0 };
                if (MenuConfig.AddWheelToUniversalCategory(pendingWheelCategory, item))
                {
                    ShowNotification($"~g~Added: {result}");
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~Couldn't add the wheel.");
            }
            else if (status == 2) { isNamingWheel = false; }
        }

        private void StartAddWheelCategory()
        {
            if (isAddingWheelCategory) return;
            isAddingWheelCategory = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name the new wheel category");
        }

        private void UpdateAddWheelCategory()
        {
            if (!isAddingWheelCategory) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isAddingWheelCategory = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Empty name."); return; }
                if (MenuConfig.CreateUniversalWheelCategory(result))
                {
                    ShowNotification($"~g~Wheel category created: {result}");
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~That wheel category already exists.");
            }
            else if (status == 2) { isAddingWheelCategory = false; }
        }

        // ---- Edit an existing custom item's name + price (edit mode; A/Enter on the item) ----
        private bool isEditingItemName = false, isEditingItemPrice = false;
        private string editItemCategory = null, editItemName = null;
        private int editItemIndex = -1, editItemPrice = 0;
        private bool editItemIsWheel = false;

        private void StartEditItem(string category, int index, bool isWheel, string currentName, int currentPrice)
        {
            if (isEditingItemName || isEditingItemPrice) return;
            editItemCategory = category; editItemIndex = index; editItemIsWheel = isWheel;
            editItemName = currentName; editItemPrice = currentPrice;
            isEditingItemName = true; keyboardCheckCooldown = 5;
            // Pre-fill the current name so the modder can just press Enter to keep it and jump to the price.
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", currentName ?? "", "", "", "", 64);
            ShowNotification("~y~Edit name (Enter to keep), then price");
        }

        private void UpdateEditItemName()
        {
            if (!isEditingItemName) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                if (!string.IsNullOrEmpty(result)) editItemName = result;   // empty -> keep current name
                isEditingItemName = false;
                isEditingItemPrice = true; keyboardCheckCooldown = 5;
                Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", editItemPrice.ToString(), "", "", "", 9);
                ShowNotification("~y~Edit price (Enter to keep)");
            }
            else if (status == 2) { isEditingItemName = false; }   // cancel aborts the whole edit
        }

        private void UpdateEditItemPrice()
        {
            if (!isEditingItemPrice) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1 || status == 2)
            {
                int price = editItemPrice;
                if (status == 1)
                {
                    string raw = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                    string digits = ""; foreach (char c in raw) if (char.IsDigit(c)) digits += c;
                    if (!string.IsNullOrEmpty(digits)) int.TryParse(digits, out price);
                }
                isEditingItemPrice = false;
                bool ok = editItemIsWheel
                    ? MenuConfig.UpdateUniversalWheelItem(editItemCategory, editItemIndex, editItemName, price)
                    : MenuConfig.UpdateVehicleCategoryItem(editItemCategory, editItemIndex, editItemName, price);
                if (ok)
                {
                    ShowNotification($"~g~Updated: {editItemName}" + (price > 0 ? $" (${price:N0})" : " (Free)"));
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~Couldn't update item.");
            }
        }

        private bool editDescVehicleSpecific = false;
        private void StartEditDescriptionVeh(string categoryPath)
        {
            if (isEditingDescription) return;
            editingCategoryName = categoryPath;
            editDescVehicleSpecific = true;
            isEditingDescription = true;
            keyboardCheckCooldown = 5;
            string cur = MenuConfig.GetDescription(categoryPath) ?? "";
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", cur, "", "", "", 200);
            ShowNotification("~y~Edit the description");
        }

        private bool isRenamingCategory = false;
        private string pendingRenamePath = null;
        private bool pendingRenameIsCustom = true;
        private void StartRenameCategory(string categoryKey, bool isCustom = true)
        {
            if (isRenamingCategory || isAddingCategory || isEditingDescription) return;
            pendingRenamePath = categoryKey;
            pendingRenameIsCustom = isCustom;
            isRenamingCategory = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~New category name");
        }

        private void UpdateRenameCategory()
        {
            if (!isRenamingCategory) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isRenamingCategory = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Rename cancelled (empty)."); return; }
                bool ok;
                if (pendingRenameIsCustom) ok = MenuConfig.RenameVehicleCategory(pendingRenamePath, result);
                else { MenuConfig.SetCategoryRename(pendingRenamePath, result); ok = true; }   // built-in override
                if (ok)
                {
                    ShowNotification($"~g~Renamed to: {result}");
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~Couldn't rename.");
            }
            else if (status == 2) { isRenamingCategory = false; }
        }

        private void UpdateAddCategory()
        {
            if (!isAddingCategory) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isAddingCategory = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Category not created (empty name)."); return; }
                if (MenuConfig.CreateVehicleCategory(addCategoryParent, result))
                {
                    ShowNotification($"~g~Category created: {result}");
                    if (currentVehicle != null) RebuildMenusForVehicle();
                }
                else ShowNotification("~r~That category already exists.");
            }
            else if (status == 2) { isAddingCategory = false; }   // cancelled
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
            string vehicleName = VehicleKey(currentVehicle);

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

                    bool owned = capturedItem.Value >= 0 && VehicleSaveData.IsCustomItemOwned(VehicleKey(currentVehicle), capturedCategoryPath, capturedItem.Value);
                    if (!TryPurchase(capturedPrice, owned)) return;

                    onActivate(capturedItem);
                    // Mark as owned when purchased
                    if (capturedItem.Value >= 0 && !owned)
                    {
                        VehicleSaveData.SetCustomItemOwned(VehicleKey(currentVehicle), capturedCategoryPath, capturedItem.Value);
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
            string vehicleName = VehicleKey(currentVehicle);

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
            // Custom Color follows the ELSC equip model (same as Custom Suspension / Manual Transmission):
            // NOT owned -> price; owned but NOT active -> owned tick + "Select to equip"; owned AND active ->
            // equipped garage icon + "Select to edit". Equipping applies the custom glass color (mutually
            // exclusive with the presets); selecting a preset un-equips it (the chosen color is preserved).
            NativeItem customColorItem = null;
            const int customColorPrice = 500;

            // Custom is "equipped" when a custom color is actively saved (CurrentColor != 0).
            bool CustomColorEquipped() =>
                currentVehicle != null && VehicleSaveData.IsCustomItemOwned(VehicleKey(currentVehicle), "Windows", WINDOW_CUSTOM_COLOR_KEY)
                && windowTint.CurrentColor(currentVehicle) != 0;

            void RefreshCustomColorItem()
            {
                if (customColorItem == null || currentVehicle == null) return;
                bool owned = VehicleSaveData.IsCustomItemOwned(VehicleKey(currentVehicle), "Windows", WINDOW_CUSTOM_COLOR_KEY);
                if (!owned)
                {
                    itemOwnershipStatus.Remove(customColorItem);
                    customColorItem.AltTitle = $"${customColorPrice}";
                    customColorItem.Description = "Buy a custom glass color you can fine-tune.";
                }
                else if (CustomColorEquipped())
                {
                    customColorItem.AltTitle = "";
                    itemOwnershipStatus[customColorItem] = STATUS_INSTALLED;
                    customColorItem.Description = "Select to edit your custom glass color.";
                }
                else
                {
                    customColorItem.AltTitle = "";
                    itemOwnershipStatus[customColorItem] = STATUS_OWNED;
                    customColorItem.Description = "Select to equip your custom glass color.";
                }
            }

            // Equip the custom glass color as the active tint (restores the last chosen color, or a default).
            void EquipCustomColor()
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (!windowTint.Ready) { ShowNotification("~y~Window-color table initializing… try again in a moment."); return; }
                int last = windowTint.LastColor(currentVehicle);
                int argb = last != 0 ? last : WindowTintManager.PackArgb(64, 0, 0, 0);
                windowTint.ApplyCustomColor(currentVehicle, argb);
                windowTintPurchased = true;                 // don't revert on close
                RefreshConfigMenuStatus("Windows", -999);   // no preset shows the equipped icon now
                RefreshCustomColorItem();                   // Custom shows the equipped garage icon
                ShowNotification("~g~Custom glass color equipped~w~ — select again to edit");
            }

            var menu = CreateMenuFromConfig(
                "Windows",
                () => (int)currentVehicle.Mods.WindowTint,
                (item) =>
                {
                    // Selecting a preset un-equips Custom (its chosen color is preserved for re-equip).
                    windowTint.ClearCustomColor(currentVehicle);
                    ApplyWindowTint(item.Value);
                    windowTintPurchased = true;
                    RefreshCustomColorItem();   // Custom drops back to the owned tick
                }
            );
            if (menu == null) return null;

            var colorMenu = CreateCustomWindowColorMenu();

            // Hover preview of the custom glass color (uses the active or last-chosen color, never wipes it).
            void PreviewCustomColor()
            {
                if (currentVehicle == null || !currentVehicle.Exists() || !windowTint.CanPreview) return;
                int saved = windowTint.CurrentColor(currentVehicle);
                int last = windowTint.LastColor(currentVehicle);
                int argb = saved != 0 ? saved : (last != 0 ? last : WindowTintManager.PackArgb(64, 0, 0, 0));
                windowTint.PreviewColor(currentVehicle, argb);
            }

            menu.Shown += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                origWindowTint = (int)currentVehicle.Mods.WindowTint;
                windowTintPurchased = false;
                RefreshCustomColorItem();
                // Park the car off per-tick assertion for the session so preset previews don't fight the
                // custom color; restore the real look immediately (BeginPreview moves it to the preview slot).
                windowTint.BeginPreview(currentVehicle);
                currentVehicle.Mods.WindowTint = (VehicleWindowTint)origWindowTint;
            };
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (e.Index < 0 || e.Index >= menu.Items.Count) return;
                var hovered = menu.Items[e.Index] as NativeItem;
                if (hovered != null && hovered == customColorItem) { PreviewCustomColor(); return; }
                if (hovered != null && configItemMetadata.TryGetValue(hovered, out var meta) && meta.Item2 >= 0)
                    currentVehicle.Mods.WindowTint = (VehicleWindowTint)meta.Item2;
            };
            menu.Closed += (s, e) =>
            {
                if (isNavigatingMenu) return;   // opening the color picker — don't revert / release the preview
                windowTint.EndPreview();
                if (!windowTintPurchased && origWindowTint >= 0 && currentVehicle != null && currentVehicle.Exists())
                    currentVehicle.Mods.WindowTint = (VehicleWindowTint)origWindowTint;
            };

            customColorItem = new NativeItem("Custom Color");
            RefreshCustomColorItem();
            customColorItem.Activated += (s, e) =>
            {
                if (currentVehicle == null) return;
                string vn = VehicleKey(currentVehicle);
                bool owned = VehicleSaveData.IsCustomItemOwned(vn, "Windows", WINDOW_CUSTOM_COLOR_KEY);

                if (!owned)
                {
                    if (!TryPurchase(customColorPrice, false)) return;
                    VehicleSaveData.SetCustomItemOwned(vn, "Windows", WINDOW_CUSTOM_COLOR_KEY);
                    VehicleSaveData.Save();
                    EquipCustomColor();   // buy + equip; select again to edit
                    return;
                }
                if (!CustomColorEquipped())
                {
                    EquipCustomColor();   // equip; select again to edit
                    return;
                }
                // Equipped -> open the RGB adjuster (edit).
                isNavigatingMenu = true; menu.Visible = false; colorMenu.Visible = true; isNavigatingMenu = false;
            };
            colorMenu.Closed += (s, e) => { if (!isNavigatingMenu) { menu.Visible = true; RefreshCustomColorItem(); } };
            menu.Add(customColorItem);
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
            var neonColors = category.Items;

            // Live preview: temporarily turn the neon lights ON and show the highlighted colour so the player
            // can see it before buying. On close, restore the original colour + neon on/off state (unless bought).
            void PreviewNeon(int idx)
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (idx < 0 || idx >= neonColors.Count) return;
                for (int n = 0; n < 4; n++) Function.Call((Hash)0x2AA720E4287BF269, currentVehicle, n, true); // enable all
                int pr = neonColors[idx].R ?? 255, pg = neonColors[idx].G ?? 255, pb = neonColors[idx].B ?? 255;
                Function.Call((Hash)0x8E0A582209A62695, currentVehicle, pr, pg, pb); // SET neon colour
            }
            menu.Shown += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                neonColorPurchased = false;
                unsafe
                {
                    int r0, g0, b0;
                    Function.Call((Hash)0x7619EEE8C886757F, currentVehicle, &r0, &g0, &b0); // GET neon colour
                    origNeonRGB[0] = r0; origNeonRGB[1] = g0; origNeonRGB[2] = b0;
                }
                for (int n = 0; n < 4; n++)
                    origNeonOn[n] = Function.Call<bool>((Hash)0x8C4B92553E4766A5, currentVehicle, n); // IS enabled
                PreviewNeon(menu.SelectedIndex);
            };
            menu.SelectedIndexChanged += (s, e) => PreviewNeon(e.Index);
            menu.Closed += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                if (!neonColorPurchased)
                    Function.Call((Hash)0x8E0A582209A62695, currentVehicle, origNeonRGB[0], origNeonRGB[1], origNeonRGB[2]);
                for (int n = 0; n < 4; n++)
                    Function.Call((Hash)0x2AA720E4287BF269, currentVehicle, n, origNeonOn[n]); // restore on/off
            };

            // Read the car's CURRENT neon colour so the matching swatch shows the equipped icon.
            int curR = 255, curG = 255, curB = 255;
            if (currentVehicle != null && currentVehicle.Exists())
                unsafe { int cr, cg, cb; Function.Call((Hash)0x7619EEE8C886757F, currentVehicle, &cr, &cg, &cb); curR = cr; curG = cg; curB = cb; }

            var neonItems = new List<(NativeItem item, int r, int g, int b, int price)>();
            void RefreshNeonInstalled(int r, int g, int b)
            {
                foreach (var (it, ir, ig, ib, pr) in neonItems)
                {
                    itemOwnershipStatus.Remove(it);
                    if (ir == r && ig == g && ib == b) { it.AltTitle = ""; itemOwnershipStatus[it] = STATUS_INSTALLED; }
                    else it.AltTitle = pr == 0 ? "Free" : $"${pr}";
                }
            }

            foreach (var item in category.Items)
            {
                var menuItem = new NativeItem(item.Name);
                int r = item.R ?? 255;
                int g = item.G ?? 255;
                int b = item.B ?? 255;
                int price = item.Price;
                neonItems.Add((menuItem, r, g, b, price));
                menuItem.Activated += (s, e) =>
                {
                    if (!TryPurchase(price, false)) return;
                    neonColorPurchased = true; // keep the colour; the on/off state still restores to the player's kit
                    ApplyNeonColor(r, g, b);
                    RefreshNeonInstalled(r, g, b);   // move the equipped icon to the bought colour
                };
                menu.Add(menuItem);
            }
            RefreshNeonInstalled(curR, curG, curB);   // initial equipped icon

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
        // Single source of truth for an item's description text. CRITICAL: when an item carries a native
        // LemonUI Description we capture it into customDescriptions and CLEAR the native one — LemonUI's own
        // description rendering ignores the scroll-arrow row and overlaps it, so we suppress it and redraw the
        // text ourselves (scroll-aware) in DrawCustomDescription. Capturing every call also picks up dynamic
        // description updates. Cleared per vehicle in RebuildMenusForVehicle.
        private string GetItemDescription(NativeItem item)
        {
            if (item == null) return null;
            if (!string.IsNullOrEmpty(item.Description))
            {
                customDescriptions[item] = item.Description;
                item.Description = "";
            }
            if (customDescriptions.TryGetValue(item, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;
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
            string vehicleName = VehicleKey(currentVehicle);
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
                // When an ELSC custom part is the active equip (Custom Suspension for slot 15, Manual Transmission
                // for slot 13), Stock must NOT show the equipped icon — the custom item owns it — even though no
                // vanilla mod is installed (slot == -1).
                bool customActive = (idx == 15 && VehicleSaveData.IsCustomSuspensionEquipped(vehicleName))
                                 || (idx == 13 && VehicleSaveData.IsManualTransmissionEquipped(vehicleName));
                if (currentMod == -1 && !customActive)
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
                    if (editModeActive) { ShowNotification("~y~Edit mode: preview only"); return; }
                    // Purchasing stock - clear preview state first
                    isPreviewingMod = false;
                    previewModIndex = -1;
                    ApplyModByIndex(idx, -1);
                    if (idx == 15) OnGameSuspensionChosen();
                    if (idx == 13) OnGameTransmissionChosen();
                };
                menu.Add(stockItem);
            }

            // Mod options
            for (int i = 0; i < count; i++)
            {
                // Skip parts the modder re-shelved into a custom category (hidden from their default slot).
                if (MenuConfig.IsPartHidden(modIndex, i)) continue;

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
                    if (editModeActive) { ShowNotification("~y~Edit mode: preview only"); return; }
                    bool owned = VehicleSaveData.IsModOwned(VehicleKey(currentVehicle), idx, modValue);
                    if (!TryPurchase(capturedPrice, owned)) return;

                    // Purchasing - clear preview state first
                    isPreviewingMod = false;
                    previewModIndex = -1;

                    int prevEngine = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, idx);
                    ApplyModByIndex(idx, modValue);
                    if (idx == 15) OnGameSuspensionChosen();
                    if (idx == 13) OnGameTransmissionChosen();
                    // New engine hardware -> the dyno tune no longer matches; reset it (keeps the unlock).
                    if (idx == 11 && ModSettings.VehicleTuning && currentTuning.Unlocked && prevEngine != modValue)
                    {
                        ResetTuningToStock();
                        ShowNotification("~y~New engine installed~w~ — handling tune reset");
                    }
                    // Mark as owned when purchased
                    if (!owned)
                    {
                        VehicleSaveData.SetModOwned(VehicleKey(currentVehicle), idx, modValue);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            // Edit mode: custom sub-categories (and the Add-a-Category tool) for this built-in body category,
            // so a modder can group custom parts inside e.g. the Hood or Skirts menu — per-vehicle.
            string cfgPath = ConfigPathForSlot(modIndex);
            if (cfgPath != null)
            {
                RenderCustomChildren(menu, cfgPath);
                AttachEditTools(menu, cfgPath, false);
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
                if (EditBlockApply()) return;
                isPreviewingSwap = false;
                ApplyEngineSwap(null);
                VehicleSaveData.SetEngineSwapId(VehicleKey(currentVehicle), null);
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
                    bool changedSwap = !owned;
                    isPreviewingSwap = false;
                    ApplyEngineSwap(captured);
                    VehicleSaveData.SetEngineSwapId(VehicleKey(currentVehicle), captured.Id);
                    VehicleSaveData.Save();
                    RefreshIcons();
                    // Swapped to a different engine -> reset the dyno tune (keeps the unlock).
                    if (changedSwap && ModSettings.VehicleTuning && currentTuning.Unlocked)
                    {
                        ResetTuningToStock();
                        ShowNotification("~y~New engine installed~w~ — handling tune reset");
                    }
                };
                menu.Add(item);
            }

            RefreshIcons();
            return menu;
        }

        // ---------- Vehicle Tuning (live handling fine-tune; one-time dyno unlock) ----------

        private static float GetH(Vehicle v, int off) => WheelFitment.WheelMemory.GetHandlingFloat(v, off);
        private static void SetH(Vehicle v, int off, float val) => WheelFitment.WheelMemory.SetHandlingFloat(v, off, val);

        /// <summary>Cache factory handling per model (first encounter, before any tune) + load/apply saved tune.</summary>
        private void InitTuningForVehicle()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            if (!WheelFitment.WheelMemory.HasHandling(currentVehicle)) return;
            int mh = currentVehicle.Model.Hash;
            if (!tuningStockCache.TryGetValue(mh, out var stock))
            {
                stock = CaptureTuningStock(currentVehicle);
                tuningStockCache[mh] = stock;
            }
            currentStock = stock;
            currentTuning = VehicleSaveData.GetTuningData(VehicleKey(currentVehicle));
            ApplyTuning();
        }

        private float[] CaptureTuningStock(Vehicle v) => new float[]
        {
            GetH(v, H_FORCE),
            GetH(v, H_MAXVEL),
            GetH(v, H_BRAKE),
            GetH(v, H_TRACTION),
            GetH(v, H_DRIVEBIAS),
            GetH(v, H_BRAKEBIAS_F) / 2f,   // stored 2*biasFront -> biasFront fraction
            GetH(v, H_STEER),              // radians
            Math.Max(0, WheelFitment.WheelMemory.GetHandlingGears(v)),  // [7] stock gear count (0 if unavailable)
        };

        // nInitialDriveGears offset (0x50) VERIFIED via dyno 2026-06-18: WheelMemory.GetHandlingGears read 5 on
        // blista / 6 on elegy2, matching each car's actual top gear. Write applies on vehicle reload (model-shared).
        private const bool GEARS_VERIFIED = true;

        /// <summary>Write the current tune (relative to cached stock) into the model's CHandlingData. Only the
        /// gearing + handling fields are tuned here — raw power/brake force come from part buys, not tuning.</summary>
        private void ApplyTuning()
        {
            if (currentVehicle == null || !currentVehicle.Exists() || currentStock == null) return;
            var t = currentTuning; var s = currentStock;
            SetH(currentVehicle, H_MAXVEL, s[1] * t.TopSpeedMult);   // gear ratio / final drive
            float grip = s[3] * t.GripMult;
            SetH(currentVehicle, H_TRACTION, grip);
            SetH(currentVehicle, H_TRACTION_INV, grip > 0.0001f ? 1f / grip : 0f);
            float db = t.DriveBias < 0f ? s[4] : t.DriveBias;
            SetH(currentVehicle, H_DRIVEBIAS, db);
            float bb = t.BrakeBias < 0f ? s[5] : t.BrakeBias;
            SetH(currentVehicle, H_BRAKEBIAS_F, 2f * bb);
            SetH(currentVehicle, H_BRAKEBIAS_R, 2f * (1f - bb));
            float lk = s[6] * t.SteerLockMult;
            SetH(currentVehicle, H_STEER, lk);
            SetH(currentVehicle, H_STEER_INV, lk > 0.0001f ? 1f / lk : 0f);

            // Extra gears: write the LIVE gearbox top-gear count (the model handling field 0x50 only applies on a
            // vehicle reload — the running car never sees it). Then re-cache MT and re-space the ratios so the NEW
            // gear gets a real ratio (not garbage). Idempotent: always targets stock + N, so N=0 restores stock.
            if (GEARS_VERIFIED && s.Length > 7 && s[7] > 0)
            {
                int target = (int)s[7] + Math.Max(0, t.GearCount);
                ManualTransmission.VehicleMemory.SetTopGear(currentVehicle, target);   // live count (game + MT read this)
                // NOTE: do NOT write SetHandlingGears here. That field (nInitialDriveGears 0x50) is model-shared and
                // persists across a script reload, and CaptureTuningStock READS it as "stock" — so writing stock+N
                // back to it made every reload re-capture the tuned value and add the tune again (5->6->7->8...).
                // The live SetTopGear above is the real mechanism; it's re-applied by InitTuningForVehicle on entry.
                if (elscTransmission != null)
                {
                    if (elscTransmission.IsEnabled) elscTransmission.RefreshGears((int)s[7]);    // re-cache + extend range from stock
                    else elscTransmission.ApplyNfsGearing(currentVehicle, (int)s[7]);            // automatic: extend range from stock
                }
            }
        }

        private void PersistTuning()
        {
            if (currentVehicle != null && currentVehicle.Exists())
                VehicleSaveData.SetTuningData(VehicleKey(currentVehicle), currentTuning);
        }

        /// <summary>Reset the tune values to factory (keeps the dyno unlock), re-apply, persist, resync sliders.
        /// Called by the Reset item and automatically when an engine upgrade/swap changes the powertrain.</summary>
        private void ResetTuningToStock()
        {
            currentTuning.TopSpeedMult = currentTuning.GripMult = currentTuning.SteerLockMult = 1f;
            currentTuning.DriveBias = currentTuning.BrakeBias = -1f;
            currentTuning.GearCount = 0;
            currentTuning.Fields?.Clear();
            ApplyTuning();
            PersistTuning();
            ResyncTuning();
        }

        private void ResyncTuning() { foreach (var a in _tuningResync) { try { a(); } catch { } } }

        // A visual slider rendered as [---|---] (no numbers). `steps` notches between hard limits; the knob shows
        // the current position. Adjusting applies live; sliders only exist after the dyno unlock is bought.
        private NativeListItem<string> MakeBarSlider(string label, float min, float max, int steps,
                                                     Func<float> get, Action<float> set, string desc)
        {
            var bars = new string[steps];
            for (int i = 0; i < steps; i++)
            {
                var c = new char[steps];
                for (int j = 0; j < steps; j++) c[j] = '-';
                c[i] = '|';
                bars[i] = "[" + new string(c) + "]";
            }
            float range = max - min;
            Func<int> notchOf = () =>
            {
                float frac = range > 0.0001f ? (get() - min) / range : 0f;
                int n = (int)Math.Round(frac * (steps - 1));
                return n < 0 ? 0 : (n >= steps ? steps - 1 : n);
            };
            var item = new NativeListItem<string>(label, bars) { SelectedIndex = notchOf() };
            item.Description = desc;
            item.ItemChanged += (s, e) =>
            {
                float val = min + range * (item.SelectedIndex / (float)(steps - 1));
                set(val);
                ApplyTuning();
                PersistTuning();
            };
            _tuningResync.Add(() => { item.SelectedIndex = notchOf(); });
            NoWrap(item);
            return item;
        }

        private NativeMenu CreateTuningMenu()
        {
            var menu = CreateMenu("VEHICLE TUNING");
            tuningMenu = menu;
            PopulateTuningMenu(menu);
            // Rebuild every time the menu opens so a gating part bought elsewhere (Suspension / Brakes /
            // Transmission / Manual Transmission) unlocks its slider the moment you re-enter Vehicle Tuning,
            // instead of staying locked until a full menu rebuild.
            menu.Shown += (s, e) => PopulateTuningMenu(menu);
            menu.Closed += (s, e) => VehicleSaveData.Save();
            return menu;
        }

        // Locked -> a single "Unlock" item. After purchase the same menu repopulates with the live sliders.
        private void PopulateTuningMenu(NativeMenu menu)
        {
            menu.Clear();
            _tuningResync.Clear();

            if (!currentTuning.Unlocked)
            {
                var buy = new NativeItem("Unlock Vehicle Tuning");
                buy.Description = "Dyno setup. Unlocks live gear-ratio and handling sliders for this vehicle. One-time fee per car.";
                buy.AltTitle = $"${TUNING_FEE:N0}";
                buy.Activated += (s, e) =>
                {
                    if (!TryPurchase(TUNING_FEE, false)) return;
                    currentTuning.Unlocked = true;
                    PersistTuning();
                    VehicleSaveData.Save();
                    isNavigatingMenu = true;
                    menu.Visible = false;
                    PopulateTuningMenu(menu);
                    menu.Visible = true;
                    isNavigatingMenu = false;
                };
                menu.Add(buy);
                return;
            }

            // Progression-gated, beginner-friendly tuning. What you can touch (and how far) is unlocked by the
            // performance parts you've installed — Forza/NFS style — so a stock car is barely tunable and a fully
            // built one gets real freedom. Most players only ever tap a preset.
            var u = GetUnlocks(currentVehicle);

            // ---- One-tap presets (the beginner surface) ----
            menu.Add(MakePresetItem("Setup: Acceleration", "accel",
                "One-tap: shorter gearing + a touch more grip for the quickest launch and corner exit. Uses only what you've unlocked."));
            menu.Add(MakePresetItem("Setup: Balanced", "balanced",
                "One-tap: factory-style balance. A safe all-round setup."));
            menu.Add(MakePresetItem("Setup: Top Speed", "topspeed",
                "One-tap: taller gearing for the highest top speed on long straights. Uses only what you've unlocked."));

            // ---- Final Drive (Acceleration <-> Top Speed) : gated by Transmission, ceiling raised by Engine ----
            if (u.Trans >= 0)
            {
                var fd = FinalDriveRange(u);
                menu.Add(MakeBarSlider("Final Drive", fd.lo, fd.hi, 9,
                    () => currentTuning.TopSpeedMult * 100f, v => currentTuning.TopSpeedMult = v / 100f,
                    "Left = acceleration (shorter gears), right = top speed (taller gears). A better transmission widens it; a stronger engine raises the top end."));
            }
            else menu.Add(LockedItem("Final Drive", "Install a Transmission upgrade to tune your gearing."));

            // ---- Extra Gears : gated by Engine level / swap ----
            int extraGears = MaxExtraGears(u);
            if (extraGears > 0)
            {
                var opts = new string[extraGears + 1];
                opts[0] = "Stock";
                for (int g = 1; g <= extraGears; g++) opts[g] = $"+{g}";
                var gi = new NativeListItem<string>("Gears", opts)
                { SelectedIndex = Math.Min(Math.Max(0, currentTuning.GearCount), extraGears) };
                gi.Description = "Add gears for more top end. Unlocked by Manual Transmission (or a stronger engine)."
                    + (GEARS_VERIFIED ? "" : " (Coming with the next update.)");
                gi.ItemChanged += (s, e) => { currentTuning.GearCount = gi.SelectedIndex; ApplyTuning(); PersistTuning(); };
                _tuningResync.Add(() => { gi.SelectedIndex = Math.Min(Math.Max(0, currentTuning.GearCount), extraGears); });
                NoWrap(gi);
                menu.Add(gi);
            }
            else menu.Add(LockedItem("Gears", "Equip Manual Transmission (or install an Engine upgrade / swap) to add gears."));

            // ---- Tire Grip : gated by Suspension ----
            if (u.Susp >= 0)
            {
                float gLo = u.Susp >= 2 ? 94 : u.Susp >= 1 ? 96 : 98;
                float gHi = u.Susp >= 2 ? 108 : u.Susp >= 1 ? 106 : 104;
                menu.Add(MakeBarSlider("Tire Grip", gLo, gHi, 9,
                    () => currentTuning.GripMult * 100f, v => currentTuning.GripMult = v / 100f,
                    "Cornering grip. Right = more traction. Better suspension widens the range."));
            }
            else menu.Add(LockedItem("Tire Grip", "Set up Custom Suspension & Camber (or install a Suspension upgrade) to tune grip."));

            // ---- Steering Lock : gated by Suspension ----
            if (u.Susp >= 0)
                menu.Add(MakeBarSlider("Steering Lock", 95, u.Susp >= 2 ? 115 : 108, 9,
                    () => currentTuning.SteerLockMult * 100f, v => currentTuning.SteerLockMult = v / 100f,
                    "Maximum steering angle. Right = sharper turn-in."));
            else menu.Add(LockedItem("Steering Lock", "Set up Custom Suspension & Camber (or install a Suspension upgrade) to sharpen steering."));

            // ---- Power Split : AWD cars only, gated by Race Transmission ----
            bool awd = currentStock != null && currentStock.Length > 4 && currentStock[4] > 0.05f && currentStock[4] < 0.95f;
            if (awd)
            {
                if (u.Trans >= 2)
                    menu.Add(MakeBarSlider("Power Split", 0, 100, 9,
                        () => (currentTuning.DriveBias < 0f ? currentStock[4] : currentTuning.DriveBias) * 100f,
                        v => currentTuning.DriveBias = v / 100f,
                        "AWD power distribution. Left = rear-biased (oversteer), right = front-biased (stable)."));
                else menu.Add(LockedItem("Power Split", "Install Race Transmission to tune the AWD power split."));
            }

            // ---- Brake Bias : gated by Race Brakes ----
            if (u.Brakes >= 2)
                menu.Add(MakeBarSlider("Brake Bias", 35, 65, 9,
                    () => (currentTuning.BrakeBias < 0f ? (currentStock != null && currentStock.Length > 5 ? currentStock[5] : 0.5f) : currentTuning.BrakeBias) * 100f,
                    v => currentTuning.BrakeBias = v / 100f,
                    "Brake distribution. Left = rear-biased (rotates more), right = front-biased (more stable)."));
            else menu.Add(LockedItem("Brake Bias", "Install Race Brakes to tune brake balance."));

            var reset = new NativeItem("Reset to Stock");
            reset.Description = "Return all tuning on this vehicle to factory.";
            reset.Activated += (s, e) => { if (EditBlockApply()) return; ResetTuningToStock(); };
            menu.Add(reset);
        }

        // What the installed performance parts unlock for tuning (-1 = stock/not installed; 0..N = part level).
        private struct TuneUnlocks { public int Trans, Engine, Susp, Brakes; public bool HasSwap, HasManual; }
        private TuneUnlocks GetUnlocks(Vehicle v)
        {
            var u = new TuneUnlocks { Trans = -1, Engine = -1, Susp = -1, Brakes = -1 };
            if (v == null || !v.Exists()) return u;
            u.Trans = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, 13);
            u.Engine = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, 11);
            u.Susp = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, 15);
            u.Brakes = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, 12);
            u.HasSwap = !string.IsNullOrEmpty(VehicleSaveData.GetEngineSwapId(VehicleKey(v)));
            // ELSC's Manual Transmission is a transmission upgrade in its own right (NOT a vanilla mod slot 13),
            // so count it as a high transmission tier — this unlocks Final Drive (and widens its range) even
            // when no vanilla Transmission mod is installed.
            if (VehicleSaveData.IsManualTransmissionOwned(VehicleKey(v)))
            {
                u.Trans = Math.Max(u.Trans, 2);
                u.HasManual = true;   // gates the Gears tuning (the +gears feature is driven by MT)
            }
            // Custom Suspension & Camber (fitment) is ELSC's FREE suspension feature — once the player has set
            // up a custom stance for this car, count it as suspension installed (unlocks Tire Grip / Steering
            // Lock), since there's no vanilla suspension mod to read in that case.
            if (VehicleSaveData.IsWheelFitmentOwned(VehicleKey(v)))
                u.Susp = Math.Max(u.Susp, 1);
            return u;
        }

        // Final-drive slider range (in % of stock). Transmission widens both ends; engine raises only the top.
        private (float lo, float hi) FinalDriveRange(TuneUnlocks u)
        {
            float lo = u.Trans >= 2 ? 90f : u.Trans >= 1 ? 95f : 98f;
            float hi = u.Trans >= 2 ? 110f : u.Trans >= 1 ? 105f : 102f;
            hi += 2f * Math.Max(0, u.Engine);   // each engine level adds +2% top-speed ceiling
            return (lo, hi);
        }

        // Extra gears allowed over stock, gated by engine strength (+swap). Strict: needs EMS 2+.
        private int MaxExtraGears(TuneUnlocks u)
        {
            int n = u.Engine >= 2 ? 2 : u.Engine >= 1 ? 1 : 0;
            if (u.HasSwap) n += 1;
            // Manual Transmission is the gateway to gear tuning — owning it unlocks the extra-gear range even on a
            // stock engine. Engine/swap still stack toward the cap.
            if (u.HasManual) n = Math.Max(n, 2);
            return Math.Min(n, 3);
        }

        // A greyed, non-selectable row that explains what part unlocks this control (teaches the progression).
        private NativeItem LockedItem(string label, string hint)
        {
            var it = new NativeItem(label) { AltTitle = "Locked" };
            it.Description = hint;
            it.Enabled = false;
            return it;
        }

        private NativeItem MakePresetItem(string label, string kind, string desc)
        {
            var it = new NativeItem(label) { AltTitle = ">>" };
            it.Description = desc;
            it.Activated += (s, e) => { if (EditBlockApply()) return; ApplyPreset(kind); };
            return it;
        }

        // Presets only move values WITHIN what's currently unlocked, so a stock car's "Top Speed" barely changes —
        // reinforcing that real gains come from parts, not the slider.
        private void ApplyPreset(string kind)
        {
            var u = GetUnlocks(currentVehicle);
            var fd = FinalDriveRange(u);
            float gHi = u.Susp >= 2 ? 1.08f : u.Susp >= 1 ? 1.06f : u.Susp >= 0 ? 1.04f : 1f;
            switch (kind)
            {
                case "accel":
                    currentTuning.TopSpeedMult = (u.Trans >= 0 ? fd.lo : 100f) / 100f;
                    currentTuning.GripMult = Math.Min(1.04f, gHi);
                    break;
                case "topspeed":
                    currentTuning.TopSpeedMult = (u.Trans >= 0 ? fd.hi : 100f) / 100f;
                    currentTuning.GripMult = 1f;
                    break;
                default: // balanced
                    currentTuning.TopSpeedMult = 1f;
                    currentTuning.GripMult = 1f;
                    break;
            }
            ApplyTuning();
            PersistTuning();
            ResyncTuning();
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
                string vName = VehicleKey(currentVehicle);
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
                    // Lock the player's EXACT current stance (absolute width/size/height) and hold it on every
                    // previewed rim, so the wheels look identical to how they're installed instead of the fitment
                    // re-deriving a per-rim baseline that compounds wider/skinnier as you scroll.
                    suspendWheelFitment = false;
                    try { wheelFitment.BeginPreviewLock(); } catch { }
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
                // Release the preview lock and resume the live fitment. The wheel-key change triggers
                // RefreshBaseline (seating + baseline) on its own next tick — do NOT force a full re-init here
                // (lastFitmentVehicle=null), which would WIPE the player's live camber/track/height values.
                try { wheelFitment.EndPreviewLock(); } catch { }
                suspendWheelFitment = false;
            };

            for (int i = 0; i < count; i++)
            {
                if (MenuConfig.IsRimHidden(wheelType, i)) continue;   // re-shelved into a universal wheel category

                int price = ModPricing.GetWheelPrice(wheelType, i);
                var item = new NativeItem(wheelNames[i]);

                // Show the owned icon (and make it free) if this wheel was bought before — matches every
                // other mod category, which the wheel menu previously didn't do (wheels weren't saved).
                bool ownedAtBuild = currentVehicle != null && currentVehicle.Exists()
                    && VehicleSaveData.IsModOwned(VehicleKey(currentVehicle), 23, WheelOwnKey(wType, i));
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
                    bool owned = VehicleSaveData.IsModOwned(VehicleKey(currentVehicle), 23, WheelOwnKey(wType, wheelIndex));
                    if (!TryPurchase(wheelPrice, owned)) return;
                    wheelPurchased = true; // keep the preview, don't revert on close
                    ApplyWheelAxle(wType, wheelIndex, wheelApplyAxle);
                    // Save wheel ownership so it's free to re-install later and shows the owned icon.
                    if (!owned)
                    {
                        VehicleSaveData.SetModOwned(VehicleKey(currentVehicle), 23, WheelOwnKey(wType, wheelIndex));
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
                        if (EditBlockApply()) return;
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

            // Xenon headlight color names — these MUST match GTA's xenon color enum exactly (the index is what
            // SET_VEHICLE_XENON_LIGHT_COLOR_INDEX takes). A spurious "Default White" used to sit at 0, shifting every
            // label one slot off its real color (e.g. "Red" applied index 9 = Pony Pink). Index 0 = White (default).
            var colors = new[]
            {
                (0, "White"),
                (1, "Blue"),
                (2, "Electric Blue"),
                (3, "Mint Green"),
                (4, "Lime Green"),
                (5, "Yellow"),
                (6, "Golden Shower"),
                (7, "Orange"),
                (8, "Red"),
                (9, "Pony Pink"),
                (10, "Hot Pink"),
                (11, "Purple"),
                (12, "Blacklight")
            };

            // Get current color
            int currentColor = Function.Call<int>(Hash.GET_VEHICLE_XENON_LIGHT_COLOR_INDEX, currentVehicle);
            // The color the player has actually committed to (bought/applied). Hover only PREVIEWS; if they back out
            // without applying, we revert to this on close so the preview doesn't stick.
            int committedColor = currentColor;

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
                    if (EditBlockApply()) return;
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

                    // Apply color — now it's committed, so the close-revert keeps it.
                    Function.Call(Hash.SET_VEHICLE_XENON_LIGHT_COLOR_INDEX, currentVehicle, idx);
                    committedColor = idx;
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

            menu.Closed += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                {
                    // Revert any un-bought preview back to the committed color, then restore auto light mode.
                    if (Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, currentVehicle, 22))
                        Function.Call(Hash.SET_VEHICLE_XENON_LIGHT_COLOR_INDEX, currentVehicle, committedColor);
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

        /// <summary>Clear ELSC saves + package files tied to Menyoo's default placeholder plate ("menyoo").
        /// Save entries are keyed by plate; package files store the captured plateText. Safe to run every load.</summary>
        private void PurgeMenyooData()
        {
            try
            {
                int vehicles = VehicleSaveData.PurgeVehiclesByPlate("menyoo");

                int pkgs = 0;
                if (Directory.Exists(PackagesDirectory))
                {
                    foreach (var file in Directory.GetFiles(PackagesDirectory, "*.json"))
                    {
                        try
                        {
                            var p = LoadPackageData(file);
                            if (p != null && !string.IsNullOrEmpty(p.plateText)
                                && string.Equals(p.plateText.Trim(), "menyoo", StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(file);
                                pkgs++;
                            }
                        }
                        catch { }
                    }
                }

                if (vehicles > 0 || pkgs > 0)
                    Log($"[Cleanup] Removed {vehicles} menyoo save(s) + {pkgs} menyoo package(s)");
            }
            catch (Exception ex) { Log($"[Cleanup] menyoo purge error: {ex.Message}"); }
        }

        /// <summary>
        /// Create menu for saving/loading vehicle mod packages (Side Course feature)
        /// </summary>
        private NativeMenu CreatePackagesMenu()
        {
            var menu = CreateMenu("PACKAGES");

            // Save current mods option
            var saveItem = new NativeItem("Save Current Mods");
            saveItem.Description = "Save all current modifications to a package file (you'll be asked to name it)";
            saveItem.Activated += (s, e) =>
            {
                BeginNamePackage();
            };
            menu.Add(saveItem);

            menu.Add(new NativeSeparatorItem());

            // Load saved packages
            LoadPackageItems(menu);

            // Preview-on-hover: snapshot the car's mods when the menu opens, live-preview the highlighted package,
            // commit on select, and revert to the snapshot on close (unless a package was committed).
            menu.Shown += (s, e) => { CaptureVehicleModSnapshot(); _packageCommitted = false; PreviewSelectedPackage(); };
            menu.SelectedIndexChanged += (s, e) => PreviewSelectedPackage();
            menu.Closed += (s, e) => { if (!_packageCommitted) RestoreVehicleModSnapshot(); };

            return menu;
        }

        /// <summary>
        /// Load package files and add them to the menu
        /// </summary>
        private void LoadPackageItems(NativeMenu menu)
        {
            _packageItemPaths.Clear();   // rebuilding the list -> drop stale item->path mappings
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
                    // Status, mirroring other ELSC items: this car's last-applied package -> equipped (garage) icon;
                    // else if you own every part in it (offset $0) -> owned tick; else show the cost you'd pay.
                    string equippedPkg = currentVehicle != null ? VehicleSaveData.GetEquippedPackage(VehicleKey(currentVehicle)) : null;
                    bool isEquipped = string.Equals(equippedPkg, displayName, StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        var pkg = LoadPackageData(filePath);
                        if (pkg == null) { packageItem.AltTitle = ">>"; packageItem.Description = $"Couldn't read {displayName}."; }
                        else
                        {
                            int offset = ComputePackageOffset(pkg);
                            if (isEquipped)
                            {
                                packageItem.AltTitle = ""; itemOwnershipStatus[packageItem] = STATUS_INSTALLED;
                                packageItem.Description = $"Equipped. Hover to preview; select to re-apply. Press {Glyph((int)GTA.Control.FrontendX)} to delete.";
                            }
                            else if (offset > 0)
                            {
                                packageItem.AltTitle = $"${offset:N0}";
                                packageItem.Description = $"Hover to preview; select to buy & apply (${offset:N0} for parts you don't own). Press {Glyph((int)GTA.Control.FrontendX)} to delete.";
                            }
                            else
                            {
                                packageItem.AltTitle = ""; itemOwnershipStatus[packageItem] = STATUS_OWNED;
                                packageItem.Description = $"Owned. Hover to preview; select to apply (free). Press {Glyph((int)GTA.Control.FrontendX)} to delete.";
                            }
                        }
                    }
                    catch { packageItem.AltTitle = ">>"; }
                    _packageItemPaths[packageItem] = filePath;   // for hover preview + X-to-delete
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
        // Prompt the user to name the package (on-screen keyboard), then SaveVehiclePackage(name).
        private void BeginNamePackage()
        {
            if (isNamingPackage) return;
            if (currentVehicle == null || !currentVehicle.Exists())
            {
                ShowNotification("~r~No vehicle to save!");
                return;
            }
            isNamingPackage = true; keyboardCheckCooldown = 5;
            Function.Call(Hash.DISPLAY_ONSCREEN_KEYBOARD, 0, "FMMC_KEY_TIP8", "", "", "", "", "", 32);
            ShowNotification("~y~Name this package (same name overwrites)");
        }

        private void UpdateNamePackage()
        {
            if (!isNamingPackage) return;
            if (keyboardCheckCooldown > 0) { keyboardCheckCooldown--; return; }
            keyboardCheckCooldown = 5;
            int status = Function.Call<int>(Hash.UPDATE_ONSCREEN_KEYBOARD);
            if (status == 1)
            {
                string result = (Function.Call<string>(Hash.GET_ONSCREEN_KEYBOARD_RESULT) ?? "").Trim();
                isNamingPackage = false;
                if (string.IsNullOrEmpty(result)) { ShowNotification("~r~Package not saved (empty name)."); return; }
                SaveVehiclePackage(result);
            }
            else if (status == 2) { isNamingPackage = false; }   // cancelled
        }

        // Turn a user-typed package name into a safe filename token (so the same name maps to the same file).
        private string SanitizePackageName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
                sb.Append((char.IsLetterOrDigit(c) || c == ' ' || c == '-') ? c : '_');
            string clean = sb.ToString().Trim();
            return string.IsNullOrEmpty(clean) ? "Package" : clean;
        }

        private void SaveVehiclePackage(string packageName)
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
                string safeName = SanitizePackageName(packageName);
                // Same vehicle + same name => identical path => File.WriteAllText overwrites the old package.
                string fileName = $"{vehicleModel}_{safeName}.json";
                string filePath = Path.Combine(PackagesDirectory, fileName);
                bool overwrite = File.Exists(filePath);

                // Capture a complete, shareable snapshot of EVERYTHING ELSC can track, then serialize with Json.NET.
                var pkg = CaptureCurrentPackage();
                File.WriteAllText(filePath, JsonConvert.SerializeObject(pkg, Formatting.Indented));

                // You built this car, so you OWN everything in the package you just saved — mark it owned for THIS
                // car (so re-applying it is free, $0 offset) and flag it as the car's equipped build (the car
                // currently IS this snapshot). Without this, parts not bought through ELSC's buy flow (e.g. a
                // Menyoo-modded or spawned car) would read as un-owned and wrongly charge on re-apply.
                MarkPackageOwned(pkg);
                VehicleSaveData.SetEquippedPackage(VehicleKey(currentVehicle), safeName);

                ShowNotification(overwrite ? $"~g~Package overwritten: {safeName}" : $"~g~Package saved: {safeName}");
                Log($"Package saved to: {filePath}");

                // Refresh the Packages submenu in place so the new/updated package shows in the load list.
                RefreshPackagesMenu();
            }
            catch (Exception ex)
            {
                ShowNotification("~r~Failed to save package!");
                Log($"Error saving package: {ex.Message}");
            }
        }

        // Rebuild the Packages submenu's items (Save + separator + saved packages) without re-creating the menu.
        private void RefreshPackagesMenu()
        {
            if (packagesMenu == null) return;
            packagesMenu.Clear();

            var saveItem = new NativeItem("Save Current Mods");
            saveItem.Description = "Save all current modifications to a package file (you'll be asked to name it)";
            saveItem.Activated += (s, e) => { BeginNamePackage(); };
            packagesMenu.Add(saveItem);

            packagesMenu.Add(new NativeSeparatorItem());
            LoadPackageItems(packagesMenu);
        }

        // Toggle-type mod slots are driven by TOGGLE_VEHICLE_MOD, not SET_VEHICLE_MOD.
        private static bool IsToggleModSlot(int slot) => slot >= 17 && slot <= 22;

        // Neon natives (raw hashes, mirroring the ones used in CreateNeonColorMenuFromConfig).
        private const ulong NEON_IS_ENABLED   = 0x8C4B92553E4766A5;  // _IS_VEHICLE_NEON_LIGHT_ENABLED(veh, index)
        private const ulong NEON_GET_COLOUR   = 0x7619EEE8C886757F;  // _GET_VEHICLE_NEON_LIGHTS_COLOUR(veh, &r,&g,&b)
        private const ulong NEON_SET_ENABLED  = 0x2AA720E4287BF269;  // _SET_VEHICLE_NEON_LIGHT_ENABLED(veh, index, on)
        private const ulong NEON_SET_COLOUR   = 0x8E0A582209A62695;  // _SET_VEHICLE_NEON_LIGHTS_COLOUR(veh, r,g,b)

        #region Vehicle Package — data model

        /// <summary>
        /// A complete, shareable build snapshot of everything ELSC can track for one vehicle.
        /// VISUAL fields are applied for hover-preview AND restore; ELSC fields are applied only on commit.
        /// </summary>
        private class VehiclePackageData
        {
            public string vehicle;          // model token
            public string created;          // timestamp

            // ---- VISUAL (native, applied on preview + restore) ----
            public Dictionary<int, int> mods = new Dictionary<int, int>();  // non-toggle slots 0-48, value>=0
            public int wheelType;
            public bool turbo, xenon, tireSmoke;
            public int primaryColor, secondaryColor, pearlColor, rimColor;
            public int primaryCustomArgb, secondaryCustomArgb;  // 0 = not a custom colour
            public int tireSmokeArgb;
            public bool neonLeft, neonRight, neonFront, neonBack;
            public int neonArgb;
            public int windowTint;
            public int livery = -1;
            public string plateText;
            public int plateStyle;
            public List<int> extras = new List<int>();   // extra ids that are ON (1-14)

            // ---- ELSC (applied only on commit) ----
            public VehicleSaveData.FitmentData fitment;
            public bool wheelFitmentOwned, customSuspensionEquipped;
            public VehicleSaveData.TuningData tuning;
            public bool mtOwned, mtEquipped;
            public int nosOwnedTiers, nosEquippedTier = -1;
            public string engineSwapId;
            public int customWindowColor;   // AARRGGBB (0 = none)
            public string speedoConfig;     // global speedo look (style/units/per-style colours) JSON, from Speedo.ExportConfig
        }

        #endregion

        #region Vehicle Package — capture

        /// <summary>Build a complete package snapshot from the live currentVehicle. Each native/getter group is
        /// wrapped so one failure can't abort the rest.</summary>
        private VehiclePackageData CaptureCurrentPackage()
        {
            var p = new VehiclePackageData
            {
                vehicle = currentVehicle != null ? currentVehicle.Model.ToString() : "",
                created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            if (currentVehicle == null || !currentVehicle.Exists()) return p;
            var v = currentVehicle;
            string vn = VehicleKey(v);

            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v, 0); } catch (Exception ex) { Log($"[Pkg] mod kit: {ex.Message}"); }

            // Mods (non-toggle slots only)
            try
            {
                for (int i = 0; i <= 48; i++)
                {
                    if (IsToggleModSlot(i)) continue;
                    int val = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, i);
                    if (val >= 0) p.mods[i] = val;
                }
            }
            catch (Exception ex) { Log($"[Pkg] mods: {ex.Message}"); }

            try { p.wheelType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, v); } catch (Exception ex) { Log($"[Pkg] wheelType: {ex.Message}"); }

            // Toggle mods
            try { p.turbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v, 18); } catch (Exception ex) { Log($"[Pkg] turbo: {ex.Message}"); }
            try { p.xenon = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v, 22); } catch (Exception ex) { Log($"[Pkg] xenon: {ex.Message}"); }
            try { p.tireSmoke = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, v, 20); } catch (Exception ex) { Log($"[Pkg] tireSmoke: {ex.Message}"); }

            // Colours
            try { unsafe { int a, b; Function.Call(Hash.GET_VEHICLE_COLOURS, v, &a, &b); p.primaryColor = a; p.secondaryColor = b; } }
            catch (Exception ex) { Log($"[Pkg] colours: {ex.Message}"); }
            try { unsafe { int pe, rm; Function.Call(Hash.GET_VEHICLE_EXTRA_COLOURS, v, &pe, &rm); p.pearlColor = pe; p.rimColor = rm; } }
            catch (Exception ex) { Log($"[Pkg] extraColours: {ex.Message}"); }

            // Custom RGB colours
            try
            {
                if (Function.Call<bool>(Hash.GET_IS_VEHICLE_PRIMARY_COLOUR_CUSTOM, v))
                    unsafe { int r, g, b; Function.Call(Hash.GET_VEHICLE_CUSTOM_PRIMARY_COLOUR, v, &r, &g, &b); p.primaryCustomArgb = PackRgb(r, g, b); }
            }
            catch (Exception ex) { Log($"[Pkg] customPrimary: {ex.Message}"); }
            try
            {
                if (Function.Call<bool>(Hash.GET_IS_VEHICLE_SECONDARY_COLOUR_CUSTOM, v))
                    unsafe { int r, g, b; Function.Call(Hash.GET_VEHICLE_CUSTOM_SECONDARY_COLOUR, v, &r, &g, &b); p.secondaryCustomArgb = PackRgb(r, g, b); }
            }
            catch (Exception ex) { Log($"[Pkg] customSecondary: {ex.Message}"); }

            // Tyre smoke colour
            try { unsafe { int r, g, b; Function.Call(Hash.GET_VEHICLE_TYRE_SMOKE_COLOR, v, &r, &g, &b); p.tireSmokeArgb = PackRgb(r, g, b); } }
            catch (Exception ex) { Log($"[Pkg] tyreSmokeColor: {ex.Message}"); }

            // Neon
            try
            {
                p.neonLeft  = Function.Call<bool>((Hash)NEON_IS_ENABLED, v, 0);
                p.neonRight = Function.Call<bool>((Hash)NEON_IS_ENABLED, v, 1);
                p.neonFront = Function.Call<bool>((Hash)NEON_IS_ENABLED, v, 2);
                p.neonBack  = Function.Call<bool>((Hash)NEON_IS_ENABLED, v, 3);
                unsafe { int r, g, b; Function.Call((Hash)NEON_GET_COLOUR, v, &r, &g, &b); p.neonArgb = PackRgb(r, g, b); }
            }
            catch (Exception ex) { Log($"[Pkg] neon: {ex.Message}"); }

            try { p.windowTint = Function.Call<int>(Hash.GET_VEHICLE_WINDOW_TINT, v); } catch (Exception ex) { Log($"[Pkg] windowTint: {ex.Message}"); }
            try { p.livery = Function.Call<int>(Hash.GET_VEHICLE_LIVERY, v); } catch (Exception ex) { Log($"[Pkg] livery: {ex.Message}"); }
            try { p.plateText = Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v); } catch (Exception ex) { Log($"[Pkg] plateText: {ex.Message}"); }
            try { p.plateStyle = Function.Call<int>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, v); } catch (Exception ex) { Log($"[Pkg] plateStyle: {ex.Message}"); }

            // Extras (1-14)
            try
            {
                for (int i = 1; i <= 14; i++)
                    if (Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v, i) && Function.Call<bool>(Hash.IS_VEHICLE_EXTRA_TURNED_ON, v, i))
                        p.extras.Add(i);
            }
            catch (Exception ex) { Log($"[Pkg] extras: {ex.Message}"); }

            // ---- ELSC state (from save data) ----
            try
            {
                // Prefer the LIVE stance — so the snapshot restores the actual current stance and a save captures
                // exactly what's on the car — falling back to saved fitment data when the controller isn't bound.
                if (wheelFitment != null && wheelFitment.IsInitialized && lastFitmentVehicle == currentVehicle)
                    p.fitment = new VehicleSaveData.FitmentData
                    {
                        FrontCamber = wheelFitment.FrontCamber, RearCamber = wheelFitment.RearCamber,
                        FrontTrackWidth = wheelFitment.FrontTrackWidth, RearTrackWidth = wheelFitment.RearTrackWidth,
                        FrontHeight = wheelFitment.FrontHeight, RearHeight = wheelFitment.RearHeight,
                        Rake = wheelFitment.Rake, StockFake = wheelFitment.StockFake,
                        VisualSize = wheelFitment.VisualSize, VisualWidth = wheelFitment.VisualWidth
                    };
                else p.fitment = VehicleSaveData.GetFitmentData(vn);
            }
            catch (Exception ex) { Log($"[Pkg] fitment: {ex.Message}"); }
            try { p.wheelFitmentOwned = VehicleSaveData.IsWheelFitmentOwned(vn); } catch (Exception ex) { Log($"[Pkg] fitmentOwned: {ex.Message}"); }
            try { p.customSuspensionEquipped = VehicleSaveData.IsCustomSuspensionEquipped(vn); } catch (Exception ex) { Log($"[Pkg] customSusp: {ex.Message}"); }
            try { p.tuning = VehicleSaveData.GetTuningData(vn); } catch (Exception ex) { Log($"[Pkg] tuning: {ex.Message}"); }
            try { p.mtOwned = VehicleSaveData.IsManualTransmissionOwned(vn); } catch (Exception ex) { Log($"[Pkg] mtOwned: {ex.Message}"); }
            try { p.mtEquipped = VehicleSaveData.IsManualTransmissionEquipped(vn); } catch (Exception ex) { Log($"[Pkg] mtEquipped: {ex.Message}"); }
            try { p.nosOwnedTiers = VehicleSaveData.GetNosOwnedTiers(vn); } catch (Exception ex) { Log($"[Pkg] nosOwned: {ex.Message}"); }
            try { p.nosEquippedTier = VehicleSaveData.GetNosEquippedTier(vn); } catch (Exception ex) { Log($"[Pkg] nosEquipped: {ex.Message}"); }
            try { p.engineSwapId = VehicleSaveData.GetEngineSwapId(vn); } catch (Exception ex) { Log($"[Pkg] engineSwap: {ex.Message}"); }
            try { p.customWindowColor = VehicleSaveData.GetCustomWindowColor(vn); } catch (Exception ex) { Log($"[Pkg] customWindow: {ex.Message}"); }
            try { p.speedoConfig = Speedo.ExportConfig(); } catch (Exception ex) { Log($"[Pkg] speedo: {ex.Message}"); }   // global speedo look (style/units/colours)

            return p;
        }

        // ARGB pack with full alpha; RGB unpackers.
        private static int PackRgb(int r, int g, int b) => unchecked((int)(0xFF000000u | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF)));
        private static void UnpackRgb(int argb, out int r, out int g, out int b) { r = (argb >> 16) & 0xFF; g = (argb >> 8) & 0xFF; b = argb & 0xFF; }

        /// <summary>Deserialize a package file. Returns null on failure (e.g. an old hand-rolled JSON file).</summary>
        private VehiclePackageData LoadPackageData(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);
                return JsonConvert.DeserializeObject<VehiclePackageData>(json);
            }
            catch (Exception ex) { Log($"[Pkg] load '{filePath}': {ex.Message}"); return null; }
        }

        #endregion

        #region Vehicle Package — apply visual

        /// <summary>Apply ONLY the visual (native) fields of a package to the current vehicle. Used for hover
        /// preview AND restore. Each group is wrapped so one failure can't abort the rest.</summary>
        private void ApplyPackageVisual(VehiclePackageData p, bool livePreview = false)
        {
            if (p == null || currentVehicle == null || !currentVehicle.Exists()) return;
            var v = currentVehicle;

            try { Function.Call(Hash.SET_VEHICLE_MOD_KIT, v, 0); } catch (Exception ex) { Log($"[Pkg] apply mod kit: {ex.Message}"); }

            try { if (p.wheelType >= 0) Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, v, p.wheelType); } catch (Exception ex) { Log($"[Pkg] apply wheelType: {ex.Message}"); }

            // Mods: write every non-toggle slot. Slots absent from the package are reset to stock (-1) so a
            // preview/restore doesn't leave parts from the previous state behind.
            try
            {
                for (int i = 0; i <= 48; i++)
                {
                    if (IsToggleModSlot(i)) continue;
                    int val = (p.mods != null && p.mods.TryGetValue(i, out int mv)) ? mv : -1;
                    Function.Call(Hash.SET_VEHICLE_MOD, v, i, val, false);
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply mods: {ex.Message}"); }

            try { Function.Call(Hash.TOGGLE_VEHICLE_MOD, v, 18, p.turbo); } catch (Exception ex) { Log($"[Pkg] apply turbo: {ex.Message}"); }
            try { Function.Call(Hash.TOGGLE_VEHICLE_MOD, v, 22, p.xenon); } catch (Exception ex) { Log($"[Pkg] apply xenon: {ex.Message}"); }

            try
            {
                Function.Call(Hash.TOGGLE_VEHICLE_MOD, v, 20, p.tireSmoke);
                if (p.tireSmoke && p.tireSmokeArgb != 0)
                {
                    UnpackRgb(p.tireSmokeArgb, out int r, out int g, out int b);
                    Function.Call(Hash.SET_VEHICLE_TYRE_SMOKE_COLOR, v, r, g, b);
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply tireSmoke: {ex.Message}"); }

            try { Function.Call(Hash.SET_VEHICLE_COLOURS, v, p.primaryColor, p.secondaryColor); } catch (Exception ex) { Log($"[Pkg] apply colours: {ex.Message}"); }
            try { Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, v, p.pearlColor, p.rimColor); } catch (Exception ex) { Log($"[Pkg] apply extraColours: {ex.Message}"); }

            try { if (p.primaryCustomArgb != 0) { UnpackRgb(p.primaryCustomArgb, out int r, out int g, out int b); Function.Call(Hash.SET_VEHICLE_CUSTOM_PRIMARY_COLOUR, v, r, g, b); } }
            catch (Exception ex) { Log($"[Pkg] apply customPrimary: {ex.Message}"); }
            try { if (p.secondaryCustomArgb != 0) { UnpackRgb(p.secondaryCustomArgb, out int r, out int g, out int b); Function.Call(Hash.SET_VEHICLE_CUSTOM_SECONDARY_COLOUR, v, r, g, b); } }
            catch (Exception ex) { Log($"[Pkg] apply customSecondary: {ex.Message}"); }

            // Neon
            try
            {
                if (p.neonArgb != 0) { UnpackRgb(p.neonArgb, out int r, out int g, out int b); Function.Call((Hash)NEON_SET_COLOUR, v, r, g, b); }
                Function.Call((Hash)NEON_SET_ENABLED, v, 0, p.neonLeft);
                Function.Call((Hash)NEON_SET_ENABLED, v, 1, p.neonRight);
                Function.Call((Hash)NEON_SET_ENABLED, v, 2, p.neonFront);
                Function.Call((Hash)NEON_SET_ENABLED, v, 3, p.neonBack);
            }
            catch (Exception ex) { Log($"[Pkg] apply neon: {ex.Message}"); }

            // Window tint — incl. the ELSC custom glass colour. During a live hover preview, drive it through the
            // tint manager's exclusive PREVIEW slot so its per-frame AssertCar loop doesn't fight us; on restore/
            // commit, release the preview so AssertCar re-asserts the car's real (saved) colour.
            try
            {
                if (livePreview && windowTint != null && windowTint.CanPreview)
                {
                    windowTint.BeginPreview(v);
                    if (p.customWindowColor != 0) windowTint.PreviewColor(v, p.customWindowColor);
                    else Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, p.windowTint);
                }
                else
                {
                    if (windowTint != null) windowTint.EndPreview();
                    Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, p.windowTint);
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply windowTint: {ex.Message}"); }
            try { if (p.livery >= 0) Function.Call(Hash.SET_VEHICLE_LIVERY, v, p.livery); } catch (Exception ex) { Log($"[Pkg] apply livery: {ex.Message}"); }
            // NOTE: deliberately do NOT apply p.plateText. The plate TEXT is the car's per-instance identity (the
            // VehicleKey). Cloning the package author's plate onto the car would change its identity and orphan the
            // ownership of any OTHER packages bought for this car. We keep the car's own plate; only the cosmetic
            // plate STYLE (background) travels with the package.
            try { Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT_INDEX, v, p.plateStyle); } catch (Exception ex) { Log($"[Pkg] apply plateStyle: {ex.Message}"); }

            // Extras (1-14): turn on the ones in the package, off otherwise. SET_VEHICLE_EXTRA's flag is INVERTED.
            try
            {
                for (int i = 1; i <= 14; i++)
                {
                    if (!Function.Call<bool>(Hash.DOES_EXTRA_EXIST, v, i)) continue;
                    bool on = p.extras != null && p.extras.Contains(i);
                    Function.Call(Hash.SET_VEHICLE_EXTRA, v, i, !on);
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply extras: {ex.Message}"); }

            // Stance/fitment (camber, ride height, rake, track, wheel size/width) — push the package's values onto
            // the live fitment controller so the preview/restore shows the actual stance, not just the wheel design.
            // Done after the wheel mod/type above so VisualSize/Width have the right rims to scale. The per-frame
            // wheelFitment.Update() applies these; restore writes the snapshot's values back the same way.
            try
            {
                if (p.fitment != null && wheelFitment != null)
                {
                    if (!wheelFitment.IsInitialized || lastFitmentVehicle != currentVehicle) InitFitmentForVehicle();
                    if (wheelFitment.IsInitialized)
                    {
                        wheelFitment.FrontCamber = p.fitment.FrontCamber;
                        wheelFitment.RearCamber = p.fitment.RearCamber;
                        wheelFitment.FrontTrackWidth = p.fitment.FrontTrackWidth;
                        wheelFitment.RearTrackWidth = p.fitment.RearTrackWidth;
                        wheelFitment.FrontHeight = p.fitment.FrontHeight;
                        wheelFitment.RearHeight = p.fitment.RearHeight;
                        wheelFitment.Rake = p.fitment.Rake;
                        wheelFitment.StockFake = p.fitment.StockFake;
                        wheelFitment.VisualSize = p.fitment.VisualSize;
                        wheelFitment.VisualWidth = p.fitment.VisualWidth;
                    }
                }
            }
            catch (Exception ex) { Log($"[Pkg] apply fitment: {ex.Message}"); }

            // Speedometer look (global: style/units/colours). Apply IN-MEMORY (no file write) so a hover preview /
            // restore can show it without persisting; on a live preview, set Speedo.Preview to the package's active
            // style so the menu's demo gauge renders it. Restore (livePreview=false, p=snapshot) reverts + clears
            // the preview; the actual persist happens in ApplyPackageElsc on commit.
            try
            {
                if (!string.IsNullOrEmpty(p.speedoConfig))
                {
                    Speedo.ImportConfig(p.speedoConfig, persist: false);
                    Speedo.Preview = livePreview ? Speedo.ActiveStyleOf(p.speedoConfig) : null;
                }
                else if (!livePreview) Speedo.Preview = null;
            }
            catch (Exception ex) { Log($"[Pkg] apply speedo: {ex.Message}"); }
        }

        #endregion

        #region Vehicle Package — apply ELSC features (commit only)

        /// <summary>Apply the package's ELSC features (tuning, MT, custom suspension/fitment, NOS, engine swap,
        /// custom window glass). Writes into VehicleSaveData via its setters then re-uses the existing apply
        /// paths. Each feature wrapped so one failure can't abort the rest.</summary>
        private void ApplyPackageElsc(VehiclePackageData p)
        {
            if (p == null || currentVehicle == null || !currentVehicle.Exists()) return;
            string vn = VehicleKey(currentVehicle);

            // Wheel fitment / custom suspension
            try
            {
                if (p.fitment != null && (p.wheelFitmentOwned || p.customSuspensionEquipped))
                {
                    VehicleSaveData.SetFitmentData(vn, p.fitment);
                    VehicleSaveData.SetWheelFitmentOwned(vn, true);
                    // Mirror InitFitmentForVehicle: push the saved values onto the live fitment controller.
                    if (!wheelFitment.IsInitialized || lastFitmentVehicle != currentVehicle)
                        InitFitmentForVehicle();
                    if (wheelFitment.IsInitialized)
                    {
                        wheelFitment.FrontCamber = p.fitment.FrontCamber;
                        wheelFitment.RearCamber = p.fitment.RearCamber;
                        wheelFitment.FrontTrackWidth = p.fitment.FrontTrackWidth;
                        wheelFitment.RearTrackWidth = p.fitment.RearTrackWidth;
                        wheelFitment.FrontHeight = p.fitment.FrontHeight;
                        wheelFitment.RearHeight = p.fitment.RearHeight;
                        wheelFitment.Rake = p.fitment.Rake;
                        wheelFitment.StockFake = p.fitment.StockFake;
                        wheelFitment.VisualSize = p.fitment.VisualSize;
                        wheelFitment.VisualWidth = p.fitment.VisualWidth;
                    }
                    if (p.customSuspensionEquipped) EquipCustomSuspension();   // drops vanilla level, flags equipped, re-applies ride height
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc fitment: {ex.Message}"); }

            // Engine swap (resolve id -> EngineSwap)
            try
            {
                if (!string.IsNullOrEmpty(p.engineSwapId))
                {
                    var swap = EngineSwaps.Lookup(p.engineSwapId);
                    if (swap != null) { ApplyEngineSwap(swap); VehicleSaveData.SetEngineSwapId(vn, swap.Id); }
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc engineSwap: {ex.Message}"); }

            // Manual transmission
            try
            {
                if (p.mtOwned)
                {
                    VehicleSaveData.SetManualTransmissionOwned(vn, true);
                    if (p.mtEquipped && ELSCTransmission.IsAvailable) EquipManualTransmission();   // sets equipped + enables shifting + rebuilds
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc mt: {ex.Message}"); }

            // Nitrous
            try
            {
                if (p.nosOwnedTiers != 0)
                {
                    VehicleSaveData.SetNosOwnedTiers(vn, p.nosOwnedTiers);
                    VehicleSaveData.SetNosEquippedTier(vn, p.nosEquippedTier);
                    if (elscTransmission != null)
                    {
                        elscTransmission.NosInstalled = p.nosEquippedTier >= 0;
                        if (p.nosEquippedTier >= 0) elscTransmission.ActiveFxIndex = p.nosEquippedTier;
                    }
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc nos: {ex.Message}"); }

            // Handling tune
            try
            {
                if (p.tuning != null)
                {
                    VehicleSaveData.SetTuningData(vn, p.tuning);
                    currentTuning = p.tuning;
                    InitTuningForVehicle();   // caches stock + applies the tune
                    ApplyTuning();
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc tuning: {ex.Message}"); }

            // Custom window glass colour
            try
            {
                if (p.customWindowColor != 0 && windowTint != null && windowTint.Ready)
                    windowTint.ApplyCustomColor(currentVehicle, p.customWindowColor);
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc windowGlass: {ex.Message}"); }

            // Speedometer look (global) — persist it now (the active style is applied only if owned). Also record
            // the package's style PER-CAR so the gauge sticks on this car (and only this car).
            try
            {
                if (!string.IsNullOrEmpty(p.speedoConfig))
                {
                    Speedo.Preview = null;
                    Speedo.ImportConfig(p.speedoConfig, persist: true);
                    if (currentVehicle != null && currentVehicle.Exists())
                    {
                        VehicleSaveData.SetSpeedoStyle(VehicleKey(currentVehicle), (int)Speedo.Active);
                        SyncSpeedoToCar(currentVehicle);
                    }
                }
            }
            catch (Exception ex) { Log($"[Pkg] applyElsc speedo: {ex.Message}"); }

            try { VehicleSaveData.Save(); } catch (Exception ex) { Log($"[Pkg] applyElsc save: {ex.Message}"); }
        }

        #endregion

        #region Vehicle Package — cost offset

        /// <summary>Total cost of every part in the package that the player does NOT already own for this
        /// vehicle (owned => 0). Approximate where the buy flow doesn't track per-part ownership.</summary>
        private int ComputePackageOffset(VehiclePackageData p)
        {
            if (ModSettings.AllItemsFree) return 0;   // INI: everything's free
            if (p == null || currentVehicle == null || !currentVehicle.Exists()) return 0;
            string vn = VehicleKey(currentVehicle);
            int total = 0;

            // Mods (cosmetic + performance). value 0 on a cosmetic slot is usually "stock part" — only charge
            // for installed (>=0) parts the player doesn't already own.
            try
            {
                if (p.mods != null)
                    foreach (var kv in p.mods)
                    {
                        int slot = kv.Key, val = kv.Value;
                        if (val < 0) continue;
                        if (!VehicleSaveData.IsModOwned(vn, slot, val))
                            total += ModPricing.GetModPriceByIndex(slot, val);
                    }
            }
            catch (Exception ex) { Log($"[Pkg] offset mods: {ex.Message}"); }

            // Wheels (slot 23 value) — charge per the wheel category if a non-stock design is set and unowned.
            try
            {
                if (p.mods != null && p.mods.TryGetValue(23, out int wIdx) && wIdx >= 0)
                {
                    int ownKey = WheelOwnKey(p.wheelType, wIdx);
                    if (!VehicleSaveData.IsModOwned(vn, 23, ownKey))
                        total += ModPricing.GetWheelPrice(p.wheelType, wIdx);
                }
            }
            catch (Exception ex) { Log($"[Pkg] offset wheels: {ex.Message}"); }

            // Toggle mods
            try { if (p.turbo && !VehicleSaveData.IsTurboOwned(vn)) total += ModPricing.TurboPrice; } catch (Exception ex) { Log($"[Pkg] offset turbo: {ex.Message}"); }
            try { if (p.xenon && !VehicleSaveData.IsXenonOwned(vn)) total += ModPricing.XenonLightsPrice; } catch (Exception ex) { Log($"[Pkg] offset xenon: {ex.Message}"); }

            // Window tint (preset index)
            try
            {
                if (p.windowTint > 0 && !VehicleSaveData.IsTintOwned(vn, p.windowTint))
                {
                    int ti = p.windowTint;
                    total += (ti >= 0 && ti < ModPricing.WindowTintPrices.Length) ? ModPricing.WindowTintPrices[ti] : 0;
                }
            }
            catch (Exception ex) { Log($"[Pkg] offset tint: {ex.Message}"); }

            // Plate style
            try
            {
                if (p.plateStyle >= 0 && !VehicleSaveData.IsPlateOwned(vn, p.plateStyle))
                    total += (p.plateStyle < ModPricing.PlatePrices.Length) ? ModPricing.PlatePrices[p.plateStyle] : 0;
            }
            catch (Exception ex) { Log($"[Pkg] offset plate: {ex.Message}"); }

            // Livery (slot 48 in ELSC's ownership map)
            try
            {
                if (p.livery >= 0 && !VehicleSaveData.IsModOwned(vn, 48, p.livery))
                    total += ModPricing.GetModPriceByIndex(48, p.livery);
            }
            catch (Exception ex) { Log($"[Pkg] offset livery: {ex.Message}"); }

            // ---- ELSC features ----
            try { if ((p.wheelFitmentOwned || p.customSuspensionEquipped) && !VehicleSaveData.IsWheelFitmentOwned(vn)) total += ModPricing.CustomSuspensionPrice; } catch (Exception ex) { Log($"[Pkg] offset susp: {ex.Message}"); }
            try { if (p.mtOwned && !VehicleSaveData.IsManualTransmissionOwned(vn)) total += ModPricing.ManualTransmissionPrice; } catch (Exception ex) { Log($"[Pkg] offset mt: {ex.Message}"); }

            // NOS tiers: charge each owned tier in the package the player doesn't already own.
            try
            {
                int have = VehicleSaveData.GetNosOwnedTiers(vn);
                for (int tier = 0; tier < ModPricing.NosTierPrices.Length; tier++)
                {
                    bool inPkg = (p.nosOwnedTiers & (1 << tier)) != 0;
                    bool owned = (have & (1 << tier)) != 0;
                    if (inPkg && !owned) total += ModPricing.NosTierPrices[tier];
                }
            }
            catch (Exception ex) { Log($"[Pkg] offset nos: {ex.Message}"); }

            // Engine swap (no per-vehicle ownership tracking — charge unless this swap is already installed here).
            try
            {
                if (!string.IsNullOrEmpty(p.engineSwapId))
                {
                    var swap = EngineSwaps.Lookup(p.engineSwapId);
                    if (swap != null && VehicleSaveData.GetEngineSwapId(vn) != swap.Id) total += swap.Price;
                }
            }
            catch (Exception ex) { Log($"[Pkg] offset engineSwap: {ex.Message}"); }

            return total;
        }

        #endregion

        #region Vehicle Package — commit, snapshot, preview

        /// <summary>Commit a package: charge the cost offset (parts not yet owned), mark everything owned, then
        /// apply the full visual + ELSC build. Aborts (and reverts to snapshot) if the player can't afford it.</summary>
        private void LoadVehiclePackage(string filePath)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) { ShowNotification("~r~No vehicle!"); return; }

            var p = LoadPackageData(filePath);
            if (p == null) { ShowNotification("~r~Failed to load package!"); RestoreVehicleModSnapshot(); return; }

            string vn = VehicleKey(currentVehicle);
            int offset = ComputePackageOffset(p);

            // Charge (or block). TryPurchase returns false if it can't afford OR we're in edit mode — abort cleanly.
            if (offset > 0 && !TryPurchase(offset, false))
            {
                RestoreVehicleModSnapshot();   // undo any hover preview
                return;
            }
            else if (offset == 0 && EditBlockApply())   // free package still blocked while editing
            {
                RestoreVehicleModSnapshot();
                return;
            }

            _packageCommitted = true;   // keep it on close (don't revert the preview)

            // The car KEEPS its own plate (identity). We do NOT apply the package author's plate text (see
            // ApplyPackageVisual) and we no longer randomize it — so the car's per-instance key stays STABLE and
            // ownership ACCUMULATES across every package you buy for this car (buying a second leaves the first
            // owned, re-equippable for free). Apply ELSC features before deriving the final key, since the custom
            // window glass can re-plate the car on a collision; write ownership/equipped under that same final key.
            ApplyPackageVisual(p);
            ApplyPackageElsc(p);
            vn = VehicleKey(currentVehicle);   // FINAL per-instance key (after any window-glass re-plate)
            MarkPackageOwned(p);

            // Remember it as this car's equipped package (drives the garage marker) + refresh the list markers.
            VehicleSaveData.SetEquippedPackage(vn, PackageDisplayName(filePath));
            RefreshPackagesMenu();

            if (offset > 0) ShowNotification($"~g~Package applied~w~ — ${offset:N0} charged");
            else ShowNotification("~g~Package applied~w~ — all parts owned");
            Log($"Package committed from: {filePath} (offset ${offset})");
        }

        // Friendly package name from its file path: "{model}_{Name}.json" -> "Name".
        private string PackageDisplayName(string filePath)
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            return fileName.Contains("_") ? fileName.Substring(fileName.IndexOf("_") + 1) : fileName;
        }

        // ---- Delete a package: press X on a package row -> "are you sure?" prompt -> delete the file ----
        private NativeMenu _pkgDeleteMenu;
        private NativeItem _pkgDeleteItem;       // the "Delete" row (its title is re-stamped per target)
        private string _pkgDeleteTarget, _pkgDeleteName;

        // Called every frame while the LSC menu is up: X on a highlighted package opens the confirm prompt.
        private void CheckPackageDeleteInput()
        {
            if (packagesMenu == null || !packagesMenu.Visible) return;
            if (isWalkAroundActive) return;   // X opens doors in walk-around mode — don't let it delete a package
            if (_pkgDeleteMenu != null && _pkgDeleteMenu.Visible) return;   // already confirming
            var sel = packagesMenu.SelectedItem as NativeItem;
            if (sel == null || !_packageItemPaths.TryGetValue(sel, out string path)) return;
            bool xPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.FrontendX)
                         || Function.Call<bool>(Hash.IS_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.FrontendX);
            if (xPressed) OpenPackageDeleteConfirm(path, PackageDisplayName(path));
        }

        private void OpenPackageDeleteConfirm(string filePath, string displayName)
        {
            _pkgDeleteTarget = filePath; _pkgDeleteName = displayName;
            if (_pkgDeleteMenu == null)
            {
                _pkgDeleteMenu = CreateMenu("DELETE PACKAGE");
                var cancel = new NativeItem("Cancel", "Keep this package. (Back also cancels.)");
                cancel.Activated += (s, e) => ClosePackageDeleteConfirm();
                _pkgDeleteMenu.Add(cancel);
                _pkgDeleteItem = new NativeItem("Delete", "Permanently delete this package file. This cannot be undone.");
                _pkgDeleteItem.Activated += (s, e) =>
                {
                    try
                    {
                        if (File.Exists(_pkgDeleteTarget)) File.Delete(_pkgDeleteTarget);
                        if (currentVehicle != null && string.Equals(VehicleSaveData.GetEquippedPackage(VehicleKey(currentVehicle)), _pkgDeleteName, StringComparison.OrdinalIgnoreCase))
                            VehicleSaveData.SetEquippedPackage(VehicleKey(currentVehicle), null);
                        ShowNotification($"~g~Deleted package: {_pkgDeleteName}");
                    }
                    catch (Exception ex) { ShowNotification("~r~Failed to delete package!"); Log($"Delete package: {ex.Message}"); }
                    RefreshPackagesMenu();
                    ClosePackageDeleteConfirm();
                };
                _pkgDeleteMenu.Add(_pkgDeleteItem);
                _pkgDeleteMenu.Closed += (s, e) => { if (!isNavigatingMenu && packagesMenu != null) packagesMenu.Visible = true; };
            }
            _pkgDeleteItem.Title = $"Delete \"{displayName}\"";
            _pkgDeleteMenu.SelectedIndex = 0;   // default to Cancel (safer)
            isNavigatingMenu = true; packagesMenu.Visible = false; _pkgDeleteMenu.Visible = true; isNavigatingMenu = false;
        }

        private void ClosePackageDeleteConfirm()
        {
            if (_pkgDeleteMenu == null) return;
            isNavigatingMenu = true; _pkgDeleteMenu.Visible = false; if (packagesMenu != null) packagesMenu.Visible = true; isNavigatingMenu = false;
        }

        /// <summary>Mark every part in the package as owned for this vehicle (so re-applying is free).</summary>
        private void MarkPackageOwned(VehiclePackageData p)
        {
            if (p == null || currentVehicle == null || !currentVehicle.Exists()) return;
            string vn = VehicleKey(currentVehicle);
            try
            {
                if (p.mods != null)
                    foreach (var kv in p.mods)
                        if (kv.Value >= 0)
                        {
                            if (kv.Key == 23) VehicleSaveData.SetModOwned(vn, 23, WheelOwnKey(p.wheelType, kv.Value));
                            else VehicleSaveData.SetModOwned(vn, kv.Key, kv.Value);
                        }
            }
            catch (Exception ex) { Log($"[Pkg] own mods: {ex.Message}"); }
            try { if (p.turbo) VehicleSaveData.SetTurboOwned(vn); } catch (Exception ex) { Log($"[Pkg] own turbo: {ex.Message}"); }
            try { if (p.xenon) VehicleSaveData.SetXenonOwned(vn); } catch (Exception ex) { Log($"[Pkg] own xenon: {ex.Message}"); }
            try { if (p.windowTint > 0) VehicleSaveData.SetTintOwned(vn, p.windowTint); } catch (Exception ex) { Log($"[Pkg] own tint: {ex.Message}"); }
            try { if (p.plateStyle >= 0) VehicleSaveData.SetPlateOwned(vn, p.plateStyle); } catch (Exception ex) { Log($"[Pkg] own plate: {ex.Message}"); }
            try { if (p.livery >= 0) VehicleSaveData.SetModOwned(vn, 48, p.livery); } catch (Exception ex) { Log($"[Pkg] own livery: {ex.Message}"); }
            try { if (p.wheelFitmentOwned || p.customSuspensionEquipped) VehicleSaveData.SetWheelFitmentOwned(vn, true); } catch (Exception ex) { Log($"[Pkg] own susp: {ex.Message}"); }
            try { if (p.mtOwned) VehicleSaveData.SetManualTransmissionOwned(vn, true); } catch (Exception ex) { Log($"[Pkg] own mt: {ex.Message}"); }
            try { if (p.nosOwnedTiers != 0) VehicleSaveData.SetNosOwnedTiers(vn, VehicleSaveData.GetNosOwnedTiers(vn) | p.nosOwnedTiers); } catch (Exception ex) { Log($"[Pkg] own nos: {ex.Message}"); }
            try { VehicleSaveData.Save(); } catch (Exception ex) { Log($"[Pkg] own save: {ex.Message}"); }
        }

        /// <summary>Snapshot the car's current visual build (so a package preview can be reverted exactly).</summary>
        private void CaptureVehicleModSnapshot()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) { _packageSnapshot = null; return; }
            _packageSnapshot = CaptureCurrentPackage();
        }

        /// <summary>Put the car's visuals back to the snapshot taken when the Packages menu opened. (Functional
        /// ELSC features aren't touched during preview, so restoring visuals is sufficient.)</summary>
        private void RestoreVehicleModSnapshot()
        {
            if (_packageSnapshot == null || currentVehicle == null || !currentVehicle.Exists()) return;
            ApplyPackageVisual(_packageSnapshot);
        }

        /// <summary>Revert to the snapshot, then (if hovering a package row) apply that package's visuals as a
        /// silent live preview.</summary>
        private void PreviewSelectedPackage()
        {
            if (_packageSnapshot == null || packagesMenu == null) return;
            RestoreVehicleModSnapshot();   // clean baseline so previews don't stack
            var sel = packagesMenu.SelectedItem as NativeItem;
            if (sel != null && _packageItemPaths.TryGetValue(sel, out string path))
            {
                var p = LoadPackageData(path);
                if (p != null) ApplyPackageVisual(p, livePreview: true);
            }
        }

        #endregion

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

            string vn = VehicleKey(currentVehicle);
            bool owned = VehicleSaveData.IsManualTransmissionOwned(vn);
            bool equipped = owned && VehicleSaveData.IsManualTransmissionEquipped(vn);

            // Keep the live transmission state in sync with the saved equipped flag (persist across reloads /
            // vehicle switches): enabled only when this car has MT equipped.
            if (equipped && !elscTransmission.IsEnabled) { elscTransmission.SetVehicle(currentVehicle); elscTransmission.Enable(); }
            else if (!equipped && elscTransmission.IsEnabled) { elscTransmission.Disable(); }

            // Three states, mirroring Custom Suspension: NOT owned -> price; owned but NOT the active transmission
            // -> owned tick + "Select to equip"; owned AND equipped -> equipped garage icon + "Select to edit".
            var mtItem = new NativeItem("Manual Transmission");
            if (!owned)
            {
                mtItem.AltTitle = $"${ModPricing.ManualTransmissionPrice}";
                mtItem.Description = "Buy manual shifting — also unlocks the transmission tuning (Final Drive, Gears). You'll set your shift buttons.";
            }
            else if (equipped)
            {
                mtItem.AltTitle = "";
                itemOwnershipStatus[mtItem] = STATUS_INSTALLED;
                mtItem.Description = $"Shift Up: {ModSettings.ShiftUpKey} | Shift Down: {ModSettings.ShiftDownKey}  ·  Select to edit keys";
            }
            else
            {
                mtItem.AltTitle = "";
                itemOwnershipStatus[mtItem] = STATUS_OWNED;
                mtItem.Description = "Select to equip your manual transmission.";
            }

            mtItem.Activated += (s, e) =>
            {
                if (currentVehicle == null) return;
                string v = VehicleKey(currentVehicle);
                bool own = VehicleSaveData.IsManualTransmissionOwned(v);
                bool eq = own && VehicleSaveData.IsManualTransmissionEquipped(v);

                if (!own)
                {
                    // Charge, then configure keys; FinishMTBinding equips it (removes vanilla auto, rebuilds).
                    if (!TryPurchase(ModPricing.ManualTransmissionPrice, false)) return;
                    VehicleSaveData.SetManualTransmissionOwned(v, true);
                    mtBindingState = 1; mtBindingItem = mtItem;
                    return;
                }
                if (!eq)
                {
                    EquipManualTransmission();   // equip; select again to edit keys
                    return;
                }
                // Equipped -> edit keys (re-bind).
                mtBindingState = 1; mtBindingItem = mtItem;
            };
            menu.Add(mtItem);
        }

        #endregion

        private NativeMenu CreateLiveryMenu(int count, bool useNativeLivery)
        {
            var menu = CreateMenu("LIVERY");
            bool isNative = useNativeLivery;
            string vehicleName = VehicleKey(currentVehicle);

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
                if (EditBlockApply()) return;
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
                    if (EditBlockApply()) return;
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
                    string vehicleName = VehicleKey(currentVehicle);

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
        private int originalWheelColor = 0;   // preserve the rim color through a color preview/revert
        // Plate-style + neon-color live preview state (revert on close if not purchased)
        private int origPlateStyle = -1;
        private bool platePurchased = false;
        // Window-tint live preview state (revert on close if not purchased)
        private int origWindowTint = -1;
        private bool windowTintPurchased = false;
        private readonly int[] origNeonRGB = new int[3];
        private readonly bool[] origNeonOn = new bool[4];
        private bool neonColorPurchased = false;

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
                            originalWheelColor = wheel;   // remember the rim color so the revert doesn't wipe it
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
                        Function.Call(Hash.SET_VEHICLE_EXTRA_COLOURS, currentVehicle, originalPearlescent, originalWheelColor);
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

        // ---- Horn naming (GET_MOD_TEXT_LABEL is empty for horns, so replicate the game's LSC chain) ----
        // The real name is: GET_VEHICLE_MOD_IDENTIFIER_HASH(veh,14,idx) -> func_104 (audio-name hash -> canonical
        // index) -> func_1587 (canonical index -> GXT label) -> GetLocalizedString. The mod index is NOT the label
        // index (idx 0 is the Truck Horn, not "Stock"). Tables lifted from carmod_shop.c, verified vs live hashes.
        private static uint Joaat(string s)
        {
            uint h = 0;
            foreach (char c in s.ToLowerInvariant()) { h += c; h += h << 10; h ^= h >> 6; }
            h += h << 3; h ^= h >> 11; h += h << 15;
            return h;
        }
        private static readonly Dictionary<uint, int> _hornHashToCanon = BuildHornHashMap();
        private static Dictionary<uint, int> BuildHornHashMap()
        {
            var m = new Dictionary<uint, int>();
            void A(int c, string n) { m[Joaat(n)] = c; }
            A(1, "indep_horn_1"); A(2, "indep_horn_2"); A(3, "indep_horn_3"); A(4, "indep_horn_4");
            A(5, "hipster_horn_1"); A(6, "hipster_horn_2"); A(7, "hipster_horn_3"); A(8, "hipster_horn_4");
            A(9, "dlc_busi2_c_major_notes_c0"); A(10, "dlc_busi2_c_major_notes_d0"); A(11, "dlc_busi2_c_major_notes_e0");
            A(12, "dlc_busi2_c_major_notes_f0"); A(13, "dlc_busi2_c_major_notes_g0"); A(14, "dlc_busi2_c_major_notes_a0");
            A(15, "dlc_busi2_c_major_notes_b0"); A(16, "dlc_busi2_c_major_notes_c1");
            A(17, "musical_horn_business_1"); A(18, "musical_horn_business_2"); A(19, "musical_horn_business_3");
            A(20, "musical_horn_business_4"); A(21, "musical_horn_business_5"); A(22, "musical_horn_business_6");
            A(23, "musical_horn_business_7");
            A(24, "luxe_horn_2"); A(25, "luxe_horn_1"); A(26, "luxe_horn_3");
            A(27, "luxury_horn_2"); A(28, "luxory_horn_1"); A(29, "luxury_horn_3");
            A(30, "LOWRIDER_HORN_1"); A(31, "LOWRIDER_HORN_2"); A(34, "ORGAN_HORN_LOOP_01"); A(35, "ORGAN_HORN_LOOP_02");
            A(38, "XM15_HORN_01"); A(39, "XM15_HORN_02"); A(40, "XM15_HORN_03");
            A(44, "HORN_TRUCK"); A(45, "HORN_COP"); A(46, "HORN_CLOWN");
            A(47, "HORN_MUSICAL_1"); A(48, "HORN_MUSICAL_2"); A(49, "HORN_MUSICAL_3"); A(50, "HORN_MUSICAL_4"); A(51, "HORN_MUSICAL_5");
            A(52, "HORN_SAD_TROMBONE"); A(53, "dlc_aw_airhorn_01"); A(54, "dlc_aw_airhorn_02"); A(55, "dlc_aw_airhorn_03");
            return m;
        }
        private static string HornGxtLabel(int canon)
        {
            switch (canon)
            {
                case 1: return "HORN_INDI_1"; case 2: return "HORN_INDI_2"; case 3: return "HORN_INDI_3"; case 4: return "HORN_INDI_4";
                case 5: return "HORN_HIPS1"; case 6: return "HORN_HIPS2"; case 7: return "HORN_HIPS3"; case 8: return "HORN_HIPS4";
                case 9: return "HORN_CNOTE_C0"; case 10: return "HORN_CNOTE_D0"; case 11: return "HORN_CNOTE_E0"; case 12: return "HORN_CNOTE_F0";
                case 13: return "HORN_CNOTE_G0"; case 14: return "HORN_CNOTE_A0"; case 15: return "HORN_CNOTE_B0"; case 16: return "HORN_CNOTE_C1";
                case 17: return "HORN_CLAS1"; case 18: return "HORN_CLAS2"; case 19: return "HORN_CLAS3"; case 20: return "HORN_CLAS4";
                case 21: return "HORN_CLAS5"; case 22: return "HORN_CLAS6"; case 23: return "HORN_CLAS7";
                case 24: return "HORN_LUXE1"; case 25: return "HORN_LUXE2"; case 26: return "HORN_LUXE3";
                case 30: return "HORN_LOWRDER1"; case 31: return "HORN_LOWRDER2";
                case 34: return "HORN_HWEEN1"; case 35: return "HORN_HWEEN2";
                case 38: return "HORN_XM15_1"; case 39: return "HORN_XM15_2"; case 40: return "HORN_XM15_3";
                case 44: return "CMOD_HRN_TRK"; case 45: return "CMOD_HRN_COP"; case 46: return "CMOD_HRN_CLO";
                case 47: return "CMOD_HRN_MUS1"; case 48: return "CMOD_HRN_MUS2"; case 49: return "CMOD_HRN_MUS3";
                case 50: return "CMOD_HRN_MUS4"; case 51: return "CMOD_HRN_MUS5"; case 52: return "CMOD_HRN_SAD";
                case 53: return "CMOD_AIRHORN_01"; case 54: return "CMOD_AIRHORN_02"; case 55: return "CMOD_AIRHORN_03";
                default: return null;
            }
        }
        // Returns null for indices that don't resolve to a real horn — these are the game's internal *_PREVIEW
        // audio slots (duplicates of the real horn) which vanilla LSC hides; the menu skips them.
        private string HornDisplayName(int idx)
        {
            if (idx < 0) return "Stock Horn";
            int hash = Function.Call<int>(Hash.GET_VEHICLE_MOD_IDENTIFIER_HASH, currentVehicle, (int)VehicleModType.Horns, idx);
            if (!_hornHashToCanon.TryGetValue((uint)hash, out int canon)) return null;
            string label = HornGxtLabel(canon);
            string nm = string.IsNullOrEmpty(label) ? null : Game.GetLocalizedString(label);
            return string.IsNullOrEmpty(nm) || nm == "NULL" ? null : nm;
        }

        private NativeMenu CreateHornMenu()
        {
            var menu = CreateMenu("Horn");

            int currentHorn = currentVehicle.Mods[VehicleModType.Horns].Index;
            int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, (int)VehicleModType.Horns);
            string vehicleName = VehicleKey(currentVehicle);

            // Set the highlighted horn as the installed one so pressing L3 (handled in OnTick) auditions it;
            // revert to the real/purchased horn when leaving the menu. itemModIndex maps menu position -> horn
            // mod index (not 1:1 because preview-duplicate slots are skipped).
            int previewBaseHorn = currentHorn;
            _hornItemModIndex.Clear();
            var itemModIndex = _hornItemModIndex;
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists() && e.Index >= 0 && e.Index < itemModIndex.Count)
                    currentVehicle.Mods[VehicleModType.Horns].Index = itemModIndex[e.Index];
            };
            menu.Closed += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                    currentVehicle.Mods[VehicleModType.Horns].Index = previewBaseHorn;
            };

            for (int i = -1; i < count; i++)
            {
                string name = HornDisplayName(i);
                if (name == null) continue;   // skip internal *_PREVIEW duplicate horn slots
                itemModIndex.Add(i);
                bool isInstalled = currentHorn == i;
                bool isOwned = i >= 0 && VehicleSaveData.IsHornOwned(vehicleName, i);
                var item = new NativeItem(name);
                item.Description = "Hold Preview to hear this horn.";

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
                    bool owned = hornIndex >= 0 && VehicleSaveData.IsHornOwned(VehicleKey(currentVehicle), hornIndex);
                    if (!TryPurchase(hornPrice, owned)) return;

                    ApplyHorn(hornIndex);
                    previewBaseHorn = hornIndex;  // purchased horn becomes the revert target on menu close
                    if (hornIndex >= 0 && !owned)
                    {
                        VehicleSaveData.SetHornOwned(VehicleKey(currentVehicle), hornIndex);
                        VehicleSaveData.Save();
                    }
                };
                menu.Add(item);
            }

            return menu;
        }

        private NativeMenu CreateNosColorMenu()
        {
            var menu = CreateMenu("Nitrous (NOS)");
            string vn = VehicleKey(currentVehicle);

            // Per-vehicle nitrous (mirrors the equip model): each tier is bought + equipped per car; an Off item
            // un-equips without losing what you bought; re-selecting the equipped tier re-binds the spray key.
            int tierCount = ManualTransmission.ELSCTransmission.FxCatalog.Length;
            var refreshers = new List<Action>();
            int equippedTier = VehicleSaveData.GetNosEquippedTier(vn);
            nosPreviewFx = Math.Max(0, Math.Min(tierCount - 1, equippedTier >= 0 ? equippedTier : 0));

            // Off / No Nitrous — equipped (garage) when nothing is active; select to un-equip.
            var offItem = new NativeItem("Off (No Nitrous)");
            offItem.Description = "Disable nitrous on this car (keeps any tiers you've bought).";
            Action refreshOff = () =>
            {
                if (VehicleSaveData.GetNosEquippedTier(VehicleKey(currentVehicle)) < 0)
                { offItem.AltTitle = ""; itemOwnershipStatus[offItem] = STATUS_INSTALLED; }
                else { offItem.AltTitle = ""; itemOwnershipStatus.Remove(offItem); }
            };
            refreshers.Add(refreshOff);
            offItem.Activated += (s, e) =>
            {
                VehicleSaveData.SetNosEquippedTier(VehicleKey(currentVehicle), -1);
                VehicleSaveData.Save();
                if (elscTransmission != null) elscTransmission.NosInstalled = false;
                foreach (var r in refreshers) r();
                ShowNotification("~y~Nitrous removed");
            };
            menu.Add(offItem);

            for (int ti = 0; ti < tierCount; ti++)
            {
                int idx = ti;
                int price = idx < ModPricing.NosTierPrices.Length ? ModPricing.NosTierPrices[idx]
                                                                  : ModPricing.NosTierPrices[ModPricing.NosTierPrices.Length - 1];
                var tierItem = new NativeItem(ManualTransmission.ELSCTransmission.FxCatalog[idx].Name);
                Action refresh = () =>
                {
                    string v = VehicleKey(currentVehicle);
                    bool owned = (VehicleSaveData.GetNosOwnedTiers(v) & (1 << idx)) != 0;
                    bool active = VehicleSaveData.GetNosEquippedTier(v) == idx;
                    if (active && owned) { tierItem.AltTitle = ""; itemOwnershipStatus[tierItem] = STATUS_INSTALLED; tierItem.Description = $"{ManualTransmission.ELSCTransmission.FxCatalog[idx].Buff}. Select to edit the spray key."; }
                    else if (owned) { tierItem.AltTitle = ""; itemOwnershipStatus[tierItem] = STATUS_OWNED; tierItem.Description = $"{ManualTransmission.ELSCTransmission.FxCatalog[idx].Buff}. Select to equip."; }
                    else { tierItem.AltTitle = $"${price:N0}"; itemOwnershipStatus.Remove(tierItem); tierItem.Description = $"{ManualTransmission.ELSCTransmission.FxCatalog[idx].Buff}. Select to buy & equip."; }
                };
                refreshers.Add(refresh);
                refresh();
                tierItem.Activated += (s, e) =>
                {
                    string v = VehicleKey(currentVehicle);
                    int ownedMask = VehicleSaveData.GetNosOwnedTiers(v);
                    bool owned = (ownedMask & (1 << idx)) != 0;
                    bool active = VehicleSaveData.GetNosEquippedTier(v) == idx;
                    bool firstNos = ownedMask == 0;

                    if (active)
                    {
                        // Already equipped -> edit the spray key (re-bind).
                        nosBindingState = 1; nosBindingItem = tierItem;
                        return;
                    }
                    if (!owned)
                    {
                        if (!TryPurchase(price, false)) return;
                        VehicleSaveData.SetNosOwnedTiers(v, ownedMask | (1 << idx));
                        MechanicSpeak();
                    }
                    // Equip this tier.
                    VehicleSaveData.SetNosEquippedTier(v, idx);
                    VehicleSaveData.Save();
                    nosPreviewFx = idx;
                    if (elscTransmission != null) { elscTransmission.NosInstalled = true; elscTransmission.ActiveFxIndex = idx; }
                    // Nitrous needs a gauge (the bottle/turbo meter) — turn one on for THIS car if it has none (matches MT).
                    if (VehicleSaveData.GetSpeedoStyle(v) == 0)
                    {
                        VehicleSaveData.SetSpeedoStyle(v, (int)SpeedoStyle.Simple);
                        Speedo.Active = SpeedoStyle.Simple;
                        ShowNotification("~g~Speedometer enabled for Nitrous");
                    }
                    foreach (var r in refreshers) r();
                    if (firstNos) { nosBindingState = 1; nosBindingItem = tierItem; }   // prompt spray-button bind once
                };
                menu.Add(tierItem);
            }

            // Preview-on-hover: holding the NOS button shows the highlighted tier (even before buying).
            menu.SelectedIndexChanged += (s, e) => { int t = e.Index - 1; if (t >= 0 && t < tierCount) nosPreviewFx = t; };

            // Colour picker deferred to a future update (no tint-respecting flame asset yet). The saved colour
            // value stays in ModSettings.NosFlameColorArgb for that update + the planned speedometer integration.
            nosPreviewColor = Color.FromArgb(ModSettings.NosFlameColorArgb);
            return menu;
        }

        private NativeMenu CreateSpeedometerMenu()
        {
            var menu = CreateMenu("Speedometer");
            var styles = new[] { SpeedoStyle.Off, SpeedoStyle.Simple, SpeedoStyle.Nfsu };
            var refreshers = new List<Action>();

            // The editor (colors + units + size/position) opens by SELECTING the equipped gauge — no separate
            // "Customize" category. Built once here, shown from the equipped style's Activated handler.
            var custMenu = CreateSpeedoCustomizationMenu();
            custMenu.Closed += (s, e) => { if (!isNavigatingMenu) { menu.Visible = true; Speedo.Preview = Speedo.Active; } };

            foreach (var stEach in styles)
            {
                SpeedoStyle st = stEach;
                int price = st == SpeedoStyle.Nfsu ? ModPricing.SpeedoNfsuPrice : 0;
                var item = new NativeItem(Speedo.StyleName(st));

                Action refresh = () =>
                {
                    bool active = Speedo.Active == st;
                    if (active)
                    {
                        item.AltTitle = ""; itemOwnershipStatus[item] = STATUS_INSTALLED;
                        item.Description = st == SpeedoStyle.Off ? "No gauge shown. Select another style to show one."
                                                                : "Equipped. Select to edit colors & units.";
                    }
                    else if (Speedo.IsOwned(st))
                    {
                        item.AltTitle = ""; itemOwnershipStatus[item] = STATUS_OWNED;
                        item.Description = st == SpeedoStyle.Off ? "Hide the gauge. Select to equip." : "Owned. Select to equip.";
                    }
                    else
                    {
                        item.AltTitle = price == 0 ? "Free" : $"${price:N0}"; itemOwnershipStatus.Remove(item);
                        item.Description = "Arcade-racer street gauge. Hover to preview; select to buy & equip.";
                    }
                };
                refreshers.Add(refresh);
                refresh();

                item.Activated += (s, e) =>
                {
                    // Equipped gauge -> open the editor (Off has nothing to edit).
                    if (Speedo.Active == st)
                    {
                        if (st == SpeedoStyle.Off) { ShowNotification("~y~No gauge to customize — equip a style first."); return; }
                        isNavigatingMenu = true; Speedo.Preview = Speedo.Active; menu.Visible = false; custMenu.Visible = true; isNavigatingMenu = false;
                        return;
                    }
                    // Block turning the gauge Off while this car has Manual Transmission OR Nitrous equipped — both
                    // read off the gauge (gear/RPM for MT, the bottle/turbo meter for NOS).
                    if (st == SpeedoStyle.Off && currentVehicle != null && currentVehicle.Exists())
                    {
                        bool mtEq = VehicleSaveData.IsManualTransmissionEquipped(VehicleKey(currentVehicle));
                        bool nosEq = VehicleSaveData.GetNosEquippedTier(VehicleKey(currentVehicle)) >= 0;
                        if (mtEq || nosEq)
                        {
                            string need = mtEq && nosEq ? "Manual Transmission & Nitrous" : mtEq ? "Manual Transmission" : "Nitrous";
                            ShowNotification($"~y~{need} needs a speedometer — pick a gauge style.");
                            return;
                        }
                    }
                    if (!Speedo.IsOwned(st))
                    {
                        if (!TryPurchase(price, false)) return;
                        Speedo.Owned.Add(st);
                    }
                    Speedo.Preview = st;
                    ShowNotification(Speedo.Equip(st));
                    // Persist the choice PER-CAR so it shows only on this vehicle (not globally on every car).
                    if (currentVehicle != null && currentVehicle.Exists())
                        VehicleSaveData.SetSpeedoStyle(VehicleKey(currentVehicle), (int)st);
                    foreach (var r in refreshers) r();
                    MechanicSpeak();
                };
                menu.Add(item);
            }

            // Preview-on-hover: show whichever style is highlighted (Customization row falls back to the active one).
            menu.SelectedIndexChanged += (s, e) =>
            {
                Speedo.Preview = (e.Index >= 0 && e.Index < styles.Length) ? (SpeedoStyle?)styles[e.Index] : Speedo.Active;
            };

            return menu;
        }

        private NativeMenu CreateSpeedoCustomizationMenu()
        {
            var menu = CreateMenu("Speedo Customization");

            // Units as a SELECT toggle (a 2-item left/right list wasn't registering input in this menu, while
            // the multi-item color/size lists do). Select flips MPH <-> KM/H reliably.
            var units = new NativeItem("Units") { AltTitle = Speedo.Mph ? "MPH" : "KM/H" };
            units.Description = "Select to switch between MPH and KM/H.";
            units.Activated += (s, e) => { Speedo.Mph = !Speedo.Mph; Speedo.SaveConfig(); units.AltTitle = Speedo.Mph ? "MPH" : "KM/H"; };
            menu.Add(units);

            var sizes = new List<string>();
            for (int p = 70; p <= 150; p += 10) sizes.Add(p + "%");
            int sizeIdx = Math.Max(0, Math.Min(sizes.Count - 1, ((int)Math.Round(Speedo.Scale * 100) - 70) / 10));
            var size = new NativeListItem<string>("Size", sizes.ToArray()) { SelectedIndex = sizeIdx };
            size.Description = "Overall gauge size.";
            size.ItemChanged += (s, e) => { Speedo.Scale = (70 + e.Index * 10) / 100f; Speedo.SaveConfig(); };
            NoWrap(size); menu.Add(size);

            // ---- Per-element colors (customize EVERY color on the gauge) ----
            var palNames = new[] { "Red", "Orange", "Amber", "Yellow", "Lime", "Green", "Teal", "Cyan", "Blue", "Purple", "Magenta", "Pink", "White", "Gray", "Black" };
            var palVals = new[]
            {
                Color.FromArgb(255,235,60,60),  Color.FromArgb(255,255,140,0),  Color.FromArgb(255,255,191,0),
                Color.FromArgb(255,255,235,59), Color.FromArgb(255,170,255,60), Color.FromArgb(255,90,230,120),
                Color.FromArgb(255,0,200,180),  Color.FromArgb(255,80,210,255), Color.FromArgb(255,40,120,255),
                Color.FromArgb(255,160,90,255), Color.FromArgb(255,230,80,230), Color.FromArgb(255,255,120,180),
                Color.White,                    Color.FromArgb(255,200,200,200), Color.FromArgb(255,20,20,20)
            };
            int Nearest(Color c)
            {
                int best = 0; double bd = double.MaxValue;
                for (int i = 0; i < palVals.Length; i++)
                {
                    var p = palVals[i];
                    double d = (p.R - c.R) * (p.R - c.R) + (p.G - c.G) * (p.G - c.G) + (p.B - c.B) * (p.B - c.B);
                    if (d < bd) { bd = d; best = i; }
                }
                return best;
            }
            // Colors edit the ACTIVE style's palette, so Simple and FASTandSPEEDY keep separate colors.
            Speedo.Palette Cur() => Speedo.ColorsFor(Speedo.Active);
            var colorRefreshers = new List<Action>();
            void AddColor(string label, string desc, Func<Color> get, Action<Color> set)
            {
                var item = new NativeListItem<string>(label, palNames) { SelectedIndex = Nearest(get()) };
                item.Description = desc;
                item.ItemChanged += (s, e) => { set(palVals[e.Index]); Speedo.SaveConfig(); };
                colorRefreshers.Add(() => item.SelectedIndex = Nearest(get()));
                NoWrap(item); menu.Add(item);
            }
            AddColor("Accent Color", "RPM bar fill / needle.",     () => Cur().Accent,  c => Cur().Accent = c);
            AddColor("Speed Color",  "The speed number.",          () => Cur().Speed,   c => Cur().Speed = c);
            AddColor("Units Color",  "The MPH / KM·H label.",      () => Cur().Unit,    c => Cur().Unit = c);
            AddColor("Gear Color",   "The gear number.",           () => Cur().Gear,    c => Cur().Gear = c);
            AddColor("NOS Color",    "The nitrous bar.",           () => Cur().Nos,     c => Cur().Nos = c);
            AddColor("Redline Color","Redline zone / warning.",    () => Cur().Redline, c => Cur().Redline = c);
            // FASTandSPEEDY dial fills (Color 1 = big circle, Color 2 = small circle) + brightness sliders.
            AddColor("Big Circle (Color 1)",   "FASTandSPEEDY main dial fill.",  () => Cur().Circle1, c => Cur().Circle1 = c);
            AddColor("Small Circle (Color 2)", "FASTandSPEEDY turbo dial fill.", () => Cur().Circle2, c => Cur().Circle2 = c);

            // Brightness 0..100% in steps of 5 — how brightly the circle colour shows over the dark dial.
            var brite = new List<string>(); for (int p = 0; p <= 100; p += 5) brite.Add(p + "%");
            int B2I(int a) => Math.Max(0, Math.Min(brite.Count - 1, (int)Math.Round(a / 255f * 100f / 5f)));
            var c1b = new NativeListItem<string>("Big Circle Brightness", brite.ToArray()) { SelectedIndex = B2I(Cur().Circle1Alpha) };
            c1b.Description = "How brightly the big dial colour shows.";
            c1b.ItemChanged += (s, e) => { Cur().Circle1Alpha = (int)Math.Round(e.Index * 5 / 100f * 255f); Speedo.SaveConfig(); };
            colorRefreshers.Add(() => c1b.SelectedIndex = B2I(Cur().Circle1Alpha));
            menu.Add(c1b);
            var c2b = new NativeListItem<string>("Small Circle Brightness", brite.ToArray()) { SelectedIndex = B2I(Cur().Circle2Alpha) };
            c2b.Description = "How brightly the small dial colour shows.";
            c2b.ItemChanged += (s, e) => { Cur().Circle2Alpha = (int)Math.Round(e.Index * 5 / 100f * 255f); Speedo.SaveConfig(); };
            colorRefreshers.Add(() => c2b.SelectedIndex = B2I(Cur().Circle2Alpha));
            menu.Add(c2b);

            // When the editor opens, sync every picker to the (now-active) style's palette + the unit toggle.
            menu.Shown += (s, e) =>
            {
                units.AltTitle = Speedo.Mph ? "MPH" : "KM/H";
                foreach (var r in colorRefreshers) r();
            };

            var up = new NativeItem("Move Up"); up.Activated += (s, e) => { Speedo.OffY -= 0.01f; Speedo.SaveConfig(); };
            var down = new NativeItem("Move Down"); down.Activated += (s, e) => { Speedo.OffY += 0.01f; Speedo.SaveConfig(); };
            var left = new NativeItem("Move Left"); left.Activated += (s, e) => { Speedo.OffX -= 0.01f; Speedo.SaveConfig(); };
            var right = new NativeItem("Move Right"); right.Activated += (s, e) => { Speedo.OffX += 0.01f; Speedo.SaveConfig(); };
            var reset = new NativeItem("Reset Position"); reset.Activated += (s, e) => { Speedo.OffX = 0f; Speedo.OffY = 0f; Speedo.SaveConfig(); };
            menu.Add(up); menu.Add(down); menu.Add(left); menu.Add(right); menu.Add(reset);

            return menu;
        }

        private NativeMenu CreateTireDesignMenu()
        {
            var menu = CreateMenu("Tire Design");
            const string catPath = "Wheels/Tires/Tire Design";
            string vehicleName = VehicleKey(currentVehicle);
            bool curCustom = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, currentVehicle, 23);

            // value: 0 = Stock (free), 1 = Custom (priced aftermarket tire)
            var options = new (string Name, int Value, int Price, string Desc)[]
            {
                ("Stock", 0, 0, "Standard factory tire."),
                ("Custom", 1, ModPricing.CustomTiresPrice, "Aftermarket tire profile (low-profile sidewall).")
            };

            foreach (var (name, value, price, desc) in options)
            {
                bool isInstalled = (curCustom ? 1 : 0) == value;
                bool isOwned = value == 0 || VehicleSaveData.IsCustomItemOwned(vehicleName, catPath, value);
                var item = new NativeItem(name) { Description = desc };

                if (isInstalled)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_INSTALLED;
                    if (value > 0 && !isOwned) VehicleSaveData.SetCustomItemOwned(vehicleName, catPath, value);
                }
                else if (isOwned)
                {
                    item.AltTitle = "";
                    itemOwnershipStatus[item] = STATUS_OWNED;
                }
                else
                {
                    item.AltTitle = price == 0 ? "Free" : $"${price:N0}";
                }

                int val = value;
                int itemPrice = price;
                item.Activated += (s, e) =>
                {
                    bool owned = val == 0 || VehicleSaveData.IsCustomItemOwned(VehicleKey(currentVehicle), catPath, val);
                    if (!TryPurchase(itemPrice, owned)) return;
                    ApplyCustomTires(val == 1);
                    if (val > 0 && !owned)
                    {
                        VehicleSaveData.SetCustomItemOwned(VehicleKey(currentVehicle), catPath, val);
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

            string vehicleName = VehicleKey(currentVehicle);
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
                int itemPrice = price;
                item.Activated += (s, e) =>
                {
                    bool owned = idx == 0 || (int)currentVehicle.Mods.WindowTint == idx ||
                                 VehicleSaveData.IsTintOwned(VehicleKey(currentVehicle), idx);
                    if (!TryPurchase(itemPrice, owned)) return;
                    ApplyWindowTint(idx);
                    if (idx > 0)
                    {
                        VehicleSaveData.SetTintOwned(VehicleKey(currentVehicle), idx);
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

            // Live preview: show the highlighted plate style on the car; revert if you back out without buying.
            // Menu layout: item 0 = Custom Text, 1 = Random, 2+ = the plate styles (style index = itemIndex - 2).
            menu.Shown += (s, e) =>
            {
                if (currentVehicle != null && currentVehicle.Exists())
                { origPlateStyle = (int)currentVehicle.Mods.LicensePlateStyle; platePurchased = false; }
            };
            menu.SelectedIndexChanged += (s, e) =>
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                int styleIdx = e.Index - 2;
                if (styleIdx >= 0) currentVehicle.Mods.LicensePlateStyle = (LicensePlateStyle)styleIdx;
            };
            menu.Closed += (s, e) =>
            {
                if (!platePurchased && origPlateStyle >= 0 && currentVehicle != null && currentVehicle.Exists())
                    currentVehicle.Mods.LicensePlateStyle = (LicensePlateStyle)origPlateStyle;
            };

            // --- Custom plate TEXT (ELSC extra: name your car; doubles as its persistent identity) ---
            string curText = GetPlateText(currentVehicle);
            var customTextItem = new NativeItem("Custom Plate Text", "Type your own plate (up to 8 characters)")
            {
                AltTitle = string.IsNullOrEmpty(curText) ? "" : curText
            };
            customTextItem.Activated += (s, e) => { if (EditBlockApply()) return; StartEditPlate(); };
            menu.Add(customTextItem);

            var randomTextItem = new NativeItem("Random Plate", "Generate a unique plate automatically");
            randomTextItem.Activated += (s, e) =>
            {
                if (EditBlockApply()) return;
                string p = windowTint.GenerateUniquePlate();
                if (!string.IsNullOrEmpty(p)) ApplyCustomPlate(p, GetPlateText(currentVehicle));
            };
            menu.Add(randomTextItem);

            string[] plates = { "Blue on White 1", "Yellow on Black", "Yellow on Blue", "Blue on White 2", "Blue on White 3", "Yankton" };
            int currentPlate = (int)currentVehicle.Mods.LicensePlateStyle;

            string vehicleName = VehicleKey(currentVehicle);
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
                    bool owned = VehicleSaveData.IsPlateOwned(VehicleKey(currentVehicle), plateIndex);
                    if (!TryPurchase(platePrice, owned)) return;

                    platePurchased = true; // keep the previewed style, don't revert on close
                    ApplyPlateStyle(plateIndex);
                    if (!owned)
                    {
                        VehicleSaveData.SetPlateOwned(VehicleKey(currentVehicle), plateIndex);
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

            string vehicleName = VehicleKey(currentVehicle);

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
            string vehicleName = VehicleKey(currentVehicle);

            for (int i = 0; i < hornMenu.Items.Count; i++)
            {
                var item = hornMenu.Items[i] as NativeItem;
                if (item == null) continue;

                int hornIndex = i < _hornItemModIndex.Count ? _hornItemModIndex[i] : -1; // menu pos -> mod index
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
            string vehicleName = VehicleKey(currentVehicle);

            for (int i = 0; i < plateMenu.Items.Count; i++)
            {
                var item = plateMenu.Items[i] as NativeItem;
                if (item == null) continue;

                // Menu items 0 and 1 are the "Custom Plate Text" / "Random Plate" headers; the plate STYLES
                // start at item 2. The style index = item index - 2 (matches CreatePlateMenu's layout note).
                int styleIdx = i - 2;
                if (styleIdx < 0) continue;   // skip the two header items

                bool isNowInstalled = (styleIdx == newInstalledIndex);
                bool isOwned = VehicleSaveData.IsPlateOwned(vehicleName, styleIdx);

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
            string vehicleName = VehicleKey(currentVehicle);
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
            string vehicleName = VehicleKey(currentVehicle);
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
            bool custom = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, currentVehicle, 23); // keep custom tires
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            if (axle != 2) Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, wheelIndex, custom); // front
            if (axle != 1) Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, wheelIndex, custom); // rear
            ShowNotification(axle == 1 ? "~g~Front wheels installed!" : axle == 2 ? "~g~Rear wheels installed!" : "~g~Wheels installed!");
            MechanicSpeak();
            Log($"Applied wheel type {wheelType} index {wheelIndex} axle {axle}");
        }

        private void ApplyWheel(int wheelType, int wheelIndex)
        {
            if (currentVehicle == null) return;

            bool custom = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, currentVehicle, 23); // keep custom tires
            // Ensure mod kit is installed (required for SET_VEHICLE_MOD to work)
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, wheelIndex, custom); // Front wheels
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, wheelIndex, custom); // Back wheels

            ShowNotification("~g~Wheels installed!");
            MechanicSpeak();
            Log($"Applied wheel type {wheelType} index {wheelIndex}");
        }

        /// <summary>Toggle the aftermarket tire profile (SET_VEHICLE_MOD variation flag) on both axles.</summary>
        private void ApplyCustomTires(bool custom)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            Function.Call(Hash.SET_VEHICLE_MOD_KIT, currentVehicle, 0);
            int front = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 23);
            int back = Function.Call<int>(Hash.GET_VEHICLE_MOD, currentVehicle, 24);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, front, custom);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, back, custom);
            ShowNotification(custom ? "~g~Custom tires installed!" : "~g~Stock tires installed!");
            MechanicSpeak();
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

            currentVehicle.Repair();                                                   // SET_VEHICLE_FIXED (health + scuffs)
            try { Function.Call(Hash.SET_VEHICLE_DEFORMATION_FIXED, currentVehicle); } catch { }  // pop dents/nicks back out
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

            // Customize Radio: loop the player's own tracks while in an LSC. Crossfades between states instead of
            // cutting out — normal while editing (menu open), quieter when the menu's closed but you're still in the
            // shop, and a gentle fade to silence when you leave LSC. Reopening ELSC in the shop normalizes it again.
            try
            {
                if (customizeRadio != null)
                {
                    bool available = ModSettings.CustomizeRadio && customizeRadio.HasTracks;
                    // Target level for the current state. Only at an ACTUAL Los Santos Customs (isInLSC) — not when
                    // the menu is opened anywhere in free roam via OpenAnywhere.
                    float target;
                    if (!available)                       target = 0f;
                    else if (isInLSC && isMenuActive)      target = 1f;                  // editing → normal
                    else if (isInLSC)                      target = RADIO_QUIET_LEVEL;   // in shop, menu closed → quiet
                    else                                   target = 0f;                  // left LSC → fade out

                    // Rise quickly (snappy "normalize"), fall gently (smooth fade-out / quieten).
                    float rate = target > _radioFade ? 0.18f : 0.045f;
                    _radioFade += (target - _radioFade) * rate;
                    if (_radioFade < 0.0025f) _radioFade = 0f;   // floor so the fade-out fully completes

                    // Keep playing while we still have any level to render; stop once fully faded (or unavailable).
                    if (available && _radioFade > 0f)
                    {
                        // Follow the game's Music volume (GET_PROFILE_SETTING 301, 0-10), scaled by the INI ceiling
                        // — so lowering Music volume in GTA settings quiets/mutes the customize radio.
                        int musicVol = 10;
                        try { musicVol = Function.Call<int>(Hash.GET_PROFILE_SETTING, 301); } catch { }
                        if (musicVol < 0) musicVol = 0; else if (musicVol > 10) musicVol = 10;
                        customizeRadio.Volume = (musicVol / 10f) * ModSettings.CustomizeRadioVolume * _radioFade;
                        customizeRadio.Start();
                        customizeRadio.Tick();
                    }
                    else { customizeRadio.Stop(); _radioFade = 0f; }
                }
            }
            catch { }

            // While Manual Transmission is active, disable drive-by so aiming a gun in the car can't roll down /
            // break the windows (SET_PLAYER_CAN_DO_DRIVE_BY). Only when MT is on — restored the moment it's off.
            try
            {
                bool mtOn = elscTransmission != null && elscTransmission.IsEnabled;
                if (mtOn) { Function.Call(Hash.SET_PLAYER_CAN_DO_DRIVE_BY, Game.Player, false); _driveByDisabled = true; }
                else if (_driveByDisabled) { Function.Call(Hash.SET_PLAYER_CAN_DO_DRIVE_BY, Game.Player, true); _driveByDisabled = false; }
            }
            catch { }

            // Speedometer design-preview harness (white-screen isolate for screenshot iteration).
            try { Speedo.DrawPreviewIfRequested(); } catch { }

            // NOS flame-colour preview: while the picker is open, hold the bound NOS button to spray flames
            // (in the selected colour) out the parked car so the player can see the colour live.
            if (nosColorMenuOpen && elscTransmission != null)
            {
                try
                {
                    var pv = Game.Player.Character?.CurrentVehicle;
                    bool held = Game.IsKeyPressed(ModSettings.NosKey)
                             || Function.Call<bool>(Hash.IS_CONTROL_PRESSED, 0, ModSettings.NosButton)
                             || Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, ModSettings.NosButton)
                             // In the menu/frontend context the physical X button maps to FrontendX, not the
                             // vehicle handbrake (control 76) — so also read FrontendX so the hold-to-preview works.
                             || Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendX);
                    if (held && pv != null && pv.Exists())
                    {
                        elscTransmission.EmitExhaustFlames(pv, nosPreviewColor, nosPreviewFx);
                        Function.Call((Hash)0x4A04DE7CAB2739A1, pv.Handle, true);   // SET_VEHICLE_BOOST_ACTIVE: whoosh + blur
                    }
                    else
                    {
                        elscTransmission.StopExhaustFlames();   // release -> stop the flames
                        if (pv != null && pv.Exists())
                            Function.Call((Hash)0x4A04DE7CAB2739A1, pv.Handle, false);
                    }
                }
                catch { }
            }

            // Re-assert the radio station after an engine-audio swap hijacked it (async, so hold it briefly).
            if (radioRestoreUntil != 0)
            {
                if (Game.GameTime < radioRestoreUntil) RestoreRadioStation();
                else radioRestoreUntil = 0;
            }


            // Per-car plate identity MUST run BEFORE any apply-by-plate sweep below (window tint + snapshot), or a
            // duplicate-plate car gets another car's saved look applied before it's re-plated.
            // 1) The car the player just got into (auto-rename empty/colliding plates), once per car.
            BindPlateForCurrentVehicle();
            // 2) Spawned duplicates (e.g. several Menyoo cars all plated "MENYOO") -> give the extras unique plates.
            DedupeWorldPlates();

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
            if (isMenuActive && !isWalkAroundActive && !isFirstPersonActive && Game.GameTime < menuCamUntil)
            {
                Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_HEADING, MENU_CAM_HEADING);
                Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_PITCH, MENU_CAM_PITCH, 1.0f);
            }

            // Collision wireframe overlay (F8) — drawn every frame regardless of menu state.
            if (showCollisionDebug) DrawCollisionDebug();

            // Handle description editor first (on-screen keyboard)
            UpdateDescriptionEditor();
            UpdateAddCategory();   // edit mode: on-screen keyboard for naming a new category
            UpdateNamePart();      // edit mode: on-screen keyboard for naming a re-shelved part
            UpdatePricePart();     // edit mode: on-screen keyboard for the part's price
            UpdateRenameCategory();// edit mode: on-screen keyboard for renaming a category
            UpdateAddWheelCategory();  // edit mode: name a new universal wheel category
            UpdateNameWheel();         // edit mode: name a re-shelved rim
            UpdateEditItemName();      // edit mode: edit an item's name (pre-filled)
            UpdateEditItemPrice();     // edit mode: edit an item's price (pre-filled)
            UpdateNamePackage();       // packages: on-screen keyboard for naming a saved package

            // Handle custom plate-text editor (on-screen keyboard)
            UpdatePlateEditor();

            // Check for X to edit/cancel description (editor mode)
            CheckDescriptionEditInput();

            // Skip input processing while ANY on-screen keyboard is open (description, plate, package name, etc.) so
            // the keys being typed go to the text box and don't leak through to the menu / gameplay underneath
            // (e.g. a letter in the package name triggering the walk-around toggle). Still draw the menu + custom UI.
            if (IsTypingText)
            {
                Game.DisableAllControlsThisFrame();
                menuPool.Process();
                DrawCustomBanner(); // Keep custom UI visible
                return;
            }

            // Manual transmission / nitrous key binding mode - block input and draw overlay
            if (mtBindingState > 0 || nosBindingState > 0)
            {
                Game.DisableAllControlsThisFrame();
                menuPool.Process();
                DrawCustomBanner();
                DrawMTBindingOverlay();
                CheckControllerButtonBinding();
                return; // Skip normal input processing
            }

            // In-game control remapper (edit mode): waiting for the user to press a key/button to bind.
            if (isRebindingKey || isRebindingButton)
            {
                Game.DisableAllControlsThisFrame();
                menuPool.Process();
                DrawCustomBanner();
                DrawRebindPrompt();
                if (isRebindingButton) CheckRebindButtonCapture();   // key capture happens in OnKeyDown
                return;
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

            // Hold an inspected car's wheels at the chosen angle (in AND out of the menu, incl. after the player
            // leaves it parked in free roam). Released the moment it's actually driven.
            UpdateSteeringHold();

            // Auto-apply saved stances to specific cars within range (skips the player's current car).
            // Guarded like fitment: on error, disable for the session rather than crash the script.
            if (_stanceMgrInit && ModSettings.VStancerIntegration)
            {
                try { stanceManager.Update(); }
                catch (Exception ex) { Log($"[Stance] OnTick error: {ex.Message}"); _stanceMgrInit = false; Log("[Stance] Disabled due to errors"); }
            }

            // Idle showcase cinematic: after ~10s of no input it fades to black, hides the HUD + menu, and slowly
            // orbits the car (fading on each side switch). Any input snaps back + unhides. Owns its own camera.
            UpdateIdleCinematic();

            if (_idleHideUI)
            {
                // The cinematic owns the screen now: hide the game HUD/radar and block input. The ELSC menu + custom
                // UI below are skipped so nothing draws over the shot. (During the initial fade-out the menu still
                // draws so it fades to black WITH the menu, then we hide once black.)
                Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);
                Game.DisableAllControlsThisFrame();
            }
            else
            {
                // In walk-around, D-pad Left/Right steers the wheels for inspection — block the menu from also
                // consuming them (it would change a highlighted list item). Disabled BEFORE input is processed so
                // neither HandleMenuInput nor LemonUI sees them; UpdateWalkAround reads them as disabled controls.
                if (isWalkAroundActive && ModSettings.KeepSteeringAngle)
                {
                    Game.DisableControlThisFrame(GTA.Control.FrontendLeft);
                    Game.DisableControlThisFrame(GTA.Control.FrontendRight);
                }

                // Handle custom menu input with native GTA-style acceleration
                HandleMenuInput();

                // Capture + clear the selected item's native description BEFORE LemonUI draws it, so its un-offset
                // native description never flashes; DrawCustomBanner redraws it below the scroll arrows.
                PreClearVisibleDescription();

                menuPool.Process();

                // Packages: X on a highlighted package opens the delete-confirm prompt.
                CheckPackageDeleteInput();

                // Turn the wheels with D-pad Left/Right in the normal menu too (not just walk-around). Stands down on
                // list/slider items (there Left/Right adjusts the item) so it never fights menu value changes.
                if (isMenuActive && !isWalkAroundActive)
                {
                    var sm = GetVisibleMenu();
                    if (sm == null || !ItemUsesLeftRight(sm.SelectedItem))
                        HandleInspectSteering(currentVehicle);
                }

                // Draw custom banner overlay
                DrawCustomBanner();

                // Draw sprite browser if enabled (F6 to toggle)
                DrawSpriteBrowser();
            }

            // Speedometer preview — ONLY while a menu is open. DEMO telemetry (88 mph, gear 4, NOS) so units +
            // colors are visible while parked. CRITICAL: if the menu isn't open, clear Preview — otherwise it
            // leaks into driving and the static demo gauge draws ON TOP of the real one (ghosted/overlaid digits).
            if (!isMenuActive)
            {
                Speedo.Preview = null;
            }
            else if (!_idleHideUI && Speedo.Preview != null && currentVehicle != null && currentVehicle.Exists())
            {
                Speedo.Draw(new SpeedoFrame
                {
                    SpeedMph = 88f, Rpm01 = 0.72f, GearText = "4", Redline = false,
                    HasTurbo = true, Turbo01 = 0.72f, HasNos = true, Nos01 = 0.66f,
                    HasShiftPoints = true, PerfectMin01 = 0.90f, PerfectMax01 = 0.98f
                });
            }

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

            // Walk-around / first-person camera modes. Skipped while the idle cinematic owns the screen so the
            // custom camera doesn't fight the cinematic cam; on input the cinematic restores it.
            if (isWalkAroundActive && !_idleHideUI)
            {
                UpdateWalkAround();
            }
            else if (isFirstPersonActive && !_idleHideUI)
            {
                UpdateFirstPerson();
            }

            // Cooldown timer for camera mode
            if (cameraModeCooldown > 0)
                cameraModeCooldown--;

            // Camera cycle (Y button): Basic -> Walk-Around -> First-Person -> Basic. Single handler for ALL modes.
            if (ModSettings.CustomCamera && isMenuActive && _lscEjectPhase == 0 && cameraModeCooldown == 0)
            {
                // Detect the press while the control stays DISABLED, so the game never runs its
                // native exit-vehicle behaviour for this button (default Y / VehicleExit).
                if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, ModSettings.WalkAroundButton))
                {
                    CycleCamera();
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

            // Hide the minimap/GPS while the ELSC menu is open; restore it when the menu closes.
            if (isMenuActive)
            {
                if (!radarHiddenByMenu) { Function.Call(Hash.DISPLAY_RADAR, false); radarHiddenByMenu = true; }
            }
            else if (radarHiddenByMenu)
            {
                Function.Call(Hash.DISPLAY_RADAR, true); radarHiddenByMenu = false;
            }

            // Block input while menu active (but allow movement in walk-around)
            if (isMenuActive)
            {
                // Show cash HUD every frame
                Function.Call((Hash)0x96DEC8D5430208B7, true); // DISPLAY_CASH

                // ELSC edit-mode indicator (yellow banner up top) so it's unmistakable you're editing.
                if (editModeActive)
                {
                    var em = new GTA.UI.TextElement("~y~● EDIT MODE  ~w~~italic~F6 to exit",
                        new System.Drawing.PointF(GTA.UI.Screen.Width * 0.5f, GTA.UI.Screen.Height * 0.035f),
                        0.5f, System.Drawing.Color.FromArgb(255, 255, 215, 0),
                        GTA.UI.Font.ChaletLondon, GTA.UI.Alignment.Center);
                    em.Outline = true;
                    em.Draw();
                }

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
                    // Horn menu open: re-enable L3/horn so previewing works in walk-around too (UpdateWalkAround
                    // disables VehicleHorn each frame; this runs after it, so the enable wins).
                    if (hornMenu != null && hornMenu.Visible)
                        Game.EnableControlThisFrame(GTA.Control.VehicleHorn);
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
                    // NOTE: VehicleExit is intentionally LEFT DISABLED. Enabling it let the game's native
                    // "hold Y to exit vehicle" fire (spamming the walk-around button ejected the player).
                    // The walk-around button press is detected via IS_DISABLED_CONTROL_JUST_PRESSED below.
                    Game.EnableControlThisFrame(GTA.Control.Aim); // LB for edit mode
                    Game.EnableControlThisFrame(GTA.Control.Sprint); // Shift for edit mode (keyboard)
                    Game.EnableControlThisFrame(GTA.Control.VehicleDuck); // X for edit description
                    // Horn menu open: leave L3 (horn) enabled so the player can hold it to preview horns naturally.
                    if (hornMenu != null && hornMenu.Visible)
                        Game.EnableControlThisFrame(GTA.Control.VehicleHorn);
                }
            }

            // Fake-fade: advance the overlay alpha and draw it LAST so it covers the menu + HUD. Runs every frame
            // (even with the cinematic stopped) so an interrupted fade can still finish clearing.
            UpdateIdleFade();
            DrawIdleFadeOverlay();

            // LSC detection
            CheckLSCEntry();
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // While the player is typing into an on-screen keyboard, swallow every key here too — otherwise a letter
            // in their text (package name, plate, etc.) could fire an edit-mode hotkey or other ELSC action.
            if (IsTypingText) return;

            // Open ELSC ANYWHERE (INI OpenAnywhere, off by default): the menu key opens the menu without a shop.
            if (ModSettings.OpenAnywhere && e.KeyCode == ModSettings.MenuKey && !isMenuActive)
            {
                TryOpenMenu();
                return;
            }

            // Control remapper: capture the next key press as the new binding (edit-mode CONTROLS menu).
            if (isRebindingKey)
            {
                if (e.KeyCode == Keys.Escape) { CancelRebind("~r~Rebind cancelled"); return; }
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return; // ignore lone modifiers
                rebindKeySetter?.Invoke(e.KeyCode);
                ShowNotification($"~g~{rebindLabel} = {e.KeyCode}");
                FinishRebind();
                return;
            }

            // ELSC modder edit mode: live toggle (F6) while the menu is open. Master-gated by EditorMode so
            // players never trigger it. Phase 1 — toggles the state + the yellow EDIT MODE indicator.
            if (ModSettings.EditorMode && isMenuActive && e.KeyCode == ModSettings.EditModeKey)
            {
                editModeActive = !editModeActive;
                ShowNotification(editModeActive ? "~y~ELSC EDIT MODE ON" : "~w~Edit mode off");
                // Rebuild so the edit-mode items (Add a Category, etc.) appear/disappear.
                if (currentVehicle != null) RebuildMenusForVehicle();
                return;
            }

            // Edit mode: DELETE the highlighted custom category and restore its parts to their default menus.
            if (ModSettings.EditorMode && editModeActive && isMenuActive && e.KeyCode == ModSettings.DeleteCategoryKey
                && mainMenu != null && mainMenu.Visible && mainMenu.SelectedIndex >= 0
                && mainMenu.SelectedIndex < mainMenu.Items.Count)
            {
                var delSel = mainMenu.Items[mainMenu.SelectedIndex] as NativeItem;
                if (delSel != null && mainCatItems.TryGetValue(delSel, out var delInfo))
                {
                    if (delInfo.isCustom)
                    {
                        if (MenuConfig.DeleteVehicleCategory(delInfo.key))
                        {
                            ShowNotification($"~y~Deleted: {delInfo.key}~n~~w~Its parts are back in their default menus.");
                            CaptureMenuPosition(mainMenu.SelectedIndex - 1);   // deleted row is gone -> shift highlight up one
                            if (currentVehicle != null) RebuildMenusForVehicle();
                        }
                    }
                    else
                    {
                        // Built-in category. Delete priority: un-hide -> clear rename -> hide.
                        if (MenuConfig.IsCategoryHidden(delInfo.key))
                        {
                            MenuConfig.SetCategoryHidden(delInfo.key, false);
                            ShowNotification($"~g~Restored category: {delInfo.key}");
                        }
                        else if (MenuConfig.GetCategoryRename(delInfo.key) != null)
                        {
                            MenuConfig.SetCategoryRename(delInfo.key, null);
                            ShowNotification($"~y~Restored original name: {delInfo.key}");
                        }
                        else
                        {
                            MenuConfig.SetCategoryHidden(delInfo.key, true);
                            ShowNotification($"~y~Hid category: {delInfo.key}~n~~w~Delete it again in edit mode to restore.");
                        }
                        if (currentVehicle != null) RebuildMenusForVehicle();
                    }
                }
                return;
            }

            // Edit mode: RENAME the highlighted category (built-in or custom) for THIS vehicle. F2.
            if (ModSettings.EditorMode && editModeActive && isMenuActive && e.KeyCode == ModSettings.RenameCategoryKey
                && mainMenu != null && mainMenu.Visible && mainMenu.SelectedIndex >= 0
                && mainMenu.SelectedIndex < mainMenu.Items.Count)
            {
                var sel = mainMenu.Items[mainMenu.SelectedIndex] as NativeItem;
                if (sel != null && mainCatItems.TryGetValue(sel, out var info))
                    StartRenameCategory(info.key, info.isCustom);
                return;
            }

            // Manual transmission key binding capture
            if (nosBindingState > 0)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    nosBindingState = 0; nosBindingItem = null;
                    ShowNotification("~y~Nitrous installed. Spray key unchanged (" + ModSettings.NosKey + ").");
                    return;
                }
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return) return;
                ModSettings.NosKey = e.KeyCode;
                ModSettings.Save();
                FinishNosBinding();
                return;
            }

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
            if (e.KeyCode == ModSettings.DebugMenuKey && isMenuActive)
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

        /// <summary>
        /// True when the player is on a story mission / cutscene / has had control scripted away. While this is
        /// true ELSC stands down completely — no menu open, no LSC-shop takeover — so the vanilla game (and any
        /// vanilla LSC the mission uses) runs untouched. GET_MISSION_FLAG is the primary signal; the cutscene and
        /// player-control checks catch mission-adjacent states that don't raise the flag.
        /// </summary>
        private bool IsOnMission()
        {
            try
            {
                if (Function.Call<bool>(Hash.GET_MISSION_FLAG)) return true;
                if (Game.IsCutsceneActive) return true;
                if (!Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player)) return true;
            }
            catch { }
            return false;
        }

        private void TryOpenMenu()
        {
            try
            {
                // Fully disabled during missions — let the game behave vanilla.
                if (IsOnMission()) return;

                var player = Game.Player.Character;

                if (!player.IsInVehicle())
                {
                    ShowNotification("~r~You must be in a vehicle!");
                    return;
                }

                currentVehicle = player.CurrentVehicle;
                currentVehicle.Mods.InstallModKit();
                SyncSpeedoToCar(currentVehicle);   // per-car speedo: menu reflects THIS car's equipped style

                // Cache screen resolution for coordinate conversion
                UpdateCachedScreenResolution();

                // Rebuild menus for this vehicle
                RebuildMenusForVehicle();

                // Apply auto-offset for current resolution (unless in debug mode)
                ApplyAutoOffset();

                isMenuActive = true;
                ShowMenuWithRepairGate();   // damaged car -> repair prompt first; otherwise straight into the menu

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
            if (isFirstPersonActive)
            {
                ExitFirstPerson();
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

        // ---- Repair gate: like real LSC, a damaged car must be repaired before customizing. Cost scales with
        // how badly it's damaged (body + engine + tank health); checked on EVERY menu open. ----
        /// <summary>Hand the view from the eject cams back to the gameplay camera (which is already AT the menu
        /// framing, so the cut is seamless) and delete both temp cams. ease only used for the misdetect bail.</summary>
        private void ReleaseLscHoldCam(bool ease)
        {
            if (_lscHoldCam == null && _lscMenuCam == null) return;
            try { Function.Call(Hash.RENDER_SCRIPT_CAMS, false, ease, ease ? 400 : 0, true, false, 0); } catch { }
            if (ease) { _lscHoldCamReleaseAt = Game.GameTime + 450; }   // keep alive through the brief blend, then reap
            else
            {
                try { if (_lscHoldCam != null) _lscHoldCam.Delete(); } catch { }
                try { if (_lscMenuCam != null) _lscMenuCam.Delete(); } catch { }
                _lscHoldCam = null; _lscMenuCam = null; _lscHoldCamReleaseAt = 0;
            }
        }

        // True while the game's mod-shop script (carmod_shop) is running — i.e. the player is actually at a Los Santos
        // Customs shop (vanilla or a modded one that uses it). It is NOT running during Menyoo Spooner / trainers, so
        // this distinguishes "the real customs menu opened" from "another mod just disabled my control in a parked car".
        private int _carmodShopHash = 0;
        private bool CarmodShopRunning()
        {
            try
            {
                if (_carmodShopHash == 0) _carmodShopHash = Function.Call<int>(Hash.GET_HASH_KEY, "carmod_shop");
                return Function.Call<int>(Hash.GET_NUMBER_OF_THREADS_RUNNING_THE_SCRIPT_WITH_THIS_HASH, _carmodShopHash) > 0;
            }
            catch { return false; }
        }

        // Faithful port of vanilla LSC's damage assessment (carmod_shop func_3162): a component-based POINT
        // system, not a pure health check. Crucially it counts visual-only damage — scratches/scuff DECALS,
        // dinged doors, burst tyres, cracked glass, popped bumpers, dead headlights — none of which move the
        // body/engine/tank health floats. That's why the old health-only check never saw "scratches and nicks".
        private int ComputeRepairCost()
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return 0;
            var v = currentVehicle;
            bool B(Hash h, params InputArgument[] a) { try { var args = new InputArgument[a.Length + 1]; args[0] = v; Array.Copy(a, 0, args, 1, a.Length); return Function.Call<bool>(h, args); } catch { return false; } }
            int pts = 0;

            // Mechanical health tiers (engine / petrol tank / body[entity] health) — LSC's exact thresholds.
            float eng = Math.Max(0f, v.EngineHealth) / 1000f;
            pts += eng > 0.99f ? 0 : eng > 0.8f ? 20 : eng > 0.6f ? 40 : eng > 0.4f ? 80 : 100;
            float tank = Math.Max(0f, v.PetrolTankHealth) / 1000f;
            pts += tank > 0.99f ? 0 : tank > 0.8f ? 20 : tank > 0.6f ? 40 : tank > 0.4f ? 60 : 75;
            float body = Math.Max(0, v.Health) / 1000f;
            pts += body > 0.99f ? 0 : body > 0.8f ? 40 : body > 0.6f ? 80 : body > 0.4f ? 150 : 200;

            // Visual damage (the part the old code missed entirely).
            if (B(Hash.GET_DOES_VEHICLE_HAVE_DAMAGE_DECALS)) pts += 50;              // scratches / scuffs
            if (B(Hash.IS_VEHICLE_BUMPER_BROKEN_OFF, true))  pts += 50;              // front bumper off
            if (B(Hash.IS_VEHICLE_BUMPER_BROKEN_OFF, false)) pts += 50;              // rear bumper off
            if (!B(Hash.ARE_ALL_VEHICLE_WINDOWS_INTACT))
            {
                pts += 20;
                if (!B(Hash.IS_VEHICLE_WINDOW_INTACT, 6)) pts += 40;                 // windscreen
                if (!B(Hash.IS_VEHICLE_WINDOW_INTACT, 7)) pts += 40;                 // rear windscreen
            }
            for (int d = 0; d < 6; d++) if (B(Hash.IS_VEHICLE_DOOR_DAMAGED, d)) pts += 25;
            if (B(Hash.GET_IS_LEFT_VEHICLE_HEADLIGHT_DAMAGED))  pts += 15;
            if (B(Hash.GET_IS_RIGHT_VEHICLE_HEADLIGHT_DAMAGED)) pts += 15;
            for (int t = 0; t < 8; t++) if (B(Hash.IS_VEHICLE_TYRE_BURST, t, false)) pts += 25;

            if (pts <= 0) return 0;   // genuinely pristine -> no repair prompt
            int cost = pts * 2 + 50;  // points -> dollars with LSC's $50 base fee
            if (cost > 5000) cost = 5000;
            return (int)(Math.Round(cost / 25.0) * 25);
        }

        // Open the customization menu — but if the car is damaged, show the repair prompt first. Paying the
        // (damage-scaled) repair fee fixes the car and proceeds to the menu; backing out closes (no free repair).
        private void ShowMenuWithRepairGate()
        {
            int cost = ComputeRepairCost();
            if (cost <= 0) { mainMenu.Visible = true; return; }   // pristine -> straight in (any damage shows the prompt)

            if (repairMenu != null) { menuPool.Remove(repairMenu); repairMenu = null; }
            repairMenu = CreateMenu("Repair");
            var repairItem = new NativeItem("Repair Vehicle");
            repairItem.Description = "Your vehicle is damaged. Repairs are required before you can customize it.";
            repairItem.AltTitle = $"${cost:N0}";
            int fee = cost;
            repairItem.Activated += (s, e) =>
            {
                if (!TryPurchase(fee, false)) return;   // not enough cash -> message, stay on the repair prompt
                RepairVehicle();
                isNavigatingMenu = true;
                repairMenu.Visible = false;
                mainMenu.Visible = true;
                isNavigatingMenu = false;
            };
            repairMenu.Add(repairItem);
            repairMenu.Closed += (s, e) => { if (!isNavigatingMenu) CloseMenu(); };   // back out = leave (no customizing)
            repairMenu.Visible = true;
        }

        #endregion

        #region LSC Detection

        private void CheckLSCEntry()
        {
            // Reap the eject cams once their release blend has finished (deferred so the blend can complete).
            if ((_lscHoldCam != null || _lscMenuCam != null) && _lscHoldCamReleaseAt != 0 && Game.GameTime > _lscHoldCamReleaseAt)
            {
                try { if (_lscHoldCam != null) _lscHoldCam.Delete(); } catch { }
                try { if (_lscMenuCam != null) _lscMenuCam.Delete(); } catch { }
                _lscHoldCam = null; _lscMenuCam = null; _lscHoldCamReleaseAt = 0;
            }

            if (_recordVanillaLSC) return;   // TEMP: let vanilla carmod_shop run uninterrupted for timing capture

            // NOTE: the eject takeover below runs BEFORE the IsOnMission() bail on purpose — the vanilla menu IS a
            // control-off state, and IsOnMission() treats control-off as a mission, so bailing first would make the
            // detection (which keys on control-off) impossible. We guard it with the REAL mission signals instead.

            // ── UNIVERSAL EJECT TAKEOVER ───────────────────────────────────────────────────────────────────
            // The vanilla customs menu = player IN a vehicle, control OFF, car STOPPED. (The car moves through the
            // whole drive-in animation and only stops when the menu actually opens, so "control off + stopped"
            // cleanly marks menu-open and not the cinematic. Missions/cutscenes are already bailed at the top.)
            // There's no native to close that menu, but popping the player out of the driver seat (then instantly
            // re-seating) makes carmod_shop abort it and hand control back — WITHOUT swapping the mechanic. Works at
            // ANY shop, including MODDED ones. (Additive: known vanilla shops are usually preempted below first.)
            // Guard against REAL missions/cutscenes (which are ALSO control-off) — but NOT on control-off itself.
            bool realMission = false;
            try { realMission = Function.Call<bool>(Hash.GET_MISSION_FLAG) || Game.IsCutsceneActive; } catch { }

            // Run while idle-and-not-in-menu (to START a takeover) OR whenever a takeover is already in progress
            // (phases 4-5 run AFTER ELSC opens, i.e. with isMenuActive true, to finish the camera interp).
            if (!realMission && (_lscEjectPhase > 0 || !isMenuActive))
            {
                // Set OUR custom camera at the menu framing FIRST, eject behind it, then hand to ELSC's own camera:
                // (0) detect, aim the hidden gameplay cam at the menu angle; (1) snapshot that into a script cam and
                // cut to it; (2) eject; (3) re-seat; (4) control back -> open ELSC and hand the script cam back to the
                // gameplay cam (already AT the same framing -> seamless). No drive-in freeze, no interp, no snap.
                if (_lscEjectPhase == 0)
                {
                    var pv = Game.Player.Character?.CurrentVehicle;
                    bool controlOff = !Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player);
                    // MOD-CONFLICT GUARD: "in a stopped car with control off" also matches Menyoo's Spooner mode (and
                    // other trainers), so ELSC used to keep yanking the camera there. The real customs menu is driven
                    // by the game's carmod_shop script, which only runs while you're at a mod shop (vanilla OR a modded
                    // one that uses it) and is NOT running during Spooner anywhere else. Gate the takeover on it.
                    // (&& short-circuits, so the native only runs once we're actually stopped with control off.)
                    bool looksLikeMenu = pv != null && pv.Exists() && controlOff && pv.Speed < 0.5f && CarmodShopRunning();
                    if (!looksLikeMenu) _lscMenuMaybeSince = 0;
                    else
                    {
                        if (_lscMenuMaybeSince == 0) _lscMenuMaybeSince = Game.GameTime;
                        if (Game.GameTime - _lscMenuMaybeSince > 150)
                        {
                            // FIRST open this visit: remember the car's nice spot (the drive-in just parked it here).
                            // RE-OPEN: carmod_shop re-teleports the car to an awkward canonical spot when its menu
                            // re-triggers — snap it back to the captured spot/heading so re-opens look identical.
                            if (!_lscCarCaptured)
                            {
                                _lscCarPos = pv.Position; _lscCarHeading = pv.Heading; _lscCarCaptured = true;
                            }
                            else
                            {
                                try
                                {
                                    Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, pv, _lscCarPos.X, _lscCarPos.Y, _lscCarPos.Z, false, false, false);
                                    Function.Call(Hash.SET_ENTITY_HEADING, pv, _lscCarHeading);
                                    Function.Call(Hash.SET_VEHICLE_ON_GROUND_PROPERLY, pv);
                                }
                                catch { }
                            }
                            // Aim the (hidden) gameplay cam at ELSC's menu framing so we can snapshot it next.
                            Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_HEADING, MENU_CAM_HEADING);
                            Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_PITCH, MENU_CAM_PITCH, 1.0f);
                            _lscEjectVeh = pv; _lscEjectPhase = 1; _lscEjectSince = Game.GameTime; _lscMenuMaybeSince = 0;
                            isInLSC = true; customizeSpot = pv.Position; lscWorldPos = pv.Position;
                            Log("Customs menu detected -> aiming menu cam, begin eject takeover (mechanic untouched)");
                        }
                    }
                }
                else if (_lscEjectPhase == 1)
                {
                    // Hold the framing, and once it's applied snapshot it into our custom script cam and cut to it.
                    Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_HEADING, MENU_CAM_HEADING);
                    Function.Call(Hash.SET_GAMEPLAY_CAM_RELATIVE_PITCH, MENU_CAM_PITCH, 1.0f);
                    if (Game.GameTime - _lscEjectSince > 120)
                    {
                        try
                        {
                            _lscMenuCam = World.CreateCamera(GameplayCamera.Position, GameplayCamera.Rotation, GameplayCamera.FieldOfView);
                            _lscMenuCam.IsActive = true;
                            Function.Call(Hash.RENDER_SCRIPT_CAMS, true, false, 0, true, false, 0);
                        }
                        catch { _lscMenuCam = null; }
                        _lscEjectPhase = 2; _lscEjectSince = Game.GameTime;
                    }
                }
                else if (_lscEjectPhase == 2)
                {
                    // Eject (pop out of the driver seat) — fully hidden behind the custom menu cam.
                    if (_lscEjectVeh != null && _lscEjectVeh.Exists())
                    {
                        Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, Game.Player.Character);
                        Function.Call(Hash.SET_PED_INTO_VEHICLE, Game.Player.Character, _lscEjectVeh, -2);
                    }
                    _lscEjectPhase = 3; _lscEjectSince = Game.GameTime;
                }
                else if (_lscEjectPhase == 3)
                {
                    // Re-seat in the driver seat.
                    if (_lscEjectVeh != null && _lscEjectVeh.Exists())
                        Function.Call(Hash.SET_PED_INTO_VEHICLE, Game.Player.Character, _lscEjectVeh, -1);
                    _lscEjectPhase = 4; _lscEjectSince = Game.GameTime;
                }
                else if (_lscEjectPhase == 4)
                {
                    // Control returning confirms it was the menu -> open ELSC (re-applies the SAME menu framing +
                    // menuCamUntil), then hand our script cam back to the gameplay cam — same angle, so it's seamless.
                    bool controlBack = Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player);
                    if (controlBack)
                    {
                        TryOpenMenu();
                        Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                        Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
                        ReleaseLscHoldCam(false);   // RENDER_SCRIPT_CAMS off + delete the custom cam (seamless handoff)
                        _lscEjectPhase = 0; _lscEjectVeh = null;
                        Log("ELSC open -> handed menu cam back to ELSC's camera");
                    }
                    else if (Game.GameTime - _lscEjectSince > 700)
                    {
                        ReleaseLscHoldCam(false);   // misdetect -> stand down + clean up
                        _lscEjectPhase = 0; _lscEjectVeh = null;
                    }
                }
            }
            else
            {
                _lscEjectPhase = 0; _lscEjectVeh = null;   // ELSC open / real mission -> reset the eject state machine
                if ((_lscHoldCam != null || _lscMenuCam != null) && _lscHoldCamReleaseAt == 0) ReleaseLscHoldCam(false);   // never orphan a cam
            }

            // The rest of the LSC flow (interior preempt, marker, help suppression) stays gated on the old
            // mission check (which includes control-off) — only the eject takeover above needed to bypass it.
            if (IsOnMission()) return;

            var player = Game.Player.Character;
            int interior = Function.Call<int>(Hash.GET_INTERIOR_FROM_ENTITY, player);

            if (interior != lastInterior)
            {
                Log($"Interior changed: {lastInterior} -> {interior}");

                bool enteredLSC = Array.Exists(LSC_INTERIORS, id => id == interior);
                bool leftLSC = Array.Exists(LSC_INTERIORS, id => id == lastInterior);

                if (enteredLSC && player.IsInVehicle())
                {
                    isInLSC = true;   // the universal eject takeover auto-opens ELSC when the vanilla menu appears
                }
                else if (leftLSC)
                {
                    Log("Left Los Santos Customs - closing menu");
                    isInLSC = false;
                    lastVehiclePos = Vector3.Zero;
                    _lscCarCaptured = false;   // re-capture the nice spot on the next fresh entry

                    Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);

                    if (isMenuActive)
                    {
                        mainMenu.Visible = false;
                        CloseMenu();
                    }
                }

                lastInterior = interior;
            }

            // Remember the LSC world position while inside (used by leave-cleanup / nearby checks).
            if (isInLSC) lscWorldPos = Game.Player.Character.Position;

            if (isInLSC && isMenuActive)
            {
                Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
            }
        }

        #endregion

        #region Mechanic Management

        /// <summary>Play a short mechanic voice line on a mod purchase. With the mechanic-swap removed, the real
        /// carmod_shop bay mechanic is still present — find the nearest ped to the vehicle and speak from it.</summary>
        private void MechanicSpeak()
        {
            try
            {
                Vector3 pos = (currentVehicle != null && currentVehicle.Exists()) ? currentVehicle.Position : Game.Player.Character.Position;
                Ped mech = null; float best = 15f;
                foreach (var ped in World.GetNearbyPeds(pos, 15f))
                {
                    if (ped == null || !ped.Exists() || ped == Game.Player.Character) continue;
                    float d = ped.Position.DistanceTo(pos);
                    if (d < best) { best = d; mech = ped; }
                }
                if (mech == null) return;
                string[] speeches = { "GENERIC_THANKS", "GENERIC_BYE", "CHAT_STATE", "CHAT_RESP" };
                string speech = speeches[new Random().Next(speeches.Length)];
                Function.Call(Hash.PLAY_PED_AMBIENT_SPEECH_NATIVE, mech, speech, "SPEECH_PARAMS_FORCE_NORMAL");
            }
            catch { }
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
                smoothLookAt = walkAroundVehicle.Position + new Vector3(0, 0, 0.6f);  // start looking at car centre

                // Create scripted camera
                Vector3 camStartPos = new Vector3(cameraOrbitPos.X, cameraOrbitPos.Y, walkAroundVehicle.Position.Z + cameraHeight);
                walkAroundCam = World.CreateCamera(camStartPos, Vector3.Zero, 50f);
                walkAroundCam.PointAt(walkAroundVehicle);
                World.RenderingCamera = walkAroundCam;

                // Reset look offset and preset mode
                lookOffsetH = 0f;
                currentPresetIndex = 0;
                isInPresetMode = false;

                ShowNotification("~b~Camera Mode~w~\nLB/RB - Cycle Parts | X - Open/Close Doors\nLS/RS - Free Roam | Y - Exit");
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

        /// <summary>Y cycles the camera: Basic (gameplay/menu cam) -> Walk-Around (orbit) -> First-Person -> Basic.</summary>
        private void CycleCamera()
        {
            if (isWalkAroundActive) { ExitWalkAround(); EnterFirstPerson(); }
            else if (isFirstPersonActive) { ExitFirstPerson(); }   // -> back to Basic
            else { EnterWalkAround(); }
            cameraModeCooldown = 12;   // short debounce so the next Y press isn't eaten
        }

        /// <summary>Driver eye position for the first-person cam (driver-seat bone + eye height, slightly forward).</summary>
        private Vector3 FirstPersonEyePos()
        {
            var veh = currentVehicle;
            int bone = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, veh, "seat_dside_f");
            Vector3 seat = bone != -1
                ? Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, veh, bone)
                : veh.Position;
            return seat + veh.UpVector * 0.62f + veh.ForwardVector * 0.08f;
        }

        private void EnterFirstPerson()
        {
            try
            {
                if (currentVehicle == null || !currentVehicle.Exists()) return;
                isFirstPersonActive = true;
                _fpYaw = 0f; _fpPitch = 0f;
                Vector3 eye = FirstPersonEyePos();
                firstPersonCam = World.CreateCamera(eye, new Vector3(0f, 0f, currentVehicle.Heading), 60f);
                firstPersonCam.IsActive = true;
                World.RenderingCamera = firstPersonCam;
                ShowNotification("~b~First-Person~w~\nLook around | Y - Next camera");
                Log("Entered first-person camera mode");
            }
            catch (Exception ex) { Log($"ERROR EnterFirstPerson: {ex.Message}"); ExitFirstPerson(); }
        }

        private void ExitFirstPerson()
        {
            try
            {
                World.RenderingCamera = null;
                if (firstPersonCam != null && firstPersonCam.Exists()) firstPersonCam.Delete();
            }
            catch { }
            firstPersonCam = null;
            isFirstPersonActive = false;
            cameraModeCooldown = 12;
        }

        /// <summary>First-person look: right stick / mouse aims; the eye stays at the driver seat.</summary>
        private void UpdateFirstPerson()
        {
            if (currentVehicle == null || !currentVehicle.Exists() || firstPersonCam == null || !firstPersonCam.Exists())
            {
                ExitFirstPerson();
                return;
            }

            float lookH = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookLeftRight);
            float lookV = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookUpDown);
            _fpYaw -= lookH * FP_LOOK_SENS;
            _fpPitch -= lookV * FP_LOOK_SENS;
            _fpPitch = Math.Max(-80f, Math.Min(80f, _fpPitch));
            if (_fpYaw > 180f) _fpYaw -= 360f; else if (_fpYaw < -180f) _fpYaw += 360f;

            firstPersonCam.Position = FirstPersonEyePos();
            firstPersonCam.Rotation = new Vector3(_fpPitch, 0f, currentVehicle.Heading + _fpYaw);
        }

        /// <summary>
        /// Re-asserts the held steering angle on the inspected car every frame so its wheels don't snap back to
        /// center (the game zeroes a parked/driverless car's steering each frame). Runs in AND out of the menu so the
        /// angle survives the player leaving the car parked in free roam. Releases the hold once the car is actually
        /// driven (speed up, or the player steers while seated) so normal steering resumes.
        /// </summary>
        private void UpdateSteeringHold()
        {
            if (_steerHoldHandle == 0) return;
            try
            {
                var v = (Vehicle)Entity.FromHandle(_steerHoldHandle);
                if (v == null || !v.Exists())
                {
                    _steerHoldHandle = 0; _heldSteerDeg = 0f; return;
                }

                // Driven away → let go (the global SteeringFix patch then preserves the natural angle).
                if (v.Speed > 1.2f) { _steerHoldHandle = 0; _heldSteerDeg = 0f; return; }

                // Player seated and actively steering OUTSIDE the menu → hand control back.
                if (!isMenuActive)
                {
                    var drv = v.Driver;
                    if (drv != null && drv.Exists() && drv == Game.Player.Character)
                    {
                        float steerIn = Function.Call<float>(Hash.GET_CONTROL_NORMAL, 0, (int)GTA.Control.VehicleMoveLeftRight);
                        if (Math.Abs(steerIn) > 0.1f) { _steerHoldHandle = 0; _heldSteerDeg = 0f; return; }
                    }
                }

                v.SteeringAngle = _heldSteerDeg;
            }
            catch { _steerHoldHandle = 0; }
        }

        /// <summary>
        /// D-pad / arrow Left+Right turns the given vehicle's front wheels for inspection (held by UpdateSteeringHold
        /// + the global SteeringFix patch so they don't snap back). Reads the controls as DISABLED so it works whether
        /// they're disabled-for-the-menu (walk-around) or live (normal menu). Shared by walk-around and the menu.
        /// </summary>
        private void HandleInspectSteering(Vehicle veh)
        {
            if (!ModSettings.KeepSteeringAngle || veh == null || !veh.Exists()) return;

            bool sLeft  = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLeft);
            bool sRight = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendRight);
            if (sLeft == sRight) return;   // none, or both — do nothing

            // Seed from the wheels' real angle the first time we grab THIS car, so there's no jump.
            if (_steerHoldHandle != veh.Handle)
            {
                float cur = 0f;
                try { cur = veh.SteeringAngle; } catch { }
                _heldSteerDeg = Math.Max(-STEER_HOLD_MAX_DEG, Math.Min(STEER_HOLD_MAX_DEG, cur));
            }
            // Right turns the wheels right, Left turns them left (GTA's SteeringAngle is +left/-right, so negate).
            _heldSteerDeg += (sRight ? -1f : 1f) * 1.6f;   // ~1.6 deg/frame
            _heldSteerDeg = Math.Max(-STEER_HOLD_MAX_DEG, Math.Min(STEER_HOLD_MAX_DEG, _heldSteerDeg));
            _steerHoldHandle = veh.Handle;
            try { veh.SteeringAngle = _heldSteerDeg; } catch { }   // immediate visual feedback
        }

        /// <summary>
        /// True if the menu item already uses Left/Right (list / slider). On those, Left/Right adjusts the item, so
        /// the inspect-steering control stands down to avoid fighting it.
        /// </summary>
        private static bool ItemUsesLeftRight(LemonUI.Menus.NativeItem item)
        {
            if (item == null) return false;
            string n = item.GetType().Name;
            return n.IndexOf("List", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Slider", StringComparison.OrdinalIgnoreCase) >= 0;
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

            // Walk-around button-hint bar (Cycle View / Move / Look / Open Door / Exit) drawn on OUR OWN
            // instanced instructional-buttons scaleform at GFX order 7 — see DrawWalkAroundHints().
            // Hidden while the idle cinematic fades (its scaleform would draw over the fake-fade overlay).
            if (_idlePhase == 0) DrawWalkAroundHints();

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
            Game.DisableControlThisFrame(GTA.Control.FrontendX);   // reserved for open/close door (below)

            // Rev conflict fix: on keyboard W is BOTH our forward key AND VehicleAccelerate (rev). Suppress the rev
            // while W moves, and put keyboard revving on its own key (R) by forcing the throttle. Keys.W/R only ever
            // register on keyboard, so the controller's RT (= VehicleAccelerate) keeps revving normally.
            bool kbRev = Game.IsKeyPressed(Keys.R);
            if (Game.IsKeyPressed(Keys.W) && !kbRev)
                Game.DisableControlThisFrame(GTA.Control.VehicleAccelerate);
            if (kbRev)
                Function.Call(Hash.SET_CONTROL_VALUE_NEXT_FRAME, 0, (int)GTA.Control.VehicleAccelerate, 1.0f);

            // NOTE: the Y button (cycle camera) is handled centrally in OnTick (CycleCamera) for all modes; from
            // walk-around it advances to first-person. Don't read it here too or it would double-fire.

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

            // LB/RB to cycle through preset camera positions. Use the frontend bumper controls (the menu is open,
            // so the pad is in frontend mode); the old Aim/Cover mapping had Aim = Left TRIGGER, so LB never fired.
            bool lbPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, ModSettings.CamPrevButton);
            bool rbPressed = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, ModSettings.CamNextButton)
                          || Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.Cover);

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

            // X to open/close the door nearest the camera. Deliberately NOT A (FrontendAccept) — that's the
            // menu's buy/select button, and sharing it opened a door every time you bought a part.
            // Suppressed while the nitrous picker is open: there, X (= the NOS spray button) tests the nitro.
            if (!nosColorMenuOpen && Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, ModSettings.DoorButton))
            {
                int di = LookedAtPart(walkAroundVehicle, walkAroundCam.Position);
                if (di >= 0) ToggleDoor(di, DoorName(di));
                else ShowNotification("~w~No openable doors here.");
            }

            // D-pad Left/Right turns the front wheels so you can inspect them. The angle is held by UpdateSteeringHold
            // (and the global SteeringFix patch) so it doesn't snap back when you leave the car. Controls are disabled
            // for the menu this frame (see OnTick) so they don't double-change a highlighted list item.
            HandleInspectSteering(walkAroundVehicle);

            // MOVEMENT — left stick (controller) OR WASD (keyboard). Read additively + clamp so either works.
            float inputForward = -Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.VehicleMoveUpDown);
            float inputRight = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.VehicleMoveLeftRight);
            if (Game.IsKeyPressed(Keys.W)) inputForward += 1f;
            if (Game.IsKeyPressed(Keys.S)) inputForward -= 1f;
            if (Game.IsKeyPressed(Keys.D)) inputRight += 1f;
            if (Game.IsKeyPressed(Keys.A)) inputRight -= 1f;
            inputForward = Math.Max(-1f, Math.Min(1f, inputForward));
            inputRight = Math.Max(-1f, Math.Min(1f, inputRight));

            // LOOK — right stick (controller) or mouse movement (keyboard). In walk-around there's no active cursor,
            // so the game maps the mouse straight to LookLeftRight/UpDown = free-look. No click needed.
            float lookInputH = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookLeftRight);
            float lookInputV = Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookUpDown);

            // If any stick / mouse input moved, exit preset mode and return to free roam
            bool stickMoved = Math.Abs(inputForward) > 0.2f || Math.Abs(inputRight) > 0.2f ||
                              Math.Abs(lookInputH) > 0.2f || Math.Abs(lookInputV) > 0.2f;

            if (stickMoved && isInPresetMode)
            {
                isInPresetMode = false;
                // Continue free roam from wherever the framing left the camera (avoids a jump).
                cameraOrbitPos = smoothCamPos;
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

            // Decide the desired camera pose. In PRESET mode we use the framing computed from the part's actual
            // bone (see FramePreset) and look straight at the part. In FREE ROAM we orbit a clamped box and look
            // at the car centre. Both are smoothed below for clean transitions.
            Vector3 desiredCamPos;
            Vector3 desiredLookAt;

            if (isInPresetMode)
            {
                desiredCamPos = presetCamPos;
                desiredLookAt = presetTargetWorld;
            }
            else
            {
                // Clamp the orbit position to a box around the vehicle (tighter sides, more room front/back).
                Vector3 localPos = walkAroundVehicle.GetPositionOffset(cameraOrbitPos);
                float clampedX = localPos.X;
                float clampedY = localPos.Y;
                if (Math.Abs(clampedX) > vehicleHalfWidth) clampedX = Math.Sign(clampedX) * vehicleHalfWidth;
                if (Math.Abs(clampedY) > vehicleHalfLength) clampedY = Math.Sign(clampedY) * vehicleHalfLength;
                float localDist = (float)Math.Sqrt(clampedX * clampedX + clampedY * clampedY);
                if (localDist < vehicleCamMinDist && localDist > 0.01f)
                {
                    float scale = vehicleCamMinDist / localDist;
                    clampedX *= scale; clampedY *= scale;
                }
                cameraOrbitPos = walkAroundVehicle.GetOffsetPosition(new Vector3(clampedX, clampedY, 0));
                smoothCamHeight = smoothCamHeight + (cameraHeight - smoothCamHeight) * CAM_SMOOTH_SPEED;
                desiredCamPos = new Vector3(cameraOrbitPos.X, cameraOrbitPos.Y, walkAroundVehicle.Position.Z + smoothCamHeight);

                // Look at the car centre with the horizontal look offset applied.
                Vector3 vehicleCenter = walkAroundVehicle.Position; vehicleCenter.Z += 0.6f;
                Vector3 baseLookDir = vehicleCenter - desiredCamPos;
                float offsetRadH = lookOffsetH * (float)Math.PI / 180f;
                Vector3 lookDir = new Vector3(
                    baseLookDir.X * (float)Math.Cos(offsetRadH) - baseLookDir.Y * (float)Math.Sin(offsetRadH),
                    baseLookDir.X * (float)Math.Sin(offsetRadH) + baseLookDir.Y * (float)Math.Cos(offsetRadH),
                    baseLookDir.Z);
                desiredLookAt = desiredCamPos + lookDir;

                // Keep the free-roam camera outside the vehicle's bounding box (radial push-out).
                Vector3 lcp = walkAroundVehicle.GetPositionOffset(desiredCamPos);
                float minDistX = (vehicleHalfWidth - 1.5f) + 0.3f;
                float minDistY = (vehicleHalfLength - 2.0f) + 0.3f;
                if (Math.Abs(lcp.X) < minDistX && Math.Abs(lcp.Y) < minDistY)
                {
                    float angle = (float)Math.Atan2(lcp.Y, lcp.X);
                    float cosA = (float)Math.Abs(Math.Cos(angle)), sinA = (float)Math.Abs(Math.Sin(angle));
                    float eX = cosA > 0.001f ? minDistX / cosA : float.MaxValue;
                    float eY = sinA > 0.001f ? minDistY / sinA : float.MaxValue;
                    float edge = Math.Min(eX, eY);
                    lcp.X = (float)Math.Cos(angle) * edge; lcp.Y = (float)Math.Sin(angle) * edge;
                    Vector3 pushed = walkAroundVehicle.GetOffsetPosition(lcp);
                    desiredCamPos.X = pushed.X; desiredCamPos.Y = pushed.Y;
                }
            }

            // Smoothly move BOTH the camera position and the look target toward the desired pose.
            smoothCamPos = Vector3.Lerp(smoothCamPos, desiredCamPos, CAM_SMOOTH_SPEED);
            smoothLookAt = Vector3.Lerp(smoothLookAt, desiredLookAt, CAM_SMOOTH_SPEED);
            walkAroundCam.Position = smoothCamPos;
            walkAroundCam.PointAt(smoothLookAt);

            // Button helpers. The bottom hint bar (drawn above, with glyphs) is the primary helper in BOTH cases.
            // OUTSIDE an LSC there's no carmod_shop, so we can also show the native help bubble (mode + preset name)
            // for the original look. INSIDE an LSC that bubble would beep (carmod_shop owns the slot) and our drawn
            // text would overlap the menu — so we skip the top helper there and rely on the bottom hint bar.
            bool preset = isInPresetMode && currentPresetIndex >= 0 && currentPresetIndex < availablePresets.Count;
            if (!isInLSC && ModSettings.ShowHints)
            {
                ShowSilentHelp(preset
                    ? $"~b~{availablePresets[currentPresetIndex].Name}~w~ ({currentPresetIndex + 1}/{availablePresets.Count})\nLB/RB - Cycle | Stick - Free Roam"
                    : $"~w~Free Roam~w~\nLB/RB - Part Views ({availablePresets.Count}) | X - Doors | Y - Exit");
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

        // door bones by VehicleDoorIndex: 0 FL, 1 FR, 2 RL, 3 RR, 4 Hood, 5 Trunk. A part only exists on this
        // car if its bone resolves — that's how we avoid trying to open doors a 2-door (or no-trunk) car lacks.
        private static readonly string[] _doorBones =
            { "door_dside_f", "door_pside_f", "door_dside_r", "door_pside_r", "bonnet", "boot" };

        private bool DoorExists(Vehicle v, int idx)
        {
            if (v == null || !v.Exists() || idx < 0 || idx >= _doorBones.Length) return false;
            return Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v, _doorBones[idx]) >= 0;
        }

        /// <summary>Pick the part the camera is VIEWING based on which edge of the car it's off of: front/back
        /// edge => hood/trunk, side edge => the door in that quadrant. Coordinates are normalized by the car's
        /// width/length so it works on any size vehicle. Falls back to the nearest existing part if the ideal
        /// one doesn't exist on this model (e.g. no hood). This is more intuitive than nearest-bone: standing at
        /// the front opens the hood, not a front door.</summary>
        private int LookedAtPart(Vehicle v, Vector3 camPos)
        {
            if (v == null || !v.Exists()) return -1;
            Vector3 lp = v.GetPositionOffset(camPos);                 // X = right(+)/left(-), Y = front(+)/back(-)
            float nx = lp.X / Math.Max(0.1f, vehicleHalfWidth);      // -1..~1 across the width
            float ny = lp.Y / Math.Max(0.1f, vehicleHalfLength);     // -1..~1 along the length
            int ideal;
            if (Math.Abs(ny) > Math.Abs(nx))                          // more off the front/back than the sides
                ideal = ny > 0 ? 4 : 5;                              // Hood / Trunk
            else
                ideal = ny > 0 ? (nx > 0 ? 1 : 0) : (nx > 0 ? 3 : 2); // door by quadrant (FL/FR/RL/RR)
            if (DoorExists(v, ideal)) return ideal;
            return NearestValidDoor(v, camPos);                       // ideal part absent -> nearest existing one
        }

        /// <summary>Index (0-5) of the openable door/part whose bone is physically closest to <paramref name="fromPos"/>,
        /// skipping any that don't exist on this vehicle. Returns -1 if the car has none.</summary>
        private int NearestValidDoor(Vehicle v, Vector3 fromPos)
        {
            if (v == null || !v.Exists()) return -1;
            int best = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _doorBones.Length; i++)
            {
                int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v, _doorBones[i]);
                if (bi < 0) continue;   // this door/part doesn't exist on this model
                Vector3 boneWorld = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, v, bi);
                float d = fromPos.DistanceTo(boneWorld);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        private string DoorName(int idx)
        {
            switch (idx)
            {
                case 0: return "Front Left Door";
                case 1: return "Front Right Door";
                case 2: return "Rear Left Door";
                case 3: return "Rear Right Door";
                case 4: return "Hood";
                case 5: return "Trunk";
                default: return "Door";
            }
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
                        if (ModSettings.ShowHints) ShowNotification($"~w~Closed {doorName}");
                    }
                    else
                    {
                        Function.Call(Hash.SET_VEHICLE_DOOR_OPEN, walkAroundVehicle, doorIndex, false, false);
                        if (ModSettings.ShowHints) ShowNotification($"~w~Opened {doorName}");
                    }
                }
            }
            catch { }
        }

        #region Idle showcase cinematic (attract mode)

        /// <summary>Run the idle cinematic state machine each tick while the menu is up. Triggers after ~10s of no
        /// input; fades to black, hides the HUD/menu, slowly orbits the car, fading on each side switch. Any input
        /// snaps the camera back and unhides everything. Safe: it can never leave the screen black or the cam stuck.</summary>
        private void UpdateIdleCinematic()
        {
            bool eligible = ModSettings.CustomCamera && ModSettings.IdleCinematic
                && (isMenuActive || isWalkAroundActive) && _lscEjectPhase == 0   // also runs in walk-around mode
                && currentVehicle != null && currentVehicle.Exists() && !IsTypingText;

            if (!eligible)
            {
                if (_idlePhase != 0) StopIdleCinematic(true);
                _lastInteractionTime = Game.GameTime;
                return;
            }

            // Any menu/camera input wakes it: reset the idle timer and (if a cinematic is running) snap back.
            if (IsMenuIdleInput())
            {
                _lastInteractionTime = Game.GameTime;
                if (_idlePhase != 0) StopIdleCinematic(true);
                return;
            }

            int now = Game.GameTime;
            try
            {
                switch (_idlePhase)
                {
                    case 0:  // idle — wait for the trigger
                        if (now - _lastInteractionTime > IDLE_DELAY_MS) BeginIdleCinematic();
                        break;

                    case 1:  // fading the menu to black before the first shot
                        if (_idleScreenBlack)
                        {
                            SetupIdleShot(true);              // create + cut to the orbit cam while black
                            StartIdleFade(0f, 900f);          // fade the overlay back out
                            _idlePhase = 2; _idleSideStart = now;
                        }
                        break;

                    case 2:  // playing — slow orbit; switch sides after the per-side time
                        UpdateIdleShot();
                        if (now - _idleSideStart > IDLE_PER_SIDE_MS)
                        {
                            StartIdleFade(255f, 450f);        // fade overlay to black for the switch
                            _idlePhase = 3;
                        }
                        break;

                    case 3:  // fading out before a switch — KEEP MOVING, then jump to a new side once fully black
                        UpdateIdleShot();
                        if (_idleScreenBlack)
                        {
                            SetupIdleShot(false);
                            StartIdleFade(0f, 700f);
                            _idlePhase = 2; _idleSideStart = now;
                        }
                        break;
                }
            }
            catch (Exception ex) { Log($"[Idle] {ex.Message}"); StopIdleCinematic(true); }
        }

        private void BeginIdleCinematic()
        {
            _idlePrevRenderingCam = World.RenderingCamera;   // remember what to return to (null = gameplay)
            StartIdleFade(255f, 500f);                       // fade the (fake) overlay to black to enter
            _idlePhase = 1;
        }

        /// <summary>Aim the fake-fade overlay at a target opacity (0 clear / 255 black) over the given duration.</summary>
        private void StartIdleFade(float target, float durationMs)
        {
            _idleFadeTarget = Math.Max(0f, Math.Min(255f, target));
            _idleFadeRate = durationMs > 1f ? 255f / durationMs : 255f;
        }

        /// <summary>Advance the overlay alpha toward its target (time-based). Runs every frame, independent of the
        /// cinematic state, so the overlay still finishes fading out after the cinematic stops.</summary>
        private void UpdateIdleFade()
        {
            if (_idleFadeAlpha == _idleFadeTarget) return;
            float step = _idleFadeRate * (Game.LastFrameTime * 1000f);
            if (_idleFadeAlpha < _idleFadeTarget) _idleFadeAlpha = Math.Min(_idleFadeTarget, _idleFadeAlpha + step);
            else _idleFadeAlpha = Math.Max(_idleFadeTarget, _idleFadeAlpha - step);
        }

        /// <summary>Draw the fake-fade black box. Oversized + centered so it fully covers ANY aspect ratio (incl.
        /// ultrawide / triple-monitor) — the excess is clipped. Drawn last in OnTick so it sits on top of the menu.</summary>
        private void DrawIdleFadeOverlay()
        {
            if (_idleFadeAlpha <= 0.5f) return;
            int a = (int)Math.Max(0f, Math.Min(255f, _idleFadeAlpha));
            Function.Call(Hash.DRAW_RECT, 0.5f, 0.5f, 1.5f, 1.5f, 0, 0, 0, a, 0);
        }

        /// <summary>Position the orbit camera for a side (created while the screen is black so the cut isn't seen).</summary>
        private void SetupIdleShot(bool firstShot)
        {
            ComputeCarFraming();
            if (firstShot) _idleSideBaseDeg = _idleRng.Next(0, 360);
            else
            {
                float delta = 70f + _idleRng.Next(0, 220);            // jump to a clearly different side (wider spread)
                if (_idleRng.Next(2) == 0) delta = -delta;
                _idleSideBaseDeg = (((_idleSideBaseDeg + delta) % 360f) + 360f) % 360f;
            }

            // Pick a fresh shot STYLE each side so it mixes high/low/close/wide instead of repeating a few views.
            float r() => (float)_idleRng.NextDouble();
            switch (_idleRng.Next(0, 6))
            {
                case 0:  _idleShotHeight = 0.20f + r() * 0.30f;          _idleShotDistF = 0.95f + r() * 0.20f; _idleShotFov = 52f; break;  // low hero (looks up)
                case 1:  _idleShotHeight = 0.45f + r() * 0.35f;          _idleShotDistF = 1.25f + r() * 0.30f; _idleShotFov = 55f; break;  // low + wide
                case 2:  _idleShotHeight = cameraHeight * 0.9f;          _idleShotDistF = 1.05f + r() * 0.25f; _idleShotFov = 45f; break;  // eye-level
                case 3:  _idleShotHeight = cameraHeight * 1.5f + 0.5f;   _idleShotDistF = 1.10f + r() * 0.30f; _idleShotFov = 42f; break;  // slightly high
                case 4:  _idleShotHeight = cameraHeight * 2.4f + 1.0f;   _idleShotDistF = 1.00f + r() * 0.30f; _idleShotFov = 40f; break;  // high overhead (looks down)
                default: _idleShotHeight = cameraHeight * 1.2f;          _idleShotDistF = 1.55f + r() * 0.45f; _idleShotFov = 36f; break;  // pulled back, tele
            }
            _idleSlideSign = _idleRng.Next(2) == 0 ? 1 : -1;            // drift left or right this side

            Vector3 pos = IdleCamPos(_idleSideBaseDeg);
            if (_idleCam == null || !_idleCam.Exists())
            {
                _idleCam = World.CreateCamera(pos, Vector3.Zero, _idleShotFov);
                _idleCam.IsActive = true;
                Function.Call(Hash.RENDER_SCRIPT_CAMS, true, false, 0, true, false, 0);   // cut to the script cam
            }
            else { _idleCam.Position = pos; try { _idleCam.FieldOfView = _idleShotFov; } catch { } }
            _idleCam.PointAt(_idleCenter);
        }

        private void UpdateIdleShot()
        {
            if (_idleCam == null || !_idleCam.Exists()) { StopIdleCinematic(true); return; }
            // NOT capped at 1 — the slide keeps going through the fade-out so the camera never freezes; the actual
            // jump to a new side happens only while the screen is fully black.
            float t = (Game.GameTime - _idleSideStart) / (float)IDLE_PER_SIDE_MS;
            _idleCam.Position = IdleCamPos(_idleSideBaseDeg + t * IDLE_SLIDE_DEG * _idleSlideSign);
            _idleCam.PointAt(_idleCenter);
        }

        /// <summary>End the cinematic: restore the prior camera (cut), delete the orbit cam, fade back in if the
        /// screen is black, and unhide everything next frame. Used on input AND when no longer eligible.</summary>
        private void StopIdleCinematic(bool fadeInIfBlack)
        {
            try
            {
                if (_idlePrevRenderingCam != null && _idlePrevRenderingCam.Exists())
                    World.RenderingCamera = _idlePrevRenderingCam;
                else
                    Function.Call(Hash.RENDER_SCRIPT_CAMS, false, false, 0, true, false, 0);   // back to gameplay cam
            }
            catch { }
            try { if (_idleCam != null && _idleCam.Exists()) _idleCam.Delete(); } catch { }
            _idleCam = null;
            _idlePrevRenderingCam = null;
            // Never leave the player on a black screen — fade the overlay out fast if it's up (snap clear otherwise).
            if (fadeInIfBlack && _idleFadeAlpha > 0f) StartIdleFade(0f, 250f);
            else { _idleFadeAlpha = 0f; _idleFadeTarget = 0f; }
            _idlePhase = 0;
            _lastInteractionTime = Game.GameTime;
        }

        private void ComputeCarFraming()
        {
            // Reuse the SAME per-vehicle extents as walk-around (vehicleHalfWidth/Length + cameraHeight) so the idle
            // orbit sits right at the bounding box, not way out in a big circle.
            CalculateVehicleCameraDistances(currentVehicle);
            _idleCenter = currentVehicle.Position; _idleCenter.Z += 0.6f;   // look at the car centre (matches walk-around)
        }

        private Vector3 IdleCamPos(float deg)
        {
            // Orbit on the vehicle's bounding-box ellipse in its LOCAL frame (a touch outside), at the walk-around
            // camera height — same framing the player gets when free-roaming the camera.
            float rad = deg * (float)Math.PI / 180f;
            Vector3 local = new Vector3((float)Math.Cos(rad) * vehicleHalfWidth * _idleShotDistF, (float)Math.Sin(rad) * vehicleHalfLength * _idleShotDistF, 0f);
            Vector3 world = currentVehicle.GetOffsetPosition(local);
            return new Vector3(world.X, world.Y, currentVehicle.Position.Z + _idleShotHeight);
        }

        /// <summary>Any menu navigation / button / stick movement this frame (read even while disabled). NOTE: no
        /// mouse-cursor check — its reading fluctuates after DisableAllControls and was firing every frame, which
        /// pinned the idle timer and stopped the cinematic re-triggering after the first run.</summary>
        private bool IsMenuIdleInput()
        {
            var vm = GetVisibleMenu();
            int sel = vm != null ? vm.SelectedIndex : -1;
            bool selChanged = sel != _idlePrevSelIndex;
            _idlePrevSelIndex = sel;
            if (selChanged) return true;
            foreach (var c in _idleInputCtrls)
                if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)c)) return true;
            // Left stick / d-pad (menu nav).
            if (Math.Abs(Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.FrontendAxisX)) > 0.25f) return true;
            if (Math.Abs(Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.FrontendAxisY)) > 0.25f) return true;
            // Right stick / mouse (camera look) — swinging the camera must count as input too. These are momentary
            // axis values (~0 at rest), so they don't fluctuate like the mouse-cursor position did.
            if (Math.Abs(Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookLeftRight)) > 0.12f) return true;
            if (Math.Abs(Function.Call<float>(Hash.GET_DISABLED_CONTROL_NORMAL, 0, (int)GTA.Control.LookUpDown)) > 0.12f) return true;
            return false;
        }

        #endregion

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
            lookOffsetH = 0f;

            // Frame the actual part (from its bone) instead of a hand-tuned offset, and look straight at it.
            FramePreset(presetIndex);

            // Navigate menu to highlight the corresponding mod category
            NavigateMenuToModType(availablePresets[presetIndex].ModType);
        }

        // Representative bone per mod type for accurate framing. null => no reliable bone (use the offset hint).
        private string BoneForMod(VehicleModType m)
        {
            switch (m)
            {
                case VehicleModType.FrontBumper: return "bumper_f";
                case VehicleModType.RearBumper: return "bumper_r";
                case VehicleModType.Hood: return "bonnet";
                case VehicleModType.Engine: return "engine";
                case VehicleModType.Spoilers: return "spoiler";
                case VehicleModType.Roof: return "roof";
                case VehicleModType.Exhaust: return "exhaust";
                case VehicleModType.Grille: return "bumper_f";
                default: return null;   // SideSkirt / Fender / Frame: no reliable bone -> fall back to the offset
            }
        }

        // The part's location in vehicle-local meters: the real bone if it exists, else the preset's offset hint.
        private Vector3 PartLocalTarget(Vehicle v, VehicleModType modType, Vector3 fallbackOffset)
        {
            string bone = BoneForMod(modType);
            if (bone != null)
            {
                int bi = Function.Call<int>(Hash.GET_ENTITY_BONE_INDEX_BY_NAME, v, bone);
                if (bi >= 0)
                {
                    Vector3 w = Function.Call<Vector3>(Hash.GET_WORLD_POSITION_OF_ENTITY_BONE, v, bi);
                    return v.GetPositionOffset(w);
                }
            }
            return new Vector3(fallbackOffset.X * vehicleHalfWidth, fallbackOffset.Y * vehicleHalfLength, fallbackOffset.Z);
        }

        /// <summary>Compute a camera pose that frames the part for <paramref name="presetIndex"/>: sit outside the
        /// car along the line from its centre to the part, a bit above it, looking right at it.</summary>
        private void FramePreset(int presetIndex)
        {
            var preset = availablePresets[presetIndex];
            Vector3 tLocal = PartLocalTarget(walkAroundVehicle, preset.ModType, preset.LocalCameraOffset);
            Vector3 tWorld = walkAroundVehicle.GetOffsetPosition(tLocal);
            presetTargetWorld = tWorld;

            // Outward = horizontal direction from the car centre to the part. Central parts (hood/roof/trunk)
            // have ~no sideways component, so back off along the car's length instead.
            Vector3 outward = walkAroundVehicle.GetOffsetPosition(new Vector3(tLocal.X, tLocal.Y, 0f)) - walkAroundVehicle.Position;
            outward.Z = 0f;
            if (outward.Length() < 0.4f)
                outward = walkAroundVehicle.ForwardVector * (tLocal.Y >= 0f ? 1f : -1f);
            outward.Normalize();

            float dist = 1.2f + Math.Max(vehicleHalfWidth, vehicleHalfLength) * 0.25f;   // close, part-filling framing
            Vector3 cam = tWorld + outward * dist;
            cam.Z = tWorld.Z + 0.55f;   // a touch above the part
            presetCamPos = cam;
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

        // Position of a mod type's category in the on-screen menu (int.MaxValue if it isn't shown).
        private int MenuIndexOf(VehicleModType modType)
        {
            try
            {
                if (mainMenu == null) return int.MaxValue;
                string targetName = ModCategories.GetDisplayName((int)modType);
                for (int i = 0; i < mainMenu.Items.Count; i++)
                    if (mainMenu.Items[i].Title == targetName) return i;
            }
            catch { }
            return int.MaxValue;
        }

        private void BuildAvailablePresets()
        {
            availablePresets.Clear();

            if (walkAroundVehicle == null) return;

            // Check which mod types are available for this vehicle. Only keep presets whose category is actually
            // SHOWN in the menu — otherwise cycling to it leaves the menu selection stuck (it can't navigate to an
            // item that isn't there) while the camera still moves, which looks broken.
            foreach (var preset in AllModPresets)
            {
                int modCount = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, walkAroundVehicle, (int)preset.ModType);
                bool inMenu = MenuIndexOf(preset.ModType) != int.MaxValue;
                if (modCount > 0 && inMenu)
                {
                    availablePresets.Add(preset);
                    Log($"Added preset: {preset.Name} ({modCount} mods available)");
                }
                else if (modCount > 0)
                {
                    Log($"Skipped preset {preset.Name}: not present in the menu");
                }
            }

            // Order presets to match the on-screen menu, so RB steps the selection FORWARD (down the menu) and
            // LB steps it BACKWARD — instead of jumping around because the preset list was in a different order.
            if (mainMenu != null && mainMenu.Items.Count > 0)
                availablePresets.Sort((a, b) => MenuIndexOf(a.ModType).CompareTo(MenuIndexOf(b.ModType)));

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
        /// <summary>Show the native help bubble WITHOUT a beep, via the raw natives (SHVDN's ShowHelpTextThisFrame
        /// re-triggers the appear-sound every frame even with beep:false). Called per-frame, it persists and
        /// overrides the game's own help bubble in the same slot — same look as before, no constant beep.</summary>
        private void ShowSilentHelp(string text)
        {
            if (!ModSettings.ShowHints) return;   // INI: suppress ELSC's hint bubbles everywhere
            Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_HELP, "STRING");
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_HELP, 0, false, false, -1);   // loop=false, BEEP=false, shape=none
        }

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
        // Edit mode blanket guard for commit (apply/install) handlers. Returns true (and notifies) when the
        // modder is editing, so the caller bails before changing anything. Preview-on-hover never calls this,
        // so previews + name/price editing still work. TryPurchase also calls it as a backstop.
        private bool EditBlockApply()
        {
            if (!editModeActive) return false;
            ShowNotification("~y~Edit mode: preview only (nothing applied)");
            return true;
        }

        private bool TryPurchase(int price, bool alreadyOwned)
        {
            // Backstop: every commit that flows through here is blocked while editing (paid OR free).
            if (EditBlockApply()) return false;

            if (ModSettings.AllItemsFree) return true;   // INI: everything's free
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
            editDescVehicleSpecific = false;   // base editor writes the shared Default; the veh-specific path sets this true

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
                    if (editDescVehicleSpecific) MenuConfig.SetVehicleDescription(editingCategoryName, result);
                    else MenuConfig.SetDescription(editingCategoryName, result);
                    editDescVehicleSpecific = false;
                    ShowNotification($"~g~Description saved for: {editingCategoryName}");
                    Log($"[DescEditor] Saved description for '{editingCategoryName}': {result}");

                    // Rebuild menus to show updated description (stays on the menu you were editing)
                    if (currentVehicle != null) RebuildMenusForVehicle();
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

        /// <summary>
        /// Per-INSTANCE save key for a vehicle: "{DisplayName}|{PLATE}" (model + plate). This is what every
        /// VehicleSaveData call keys off, so two cars of the same model with different plates keep SEPARATE
        /// builds (a freshly-spawned stock car no longer inherits a modified car's saved build). Empty/missing
        /// plate -> "NOPLATE". VehicleSaveData lowercases keys internally, so case here doesn't matter.
        /// </summary>
        private string VehicleKey(Vehicle v)
        {
            if (v == null || !v.Exists()) return "";
            string plate;
            try { plate = (Function.Call<string>(Hash.GET_VEHICLE_NUMBER_PLATE_TEXT, v) ?? "").Trim().ToUpperInvariant(); }
            catch (Exception ex) { Log($"[VehicleKey] plate read failed: {ex.Message}"); plate = ""; }
            if (string.IsNullOrEmpty(plate)) plate = "NOPLATE";
            return v.DisplayName + "|" + plate;
        }

        /// <summary>An 8-char plate ("EL" + 6 random digits) not used by any spawned vehicle nor any active claim.</summary>
        private string UniquePlate()
        {
            // Plates already in use by a live vehicle or claimed this session.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var v in World.GetAllVehicles())
                    if (v != null && v.Exists()) used.Add(GetPlateText(v).ToUpperInvariant());
            }
            catch (Exception ex) { Log($"[UniquePlate] enumerate failed: {ex.Message}"); }
            foreach (var k in _plateClaims.Keys) used.Add(k);

            for (int attempt = 0; attempt < 64; attempt++)
            {
                string p = "EL" + _plateRng.Next(0, 1000000).ToString("D6");   // 8 chars, valid plate text
                if (!used.Contains(p)) return p;
            }
            // Extremely unlikely fallback (still 8 chars).
            return "EL" + _plateRng.Next(0, 1000000).ToString("D6");
        }

        /// <summary>
        /// Called once per newly-bound car (BEFORE ELSC reads/applies its saved build). If this car's plate is
        /// empty, or collides with a DIFFERENT live vehicle that already claimed it this session, assign a fresh
        /// unique plate so this instance gets its own per-instance key (and stays stock). Otherwise just record
        /// the claim. The first-claimed (already-built) car keeps its plate + data; the duplicate is the one
        /// re-plated. No data migration here — a fresh duplicate has no build to carry.
        /// </summary>
        private void EnsureUniquePlate(Vehicle v)
        {
            if (v == null || !v.Exists()) return;
            try
            {
                string plate = GetPlateText(v).ToUpperInvariant();

                bool collides = false;
                if (string.IsNullOrEmpty(plate))
                {
                    collides = true;   // no plate at all -> give it a stable unique one
                }
                else if (_plateClaims.TryGetValue(plate, out int owner) && owner != v.Handle)
                {
                    // Another handle claimed this plate. Only a conflict if that car is still a live, different vehicle.
                    bool ownerAlive = false;
                    foreach (var other in World.GetAllVehicles())
                        if (other != null && other.Exists() && other.Handle == owner) { ownerAlive = true; break; }
                    if (ownerAlive)
                        collides = true;
                    else
                        _plateClaims.Remove(plate);   // stale claim — free it
                }

                if (collides)
                {
                    string p = UniquePlate();
                    Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, v, p);
                    // This is a fresh/duplicate instance — clear any window tint that bled over from the car it used
                    // to share a plate with (the custom-glass manager applies saved colours by model+plate). A
                    // genuinely fresh spawn has no tint; if the player wants one they set it via ELSC (saves anew).
                    try { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, 0); } catch { }
                    _plateClaims[p] = v.Handle;
                    Log($"[Plate] Duplicate/empty plate on '{v.DisplayName}' -> assigned unique '{p}'");
                }
                else
                {
                    _plateClaims[plate] = v.Handle;
                }
            }
            catch (Exception ex) { Log($"[Plate] EnsureUniquePlate failed: {ex.Message}"); }
        }

        /// <summary>
        /// Guard wrapper: detect a NEW car the player is driving and run EnsureUniquePlate exactly once per bind
        /// (tracked by Handle), before any save-data read/apply this frame. Called from OnTick.
        /// </summary>
        private void BindPlateForCurrentVehicle()
        {
            try
            {
                var ped = Game.Player.Character;
                if (ped == null || !ped.IsInVehicle()) { _lastPlateBoundHandle = 0; return; }
                var v = ped.CurrentVehicle;
                if (v == null || !v.Exists()) { _lastPlateBoundHandle = 0; return; }
                if (v.Handle == _lastPlateBoundHandle) return;   // already handled this car
                _lastPlateBoundHandle = v.Handle;
                EnsureUniquePlate(v);
            }
            catch (Exception ex) { Log($"[Plate] BindPlateForCurrentVehicle failed: {ex.Message}"); }
        }

        private int _lastPlateSweep = 0;

        /// <summary>Throttled world sweep: when two or more nearby cars share the SAME model AND plate (the classic
        /// "all my Menyoo cars are plated MENYOO" case), give the extras a fresh unique plate so each owns its own
        /// ELSC save instead of overwriting the first one's. Only same-model + same-(non-empty)-plate collisions are
        /// touched, so ordinary traffic (unique plates) is never re-plated.</summary>
        private void DedupeWorldPlates()
        {
            if (Game.GameTime - _lastPlateSweep < 1500) return;
            _lastPlateSweep = Game.GameTime;
            try
            {
                var ped = Game.Player.Character;
                if (ped == null || !ped.Exists()) return;
                int curHandle = (ped.IsInVehicle() && ped.CurrentVehicle != null && ped.CurrentVehicle.Exists())
                    ? ped.CurrentVehicle.Handle : 0;

                var seen = new HashSet<string>();
                foreach (var v in World.GetNearbyVehicles(ped.Position, 120f))
                {
                    if (v == null || !v.Exists()) continue;
                    string plate = GetPlateText(v).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(plate)) continue;          // ignore blank-plate traffic
                    string key = v.Model.Hash + "|" + plate;
                    if (seen.Add(key)) continue;                        // first car with this model+plate -> keep it
                    if (v.Handle == curHandle) continue;                // player's car is handled by BindPlate

                    string p = UniquePlate();
                    Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, v, p);
                    try { Function.Call(Hash.SET_VEHICLE_WINDOW_TINT, v, 0); } catch { }   // don't inherit shared-plate tint
                    _plateClaims[p] = v.Handle;
                    seen.Add(v.Model.Hash + "|" + p);
                    Log($"[Plate] Dedupe duplicate '{plate}' on '{v.DisplayName}' -> '{p}'");
                }
            }
            catch (Exception ex) { Log($"[Plate] DedupeWorldPlates error: {ex.Message}"); }
        }

        /// <summary>True if some OTHER live, spawned vehicle currently in the world already wears this plate text.</summary>
        private bool PlateUsedBySpawnedVehicle(Vehicle self, string plateText)
        {
            string want = (plateText ?? "").Trim();
            if (string.IsNullOrEmpty(want)) return false;
            try
            {
                foreach (var v in World.GetAllVehicles())
                {
                    if (v == null || !v.Exists()) continue;
                    if (self != null && v.Handle == self.Handle) continue;
                    if (string.Equals(GetPlateText(v), want, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (Exception ex) { Log($"[Plate] PlateUsedBySpawnedVehicle failed: {ex.Message}"); }
            return false;
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

                // Reject if the plate is already used by another SAVED vehicle (window-tint check) OR by another
                // live SPAWNED vehicle currently in the world (so two cars can't share a plate -> identity collision).
                if (windowTint.PlateUsedBySavedVehicle(currentVehicle, text) || PlateUsedBySpawnedVehicle(currentVehicle, text))
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

        /// <summary>Set the plate text and migrate this car's ENTIRE saved build (and window color) so the whole
        /// build follows the rename instead of being orphaned under the old plate key.</summary>
        private void ApplyCustomPlate(string text, string oldPlate)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;

            string model = currentVehicle.DisplayName;
            // VehicleKey-style keys: empty plate -> "NOPLATE" so they line up with what VehicleKey() produces.
            string oldUp = (oldPlate ?? "").Trim().ToUpperInvariant(); if (string.IsNullOrEmpty(oldUp)) oldUp = "NOPLATE";
            string newUp = (text ?? "").Trim().ToUpperInvariant();     if (string.IsNullOrEmpty(newUp)) newUp = "NOPLATE";
            string oldFullKey = model + "|" + oldUp;
            string newFullKey = model + "|" + newUp;

            windowTint.MoveColorToPlate(model, oldPlate, text);  // keep the tint on the renamed car
            try { VehicleSaveData.MigrateVehicle(oldFullKey, newFullKey); }   // carry the WHOLE build to the new plate key
            catch (Exception ex) { Log($"[Plate] MigrateVehicle failed: {ex.Message}"); }

            Function.Call(Hash.SET_VEHICLE_NUMBER_PLATE_TEXT, currentVehicle, text);

            // Re-point the session plate-claim from the old plate to the new one so dedupe stays consistent.
            try
            {
                if (!string.IsNullOrEmpty(oldUp) && _plateClaims.TryGetValue(oldUp, out int h) && h == currentVehicle.Handle)
                    _plateClaims.Remove(oldUp);
                _plateClaims[newUp] = currentVehicle.Handle;
                _lastPlateBoundHandle = currentVehicle.Handle;   // we just set this car's plate — don't re-dedupe it
            }
            catch (Exception ex) { Log($"[Plate] claim re-point failed: {ex.Message}"); }

            VehicleSaveData.Save();
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
            if (IsTypingText) return;   // never start an edit while the player is typing into another text box

            // X button = FrontendX or Duck (depends on context)
            bool xHeld = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendX) ||
                         Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.Duck);

            if (!ModSettings.EditorModeEnabled) return;

            // Don't process X input while keyboard is open (A=save, B=cancel handled by GTA)
            if (isEditingDescription) return;

            // Some preview buttons share a physical button with the "hold X to edit description" gesture on
            // controller: L3 (horn preview = FrontendLs/Duck) and X (NOS preview = NosButton/VehicleDuck). Skip
            // the edit-description check while any of those preview buttons is held so previewing never edits.
            if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, ModSettings.HornPreviewButton) ||
                Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, (int)GTA.Control.FrontendLs) ||
                Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, ModSettings.NosButton))
            {
                lastLbHeldTime = DateTime.MinValue;   // reset the hold timer so it can't carry over
                return;
            }

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
                ApplyMeleeRestriction(false);   // on foot: restore normal weapon use
                return;
            }

            Vehicle vehicle = player.CurrentVehicle;
            if (vehicle == null || !vehicle.Exists()) return;

            // Check if vehicle changed
            if (vehicle != lastTransmissionVehicle)
            {
                lastTransmissionVehicle = vehicle;

                // Auto-enable MT only when it's the EQUIPPED transmission. MT and the vanilla transmission levels
                // are mutually exclusive: owning MT but equipping a vanilla race transmission means MT is owned-but-
                // not-equipped, and must stay OFF (checking "owned" here forced MT on over the race box on reload).
                if (VehicleSaveData.IsManualTransmissionOwned(VehicleKey(vehicle))
                    && VehicleSaveData.IsManualTransmissionEquipped(VehicleKey(vehicle)))
                {
                    elscTransmission.SetVehicle(vehicle);
                    elscTransmission.Enable();
                }
                else if (elscTransmission.IsEnabled)
                {
                    // MT not the equipped transmission on this car - disable
                    elscTransmission.Disable();
                }
            }

            // Dyno (dev tool): a pending autorun trigger force-enables MT on whatever car you're in, so the
            // runway harness can run on any spawned vehicle without buying MT first.
            if (!elscTransmission.IsEnabled && elscTransmission.DynoPending())
            {
                elscTransmission.SetVehicle(vehicle);
                elscTransmission.Enable();
            }

            // Update transmission state
            if (elscTransmission.IsEnabled)
            {
                // Per-vehicle nitrous: installed (can spray) only when this car has a tier equipped.
                int nosTier = VehicleSaveData.GetNosEquippedTier(VehicleKey(vehicle));
                elscTransmission.NosInstalled = nosTier >= 0;
                if (nosTier >= 0) elscTransmission.ActiveFxIndex = nosTier;
                // Apply live-tunable MT feel settings (cheap; lets INI edits take effect on reload).
                elscTransmission.RumbleEnabled = ModSettings.MtRumble;
                elscTransmission.ExhaustPops = ModSettings.MtExhaustPops;
                elscTransmission.ShiftKickForcePerfect = ModSettings.MtKickForce;
                elscTransmission.ShiftKickForceBase = ModSettings.MtKickForce * 0.53f;   // keep the base/perfect ratio
                elscTransmission.LimiterRpm = ModSettings.MtLimiterRpm;
                elscTransmission.NosRefillPerSec = ModSettings.MtNosRefillPerSec;
                elscTransmission.PerfectShiftNosBump = ModSettings.MtNosPerfectBump;
                elscTransmission.Update();
            }
            else
            {
                // Automatic transmission: nitrous still works. Drive the standalone NOS path (no MT pipeline).
                int nosTier = VehicleSaveData.GetNosEquippedTier(VehicleKey(vehicle));
                elscTransmission.NosInstalled = nosTier >= 0;
                if (nosTier >= 0)
                {
                    elscTransmission.ActiveFxIndex = nosTier;
                    elscTransmission.NosRefillPerSec = ModSettings.MtNosRefillPerSec;
                    elscTransmission.UpdateNosAutomatic(vehicle);
                }
            }

            // Restrict to melee (no drive-by shooting) while moving with MT. Also frees the weapon/aim controls
            // so they can't bleed into the shift buttons. Restored the moment you stop or MT is off.
            ApplyMeleeRestriction(ModSettings.MeleeOnlyInMotion && elscTransmission.IsEnabled && vehicle.Speed > 2f);

            // Speedometer HUD — always on while driving; the active style is chosen/bought in the LSC menu.
            DrawSpeedometer(vehicle);

            // NOTE: wheel fitment is updated separately in UpdateWheelFitment(), called from OnTick
            // UNCONDITIONALLY (in AND out of the LSC menu) — its per-frame re-apply and the physics
            // re-settle must run while the menu is open, which is exactly when the player is adjusting
            // height/camber. (This method only runs when !isMenuActive, so it can't host the fitment update.)
        }

        /// <summary>
        /// Build this frame's telemetry (from the manual transmission when active, else from native vehicle
        /// state) and draw the active speedometer skin. Called every tick while driving (and for preview while
        /// the Speedometer menu is open).
        /// </summary>
        private int _speedoSyncedHandle = 0;

        /// <summary>Per-car speedometer: load THIS vehicle's saved style into the live display (Speedo.Active), so
        /// the gauge only appears on cars it's equipped on instead of globally. Look/colours stay global.</summary>
        private void SyncSpeedoToCar(Vehicle v)
        {
            if (v == null || !v.Exists()) return;
            _speedoSyncedHandle = v.Handle;
            try { Speedo.Active = (SpeedoStyle)VehicleSaveData.GetSpeedoStyle(VehicleKey(v)); }
            catch { Speedo.Active = SpeedoStyle.Off; }
        }

        private void DrawSpeedometer(Vehicle vehicle)
        {
            if (Speedo.Previewing) return;   // white-screen design preview owns the HUD this frame
            if (vehicle == null || !vehicle.Exists()) return;
            // When the player changes vehicles, load that car's per-car speedo style into the live display.
            if (_speedoSyncedHandle != vehicle.Handle) SyncSpeedoToCar(vehicle);
            // Hide while the player isn't in control: LSC drive-in animations / cutscenes render in a way that
            // ghosts the digits (the old number lingers), and the gauge shouldn't show during those anyway.
            if (_lscEjectPhase > 0 || !Function.Call<bool>(Hash.IS_PLAYER_CONTROL_ON, Game.Player)) return;

            var f = new SpeedoFrame { SpeedMph = vehicle.Speed * 2.23694f };
            bool mt = elscTransmission != null && elscTransmission.IsEnabled;
            if (mt)
            {
                f.Rpm01 = elscTransmission.GetCurrentRPM();
                f.GearText = elscTransmission.InReverse ? "R" : (elscTransmission.InNeutral ? "N" : elscTransmission.CurrentGear.ToString());
                f.Redline = elscTransmission.IsInRedline();
                f.HasNos = elscTransmission.NosInstalled;
                f.Nos01 = elscTransmission.NosLevel;
                f.HasShiftPoints = true;   // mark the perfect-shift window on the RPM bar
                f.PerfectMin01 = elscTransmission.PerfectShiftMin;
                f.PerfectMax01 = elscTransmission.PerfectShiftMax;
            }
            else
            {
                f.Rpm01 = vehicle.CurrentRPM;
                int g = vehicle.CurrentGear;
                float fwd = Vector3.Dot(vehicle.Velocity, vehicle.ForwardVector);
                f.GearText = fwd < -0.5f ? "R" : (g <= 0 ? "N" : g.ToString());
                f.Redline = vehicle.CurrentRPM >= 0.95f;
                // Nitrous works on the automatic box too — show its bottle meter here as well.
                if (elscTransmission != null) { f.HasNos = elscTransmission.NosInstalled; f.Nos01 = elscTransmission.NosLevel; }
            }
            // Turbo: shown when the turbo mod is fitted; boost approximated from revs (no memory read).
            f.HasTurbo = Function.Call<bool>(Hash.IS_TOGGLE_MOD_ON, vehicle, 18);
            f.Turbo01 = f.Rpm01;

            // The simple gear/RPM (+NOS) gauge always shows once MT or Nitrous is on this vehicle — even if the
            // player left the speedometer Off (e.g. a legacy save, or NOS equipped before the auto-enable) — so
            // MT always shows gear/shift point and NOS always shows the bottle meter.
            if (Speedo.Active == SpeedoStyle.Off && Speedo.Preview == null)
            {
                if (mt || f.HasNos) Speedo.DrawSimpleForced(f);
                return;
            }
            Speedo.Draw(f);
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
                            string vehName = VehicleKey(vehicle);
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
                    // Only judge stance stability WHILE STANCING (menu open). Driving around outside the menu
                    // still re-asserts the geometry, but no longer runs the bounce/auto-recovery monitor.
                    wheelFitment.MonitorEnabled = isMenuActive;
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
        // Drive-by lock: while moving with MT, the player can't aim/fire a gun from the car (melee only).
        // Toggled only on state change so we don't spam the native every frame.
        private bool _meleeRestricted = false;
        private void ApplyMeleeRestriction(bool restrict)
        {
            if (restrict == _meleeRestricted) return;
            _meleeRestricted = restrict;
            Function.Call(Hash.SET_PLAYER_CAN_DO_DRIVE_BY, Game.Player, !restrict);
        }

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
                if (nosBindingState > 0)
                {
                    nosBindingState = 0; nosBindingItem = null;
                    ShowNotification("~y~Nitrous installed. Spray button unchanged.");
                    return;
                }
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

                    if (nosBindingState > 0)
                    {
                        ModSettings.NosButton = controlIndex;
                        ModSettings.Save();
                        FinishNosBinding();
                    }
                    else if (mtBindingState == 1)
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

        /// <summary>Complete nitrous key binding and update the NOS menu item.</summary>
        private void FinishNosBinding()
        {
            if (nosBindingItem != null)
            {
                nosBindingItem.AltTitle = "";
                nosBindingItem.Description = $"Spray: {ModSettings.NosKey}. Hold it on the gas to use nitrous.";
                itemOwnershipStatus[nosBindingItem] = STATUS_INSTALLED;
            }
            nosBindingState = 0;
            nosBindingItem = null;
            ShowNotification($"~g~Nitrous ready! Spray with {ModSettings.NosKey}.");
        }

        /// <summary>
        /// Complete the manual transmission binding and update the menu item
        /// </summary>
        private void FinishMTBinding()
        {
            mtBindingState = 0;
            mtBindingItem = null;

            if (currentVehicle != null && currentVehicle.Exists())
            {
                string vn = VehicleKey(currentVehicle);
                VehicleSaveData.SetManualTransmissionOwned(vn, true);
                bool wasEquipped = VehicleSaveData.IsManualTransmissionEquipped(vn);
                if (!wasEquipped)
                {
                    // First-time buy: equip it now (removes vanilla transmission, enables, flags, rebuilds).
                    EquipManualTransmission();
                }
                else
                {
                    // Editing keys on an already-equipped MT: just re-assert + refresh the menu labels.
                    elscTransmission.SetVehicle(currentVehicle);
                    elscTransmission.Enable();
                    VehicleSaveData.Save();
                    ShowNotification($"~g~Shift keys set — Up: {ModSettings.ShiftUpKey}, Down: {ModSettings.ShiftDownKey}");
                    CaptureMenuPosition();
                    RebuildMenusForVehicle();
                }
            }
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
        private bool _shiftUpWasDown = false;
        private bool _shiftDownWasDown = false;
        private void HandleButtonMapping()
        {
            if (!elscTransmission.IsEnabled || isMenuActive)
            {
                _shiftUpWasDown = _shiftDownWasDown = false;
                return;
            }

            int upCtl = ModSettings.ShiftUpButton;
            int dnCtl = ModSettings.ShiftDownButton;

            // Take ownership of the shift controls this frame so the GAME can neither consume them
            // (missed shifts — "doesn't catch") nor trigger them itself (phantom shifts — "shifts for me").
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, upCtl, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, dnCtl, true);

            // Mod-managed edge detection on the now-disabled control: fire exactly once per physical press,
            // every press — IS_DISABLED_CONTROL_PRESSED still reads the raw button state.
            bool upDown = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, upCtl);
            bool dnDown = Function.Call<bool>(Hash.IS_DISABLED_CONTROL_PRESSED, 0, dnCtl);
            if (upDown && !_shiftUpWasDown) elscTransmission.ShiftUp();
            if (dnDown && !_shiftDownWasDown) elscTransmission.ShiftDown();
            _shiftUpWasDown = upDown;
            _shiftDownWasDown = dnDown;
        }

        #endregion
    }
}
