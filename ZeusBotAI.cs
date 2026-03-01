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
        public float CurrentAimSpeed { get; set; } = 0.16f; 
        public Vector RepulsionForce { get; set; } = new Vector(0, 0, 0);
        public Vector LastPosition { get; set; } = new Vector(0, 0, 0);
        public float LastStuckCheckTime { get; set; } = 0f;
        public Vector EscapeVector { get; set; } = new Vector(0, 0, 0);
        public float EscapeEndTime { get; set; } = 0f;
        
        // --- LADDER LOGIC MEMORY ---
        public bool WasOnLadder { get; set; } = false;
        public float LadderClearTime { get; set; } = 0f;
        public Vector LadderClearDir { get; set; } = new Vector(0, 0, 0);
    }

    public class ZeusBotAIPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Hybrid Weapon Forcing & High Aggression)";
        public override string ModuleVersion => "17.0.5";
        
        private CounterStrikeSharp.API.Modules.Timers.Timer? brainTimer;
        private readonly Dictionary<uint, CombatState> botMemory = new Dictionary<uint, CombatState>();
        private readonly Random random = new Random();

        public override void Load(bool hotReload)
        {
            Server.ExecuteCommand("bot_dont_shoot 0"); 
            brainTimer = AddTimer(0.1f, ProcessBotBrains, TimerFlags.REPEAT);
            RegisterListener<Listeners.OnTick>(InjectKinematicsAndAim);
            Console.WriteLine("[Zeus Bot AI] v17.0.5 Hybrid Weapon Forcing & High Aggression loaded.");
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
                float distToTarget = float.MaxValue;
                
                if (target == null) 
                {
                    memory.CurrentTarget = null;
                }
                else 
                {
                    if (memory.CurrentTarget != target)
                    {
                        memory.CurrentTarget = target;
                        memory.TargetAcquiredTime = currentTime;
                        memory.CurrentAimSpeed = (random.NextSingle() * 0.08f) + 0.16f; 
                    }
                    
                    if (botPawn.AbsOrigin != null && target.PlayerPawn.Value?.AbsOrigin != null)
                    {
                        distToTarget = (botPawn.AbsOrigin - target.PlayerPawn.Value.AbsOrigin).Length();
                    }
                }

                if (currentTime > memory.NextStrafeSwitch)
                {
                    memory.StrafeDirection = random.NextDouble() > 0.5 ? 1.0f : -1.0f;
                    memory.NextStrafeSwitch = currentTime + (random.NextSingle() * 1.0f + 0.5f); 
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
                        
                        if (distToAlly < 250.0f) 
                        {
                            Vector dirAway = GetNormalizedVector(otherPawn.AbsOrigin, botPos);
                            float weight = (float)Math.Pow(1.0f - (distToAlly / 250.0f), 2.0) * 1.2f; 
                            totalRepulsion.X += dirAway.X * weight;
                            totalRepulsion.Y += dirAway.Y * weight;
                        }
                    }
                }
                memory.RepulsionForce = totalRepulsion;

                // Handle the intelligent stripping and forcing
                ManageWeaponsAndForcing(bot, botPawn, target != null, distToTarget);
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
                        
                        float ladderPitchTarget = (targetPos.Z > botPos.Z) ? -60.0f : 60.0f;
                        float ladderSmoothedPitch = botAngles.X + NormalizeAngle(ladderPitchTarget - botAngles.X) * memory.CurrentAimSpeed;
                        
                        var smoothedAnglesLadder = new QAngle(ladderSmoothedPitch, botAngles.Y, 0);
                        botPawn.Teleport(null, smoothedAnglesLadder, null);
                        
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Forward;
                        continue; 
                    }

                    if (memory.WasOnLadder)
                    {
                        memory.WasOnLadder = false;
                        memory.LadderClearTime = currentTime + 0.8f; 
                        memory.LadderClearDir = new Vector(pursuitDir.X, pursuitDir.Y, 0); 
                        memory.NextStrafeSwitch = currentTime + 1.5f; 
                    }

                    float distance = (botPos - targetPos).Length();
                    Vector currentVel = new Vector(botPawn.AbsVelocity.X, botPawn.AbsVelocity.Y, botPawn.AbsVelocity.Z);
                    Vector strafeDir = new Vector(-pursuitDir.Y * memory.StrafeDirection, pursuitDir.X * memory.StrafeDirection, 0);
                    Vector finalMoveDir = new Vector(0, 0, 0);
                    float moveSpeed = 260.0f; 

                    bool isAirborne = ((botPawn.Flags & (uint)PlayerFlags.FL_ONGROUND) == 0) || Math.Abs(currentVel.Z) > 10.0f;
                    
                    botPawn.MovementServices.Buttons.ButtonStates[0] = 0;

                    // --- HIGH AGGRESSION JUMPING ---
                    bool isClearingLadder = currentTime < memory.LadderClearTime;
                    
                    if (currentTime > memory.JumpCooldown && distance < 1200.0f && !isClearingLadder)
                    {
                        if (random.NextDouble() < 0.06) 
                        {
                            botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Jump;
                            memory.JumpCooldown = currentTime + 0.8f + (random.NextSingle() * 1.0f); 
                            memory.DuckReleaseTime = currentTime + 0.4f;
                        }
                    }

                    if (currentTime < memory.DuckReleaseTime)
                    {
                        botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Duck;
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

                    bool isEscaping = currentTime < memory.EscapeEndTime;
                    float driftFactor = (float)Math.Sin(currentTime * 4.5f + bot.Index) * 0.3f; 

                    // --- HIGH AGGRESSION MASTER BLENDER ---
                    if (isClearingLadder)
                    {
                        finalMoveDir = NormalizeVector(memory.LadderClearDir);
                    }
                    else if (isEscaping)
                    {
                        finalMoveDir = new Vector((memory.EscapeVector.X * 1.5f) + (pursuitDir.X * 0.5f), (memory.EscapeVector.Y * 1.5f) + (pursuitDir.Y * 0.5f), 0);
                    }
                    else if (isPincering && distance > 150.0f)
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 0.8f) + (pincerWrapDir.X * 1.0f), 
                                                  (pursuitDir.Y * 0.8f) + (pincerWrapDir.Y * 1.0f), 0);
                    }
                    else if (distance > 180.0f) 
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 1.2f) + (strafeDir.X * driftFactor), (pursuitDir.Y * 1.2f) + (strafeDir.Y * driftFactor), 0);
                    }
                    else if (distance > 90.0f) 
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 1.0f) + (strafeDir.X * 0.4f), (pursuitDir.Y * 1.0f) + (strafeDir.Y * 0.4f), 0);
                    }
                    else 
                    {
                        finalMoveDir = new Vector((pursuitDir.X * 0.2f) + (strafeDir.X * 1.2f), (pursuitDir.Y * 0.2f) + (strafeDir.Y * 1.2f), 0);
                    }

                    if (isAirborne && !isClearingLadder) finalMoveDir = new Vector(finalMoveDir.X * 1.3f, finalMoveDir.Y * 1.3f, 0); 

                    if (!isClearingLadder)
                    {
                        finalMoveDir.X += memory.RepulsionForce.X;
                        finalMoveDir.Y += memory.RepulsionForce.Y;
                    }
                    
                    finalMoveDir = NormalizeVector(finalMoveDir);

                    // --- RAMP-LAUNCH FIX ---
                    float zVelocity = currentVel.Z;
                    if (!isAirborne && zVelocity > 0) 
                    {
                        zVelocity = 0; 
                    }

                    Vector injectedVelocity = new Vector(finalMoveDir.X * moveSpeed, finalMoveDir.Y * moveSpeed, zVelocity);

                    // --- PRO CROSSHAIR PLACEMENT ---
                    float deltaX = targetPos.X - botPos.X;
                    float deltaY = targetPos.Y - botPos.Y;
                    float deltaZ = (targetPos.Z + 50.0f) - (botPos.Z + 50.0f); 

                    float perfectPitch = (float)(Math.Atan2(-deltaZ, Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) * 180.0 / Math.PI);
                    float perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);

                    perfectPitch = Math.Clamp(perfectPitch, -15.0f, 15.0f);

                    float currentYaw = botAngles.Y;
                    float currentPitch = botAngles.X;

                    float newYaw = currentYaw + NormalizeAngle(perfectYaw - currentYaw) * memory.CurrentAimSpeed;
                    float newPitch = currentPitch + NormalizeAngle(perfectPitch - currentPitch) * memory.CurrentAimSpeed;
                    newPitch = Math.Clamp(newPitch, -15.0f, 15.0f); 
                    
                    var smoothedAngles = new QAngle(newPitch, newYaw, 0);
                    botPawn.Teleport(null, smoothedAngles, injectedVelocity);

                    // --- FIRE LOGIC ---
                    float yawDiff = Math.Abs(NormalizeAngle(perfectYaw - newYaw));
                    float pitchDiff = Math.Abs(NormalizeAngle(perfectPitch - newPitch));
                    
                    var activeWeapon = botPawn.WeaponServices?.ActiveWeapon.Value;
                    bool holdingTaser = activeWeapon != null && activeWeapon.DesignerName != null && activeWeapon.DesignerName.Contains("taser");

                    if (holdingTaser && distance <= 190.0f && yawDiff < 15.0f && pitchDiff < 15.0f && currentTime > memory.TargetAcquiredTime + 0.15f)
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

        // --- HYBRID WEAPON FORCING & INVENTORY MANAGEMENT ---
        private void ManageWeaponsAndForcing(CCSPlayerController bot, CCSPlayerPawn pawn, bool hasTarget, float distanceToTarget)
        {
            var weaponServices = pawn.WeaponServices;
            if (weaponServices?.MyWeapons == null) return;

            bool hasZeus = false;
            uint taserHandle = 0;
            uint knifeHandle = 0;
            List<CBasePlayerWeapon> weaponsToRemove = new List<CBasePlayerWeapon>();

            // 1. Identify and clean up inventory
            foreach (var weaponHandle in weaponServices.MyWeapons)
            {
                var weapon = weaponHandle.Value;
                if (weapon == null || weapon.DesignerName == null) continue;

                string name = weapon.DesignerName;

                if (name.Contains("taser"))
                {
                    if (weapon.Clip1 <= 0) 
                    {
                        // Trash empty tasers so the brain doesn't blacklist them
                        weaponsToRemove.Add(weapon);
                    }
                    else
                    {
                        hasZeus = true;
                        taserHandle = weaponHandle.Raw;
                    }
                }
                else if (name.Contains("knife") || name.Contains("bayonet"))
                {
                    knifeHandle = weaponHandle.Raw;
                }
                else if (!name.Contains("grenade") && !name.Contains("flashbang") && 
                         !name.Contains("molotov") && !name.Contains("incgrenade") && 
                         !name.Contains("decoy") && !name.Contains("c4"))
                {
                    // Strip all firearms
                    weaponsToRemove.Add(weapon);
                }
            }

            foreach (var weapon in weaponsToRemove)
            {
                weapon.Remove();
            }

            // 2. Grant fresh Zeus if needed
            if (!hasZeus)
            {
                bot.GiveNamedItem("weapon_taser");
                return; // Wait for the next tick for the handle to generate before forcing it
            }

            // 3. Force equip based on distance
            if (hasTarget && distanceToTarget < 1500.0f)
            {
                var activeWeapon = weaponServices.ActiveWeapon.Value;
                if (activeWeapon != null && activeWeapon.DesignerName != null)
                {
                    bool isUtility = activeWeapon.DesignerName.Contains("grenade") || 
                                     activeWeapon.DesignerName.Contains("flashbang") || 
                                     activeWeapon.DesignerName.Contains("molotov") || 
                                     activeWeapon.DesignerName.Contains("incgrenade") || 
                                     activeWeapon.DesignerName.Contains("decoy");

                    if (!isUtility)
                    {
                        if (distanceToTarget > 400.0f && knifeHandle != 0)
                        {
                            if (!activeWeapon.DesignerName.Contains("knife") && !activeWeapon.DesignerName.Contains("bayonet"))
                            {
                                weaponServices.ActiveWeapon.Raw = knifeHandle;
                                Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");
                            }
                        }
                        else if (distanceToTarget <= 400.0f && taserHandle != 0)
                        {
                            if (!activeWeapon.DesignerName.Contains("taser"))
                            {
                                weaponServices.ActiveWeapon.Raw = taserHandle;
                                Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");
                            }
                        }
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
                if (distance > 2000.0f) continue; 

                float threatScore = (2000.0f - distance);
                Vector dirToEnemy = GetNormalizedVector(botPos, enemyPos);
                Vector dirToBot = GetNormalizedVector(enemyPos, botPos);
                Vector enemyForward = GetForwardVector(enemyAngles);

                float enemyDot = DotProduct(enemyForward, dirToBot);
                if (enemyDot > 0.85f) threatScore += 1200.0f; 
                else if (enemyDot > 0.5f) threatScore += 400.0f; 

                float botDot = DotProduct(botForward, dirToEnemy);
                if (botDot > 0.7f) threatScore += 400.0f; 
                else if (botDot < 0.0f) threatScore -= 300.0f; 

                if (distance <= 300.0f) threatScore += 4000.0f; 

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
