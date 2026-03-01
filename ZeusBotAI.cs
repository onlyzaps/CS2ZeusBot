using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeusBotAI
{
    public class CombatState
    {
        public CCSPlayerController? CurrentTarget { get; set; }
        public float TargetAcquiredTime { get; set; } = 0f;
        public ulong CurrentStrafeKey { get; set; } = 0;
        public float NextStrafeSwitch { get; set; } = 0f;
        public float JumpCooldown { get; set; } = 0f;
        public float DuckReleaseTime { get; set; } = 0f;
        public ulong CurrentMovementMask { get; set; } = 0;
        
        // Dynamic aim speed to simulate human mouse movement
        public float CurrentAimSpeed { get; set; } = 0.15f; 
    }

    public class ZeusBotAIPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Fluid Aim & Aggressive Brawler)";
        public override string ModuleVersion => "3.0.1";
        
        private CounterStrikeSharp.API.Modules.Timers.Timer? brainTimer;
        private readonly Dictionary<uint, CombatState> botMemory = new Dictionary<uint, CombatState>();
        private readonly Random random = new Random();

        public override void Load(bool hotReload)
        {
            // The Brain: Target selection and macro-tactics (10Hz)
            brainTimer = AddTimer(0.1f, ProcessBotBrains, TimerFlags.REPEAT);
            
            // The Nervous System: Fluid aiming, trigger discipline, and movement physics (64Hz)
            RegisterListener<Listeners.OnTick>(InjectPhysicsAndAim);
            
            Console.WriteLine("[Zeus Bot AI] v3.0 Fluid Aim & Aggressive Pushing loaded.");
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
                
                if (target == null) 
                {
                    memory.CurrentTarget = null;
                    memory.CurrentMovementMask = 0;
                    continue;
                }

                // If this is a newly acquired target, register the reaction start time
                if (memory.CurrentTarget != target)
                {
                    memory.CurrentTarget = target;
                    memory.TargetAcquiredTime = currentTime;
                    // Randomize how fast this specific "mouse swipe" will be (7% to 20% smoothing per tick)
                    memory.CurrentAimSpeed = (random.NextSingle() * 0.13f) + 0.07f; 
                }

                var targetPawn = target.PlayerPawn.Value;
                if (targetPawn == null) continue;

                var botOrigin = botPawn.AbsOrigin;
                var targetOrigin = targetPawn.AbsOrigin;
                if (botOrigin == null || targetOrigin == null) continue;

                float distance = (botOrigin - targetOrigin).Length();

                if (distance <= 800.0f) // Extended awareness range for aggressive pushing
                {
                    EnsureBotHasAndHoldsZeus(bot, botPawn);
                    ulong newMask = 0;

                    // --- AGGRESSIVE STRAFE LOGIC ---
                    if (currentTime > memory.NextStrafeSwitch)
                    {
                        memory.CurrentStrafeKey = random.NextDouble() > 0.5 ? (ulong)PlayerButtons.Moveleft : (ulong)PlayerButtons.Moveright;
                        // Faster strafe swapping for a more erratic, "crackhead" playstyle
                        memory.NextStrafeSwitch = currentTime + (random.NextSingle() * 0.7f + 0.2f);
                    }
                    newMask |= memory.CurrentStrafeKey;

                    // --- RELENTLESS PUSH LOGIC ---
                    // Never back down unless literally colliding (distance < 50)
                    if (distance > 130.0f)
                    {
                        newMask |= (ulong)PlayerButtons.Forward; // Always hold W to close the gap
                    }
                    else if (distance < 50.0f)
                    {
                        newMask |= (ulong)PlayerButtons.Back; // Step back just enough to not get physically body-blocked
                    }

                    // --- HOPPY / BUNNYHOP LOGIC ---
                    // If far away, jump constantly to close the gap safely. If close, jump occasionally to throw off aim.
                    float jumpChance = distance > 250.0f ? 0.30f : 0.08f;
                    
                    if (currentTime > memory.JumpCooldown && random.NextDouble() < jumpChance)
                    {
                        newMask |= (ulong)PlayerButtons.Jump;
                        memory.JumpCooldown = currentTime + (random.NextSingle() * 0.5f + 0.5f); 
                        
                        // Add a mid-air crouch to tuck legs (harder to hit)
                        newMask |= (ulong)PlayerButtons.Duck;
                        memory.DuckReleaseTime = currentTime + 0.4f;
                    }

                    // Maintain ducking if currently in a mid-air tuck
                    if (currentTime < memory.DuckReleaseTime)
                    {
                        newMask |= (ulong)PlayerButtons.Duck;
                    }

                    memory.CurrentMovementMask = newMask;
                }
                else
                {
                    memory.CurrentTarget = null;
                    memory.CurrentMovementMask = 0;
                }
            }
        }

        private void InjectPhysicsAndAim()
        {
            float currentTime = Server.CurrentTime;

            foreach (var kvp in botMemory)
            {
                var memory = kvp.Value;
                var bot = Utilities.GetPlayerFromIndex((int)kvp.Key);
                
                if (bot == null || !bot.IsValid || !bot.PawnIsAlive) continue;
                
                var botPawn = bot.PlayerPawn.Value;
                if (botPawn?.MovementServices == null) continue;

                // 1. INJECT MOVEMENT
                if (memory.CurrentMovementMask != 0)
                {
                    botPawn.MovementServices.Buttons.ButtonStates[0] &= ~((ulong)PlayerButtons.Forward | (ulong)PlayerButtons.Back | (ulong)PlayerButtons.Moveleft | (ulong)PlayerButtons.Moveright | (ulong)PlayerButtons.Jump | (ulong)PlayerButtons.Duck);
                    botPawn.MovementServices.Buttons.ButtonStates[0] |= memory.CurrentMovementMask;
                }

                // 2. FLUID AIM INTERPOLATION
                if (memory.CurrentTarget != null && memory.CurrentTarget.IsValid && memory.CurrentTarget.PawnIsAlive)
                {
                    // Human reaction delay: Don't start tracking perfectly until 150ms after spotting them
                    if (currentTime < memory.TargetAcquiredTime + 0.15f) continue;

                    var targetPawn = memory.CurrentTarget.PlayerPawn.Value;
                    if (targetPawn == null) continue;

                    var botPos = botPawn.AbsOrigin;
                    var targetPos = targetPawn.AbsOrigin;
                    var botAngles = botPawn.EyeAngles;

                    if (botPos == null || targetPos == null || botAngles == null) continue;

                    // Calculate perfect angles to the target's upper chest
                    float deltaX = targetPos.X - botPos.X;
                    float deltaY = targetPos.Y - botPos.Y;
                    float deltaZ = (targetPos.Z + 45.0f) - (botPos.Z + 45.0f); 

                    float perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);
                    float perfectPitch = (float)(Math.Atan2(-deltaZ, Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) * 180.0 / Math.PI);

                    // LERP (Interpolate) the current view angles smoothly toward the perfect angles
                    float currentYaw = botAngles.Y;
                    float currentPitch = botAngles.X;

                    float newYaw = currentYaw + NormalizeAngle(perfectYaw - currentYaw) * memory.CurrentAimSpeed;
                    float newPitch = currentPitch + NormalizeAngle(perfectPitch - currentPitch) * memory.CurrentAimSpeed;

                    // Apply the new smoothly dragged angles (passing null for pos/vel preserves hoppy momentum!)
                    var smoothedAngles = new QAngle(newPitch, newYaw, 0);
                    botPawn.Teleport(null, smoothedAngles, null);

                    // 3. CROSSHAIR-TRIGGERED FIRING
                    // Instead of a timer, we actively check if the crosshair has swept over the target.
                    float distance = (botPos - targetPos).Length();
                    float yawDiff = Math.Abs(NormalizeAngle(perfectYaw - newYaw));
                    float pitchDiff = Math.Abs(NormalizeAngle(perfectPitch - newPitch));

                    // If within Zeus range AND the crosshair is within 5 degrees of the target's center...
                    if (distance <= 170.0f && yawDiff < 5.0f && pitchDiff < 5.0f)
                    {
                        // Pull the trigger!
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Attack;
                    }
                    else
                    {
                        // Release the trigger
                        botPawn.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Attack;
                    }
                }
            }
        }

        // Helper to ensure angles wrap correctly (-180 to 180) for shortest-path mouse swiping
        private float NormalizeAngle(float angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
            return angle;
        }

        // ... [GetHighestPriorityTarget, EnsureBotHasAndHoldsZeus, etc. remain here]

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
            RemoveListener<Listeners.OnTick>(InjectPhysicsAndAim);
        }
    }
}
