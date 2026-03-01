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
        public override string ModuleName => "Zeus Bot AI";
        public override string ModuleVersion => "17.0.10";
        private CounterStrikeSharp.API.Modules.Timers.Timer? botAiTimer;
        
        // Tracks which bots are currently "reacting" so we don't spam timers on them
        private readonly HashSet<uint> reactingBots = new HashSet<uint>();
        private readonly Random random = new Random();

        public override void Load(bool hotReload)
        {
            // Run our main scanner 10 times a second
            botAiTimer = AddTimer(0.1f, RunBotScanner, TimerFlags.REPEAT);
            Console.WriteLine("[Zeus Bot AI] Humanized behavior tree loaded.");
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
        
                if (reactingBots.Contains(bot.Index)) continue;
        
                var target = GetNearestEnemyInFOV(bot, botPawn, aliveEnemies);
                if (target == null) continue;
        
                var targetPawn = target.PlayerPawn.Value;
                if (targetPawn == null) continue;
        
                float distance = (botPawn.AbsOrigin! - targetPawn.AbsOrigin!).Length();
        
                // CONCEALED CARRY LOGIC:
                // Only force the bot to pull out the Zeus if the player is getting close.
                // 350 units gives the bot roughly 1 second to play the "deploy" animation before firing.
                if (distance <= 350.0f)
                {
                    EnsureBotHasAndHoldsZeus(bot, botPawn);
        
                    // If they close the gap to 180 units, pull the trigger
                    if (distance <= 180.0f)
                    {
                        StartHumanReaction(bot, botPawn, targetPawn);
                    }
                }
                // If the player is further than 350 units, we leave the bot alone.
                // This stops the AI from infinitely dropping the weapon.
            }
        }

        private void EnsureBotHasAndHoldsZeus(CCSPlayerController bot, CCSPlayerPawn botPawn)
        {
            var weaponServices = botPawn.WeaponServices;
            if (weaponServices == null) return;
        
            bool hasZeus = false;
            uint taserHandleRaw = 0;
        
            // Scan their inventory for the Zeus and grab its exact memory handle
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
                    // Forcefully jam the Zeus into their active weapon slot
                    weaponServices.ActiveWeapon.Raw = taserHandleRaw;
                    
                    // Tell the server to network this change immediately so they don't T-pose
                    Utilities.SetStateChanged(botPawn, "CBasePlayerPawn", "m_pWeaponServices");
                }
            }
        }

        private CCSPlayerController? GetNearestEnemyInFOV(CCSPlayerController bot, CCSPlayerPawn botPawn, List<CCSPlayerController> enemies)
        {
            CCSPlayerController? bestTarget = null;
            float shortestDistance = float.MaxValue;

            var botPos = botPawn.AbsOrigin!;
            var botAngles = botPawn.EyeAngles; // Where the bot is currently looking
            if (botAngles == null) return null;

            // Convert bot's Pitch/Yaw into a forward-facing 3D vector
            float pitchRad = botAngles.X * (float)(Math.PI / 180.0);
            float yawRad = botAngles.Y * (float)(Math.PI / 180.0);
            Vector botForward = new Vector(
                (float)(Math.Cos(yawRad) * Math.Cos(pitchRad)),
                (float)(Math.Sin(yawRad) * Math.Cos(pitchRad)),
                (float)(-Math.Sin(pitchRad))
            );

            foreach (var enemy in enemies)
            {
                if (enemy.TeamNum == bot.TeamNum) continue;

                var enemyPawn = enemy.PlayerPawn.Value;
                if (enemyPawn == null) continue;

                var enemyPos = enemyPawn.AbsOrigin!;
                float distance = (botPos - enemyPos).Length();

                // Vector from bot to enemy
                Vector directionToEnemy = new Vector(enemyPos.X - botPos.X, enemyPos.Y - botPos.Y, enemyPos.Z - botPos.Z);
                
                // Normalize it (make its length 1)
                float length = (float)Math.Sqrt(directionToEnemy.X * directionToEnemy.X + directionToEnemy.Y * directionToEnemy.Y + directionToEnemy.Z * directionToEnemy.Z);
                directionToEnemy.X /= length;
                directionToEnemy.Y /= length;
                directionToEnemy.Z /= length;

                // Dot product calculates the angle difference. 1.0 is dead center. 0.0 is exactly 90 degrees to the side.
                // 0.5 roughly translates to a 120-degree cone of vision in front of the bot.
                float dotProduct = (botForward.X * directionToEnemy.X) + (botForward.Y * directionToEnemy.Y) + (botForward.Z * directionToEnemy.Z);

                if (dotProduct > 0.5f && distance < shortestDistance)
                {
                    shortestDistance = distance;
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

                // Add "Panic Inaccuracy" (-5 to +5 degrees off center) so it doesn't look like an aimbot
                float panicYaw = perfectYaw + ((random.NextSingle() * 10.0f) - 5.0f);
                float panicPitch = perfectPitch + ((random.NextSingle() * 10.0f) - 5.0f);

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

        public override void Unload(bool hotReload)
        {
            botAiTimer?.Kill();
            botAiTimer = null;
            reactingBots.Clear();
        }
    }
}
