using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeusBotAI
{
    // A dedicated memory class for each bot to track its combat state smoothly
    public class CombatState
    {
        public bool IsEngaging { get; set; } = false;
        public bool IsReacting { get; set; } = false;
        public ulong CurrentStrafeKey { get; set; } = 0;
        public float NextStrafeSwitch { get; set; } = 0f;
        public ulong CurrentMovementMask { get; set; } = 0;
        public float JumpCooldown { get; set; } = 0f;
    }

    public class ZeusBotAIPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Advanced Combat State Machine)";
        public override string ModuleVersion => "2.0.1";
        
        private CounterStrikeSharp.API.Modules.Timers.Timer? brainTimer;
        private readonly Dictionary<uint, CombatState> botMemory = new Dictionary<uint, CombatState>();
        private readonly Random random = new Random();

        public override void Load(bool hotReload)
        {
            // The Brain: Processes situational awareness 10 times a second
            brainTimer = AddTimer(0.1f, ProcessBotBrains, TimerFlags.REPEAT);
            
            // The Nervous System: Injects physical movement keystrokes 64 times a second
            RegisterListener<Listeners.OnTick>(InjectMovementPhysics);
            
            Console.WriteLine("[Zeus Bot AI] v2.0 Tactical State Machine loaded. Jitter fixed.");
        }

        private void ProcessBotBrains()
        {
            var players = Utilities.GetPlayers();
            var bots = players.Where(p => p != null && p.IsValid && p.IsBot && p.PawnIsAlive).ToList();
            var aliveEnemies = players.Where(p => p != null && p.IsValid && p.PawnIsAlive && !p.IsBot).ToList();

            if (!bots.Any()) return;

            float currentTime = Server.CurrentTime;

            foreach (var bot in bots)
            {
                var botPawn = bot.PlayerPawn.Value;
                if (botPawn == null) continue;

                if (!botMemory.TryGetValue(bot.Index, out var memory))
                {
                    memory = new CombatState();
                    botMemory[bot.Index] = memory;
                }

                var target = GetHighestPriorityTarget(bot, botPawn, aliveEnemies);
                
                // If no valid target is nearby, release movement control to the default CS2 Bot AI
                if (target == null) 
                {
                    memory.IsEngaging = false;
                    memory.CurrentMovementMask = 0;
                    continue;
                }

                var targetPawn = target.PlayerPawn.Value;
                if (targetPawn == null) continue;

                var botOrigin = botPawn.AbsOrigin;
                var targetOrigin = targetPawn.AbsOrigin;
                if (botOrigin == null || targetOrigin == null) continue;

                float distance = (botOrigin - targetOrigin).Length();

                if (distance <= 600.0f)
                {
                    memory.IsEngaging = true;
                    EnsureBotHasAndHoldsZeus(bot, botPawn);

                    ulong newMask = 0;

                    // --- STRAFE DIRECTION LOGIC ---
                    // Randomly switch left/right strafing to be unpredictable (every 0.4 to 1.5 seconds)
                    if (currentTime > memory.NextStrafeSwitch)
                    {
                        memory.CurrentStrafeKey = random.NextDouble() > 0.5 ? (ulong)PlayerButtons.Moveleft : (ulong)PlayerButtons.Moveright;
                        memory.NextStrafeSwitch = currentTime + (random.NextSingle() * 1.1f + 0.4f);
                    }
                    newMask |= memory.CurrentStrafeKey;

                    // --- TACTICAL DISTANCE MANAGEMENT ---
                    if (distance > 250.0f)
                    {
                        // Serpentine Approach: Close the gap fast but zig-zag
                        newMask |= (ulong)PlayerButtons.Forward;
                    }
                    else if (distance < 130.0f)
                    {
                        // Tactical Retreat: Kiting backward if the human pushes too hard
                        newMask |= (ulong)PlayerButtons.Back;
                    }
                    // If distance is between 130 and 250, do not press W or S. purely circle-strafe using the A/D keys assigned above.

                    // --- EVASIVE MANEUVERS (Bunnyhopping & Duck-peeking) ---
                    if (currentTime > memory.JumpCooldown && random.NextDouble() < 0.15)
                    {
                        newMask |= (ulong)PlayerButtons.Jump;
                        memory.JumpCooldown = currentTime + 1.2f; // Don't spam jump too fast
                        
                        // Mid-air crouch injection
                        AddTimer(0.15f, () => {
                            if (bot.IsValid && bot.PlayerPawn.Value?.MovementServices != null)
                                bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Duck;
                        });
                    }

                    memory.CurrentMovementMask = newMask;

                    // --- THE KILL SHOT ---
                    if (distance <= 170.0f && !memory.IsReacting)
                    {
                        ExecuteFlickShot(bot, botPawn, targetPawn, memory);
                    }
                }
                else
                {
                    memory.IsEngaging = false;
                    memory.CurrentMovementMask = 0;
                }
            }
        }

        private void InjectMovementPhysics()
        {
            foreach (var kvp in botMemory)
            {
                var memory = kvp.Value;
                if (!memory.IsEngaging || memory.CurrentMovementMask == 0) continue; 

                var bot = Utilities.GetPlayerFromIndex((int)kvp.Key);
                if (bot != null && bot.IsValid && bot.PawnIsAlive && bot.PlayerPawn.Value?.MovementServices != null)
                {
                    // 1. Strip the default Bot AI's movement intentions
                    bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] &= ~((ulong)PlayerButtons.Forward | (ulong)PlayerButtons.Back | (ulong)PlayerButtons.Moveleft | (ulong)PlayerButtons.Moveright);
                    
                    // 2. Inject our State Machine's continuous movement mask
                    bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] |= memory.CurrentMovementMask;
                }
            }
        }

        private void ExecuteFlickShot(CCSPlayerController bot, CCSPlayerPawn botPawn, CCSPlayerPawn targetPawn, CombatState memory)
        {
            memory.IsReacting = true;

            var botPos = botPawn.AbsOrigin;
            var targetPos = targetPawn.AbsOrigin;
            var botAngles = botPawn.EyeAngles;

            if (botPos == null || targetPos == null || botAngles == null)
            {
                memory.IsReacting = false;
                return;
            }

            float deltaX = targetPos.X - botPos.X;
            float deltaY = targetPos.Y - botPos.Y;
            float perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);

            float currentYaw = botAngles.Y;
            float yawDifference = Math.Abs(perfectYaw - currentYaw);
            if (yawDifference > 180.0f) yawDifference = 360.0f - yawDifference;

            // Fluid flick timing based on degree of angle change
            float baseReaction = (random.NextSingle() * 0.08f) + 0.08f;
            float flickPenalty = (yawDifference / 180.0f) * 0.12f; 
            float reactionTime = baseReaction + flickPenalty;

            AddTimer(reactionTime, () =>
            {
                memory.IsReacting = false;

                if (!bot.IsValid || !bot.PawnIsAlive || !targetPawn.IsValid) return;

                var newBotPos = botPawn.AbsOrigin;
                var newTargetPos = targetPawn.AbsOrigin;
                if (newBotPos == null || newTargetPos == null) return;

                float distance = (newBotPos - newTargetPos).Length();
                if (distance > 185.0f) return; // The human strafed out of range during our reaction time! Cancel shot.

                deltaX = newTargetPos.X - newBotPos.X;
                deltaY = newTargetPos.Y - newBotPos.Y;
                float deltaZ = (newTargetPos.Z + 40.0f) - (newBotPos.Z + 40.0f); 

                perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);
                float perfectPitch = (float)(Math.Atan2(-deltaZ, Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) * 180.0 / Math.PI);

                float inaccuracyScale = 1.0f + ((yawDifference / 180.0f) * 2.5f);
                float panicYaw = perfectYaw + ((random.NextSingle() * (inaccuracyScale * 2)) - inaccuracyScale);
                float panicPitch = perfectPitch + ((random.NextSingle() * (inaccuracyScale * 2)) - inaccuracyScale);

                var newAngles = new QAngle(panicPitch, panicYaw, 0);
                
                // THE FIX: Pass NULL to position and velocity. 
                // This updates their aim instantly without killing their momentum!
                botPawn.Teleport(null, newAngles, null);

                if (botPawn.MovementServices != null)
                {
                    botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Attack;
                    
                    AddTimer(0.05f, () => 
                    { 
                        if (bot.IsValid && bot.PlayerPawn.Value?.MovementServices != null)
                        {
                            bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Attack; 
                        }
                    });
                }
            });
        }

        // ... [GetHighestPriorityTarget, EnsureBotHasAndHoldsZeus, DotProduct, etc. remain identical to the previous version]
        
        private void EnsureBotHasAndHoldsZeus(CCSPlayerController bot, CCSPlayerPawn botPawn)
        {
            var weaponServices = botPawn.WeaponServices;
            if (weaponServices == null) return;

            bool hasZeus = false;
            uint taserHandleRaw = 0;

            if (weaponServices.MyWeapons != null)
            {
                foreach (var weaponHandle in weaponServices.MyWeapons)
                {
                    var weapon = weaponHandle.Value;
                    if (weapon != null && weapon.DesignerName != null && weapon.DesignerName.Contains("taser"))
                    {
                        hasZeus = true;
                        taserHandleRaw = weaponHandle.Raw;
                        break;
                    }
                }
            }

            if (!hasZeus)
            {
                bot.GiveNamedItem("weapon_taser");
            }
            else
            {
                var activeWeapon = weaponServices.ActiveWeapon.Value;
                if (activeWeapon != null && activeWeapon.DesignerName != null && !activeWeapon.DesignerName.Contains("taser"))
                {
                    weaponServices.ActiveWeapon.Raw = taserHandleRaw;
                    Utilities.SetStateChanged(botPawn, "CBasePlayerPawn", "m_pWeaponServices");
                }
            }
        }

        private CCSPlayerController? GetHighestPriorityTarget(CCSPlayerController bot, CCSPlayerPawn botPawn, List<CCSPlayerController> enemies)
        {
            CCSPlayerController? bestTarget = null;
            float highestThreatScore = -float.MaxValue;

            var botPos = botPawn.AbsOrigin;
            var botAngles = botPawn.EyeAngles;
            if (botPos == null || botAngles == null) return null;

            Vector botForward = GetForwardVector(botAngles);

            foreach (var enemy in enemies)
            {
                if (enemy.TeamNum == bot.TeamNum) continue;
                
                var enemyPawn = enemy.PlayerPawn.Value;
                if (enemyPawn == null) continue;
                
                var enemyPos = enemyPawn.AbsOrigin;
                var enemyAngles = enemyPawn.EyeAngles;
                if (enemyPos == null || enemyAngles == null) continue;

                float distance = (botPos - enemyPos).Length();
                if (distance > 1500.0f) continue; 

                float threatScore = (1500.0f - distance);

                Vector dirToEnemy = GetNormalizedVector(botPos, enemyPos);
                Vector dirToBot = GetNormalizedVector(enemyPos, botPos);
                Vector enemyForward = GetForwardVector(enemyAngles);

                float enemyDot = DotProduct(enemyForward, dirToBot);
                if (enemyDot > 0.85f) threatScore += 1200.0f; 
                else if (enemyDot > 0.5f) threatScore += 400.0f; 

                float botDot = DotProduct(botForward, dirToEnemy);
                if (botDot > 0.7f) threatScore += 400.0f; 
                else if (botDot < 0.0f) threatScore -= 300.0f; 

                var weaponServices = enemyPawn.WeaponServices;
                if (weaponServices?.ActiveWeapon?.Value != null)
                {
                    string weaponName = weaponServices.ActiveWeapon.Value.DesignerName ?? "";
                    if (weaponName.Contains("grenade") || weaponName.Contains("flashbang") || weaponName.Contains("smokegrenade") || weaponName.Contains("c4"))
                        threatScore -= 600.0f; 
                    else if (weaponName.Contains("knife"))
                        threatScore -= 150.0f; 
                    else 
                        threatScore += 300.0f; 
                }

                if (distance <= 200.0f) threatScore += 3000.0f; 

                if (threatScore > highestThreatScore)
                {
                    highestThreatScore = threatScore;
                    bestTarget = enemy;
                }
            }
            return bestTarget;
        }

        private Vector GetForwardVector(QAngle angles)
        {
            float pitchRad = angles.X * (float)(Math.PI / 180.0);
            float yawRad = angles.Y * (float)(Math.PI / 180.0);
            return new Vector(
                (float)(Math.Cos(yawRad) * Math.Cos(pitchRad)),
                (float)(Math.Sin(yawRad) * Math.Cos(pitchRad)),
                (float)(-Math.Sin(pitchRad))
            );
        }

        private Vector GetNormalizedVector(Vector from, Vector to)
        {
            Vector dir = new Vector(to.X - from.X, to.Y - from.Y, to.Z - from.Z);
            float length = (float)Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
            if (length == 0) return new Vector(0, 0, 0);
            dir.X /= length;
            dir.Y /= length;
            dir.Z /= length;
            return dir;
        }

        private float DotProduct(Vector v1, Vector v2)
        {
            return (v1.X * v2.X) + (v1.Y * v2.Y) + (v1.Z * v2.Z);
        }

        public override void Unload(bool hotReload)
        {
            brainTimer?.Kill();
            brainTimer = null;
            botMemory.Clear();
            RemoveListener<Listeners.OnTick>(InjectMovementPhysics);
        }
    }
}
