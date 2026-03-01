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
        public float CurrentAimSpeed { get; set; } = 0.15f; 
        public Vector RepulsionForce { get; set; } = new Vector(0, 0, 0);
        public float FearEndTime { get; set; } = 0f;
        public Vector LastPosition { get; set; } = new Vector(0, 0, 0);
        public float LastStuckCheckTime { get; set; } = 0f;
        public Vector EscapeVector { get; set; } = new Vector(0, 0, 0);
        public float EscapeEndTime { get; set; } = 0f;
        public float NextFireTime { get; set; } = 0f; 
        
        // --- NEW: LADDER LOGIC MEMORY ---
        public bool WasOnLadder { get; set; } = false;
        public float LadderClearTime { get; set; } = 0f;
        public Vector LadderClearDir { get; set; } = new Vector(0, 0, 0);
    }

    public class ZeusBotAIPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Ladder Logic Fix)";
        public override string ModuleVersion => "16.0.1";
        
        private CounterStrikeSharp.API.Modules.Timers.Timer? brainTimer;
        private readonly Dictionary<uint, CombatState> botMemory = new Dictionary<uint, CombatState>();
        private readonly Random random = new Random();

        public override void Load(bool hotReload)
        {
            Server.ExecuteCommand("bot_dont_shoot 0"); 
            brainTimer = AddTimer(0.1f, ProcessBotBrains, TimerFlags.REPEAT);
            RegisterListener<Listeners.OnTick>(InjectKinematicsAndAim);
            Console.WriteLine("[Zeus Bot AI] v16.0 Ladder Logic Fix loaded.");
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
                    if (botPawn.AbsOrigin != null)
                        memory.LastPosition = new Vector(botPawn.AbsOrigin.X, botPawn.AbsOrigin.Y, botPawn.AbsOrigin.Z);
                    botMemory[bot.Index] = memory;
                }

                var target = GetHighestPriorityTarget(bot, botPawn, aliveEnemies);
                
                if (target == null) 
                {
                    memory.CurrentTarget = null;
                }
                else if (memory.CurrentTarget != target)
                {
                    memory.CurrentTarget = target;
                    memory.TargetAcquiredTime = currentTime;
                    memory.CurrentAimSpeed = (random.NextSingle() * 0.08f) + 0.14f; 
                }

                if (currentTime > memory.NextStrafeSwitch)
                {
                    memory.StrafeDirection = random.NextDouble() > 0.5 ? 1.0f : -1.0f;
                    memory.NextStrafeSwitch = currentTime + (random.NextSingle() * 1.5f + 1.0f);
                }

                Vector totalRepulsion = new Vector(0, 0, 0);
                var botPos = botPawn.AbsOrigin;

                if (botPos != null)
                {
                    foreach (var otherBot in bots)
                    {
                        if (otherBot == bot) continue; 
                        var otherPawn = otherBot.PlayerPawn.Value;
                        if (otherPawn == null || otherPawn.AbsOrigin == null) continue;

                        float distToAlly = (botPos - otherPawn.AbsOrigin).Length();
                        
                        if (distToAlly < 350.0f)
                        {
                            Vector dirAway = GetNormalizedVector(otherPawn.AbsOrigin, botPos);
                            float weight = (float)Math.Pow(1.0f - (distToAlly / 350.0f), 2.0) * 1.5f; 
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
            var players = Utilities.GetPlayers();
            var bots = players.Where(p => p != null && p.IsValid && p.IsBot && p.PawnIsAlive).ToList();

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

                    Vector pursuitDir = GetNormalizedVector(botPos, targetPos);

                    // --- LADDER LOGIC OVERRIDE ---
                    bool isOnLadder = botPawn.MoveType == MoveType_t.MOVETYPE_LADDER;

                    if (isOnLadder)
                    {
                        memory.WasOnLadder = true;
                        
                        // Force them to look up or down depending on your elevation relative to them
                        float perfectPitch = (targetPos.Z > botPos.Z) ? -60.0f : 60.0f;
                        float newPitch = botAngles.X + NormalizeAngle(perfectPitch - botAngles.X) * memory.CurrentAimSpeed;
                        
                        // We teleport ONLY their aim. We do NOT inject velocity, which lets the engine climb naturally.
                        var smoothedAnglesLadder = new QAngle(newPitch, botAngles.Y, 0);
                        botPawn.Teleport(null, smoothedAnglesLadder, null);
                        
                        // Force the W key so they actively climb
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Forward;
                        
                        continue; // Skip the rest of the ground kinematics for this tick
                    }

                    if (memory.WasOnLadder)
                    {
                        // The bot just got off the ladder this tick
                        memory.WasOnLadder = false;
                        memory.LadderClearTime = currentTime + 0.8f; // Give them 0.8 seconds to walk away from the lip
                        memory.LadderClearDir = new Vector(pursuitDir.X, pursuitDir.Y, 0); // Lock vector forward
                        memory.NextStrafeSwitch = currentTime + 1.5f; // Delay strafing so they don't side-step back into the hole
                    }

                    float distance = (botPos - targetPos).Length();
                    Vector currentVel = new Vector(botPawn.AbsVelocity.X, botPawn.AbsVelocity.Y, botPawn.AbsVelocity.Z);
                    Vector strafeDir = new Vector(-pursuitDir.Y * memory.StrafeDirection, pursuitDir.X * memory.StrafeDirection, 0);
                    Vector finalMoveDir = new Vector(0, 0, 0);
                    float moveSpeed = 260.0f; 

                    bool isAirborne = ((botPawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0) || Math.Abs(currentVel.Z) > 10.0f;
                    
                    botPawn.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Attack;

                    // --- AGGRESSION JUMPING ---
                    bool isClearingLadder = currentTime < memory.LadderClearTime;
                    
                    if (!isAirborne && currentTime > memory.JumpCooldown && distance < 800.0f && !isClearingLadder)
                    {
                        if (random.NextDouble() < 0.04)
                        {
                            botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Jump;
                            memory.JumpCooldown = currentTime + 1.2f + (random.NextSingle() * 1.5f);
                        }
                    }

                    // --- WALL-SLIDE EVASION ---
                    if (currentTime > memory.LastStuckCheckTime + 0.4f && !isClearingLadder)
                    {
                        float distMoved = (botPos - memory.LastPosition).Length();
                        if (distMoved < 25.0f && !isAirborne && distance > 100.0f)
                        {
                            float slideDir = random.NextDouble() > 0.5 ? 1.0f : -1.0f;
                            memory.EscapeVector = new Vector(-pursuitDir.Y * slideDir, pursuitDir.X * slideDir, 0);
                            memory.EscapeEndTime = currentTime + 0.8f; 
                        }
                        memory.LastPosition = new Vector(botPos.X, botPos.Y, botPos.Z);
                        memory.LastStuckCheckTime = currentTime;
                    }

                    // --- PINCER FLANKING LOGIC ---
                    Vector pincerWrapDir = new Vector(0, 0, 0);
                    bool isPincering = false;
                    int pincerPartners = 0;

                    if (!isClearingLadder)
                    {
                        foreach (var ally in bots)
                        {
                            if (ally == bot) continue;
                            var allyPawn = ally.PlayerPawn.Value;
                            if (allyPawn == null || allyPawn.AbsOrigin == null) continue;

                            if (botMemory.TryGetValue(ally.Index, out var allyMem) && allyMem.CurrentTarget == memory.CurrentTarget)
                            {
                                float allyDist = (allyPawn.AbsOrigin - targetPos).Length();
                                
                                if (allyDist < distance + 300.0f) 
                                {
                                    Vector allyToTarget = GetNormalizedVector(allyPawn.AbsOrigin, targetPos);
                                    Vector perpAngle = new Vector(-allyToTarget.Y, allyToTarget.X, 0);
                                    Vector botToAlly = GetNormalizedVector(botPos, allyPawn.AbsOrigin);
                                    
                                    float sideCheck = DotProduct(perpAngle, botToAlly);
                                    
                                    if (sideCheck > 0) pincerWrapDir = new Vector(pincerWrapDir.X - perpAngle.X, pincerWrapDir.Y - perpAngle.Y, 0);
                                    else pincerWrapDir = new Vector(pincerWrapDir.X + perpAngle.X, pincerWrapDir.Y + perpAngle.Y, 0);
                                    
                                    isPincering = true;
                                    pincerPartners++;

                                    if (pincerPartners >= 1) break; 
                                }
                            }
                        }
                        if (isPincering) pincerWrapDir = NormalizeVector(pincerWrapDir);
                    }

                    // --- THREAT AWARENESS (Fear) ---
                    Vector targetForward = GetForwardVector(targetAngles);
                    Vector dirToBot = GetNormalizedVector(targetPos, botPos);
                    float playerAimDot = DotProduct(targetForward, dirToBot);

                    if (playerAimDot > 0.95f && distance < 1000.0f && currentTime > memory.FearEndTime && !isClearingLadder)
                    {
                        memory.FearEndTime = currentTime + 0.5f; 
                        memory.StrafeDirection = (random.NextDouble() > 0.5) ? 1.0f : -1.0f; 
                        memory.NextStrafeSwitch = currentTime + 1.5f;
                    }

                    bool isAfraid = currentTime < memory.FearEndTime;
                    bool isEscaping = currentTime < memory.EscapeEndTime;
                    float driftFactor = (float)Math.Sin(currentTime * 3.5f + bot.Index) * 0.4f;

                    // --- MASTER KINEMATIC BLENDER ---
                    if (isClearingLadder)
                    {
                        // Strict override: Walk directly away from the ladder to prevent falling back in
                        finalMoveDir = NormalizeVector(memory.LadderClearDir);
                    }
                    else if (isEscaping)
                    {
                        finalMoveDir = new Vector((memory.EscapeVector.X * 1.5f) + (pursuitDir.X * 0.2f), (memory.EscapeVector.Y * 1.5f) + (pursuitDir.Y * 0.2f), 0);
                    }
                    else if (isPincering && distance > 120.0f)
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 0.5f) + (pincerWrapDir.X * 1.0f) + (strafeDir.X * driftFactor), 
                                                  (pursuitDir.Y * 0.5f) + (pincerWrapDir.Y * 1.0f) + (strafeDir.Y * driftFactor), 0);
                    }
                    else if (isAfraid)
                    {
                        Vector evadeDir = new Vector(-targetForward.Y * memory.StrafeDirection, targetForward.X * memory.StrafeDirection, 0);
                        finalMoveDir = new Vector((evadeDir.X * 1.2f) + (pursuitDir.X * 0.3f), (evadeDir.Y * 1.2f) + (pursuitDir.Y * 0.3f), 0);
                    }
                    else if (distance > 130.0f) 
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 0.8f) + (strafeDir.X * driftFactor), (pursuitDir.Y * 0.8f) + (strafeDir.Y * driftFactor), 0);
                    }
                    else if (distance > 80.0f) 
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 0.1f) + (strafeDir.X * 1.0f), (pursuitDir.Y * 0.1f) + (strafeDir.Y * 1.0f), 0);
                    }
                    else 
                    {
                        finalMoveDir = new Vector((-pursuitDir.X * 0.3f) + (strafeDir.X * 0.8f), (-pursuitDir.Y * 0.3f) + (strafeDir.Y * 0.8f), 0);
                    }

                    if (isAirborne && !isClearingLadder) finalMoveDir = new Vector(finalMoveDir.X * 1.2f, finalMoveDir.Y * 1.2f, 0);

                    // Add Repulsion (only if not strictly clearing a ladder)
                    if (!isClearingLadder)
                    {
                        finalMoveDir.X += memory.RepulsionForce.X;
                        finalMoveDir.Y += memory.RepulsionForce.Y;
                    }
                    
                    finalMoveDir = NormalizeVector(finalMoveDir);
                    Vector injectedVelocity = new Vector(finalMoveDir.X * moveSpeed, finalMoveDir.Y * moveSpeed, currentVel.Z);

                    // --- PRO CROSSHAIR PLACEMENT ---
                    float deltaX = targetPos.X - botPos.X;
                    float deltaY = targetPos.Y - botPos.Y;
                    float deltaZ = (targetPos.Z + 50.0f) - (botPos.Z + 50.0f); 

                    float perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);
                    float perfectPitch = (float)(Math.Atan2(-deltaZ, Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) * 180.0 / Math.PI);

                    perfectPitch = Math.Clamp(perfectPitch, -15.0f, 15.0f);

                    float currentYaw = botAngles.Y;
                    float currentPitch = botAngles.X;

                    float newYaw = currentYaw + NormalizeAngle(perfectYaw - currentYaw) * memory.CurrentAimSpeed;
                    float newPitch = currentPitch + NormalizeAngle(perfectPitch - currentPitch) * memory.CurrentAimSpeed;
                    newPitch = Math.Clamp(newPitch, -15.0f, 15.0f); 
                    
                    var smoothedAngles = new QAngle(newPitch, newYaw, 0);
                    botPawn.Teleport(null, smoothedAngles, injectedVelocity);

                    // --- DYNAMIC TRIGGER CONE ---
                    float yawDiff = Math.Abs(NormalizeAngle(perfectYaw - newYaw));
                    float pitchDiff = Math.Abs(NormalizeAngle(perfectPitch - newPitch));
                    float allowedYawDiff = distance < 80.0f ? 25.0f : 15.0f;
                    float allowedPitchDiff = 15.0f;

                    if (currentTime >= memory.NextFireTime && distance <= 140.0f && yawDiff < allowedYawDiff && pitchDiff < allowedPitchDiff && currentTime > memory.TargetAcquiredTime + 0.1f)
                    {
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Attack;
                        memory.NextFireTime = currentTime + 0.3f;
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
            if (weaponServices == null || weaponServices.MyWeapons == null) return;

            bool hasZeus = false;

            foreach (var weaponHandle in weaponServices.MyWeapons)
            {
                var weapon = weaponHandle.Value;
                if (weapon != null && weapon.IsValid && weapon.DesignerName != null)
                {
                    if (weapon.DesignerName.Contains("taser"))
                    {
                        hasZeus = true;
                        if (weapon.Clip1 <= 0)
                        {
                            weapon.Clip1 = 1;
                            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_iClip1");
                        }
                    }
                }
            }

            if (!hasZeus)
            {
                bot.GiveNamedItem("weapon_taser");
            }

            foreach (var weaponHandle in weaponServices.MyWeapons)
            {
                var weapon = weaponHandle.Value;
                if (weapon != null && weapon.IsValid && weapon.DesignerName != null)
                {
                    string wName = weapon.DesignerName;
                    
                    if (!wName.Contains("taser") && 
                        !wName.Contains("c4") && 
                        !wName.Contains("knife") && 
                        !wName.Contains("bayonet") && 
                        !wName.Contains("grenade") && 
                        !wName.Contains("flashbang") && 
                        !wName.Contains("molotov") && 
                        !wName.Contains("decoy"))
                    {
                        weapon.Remove(); 
                    }
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
