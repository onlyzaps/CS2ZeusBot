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
        public float NextStrafeSwitch { get; set; } = 0f;
        public float StrafeDirection { get; set; } = 1.0f; 
        public float JumpCooldown { get; set; } = 0f;
        public float DuckReleaseTime { get; set; } = 0f;
        public float CurrentAimSpeed { get; set; } = 0.15f; 
        public Vector RepulsionForce { get; set; } = new Vector(0, 0, 0);
    }

    public class ZeusBotAIPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Chaotic Evasion & Air-Strafing)";
        public override string ModuleVersion => "6.0.1";
        
        private CounterStrikeSharp.API.Modules.Timers.Timer? brainTimer;
        private readonly Dictionary<uint, CombatState> botMemory = new Dictionary<uint, CombatState>();
        private readonly Random random = new Random();

        public override void Load(bool hotReload)
        {
            brainTimer = AddTimer(0.1f, ProcessBotBrains, TimerFlags.REPEAT);
            RegisterListener<Listeners.OnTick>(InjectKinematicsAndAim);
            Console.WriteLine("[Zeus Bot AI] v6.0 Chaotic Evasion & Air-Strafing loaded.");
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
                    memory.RepulsionForce = new Vector(0, 0, 0);
                    continue;
                }

                if (memory.CurrentTarget != target)
                {
                    memory.CurrentTarget = target;
                    memory.TargetAcquiredTime = currentTime;
                    memory.CurrentAimSpeed = (random.NextSingle() * 0.18f) + 0.12f; 
                }

                // Hyper-aggressive strafe flipping
                if (currentTime > memory.NextStrafeSwitch)
                {
                    memory.StrafeDirection = random.NextDouble() > 0.5 ? 1.0f : -1.0f;
                    // Much faster switching in close combat
                    float switchDelay = (botPawn.AbsOrigin != null && target.PlayerPawn.Value?.AbsOrigin != null && 
                                        (botPawn.AbsOrigin - target.PlayerPawn.Value.AbsOrigin).Length() < 250.0f) 
                                        ? (random.NextSingle() * 0.3f + 0.1f) 
                                        : (random.NextSingle() * 0.5f + 0.2f);
                    
                    memory.NextStrafeSwitch = currentTime + switchDelay;
                }

                // --- MULTI-TARGET REPULSION ---
                Vector totalRepulsion = new Vector(0, 0, 0);
                var botPos = botPawn.AbsOrigin;

                if (botPos != null)
                {
                    foreach (var enemy in aliveEnemies)
                    {
                        if (enemy == target) continue; 
                        
                        var enemyPawn = enemy.PlayerPawn.Value;
                        if (enemyPawn == null || enemyPawn.AbsOrigin == null) continue;

                        float distToSecondary = (botPos - enemyPawn.AbsOrigin).Length();
                        float maxRepelDistance = 600.0f;

                        if (distToSecondary < maxRepelDistance)
                        {
                            Vector dirAway = GetNormalizedVector(enemyPawn.AbsOrigin, botPos);
                            float weight = (float)Math.Pow(1.0f - (distToSecondary / maxRepelDistance), 2.0) * 1.5f; 
                            totalRepulsion.X += dirAway.X * weight;
                            totalRepulsion.Y += dirAway.Y * weight;
                        }
                    }
                }
                memory.RepulsionForce = totalRepulsion;

                EnsureBotHasAndHoldsZeus(bot, botPawn);
            }
        }

        private void InjectKinematicsAndAim()
        {
            float currentTime = Server.CurrentTime;

            foreach (var kvp in botMemory)
            {
                var memory = kvp.Value;
                var bot = Utilities.GetPlayerFromIndex((int)kvp.Key);
                
                if (bot == null || !bot.IsValid || !bot.PawnIsAlive) continue;
                
                var botPawn = bot.PlayerPawn.Value;
                if (botPawn?.MovementServices == null || botPawn.AbsVelocity == null) continue;

                if (memory.CurrentTarget != null && memory.CurrentTarget.IsValid && memory.CurrentTarget.PawnIsAlive)
                {
                    var targetPawn = memory.CurrentTarget.PlayerPawn.Value;
                    if (targetPawn == null) continue;

                    var botPos = botPawn.AbsOrigin;
                    var targetPos = targetPawn.AbsOrigin;
                    var botAngles = botPawn.EyeAngles;

                    if (botPos == null || targetPos == null || botAngles == null) continue;

                    float distance = (botPos - targetPos).Length();
                    Vector currentVel = new Vector(botPawn.AbsVelocity.X, botPawn.AbsVelocity.Y, botPawn.AbsVelocity.Z);
                    Vector pursuitDir = GetNormalizedVector(botPos, targetPos);
                    Vector strafeDir = new Vector(-pursuitDir.Y * memory.StrafeDirection, pursuitDir.X * memory.StrafeDirection, 0);
                    Vector finalMoveDir = new Vector(0, 0, 0);
                    float moveSpeed = 250.0f; 

                    // --- CHAOTIC OSCILLATION (Lissajous curves) ---
                    // By multiplying sin and cos at different frequencies, the movement becomes highly erratic
                    float complexWeave = (float)(Math.Sin(currentTime * 14.0) * Math.Cos(currentTime * 7.0)) * 0.5f;

                    // Detect if airborne (Velocity Z is significantly non-zero)
                    bool isAirborne = Math.Abs(currentVel.Z) > 5.0f;

                    if (distance > 400.0f)
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 0.9f) + (strafeDir.X * 0.1f), (pursuitDir.Y * 0.9f) + (strafeDir.Y * 0.1f), 0);
                    }
                    else if (distance > 220.0f)
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 0.6f) + (strafeDir.X * 0.6f), (pursuitDir.Y * 0.6f) + (strafeDir.Y * 0.6f), 0);
                    }
                    else
                    {
                        // KILL ZONE: Evasive Air-Strafing & Stuttering
                        if (isAirborne)
                        {
                            // Mid-air: Increase strafe width aggressively to "hook" around the player
                            finalMoveDir = new Vector(
                                (strafeDir.X * 1.2f) + (pursuitDir.X * complexWeave * 0.5f), 
                                (strafeDir.Y * 1.2f) + (pursuitDir.Y * complexWeave * 0.5f), 
                                0
                            );
                        }
                        else
                        {
                            // Grounded: Heavy stutter-stepping and feinting
                            finalMoveDir = new Vector(
                                strafeDir.X + (pursuitDir.X * complexWeave), 
                                strafeDir.Y + (pursuitDir.Y * complexWeave), 
                                0
                            );
                        }
                    }

                    finalMoveDir.X += memory.RepulsionForce.X;
                    finalMoveDir.Y += memory.RepulsionForce.Y;
                    finalMoveDir = NormalizeVector(finalMoveDir);
                    
                    // Maintain vertical velocity for gravity and jumping
                    Vector injectedVelocity = new Vector(finalMoveDir.X * moveSpeed, finalMoveDir.Y * moveSpeed, currentVel.Z);

                    // --- FLUID AIM ---
                    float deltaX = targetPos.X - botPos.X;
                    float deltaY = targetPos.Y - botPos.Y;
                    // Aim slightly lower if they are airborne so the Zeus connects with the torso
                    float zOffset = isAirborne ? 30.0f : 45.0f; 
                    float deltaZ = (targetPos.Z + zOffset) - (botPos.Z + 45.0f); 

                    float perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);
                    float perfectPitch = (float)(Math.Atan2(-deltaZ, Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) * 180.0 / Math.PI);

                    float currentYaw = botAngles.Y;
                    float currentPitch = botAngles.X;

                    float newYaw = currentYaw + NormalizeAngle(perfectYaw - currentYaw) * memory.CurrentAimSpeed;
                    float newPitch = currentPitch + NormalizeAngle(perfectPitch - currentPitch) * memory.CurrentAimSpeed;
                    var smoothedAngles = new QAngle(newPitch, newYaw, 0);

                    botPawn.Teleport(null, smoothedAngles, injectedVelocity);

                    // --- EVASIVE HOPPING & TRIGGER DISCIPLINE ---
                    float yawDiff = Math.Abs(NormalizeAngle(perfectYaw - newYaw));
                    botPawn.MovementServices.Buttons.ButtonStates[0] = 0; 

                    // KILL ZONE HOPPING LOGIC
                    if (distance <= 250.0f)
                    {
                        // 1. Spastic Jumping
                        if (currentTime > memory.JumpCooldown && !isAirborne && random.NextDouble() < 0.25) // 25% chance to jump per tick when off cooldown
                        {
                            botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Jump;
                            // Randomize cooldown so they don't perfectly rhythm-bounce
                            memory.JumpCooldown = currentTime + (random.NextSingle() * 0.4f + 0.3f); 
                            
                            // 2. Crouch-Jump execution: Tuck legs mid-air 100ms after jumping
                            memory.DuckReleaseTime = currentTime + (random.NextSingle() * 0.5f + 0.3f);
                            
                            // Force a strafe direction flip mid-jump to break tracking
                            memory.StrafeDirection *= -1.0f;
                            memory.NextStrafeSwitch = currentTime + 0.5f;
                        }

                        // 3. Maintain mid-air tuck
                        if (currentTime < memory.DuckReleaseTime || (isAirborne && random.NextDouble() < 0.8))
                        {
                            botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Duck;
                        }
                    }
                    else if (distance > 300.0f && currentTime > memory.JumpCooldown && random.NextDouble() < 0.05)
                    {
                        // Occasional distance gap-closing hops
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Jump;
                        memory.JumpCooldown = currentTime + 0.8f;
                    }

                    // PULL THE TRIGGER
                    if (distance <= 170.0f && yawDiff < 5.0f && currentTime > memory.TargetAcquiredTime + 0.15f)
                    {
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Attack;
                    }
                }
            }
        }

        private float NormalizeAngle(float angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
            return angle;
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
            return NormalizeVector(dir);
        }

        private Vector NormalizeVector(Vector vec)
        {
            float length = (float)Math.Sqrt(vec.X * vec.X + vec.Y * vec.Y + vec.Z * vec.Z);
            if (length == 0) return new Vector(0, 0, 0);
            return new Vector(vec.X / length, vec.Y / length, vec.Z / length);
        }

        private float DotProduct(Vector v1, Vector v2)
        {
            return (v1.X * v2.X) + (v1.Y * v2.Y) + (v1.Z * v2.Z);
        }

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

                if (distance <= 200.0f) threatScore += 3000.0f; 

                if (threatScore > highestThreatScore)
                {
                    highestThreatScore = threatScore;
                    bestTarget = enemy;
                }
            }
            return bestTarget;
        }

        public override void Unload(bool hotReload)
        {
            brainTimer?.Kill();
            brainTimer = null;
            botMemory.Clear();
            RemoveListener<Listeners.OnTick>(InjectKinematicsAndAim);
        }
    }
}
