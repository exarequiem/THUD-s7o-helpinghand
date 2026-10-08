using System;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SharpDX.DirectInput;
using Turbo.Plugins.Default;

namespace Turbo.Plugins.s7o
{
    // v1.5.0: acknowledge cursor restore without treating a stale native sample as failure.
    // Optional Sanctified Shadow Strafe module.
    // Sanctified Strafe auto-casts the last non-channeled Hatred spender, so Impale
    // is only primed/re-primed while the manual cadence belongs to a Hatred generator.
    // F3 ownership is Shadow 6-piece only and remains independent of DHStrafe state.
    // Speed uses a sparse generator cadence for travel; Combat uses the fast GoD-style generator cadence.
    public class s7o_Impale : BasePlugin, IKeyEventHandler, IAfterCollectHandler, IInGameTopPainter, INewAreaHandler, IS7oAutoLootInputHandoff
    {
        private const uint ShadowSetSno = 916931u;
        private const uint GoDSetSno = 791249u;
        private const uint StrafeSno = 134030u;
        private const uint ImpaleSno = 131366u;
        private const uint MultishotSno = 77649u; // Disparo Múltiple
        private const uint RapidFireSno = 131192u;
        private const uint BolasSno = 77552u;
        private const uint GrenadesSno = 86610u;
        private const uint HungeringArrowSno = 129215u;
        private const uint EntanglingShotSno = 361936u;
        private const uint EvasiveFireSno = 377450u;
        private const uint UESetSno = 791334u; // Unhallowed Essence

        private enum DHBuild { None, ShadowImpale, UEMultishot }
        private DHBuild _detectedBuild = DHBuild.None;
        private uint _spenderSno = 0;
        private string _ownerName = "StrafeMacro";
        private const int PrimeVerifyMs = 450;
        private const int PrimeRetryMs = 250;
        private const int PrimeBackoffMs = 1000;

        public Key ToggleHotkey = Key.F3;
        public Key ModeHotkey = Key.F2;
        public bool RequireSet = true; // Valida dinámicamente las 6 piezas de la build activa
        public bool AutoAimGenerator = true; // Auto-aim genérico para generadores a distancia
        public int SpeedGeneratorIntervalMs = 2000;
        public int CombatGeneratorIntervalMs = 140;
        public int SkillPulseHoldMs = 8;
        public int AutoLootPauseMs = 300; // Same bounded pickup pause as DHStrafe.
        public bool PauseNearUnoperatedPylon = true;
        public float PylonPauseRange = 15f;
        public bool PauseNearPortal = true;
        public float PortalPauseRange = 15f;
        public int PortalArrivalEscapeMinMs = 2000; // Arrival arming window, not a movement delay.
        public bool ShowStatusText = true;
        public float StatusTextCenterXFrac = 0.50f;
        public float StatusTextYFrac = 0.58f;
        public float StatusTextYOffsetPx = 1.0f;

        private IKeyEvent _toggleEvent;
        private IKeyEvent _modeEvent;
        private bool _running;
        private bool _combat;
        private bool _needsSpenderPrime;
        private bool _primePending;
        private int _primeStartedTick;
        private int _nextPrimeTick;
        private int _primeAttempts;
        private int _interactionPauseUntilTick;
        private int _autoLootPauseUntilTick;
        private int _autoLootHandoffSerial;
        private IUiElement _chatEditLine;
        private IUiElement _urshiGemPane;
        private ActionKey _heldStrafe = ActionKey.Unknown;
        private ActionKey _pulse = ActionKey.Unknown;
        private ushort _ownedStandstill;
        private int _pulseReleaseTick;
        private int _nextGeneratorTick;
        private string _settingsPath;
        private s7o_HUD_MENU _hudMenu;
        private readonly List<IUiElement> _leftClickUiElements = new List<IUiElement>();
        private readonly List<RectangleF> _leftClickUiRects = new List<RectangleF>();
        private bool _leftClickUiReadable;
        private bool _pylonPauseActive, _portalPauseActive, _portalArrivalEscapeActive;
        private string _inputPauseReason = "idle";
        private bool _areaResumePending;
        private uint _trackedPortalWorldId, _trackedPortalAnnId, _trackedPortalAcdId;
        private float _trackedPortalX, _trackedPortalY;
        private int _trackedPortalLastSeenTick = int.MinValue;
        private int _trackedPortalArrivalTick = int.MinValue;
        private bool _trackedPortalClearedRange, _trackedPortalArmed;
        private const int PortalIdentityRetentionMs = 750;
        // ZDH Helper's click mask, uniformly scaled and anchored at the same edge/center.
        private static readonly RectangleF[] ClickGuardRects1920x1080 = {
            new RectangleF(116f, 11f, 76f, 71f),
            new RectangleF(34f, 57f, 58f, 61f),
            new RectangleF(871f, 2f, 179f, 21f),
            new RectangleF(1644f, 23f, 60f, 26f),
            new RectangleF(1816f, 120f, 25f, 15f),
            new RectangleF(1863f, 363f, 31f, 29f),
            new RectangleF(8f, 973f, 85f, 80f),
            new RectangleF(315f, 893f, 1289f, 187f),
            new RectangleF(1754f, 961f, 157f, 83f),
            new RectangleF(0f, 0f, 245f, 155f) // Pestilence's follower/portrait-side guard.
        };
        private enum AimStage { Idle, Lease, Aim, Hold, PostInputSettle, Restore, RestoreSettle }
        private const int AimPreviewMs = 31, AimHoldMs = 35, AimPostInputMs = 24;
        private const int AimMinimumLeaseMs = 105, AimPauseAckMs = 80;
        private const int AimPreInputLimitMs = 200, AimPostInputLimitMs = 320;
        private AimStage _generatorAimStage;
        private int _genAimStartedTick, _genDueTick, _genInputTick, _genResumeTick;
        private int _genTransactionSerial;
        private uint _genAimActor;
        private int _genSavedX, _genSavedY, _genAimX, _genAimY;
        private int _genRestoreX, _genRestoreY, _genReferenceX, _genReferenceY;
        private int _genDeltaX, _genDeltaY, _genSyntheticFromX, _genSyntheticFromY;
        private bool _genCursorOwned, _genSyntheticPending, _genRestorePrepared;
        private bool _genInputSent, _genSawCastAnimation, _genRestoreWriteSent;
        private bool _genRestoreConfirmed, _genRestoreRescueAttempted;
        private int _genRestoreStartedTick = int.MinValue, _genRestoreWriteGameTick = int.MinValue;
        private bool _genRestoreDesktopConfirmed, _genRestoreAwaitingNative;
        private AnimSnoEnum _genPreInputAnimation;
        private string _genEndReason = "idle";

        private static readonly ActionKey[] MouseUiActions = {
            ActionKey.LeftSkill, ActionKey.RightSkill, ActionKey.Skill1, ActionKey.Skill2,
            ActionKey.Skill3, ActionKey.Skill4, ActionKey.Heal, ActionKey.TownPortal,
            ActionKey.Inventory, ActionKey.SkillsWindow, ActionKey.ParagonWindow,
            ActionKey.Map, ActionKey.WaypointMap, ActionKey.Social, ActionKey.Close
        };
        private IFont _statusFont;
        private IFont _runningFont;
        private IFont _combatFont;

        public s7o_Impale()
        {
            Enabled = true;
            Order = 21010;
        }

