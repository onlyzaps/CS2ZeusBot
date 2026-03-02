using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeusBotAI
{
    #region GOAP Core & Enums

    // The foundational blueprint for anything the bot can DO (run, shoot, swap weapons).
    public abstract class GoapAction
    {
        // Checks if the action makes sense in the current context before trying it.
        public virtual bool CheckContextPrecondition(BotAgent agent) => true;
        // Verifies the action hasn't become invalid mid-execution.
        public virtual bool IsValid(BotAgent agent) => true;
        // Tells the planner when this specific task is finished.
        public abstract bool IsDone(BotAgent agent);
        // The actual logic that runs every server tick when this action is active.
        public abstract void Execute(BotAgent agent, float deltaTime);
        // Hooks for setup and teardown when the action starts/stops.
        public virtual void OnEnter(BotAgent agent) { }
        public virtual void OnExit(BotAgent agent) { }
    }

    // The blueprint for what the bot WANTS (e.g., stay alive, kill the player).
    public abstract class GoapGoal
    {
        // Determines how badly the bot wants this goal right now. Highest priority wins.
        public abstract int GetPriority(BotAgent agent);
    }
    #endregion

    #region Fact & Memory Architecture

    // Represents a single piece of information the bot knows (like an enemy's location).
    public class Fact
    {
        public CCSPlayerController? Subject;
        public Vector LastKnownPosition = new Vector(0, 0, 0);
        public float Confidence = 1.0f; // Drops as the memory gets older.
        public float ThreatLevel = 0f;  // Dictates whether the bot ignores them or attacks.
        public float TimeSinceLastSeen = 0f;
    }

    // The bot's short-term memory system.
    public class WorkingMemory
    {
        public Dictionary<uint, Fact> Facts = new Dictionary<uint, Fact>();

        // Runs every tick to simulate "forgetting" targets that break line of sight.
        public void Update(float deltaTime)
        {
            var keys = Facts.Keys.ToList();
            foreach (var key in keys)
            {
                var fact = Facts[key];
                // Clean up disconnected or dead players instantly to prevent memory leaks/errors.
                if (fact.Subject == null || !fact.Subject.IsValid || !fact.Subject.PawnIsAlive)
                {
                    Facts.Remove(key);
                    continue;
                }

                fact.TimeSinceLastSeen += deltaTime;
                
                // If the bot hasn't seen the target for half a second, start doubting their position.
                if (fact.TimeSinceLastSeen > 0.5f) 
                {
                    fact.Confidence -= deltaTime * 0.2f;
                }

                // Memory completely faded. The bot forgets the player exists.
                if (fact.Confidence <= 0)
                {
                    Facts.Remove(key);
                }
            }
        }
    }

    // The "scratchpad" where the bot writes down its immediate physical intentions for the current frame.
    public class BotBlackboard
    {
        public Vector DesiredMoveDirection = new Vector(0, 0, 0);
        public float DesiredSpeed = 0f;
        public QAngle DesiredAim = new QAngle(0, 0, 0);
        public ulong ButtonsToPress = 0; // Bitmask for CS2 engine inputs (+jump, +attack, etc.)
        public Fact? CurrentTargetFact = null;
        
        public float FearTimer = 0f;
        public float ActionCooldown = 0f;
        
        // Navigation & Traversal
        public float JumpCooldown = 0f;

        // Combat Engine State
        public float NextStateTime = 0f;
        public int MovePattern = 0;
        public float StrafeDir = 1f;
        public int BhopCount = 0;
    }

    #endregion

    #region The Bot Agent

    // The wrapper that connects our custom GOAP brain to a physical CS2 Bot Pawn.
    public class BotAgent
    {
        public CCSPlayerController Controller;
        public CCSPlayerPawn Pawn => Controller.PlayerPawn.Value!;
        public WorkingMemory Memory = new WorkingMemory();
        public BotBlackboard Blackboard = new BotBlackboard();

        public List<GoapGoal> Goals = new List<GoapGoal>();
        public List<GoapAction> AvailableActions = new List<GoapAction>();
        public Queue<GoapAction> CurrentPlan = new Queue<GoapAction>();
        public GoapAction? CurrentAction = null;

        public BotAgent(CCSPlayerController controller)
        {
            Controller = controller;
            InitGoalsAndActions();
        }

        // Populates the bot's "brain" with all possible things it can care about and do.
        private void InitGoalsAndActions()
        {
            Goals.Add(new GoalSurvive());
            Goals.Add(new GoalKillEnemy());

            AvailableActions.Add(new ActionEquipZeus());
            AvailableActions.Add(new ActionEquipKnife());
            AvailableActions.Add(new ActionTraverseMap());
            AvailableActions.Add(new ActionApproachTarget());
            AvailableActions.Add(new ActionEvasiveRetreat());
            AvailableActions.Add(new ActionEngageZeus());
            AvailableActions.Add(new ActionEngageKnife());
        }

        // The Bot's "Eyes". Scans the map to figure out who is a threat.
        public void UpdateSensor(List<CCSPlayerController> allPlayers, float currentTime)
        {
            if (Pawn == null || !Pawn.IsValid) return;
            Vector myPos = Pawn.AbsOrigin!;
            Vector myForward = MathUtils.GetForwardVector(Pawn.EyeAngles!);

            bool foundValidEnemyOnMap = false;

            // Loop through every player on the server to evaluate them.
            foreach (var player in allPlayers)
            {
                // Ignore ourselves, dead players, and teammates.
                if (player == Controller || !player.PawnIsAlive) continue;
                if (player.TeamNum == Controller.TeamNum) continue;

                var otherPawn = player.PlayerPawn.Value;
                if (otherPawn == null || !otherPawn.IsValid) continue;

                foundValidEnemyOnMap = true;
                Vector otherPos = otherPawn.AbsOrigin!;
                float dist = (myPos - otherPos).Length();
                Vector dirToOther = MathUtils.NormalizeVector(otherPos - myPos);

                // Add newly discovered enemies to the bot's working memory.
                if (!Memory.Facts.ContainsKey(player.Index))
                {
                    Memory.Facts[player.Index] = new Fact { Subject = player };
                }

                var fact = Memory.Facts[player.Index];
                
                // We grant the bot a "global wallhack" of the target's position...
                fact.LastKnownPosition = otherPos;
                fact.Confidence = 1.0f;

                // ... BUT we artificially restrict their reaction to it using human-like thresholds.
                float zDiff = Math.Abs(myPos.Z - otherPos.Z);
                bool isClose = dist < 750f; // Target is within engagement range.
                bool reasonableZ = zDiff < 350f; // Target isn't on a completely different floor/roof.
                float dot = MathUtils.DotProduct(myForward, dirToOther);
                bool inFOV = dot > 0.15f; // Target is vaguely in front of the bot.

                // Query the CS2 engine natively to see if the target is actually visible/spotted.
                bool isSpotted = otherPawn.EntitySpottedState?.Spotted ?? false;

                // If all combat conditions are met, the bot "wakes up".
                if (isClose && reasonableZ && inFOV && isSpotted)
                {
                    fact.TimeSinceLastSeen = 0f;
                    // ThreatLevel 3000 tells the system to hijack the bot's physics and attack!
                    fact.ThreatLevel = 3000f; 
                }
                else
                {
                    // Target is too far or behind a wall. Drop threat to 10.
                    // This allows the native CS2 NavMesh bot to handle normal walking around corners.
                    fact.ThreatLevel = 10f; 
                }
            }

            // If no enemies exist at all, wipe the memory clean to save processing power.
            if (!foundValidEnemyOnMap)
            {
                var keys = Memory.Facts.Keys.ToList();
                foreach (var k in keys) Memory.Facts.Remove(k);
            }
        }

        // Forcefully stops what the bot is currently doing to rethink its plan.
        public void Interrupt(string reason)
        {
            CurrentPlan.Clear();
            CurrentAction?.OnExit(this);
            CurrentAction = null;
        }

        // Utility to check if the bot owns a specific weapon class.
        public bool HasWeapon(string name)
        {
            if (Pawn?.WeaponServices?.MyWeapons == null) return false;
            foreach (var w in Pawn.WeaponServices.MyWeapons)
            {
                var weapon = w.Value;
                if (weapon != null && weapon.DesignerName != null && weapon.DesignerName.Contains(name))
                {
                    return true;
                }
            }
            return false;
        }
    }

    #endregion

    #region Goals & Actions Implementation

    // Goal: Murder the player. Priority spikes up if the bot actually sees someone.
    public class GoalKillEnemy : GoapGoal
    {
        public override int GetPriority(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null ? 80 : 10;
    }

    // Goal: Run away. Priority hits 100 (max) if the bot's fear timer is active.
    public class GoalSurvive : GoapGoal
    {
        public override int GetPriority(BotAgent agent) => agent.Blackboard.FearTimer >= Server.CurrentTime ? 100 : 0;
    }

    // Action: Swap to the Zeus.
    public class ActionEquipZeus : GoapAction
    {
        public override bool IsDone(BotAgent agent)
        {
            var activeWeapon = agent.Pawn?.WeaponServices?.ActiveWeapon.Value;
            return activeWeapon != null && activeWeapon.DesignerName.Contains("taser");
        }
        public override void Execute(BotAgent agent, float dt)
        {
            var weaponServices = agent.Pawn?.WeaponServices;
            if (weaponServices?.MyWeapons != null)
            {
                // Iterate through inventory, find the taser, and force the engine to equip it.
                foreach (var weaponHandle in weaponServices.MyWeapons)
                {
                    var weapon = weaponHandle.Value;
                    if (weapon != null && weapon.DesignerName != null && weapon.DesignerName.Contains("taser"))
                    {
                        weaponServices.ActiveWeapon.Raw = weaponHandle.Raw;
                        Utilities.SetStateChanged(agent.Pawn!, "CBasePlayerPawn", "m_pWeaponServices");
                        break;
                    }
                }
            }
            // If we are swapping mid-fight, keep running at the target.
            if (agent.Blackboard.CurrentTargetFact != null && agent.Blackboard.CurrentTargetFact.ThreatLevel > 100f)
            {
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector(agent.Blackboard.CurrentTargetFact.LastKnownPosition - agent.Pawn.AbsOrigin!);
                agent.Blackboard.DesiredSpeed = 250f;
            }
            else
            {
                // If we aren't fighting (e.g., freeze time), stand completely still while swapping.
                agent.Blackboard.DesiredSpeed = 0f;
                agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0);
            }
        }
    }

    // Action: Swap to the Knife (identical logic to Zeus swap, but for speed).
    public class ActionEquipKnife : GoapAction
    {
        public override bool IsDone(BotAgent agent)
        {
            var activeWeapon = agent.Pawn?.WeaponServices?.ActiveWeapon.Value;
            return activeWeapon != null && activeWeapon.DesignerName.Contains("knife");
        }
        public override void Execute(BotAgent agent, float dt)
        {
            var weaponServices = agent.Pawn?.WeaponServices;
            if (weaponServices?.MyWeapons != null)
            {
                foreach (var weaponHandle in weaponServices.MyWeapons)
                {
                    var weapon = weaponHandle.Value;
                    if (weapon != null && weapon.DesignerName != null && weapon.DesignerName.Contains("knife"))
                    {
                        weaponServices.ActiveWeapon.Raw = weaponHandle.Raw;
                        Utilities.SetStateChanged(agent.Pawn!, "CBasePlayerPawn", "m_pWeaponServices");
                        break;
                    }
                }
            }
            if (agent.Blackboard.CurrentTargetFact != null && agent.Blackboard.CurrentTargetFact.ThreatLevel > 100f)
            {
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector(agent.Blackboard.CurrentTargetFact.LastKnownPosition - agent.Pawn.AbsOrigin!);
                agent.Blackboard.DesiredSpeed = 250f;
            }
            else
            {
                agent.Blackboard.DesiredSpeed = 0f;
                agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0);
            }
        }
    }

    // Action: Passive map roaming.
    public class ActionTraverseMap : GoapAction
    {
        public override bool CheckContextPrecondition(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        
        public override bool IsDone(BotAgent agent)
        {
            if (agent.Blackboard.CurrentTargetFact == null) return true;
            // The bot instantly stops "traversing" and switches to combat mode if threat spikes.
            return agent.Blackboard.CurrentTargetFact.ThreatLevel > 100f;
        }

        public override void Execute(BotAgent agent, float dt)
        {
            // CRITICAL HANDOFF: By setting DesiredSpeed to 0, our custom motor injector (below) ignores the bot.
            // This allows the native CS2 NavMesh bot AI to take over and pathfind beautifully through the map!
            agent.Blackboard.DesiredSpeed = 0f;
            agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0);
        }
    }

    // Action: The bot is rushing you with murderous intent.
    public class ActionApproachTarget : GoapAction
    {
        public override bool CheckContextPrecondition(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        
        public override bool IsDone(BotAgent agent) 
        {
            // Abort if target breaks line of sight. Hand control back to native AI.
            if (agent.Blackboard.CurrentTargetFact == null || agent.Blackboard.CurrentTargetFact.ThreatLevel < 100f) return true;
            float dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
            bool hasZeus = agent.HasWeapon("taser");
            var activeWep = agent.Pawn?.WeaponServices?.ActiveWeapon.Value;
            bool holdsKnife = activeWep != null && activeWep.DesignerName.Contains("knife");

            // Tactical decision: If rushing with a knife, stop rushing at 450 units to swap to the Zeus.
            if (hasZeus && holdsKnife && dist < 450f) return true;
            
            // Reached kill zone! Move to the actual Engagement action.
            return dist <= (hasZeus ? 210f : 65f);
        }

        public override void Execute(BotAgent agent, float dt)
        {
            Vector targetPos = agent.Blackboard.CurrentTargetFact!.LastKnownPosition;
            Vector myPos = agent.Pawn.AbsOrigin!;
            
            // Math Magic: Dynamic Clumping Offset.
            // We use the bot's index and a sine wave to make sure 5 bots don't rush you in a single-file line.
            // They fan out in a circle as they approach.
            float clumpOffsetAngle = agent.Controller.Index * 36f + Server.CurrentTime * 3f;
            Vector approachPos = new Vector(targetPos.X + (float)Math.Cos(clumpOffsetAngle) * 55f, targetPos.Y + (float)Math.Sin(clumpOffsetAngle) * 55f, targetPos.Z);

            Vector dirToApproach = MathUtils.NormalizeVector(approachPos - myPos);
            Vector exactTargetDir = MathUtils.NormalizeVector(targetPos - myPos);
            // Get the 90-degree right vector for strafing math.
            Vector rightDir = new Vector(-exactTargetDir.Y, exactTargetDir.X, 0);

            bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
            float currentTime = Server.CurrentTime;

            // State Machine Timer: Change movement styles randomly to be unpredictable.
            if (currentTime > agent.Blackboard.NextStateTime)
            {
                Random r = new Random();
                agent.Blackboard.MovePattern = r.Next(0, 4); 
                agent.Blackboard.NextStateTime = currentTime + (float)(r.NextDouble() * 1.0 + 0.5);
                agent.Blackboard.StrafeDir = r.NextDouble() > 0.5 ? 1f : -1f;
                agent.Blackboard.BhopCount = r.Next(2, 6); // Load up on consecutive jumps.
            }

            Vector mvmt = new Vector(0,0,0);
            float speed = 250f;

            if (agent.Blackboard.MovePattern == 1 || agent.Blackboard.MovePattern == 3)
            {
                // Pattern: Active B-Hop Strafe
                if (isGrounded && agent.Blackboard.JumpCooldown < currentTime)
                {
                    // Prime the Jump button for the engine injector.
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = currentTime + 0.3f;
                    agent.Blackboard.BhopCount--;
                    // Randomly snap left/right mid-air to break player crosshair tracking.
                    if (agent.Blackboard.BhopCount % 2 == 0) agent.Blackboard.StrafeDir *= -1f; 
                }
                
                if (!isGrounded)
                {
                    // While in the air, blend forward velocity with intense side-strafing.
                    mvmt = MathUtils.NormalizeVector((dirToApproach * 0.7f) + (rightDir * agent.Blackboard.StrafeDir * 1.0f));
                    speed = 280f; 
                    // Occasionally tap crouch mid-air to pull up legs (harder to hit).
                    if (agent.Blackboard.BhopCount % 3 == 0) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
                }
                else
                {
                    mvmt = dirToApproach;
                }
            }
            else if (agent.Blackboard.MovePattern == 2)
            {
                // Pattern: Wide Jump & Crouch-Slide
                if (isGrounded && agent.Blackboard.JumpCooldown < currentTime)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = currentTime + 0.6f;
                }
                float wideWeight = isGrounded ? 0.3f : 1.3f; 
                mvmt = MathUtils.NormalizeVector((dirToApproach * 0.7f) + (rightDir * agent.Blackboard.StrafeDir * wideWeight));
                // Hold duck entirely while falling for a slide effect.
                if (!isGrounded) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
            }
            else 
            {
                // Pattern: Serpentine Ground Jitter
                // Oscillate wildly left and right while running forward based on server time.
                float jitter = (float)Math.Sin(currentTime * 5f + agent.Controller.Index);
                if (Math.Abs(jitter) > 0.5f) agent.Blackboard.StrafeDir = Math.Sign(jitter);
                mvmt = MathUtils.NormalizeVector((dirToApproach * 1.5f) + (rightDir * agent.Blackboard.StrafeDir * 0.4f));
            }

            agent.Blackboard.DesiredMoveDirection = mvmt;
            agent.Blackboard.DesiredSpeed = speed;
        }
    }

    // Action: Running for your life.
    public class ActionEvasiveRetreat : GoapAction
    {
        public override bool IsDone(BotAgent agent) => agent.Blackboard.FearTimer < Server.CurrentTime;
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact != null)
            {
                // Calculate vector exactly away from the enemy.
                Vector tPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector myPos = agent.Pawn.AbsOrigin!;
                Vector dirAway = MathUtils.NormalizeVector(myPos - tPos);
                Vector right = new Vector(-dirAway.Y, dirAway.X, 0);

                bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
                
                // Jump sporadically to dodge bullets while fleeing.
                if (isGrounded && agent.Blackboard.JumpCooldown < Server.CurrentTime)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = Server.CurrentTime + 0.5f;
                }
                
                float erratic = (float)Math.Sin(Server.CurrentTime * 6f);
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector((dirAway * 1.5f) + (right * Math.Sign(erratic) * 0.5f));
                agent.Blackboard.DesiredSpeed = 260f;
            }
        }
    }

    // Action: Bot is in kill range and actively trying to fire the Zeus.
    public class ActionEngageZeus : GoapAction
    {
        public override bool CheckContextPrecondition(BotAgent agent) => true;
        public override bool IsValid(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        public override bool IsDone(BotAgent agent) 
        {
            // If the player successfully backs up out of range, drop this action to re-evaluate rushing.
            if (agent.Blackboard.CurrentTargetFact == null || agent.Blackboard.CurrentTargetFact.ThreatLevel < 100f) return true;
            float dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
            return dist > 230f; 
        }
        
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact == null) return;

            Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
            Vector myPos = agent.Pawn.AbsOrigin!;
            float dist = (targetPos - myPos).Length();
            
            // Continue fanning out to surround the player in close quarters.
            float clusterOffset = agent.Controller.Index * 50f + Server.CurrentTime * 2f;
            Vector attackPos = new Vector(targetPos.X + (float)Math.Cos(clusterOffset) * 45f, targetPos.Y + (float)Math.Sin(clusterOffset) * 45f, targetPos.Z);

            Vector dirToApproach = MathUtils.NormalizeVector(attackPos - myPos);
            Vector exactDirToTarget = MathUtils.NormalizeVector(targetPos - myPos);
            Vector rightDir = new Vector(-exactDirToTarget.Y, exactDirToTarget.X, 0);

            bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
            float time = Server.CurrentTime;

            // Combat Dance: Fast, erratic micro-movements to avoid getting headshot before firing.
            if (time > agent.Blackboard.NextStateTime)
            {
                Random r = new Random();
                agent.Blackboard.MovePattern = r.Next(0, 3); 
                agent.Blackboard.NextStateTime = time + 0.4f;
                agent.Blackboard.StrafeDir = r.NextDouble() > 0.5 ? 1f : -1f;
            }

            if (agent.Blackboard.MovePattern == 0)
            {
                // Erratic ADAD Peek
                float strafeVal = Math.Sign(Math.Sin(time * 6f));
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector((dirToApproach * 0.4f) + (rightDir * strafeVal * 0.8f));
                if (strafeVal > 0 && isGrounded) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
            }
            else if (agent.Blackboard.MovePattern == 1)
            {
                // Micro jump strafe
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector(dirToApproach + (rightDir * agent.Blackboard.StrafeDir * 0.5f));
                if (isGrounded && agent.Blackboard.JumpCooldown < time)
                {
                     agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                     agent.Blackboard.JumpCooldown = time + 0.6f;
                }
            }
            else
            {
                // Aggressive close with slight staggering
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector(dirToApproach + (rightDir * agent.Blackboard.StrafeDir * 0.2f));
            }
            agent.Blackboard.DesiredSpeed = 260f;

            // Aim Math: Calculate exactly where to look to hit the player.
            float eyeHeight = ((uint)agent.Pawn.Flags & 2) != 0 ? 46f : 64f; // Check if ducking to adjust Z-axis origin.
            Vector botHead = new Vector(myPos.X, myPos.Y, myPos.Z + eyeHeight);
            
            float targetZ = Math.Clamp(botHead.Z, targetPos.Z, targetPos.Z + 72f); // Aim for center mass/head
            Vector exactTarget = new Vector(targetPos.X, targetPos.Y, targetZ);
            Vector exactDir = MathUtils.NormalizeVector(exactTarget - botHead);
            Vector curForward = MathUtils.GetForwardVector(agent.Pawn.EyeAngles!);
            
            // Dot product compares where the bot is currently looking vs where the target actually is.
            float aimDot = MathUtils.DotProduct(curForward, exactDir);
            
            // Firing threshold: Don't shoot unless the crosshair is mostly on target.
            // Loosened slightly (0.985f) so the bot doesn't hesitate due to organic aim noise.
            if (aimDot > 0.985f || (dist < 150f && aimDot > 0.960f)) 
            {
                if (agent.Blackboard.ActionCooldown <= Server.CurrentTime)
                {
                    // Pull the trigger!
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack;
                    agent.Blackboard.ActionCooldown = Server.CurrentTime + 0.5f; 
                }
            }
        }
    }

    // Action: Slashing with the knife.
    public class ActionEngageKnife : GoapAction
    {
        public override bool CheckContextPrecondition(BotAgent agent) => true;
        public override bool IsValid(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        public override bool IsDone(BotAgent agent) 
        {
            if (agent.Blackboard.CurrentTargetFact == null || agent.Blackboard.CurrentTargetFact.ThreatLevel < 100f) return true;
            float dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
            return dist > 85f; 
        }
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact != null)
            {
                Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector myPos = agent.Pawn.AbsOrigin!;
                
                // Prevent exact body-stacking 
                float clusterOffset = agent.Controller.Index * 60f + Server.CurrentTime;
                Vector stabPos = new Vector(targetPos.X + (float)Math.Cos(clusterOffset) * 20f, targetPos.Y + (float)Math.Sin(clusterOffset) * 20f, targetPos.Z);
                
                Vector dirToTarget = MathUtils.NormalizeVector(stabPos - myPos);
                
                agent.Blackboard.DesiredMoveDirection = dirToTarget; 
                agent.Blackboard.DesiredSpeed = 270f; // Run slightly faster for knife attacks
                
                // Jump randomly if close to throw off enemy aim
                if (((uint)agent.Pawn.Flags & 1) != 0 && agent.Blackboard.JumpCooldown < Server.CurrentTime && (targetPos - myPos).Length() < 150f)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = Server.CurrentTime + 0.8f;
                }
            }
            if (agent.Blackboard.ActionCooldown <= Server.CurrentTime)
            {
                // Right click for heavy stab!
                agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack2; 
                agent.Blackboard.ActionCooldown = Server.CurrentTime + 0.8f;
            }
        }
    }

    #endregion

    #region The GOAP Planner 

    // The logic core that stitches actions together based on the bot's current goals.
    public class GoapPlanner
    {
        public Queue<GoapAction> Plan(BotAgent agent, GoapGoal goal)
        {
            // Filter down to actions that actually make sense right now.
            var usableActions = agent.AvailableActions.Where(a => a.CheckContextPrecondition(agent)).ToList();
            List<GoapAction> plan = new List<GoapAction>();
            
            if (goal is GoalSurvive)
            {
                plan.Add(usableActions.First(a => a is ActionEvasiveRetreat));
                return new Queue<GoapAction>(plan);
            }
            
            if (goal is GoalKillEnemy)
            {
                float dist = 9999f;
                bool inActiveCombat = false;
                
                if (agent.Blackboard.CurrentTargetFact != null)
                {
                    dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
                    inActiveCombat = agent.Blackboard.CurrentTargetFact.ThreatLevel > 100f;
                }

                bool hasZeus = agent.HasWeapon("taser");

                // Note: Instead of an expensive graph-search algorithm (A*), we use a highly optimized,
                // hardcoded decision tree. This saves immense server CPU time on 64-tick CS2 servers.

                if (!inActiveCombat)
                {
                    // Target is far away or behind walls. Put away the Zeus, pull out the knife to sprint, and let CS2 natively navigate.
                    var equipKnife = usableActions.FirstOrDefault(a => a is ActionEquipKnife);
                    if (equipKnife != null) plan.Add(equipKnife);
                    
                    var traverse = usableActions.FirstOrDefault(a => a is ActionTraverseMap);
                    if (traverse != null) plan.Add(traverse);
                }
                else
                {
                    // Target spotted and in line-of-sight! Combat initiated. 
                    if (hasZeus)
                    {
                        if (dist > 450f)
                        {
                            // Far combat: Keep the knife out to close the distance fast.
                            var prepKnife = usableActions.FirstOrDefault(a => a is ActionEquipKnife);
                            if (prepKnife != null) plan.Add(prepKnife);
                            
                            var approach = usableActions.FirstOrDefault(a => a is ActionApproachTarget);
                            if (approach != null) plan.Add(approach);
                        }
                        else
                        {
                            // Close combat: Swap to the Zeus, juke their shots, and prepare to fire.
                            var pullZeus = usableActions.FirstOrDefault(a => a is ActionEquipZeus);
                            if (pullZeus != null) plan.Add(pullZeus);
                            
                            if (dist > 210f)
                            {
                                var approachDodge = usableActions.FirstOrDefault(a => a is ActionApproachTarget);
                                if (approachDodge != null) plan.Add(approachDodge);
                            }
                            
                            var kill = usableActions.FirstOrDefault(a => a is ActionEngageZeus);
                            if (kill != null) plan.Add(kill);
                        }
                    }
                    else
                    {
                        // Fallback: Bot missed the Zeus shot or doesn't have one. Pure knife run.
                        var equipKnife = usableActions.FirstOrDefault(a => a is ActionEquipKnife);
                        if (equipKnife != null) plan.Add(equipKnife);
                        
                        if (dist > 65f)
                        {
                            var approach = usableActions.FirstOrDefault(a => a is ActionApproachTarget);
                            if (approach != null) plan.Add(approach);
                        }
                        var kill = usableActions.FirstOrDefault(a => a is ActionEngageKnife);
                        if (kill != null) plan.Add(kill);
                    }
                }
            }

            return new Queue<GoapAction>(plan);
        }
    }

    #endregion

    #region Math Utilities
    // Standard 3D spatial geometry helpers required for aimbot/movement logic.
    public static class MathUtils
    {
        public static float NormalizeAngle(float angle)
        {
            if (float.IsNaN(angle) || float.IsInfinity(angle)) return 0f;
            
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            else if (angle < -180f) angle += 360f;
            
            return angle;
        }

        public static Vector GetForwardVector(QAngle angles)
        {
            float pitchRad = angles.X * (float)(Math.PI / 180.0);
            float yawRad = angles.Y * (float)(Math.PI / 180.0);
            return new Vector((float)(Math.Cos(yawRad) * Math.Cos(pitchRad)), (float)(Math.Sin(yawRad) * Math.Cos(pitchRad)), (float)(-Math.Sin(pitchRad)));
        }

        public static Vector NormalizeVector(Vector vec)
        {
            float length = (float)Math.Sqrt(vec.X * vec.X + vec.Y * vec.Y + vec.Z * vec.Z);
            return length == 0 ? new Vector(0, 0, 0) : new Vector(vec.X / length, vec.Y / length, vec.Z / length);
        }

        public static float DotProduct(Vector v1, Vector v2) => (v1.X * v2.X) + (v1.Y * v2.Y) + (v1.Z * v2.Z);
    }
    #endregion

    #region Main Plugin Component
    // The main CounterStrikeSharp plugin class that hooks into the server lifecycle.
    public class ZeusBotAIGoapPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Refactored Core)";
        public override string ModuleVersion => "3.0.0";

        private Dictionary<uint, BotAgent> agents = new Dictionary<uint, BotAgent>();
        private GoapPlanner planner = new GoapPlanner();
        private int tickCounter = 0;

        // Fun flavor array to give bots electricity-themed names.
        private static readonly string[] BotNames = new string[]
        {
            "Zappy", "Sparky", "Zeus", "Bolt", "Zip", "Static", "Flicker", "Thor", "Jolt", "Volt",
            "Watt", "Amp", "Ohm", "Tesla", "Arc", "Ion", "Surge", "Livewire", "Shorty", "Buzzer",
            "Glow", "Indra", "Raiju", "Flash", "Shock", "Plasma", "Neon", "Zap", "Blitz", "Storm"
        };
        private Dictionary<uint, string> assignedNames = new Dictionary<uint, string>();
        private Random rnd = new Random();

        public override void Load(bool hotReload)
        {
            RegisterListener<Listeners.OnTick>(OnTick);
            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            Console.WriteLine("[Zeus Bot GOAP] Core Engine v3 loaded.");
        }

        private void OnMapStart(string mapName)
        {
            agents.Clear();
            tickCounter = 0;
        }

        // Whenever a bot spawns, rename it and force a Zeus into its inventory.
        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            var controller = @event.Userid;
            if (controller != null && controller.IsValid && controller.IsBot)
            {
                if (agents.ContainsKey(controller.Index))
                {
                    agents.Remove(controller.Index);
                }

                if (!assignedNames.ContainsKey(controller.Index))
                {
                    assignedNames[controller.Index] = BotNames[rnd.Next(BotNames.Length)];
                }
                string desiredName = assignedNames[controller.Index];

                // Execute on NextFrame to ensure the pawn entity actually exists before manipulating it.
                Server.NextFrame(() => {
                    if (controller.IsValid)
                    {
                        controller.PlayerName = desiredName;
                        Utilities.SetStateChanged(controller, "CBasePlayerController", "m_iszPlayerName");
                        
                        var pawn = controller.PlayerPawn.Value;
                        if (pawn != null && pawn.IsValid)
                        {
                            bool hasZeus = false;
                            if (pawn.WeaponServices?.MyWeapons != null)
                            {
                                foreach (var w in pawn.WeaponServices.MyWeapons)
                                {
                                    if (w.Value != null && w.Value.DesignerName != null && w.Value.DesignerName.Contains("taser"))
                                    {
                                        hasZeus = true;
                                        break;
                                    }
                                }
                            }
                            // If they don't have a taser, spawn one in for them magically.
                            if (!hasZeus)
                            {
                                controller.GiveNamedItem("weapon_taser");
                            }
                        }
                    }
                });
            }
            return HookResult.Continue;
        }

        // The master loop. Runs every single server tick (usually 64 times a second).
        private void OnTick()
        {
            tickCounter++;
            float currentTime = Server.CurrentTime;
            float dt = Server.TickInterval;

            var players = Utilities.GetPlayers();
            var bots = players.Where(p => p != null && p.IsValid && p.IsBot && p.PawnIsAlive).ToList();
            var allAlivePlayers = players.Where(p => p != null && p.IsValid && p.PawnIsAlive).ToList();

            foreach (var bot in bots)
            {
                if (!agents.TryGetValue(bot.Index, out var agent))
                {
                    agent = new BotAgent(bot);
                    agents[bot.Index] = agent;
                }

                // Optimization: Stagger the heavy sensor scans over 5 ticks instead of running every bot simultaneously.
                if (tickCounter % 5 == bot.Index % 5) 
                {
                    agent.UpdateSensor(allAlivePlayers, currentTime);
                    agent.Memory.Update(dt * 5f); 
                }

                ProcessAgentIntelligence(agent, currentTime, dt);
                InjectMotorCommands(agent);
            }
        }

        // Core AI execution: Evaluates goals, runs actions, and controls aiming logic.
        private void ProcessAgentIntelligence(BotAgent agent, float currentTime, float dt)
        {
            // Pick the highest priority enemy from memory to focus on.
            var facts = agent.Memory.Facts.Values.OrderByDescending(f => f.ThreatLevel * f.Confidence).ToList();
            agent.Blackboard.CurrentTargetFact = facts.FirstOrDefault();
            
            // Wipe inputs every frame completely fresh to prevent actions getting "stuck".
            agent.Blackboard.ButtonsToPress = 0; 
            agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0);
            agent.Blackboard.DesiredSpeed = 0f;

            // Planner validation: If we don't have a plan, make one.
            if (agent.CurrentPlan.Count == 0 && agent.CurrentAction == null)
            {
                var topGoal = agent.Goals.OrderByDescending(g => g.GetPriority(agent)).First();
                if (topGoal.GetPriority(agent) > 0)
                {
                    agent.CurrentPlan = planner.Plan(agent, topGoal);
                }
            }

            // Move to the next step in the plan.
            if (agent.CurrentAction == null && agent.CurrentPlan.Count > 0)
            {
                agent.CurrentAction = agent.CurrentPlan.Dequeue();
                if (agent.CurrentAction.IsValid(agent))
                    agent.CurrentAction.OnEnter(agent);
                else
                    agent.Interrupt("Action Invalidated");
            }

            // Execute the current action (this populates the Blackboard with movement/aim requests).
            if (agent.CurrentAction != null)
            {
                agent.CurrentAction.Execute(agent, dt);
                if (agent.CurrentAction.IsDone(agent))
                {
                    agent.CurrentAction.OnExit(agent);
                    agent.CurrentAction = null;
                }
            }

            // The custom Aimbot logic block.
            if (agent.Blackboard.CurrentTargetFact != null && agent.Blackboard.DesiredMoveDirection.Length() > 0 && agent.Blackboard.DesiredSpeed > 0f)
            {
                Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector botPos = agent.Pawn.AbsOrigin!;
                
                float botEyeHeight = ((uint)agent.Pawn.Flags & 2) != 0 ? 46f : 64f; 
                float myEyeZ = botPos.Z + botEyeHeight;

                // 1. Organic Margin of Inaccuracy: Widen the reticle center so it wanders around arm/shoulder/chest level.
                // This makes the bot look human instead of locking onto a skeleton bone rigidly.
                float time = Server.CurrentTime;
                float noiseX = (float)Math.Sin(time * 4.2f + agent.Controller.Index) * 20f;
                float noiseY = (float)Math.Cos(time * 3.8f + agent.Controller.Index) * 20f;
                float noiseZ = (float)Math.Sin(time * 4.5f + agent.Controller.Index) * 15f;
                
                float focusX = targetPos.X + noiseX;
                float focusY = targetPos.Y + noiseY;
                
                float dx = focusX - botPos.X;
                float dy = focusY - botPos.Y;
                float distance2D = (float)Math.Sqrt(dx * dx + dy * dy);

                // Target approx chest/head height + organic vertical variance
                float focusZ = targetPos.Z + 50f + noiseZ; 
                
                // 2. Anti-Wallhack Visuals ("Pre-aiming" corners)
                if (distance2D > 350f) 
                {
                    // If target is far (potentially holding angle behind walls/radar spot), bias pitch heavily to the horizon.
                    // This creates a realistic "holding an angle" appearance instead of tracking height perfectly through walls.
                    focusZ = (myEyeZ * 0.7f) + (focusZ * 0.3f);
                }
                else 
                {
                    // Close combat: allow pitching down/up bounded to normal model limits.
                    focusZ = Math.Clamp(focusZ, targetPos.Z - 10f, targetPos.Z + 80f);
                }

                float dz = focusZ - myEyeZ;

                // Stabilize perfectYaw if the target is directly above or below, preventing spinning in place.
                float perfectYaw = distance2D > 5.0f ? (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI) : agent.Pawn.EyeAngles!.Y;
                float perfectPitch = (float)(Math.Atan2(-dz, distance2D) * 180.0 / Math.PI);
                
                float currentYaw = agent.Pawn.EyeAngles!.Y;
                float currentPitch = agent.Pawn.EyeAngles.X;
                
                // Calculate true angular differences so it doesn't spin 360 degrees the wrong way.
                float yawDiff = MathUtils.NormalizeAngle(perfectYaw - currentYaw);
                float pitchDiff = MathUtils.NormalizeAngle(perfectPitch - currentPitch);

                // Swift tracking: Constant-speed organic rotation instead of easing.
                // Easing causes the bot's crosshair to trail endlessly behind moving targets.
                float turnSpeed = agent.Blackboard.CurrentTargetFact.ThreatLevel > 100f ? 800f : 300f; // Degrees per second
                float maxStep = turnSpeed * dt;
                
                // Directly close the distance cap bounded by maxStep to avoid "floaty" aim.
                float yawStep = Math.Clamp(yawDiff, -maxStep, maxStep);
                float pitchStep = Math.Clamp(pitchDiff, -maxStep, maxStep);
                
                float newYaw = currentYaw + yawStep;
                float newPitch = Math.Clamp(currentPitch + pitchStep, -89f, 89f);
                
                // Finalize the desired looking angle to the blackboard.
                agent.Blackboard.DesiredAim = new QAngle(newPitch, newYaw, 0);
            }
        }

        // The physics override engine. Translates blackboard desires into literal server engine commands.
        private void InjectMotorCommands(BotAgent agent)
        {
            var pawn = agent.Pawn;
            if (pawn?.MovementServices == null || !pawn.IsValid) return;

            // If the custom AI doesn't explicitly request speed (i.e. normal map traversal), 
            // we hand complete control back to the Base CS2 Bot AI so they don't stare at walls!
            if (agent.Blackboard.DesiredSpeed <= 0f) return;

            QAngle outAngles = agent.Blackboard.DesiredAim;

            Vector currentVel = pawn.AbsVelocity ?? new Vector(0,0,0);
            Vector injectedVelocity = new Vector(currentVel.X, currentVel.Y, currentVel.Z);

            // Apply horizontal momentum.
            injectedVelocity.X = agent.Blackboard.DesiredMoveDirection.X * agent.Blackboard.DesiredSpeed;
            injectedVelocity.Y = agent.Blackboard.DesiredMoveDirection.Y * agent.Blackboard.DesiredSpeed;
            
            // Maintain grounded state seamlessly (pushing them into the floor artificially to stop floating).
            bool isGrounded = ((uint)pawn.Flags & 1) != 0;
            if (isGrounded)
            {
                injectedVelocity.Z = -15f; 
            }

            // Execute Vertical Jump logic.
            if ((agent.Blackboard.ButtonsToPress & (ulong)PlayerButtons.Jump) != 0)
            {
                bool safeToJump = true;
                if (agent.Blackboard.CurrentTargetFact != null)
                {
                    // Prevent the bot from jumping off a high ledge to their death accidentally.
                    Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                    float zDiff = pawn.AbsOrigin!.Z - targetPos.Z;
                    float xyDist = (float)Math.Sqrt(Math.Pow(pawn.AbsOrigin.X - targetPos.X, 2) + Math.Pow(pawn.AbsOrigin.Y - targetPos.Y, 2));
                    
                    if (zDiff > 100f && xyDist > 200f) safeToJump = false; 
                }
                
                // Allow the jump! The GOAP Action already checked and updated the Jump Cooldown before sending this button press!
                if (isGrounded && safeToJump)
                {
                    injectedVelocity.Z = 300f; // CS2 explicit jump momentum limit.
                }
                // We strip the jump button mask here so we don't pass it to the native engine, 
                // which avoids a bug where the bot tries to double-jump via both our custom physics AND the native physics.
                agent.Blackboard.ButtonsToPress &= ~((ulong)PlayerButtons.Jump);
            }

            // Submit buttons directly to engine memory (Crucial for Shooting / Ducking registering natively).
            pawn.MovementServices.Buttons.ButtonStates[0] |= agent.Blackboard.ButtonsToPress;

            // Teleport rigidly applying custom combat forces (keep physical body flat, only rotate Y axis).
            QAngle bodyAngles = new QAngle(0f, outAngles.Y, outAngles.Z);
            pawn.Teleport(null, bodyAngles, injectedVelocity);

            // Apply view angles for true "mouselook" tracking natively without leaning the hull of the player model.
            if (pawn.EyeAngles != null)
            {
                pawn.EyeAngles.X = outAngles.X;
                pawn.EyeAngles.Y = outAngles.Y;
                pawn.EyeAngles.Z = outAngles.Z;
            }
            if (pawn.V_angle != null)
            {
                pawn.V_angle.X = outAngles.X;
                pawn.V_angle.Y = outAngles.Y;
                pawn.V_angle.Z = outAngles.Z;
            }
        }

        public override void Unload(bool hotReload)
        {
            RemoveListener<Listeners.OnTick>(OnTick);
            RemoveListener<Listeners.OnMapStart>(OnMapStart);
            agents.Clear();
            assignedNames.Clear();
        }
    }
    #endregion
}
