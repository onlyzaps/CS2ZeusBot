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
        public float FearEndTime { get; set; } = 0f;
    }

    public class ZeusBotAIPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Context Steering & Pro Sweeps)";
        public override string ModuleVersion => "8.0.0";
        
        private CounterStrikeSharp.API.Modules.Timers.Timer? brainTimer;
        private readonly Dictionary<uint, CombatState> botMemory = new Dictionary<uint, CombatState>();
        private readonly Random random = new Random();

        public override void Load(bool hotReload)
        {
            brainTimer = AddTimer(0.1f, ProcessBotBrains, TimerFlags.REPEAT);
            RegisterListener<Listeners.OnTick>(InjectKinematicsAndAim);
            Console.WriteLine("[Zeus Bot AI] v8.0 Pro Context Steering loaded (Wide Swings & Tactical Hops).");
        }

        private void ProcessBotBrains()
        {
            var players = Utilities.GetPlayers();
            var bots = players.Where(p => p != null && p.IsValid && p.IsBot && p.PawnIsAlive).ToList();
            var aliveEnemies = players.Where(p => p != null && p.IsValid && p.PawnIsAlive && p.PlayerPawn.Value != null && p.PlayerPawn.Value.Health > 0 && !p.IsBot).ToList();

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
                    memory.CurrentAimSpeed = (random.NextSingle() * 0.08f) + 0.12f; // Smooth, precise micro-adjustments
                }

                // WIDE SWINGS: Less erratic shifting. Pros commit to strafes to cross your screen.
                if (currentTime > memory.NextStrafeSwitch)
                {
                    memory.StrafeDirection = random.NextDouble() > 0.5 ? 1.0f : -1.0f;
                    // Hold strafes for 1.5 to 3.0 seconds unless interrupted by fear
                    memory.NextStrafeSwitch = currentTime + (random.NextSingle() * 1.5f + 1.5f);
                }

                // SWARM INTELLIGENCE: Repulsion Fields (prevents bots from clumping)
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
                        if (distToSecondary < 400.0f)
                        {
                            Vector dirAway = GetNormalizedVector(enemyPawn.AbsOrigin, botPos);
                            float weight = (float)Math.Pow(1.0f - (distToSecondary / 400.0f), 2.0) * 1.2f; 
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
                    if (targetPawn == null || targetPawn.Health <= 0) 
                    {
                        memory.CurrentTarget = null;
                        continue;
                    }

                    var botPos = botPawn.AbsOrigin;
                    var targetPos = targetPawn.AbsOrigin;
                    var botAngles = botPawn.EyeAngles;
                    var targetAngles = targetPawn.EyeAngles; 

                    if (botPos == null || targetPos == null || botAngles == null || targetAngles == null) continue;

                    float distance = (botPos - targetPos).Length();
                    Vector currentVel = new Vector(botPawn.AbsVelocity.X, botPawn.AbsVelocity.Y, botPawn.AbsVelocity.Z);
                    Vector pursuitDir = GetNormalizedVector(botPos, targetPos);
                    Vector strafeDir = new Vector(-pursuitDir.Y * memory.StrafeDirection, pursuitDir.X * memory.StrafeDirection, 0);
                    Vector finalMoveDir = new Vector(0, 0, 0);
                    float moveSpeed = 250.0f; 

                    bool isAirborne = (botPawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0;
                    botPawn.MovementServices.Buttons.ButtonStates[0] = 0; 

                    // --- CONTEXT STEERING BEHAVIORS ---
                    Vector targetForward = GetForwardVector(targetAngles);
                    Vector dirToBot = GetNormalizedVector(targetPos, botPos);
                    float playerAimDot = DotProduct(targetForward, dirToBot);

                    // THREAT DETECTION: Are you aiming at them?
                    if (playerAimDot > 0.95f && distance < 1000.0f && currentTime > memory.FearEndTime)
                    {
                        memory.FearEndTime = currentTime + 0.8f; 
                        // Intelligent Evasion: If you aim at them, they instantly switch strafe to cross your crosshair
                        memory.StrafeDirection = (random.NextDouble() > 0.5) ? 1.0f : -1.0f; 
                        memory.NextStrafeSwitch = currentTime + 2.0f;
                    }

                    bool isAfraid = currentTime < memory.FearEndTime;

                    // --- THE GOLDILOCKS JUMP MATRIX ---
                    if (!isAirborne && currentTime > memory.JumpCooldown)
                    {
                        bool jumpExecuted = false;

                        // 1. Evasive Jump (Dodging Crosshair)
                        if (isAfraid && distance < 600.0f && random.NextDouble() < 0.35)
                        {
                            jumpExecuted = true;
                        }
                        // 2. Aggressive Gap-Closing (The Ferrari Peek)
                        else if (!isAfraid && distance > 300.0f && distance < 700.0f && random.NextDouble() < 0.20)
                        {
                            jumpExecuted = true;
                        }

                        if (jumpExecuted)
                        {
                            botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Jump;
                            memory.JumpCooldown = currentTime + (random.NextSingle() * 0.8f + 0.8f); // 0.8s to 1.6s cooldown
                            memory.DuckReleaseTime = currentTime + 0.5f; 
                        }
                    }

                    // --- INTELLIGENT VECTOR BLENDING ---
                    if (isAfraid)
                    {
                        // EVASION VECTOR: Move perpendicular to the player's LOOK direction, not just their position.
                        Vector evadeDir = new Vector(-targetForward.Y * memory.StrafeDirection, targetForward.X * memory.StrafeDirection, 0);
                        finalMoveDir = new Vector((evadeDir.X * 1.5f) + (-pursuitDir.X * 0.4f), (evadeDir.Y * 1.5f) + (-pursuitDir.Y * 0.4f), 0);
                    }
                    else if (distance > 350.0f)
                    {
                        // SEEK VECTOR (Aggressive Push): Spiral inwards rapidly.
                        finalMoveDir = new Vector((pursuitDir.X * 0.7f) + (strafeDir.X * 0.6f), (pursuitDir.Y * 0.7f) + (strafeDir.Y * 0.6f), 0);
                    }
                    else if (distance > 180.0f)
                    {
                        // ORBIT VECTOR (The Duel): Pure, wide sweeping strafes at the perfect engagement distance.
                        finalMoveDir = new Vector((pursuitDir.X * 0.1f) + (strafeDir.X * 1.0f), (pursuitDir.Y * 0.1f) + (strafeDir.Y * 1.0f), 0);
                    }
                    else
                    {
                        // RETREAT VECTOR: Too close! Backpedal while strafing so they don't overrun you.
                        finalMoveDir = new Vector((-pursuitDir.X * 0.5f) + (strafeDir.X * 0.8f), (-pursuitDir.Y * 0.5f) + (strafeDir.Y * 0.8f), 0);
                    }

                    if (isAirborne)
                    {
                        // True Air-Strafing: Curve the momentum smoothly into the target vector
                        finalMoveDir = new Vector(finalMoveDir.X * 1.4f, finalMoveDir.Y * 1.4f, 0);
                    }

                    finalMoveDir.X += memory.RepulsionForce.X;
                    finalMoveDir.Y += memory.RepulsionForce.Y;
                    finalMoveDir = NormalizeVector(finalMoveDir);
                    
                    Vector injectedVelocity = new Vector(finalMoveDir.X * moveSpeed, finalMoveDir.Y * moveSpeed, currentVel.Z);

                    // --- PRO-LEVEL AIM TRACKING ---
                    float deltaX = targetPos.X - botPos.X;
                    float deltaY = targetPos.Y - botPos.Y;
                    float deltaZ = (targetPos.Z + 50.0f) - (botPos.Z + 50.0f); 

                    float perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);
                    float perfectPitch = (float)(Math.Atan2(-deltaZ, Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) * 180.0 / Math.PI);

                    perfectPitch = Math.Clamp(perfectPitch, -89.0f, 89.0f);

                    float currentYaw = botAngles.Y;
                    float currentPitch = botAngles.X;

                    float newYaw = currentYaw + NormalizeAngle(perfectYaw - currentYaw) * memory.CurrentAimSpeed;
                    float newPitch = currentPitch + NormalizeAngle(perfectPitch - currentPitch) * memory.CurrentAimSpeed;
                    
                    newPitch = Math.Clamp(newPitch, -89.0f, 89.0f); 
                    
                    var smoothedAngles = new QAngle(newPitch, newYaw, 0);
                    botPawn.Teleport(null, smoothedAngles, injectedVelocity);

                    float yawDiff = Math.Abs(NormalizeAngle(perfectYaw - newYaw));
                    float pitchDiff = Math.Abs(NormalizeAngle(perfectPitch - newPitch));
                    
                    // Hitbox manipulation: Tuck legs during tactical hops
                    if (currentTime < memory.DuckReleaseTime)
                    {
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Duck;
                    }

                    // TRIGGER DISCIPLINE: Only fire when deeply lined up in the "Kill Zone"
                    if (distance <= 180.0f && yawDiff < 4.0f && pitchDiff < 6.0f && currentTime > memory.TargetAcquiredTime + 0.1f)
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
                if (enemyPawn == null || enemyPawn.Health <= 0) continue;
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