        public override void Load(IController hud)
        {
            base.Load(hud);
            s7o_ImpaleInputContext.Hud = hud;
            s7o_ImpaleInputContext.CanPressLeftSkill = IsLeftClickSafe;
            s7o_ImpaleInputContext.CanPressLeftSkillAt = IsLeftClickDesktopPointSafe;
            foreach (var key in MouseUiActions)
                try
                {
                    var ui = Hud.Render.GetPlayerSkillUiElement(key);
                    if (ui != null && !_leftClickUiElements.Contains(ui)) _leftClickUiElements.Add(ui);
                }
                catch { }
            try { AddClickUi(Hud.Render.ParagonLevelUpSplashTextUiElement); } catch { }
            try { if (Hud.Inventory != null) AddClickUi(Hud.Inventory.FollowerMainUiElement); } catch { }
            foreach (string path in new[] {
                "Root.NormalLayer.SkillPane_main.LayoutRoot.SkillsList",
                "Root.NormalLayer.Paragon_main.LayoutRoot.ParagonPointSelect",
                "Root.NormalLayer.game_notify_dialog_backgroundScreen.dialog_new_paragon_button",
                "Root.NormalLayer.BattleNetProfile_main.LayoutRoot.OverlayContainer",
                "Root.NormalLayer.BattleNetLeaderboard_main.LayoutRoot.OverlayContainer",
                "Root.NormalLayer.BattleNetAchievements_main.LayoutRoot.OverlayContainer",
                "Root.NormalLayer.BattleNetStore_main.LayoutRoot.OverlayContainer",
                "Root.NormalLayer.Guild_main.LayoutRoot.OverlayContainer",
                "Root.NormalLayer.gamemenu_dialog.gamemenu_bkgrnd",
                "Root.NormalLayer.conversation_dialog_main",
                "Root.TopLayer.follower_swap",
                "Root.TopLayer.BattleNetSocialDialogs_main.LayoutRoot.DialogWriteNote"
            })
                try
                {
                    var ui = Hud.Render.RegisterUiElement(path, null, null);
                    if (ui != null && !_leftClickUiElements.Contains(ui)) _leftClickUiElements.Add(ui);
                }
                catch { }
            _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "plugins", "s7o", "settings", "s7o_Impale.ini");
            try
            {
                if (File.Exists(_settingsPath))
                    foreach (string line in File.ReadAllLines(_settingsPath))
                        if (line.StartsWith("AutoAimGenerator=", StringComparison.OrdinalIgnoreCase))
                        {
                            bool value;
                            if (bool.TryParse(line.Substring(18), out value)) AutoAimGenerator = value;
                        }
            }
            catch { }
            _chatEditLine = Hud.Render.RegisterUiElement(
                "Root.NormalLayer.chatentry_dialog_backgroundScreen.chatentry_content.chat_editline", null, null);
            _urshiGemPane = Hud.Render.RegisterUiElement(
                "Root.NormalLayer.vendor_dialog_mainPage.riftReward_dialog.LayoutRoot.gemUpgradePane", null, null);
            _toggleEvent = Hud.Input.CreateKeyEvent(true, ToggleHotkey, false, false, false);
            _modeEvent = Hud.Input.CreateKeyEvent(true, ModeHotkey, false, false, false);
            _statusFont = Hud.Render.CreateFont("tahoma", 8, 255, 220, 190, 80, true, false, 255, 0, 0, 0, true);
            _runningFont = Hud.Render.CreateFont("tahoma", 8, 255, 80, 255, 120, true, false, 255, 0, 0, 0, true);
            _combatFont = Hud.Render.CreateFont("tahoma", 8, 255, 255, 80, 80, true, false, 255, 0, 0, 0, true);
        }

        public void OnNewArea(bool newGame, ISnoArea area)
        {
            if (newGame || (area != null && area.IsTown))
            { Stop(); return; }

            // Match DHStrafe's floor handoff: release our old inputs/cursor, but keep
            // F3 and F2 mode armed. Re-acquire from fresh skills after loading ends.
            int now = Environment.TickCount;
            CancelPulse("new area");
            ReleaseStrafe();
            ResetPortalApproachState();
            _pylonPauseActive = false;
            _primePending = false;
            _primeAttempts = 0;
            _primeStartedTick = 0;
            _nextPrimeTick = _nextGeneratorTick = now;
            _interactionPauseUntilTick = _autoLootPauseUntilTick = now;
            _areaResumePending = _running && area != null;
            _inputPauseReason = _running ? "world transition" : "idle";
        }
        public void ForceStopForDisable() { Stop(); }

        // AutoLoot calls this before saving or moving its own cursor. Return our
        // aim first, release only our inputs, and leave F3 armed for automatic resume.
        public void PauseForAutoLootPickup()
        {
            if (!Enabled || !_running) return;
            int now = Environment.TickCount;
            _autoLootPauseUntilTick = unchecked(now + Math.Max(50, Math.Min(1000, AutoLootPauseMs)));
            _autoLootHandoffSerial = unchecked(_autoLootHandoffSerial + 1);
            _inputPauseReason = "AutoLoot pickup";
            CancelPulse("AutoLoot pickup");
            ReleaseStrafe();
            _primePending = false;
        }

        public void StopForAutoLootUrshiHandoff() { Stop(); }

        public void OnKeyEvent(IKeyEvent keyEvent)
        {
            if (!Enabled || keyEvent == null || !keyEvent.IsPressed) return;

            if (_toggleEvent != null && _toggleEvent.Matches(keyEvent))
            {
                if (_running)
                {
                    Stop();
                    return;
                }

                IPlayerSkill strafe;
                IPlayerSkill spender;
                IPlayerSkill generator;
                if (!CanRun(out strafe, out spender, out generator)) return;

                _running = true;
                _combat = false;
                _needsSpenderPrime = true;
                _nextGeneratorTick = Environment.TickCount;
                _nextPrimeTick = _nextGeneratorTick;
                _interactionPauseUntilTick = _nextGeneratorTick;
                _autoLootPauseUntilTick = _nextGeneratorTick;
                return;
            }

            if (_running && _modeEvent != null && _modeEvent.Matches(keyEvent)
                && Hud.Game != null && !Hud.Game.IsInTown && !InputUiBlocked())
            {
                _combat = !_combat;
                _nextGeneratorTick = Environment.TickCount;
            }
        }

