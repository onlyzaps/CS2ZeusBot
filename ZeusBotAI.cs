using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeusBotAI
{
    public class ZeusBotAIPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Threat Matrix Integration)";
        public override string ModuleVersion => "1.3.0";
        private CounterStrikeSharp.API.Modules.Timers.Timer? botAiTimer;
        
        // Tracks which bots are currently "reacting" so we don't spam timers on them
        private readonly HashSet<uint> reactingBots = new HashSet<uint>();
        private readonly Random random = new Random();

        public override void Load(bool hotReload)
        {
            // Run our main scanner 10 times a second
            botAiTimer = AddTimer(0.1f, RunBotScanner, TimerFlags.REPEAT);
            Console.WriteLine("[Zeus Bot AI] Dynamic Threat Matrix and dodging loaded.");
        }

        private void RunBotScanner()
        {
            var players = Utilities.GetPlayers();
            var bots = players.Where(p => p != null && p.IsValid && p.IsBot && p.PawnIsAlive).ToList();
            
            if (!bots.Any()) return;

            var aliveEnemies = players.Where(p => p != null && p.IsValid && p.PawnIsAlive && !p.IsBot).ToList();

            foreach (var bot in bots)
            {
                var botPawn = bot.PlayerPawn.Value;
                if (botPawn == null) continue;

                // If this bot is already in the middle of a reaction delay, leave them alone
                if (reactingBots.Contains(bot.Index)) continue;

                var target = GetHighestPriorityTarget(bot, botPawn, aliveEnemies);
                if (target == null) continue;

                var targetPawn = target.PlayerPawn.Value;
                if (targetPawn == null) continue;

                var botOrigin = botPawn.AbsOrigin;
                var targetOrigin = targetPawn.AbsOrigin;
                if (botOrigin == null || targetOrigin == null) continue;

                float distance = (botOrigin - targetOrigin).Length();

                // CONCEALED CARRY & MOVEMENT LOGIC
                if (distance <= 500.0f)
                {
                    EnsureBotHasAndHoldsZeus(bot, botPawn);

                    // If they are outside zap range but close enough to push, inject chaotic movement
                    if (distance > 180.0f)
                    {
                        // 15% chance per tick (0.1s) to jump, simulating a frantic push
                        if (random.NextDouble() < 0.15 && botPawn.MovementServices != null)
                        {
                            botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Jump;
                            
                            // Add a mid-air crouch 100ms later to tuck their legs (harder to hit)
                            AddTimer(0.1f, () => {
                                if (bot.IsValid && bot.PlayerPawn.Value?.MovementServices != null)
                                    bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Duck;
                            });

                            // Release the buttons half a second later so they can land and run
                            AddTimer(0.5f, () => {
                                if (bot.IsValid && bot.PlayerPawn.Value?.MovementServices != null)
                                {
                                    bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Jump;
                                    bot.PlayerPawn.Value.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Duck;
                                }
                            });
                        }
                    }
                    // If they close the gap to 180 units, pull the trigger
                    else if (distance <= 180.0f)
                    {
                        StartHumanReaction(bot, botPawn, targetPawn);
                    }
                }
            }
        }

        private void EnsureBotHasAndHoldsZeus(CCSPlayerController bot, CCSPlayerPawn botPawn)
        {
            var weaponServices = botPawn.WeaponServices;
            if (weaponServices == null) return;

            bool hasZeus = false;
            uint taserHandleRaw = 0;

            // Scan their inventory for the Zeus and grab its exact memory handle
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
                // We let the engine give them the item; we'll force-equip it on the next tick
            }
            else
            {
                var activeWeapon = weaponServices.ActiveWeapon.Value;
                if (activeWeapon != null && activeWeapon.DesignerName != null && !activeWeapon.DesignerName.Contains("taser"))
                {
                    // Forcefully jam the Zeus into their active weapon slot via memory manipulation
                    weaponServices.ActiveWeapon.Raw = taserHandleRaw;
                    
                    // Tell the server to network this change immediately so they don't T-pose
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
                if (distance > 1500.0f) continue; // Ignore enemies across the map

                float threatScore = 0;

                // 1. DISTANCE: Base threat (Closer = exponentially more threatening)
                threatScore += (1500.0f - distance);

                Vector dirToEnemy = GetNormalizedVector(botPos, enemyPos);
                Vector dirToBot = GetNormalizedVector(enemyPos, botPos);
                Vector enemyForward = GetForwardVector(enemyAngles);

                // 2. ENEMY READINESS: Is the enemy looking at the bot?
                float enemyDot = DotProduct(enemyForward, dirToBot);
                if (enemyDot > 0.85f) 
                    threatScore += 600.0f; // MASSIVE THREAT: They are aiming at the bot
                else if (enemyDot > 0.5f) 
                    threatScore += 250.0f; // They are looking in the bot's general direction

                // 3. BOT MOMENTUM: Is the bot already looking at them?
                float botDot = DotProduct(botForward, dirToEnemy);
                if (botDot > 0.7f) 
                    threatScore += 400.0f; // Human commitment: prioritize targets in front of us
                else if (botDot < 0.0f) 
                    threatScore -= 300.0f; // Human reluctance: penalize targets behind us (avoid 180 flicks)

                // 4. WEAPON STATE: Are they defenseless?
                var weaponServices = enemyPawn.WeaponServices;
                if (weaponServices?.ActiveWeapon?.Value != null)
                {
                    string weaponName = weaponServices.ActiveWeapon.Value.DesignerName ?? "";
                    if (weaponName.Contains("grenade") || weaponName.Contains("flashbang") || weaponName.Contains("smokegrenade") || weaponName.Contains("decoy") || weaponName.Contains("molotov") || weaponName.Contains("incgrenade") || weaponName.Contains("c4"))
                    {
                        threatScore -= 400.0f; // Defenseless target, lower priority
                    }
                    else if (weaponName.Contains("knife"))
                    {
                        threatScore -= 150.0f; // Only a threat if super close
                    }
                    else 
                    {
                        threatScore += 200.0f; // Armed target, higher priority
                    }
                }

                // 5. THE ZEUS OVERRIDE
                if (distance <= 200.0f)
                {
                    threatScore += 2000.0f; // If anyone is inside Zap range, kill them immediately
                }

                // Final tally
                if (threatScore > highestThreatScore)
                {
                    highestThreatScore = threatScore;
                    bestTarget = enemy;
                }
            }

            return bestTarget;
        }

        private void StartHumanReaction(CCSPlayerController bot, CCSPlayerPawn botPawn, CCSPlayerPawn targetPawn)
        {
            uint botIndex = bot.Index;
            reactingBots.Add(botIndex);

            // Generate a random reaction time between 150ms and 350ms
            float reactionTime = (random.NextSingle() * 0.20f) + 0.15f;

            AddTimer(reactionTime, () =>
            {
                reactingBots.Remove(botIndex);

                // Ensure both are still alive and valid after the delay
                if (!bot.IsValid || !bot.PawnIsAlive || !targetPawn.IsValid) return;

                var botPos = botPawn.AbsOrigin;
                var targetPos = targetPawn.AbsOrigin;
                if (botPos == null || targetPos == null) return;

                // Re-check distance in case the player dashed away during the reaction time
                float distance = (botPos - targetPos).Length();
                if (distance > 190.0f) return;

                // Calculate the perfect shot
                float deltaX = targetPos.X - botPos.X;
                float deltaY = targetPos.Y - botPos.Y;
                float deltaZ = (targetPos.Z + 40.0f) - (botPos.Z + 40.0f); 

                float perfectYaw = (float)(Math.Atan2(deltaY, deltaX) * 180.0 / Math.PI);
                float perfectPitch = (float)(Math.Atan2(-deltaZ, Math.Sqrt(deltaX * deltaX + deltaY * deltaY)) * 180.0 / Math.PI);

                // Add Tightened "Panic Inaccuracy" (-2 to +2 degrees off center)
                float panicYaw = perfectYaw + ((random.NextSingle() * 4.0f) - 2.0f);
                float panicPitch = perfectPitch + ((random.NextSingle() * 4.0f) - 2.0f);

                var newAngles = new QAngle(panicPitch, panicYaw, 0);
                botPawn.Teleport(botPos, newAngles, new Vector(0, 0, 0));

                if (botPawn.MovementServices != null)
                {
                    // Inject the +attack command directly into the bot's memory state
                    botPawn.MovementServices.Buttons.ButtonStates[0] |= (ulong)PlayerButtons.Attack;
                    
                    AddTimer(0.05f, () => 
                    { 
                        // Re-validate the pawn after the delay in case they died in that 50ms window
                        if (bot.IsValid)
                        {
                            var currentPawn = bot.PlayerPawn.Value;
                            if (currentPawn != null && currentPawn.IsValid && currentPawn.MovementServices != null)
                            {
                                // Release the trigger
                                currentPawn.MovementServices.Buttons.ButtonStates[0] &= ~(ulong)PlayerButtons.Attack; 
                            }
                        }
                    });
                }
            });
        }

        // --- 3D VECTOR MATH HELPERS ---

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
            botAiTimer?.Kill();
            botAiTimer = null;
            reactingBots.Clear();
        }
    }
}
