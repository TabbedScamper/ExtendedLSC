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

        // ELSC Wheel Fitment (wheel-fitment adjustments)
        private WheelFitment.WheelFitment wheelFitment = new WheelFitment.WheelFitment();
        private Vehicle lastFitmentVehicle = null;

        // Per-instance plate identity (model + plate). _plateClaims maps an uppercased plate -> the vehicle Handle
        // that owns it this session, so a freshly-spawned duplicate (same model + same plate as a built car) gets
        // auto-renamed to a unique plate before ELSC reads/applies saved data — keeping it stock per-instance.
        private readonly Dictionary<string, int> _plateClaims = new Dictionary<string, int>();
        private int _lastPlateBoundHandle = 0;   // guard so EnsureUniquePlate runs once per new-car bind, not per frame
        private readonly Random _plateRng = new Random();

        // Default plates that external spawners/trainers stamp on every car they create — so a whole fleet ends up
        // sharing one plate and collides on a single ELSC identity. Treated as "no real plate" so each such car is
        // given its own unique plate. Data values (what's physically on the car), not a reference to any tool.
        private static readonly string[] PLACEHOLDER_PLATES = { "MENYOO" };
        private static bool IsPlaceholderPlate(string plate) => Array.IndexOf(PLACEHOLDER_PLATES, plate) >= 0;

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
        // in ONE handling field. Returns RAW fraction (can exceed 1 — engine swaps — for the performance class
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

        // Performance Index (0-999) from the four RAW stat fractions (uncapped so upgrades/engine
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
        // Exit-steering-hold fallback (used where the global SteeringFix EXE patch isn't active, e.g. Enhanced):
        // track the player's seated car + its live steering angle so we can pin it the moment they get out.
        private int _lastPlayerVehHandle = 0;
        private float _lastSeatedSteerDeg = 0f;
        private const float STEER_HOLD_MAX_DEG = 45f;

        // Walk-around camera
        private bool isWalkAroundActive = false;
        private Vehicle walkAroundVehicle = null;
        // Wheel-lock toggle (R3) in walk-around: LOCKED (default) = D-pad Left/Right pans the camera (wheels stay put);
        // UNLOCKED = D-pad Left/Right steers the front wheels to inspect them. Persists across re-entry within a session.
        private bool walkWheelsLocked = true;

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
        private enum UIElement { Menu, ScrollArrows, Description, StatsPanel, Banner }
        private UIElement selectedUIElement = UIElement.Menu;
        private readonly string[] uiElementNames = { "Menu", "Scroll Arrows", "Description", "Stats Panel", "Banner" };

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

        // Banner (added to the Resizer so it can be aligned per-resolution). Offset is in the
        // customBanner's CustomSprite space (~1280x720 base); scale multiplies the computed size.
        private float bannerOffsetX = 0f;
        private float bannerOffsetY = 0f;
        private float bannerScaleW = 1f;
        private float bannerScaleH = 1f;

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

            // Detect Legacy vs Enhanced FIRST — gates every build-specific memory feature per-edition (see GamePlatform).
            GamePlatform.Log = Log;
            Log($"[ELSC] Edition: {GamePlatform.EditionName}; struct-mem={GamePlatform.StructMemorySupported} ride-height={GamePlatform.RideHeightSupported} visual-wheel={GamePlatform.VisualWheelSupported} code-patch={GamePlatform.CodePatchMemorySupported}");

            // Initialize settings first (needed for logging)
            ModSettings.Log = Log;
            ModSettings.Load();

            // Initialize folder-based menu config
            MenuConfig.Log = Log;
            MenuConfig.Initialize();

            // Initialize vehicle save data (tracks purchased mods)
            VehicleSaveData.Log = Log;
            VehicleSaveData.Initialize();

            // NOTE: package files are user data and are NEVER auto-deleted on load — they're only removed via the
            // explicit in-menu Delete (X). Shared placeholder-plate collisions are handled by the plate-dedup +
            // snapshot ExcludeVehicle logic, without touching any saved package.

            // Let the window-tint manager surface player-facing messages (e.g. auto-plate on a collision).
            windowTint.Notify = ShowNotification;

            // Full per-vehicle snapshot save/restore (mods, paint, tint, wheels, …) keyed by model+plate.
            vehicleSnapshots.Log = Log;
            // Never restore a saved snapshot OVER the car the player is currently driving — its live build is the
            // truth. Fixes shared-placeholder-plate cars stamping a despawned car's parts onto the one you're driving.
            vehicleSnapshots.ExcludeVehicle = () =>
            {
                var pp = Game.Player.Character;
                return (pp != null && pp.Exists() && pp.IsInVehicle()) ? pp.CurrentVehicle : null;
            };

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

        // True while a hovered swap's SOUND is applied for preview but NOT bought (so it must be reverted on close).
        private bool swapSoundPreviewDirty = false;

        /// <summary>The swap at an engine-swap menu index (0 = Stock/null, otherwise EngineSwaps.All[index-1]).</summary>
        private EngineSwap SwapForIndex(int idx)
            => (idx <= 0 || idx - 1 >= EngineSwaps.All.Count) ? null : EngineSwaps.All[idx - 1];

        /// <summary>Preview an engine's SOUND only (FORCE_VEHICLE_ENGINE_AUDIO) on hover — no power/cost/save — so the
        /// player can rev it in walk-around and hear it. RevertEngineSwapSound() restores the installed engine on close.</summary>
        private void PreviewEngineSwapSound(EngineSwap swap)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return;
            // Already matches what's installed and nothing dirty to undo? Skip (avoids needless radio churn each hover).
            if (swap?.Id == activeEngineSwap?.Id && !swapSoundPreviewDirty) return;

            string preStation = Function.Call<string>(Hash.GET_PLAYER_RADIO_STATION_NAME);
            if (swap == null)
            {
                string spawn = GetVehicleSpawnName(currentVehicle);
                if (!string.IsNullOrEmpty(spawn))
                    Function.Call((Hash)5695999342423706424L, currentVehicle, spawn); // stock engine sound
            }
            else
            {
                Function.Call((Hash)5695999342423706424L, currentVehicle, swap.AudioName);
            }
            // Dirty whenever the previewed sound differs from the actually-installed engine (so close knows to revert).
            swapSoundPreviewDirty = swap?.Id != activeEngineSwap?.Id;

            // FORCE_VEHICLE_ENGINE_AUDIO hijacks the radio — re-assert the player's station (per-frame, see OnTick).
            radioRestoreStation = preStation;
            radioRestoreUntil = Game.GameTime + 500;
            RestoreRadioStation();
        }

        /// <summary>Restore the SOUND of the actually-installed engine after a hover preview (no power/cost change).</summary>
        private void RevertEngineSwapSound()
        {
            if (!swapSoundPreviewDirty) return;
            swapSoundPreviewDirty = false;
            if (currentVehicle == null || !currentVehicle.Exists()) return;

            string preStation = Function.Call<string>(Hash.GET_PLAYER_RADIO_STATION_NAME);
            if (activeEngineSwap == null)
            {
                string spawn = GetVehicleSpawnName(currentVehicle);
                if (!string.IsNullOrEmpty(spawn))
                    Function.Call((Hash)5695999342423706424L, currentVehicle, spawn);
            }
            else
            {
                Function.Call((Hash)5695999342423706424L, currentVehicle, activeEngineSwap.AudioName);
            }
            radioRestoreStation = preStation;
            radioRestoreUntil = Game.GameTime + 500;
            RestoreRadioStation();
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
            menu.SelectedIndexChanged += (s, e) =>
            {
                UpdateSwapPreview(e.Index);
                // Swap the SOUND to the hovered engine so it can be revved+heard in walk-around (sound only — no buy).
                PreviewEngineSwapSound(SwapForIndex(e.Index));
            };

            // Leaving the menu without buying: put the installed engine's sound back.
            menu.Closed += (s, e) => { isPreviewingSwap = false; RevertEngineSwapSound(); };

            // Stock (index 0) — revert
            var stockItem = new NativeItem("Stock Engine");
            stockItem.Description = "Factory engine and sound. Reverts any swap.";
            stockItem.Activated += (s, e) =>
            {
                if (EditBlockApply()) return;
                isPreviewingSwap = false;
                ApplyEngineSwap(null);
                swapSoundPreviewDirty = false;   // committed: this sound IS the installed engine now
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
                    swapSoundPreviewDirty = false;   // committed: this sound IS the installed engine now
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
                    else elscTransmission.ApplyGeometricGearing(currentVehicle, (int)s[7]);            // automatic: extend range from stock
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
            // performance parts you've installed — progression style — so a stock car is barely tunable and a fully
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

        // Cache of resolved wheel design NAMES per (model, wheelType). Reading them requires SET_VEHICLE_WHEEL_TYPE
        // (resets render size + restreams rims), so doing it for all ~12 types on every rebuild caused the menu-open
        // shrink + a ~200ms freeze. Static per model -> read once. Reads labels DIRECTLY (they're type-dependent and
        // only resolve while that type is set, which it is here).
        private readonly Dictionary<long, string[]> _wheelTypeCache = new Dictionary<long, string[]>();
        private string[] WheelTypeDesigns(int wheelType, int origType, int origFront, int origBack, bool origCustomTires)
        {
            if (currentVehicle == null || !currentVehicle.Exists()) return new string[0];
            long key = ((long)(uint)currentVehicle.Model.Hash << 8) | (uint)(wheelType & 0xFF);
            if (_wheelTypeCache.TryGetValue(key, out var cached)) return cached;

            float origVisSize = WheelMemory.GetVisualSize(currentVehicle);
            float origVisWidth = WheelMemory.GetVisualWidth(currentVehicle);
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, wheelType);
            int count = Function.Call<int>(Hash.GET_NUM_VEHICLE_MODS, currentVehicle, 23);
            var names = new string[count > 0 ? count : 0];
            for (int i = 0; i < count; i++)
            {
                string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, currentVehicle, 23, i);
                string name = (!string.IsNullOrEmpty(label) && label != "NULL") ? Game.GetLocalizedString(label) : null;
                names[i] = string.IsNullOrEmpty(name) ? $"Wheel {i + 1}" : name;
            }
            Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, currentVehicle, origType);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 23, origFront, origCustomTires);
            Function.Call(Hash.SET_VEHICLE_MOD, currentVehicle, 24, origBack, origCustomTires);
            if (origVisSize > 0.1f && origVisSize < 5f) WheelMemory.SetVisualSize(currentVehicle, origVisSize);
            if (origVisWidth > 0.1f && origVisWidth < 5f) WheelMemory.SetVisualWidth(currentVehicle, origVisWidth);
            _wheelTypeCache[key] = names;
            return names;
        }

        /// <summary>Re-install the current wheels ONCE (type toggle + restore) so the live wheel geometry resets to
        /// NATURAL and the fitment re-derives a clean baseline. This is the stabilising half of the old per-open
        /// wheel juggle — applying a stance onto already-scaled wheels otherwise double-stacks and the suspension
        /// oscillates. Called once per package apply instead of 12x on every menu open (which caused the shrink).</summary>
        private void ReseatWheelsForFitment()
        {
            try
            {
                var v = currentVehicle;
                if (v == null || !v.Exists()) return;
                int curType = Function.Call<int>(Hash.GET_VEHICLE_WHEEL_TYPE, v);
                int curFront = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, 23);
                int curBack = Function.Call<int>(Hash.GET_VEHICLE_MOD, v, 24);
                bool ct = Function.Call<bool>(Hash.GET_VEHICLE_MOD_VARIATION, v, 23);
                float vs = WheelMemory.GetVisualSize(v);
                float vw = WheelMemory.GetVisualWidth(v);

                Function.Call(Hash.SET_VEHICLE_MOD_KIT, v, 0);
                int tmp = curType == 0 ? 1 : 0;                                   // toggle the type to force a re-install
                Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, v, tmp);
                Function.Call(Hash.SET_VEHICLE_WHEEL_TYPE, v, curType);
                Function.Call(Hash.SET_VEHICLE_MOD, v, 23, curFront, ct);
                Function.Call(Hash.SET_VEHICLE_MOD, v, 24, curBack, ct);
                if (vs > 0.1f && vs < 5f) WheelMemory.SetVisualSize(v, vs);
                if (vw > 0.1f && vw < 5f) WheelMemory.SetVisualWidth(v, vw);
            }
            catch (Exception ex) { Log($"[Fitment] ReseatWheelsForFitment: {ex.Message}"); }
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
            // Wheel design NAMES are cached per (model, wheelType). Reading them needs SET_VEHICLE_WHEEL_TYPE which
            // resets the render size (the menu-open "shrink") + restreams the rims (~200ms). Cached -> the juggle
            // runs once per model+type, so re-opening the menu neither shrinks the wheels nor lags. The stance-
            // stabilising wheel RE-SEAT is done separately, once per package apply (ReseatWheelsForFitment).
            string[] wheelNames = WheelTypeDesigns(wheelType, origType, origFrontMod, origBackMod, origCustomTires);
            int count = wheelNames.Length;
            if (count <= 0) return null;

            var menu = CreateMenu(ModPricing.GetWheelTypeName(wheelType));
            wheelInspectMenus.Add(menu);   // browsing rims here → D-pad Left/Right inspect-steers the wheels
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
                    string name = CachedModLabel(48, i);
                    if (!string.IsNullOrEmpty(name)) liveryName = name;
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
                        // Preview using direct color set
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
            var styles = new[] { SpeedoStyle.Off, SpeedoStyle.Simple, SpeedoStyle.Arcade };
            var refreshers = new List<Action>();

            // The editor (colors + units + size/position) opens by SELECTING the equipped gauge — no separate
            // "Customize" category. Built once here, shown from the equipped style's Activated handler.
            var custMenu = CreateSpeedoCustomizationMenu();
            custMenu.Closed += (s, e) => { if (!isNavigatingMenu) { menu.Visible = true; Speedo.Preview = Speedo.Active; } };

            foreach (var stEach in styles)
            {
                SpeedoStyle st = stEach;
                int price = st == SpeedoStyle.Arcade ? ModPricing.SpeedoArcadePrice : 0;
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
            NativeListItem<string> AddColor(string label, string desc, Func<Color> get, Action<Color> set)
            {
                var item = new NativeListItem<string>(label, palNames) { SelectedIndex = Nearest(get()) };
                item.Description = desc;
                item.ItemChanged += (s, e) => { set(palVals[e.Index]); Speedo.SaveConfig(); };
                colorRefreshers.Add(() => item.SelectedIndex = Nearest(get()));
                NoWrap(item); menu.Add(item);
                return item;
            }
            AddColor("Accent Color", "RPM bar fill / needle.",     () => Cur().Accent,  c => Cur().Accent = c);
            AddColor("Speed Color",  "The speed number.",          () => Cur().Speed,   c => Cur().Speed = c);
            AddColor("Units Color",  "The MPH / KM·H label.",      () => Cur().Unit,    c => Cur().Unit = c);
            AddColor("Gear Color",   "The gear number.",           () => Cur().Gear,    c => Cur().Gear = c);
            AddColor("NOS Color",    "The nitrous bar.",           () => Cur().Nos,     c => Cur().Nos = c);
            var redlineItem = AddColor("Redline Color","Redline zone / warning.",    () => Cur().Redline, c => Cur().Redline = c);
            // FASTandSPEEDY dial fills (Color 1 = big circle, Color 2 = small circle) + brightness sliders.
            // These four are Arcade/FASTandSPEEDY-only — hidden for Simple on Shown (Simple draws no dial circles).
            var bigCircle = AddColor("Big Circle (Color 1)",   "FASTandSPEEDY main dial fill.",  () => Cur().Circle1, c => Cur().Circle1 = c);
            var smallCircle = AddColor("Small Circle (Color 2)", "FASTandSPEEDY turbo dial fill.", () => Cur().Circle2, c => Cur().Circle2 = c);

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

            // The four circle options apply ONLY to FASTandSPEEDY (Arcade). On open, remove them for any non-Arcade
            // style and re-insert them (right after Redline) for Arcade — so each speedo lists only its own options.
            var circleItems = new List<NativeItem> { bigCircle, smallCircle, c1b, c2b };

            // When the editor opens, sync every picker to the (now-active) style's palette + the unit toggle.
            menu.Shown += (s, e) =>
            {
                units.AltTitle = Speedo.Mph ? "MPH" : "KM/H";
                foreach (var ci in circleItems) if (menu.Items.Contains(ci)) menu.Remove(ci);
                if (Speedo.Active == SpeedoStyle.Arcade)
                {
                    int at = menu.Items.IndexOf(redlineItem) + 1;
                    for (int i = 0; i < circleItems.Count; i++) menu.Add(at + i, circleItems[i]);
                }
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

        // Resolved mod names (GET_MOD_TEXT_LABEL + localize) are STATIC per vehicle MODEL, but were re-read via two
        // natives for every item on EVERY menu rebuild — the bulk of BuildGroupedMainMenu's ~300ms. Cache by
        // (model, slot, value); persists across rebuilds + cars (never invalidated, since labels don't change).
        private readonly Dictionary<long, string> _modLabelCache = new Dictionary<long, string>();
        private string CachedModLabel(int modIndex, int valueIndex)
        {
            var v = currentVehicle;
            if (v == null || !v.Exists()) return "";
            long key = ((long)(uint)v.Model.Hash << 20) | ((long)(modIndex & 0xFF) << 12) | ((long)(valueIndex & 0xFFF));
            if (_modLabelCache.TryGetValue(key, out var hit)) return hit;
            string label = Function.Call<string>(Hash.GET_MOD_TEXT_LABEL, v, modIndex, valueIndex);
            string name = (!string.IsNullOrEmpty(label) && label != "NULL") ? Game.GetLocalizedString(label) : null;
            name = name ?? "";
            _modLabelCache[key] = name;
            return name;
        }

        private string GetModNameByIndex(int modIndex, int valueIndex)
        {
            string name = CachedModLabel(modIndex, valueIndex);
            if (!string.IsNullOrEmpty(name)) return name;
            return $"{ModCategories.GetDisplayName(modIndex)} {valueIndex + 1}";
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
            // 2) Spawned duplicates (e.g. several cars all sharing one placeholder plate) -> give the extras unique plates.
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
                // A text box is open. We return before UpdateIdleCinematic below, so: (1) tear down any running idle
                // cinematic now — otherwise its camera + hidden HUD would linger behind the keyboard; (2) keep the
                // idle timer fresh so the cinematic can't fire the instant the box closes.
                if (_idlePhase != 0) StopIdleCinematic(true);
                _lastInteractionTime = Game.GameTime;
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
                        case UIElement.Banner:
                            if (menuPositionUniformScale)
                            {
                                bannerScaleW += scaleChange;
                                bannerScaleH += scaleChange;
                            }
                            else
                            {
                                if (up) bannerScaleH -= scaleStep;
                                if (down) bannerScaleH += scaleStep;
                                if (left) bannerScaleW -= scaleStep;
                                if (right) bannerScaleW += scaleStep;
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
                        case UIElement.Banner:
                            // Banner offset is in CustomSprite (~1280x720) space -> use the pixel-based menuStep.
                            if (up) bannerOffsetY -= menuStep;
                            if (down) bannerOffsetY += menuStep;
                            if (left) bannerOffsetX -= menuStep;
                            if (right) bannerOffsetX += menuStep;
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

            // Wheel-lock fallback for editions where the global SteeringFix EXE patch isn't active (Enhanced): when the
            // player EXITS a car whose wheels are turned, pin its angle so the wheels don't snap back. We track the
            // live steering angle WHILE seated (auto-center hasn't started yet), then hand it to the hold on exit.
            // On Legacy the EXE patch already does this engine-wide, so this is skipped (SteeringFix.Applied == true).
            if (ModSettings.KeepSteeringAngle && !SteeringFix.Applied)
            {
                try
                {
                    var pv = Game.Player.Character != null ? Game.Player.Character.CurrentVehicle : null;
                    if (pv != null && pv.Exists())
                    {
                        _lastPlayerVehHandle = pv.Handle;
                        _lastSeatedSteerDeg = pv.SteeringAngle;   // remember the live driven angle
                    }
                    else if (_lastPlayerVehHandle != 0)
                    {
                        // Just stepped out — pin the car at its turned angle (driverless auto-center starts now). Use
                        // the larger of the live exit angle and the last seated angle (in case it already began decaying).
                        if (_steerHoldHandle == 0)
                        {
                            var exited = (Vehicle)Entity.FromHandle(_lastPlayerVehHandle);
                            if (exited != null && exited.Exists())
                            {
                                float ang = exited.SteeringAngle;
                                if (Math.Abs(_lastSeatedSteerDeg) > Math.Abs(ang)) ang = _lastSeatedSteerDeg;
                                if (Math.Abs(ang) > 1.5f) { _steerHoldHandle = _lastPlayerVehHandle; _heldSteerDeg = ang; }
                            }
                        }
                        _lastPlayerVehHandle = 0;
                    }
                }
                catch { }
            }

            // Hold an inspected car's wheels at the chosen angle (in AND out of the menu, incl. after the player
            // leaves it parked in free roam). Released the moment it's actually driven.
            UpdateSteeringHold();

            // Auto-apply saved stances to specific cars within range (skips the player's current car).
            // Guarded like fitment: on error, disable for the session rather than crash the script.
            if (_stanceMgrInit && ModSettings.AutoApplyStances)
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

                // Turn the wheels with D-pad Left/Right in the normal menu too (not just walk-around) — but ONLY while
                // browsing wheels (per-type lists / custom wheel categories). Everywhere else Left/Right adjusts a value
                // (suspension, tuning, paint, lists), so steering there would twitch the wheels annoyingly. Also stand
                // down on any list/slider item as a belt-and-braces guard.
                if (isMenuActive && !isWalkAroundActive)
                {
                    var sm = GetVisibleMenu();
                    if (sm != null && wheelInspectMenus.Contains(sm) && !ItemUsesLeftRight(sm.SelectedItem))
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

                // Same gate as the F5 key: only at an LSC, unless OpenAnywhere is on.
                if (rbHeld && bPressed && (isInLSC || ModSettings.OpenAnywhere))
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

            // F5 manual open: ONLY at an actual LSC (isInLSC). OpenAnywhere is handled earlier and returns, so it
            // bypasses this gate. Without the isInLSC check, the menu key opened in any vehicle anywhere, which made
            // OpenAnywhere=false meaningless (the reported "opened outside LSC" bug).
            if (e.KeyCode == menuKey && !isMenuActive && isInLSC)
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
        // Customs shop (vanilla or a modded one that uses it). It is NOT running during external spooners / trainers, so
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
                    // MOD-CONFLICT GUARD: "in a stopped car with control off" also matches external spooner/trainer modes (and
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


    }
}