        public void AfterCollect()
        {
            int now = Environment.TickCount;
            if (Hud != null && Hud.Window != null && Hud.Window.IsForeground)
            {
                s7o_InputReleaseArbiter.RetryPending(now);
                
                // RECOLECTOR DE BASURA: Si no hay un pulso autorizado activo, pero Windows
                // reporta que una tecla de habilidad (1, 2, 3 o 4) quedó pegada, forzamos su liberación.
                if (_pulse == ActionKey.Unknown)
                {
                    if (s7o_ImpaleInput.IsDown(ActionKey.Skill1)) s7o_ImpaleInput.Up(_ownerName, ActionKey.Skill1);
                    if (s7o_ImpaleInput.IsDown(ActionKey.Skill2)) s7o_ImpaleInput.Up(_ownerName, ActionKey.Skill2);
                    if (s7o_ImpaleInput.IsDown(ActionKey.Skill3)) s7o_ImpaleInput.Up(_ownerName, ActionKey.Skill3);
                    if (s7o_ImpaleInput.IsDown(ActionKey.Skill4)) s7o_ImpaleInput.Up(_ownerName, ActionKey.Skill4);
                }
            }
            if (_generatorAimStage == AimStage.Idle && _genCursorOwned)
            {
                if (RestoreGeneratorCursor()) ClearGeneratorCursor();
                return;
            }
            RefreshClickUiRects();
            UpdatePortalInteractionState(now);
            _pylonPauseActive = PauseNearUnoperatedPylon && IsUnoperatedPylonNearby(PylonPauseRange);
            ObserveGeneratorAnimation();
            FinishPulse(now);
            if (!_running) return;

            // FreeHUD can publish no hero or an empty skill snapshot before OnNewArea.
            // These are pauses, not a request to disarm F3. Never inject through them.
            if (Enabled && IsTransitionSnapshot())
            {
                _inputPauseReason = "world transition";
                CancelPulse(_inputPauseReason);
                ReleaseStrafe();
                _primePending = false;
                return;
            }

            IPlayerSkill strafe;
            IPlayerSkill spender;
            IPlayerSkill generator;
            if (!Enabled || !CanRun(out strafe, out spender, out generator))
            {
                Stop();
                return;
            }

            _areaResumePending = false;

            // Never re-acquire Strafe, standstill or generator input during a pickup.
            if (!Due(now, _autoLootPauseUntilTick))
            { _inputPauseReason = "AutoLoot pickup"; return; }

            // Match DHStrafe: interaction zones take precedence over queued casts.
            // An item hover still does not request a pickup or an interaction pause.
            bool hoveredInteraction = IsHoveringUrshi() || IsHoveringInteractable();
            if (hoveredInteraction) _interactionPauseUntilTick = unchecked(now + 300);
            if (_pylonPauseActive || _portalPauseActive || !Due(now, _interactionPauseUntilTick))
            {
                _inputPauseReason = _pylonPauseActive ? "pylon nearby"
                    : _portalPauseActive ? "portal nearby" : "hovered interaction";
                CancelPulse(_inputPauseReason);
                ReleaseStrafe();
                _primePending = false;
                return;
            }

            // A newly arrived/start-inside portal is movement-only until it has been
            // cleared once. Do not stop the hero on the entrance or aim Bolas there.
            if (_portalArrivalEscapeActive)
            {
                _inputPauseReason = "portal arrival movement";
                CancelPulse(_inputPauseReason);
                _primePending = false;
                if (_heldStrafe == ActionKey.Unknown && s7o_ImpaleInput.Down(_ownerName, strafe.Key))
                    _heldStrafe = strafe.Key;
                return;
            }

            _inputPauseReason = "running";

            if (generator != null && _generatorAimStage != AimStage.Idle && AdvanceGeneratorAim(generator, now)) return;

            if (_needsSpenderPrime && _primePending && IsSpenderAnimation())
            {
                _needsSpenderPrime = false;
                _primePending = false;
                _primeAttempts = 0;
            }
            if (_pulse != ActionKey.Unknown) return;

            if (IsAlternateHatredSpenderDown(spender, strafe, generator))
            {
                _needsSpenderPrime = true;
                _primePending = false;
                _primeAttempts = 0;
                _nextPrimeTick = now;
                ReleaseStrafe();
                return;
            }

            if (_needsSpenderPrime)
            {
                MaintainSpenderPrime(spender, generator, now);
                return;
            }

            if (_heldStrafe == ActionKey.Unknown && s7o_ImpaleInput.Down(_ownerName, strafe.Key))
                _heldStrafe = strafe.Key;
            if (_heldStrafe == ActionKey.Unknown) return;

            // Mantenimiento de la Generadora (Si existe)
            if (generator != null)
            {
                MaintainGenerator(generator, now);
            }

            // 1. RECOLECTOR DE SEGURIDAD EXTREMA: Levanta la Q de inmediato si quedó pegada por lag
            if (_pulse == ActionKey.Unknown && s7o_ImpaleInput.IsDown(spender.Key))
            {
                s7o_ImpaleInput.Up(_ownerName, spender.Key);
            }

            // Mantenimiento de la Generadora (Si existe)
            if (generator != null)
            {
                MaintainGenerator(generator, now);
            }

            // 2. INYECCIÓN OFENSIVA SINCRONIZADA A 13 FRAMES PARA MULTISHOT (MODO COMBAT)
            if (_combat && !s7o_InputReleaseArbiter.HasPendingRelease && _pulse == ActionKey.Unknown)
            {
                if (Due(now, _nextPrimeTick) && Hud.Game.Me.Stats.ResourceCurPri >= spender.ResourceCost)
                {
                    // Escaneo rápido de objetivos en pantalla
                    IMonster target = Hud.Game.AliveMonsters
                        .Where(m => m != null && m.IsAlive && m.Attackable && m.IsOnScreen && !m.Invulnerable && m.FloorCoordinate != null)
                        .OrderByDescending(m => m.Rarity == ActorRarity.Boss || m.Rarity == ActorRarity.Rare || m.Rarity == ActorRarity.Champion || m.Rarity == ActorRarity.Unique ? 2 : 0)
                        .ThenBy(m => m.NormalizedXyDistanceToMe)
                        .FirstOrDefault();

                    if (target != null)
                    {
                        var screenPoint = target.FloorCoordinate.ToScreenCoordinate(true, true);
                        if (screenPoint != null && !double.IsNaN(screenPoint.X) && !double.IsNaN(screenPoint.Y))
                        {
                            int originalX = Hud.Window.CursorX + Hud.Window.Offset.X;
                            int originalY = Hud.Window.CursorY + Hud.Window.Offset.Y;

                            int aimX = (int)Math.Round(screenPoint.X) + Hud.Window.Offset.X;
                            int aimY = (int)Math.Round(screenPoint.Y) + Hud.Window.Offset.Y;

                            // Mueve el cursor al Elite/Bicho y gatilla
                            s7o_ImpaleInput.MoveCursorAbsolute(aimX, aimY);
                            if (s7o_ImpaleInput.DownAt(_ownerName, spender.Key, aimX, aimY))
                            {
                                _pulse = spender.Key;
                                _pulseReleaseTick = unchecked(now + 25); // Pulso físico corto (25ms)
                                
                                s7o_ImpaleInput.MoveCursorAbsolute(originalX, originalY);
                                
                                // CADENCIA FIJA AJUSTADA A 13 FRAMES:
                                // 13 frames lúdicos equivalen exactamente a 216ms a 60Hz.
                                // Esto le da el tiempo exacto al arco para disparar sin encimar comandos.
                                _nextPrimeTick = unchecked(now + 216); 
                            }
                        }
                    }
                }
            }
        }

        private void MaintainGenerator(IPlayerSkill generator, int now)
        {
            if (!Due(now, _nextGeneratorTick)) return;
            int interval = Math.Max(50, _combat ? CombatGeneratorIntervalMs : SpeedGeneratorIntervalMs);
            if (s7o_ImpaleInput.IsDown(generator.Key))
            { _nextGeneratorTick = unchecked(now + interval); return; }
            
            bool isEvasive = generator.SnoPower.Sno == EvasiveFireSno;
            if (AutoAimGenerator && !isEvasive && BeginGeneratorAim(generator, now)) return;
            if (StartSkillPulse(generator.Key, now))
                _nextGeneratorTick = unchecked(now + interval);
            else
                _nextGeneratorTick = unchecked(now + 50);
        }

