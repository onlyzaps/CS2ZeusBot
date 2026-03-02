using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeusBotAI
{
    #region GOAP Core & Enums

    public abstract class GoapAction
    {
        public virtual bool CheckContextPrecondition(BotAgent agent) => true;
        public virtual bool IsValid(BotAgent agent) => true;
        public abstract bool IsDone(BotAgent agent);
        public abstract void Execute(BotAgent agent, float deltaTime);
        public virtual void OnEnter(BotAgent agent) { }
        public virtual void OnExit(BotAgent agent) { }
    }

    public abstract class GoapGoal
    {
        public abstract int GetPriority(BotAgent agent);
    }
    #endregion

    #region Fact & Memory Architecture

    public class Fact
    {
        public CCSPlayerController? Subject;
        public Vector LastKnownPosition = new Vector(0, 0, 0);
        public float Confidence = 1.0f;
        public float ThreatLevel = 0f;
        public float TimeSinceLastSeen = 0f;
    }

    public class WorkingMemory
    {
        public Dictionary<uint, Fact> Facts = new Dictionary<uint, Fact>();

        public void Update(float deltaTime)
        {
            var keys = Facts.Keys.ToList();
            foreach (var key in keys)
            {
                var fact = Facts[key];
                if (fact.Subject == null || !fact.Subject.IsValid || !fact.Subject.PawnIsAlive)
                {
                    Facts.Remove(key);
                    continue;
                }

                fact.TimeSinceLastSeen += deltaTime;
                if (fact.TimeSinceLastSeen > 0.5f) // if not seen half a second, drop confidence rapidly
                {
                    fact.Confidence -= deltaTime * 0.2f;
                }

                if (fact.Confidence <= 0)
                {
                    Facts.Remove(key);
                }
            }
        }
    }

    public class BotBlackboard
    {
        public Vector DesiredMoveDirection = new Vector(0, 0, 0);
        public float DesiredSpeed = 0f;
        public QAngle DesiredAim = new QAngle(0, 0, 0);
        public ulong ButtonsToPress = 0;
        public Fact? CurrentTargetFact = null;
        
        public float FearTimer = 0f;
        public float ActionCooldown = 0f;
        
        // Navigation & Traversal
        public float JumpCooldown = 0f;

        // Combat Engine
        public float NextStateTime = 0f;
        public int MovePattern = 0;
        public float StrafeDir = 1f;
        public int BhopCount = 0;
    }

    #endregion

    #region The Bot Agent

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

        public void UpdateSensor(List<CCSPlayerController> allPlayers, float currentTime)
        {
            if (Pawn == null || !Pawn.IsValid) return;
            Vector myPos = Pawn.AbsOrigin!;
            Vector myForward = MathUtils.GetForwardVector(Pawn.EyeAngles!);

            bool foundValidEnemyOnMap = false;

            foreach (var player in allPlayers)
            {
                if (player == Controller || !player.PawnIsAlive) continue;
                if (player.TeamNum == Controller.TeamNum) continue;

                var otherPawn = player.PlayerPawn.Value;
                if (otherPawn == null || !otherPawn.IsValid) continue;

                foundValidEnemyOnMap = true;
                Vector otherPos = otherPawn.AbsOrigin!;
                float dist = (myPos - otherPos).Length();
                Vector dirToOther = MathUtils.NormalizeVector(otherPos - myPos);

                // Add to memory
                if (!Memory.Facts.ContainsKey(player.Index))
                {
                    Memory.Facts[player.Index] = new Fact { Subject = player };
                }

                var fact = Memory.Facts[player.Index];
                
                // We grant global wallhack "LastKnownPosition" so Base AI can natively map route to them
                fact.LastKnownPosition = otherPos;
                fact.Confidence = 1.0f;

                // Completely eliminate cross-map sliding and wall staring by tightening Combat Thresholds
                float zDiff = Math.Abs(myPos.Z - otherPos.Z);
                bool isClose = dist < 750f; // Close enough to engage
                bool reasonableZ = zDiff < 350f; // Allow tracking on stairs/ramps, but prevent direct ceiling staring
                float dot = MathUtils.DotProduct(myForward, dirToOther);
                bool inFOV = dot > 0.15f; // Loosened FOV so bots don't lose targets jumping rapidly

                // Use native CS2 radar/spotted mechanics to evaluate visibility without leaking unmanaged memory
                bool isSpotted = otherPawn.EntitySpottedState?.Spotted ?? false;

                if (isClose && reasonableZ && inFOV && isSpotted)
                {
                    fact.TimeSinceLastSeen = 0f;
                    fact.ThreatLevel = 3000f; // Instantly trigger Custom GOAP Custom Physics
                }
                else
                {
                    // Not fighting. Let Base CS2 AI handle all NavMesh routing, freeze-time, and blind corners!
                    fact.ThreatLevel = 10f; 
                }
            }

            if (!foundValidEnemyOnMap)
            {
                var keys = Memory.Facts.Keys.ToList();
                foreach (var k in keys) Memory.Facts.Remove(k);
            }
        }

        public void Interrupt(string reason)
        {
            CurrentPlan.Clear();
            CurrentAction?.OnExit(this);
            CurrentAction = null;
        }

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

    public class GoalKillEnemy : GoapGoal
    {
        public override int GetPriority(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null ? 80 : 10;
    }

    public class GoalSurvive : GoapGoal
    {
        public override int GetPriority(BotAgent agent) => agent.Blackboard.FearTimer >= Server.CurrentTime ? 100 : 0;
    }

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
            if (agent.Blackboard.CurrentTargetFact != null && agent.Blackboard.CurrentTargetFact.ThreatLevel > 100f)
            {
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector(agent.Blackboard.CurrentTargetFact.LastKnownPosition - agent.Pawn.AbsOrigin!);
                agent.Blackboard.DesiredSpeed = 250f;
            }
            else
            {
                // Ensures we aren't sliding with our weapon out during freeze-time or cross-map traversal
                agent.Blackboard.DesiredSpeed = 0f;
                agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0);
            }
        }
    }

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

    // NEW: Clean Map Traversal (Sprinting out of spawn normally)
    public class ActionTraverseMap : GoapAction
    {
        public override bool CheckContextPrecondition(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        
        public override bool IsDone(BotAgent agent)
        {
            if (agent.Blackboard.CurrentTargetFact == null) return true;
            // Only stop traversing when we enter active combat (threat spikes from LOS or close proximity)
            return agent.Blackboard.CurrentTargetFact.ThreatLevel > 100f;
        }

        public override void Execute(BotAgent agent, float dt)
        {
            // Fully hand off to CS2 native bot AI for map navigation.
            // By NOT setting a DesiredSpeed, the InjectMotorCommands function will skip physics overrides,
            // allowing the bot to freely navigate navmesh/nodes until they spot a threat!
            agent.Blackboard.DesiredSpeed = 0f;
            
            // Also zero out DesiredMoveDirection so the aim lerp math below knows we are passive
            agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0);
        }
    }

    // Heavy Combat Approaching (B-hops and dodging)
    public class ActionApproachTarget : GoapAction
    {
        public override bool CheckContextPrecondition(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        
        public override bool IsDone(BotAgent agent) 
        {
            // Instantly abort and give back control to Native CS2 AI if threat drops (target dipped behind corner or retreated)
            if (agent.Blackboard.CurrentTargetFact == null || agent.Blackboard.CurrentTargetFact.ThreatLevel < 100f) return true;
            float dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
            bool hasZeus = agent.HasWeapon("taser");
            var activeWep = agent.Pawn?.WeaponServices?.ActiveWeapon.Value;
            bool holdsKnife = activeWep != null && activeWep.DesignerName.Contains("knife");

            // Stop to swap to Zeus just before kill range
            if (hasZeus && holdsKnife && dist < 450f) return true;
            
            return dist <= (hasZeus ? 210f : 65f);
        }

        public override void Execute(BotAgent agent, float dt)
        {
            Vector targetPos = agent.Blackboard.CurrentTargetFact!.LastKnownPosition;
            Vector myPos = agent.Pawn.AbsOrigin!;
            float dist = (myPos - targetPos).Length();
            
            // Dynamic Anti-Clump Offset (staggered by bot index)
            float clumpOffsetAngle = agent.Controller.Index * 36f + Server.CurrentTime * 3f;
            Vector approachPos = new Vector(targetPos.X + (float)Math.Cos(clumpOffsetAngle) * 55f, targetPos.Y + (float)Math.Sin(clumpOffsetAngle) * 55f, targetPos.Z);

            Vector dirToApproach = MathUtils.NormalizeVector(approachPos - myPos);
            Vector exactTargetDir = MathUtils.NormalizeVector(targetPos - myPos);
            Vector rightDir = new Vector(-exactTargetDir.Y, exactTargetDir.X, 0);

            bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
            float currentTime = Server.CurrentTime;

            if (currentTime > agent.Blackboard.NextStateTime)
            {
                Random r = new Random();
                // Favor jumping/strafing heavily (patterns 1, 2, 3) over general ground jitter (0)
                agent.Blackboard.MovePattern = r.Next(0, 4); 
                agent.Blackboard.NextStateTime = currentTime + (float)(r.NextDouble() * 1.0 + 0.5);
                agent.Blackboard.StrafeDir = r.NextDouble() > 0.5 ? 1f : -1f;
                agent.Blackboard.BhopCount = r.Next(2, 6); // More chained jumps
            }

            Vector mvmt = new Vector(0,0,0);
            float speed = 250f;

            if (agent.Blackboard.MovePattern == 1 || agent.Blackboard.MovePattern == 3)
            {
                // Active B-Hop Strafe
                if (isGrounded && agent.Blackboard.JumpCooldown < currentTime)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = currentTime + 0.3f;
                    agent.Blackboard.BhopCount--;
                    // Change strafe direction mid air randomly for harder tracking
                    if (agent.Blackboard.BhopCount % 2 == 0) agent.Blackboard.StrafeDir *= -1f; 
                }
                
                if (!isGrounded)
                {
                    // Sharp air strafes
                    mvmt = MathUtils.NormalizeVector((dirToApproach * 0.7f) + (rightDir * agent.Blackboard.StrafeDir * 1.0f));
                    speed = 280f; 
                    if (agent.Blackboard.BhopCount % 3 == 0) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
                }
                else
                {
                    mvmt = dirToApproach;
                }
            }
            else if (agent.Blackboard.MovePattern == 2)
            {
                // Wide Jump & Crouch-Slide
                if (isGrounded && agent.Blackboard.JumpCooldown < currentTime)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = currentTime + 0.6f;
                }
                float wideWeight = isGrounded ? 0.3f : 1.3f; 
                mvmt = MathUtils.NormalizeVector((dirToApproach * 0.7f) + (rightDir * agent.Blackboard.StrafeDir * wideWeight));
                if (!isGrounded) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
            }
            else 
            {
                // Ground Jitter (Serpentine)
                float jitter = (float)Math.Sin(currentTime * 5f + agent.Controller.Index);
                if (Math.Abs(jitter) > 0.5f) agent.Blackboard.StrafeDir = Math.Sign(jitter);
                mvmt = MathUtils.NormalizeVector((dirToApproach * 1.5f) + (rightDir * agent.Blackboard.StrafeDir * 0.4f));
            }

            agent.Blackboard.DesiredMoveDirection = mvmt;
            agent.Blackboard.DesiredSpeed = speed;
        }
    }

    public class ActionEvasiveRetreat : GoapAction
    {
        public override bool IsDone(BotAgent agent) => agent.Blackboard.FearTimer < Server.CurrentTime;
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact != null)
            {
                Vector tPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector myPos = agent.Pawn.AbsOrigin!;
                Vector dirAway = MathUtils.NormalizeVector(myPos - tPos);
                Vector right = new Vector(-dirAway.Y, dirAway.X, 0);

                bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
                
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

    public class ActionEngageZeus : GoapAction
    {
        public override bool CheckContextPrecondition(BotAgent agent) => true;
        public override bool IsValid(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        public override bool IsDone(BotAgent agent) 
        {
            if (agent.Blackboard.CurrentTargetFact == null || agent.Blackboard.CurrentTargetFact.ThreatLevel < 100f) return true;
            float dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
            return dist > 230f; // Target retreated out of active range, fail and re-plan
        }
        
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact == null) return;

            Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
            Vector myPos = agent.Pawn.AbsOrigin!;
            float dist = (targetPos - myPos).Length();
            
            // Fanning out when closing in to shoot
            float clusterOffset = agent.Controller.Index * 50f + Server.CurrentTime * 2f;
            Vector attackPos = new Vector(targetPos.X + (float)Math.Cos(clusterOffset) * 45f, targetPos.Y + (float)Math.Sin(clusterOffset) * 45f, targetPos.Z);

            Vector dirToApproach = MathUtils.NormalizeVector(attackPos - myPos);
            Vector exactDirToTarget = MathUtils.NormalizeVector(targetPos - myPos);
            Vector rightDir = new Vector(-exactDirToTarget.Y, exactDirToTarget.X, 0);

            bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
            float time = Server.CurrentTime;

            // Combat Dance
            if (time > agent.Blackboard.NextStateTime)
            {
                Random r = new Random();
                agent.Blackboard.MovePattern = r.Next(0, 3); // Better combat variety
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

            // Aim Math
            float eyeHeight = ((uint)agent.Pawn.Flags & 2) != 0 ? 46f : 64f; 
            Vector botHead = new Vector(myPos.X, myPos.Y, myPos.Z + eyeHeight);
            
            float targetZ = Math.Clamp(botHead.Z, targetPos.Z, targetPos.Z + 72f);
            Vector exactTarget = new Vector(targetPos.X, targetPos.Y, targetZ);
            Vector exactDir = MathUtils.NormalizeVector(exactTarget - botHead);
            Vector curForward = MathUtils.GetForwardVector(agent.Pawn.EyeAngles!);
            
            float aimDot = MathUtils.DotProduct(curForward, exactDir);
            
            // Firing threshold
            if (aimDot > 0.994f || (dist < 120f && aimDot > 0.980f)) 
            {
                if (agent.Blackboard.ActionCooldown <= Server.CurrentTime)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack;
                    agent.Blackboard.ActionCooldown = Server.CurrentTime + 0.5f; 
                }
            }
        }
    }

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
                agent.Blackboard.DesiredSpeed = 270f; // Slightly faster for knife
                
                // Jump randomly if close to throw off enemy aim
                if (((uint)agent.Pawn.Flags & 1) != 0 && agent.Blackboard.JumpCooldown < Server.CurrentTime && (targetPos - myPos).Length() < 150f)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = Server.CurrentTime + 0.8f;
                }
            }
            if (agent.Blackboard.ActionCooldown <= Server.CurrentTime)
            {
                agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack2; 
                agent.Blackboard.ActionCooldown = Server.CurrentTime + 0.8f;
            }
        }
    }

    #endregion

    #region The GOAP Planner 

    public class GoapPlanner
    {
        public Queue<GoapAction> Plan(BotAgent agent, GoapGoal goal)
        {
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

                if (!inActiveCombat)
                {
                    // Far away or tracking behind walls -> Knife sprint
                    var equipKnife = usableActions.FirstOrDefault(a => a is ActionEquipKnife);
                    if (equipKnife != null) plan.Add(equipKnife);
                    
                    var traverse = usableActions.FirstOrDefault(a => a is ActionTraverseMap);
                    if (traverse != null) plan.Add(traverse);
                }
                else
                {
                    // In active combat logic 
                    if (hasZeus)
                    {
                        if (dist > 450f)
                        {
                            var prepKnife = usableActions.FirstOrDefault(a => a is ActionEquipKnife);
                            if (prepKnife != null) plan.Add(prepKnife);
                            
                            var approach = usableActions.FirstOrDefault(a => a is ActionApproachTarget);
                            if (approach != null) plan.Add(approach);
                        }
                        else
                        {
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
                        // No Zeus, pure knife run
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
    public class ZeusBotAIGoapPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Refactored Core)";
        public override string ModuleVersion => "3.0.0";

        private Dictionary<uint, BotAgent> agents = new Dictionary<uint, BotAgent>();
        private GoapPlanner planner = new GoapPlanner();
        private int tickCounter = 0;

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
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            Console.WriteLine("[Zeus Bot GOAP] Core Engine v3 loaded.");
        }

        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            var controller = @event.Userid;
            if (controller != null && controller.IsValid && controller.IsBot)
            {
                if (!assignedNames.ContainsKey(controller.Index))
                {
                    assignedNames[controller.Index] = BotNames[rnd.Next(BotNames.Length)];
                }
                string desiredName = assignedNames[controller.Index];

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

                if (tickCounter % 5 == bot.Index % 5) 
                {
                    agent.UpdateSensor(allAlivePlayers, currentTime);
                    agent.Memory.Update(dt * 5f); 
                }

                ProcessAgentIntelligence(agent, currentTime, dt);
                InjectMotorCommands(agent);
            }
        }

        private void ProcessAgentIntelligence(BotAgent agent, float currentTime, float dt)
        {
            var facts = agent.Memory.Facts.Values.OrderByDescending(f => f.ThreatLevel * f.Confidence).ToList();
            agent.Blackboard.CurrentTargetFact = facts.FirstOrDefault();
            
            // Wipe inputs every frame completely fresh
            agent.Blackboard.ButtonsToPress = 0; 
            agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0);
            agent.Blackboard.DesiredSpeed = 0f;

            // Planner validation
            if (agent.CurrentPlan.Count == 0 && agent.CurrentAction == null)
            {
                var topGoal = agent.Goals.OrderByDescending(g => g.GetPriority(agent)).First();
                if (topGoal.GetPriority(agent) > 0)
                {
                    agent.CurrentPlan = planner.Plan(agent, topGoal);
                }
            }

            if (agent.CurrentAction == null && agent.CurrentPlan.Count > 0)
            {
                agent.CurrentAction = agent.CurrentPlan.Dequeue();
                if (agent.CurrentAction.IsValid(agent))
                    agent.CurrentAction.OnEnter(agent);
                else
                    agent.Interrupt("Action Invalidated");
            }

            if (agent.CurrentAction != null)
            {
                agent.CurrentAction.Execute(agent, dt);
                if (agent.CurrentAction.IsDone(agent))
                {
                    agent.CurrentAction.OnExit(agent);
                    agent.CurrentAction = null;
                }
            }

            // Aim alignment
            if (agent.Blackboard.CurrentTargetFact != null && agent.Blackboard.DesiredMoveDirection.Length() > 0 && agent.Blackboard.DesiredSpeed > 0f)
            {
                Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector botPos = agent.Pawn.AbsOrigin!;
                
                float dx = targetPos.X - botPos.X;
                float dy = targetPos.Y - botPos.Y;
                
                // Allow bots to aim at any height of the enemy's body without jerking their pitch unnecessarily
                float botEyeHeight = ((uint)agent.Pawn.Flags & 2) != 0 ? 46f : 64f; 
                float myEyeZ = botPos.Z + botEyeHeight;
                
                // Clamp target Z to the enemy's vertical boundary (0 to 72 units high) so we hit head or toes comfortably
                float targetZ = Math.Clamp(myEyeZ, targetPos.Z, targetPos.Z + 72f); 
                float dz = targetZ - myEyeZ; 

                float distance2D = (float)Math.Sqrt(dx * dx + dy * dy);

                // Stabilize perfectYaw if the target is directly above or below, preventing spinning in place
                float perfectYaw = distance2D > 5.0f ? (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI) : agent.Pawn.EyeAngles!.Y;
                float perfectPitch = (float)(Math.Atan2(-dz, distance2D) * 180.0 / Math.PI);
                
                float currentYaw = agent.Pawn.EyeAngles!.Y;
                float currentPitch = agent.Pawn.EyeAngles.X;
                
                float aimLerp = agent.Blackboard.CurrentTargetFact.ThreatLevel > 100f ? 0.8f : 0.3f; // Snappier aim in combat
                
                float newYaw = currentYaw + MathUtils.NormalizeAngle(perfectYaw - currentYaw) * aimLerp;
                float newPitch = Math.Clamp(currentPitch + MathUtils.NormalizeAngle(perfectPitch - currentPitch) * aimLerp, -89f, 89f);
                
                agent.Blackboard.DesiredAim = new QAngle(newPitch, newYaw, 0);
            }
        }

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

            injectedVelocity.X = agent.Blackboard.DesiredMoveDirection.X * agent.Blackboard.DesiredSpeed;
            injectedVelocity.Y = agent.Blackboard.DesiredMoveDirection.Y * agent.Blackboard.DesiredSpeed;
            
            // Maintain grounded state seamlessly
            bool isGrounded = ((uint)pawn.Flags & 1) != 0;
            if (isGrounded)
            {
                injectedVelocity.Z = -15f; 
            }

            // Execute Vertical Jump
            if ((agent.Blackboard.ButtonsToPress & (ulong)PlayerButtons.Jump) != 0)
            {
                bool safeToJump = true;
                if (agent.Blackboard.CurrentTargetFact != null)
                {
                    Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                    float zDiff = pawn.AbsOrigin!.Z - targetPos.Z;
                    float xyDist = (float)Math.Sqrt(Math.Pow(pawn.AbsOrigin.X - targetPos.X, 2) + Math.Pow(pawn.AbsOrigin.Y - targetPos.Y, 2));
                    
                    if (zDiff > 100f && xyDist > 200f) safeToJump = false; 
                }
                
                // Allow the jump! The GOAP Action already checked and updated the Jump Cooldown before sending this button press!
                if (isGrounded && safeToJump)
                {
                    injectedVelocity.Z = 300f; // CS2 explicit jump momentum
                }
                // Do not pass the jump button to the backend engine to avoid double-jumping physics conflicts
                agent.Blackboard.ButtonsToPress &= ~((ulong)PlayerButtons.Jump);
            }

            // Submit buttons to engine (Crucial for Shooting / Ducking)
            pawn.MovementServices.Buttons.ButtonStates[0] |= agent.Blackboard.ButtonsToPress;

            // Teleport rigidly applying custom combat forces (keep physical body flat)
            QAngle bodyAngles = new QAngle(0f, outAngles.Y, outAngles.Z);
            pawn.Teleport(null, bodyAngles, injectedVelocity);

            // Apply view angles for true "mouselook" tracking natively without leaning the hull
            if (pawn.EyeAngles != null)
            {
                pawn.EyeAngles.X = outAngles.X;
                pawn.EyeAngles.Y = outAngles.Y;
                pawn.EyeAngles.Z = outAngles.Z;
            }
        }

        public override void Unload(bool hotReload)
        {
            RemoveListener<Listeners.OnTick>(OnTick);
            agents.Clear();
            assignedNames.Clear();
        }
    }
    #endregion
}
