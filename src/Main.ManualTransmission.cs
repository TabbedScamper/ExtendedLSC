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
    public partial class Main : Script
    {
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
            if (!(ModSettings.AutoApplyStances && _fitmentInitAllowed)) return;
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