        public void SetAutoAimGenerator(bool enabled)
        {
            AutoAimGenerator = enabled;
            if (_running && _generatorAimStage != AimStage.Idle)
                CancelGeneratorAim("aim setting changed", Environment.TickCount);
            else CancelPulse();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                File.WriteAllText(_settingsPath, "AutoAimGenerator=" + enabled + Environment.NewLine);
            }
            catch { }
        }

        private bool TryGeneratorPoint(IMonster monster, ActionKey key, out int x, out int y)
        {
            x = y = 0;
            if (monster == null || !monster.IsAlive || !monster.Attackable || !monster.IsOnScreen || monster.Illusion || monster.Invulnerable || monster.Untargetable || monster.Invisible || monster.Hidden || monster.FloorCoordinate == null) return false;
            var point = monster.FloorCoordinate.ToScreenCoordinate(true, true);
            if (point == null || double.IsNaN(point.X) || double.IsNaN(point.Y) || double.IsInfinity(point.X) || double.IsInfinity(point.Y)) return false;
            double cx = Math.Round(point.X), cy = Math.Round(point.Y);
            if (cx < 0 || cy < 0 || cx >= Hud.Window.Size.Width || cy >= Hud.Window.Size.Height || (key == ActionKey.LeftSkill && !IsLeftClickPointSafe(cx, cy))) return false;
            long sx = (long)cx + Hud.Window.Offset.X, sy = (long)cy + Hud.Window.Offset.Y;
            if (sx < int.MinValue || sx > int.MaxValue || sy < int.MinValue || sy > int.MaxValue) return false;
            x = (int)sx; y = (int)sy; return true;
        }

        private bool BeginGeneratorAim(IPlayerSkill generator, int now)
        {
            if (s7o_InputReleaseArbiter.HasPendingRelease || (s7o_ImpaleInput.IsDown(ActionKey.LeftSkill) && _heldStrafe != ActionKey.LeftSkill)) return false;
            IMonster best = null; int bestTier = -1, aimX = 0, aimY = 0;
            foreach (var monster in Hud.Game.AliveMonsters)
            {
                if (monster == null) continue;
                int tier = monster.Rarity == ActorRarity.Boss || monster.Rarity == ActorRarity.Rare || monster.Rarity == ActorRarity.Champion || monster.Rarity == ActorRarity.Unique ? 2 : monster.Rarity == ActorRarity.RareMinion ? 1 : 0;
                if (best != null && (tier < bestTier || (tier == bestTier && monster.NormalizedXyDistanceToMe >= best.NormalizedXyDistanceToMe))) continue;
                int x, y; if (!TryGeneratorPoint(monster, generator.Key, out x, out y)) continue;
                best = monster; bestTier = tier; aimX = x; aimY = y;
            }
            CursorPoint cursor; 
            if (best == null || !GetCursorPos(out cursor)) return false;
            if (generator.Key == ActionKey.LeftSkill && !IsLeftClickPointSafe((long)cursor.X - Hud.Window.Offset.X, (long)cursor.Y - Hud.Window.Offset.Y)) return false;
            
            ReleaseStrafe(); 
            if (s7o_InputReleaseArbiter.HasPendingRelease || !EnsureStandstill()) return false;
            _genAimActor = best.AcdId; _genAimX = aimX; _genAimY = aimY; _genAimStartedTick = now; _genDueTick = unchecked(now + AimPauseAckMs);

            _genAimActor = best.AcdId; _genAimX = aimX; _genAimY = aimY; _genAimStartedTick = now; _genDueTick = unchecked(now + AimPauseAckMs);
            _genInputSent = _genSawCastAnimation = false; _genInputTick = _genResumeTick = 0; _genRestoreWriteSent = _genRestoreConfirmed = _genRestoreRescueAttempted = false;
            _genRestoreDesktopConfirmed = _genRestoreAwaitingNative = false; _genRestoreStartedTick = _genRestoreWriteGameTick = int.MinValue;
            _genRestorePrepared = _genCursorOwned = _genSyntheticPending = false; _genDeltaX = _genDeltaY = 0; _genTransactionSerial++; _genEndReason = "pending"; _generatorAimStage = AimStage.Lease; return true;
        }

        private void ObserveGeneratorAnimation()
        {
            if (!_genInputSent || _generatorAimStage == AimStage.Idle || _generatorAimStage == AimStage.Restore || _generatorAimStage == AimStage.RestoreSettle || Hud == null || Hud.Game == null || Hud.Game.Me == null) return;
            var state = Hud.Game.Me.AnimationState;
            if (Hud.Game.Me.Animation != _genPreInputAnimation && (state == AcdAnimationState.Attacking || state == AcdAnimationState.Casting)) _genSawCastAnimation = true;
        }
        private bool AdvanceGeneratorAim(IPlayerSkill generator, int now)
        {
            if ((!AutoAimGenerator) && _generatorAimStage != AimStage.Restore && _generatorAimStage != AimStage.RestoreSettle) { CancelGeneratorAim("aim disabled", now); return true; }
            int age = unchecked(now - (_genInputSent ? _genInputTick : _genAimStartedTick));
            if (_generatorAimStage != AimStage.Restore && _generatorAimStage != AimStage.RestoreSettle && age > (_genInputSent ? AimPostInputLimitMs : AimPreInputLimitMs)) { CancelGeneratorAim("transaction watchdog", now); return true; }
            if (_generatorAimStage == AimStage.Lease)
            {
                if (s7o_InputReleaseArbiter.HasPendingRelease || !s7o_ImpaleInput.IsVirtualKeyDown(StandstillKey()) || Hud.Game.Me.AnimationState == AcdAnimationState.Running) { if (Due(now, _genDueTick)) { CancelGeneratorAim("movement stop timeout", now); } return true; }
                int x, y; CursorPoint cursor; if (!TryGeneratorPoint(FindGeneratorTarget(), generator.Key, out x, out y) || !GetCursorPos(out cursor) || !IsLeftClickPointSafe((long)cursor.X - Hud.Window.Offset.X, (long)cursor.Y - Hud.Window.Offset.Y)) { CancelGeneratorAim("target/UI before aim", now); return true; }
                _genSavedX = Hud.Window.CursorX + Hud.Window.Offset.X; _genSavedY = Hud.Window.CursorY + Hud.Window.Offset.Y; _genReferenceX = _genSavedX; _genReferenceY = _genSavedY; _genAimX = x; _genAimY = y;
                if (!WriteGeneratorAim()) { CancelGeneratorAim("aim write failed", now); return true; }
                _genCursorOwned = true; _generatorAimStage = AimStage.Aim; _genDueTick = unchecked(now + AimPreviewMs); return true;
            }
            if (_generatorAimStage == AimStage.Aim)
            {
                if (!Due(now, _genDueTick)) return true;
                CaptureGeneratorCursorIntent(); if (Hud.Game.Me.AnimationState == AcdAnimationState.Running || !s7o_ImpaleInput.IsVirtualKeyDown(StandstillKey()) || s7o_ImpaleInput.IsDown(generator.Key)) { CancelGeneratorAim("readiness lost/player input", now); return true; }
                int x, y; if (!TryGeneratorPoint(FindGeneratorTarget(), generator.Key, out x, out y) || (generator.Key == ActionKey.LeftSkill && !IsLeftClickPointSafe((long)_genAimX - Hud.Window.Offset.X, (long)_genAimY - Hud.Window.Offset.Y))) { CancelGeneratorAim("target lost/UI", now); return true; }
                _genPreInputAnimation = Hud.Game.Me.Animation; ArmGeneratorSyntheticWrite(_genAimX, _genAimY);
                if (!s7o_ImpaleInput.DownAt(_ownerName, generator.Key, _genAimX, _genAimY)) { CancelGeneratorAim("aim/input batch failed", now); return true; }
                _pulse = generator.Key; _pulseReleaseTick = unchecked(now + AimHoldMs); _genInputSent = true; _genInputTick = now; _generatorAimStage = AimStage.Hold; return true;
            }
            if (_generatorAimStage == AimStage.Hold)
            {
                CaptureGeneratorCursorIntent(); if (_pulse != ActionKey.Unknown || s7o_InputReleaseArbiter.HasPendingRelease) return true;
                if (_genSawCastAnimation) BeginGeneratorRestore(now);
                else { _generatorAimStage = AimStage.PostInputSettle; _genDueTick = unchecked(now + AimPostInputMs); } return true;
            }
            if (_generatorAimStage == AimStage.PostInputSettle) { CaptureGeneratorCursorIntent(); if (_genSawCastAnimation || Due(now, _genDueTick)) BeginGeneratorRestore(now); return true; }
            if (_generatorAimStage == AimStage.Restore)
            {
                if (s7o_InputReleaseArbiter.HasPendingRelease) return true;
                long x = (long)Hud.Window.CursorX + Hud.Window.Offset.X; long y = (long)Hud.Window.CursorY + Hud.Window.Offset.Y;
                int rDist = GeneratorCursorDistance(x, y, _genRestoreX, _genRestoreY); int aDist = GeneratorCursorDistance(x, y, _genAimX, _genAimY);
                _genRestoreConfirmed = _genRestoreWriteSent && rDist <= 10; bool residue = _genRestoreWriteSent && rDist >= 240 && (long)aDist * 2L + 40L < rDist;
                CursorPoint dtop; bool dReadable = GetCursorPos(out dtop); _genRestoreDesktopConfirmed = _genRestoreWriteSent && dReadable && GeneratorCursorDistance(dtop.X, dtop.Y, _genRestoreX, _genRestoreY) <= 10;
                _genRestoreAwaitingNative = residue && !_genRestoreConfirmed;
                if (_genRestoreAwaitingNative)
                {
                    if (unchecked(now - _genRestoreStartedTick) >= AimPostInputLimitMs) { _genEndReason = "restore acknowledgement timeout"; Stop(); return true; }
                    if (Hud.Game.CurrentGameTick == _genRestoreWriteGameTick || !dReadable) return true;
                    int dRest = GeneratorCursorDistance(dtop.X, dtop.Y, _genRestoreX, _genRestoreY); int dAim = GeneratorCursorDistance(dtop.X, dtop.Y, _genAimX, _genAimY);
                    if (dRest >= 240 && (long)dAim * 2L + 40L < dRest && !_genRestoreRescueAttempted) { _genRestoreRescueAttempted = true; if (!RestoreGeneratorCursor()) { _genEndReason = "restore write failed"; Stop(); } } return true;
                }
                ClearGeneratorCursor(); _generatorAimStage = AimStage.RestoreSettle;
            }
            if (_generatorAimStage == AimStage.RestoreSettle)
            {
                if (s7o_InputReleaseArbiter.HasPendingRelease || !Due(now, unchecked(_genAimStartedTick + AimMinimumLeaseMs))) return true;
                ReleaseStandstill(); _generatorAimStage = AimStage.Idle; _genResumeTick = now;
                if (_genEndReason == "pending") _genEndReason = _genSawCastAnimation ? "animation observed/handback" : "settled/handback";
                SkipGeneratorCadence(now); return false;
            }
            return true;
        }

        private IMonster FindGeneratorTarget() { return Hud.Game.AliveMonsters.FirstOrDefault(m => m.AcdId == _genAimActor); }
        private void CancelGeneratorAim(string reason, int now) { if (_pulse != ActionKey.Unknown) { s7o_ImpaleInput.Up(_ownerName, _pulse); _pulse = ActionKey.Unknown; } _genEndReason = reason; if (_genCursorOwned) BeginGeneratorRestore(now); else { ReleaseStandstill(); _generatorAimStage = AimStage.Idle; _genResumeTick = now; SkipGeneratorCadence(now); } }
        private void BeginGeneratorRestore(int now) { CaptureGeneratorCursorIntent(); if (!RestoreGeneratorCursor()) { _genEndReason = "restore write failed"; Stop(); return; } _generatorAimStage = AimStage.Restore; }
        private void SkipGeneratorCadence(int now) { _nextGeneratorTick = unchecked(now + Math.Max(50, _combat ? CombatGeneratorIntervalMs : SpeedGeneratorIntervalMs)); }
        private static int GeneratorCursorDistance(long x, long y, long tx, long ty) { double dx = x - tx, dy = y - ty; return (int)Math.Min(int.MaxValue, Math.Round(Math.Sqrt(dx * dx + dy * dy))); }
        private void ArmGeneratorSyntheticWrite(int x, int y) { _genSyntheticFromX = Hud.Window.CursorX + Hud.Window.Offset.X; _genSyntheticFromY = Hud.Window.CursorY + Hud.Window.Offset.Y; _genReferenceX = x; _genReferenceY = y; _genSyntheticPending = true; }
        private bool WriteGeneratorAim() { ArmGeneratorSyntheticWrite(_genAimX, _genAimY); return s7o_ImpaleInput.MoveCursorAbsolute(_genAimX, _genAimY); }
        private void AddGeneratorCursorDelta(int dx, int dy) { _genDeltaX = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)_genDeltaX + dx)); _genDeltaY = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)_genDeltaY + dy)); }
        private void CaptureGeneratorCursorIntent()
        {
            if (!_genCursorOwned || _genRestorePrepared) return;
            int x = Hud.Window.CursorX + Hud.Window.Offset.X; int y = Hud.Window.CursorY + Hud.Window.Offset.Y;
            if (_genSyntheticPending)
            {
                int tDist = GeneratorCursorDistance(x, y, _genReferenceX, _genReferenceY); int fDist = GeneratorCursorDistance(x, y, _genSyntheticFromX, _genSyntheticFromY);
                if (tDist <= 14) { _genSyntheticPending = false; _genReferenceX = x; _genReferenceY = y; return; }
                if (fDist <= 14) return;
                if (tDist + 14 < fDist) { AddGeneratorCursorDelta(x - _genReferenceX, y - _genReferenceY); _genSyntheticPending = false; _genReferenceX = x; _genReferenceY = y; return; }
                AddGeneratorCursorDelta(x - _genSyntheticFromX, y - _genSyntheticFromY); _genSyntheticFromX = x; _genSyntheticFromY = y; return;
            }
            AddGeneratorCursorDelta(x - _genReferenceX, y - _genReferenceY); _genReferenceX = x; _genReferenceY = y;
        }

        private bool RestoreGeneratorCursor()
        {
            if (!_genCursorOwned) return true;
            if (Hud == null || Hud.Window == null || !Hud.Window.IsForeground) return false;
            if (!_genRestorePrepared)
            {
                CaptureGeneratorCursorIntent(); long x = (long)_genSavedX + _genDeltaX, y = (long)_genSavedY + _genDeltaY; long left = Hud.Window.Offset.X, top = Hud.Window.Offset.Y;
                _genRestoreX = (int)Math.Max(left, Math.Min(left + Math.Max(0, Hud.Window.Size.Width - 1), x)); _genRestoreY = (int)Math.Max(top, Math.Min(top + Math.Max(0, Hud.Window.Size.Height - 1), y));
                _genRestorePrepared = true; _genRestoreStartedTick = Environment.TickCount;
            }
            _genRestoreWriteSent = s7o_ImpaleInput.MoveCursorAbsolute(_genRestoreX, _genRestoreY);
            if (_genRestoreWriteSent) _genRestoreWriteGameTick = Hud.Game != null ? Hud.Game.CurrentGameTick : int.MinValue;
            return _genRestoreWriteSent;
        }

        private void ClearGeneratorCursor() { _genCursorOwned = _genSyntheticPending = _genRestorePrepared = false; _genRestoreAwaitingNative = false; }

        private void AddClickUi(IUiElement element)
        {
            if (element != null && !_leftClickUiElements.Contains(element)) _leftClickUiElements.Add(element);
        }

        private void RefreshClickUiRects()
        {
            _leftClickUiReadable = false;
            _leftClickUiRects.Clear();
            try
            {
                if (_hudMenu == null)
                    foreach (var plugin in Hud.AllPlugins)
                        if (plugin is s7o_HUD_MENU) { _hudMenu = (s7o_HUD_MENU)plugin; break; }
                if (!Hud.Render.UiHidden)
                {
                    // Copy native bounds once per collection, as GenMonk does.
                    foreach (var ui in _leftClickUiElements)
                        if (ui.Visible && ui.Rectangle.Width > 0 && ui.Rectangle.Height > 0)
                            _leftClickUiRects.Add(ui.Rectangle);
                    foreach (var player in Hud.Game.Players)
                        if (player != null && player.IsInGame && player.PortraitUiElement != null
                            && player.PortraitUiElement.Visible)
                            _leftClickUiRects.Add(player.PortraitUiElement.Rectangle);
                    var size = Hud.Window.Size;
                    if (size.Width <= 0 || size.Height <= 0) return;
                    float scale = Math.Min(size.Width / 1920f, size.Height / 1080f);
                    float extraX = size.Width - 1920f * scale, extraY = size.Height - 1080f * scale;
                    foreach (var source in ClickGuardRects1920x1080)
                    {
                        float centerX = source.Left + source.Width * 0.5f;
                        float centerY = source.Top + source.Height * 0.5f;
                        float offsetX = centerX < 640f ? 0f : centerX > 1280f ? extraX : extraX * 0.5f;
                        float offsetY = centerY < 360f ? 0f : centerY > 720f ? extraY : extraY * 0.5f;
                        _leftClickUiRects.Add(new RectangleF(source.Left * scale + offsetX,
                            source.Top * scale + offsetY, source.Width * scale, source.Height * scale));
                    }
                }
                _leftClickUiReadable = true;
            }
            catch { } // Unknown UI state must not permit a synthetic left click.
        }

        private bool IsLeftClickPointSafe(double x, double y)
        {
            if (!_leftClickUiReadable || double.IsNaN(x) || double.IsNaN(y)
                || x < 0 || y < 0 || x >= Hud.Window.Size.Width || y >= Hud.Window.Size.Height) return false;
            if (_hudMenu != null && _hudMenu.IsAutomationLeftClickBlocked((float)x, (float)y)) return false;
            foreach (var rect in _leftClickUiRects)
                if (x >= rect.Left - 2 && x <= rect.Right + 2
                    && y >= rect.Top - 2 && y <= rect.Bottom + 2) return false;
            return true;
        }

        private bool IsLeftClickDesktopPointSafe(int x, int y)
        {
            try
            {
                return Hud.Window.IsForeground && IsLeftClickPointSafe((long)x - Hud.Window.Offset.X,
                    (long)y - Hud.Window.Offset.Y);
            }
            catch { return false; }
        }

        private bool IsLeftClickSafe()
        {
            try
            {
                // Fallback pulses must not click a world follower either.
                var actor = Hud.Game.SelectedActor;
                if (actor != null && actor.SnoActor != null && actor.SnoActor.Kind == ActorKind.Follower)
                    return false;
                CursorPoint cursor;
                return Hud.Window.IsForeground && GetCursorPos(out cursor)
                    && IsLeftClickPointSafe((long)cursor.X - Hud.Window.Offset.X,
                        (long)cursor.Y - Hud.Window.Offset.Y);
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorPoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

        private bool IsSpenderAnimation()
        {
            var animation = Hud.Game.Me.Animation;
            if (_detectedBuild == DHBuild.ShadowImpale)
            {
                return animation == AnimSnoEnum._demonhunter_female_cast_impale_01
                    || animation == AnimSnoEnum._demonhunter_male_cast_impale_01;
            }
            if (_detectedBuild == DHBuild.UEMultishot)
            {
                return animation == AnimSnoEnum._demonhunter_female_dw_xbow_multishot_01
                    || animation == AnimSnoEnum._demonhunter_female_bow_multishot_01
                    || animation == AnimSnoEnum._demonhunter_male_xbow_multishot
                    || animation == AnimSnoEnum._demonhunter_male_dw_xbow_multishot
                    || animation == AnimSnoEnum._demonhunter_male_1hxbow_multishot
                    || animation == AnimSnoEnum._demonhunter_female_xbow_multishot_01
                    || animation == AnimSnoEnum._demonhunter_male_bow_multishot
                    || animation == AnimSnoEnum._demonhunter_female_1hxbow_multishot_01;
            }
            return false;
        }

        private void MaintainSpenderPrime(IPlayerSkill spender, IPlayerSkill generator, int now)
        {
            ReleaseStrafe();
            if (_primePending)
            {
                if (unchecked(now - _primeStartedTick) < PrimeVerifyMs) return;
                _primePending = false;
                _nextPrimeTick = unchecked(now + (_primeAttempts >= 3 ? PrimeBackoffMs : PrimeRetryMs));
                if (_primeAttempts >= 3) _primeAttempts = 0;
            }

            if (Hud.Game.Me.Stats.ResourceCurPri + 0.1f < Math.Max(0f, spender.ResourceCost))
            {
                if (generator != null) MaintainGenerator(generator, now);
                return;
            }
            if (!Due(now, _nextPrimeTick)) return;

            bool userHolding = s7o_ImpaleInput.IsDown(spender.Key);
            if (!userHolding && !StartSkillPulse(spender.Key, now, 55)) return;
            _primePending = true;
            _primeStartedTick = now;
            _primeAttempts++;
        }

        private bool InputUiBlocked()
        {
            return IsVisible(_chatEditLine) || IsVisible(_urshiGemPane)
                || IsVisible(Hud.Render.WorldMapUiElement)
                || IsVisible(Hud.Render.ActMapUiElement);
        }

        private static bool IsVisible(IUiElement element)
        {
            if (element == null) return false;
            try { element.Refresh(); return element.Visible; }
            catch { return true; }
        }

        public void PaintTopInGame(ClipState clipState)
        {
            if (!Enabled || !ShowStatusText || clipState != ClipState.AfterClip || Hud == null
                || Hud.Game == null || Hud.Game.Me == null || Hud.Window == null
                || !Hud.Game.IsInGame || Hud.Game.IsLoading || Hud.Game.IsPaused || Hud.Game.IsInTown)
                return;

            IPlayerSkill strafe;
            IPlayerSkill spender;
            IPlayerSkill generator;
            if (!CanAdvertise(out strafe, out spender, out generator)) return;

            string text;
            IFont font;
            if (_running && _combat)
            {
                text = "Combat: " + ModeHotkey + " = Speed | " + ToggleHotkey + " = Stop";
                font = _combatFont;
            }
            else if (_running)
            {
                text = "Speed: " + ModeHotkey + " = Combat | " + ToggleHotkey + " = Stop";
                font = _runningFont;
            }
            else
            {
                text = ToggleHotkey + " = Strafe";
                font = _statusFont;
            }

            if (font == null) return;
            text = s7o_Localization.Display(text);
            var layout = font.GetTextLayout(text);
            float x = Hud.Window.Size.Width * StatusTextCenterXFrac - layout.Metrics.Width / 2.0f;
            float y = Hud.Window.Size.Height * StatusTextYFrac + StatusTextYOffsetPx;
            font.DrawText(text, x, y, true);
        }

        private bool CanRun(out IPlayerSkill strafe, out IPlayerSkill spender, out IPlayerSkill generator)
        {
            strafe = null; spender = null; generator = null;
            try
            {
                if (Hud == null || Hud.Game == null || Hud.Window == null || !Hud.Game.IsInGame
                    || Hud.Game.IsLoading || Hud.Game.IsPaused || Hud.Game.IsInTown
                    || !Hud.Window.IsForeground || Hud.Game.Me == null || Hud.Game.Me.IsDead
                    || Hud.Game.Me.HeroClassDefinition == null || Hud.Game.Me.HeroClassDefinition.HeroClass != HeroClass.DemonHunter
                    || (Hud.Inventory != null && Hud.Inventory.InventoryMainUiElement != null && Hud.Inventory.InventoryMainUiElement.Visible)
                    || InputUiBlocked())
                    return false;

                if (Hud.Game.Me.GetSetItemCount(GoDSetSno) >= 4) return false;

                int shadowCount = Hud.Game.Me.GetSetItemCount(ShadowSetSno);
                int ueCount = Hud.Game.Me.GetSetItemCount(UESetSno);
                
                // Doble chequeo seguro: si el SNO del set falla, buscamos la habilidad en la barra
                bool hasMultishot = Hud.Game.Me.Powers.UsedSkills.Any(s => s != null && s.SnoPower != null && s.SnoPower.Sno == MultishotSno);
                bool hasImpale = Hud.Game.Me.Powers.UsedSkills.Any(s => s != null && s.SnoPower != null && s.SnoPower.Sno == ImpaleSno);

                if (ueCount >= 6 || (hasMultishot && shadowCount < 6)) 
                { 
                    _detectedBuild = DHBuild.UEMultishot; 
                    _spenderSno = MultishotSno; 
                    _ownerName = "Multishot"; 
                }
                else if (shadowCount >= 6 || hasImpale) 
                { 
                    _detectedBuild = DHBuild.ShadowImpale; 
                    _spenderSno = ImpaleSno; 
                    _ownerName = "Impale"; 
                }
                else if (RequireSet) 
                { 
                    _detectedBuild = DHBuild.None; 
                    return false; 
                }

                return TryGetSkills(out strafe, out spender, out generator);
            }
            catch { return false; }
        }

        private bool CanAdvertise(out IPlayerSkill strafe, out IPlayerSkill spender, out IPlayerSkill generator)
        {
            strafe = null; spender = null; generator = null;
            try
            {
                if (Hud.Game.Me == null || Hud.Game.Me.HeroClassDefinition == null 
                    || Hud.Game.Me.HeroClassDefinition.HeroClass != HeroClass.DemonHunter 
                    || Hud.Game.Me.GetSetItemCount(GoDSetSno) >= 4) 
                    return false;

                bool hasMultishot = Hud.Game.Me.Powers.UsedSkills.Any(s => s != null && s.SnoPower != null && s.SnoPower.Sno == MultishotSno);
                bool hasImpale = Hud.Game.Me.Powers.UsedSkills.Any(s => s != null && s.SnoPower != null && s.SnoPower.Sno == ImpaleSno);
                
                int shadowCount = Hud.Game.Me.GetSetItemCount(ShadowSetSno);
                int ueCount = Hud.Game.Me.GetSetItemCount(UESetSno);

                if (RequireSet && shadowCount < 6 && ueCount < 6 && !hasMultishot && !hasImpale)
                    return false;

                return TryGetSkills(out strafe, out spender, out generator);
            }
            catch { return false; }
        }

        private bool TryGetSkills(out IPlayerSkill strafe, out IPlayerSkill spender, out IPlayerSkill generator)
        {
            strafe = null;
            spender = null;
            generator = null;
            try
            {
                foreach (var skill in Hud.Game.Me.Powers.UsedSkills)
                {
                    if (skill == null || skill.SnoPower == null) continue;
                    uint sno = skill.SnoPower.Sno;
                    
                    if (sno == StrafeSno) strafe = skill;
                    else if (sno == _spenderSno) spender = skill;
                    else if (sno == BolasSno || sno == GrenadesSno || sno == HungeringArrowSno || sno == EntanglingShotSno || sno == EvasiveFireSno)
                    {
                        generator = skill;
                    }
                }
            }
            catch { return false; }

            return strafe != null && spender != null;
        }

        private bool IsAlternateHatredSpenderDown(IPlayerSkill spender, IPlayerSkill strafe, IPlayerSkill generator)
        {
            try
            {
                foreach (var skill in Hud.Game.Me.Powers.UsedSkills)
                {
                    if (skill == null || skill.SnoPower == null || skill.Key == ActionKey.Unknown) continue;
                    uint sno = skill.SnoPower.Sno;
                    if (sno == _spenderSno || sno == StrafeSno || sno == RapidFireSno
                        || skill == generator || skill == spender || skill == strafe)
                        continue;

                    var types = skill.SnoPower.ResourceCostTypeByRune;
                    int rune = skill.Rune;
                    if (skill.ResourceCost <= 0f || types == null || rune < 0 || rune >= types.Length
                        || types[rune] != PowerResourceCostType.primary)
                        continue;

                    if (s7o_ImpaleInput.IsDown(skill.Key)) return true;
                }
            }
            catch { }
            return false;
        }

        private bool StartSkillPulse(ActionKey key, int now, int holdMs = 0)
        {
            if (!EnsureStandstill()) return false;
            if (!s7o_ImpaleInput.Down(_ownerName, key))
            {
                if (_generatorAimStage == AimStage.Idle) ReleaseStandstill();
                return false;
            }

            _pulse = key;
            _pulseReleaseTick = unchecked(now + (holdMs > 0
                ? Math.Min(80, holdMs) : Math.Max(1, Math.Min(40, SkillPulseHoldMs))));
            return true;
        }

        private void FinishPulse(int now)
        {
            if (_pulse == ActionKey.Unknown || !Due(now, _pulseReleaseTick)) return;
            
            // Enviamos la orden de liberación física con máxima prioridad
            s7o_ImpaleInput.Up(_ownerName, _pulse);
            _pulse = ActionKey.Unknown;
            
            if (_generatorAimStage == AimStage.Idle) ReleaseStandstill();
            s7o_InputReleaseArbiter.RetryPending(now); // Fuerza al árbitro a procesar colas pendientes
        }

        private void CancelPulse(string reason = "cancelled")
        {
            if (_pulse != ActionKey.Unknown)
            {
                s7o_ImpaleInput.Up(_ownerName, _pulse);
                _pulse = ActionKey.Unknown;
            }
            if (RestoreGeneratorCursor()) ClearGeneratorCursor();
            if (_generatorAimStage != AimStage.Idle && _genEndReason == "pending") _genEndReason = reason;
            _generatorAimStage = AimStage.Idle;
            _genResumeTick = Environment.TickCount;
            ReleaseStandstill();
        }

        private ushort StandstillKey()
        {
            return s7o_ImpaleInput.StandstillVirtualKey(0x10);
        }

        private bool EnsureStandstill()
        {
            if (_ownedStandstill != 0) return true;
            ushort key = StandstillKey();
            if (key == 0) return false;
            if (s7o_ImpaleInput.IsVirtualKeyDown(key)) return true;
            if (!s7o_ImpaleInput.DownVirtualKey(_ownerName, key)) return false;
            _ownedStandstill = key;
            return true;
        }

        private void ReleaseStandstill()
        {
            if (_ownedStandstill == 0) return;
            s7o_ImpaleInput.UpVirtualKey(_ownerName, _ownedStandstill);
            _ownedStandstill = 0;
        }

        private void ReleaseStrafe()
        {
            if (_heldStrafe == ActionKey.Unknown) return;
            s7o_ImpaleInput.Up(_ownerName, _heldStrafe);
            _heldStrafe = ActionKey.Unknown;
        }

        private bool IsUnoperatedPylonNearby(float range)
        {
            try
            {
                if (Hud == null || Hud.Game == null || Hud.Game.Me == null
                    || Hud.Game.Me.FloorCoordinate == null || Hud.Game.Shrines == null) return false;
                float limit = Math.Max(0, range);
                foreach (var shrine in Hud.Game.Shrines)
                    if (shrine != null && shrine.IsPylon && !shrine.IsDisabled && !shrine.IsOperated
                        && shrine.FloorCoordinate != null
                        && Hud.Game.Me.FloorCoordinate.XYDistanceTo(shrine.FloorCoordinate) <= limit) return true;
                return false;
            }
            catch { return false; }
        }

        private void UpdatePortalInteractionState(int now)
        {
            _portalPauseActive = false;
            _portalArrivalEscapeActive = false;

            try
            {
                if (Hud == null || Hud.Game == null || Hud.Game.Me == null
                    || Hud.Game.Me.FloorCoordinate == null || Hud.Game.Portals == null)
                    return;

                if (!Hud.Game.IsInGame || Hud.Game.IsInTown)
                {
                    ResetPortalApproachState();
                    return;
                }

                IPortal nearest = null;
                float nearestDistance = float.MaxValue;
                foreach (var portal in Hud.Game.Portals)
                {
                    // The same arrival/approach distinction applies to Vision, Vault and Rift
                    // portals. Ignore actors retained from the previous world snapshot.
                    if (portal == null || portal.FloorCoordinate == null || portal.IsDisabled
                        || portal.WorldId != Hud.Game.Me.WorldId) continue;
                    float distanceToPortal = Hud.Game.Me.FloorCoordinate.XYDistanceTo(portal.FloorCoordinate);
                    if (distanceToPortal < nearestDistance)
                    { nearest = portal; nearestDistance = distanceToPortal; }
                }

                if (nearest == null)
                {
                    // Once an armed/approached portal disappears, immediately hand ownership
                    // back to movement while briefly retaining identity. A one-frame actor flicker
                    // can then restore the original pause if the same portal comes back.
                    if (_trackedPortalLastSeenTick != int.MinValue
                        && unchecked(now - _trackedPortalLastSeenTick) < PortalIdentityRetentionMs)
                    {
                        _portalArrivalEscapeActive = PauseNearPortal && _trackedPortalArmed;
                        return;
                    }

                    ResetPortalApproachState();
                    return;
                }

                float distance = Hud.Game.Me.FloorCoordinate.XYDistanceTo(nearest.FloorCoordinate);
                float pauseRange = Math.Max(0, PortalPauseRange);

                if (!MatchesTrackedPortal(nearest))
                {
                    _trackedPortalWorldId = nearest.WorldId;
                    _trackedPortalAnnId = nearest.AnnId;
                    _trackedPortalAcdId = nearest.AcdId;
                    _trackedPortalX = nearest.FloorCoordinate.X;
                    _trackedPortalY = nearest.FloorCoordinate.Y;
                    _trackedPortalArmed = distance > pauseRange;
                    _trackedPortalArrivalTick = _trackedPortalArmed ? int.MinValue : now;
                    _trackedPortalClearedRange = _trackedPortalArmed;
                }
                else if (!_trackedPortalArmed)
                {
                    if (distance > pauseRange) _trackedPortalClearedRange = true;
                    if (_trackedPortalClearedRange
                        && (_trackedPortalArrivalTick == int.MinValue
                            || unchecked(now - _trackedPortalArrivalTick) >= Math.Max(0, PortalArrivalEscapeMinMs)))
                        _trackedPortalArmed = true;
                }

                _trackedPortalLastSeenTick = now;
                bool insideRange = distance <= pauseRange;
                _portalPauseActive = PauseNearPortal
                    && _trackedPortalArmed && insideRange;
                _portalArrivalEscapeActive = PauseNearPortal
                    && !_trackedPortalArmed && insideRange;
            }
            catch
            {
                _portalPauseActive = false;
            }
        }

        private bool MatchesTrackedPortal(IPortal portal)
        {
            if (portal == null || portal.WorldId != _trackedPortalWorldId) return false;
            if (_trackedPortalAnnId != 0 && portal.AnnId != 0 && portal.AnnId == _trackedPortalAnnId)
                return true;
            if (_trackedPortalAcdId != 0 && portal.AcdId != 0 && portal.AcdId == _trackedPortalAcdId)
                return true;
            if (portal.FloorCoordinate == null) return false;
            float dx = portal.FloorCoordinate.X - _trackedPortalX;
            float dy = portal.FloorCoordinate.Y - _trackedPortalY;
            return dx * dx + dy * dy <= 4.0f;
        }

        private void ResetPortalApproachState()
        {
            _trackedPortalWorldId = 0;
            _trackedPortalAnnId = 0;
            _trackedPortalAcdId = 0;
            _trackedPortalX = 0;
            _trackedPortalY = 0;
            _trackedPortalLastSeenTick = int.MinValue;
            _trackedPortalArrivalTick = int.MinValue;
            _trackedPortalClearedRange = false;
            _trackedPortalArmed = false;
            _portalPauseActive = false;
            _portalArrivalEscapeActive = false;
        }

        private bool IsTransitionSnapshot()
        {
            try
            {
                if (Hud == null || Hud.Game == null || Hud.Window == null
                    || !Hud.Game.IsInGame || Hud.Game.IsLoading || Hud.Game.Me == null)
                    return true;
                // OnNewArea's payload can precede the native town flag by one snapshot.
                if (_areaResumePending && Hud.Game.IsInTown) return true;
                if (Hud.Game.Me.IsDead || Hud.Game.IsInTown) return false;
                var powers = Hud.Game.Me.Powers;
                if (powers == null || powers.UsedSkills == null) return true;
                foreach (var skill in powers.UsedSkills)
                    if (skill != null && skill.SnoPower != null) return false;
                return true;
            }
            catch { return true; }
        }

        private bool IsHoveringUrshi()
        {
            try
            {
                var actor = Hud.Game.SelectedActor;
                return actor != null && actor.NormalizedXyDistanceToMe <= 10f
                    && actor.SnoActor != null
                    && actor.SnoActor.Sno == ActorSnoEnum._p1_lr_tieredrift_nephalem;
            }
            catch { return false; }
        }

        private bool IsHoveringInteractable()
        {
            try
            {
                var actor = Hud.Game.SelectedActor;
                if (actor == null || actor.SnoActor == null || actor.NormalizedXyDistanceToMe > 10f)
                    return false;
                bool portal = actor.SnoActor.Kind == ActorKind.Portal
                    || actor.GizmoType == GizmoType.Portal || actor.GizmoType == GizmoType.BossPortal;
                // A synthetic/native hover on the arrival portal must not defeat escape.
                // A user's LMB interaction still yields; no physical hold is released.
                bool manualLeft = _pulse != ActionKey.LeftSkill && _heldStrafe != ActionKey.LeftSkill
                    && s7o_ImpaleInput.IsDown(ActionKey.LeftSkill);
                if (portal && _portalArrivalEscapeActive && !manualLeft) return false;
                if (portal) return !actor.IsDisabled;
                if (actor.IsDisabled || actor.IsOperated) return false;
                if (actor.SnoActor.Kind == ActorKind.Shrine && Hud.Game.Shrines != null)
                    foreach (var shrine in Hud.Game.Shrines)
                        if (shrine != null && shrine.AcdId == actor.AcdId)
                            return shrine.IsPylon && !shrine.IsDisabled && !shrine.IsOperated;

                // GizmoType.Chest also covers corpses/loose stones/racks. Only native
                // chest kinds (or chest-specific native codes) request this pause.
                string code = actor.SnoActor.Code ?? string.Empty;
                if (actor.SnoActor.Kind == ActorKind.ArmorRack
                    || actor.SnoActor.Kind == ActorKind.WeaponRack
                    || actor.SnoActor.Kind == ActorKind.DeadBody
                    || code.IndexOf("rockpile", StringComparison.OrdinalIgnoreCase) >= 0
                    || code.IndexOf("rock_pile", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
                return actor.SnoActor.Kind == ActorKind.ChestNormal
                    || actor.SnoActor.Kind == ActorKind.Chest
                    || (actor.GizmoType == GizmoType.Chest
                        && code.IndexOf("chest", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        private void Stop()
        {
            CancelPulse();
            ReleaseStrafe();
            _running = false;
            _areaResumePending = false;
            _combat = false;
            _needsSpenderPrime = false;
            _primePending = false;
            _primeStartedTick = 0;
            _nextPrimeTick = Environment.TickCount;
            _primeAttempts = 0;
            _interactionPauseUntilTick = Environment.TickCount;
            _autoLootPauseUntilTick = _interactionPauseUntilTick;
            _nextGeneratorTick = 0;
            _pylonPauseActive = false;
            _inputPauseReason = "idle";
            ResetPortalApproachState();
        }

        private static bool Due(int now, int target) { return unchecked(now - target) >= 0; }
    }

    internal static class s7o_ImpaleInput
    {
        private static readonly Dictionary<ActionKey, int> OwnedActions = new Dictionary<ActionKey, int>();
        private static readonly HashSet<ushort> OwnedVirtualKeys = new HashSet<ushort>();
        private static readonly int InputSize = Marshal.SizeOf(typeof(Input));
        private static s7o_AutoSkill _autoSkill;

        [StructLayout(LayoutKind.Sequential)]
        private struct Input { public uint Type; public Data U; }
        [StructLayout(LayoutKind.Explicit)]
        private struct Data
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx, Dy;
            public uint MouseData, Flags, Time;
            public IntPtr ExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct KeyInput
        {
            public ushort VirtualKey, ScanCode;
            public uint Flags, Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        private static s7o_AutoSkill ResolveAutoSkill()
        {
            if (_autoSkill != null) return _autoSkill;
            try
            {
                var hud = s7o_ImpaleInputContext.Hud;
                if (hud == null) return null;
                foreach (var plugin in hud.AllPlugins)
                {
                    var autoSkill = plugin as s7o_AutoSkill;
                    if (autoSkill == null) continue;
                    _autoSkill = autoSkill;
                    return autoSkill;
                }
            }
            catch { }
            return null;
        }

        public static ushort StandstillVirtualKey(ushort fallback)
        {
            var autoSkill = ResolveAutoSkill();
            return autoSkill != null && autoSkill.ForceStandstillVirtualKey != 0
                ? autoSkill.ForceStandstillVirtualKey : fallback;
        }

        public static bool IsDown(ActionKey key)
        {
            int code = VirtualKey(key);
            return code != 0 && (GetAsyncKeyState(code) & 0x8000) != 0;
        }

        public static bool IsVirtualKeyDown(ushort code)
        {
            return code != 0 && (GetAsyncKeyState(code) & 0x8000) != 0;
        }

        public static bool DownVirtualKey(string owner, ushort code) { return SendVirtualKey(owner, code, false); }
        public static bool UpVirtualKey(string owner, ushort code) { return SendVirtualKey(owner, code, true); }

        private static bool SendVirtualKey(string owner, ushort code, bool up)
        {
            if (code == 0) return false;
            if (up && !OwnedVirtualKeys.Contains(code)) return true;
            if (!up && IsVirtualKeyDown(code)) return false;

            Input input = new Input();
            input.Type = 1u;
            input.U.Keyboard.VirtualKey = code;
            input.U.Keyboard.Flags = up ? 2u : 0u;
            Input[] packet = new[] { input };
            Func<bool> emit = () => SendInput(1u, packet, InputSize) == 1u;

            if (up)
            {
                bool released = s7o_InputReleaseArbiter.Up(owner, code, emit);
                OwnedVirtualKeys.Remove(code);
                return released;
            }

            bool acquired = s7o_InputReleaseArbiter.Down(owner, code, emit);
            if (acquired) OwnedVirtualKeys.Add(code);
            return acquired;
        }

        public static bool Down(string owner, ActionKey key) { return Send(owner, key, false); }
        public static bool DownAt(string owner, ActionKey key, int screenX, int screenY)
        { return Send(owner, key, false, true, screenX, screenY); }

        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

        private static bool TryBuildAbsoluteMove(int screenX, int screenY, out Input input)
        {
            input = new Input();
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            if (width <= 1 || height <= 1) return false;
            input.Type = 0u;
            input.U.Mouse.Dx = (int)Math.Round(Math.Max(0.0, Math.Min(65535.0,
                ((long)screenX - left) * 65535.0 / (width - 1))));
            input.U.Mouse.Dy = (int)Math.Round(Math.Max(0.0, Math.Min(65535.0,
                ((long)screenY - top) * 65535.0 / (height - 1))));
            input.U.Mouse.Flags = 0x0001u | 0x8000u | 0x4000u; // MOVE | ABSOLUTE | VIRTUALDESK
            return true;
        }

        public static bool MoveCursorAbsolute(int screenX, int screenY)
        {
            Input move;
            if (!TryBuildAbsoluteMove(screenX, screenY, out move)) return false;
            return SendInput(1u, new[] { move }, InputSize) == 1u;
        }
        public static bool Up(string owner, ActionKey key) { return Send(owner, key, true); }

        private static int VirtualKey(ActionKey key)
        {
            switch (key)
            {
                case ActionKey.LeftSkill: return 0x01;
                case ActionKey.RightSkill: return 0x02;
                case ActionKey.Skill1: return BoundKey(ActionKey.Skill1, 0x31);
                case ActionKey.Skill2: return BoundKey(ActionKey.Skill2, 0x32);
                case ActionKey.Skill3: return BoundKey(ActionKey.Skill3, 0x33);
                case ActionKey.Skill4: return BoundKey(ActionKey.Skill4, 0x34);
                default: return 0;
            }
        }

        private static int BoundKey(ActionKey key, int fallback)
        {
            try
            {
                var autoSkill = ResolveAutoSkill();
                if (autoSkill == null) return fallback;
                ushort value = autoSkill.GetCastVirtualKey(key);
                return value == 0 ? fallback : value;
            }
            catch { return fallback; }
        }

        private static bool Send(string owner, ActionKey key, bool up, bool aimed = false, int screenX = 0, int screenY = 0)
        {
            int code;
            if (up)
            {
                if (!OwnedActions.TryGetValue(key, out code)) return true;
            }
            else
            {
                code = VirtualKey(key);
            }

            if (code == 0) return false;
            if (!up && IsDown(key)) return false;
            if (!up && key == ActionKey.LeftSkill)
            {
                // An atomic move+DOWN must validate its destination, not a stale pre-move cursor.
                bool safe = aimed
                    ? s7o_ImpaleInputContext.CanPressLeftSkillAt != null
                        && s7o_ImpaleInputContext.CanPressLeftSkillAt(screenX, screenY)
                    : s7o_ImpaleInputContext.CanPressLeftSkill != null
                        && s7o_ImpaleInputContext.CanPressLeftSkill();
                if (!safe) return false;
            }

            Input input = new Input();
            bool mouse = key == ActionKey.LeftSkill || key == ActionKey.RightSkill;
            input.Type = mouse ? 0u : 1u;
            if (mouse)
                input.U.Mouse.Flags = key == ActionKey.LeftSkill
                    ? (up ? 0x0004u : 0x0002u) : (up ? 0x0010u : 0x0008u);
            else
            {
                input.U.Keyboard.VirtualKey = (ushort)code;
                input.U.Keyboard.Flags = up ? 2u : 0u;
            }

            int identity = mouse ? (key == ActionKey.LeftSkill ? 0x10001 : 0x10002) : code;
            Input move = new Input();
            if (aimed && (up || !TryBuildAbsoluteMove(screenX, screenY, out move))) return false;
            Input[] packet = aimed ? new[] { move, input } : new[] { input };
            uint count = (uint)packet.Length;
            Func<bool> emit = () => SendInput(count, packet, InputSize) == count;

            if (up)
            {
                bool released = s7o_InputReleaseArbiter.Up(owner, identity, emit);
                OwnedActions.Remove(key); // Arbiter owns retries after a failed UP.
                return released;
            }

            bool acquired = s7o_InputReleaseArbiter.Down(owner, identity, emit);
            if (acquired) OwnedActions[key] = code;
            return acquired;
        }
    }

    internal static class s7o_ImpaleInputContext
    {
        public static IController Hud;
        public static Func<bool> CanPressLeftSkill;
        public static Func<int, int, bool> CanPressLeftSkillAt;
    }
}
