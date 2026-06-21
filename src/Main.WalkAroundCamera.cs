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

                ShowNotification("~b~Camera Mode~w~\nLB/RB - Cycle Parts | X - Open/Close Doors\nLS/RS - Free Roam | R3 - Wheel Lock | Y - Exit");
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

            // R3 toggles the wheel lock. UNLOCKED = D-pad Left/Right steers the front wheels to inspect them (held by
            // the global SteeringFix patch so it doesn't snap back on exit). LOCKED (default) = D-pad Left/Right does
            // nothing, so the wheels never twitch while you move around. R3 is free here (its only other use, debug
            // game-input mode, is gated behind an active debug mode).
            if (Function.Call<bool>(Hash.IS_DISABLED_CONTROL_JUST_PRESSED, 0, (int)GTA.Control.FrontendRs))
            {
                walkWheelsLocked = !walkWheelsLocked;
                ShowNotification(walkWheelsLocked
                    ? "~b~Wheels Locked~w~ — press R3 to turn the wheels"
                    : "~g~Wheels Unlocked~w~ — Left/Right turns the wheels");
            }

            if (!walkWheelsLocked)
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
                && currentVehicle != null && currentVehicle.Exists() && !IsTypingText
                && activeDebugMode == DebugMode.None;   // never run while the debug menu/resizer is open

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
            Vector3 desired = new Vector3(world.X, world.Y, currentVehicle.Position.Z + _idleShotHeight);
            return ClampCamInsideRoom(desired);
        }

        /// <summary>Stops the orbit camera from punching through walls/ceilings in tight LSC bays: probe from the
        /// look-target out to the desired camera spot and, on a world-geometry hit, pull the camera just inside the
        /// surface. No hit (open/large space) -> the desired position is used unchanged. One LOS probe per frame.</summary>
        private Vector3 ClampCamInsideRoom(Vector3 desired)
        {
            Vector3 from = _idleCenter;                       // inside the room, at the car
            Vector3 delta = desired - from;
            float dist = delta.Length();
            if (dist < 0.1f) return desired;
            try
            {
                // Map only: static world (walls/roof/floor) — ignores peds/vehicles/props so a stray object near the
                // car can't yank the camera in. Ignore the car itself so the ray reaches the wall, not the bodywork.
                RaycastResult r = World.Raycast(from, desired, IntersectFlags.Map, currentVehicle);
                if (r.DidHit)
                {
                    Vector3 unit = delta / dist;
                    float clamped = (r.HitPosition - from).Length() - 0.35f;   // 0.35m off the surface (lens clearance)
                    if (clamped < 0.5f) clamped = 0.5f;                        // never collapse onto the car
                    if (clamped > dist) clamped = dist;
                    return from + unit * clamped;
                }
            }
            catch { }
            return desired;
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
    }
}
