using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeusBotAI
{
    #region GOAP Core & Enums
    public enum StateKey
    {
        TargetDead,
        HasTarget,
        TargetInZeusRange,
        TargetInKnifeRange,
        HasZeus,
        HasNade,
        IsSafe,
        WeaponEquipped
    }

    public class GoapState
    {
        public Dictionary<StateKey, bool> Values { get; set; } = new Dictionary<StateKey, bool>();
        
        public GoapState Clone()
        {
            var clone = new GoapState();
            foreach (var kvp in Values) clone.Values[kvp.Key] = kvp.Value;
            return clone;
        }

        public bool Matches(GoapState requirements)
        {
            foreach (var req in requirements.Values)
            {
                if (!Values.ContainsKey(req.Key) || Values[req.Key] != req.Value)
                    return false;
            }
            return true;
        }

        public void Apply(GoapState effects)
        {
            foreach (var eff in effects.Values) Values[eff.Key] = eff.Value;
        }
    }

    public abstract class GoapAction
    {
        public string Name { get; protected set; } = "BaseAction";
        public float Cost { get; protected set; } = 1.0f;
        public GoapState Preconditions { get; } = new GoapState();
        public GoapState Effects { get; } = new GoapState();

        // Check #1: Procedural Context Precondition
        public virtual bool CheckContextPrecondition(BotAgent agent) => true;

        // Check #2: Sanity Check right before execution
        public virtual bool IsValid(BotAgent agent) => true;

        // Check #3: Execution state / Animation checking
        public abstract bool IsDone(BotAgent agent);
        public abstract void Execute(BotAgent agent, float deltaTime);
        public virtual void OnEnter(BotAgent agent) { }
        public virtual void OnExit(BotAgent agent) { }
    }

    public abstract class GoapGoal
    {
        public string Name { get; protected set; } = "BaseGoal";
        public int Priority { get; protected set; }
        public GoapState DesiredState { get; } = new GoapState();
        
        // Dynamic priority assessment based on Working Memory
        public virtual int GetPriority(BotAgent agent) => Priority; 
    }
    #endregion

    #region F.E.A.R. Agent Architecture (Part II Concepts)

    // Working Memory Fact
    public class Fact
    {
        public CCSPlayerController? Subject;
        public Vector LastKnownPosition = new Vector(0,0,0);
        public float Confidence = 1.0f; // Decays over time (0.0 to 1.0)
        public float ThreatLevel = 0f;
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
                    continue; // Forget immediately if dead
                }

                // Decay confidence over time when out of sight
                fact.Confidence -= deltaTime * 0.05f; 
                if (fact.Confidence <= 0)
                {
                    Facts.Remove(key); // Forget target
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
        public List<Vector> NearbyAllies = new List<Vector>();
        
        // Advanced Movement State Machine
        public float NextStateTime = 0f;
        public int MovePattern = 0; 
        public float StrafeDir = 1f;
        public float JumpCooldown = 0f;
        public int BhopCount = 0;
    }

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
            // Goals
            Goals.Add(new GoalSurvive());
            Goals.Add(new GoalKillEnemy());
            
            // Actions
            AvailableActions.Add(new ActionEquipZeus());
            AvailableActions.Add(new ActionEquipKnife());
            AvailableActions.Add(new ActionThrowNade());
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
            
            Blackboard.NearbyAllies.Clear();

            // Track if we found ANY enemies to keep moving towards
            bool foundEnemy = false;

            foreach (var player in allPlayers)
            {
                if (player == Controller || !player.PawnIsAlive) continue;
                var otherPawn = player.PlayerPawn.Value;
                if (otherPawn == null || !otherPawn.IsValid) continue;

                Vector otherPos = otherPawn.AbsOrigin!;
                float dist = (myPos - otherPos).Length();
                Vector dirToOther = MathUtils.NormalizeVector(otherPos - myPos);
                
                if (player.TeamNum == Controller.TeamNum)
                {
                    if (dist < 300f) // Keep track of allies to avoid crowding
                    {
                        Blackboard.NearbyAllies.Add(otherPos);
                    }
                    continue; // Skip tracking allies as targets
                }

                // If not our team, it's an enemy (so T bots track CT players, CT bots track T players)
                foundEnemy = true;

                // Global Knowledge Tracker - keeps them moving across map
                if (!Memory.Facts.ContainsKey(player.Index))
                    Memory.Facts[player.Index] = new Fact { Subject = player };

                var fact = Memory.Facts[player.Index];
                fact.LastKnownPosition = otherPos;
                fact.Confidence = 1.0f; 

                // LOS Check for entering combat mode
                float dot = MathUtils.DotProduct(myForward, dirToOther);
                bool inFOV = dot > 0.4f;

                if (inFOV || dist < 500f)
                {
                    // Combat Mode Threat Calculation
                    Vector enemyForward = MathUtils.GetForwardVector(otherPawn.EyeAngles!);
                    float enemyAimDot = MathUtils.DotProduct(enemyForward, dirToOther * -1);
                    fact.ThreatLevel = Math.Max(0, (5000f - dist) * 2f); 
                    
                    if (enemyAimDot > 0.9f) fact.ThreatLevel += 3000f; 
                    if (dist < 300f) fact.ThreatLevel += 5000f; 
                    
                    if (enemyAimDot > 0.95f && dist > 800f && Blackboard.FearTimer < currentTime)
                    {
                        Blackboard.FearTimer = currentTime + 0.8f;
                        Interrupt("High Threat Detected - Evasive Retreat");
                    }
                }
                else
                {
                    // Passthrough mode - just moving towards general area
                    fact.ThreatLevel = 100f;
                }
            }

            // If we have no enemies on the map (e.g., all dead or not spawned), clear our targets so we don't act weird
            if (!foundEnemy)
            {
                var keys = Memory.Facts.Keys.ToList();
                foreach (var key in keys) Memory.Facts.Remove(key);
            }
        }

        public void Interrupt(string reason)
        {
            CurrentPlan.Clear();
            CurrentAction?.OnExit(this);
            CurrentAction = null;
        }

        public GoapState GetWorldState()
        {
            var state = new GoapState();
            state.Values[StateKey.TargetDead] = Blackboard.CurrentTargetFact == null;
            state.Values[StateKey.HasTarget] = Blackboard.CurrentTargetFact != null;
            state.Values[StateKey.IsSafe] = Blackboard.FearTimer < Server.CurrentTime;
            
            if (Blackboard.CurrentTargetFact != null)
            {
                float dist = (Pawn.AbsOrigin! - Blackboard.CurrentTargetFact.LastKnownPosition).Length();
                state.Values[StateKey.TargetInZeusRange] = dist < 200f; // Buffer for range
                state.Values[StateKey.TargetInKnifeRange] = dist < 70f;
            }
            
            state.Values[StateKey.HasZeus] = HasWeapon("taser");
            state.Values[StateKey.HasNade] = HasWeapon("hegrenade");
            state.Values[StateKey.WeaponEquipped] = true; 

            return state;
        }

        private bool HasWeapon(string name)
        {
            if (Pawn?.WeaponServices?.MyWeapons == null) return false;
            foreach (var w in Pawn.WeaponServices.MyWeapons)
            {
                var weapon = w.Value;
                if (weapon != null && weapon.DesignerName != null && weapon.DesignerName.Contains(name))
                {
                    // By removing the Clip1 check, we make sure they still pull out the Zeus 
                    // even if CS2 initializes its clip slowly immediately upon giving it to them.
                    return true;
                }
            }
            return false;
        }
    }
    #endregion

    #region Goals & Actions Implementation

    public class ActionEquipZeus : GoapAction
    {
        public ActionEquipZeus()
        {
            Name = "Equip Zeus";
            Preconditions.Values[StateKey.HasZeus] = true;
            Effects.Values[StateKey.WeaponEquipped] = true;
            Cost = 1f;
        }

        public override bool IsDone(BotAgent agent)
        {
            var activeWeapon = agent.Pawn?.WeaponServices?.ActiveWeapon.Value;
            return activeWeapon != null && activeWeapon.DesignerName.Contains("taser");
        }

        public override void Execute(BotAgent agent, float dt)
        {
            var weaponServices = agent.Pawn?.WeaponServices;
            if (weaponServices?.MyWeapons == null) return;

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
    }

    public class ActionEquipKnife : GoapAction
    {
        public ActionEquipKnife()
        {
            Name = "Equip Knife";
            // No preconditions needed, bots always have a knife
            Effects.Values[StateKey.WeaponEquipped] = true;
            Cost = 1f;
        }

        public override bool IsDone(BotAgent agent)
        {
            var activeWeapon = agent.Pawn?.WeaponServices?.ActiveWeapon.Value;
            return activeWeapon != null && activeWeapon.DesignerName.Contains("knife");
        }

        public override void Execute(BotAgent agent, float dt)
        {
            var weaponServices = agent.Pawn?.WeaponServices;
            if (weaponServices?.MyWeapons == null) return;

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
    }

    public class ActionEngageKnife : GoapAction
    {
        public ActionEngageKnife()
        {
            Name = "Engage with Knife";
            Preconditions.Values[StateKey.TargetInKnifeRange] = true;
            Preconditions.Values[StateKey.WeaponEquipped] = true;
            Effects.Values[StateKey.TargetDead] = true;
            Cost = 2f; 
        }
        
        public override bool CheckContextPrecondition(BotAgent agent) => true;
        
        public override bool IsValid(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        public override bool IsDone(BotAgent agent) 
        {
            if (agent.Blackboard.CurrentTargetFact == null) return true;
            float dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
            return dist > 85f; // Abort if target escapes knife range
        }
        
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact != null)
            {
                Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector myPos = agent.Pawn.AbsOrigin!;
                Vector dirToTarget = MathUtils.NormalizeVector(targetPos - myPos);
                Vector rightDir = new Vector(-dirToTarget.Y, dirToTarget.X, 0);

                bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
                float currentTime = Server.CurrentTime;

                // Random hop right-click
                if (isGrounded && agent.Blackboard.JumpCooldown < currentTime && Math.Sin(currentTime * 10f) > 0.6f)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = currentTime + 0.6f;
                }
                
                // Add evasive strafe so it's not a perfect line
                float erraticSine = (float)Math.Sin(currentTime * 20f);
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector((dirToTarget * 2.0f) + (rightDir * Math.Sign(erraticSine) * 0.5f)); 
                agent.Blackboard.DesiredSpeed = 260f;
            }
            if (agent.Blackboard.ActionCooldown <= Server.CurrentTime)
            {
                agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack2; 
                agent.Blackboard.ActionCooldown = Server.CurrentTime + 0.8f;
            }
        }
    }

    public class ActionThrowNade : GoapAction
    {
        public ActionThrowNade()
        {
            Name = "Throw Nade";
            Preconditions.Values[StateKey.HasNade] = true;
            Preconditions.Values[StateKey.HasTarget] = true;
            Effects.Values[StateKey.TargetDead] = true;
            Cost = 3f; 
        }
        
        public override bool CheckContextPrecondition(BotAgent agent) => true;

        public override bool IsDone(BotAgent agent) => !HasWeapon(agent, "hegrenade");

        public override void Execute(BotAgent agent, float dt)
        {
            var weaponServices = agent.Pawn?.WeaponServices;
            if (weaponServices?.MyWeapons == null) return;

            var activeWeapon = weaponServices.ActiveWeapon.Value;
            
            if (activeWeapon == null || !activeWeapon.DesignerName.Contains("hegrenade"))
            {
                foreach (var weaponHandle in weaponServices.MyWeapons)
                {
                    var weapon = weaponHandle.Value;
                    if (weapon != null && weapon.DesignerName != null && weapon.DesignerName.Contains("hegrenade"))
                    {
                        weaponServices.ActiveWeapon.Raw = weaponHandle.Raw;
                        Utilities.SetStateChanged(agent.Pawn!, "CBasePlayerPawn", "m_pWeaponServices");
                        break;
                    }
                }
            }
            else
            {
                if (agent.Blackboard.ActionCooldown <= Server.CurrentTime)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack;
                    agent.Blackboard.ActionCooldown = Server.CurrentTime + 2.0f; // Triggers done state and puts on cooldown
                }
            }
        }
        
        private bool HasWeapon(BotAgent agent, string name)
        {
            if (agent.Pawn?.WeaponServices?.MyWeapons == null) return false;
            foreach (var w in agent.Pawn.WeaponServices.MyWeapons)
            {
                if (w.Value != null && w.Value.DesignerName.Contains(name)) return true;
            }
            return false;
        }
    }

    public class GoalKillEnemy : GoapGoal
    {
        public GoalKillEnemy() { Name = "Kill Enemy"; Priority = 50; DesiredState.Values[StateKey.TargetDead] = true; }
        public override int GetPriority(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null ? 80 : 10;
    }

    public class GoalSurvive : GoapGoal
    {
        public GoalSurvive() { Name = "Survive"; Priority = 100; DesiredState.Values[StateKey.IsSafe] = true; }
        public override int GetPriority(BotAgent agent) => agent.Blackboard.FearTimer >= Server.CurrentTime ? 100 : 0;
    }

    public class ActionEvasiveRetreat : GoapAction
    {
        public ActionEvasiveRetreat()
        {
            Name = "Evasive Retreat";
            Preconditions.Values[StateKey.IsSafe] = false;
            Effects.Values[StateKey.IsSafe] = true;
            Cost = 1f;
        }
        public override bool IsDone(BotAgent agent) => agent.Blackboard.FearTimer < Server.CurrentTime;
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact != null)
            {
                Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector myPos = agent.Pawn.AbsOrigin!;
                Vector dirAway = MathUtils.NormalizeVector(myPos - targetPos);
                Vector rightDir = new Vector(-dirAway.Y, dirAway.X, 0);

                bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
                float currentTime = Server.CurrentTime;

                if (currentTime > agent.Blackboard.NextStateTime)
                {
                    Random r = new Random();
                    agent.Blackboard.MovePattern = r.Next(0, 3);
                    agent.Blackboard.NextStateTime = currentTime + (float)(r.NextDouble() * 0.8 + 0.3);
                    agent.Blackboard.StrafeDir = r.NextDouble() > 0.5 ? 1f : -1f;
                }

                Vector intendedMvmt = new Vector(0,0,0);
                
                if (agent.Blackboard.MovePattern == 0) // Air dodge retreat
                {
                    if (isGrounded && agent.Blackboard.JumpCooldown < currentTime)
                    {
                        agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                        agent.Blackboard.JumpCooldown = currentTime + 0.5f;
                    }
                    intendedMvmt = MathUtils.NormalizeVector((dirAway * 0.8f) + (rightDir * agent.Blackboard.StrafeDir * 1.5f));
                    if (!isGrounded) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck; // tuck legs to lower hitbox
                }
                else if (agent.Blackboard.MovePattern == 1) // ZigZag fast retreat
                {
                    float erraticSine = (float)Math.Sin(currentTime * 15f + agent.Controller.Index);
                    intendedMvmt = MathUtils.NormalizeVector((dirAway * 1.5f) + (rightDir * Math.Sign(erraticSine)));
                }
                else // Slide retreat
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
                    intendedMvmt = MathUtils.NormalizeVector((dirAway * 0.5f) + (rightDir * agent.Blackboard.StrafeDir * 1.2f));
                }

                // Keep away from allies to avoid easy collaterals/grouping
                Vector separation = new Vector(0,0,0);
                foreach (var allyPos in agent.Blackboard.NearbyAllies)
                {
                    Vector repulse = agent.Pawn.AbsOrigin! - allyPos;
                    if (repulse.Length() < 150f) 
                        separation += MathUtils.NormalizeVector(repulse) * 2f;
                }
                
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector(intendedMvmt + separation);
                agent.Blackboard.DesiredSpeed = 260f; // flee fast!
            }
        }
    }

    public class ActionApproachTarget : GoapAction
    {
        public ActionApproachTarget()
        {
            Name = "Approach Target";
            Preconditions.Values[StateKey.HasTarget] = true;
            Effects.Values[StateKey.TargetInZeusRange] = true;
        }
        public override bool CheckContextPrecondition(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        public override bool IsDone(BotAgent agent) 
        {
            if (agent.Blackboard.CurrentTargetFact == null) return true;
            float targetDist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
            bool hasZeus = agent.GetWorldState().Values.GetValueOrDefault(StateKey.HasZeus, false);
            
            var activeWeapon = agent.Pawn?.WeaponServices?.ActiveWeapon.Value;
            bool isHoldingKnife = activeWeapon != null && activeWeapon.DesignerName.Contains("knife");

            // Stop approach so we can swap to Zeus before we hit lethal range
            if (hasZeus && isHoldingKnife && targetDist <= 400f)
            {
                return true; 
            }

            return targetDist <= (hasZeus ? 200f : 65f); // Close the gap to lethal range
        }
        public override void Execute(BotAgent agent, float dt)
        {
            Vector targetPos = agent.Blackboard.CurrentTargetFact!.LastKnownPosition;
            Vector myPos = agent.Pawn.AbsOrigin!;
            float dist = (myPos - targetPos).Length();
            Vector dirToTarget = MathUtils.NormalizeVector(targetPos - myPos);
            Vector rightDir = new Vector(-dirToTarget.Y, dirToTarget.X, 0);

            bool inCombatMode = dist < 800f && agent.Blackboard.CurrentTargetFact.ThreatLevel > 500f;

            if (!inCombatMode)
            {
                // Calm approach: linear sprint to close distance without erratic jumping
                agent.Blackboard.DesiredMoveDirection = dirToTarget;
                agent.Blackboard.DesiredSpeed = 250f;
                return;
            }

            bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
            float currentTime = Server.CurrentTime;

            if (currentTime > agent.Blackboard.NextStateTime)
            {
                Random r = new Random();
                int patternCount = dist > 300f ? 3 : 2;
                agent.Blackboard.MovePattern = r.Next(0, patternCount);
                agent.Blackboard.NextStateTime = currentTime + (float)(r.NextDouble() * 1.2 + 0.5);
                agent.Blackboard.StrafeDir = r.NextDouble() > 0.5 ? 1f : -1f;
                agent.Blackboard.BhopCount = r.Next(2, 5);
            }

            Vector intendedMvmt = new Vector(0,0,0);
            float speed = 250f;

            if (agent.Blackboard.MovePattern == 1 && agent.Blackboard.BhopCount > 0)
            {
                // B-hopping logic
                if (isGrounded && agent.Blackboard.JumpCooldown < currentTime)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = currentTime + 0.3f;
                    agent.Blackboard.BhopCount--;
                    agent.Blackboard.StrafeDir *= -1f; 
                }
                
                if (!isGrounded)
                {
                    intendedMvmt = MathUtils.NormalizeVector((dirToTarget * 0.8f) + (rightDir * agent.Blackboard.StrafeDir * 0.7f));
                    speed = 280f; // Extra speed while bhoping
                }
                else
                {
                    intendedMvmt = dirToTarget;
                }
            }
            else if (agent.Blackboard.MovePattern == 2)
            {
                // Wide Air-strafe dodge
                if (isGrounded && agent.Blackboard.JumpCooldown < currentTime)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                    agent.Blackboard.JumpCooldown = currentTime + 0.6f;
                }
                float wideWeight = isGrounded ? 0.3f : 1.5f; 
                intendedMvmt = MathUtils.NormalizeVector((dirToTarget * 0.6f) + (rightDir * agent.Blackboard.StrafeDir * wideWeight));
                
                if (!isGrounded && Math.Sin(currentTime * 10f) > 0) 
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
                }
            }
            else 
            {
                // Fast push / Jitter
                float jitter = (float)Math.Sin(currentTime * 12f + agent.Controller.Index);
                if (Math.Abs(jitter) > 0.8f) agent.Blackboard.StrafeDir = Math.Sign(jitter);
                
                intendedMvmt = MathUtils.NormalizeVector((dirToTarget * 1.5f) + (rightDir * agent.Blackboard.StrafeDir * 0.4f));
                if (jitter > 0.9f && dist < 500f)
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
                }
            }

            Vector separation = new Vector(0,0,0);
            foreach (var allyPos in agent.Blackboard.NearbyAllies)
            {
                Vector repulse = myPos - allyPos;
                if (repulse.Length() < 200f)
                    separation += MathUtils.NormalizeVector(repulse) * (isGrounded ? 1.5f : 0.5f);
            }

            agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector(intendedMvmt + separation);
            agent.Blackboard.DesiredSpeed = speed;
        }
    }

    public class ActionEngageZeus : GoapAction
    {
        public ActionEngageZeus()
        {
            Name = "Engage with Zeus";
            Preconditions.Values[StateKey.TargetInZeusRange] = true;
            Preconditions.Values[StateKey.HasZeus] = true;
            Effects.Values[StateKey.TargetDead] = true;
        }
        
        public override bool CheckContextPrecondition(BotAgent agent) => true;
        
        public override bool IsValid(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        
        // Remove the 2-second timeout give up since Zeus recharges fast, and let them keep trying until target dies or leaves range
        public override bool IsDone(BotAgent agent) 
        {
            if (agent.Blackboard.CurrentTargetFact == null) return true;

            float dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
            return dist > 210f; // Target escaped Zeus range, abort engagement
        }
        
        public override void OnEnter(BotAgent agent) 
        {
            // Removed give up time initialization
        }
        
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact != null)
            {
                Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector myPos = agent.Pawn.AbsOrigin!;
                float distToTarget = (targetPos - myPos).Length();
                Vector dirToTarget = MathUtils.NormalizeVector(targetPos - myPos);
                Vector rightDir = new Vector(-dirToTarget.Y, dirToTarget.X, 0);

                bool isGrounded = ((uint)agent.Pawn.Flags & 1) != 0;
                float currentTime = Server.CurrentTime;

                if (currentTime > agent.Blackboard.NextStateTime)
                {
                    Random r = new Random();
                    agent.Blackboard.MovePattern = r.Next(0, 3);
                    agent.Blackboard.NextStateTime = currentTime + (float)(r.NextDouble() * 0.8 + 0.3);
                    agent.Blackboard.StrafeDir = r.NextDouble() > 0.5 ? 1f : -1f;
                }

                float speed = 250f;
                Vector intendedMvmt = new Vector(0,0,0);

                if (agent.Blackboard.MovePattern == 2)
                {
                    // Full jump peek / Air strafe dodging
                    if (isGrounded && agent.Blackboard.JumpCooldown < currentTime)
                    {
                        agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                        agent.Blackboard.JumpCooldown = currentTime + 0.5f;
                    }
                    if (!isGrounded)
                    {
                        intendedMvmt = MathUtils.NormalizeVector((dirToTarget * 0.2f) + (rightDir * agent.Blackboard.StrafeDir * 1.5f));
                        speed = 280f;
                    }
                    else
                    {
                        intendedMvmt = MathUtils.NormalizeVector((dirToTarget * 1.2f) + (rightDir * agent.Blackboard.StrafeDir * 0.5f));
                    }
                }
                else if (agent.Blackboard.MovePattern == 1)
                {
                    // Crouch approaching
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
                    intendedMvmt = MathUtils.NormalizeVector(dirToTarget + (rightDir * agent.Blackboard.StrafeDir * 0.4f));
                }
                else
                {
                    // Hard ADAD Spam
                    float erraticSine = (float)Math.Sin(currentTime * 20f + agent.Controller.Index);
                    float strafeVal = Math.Sign(erraticSine);
                    
                    if (distToTarget < 160f)
                    {
                        if (erraticSine > 0.8f) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
                        intendedMvmt = MathUtils.NormalizeVector((dirToTarget * 0.1f) + (rightDir * strafeVal));
                    }
                    else
                    {
                        intendedMvmt = MathUtils.NormalizeVector((dirToTarget * 0.8f) + (rightDir * strafeVal * 1.2f));
                    }
                }
                
                agent.Blackboard.DesiredMoveDirection = intendedMvmt;
                agent.Blackboard.DesiredSpeed = speed;
                
                // Track aim precisely (upper chest center mass)
                float eyeHeight = ((uint)agent.Pawn.Flags & 2) != 0 ? 46f : 64f; // crouch flag 
                Vector botHead = new Vector(myPos.X, myPos.Y, myPos.Z + eyeHeight);
                Vector enemyChest = new Vector(targetPos.X, targetPos.Y, targetPos.Z + 40f);
                Vector exactDir = MathUtils.NormalizeVector(enemyChest - botHead);
                Vector currentForward = MathUtils.GetForwardVector(agent.Pawn.EyeAngles!);
                
                float aimDot = MathUtils.DotProduct(currentForward, exactDir);
                float dist = (botHead - enemyChest).Length();
                
                // Require perfect centering. 0.995f is extremely accurate, preventing premature empty clicks!
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
    }

    #endregion

    #region The GOAP Planner (Dijkstra/A* simplification)

    public class GoapPlanner
    {
        public Queue<GoapAction> Plan(BotAgent agent, GoapGoal goal, GoapState startState)
        {
            var usableActions = agent.AvailableActions.Where(a => a.CheckContextPrecondition(agent)).ToList();
            List<GoapAction> bestPlan = new List<GoapAction>();
            
            if (goal is GoalSurvive)
            {
                bestPlan.Add(usableActions.First(a => a is ActionEvasiveRetreat));
            }
            else if (goal is GoalKillEnemy)
            {
                bool hasZeus = startState.Values.GetValueOrDefault(StateKey.HasZeus, false);
                float dist = 9999f;
                if (agent.Blackboard.CurrentTargetFact != null)
                {
                    dist = (agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition).Length();
                }

                if (hasZeus)
                {
                    if (dist > 400f)
                    {
                        var equipKnife = usableActions.FirstOrDefault(a => a is ActionEquipKnife);
                        if (equipKnife != null) bestPlan.Add(equipKnife);
                        
                        var approach = usableActions.FirstOrDefault(a => a is ActionApproachTarget);
                        if (approach != null) bestPlan.Add(approach);
                    }
                    else
                    {
                        var equipZeus = usableActions.FirstOrDefault(a => a is ActionEquipZeus);
                        if (equipZeus != null) bestPlan.Add(equipZeus);
                        
                        if (dist > 200f)
                        {
                            var approach = usableActions.FirstOrDefault(a => a is ActionApproachTarget);
                            if (approach != null) bestPlan.Add(approach);
                        }
                        
                        var engageZeus = usableActions.FirstOrDefault(a => a is ActionEngageZeus);
                        if (engageZeus != null) bestPlan.Add(engageZeus);
                    }
                }
                else
                {
                    var equipKnife = usableActions.FirstOrDefault(a => a is ActionEquipKnife);
                    if (equipKnife != null) bestPlan.Add(equipKnife);
                    
                    if (dist > 65f)
                    {
                        var approach = usableActions.FirstOrDefault(a => a is ActionApproachTarget);
                        if (approach != null) bestPlan.Add(approach);
                    }
                    
                    var engageKnife = usableActions.FirstOrDefault(a => a is ActionEngageKnife);
                    if (engageKnife != null) bestPlan.Add(engageKnife);
                }
            }

            return new Queue<GoapAction>(bestPlan);
        }
    }

    #endregion

    #region Math Utilities
    public static class MathUtils
    {
        public static float NormalizeAngle(float angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
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

    #region Main Plugin Implementation
    public class ZeusBotAIGoapPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (F.E.A.R. GOAP Architecture)";
        public override string ModuleVersion => "2.0.3";

        private Dictionary<uint, BotAgent> agents = new Dictionary<uint, BotAgent>();
        private GoapPlanner planner = new GoapPlanner();
        private int tickCounter = 0;

        private static readonly string[] BotNames = new string[]
        {
            "Zappy", "Sparky", "Zeus", "Bolt", "Zip", "Static", "Flicker", "Thor", "Jolt", "Volt",
            "Watt", "Amp", "Ohm", "Tesla", "Arc", "Ion", "Surge", "Livewire", "Shorty", "Buzzer",
            "Glow", "Indra", "Raiju", "Flash", "Shock", "Current", "Flux", "Plasma", "Neon", "Zap",
            "Blitz", "Thunder", "Storm", "Joule", "Hertz", "Dynamo", "Turbine", "Grid", "Circuit", "Fuse",
            "Breaker", "Switch", "Relay", "Spark", "Singe", "Flare", "Beam", "Ray", "Laser", "Pulse",
            "Frequency", "Phase", "Ground", "Sparkler", "Crackle", "Snap", "Pop", "Kinetic", "Battery", "Charge",
            "Proton", "Electron", "Neutron", "Faraday"
        };
        private Dictionary<uint, string> assignedNames = new Dictionary<uint, string>();
        private Random rnd = new Random();

        public override void Load(bool hotReload)
        {
            RegisterListener<Listeners.OnTick>(OnTick);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            Console.WriteLine("[Zeus Bot GOAP] Loaded F.E.A.R. Architecture.");
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
                        
                        // Force give the taser if they don't have it on spawn to fix the "perpetual knife" glitch
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

                if (tickCounter % 10 == bot.Index % 10) 
                {
                    agent.UpdateSensor(allAlivePlayers, currentTime);
                    agent.Memory.Update(dt * 10f); 
                }

                ProcessAgentIntelligence(agent, currentTime, dt);
                InjectMotorCommands(agent);
            }
        }

        private void ProcessAgentIntelligence(BotAgent agent, float currentTime, float dt)
        {
            var facts = agent.Memory.Facts.Values.OrderByDescending(f => f.ThreatLevel * f.Confidence).ToList();
            agent.Blackboard.CurrentTargetFact = facts.FirstOrDefault();
            agent.Blackboard.ButtonsToPress = 0; 
            agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0);

            if (agent.CurrentPlan.Count == 0 && agent.CurrentAction == null)
            {
                var topGoal = agent.Goals.OrderByDescending(g => g.GetPriority(agent)).First();
                if (topGoal.GetPriority(agent) > 0)
                {
                    var state = agent.GetWorldState();
                    agent.CurrentPlan = planner.Plan(agent, topGoal, state);
                }
            }

            if (agent.CurrentAction == null && agent.CurrentPlan.Count > 0)
            {
                agent.CurrentAction = agent.CurrentPlan.Dequeue();
                if (agent.CurrentAction.IsValid(agent))
                    agent.CurrentAction.OnEnter(agent);
                else
                    agent.Interrupt("Action Invalidated Context");
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

            if (agent.Blackboard.CurrentTargetFact != null)
            {
                Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                Vector botPos = agent.Pawn.AbsOrigin!;
                
                // Human-like prediction aim: pull towards center mass but anticipate movement
                float dist = (targetPos - botPos).Length();
                
                float dx = targetPos.X - botPos.X;
                float dy = targetPos.Y - botPos.Y;
                float dz = (targetPos.Z + 40f) - (botPos.Z + 64f); // Account for bot head height to player chest

                float perfectYaw = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
                float perfectPitch = (float)(Math.Atan2(-dz, Math.Sqrt(dx * dx + dy * dy)) * 180.0 / Math.PI);
                
                float currentYaw = agent.Pawn.EyeAngles!.Y;
                float currentPitch = agent.Pawn.EyeAngles.X;
                
                // Very fast "flicky" aim lerp. Fast enough to track wild ADAD, but smooth enough to not look like a blatant spinbot
                float aimLerp = 0.55f; 
                if (dist < 250f || agent.CurrentAction is ActionEngageZeus) aimLerp = 0.85f; // Hard track during engagement
                if (agent.Blackboard.CurrentTargetFact.ThreatLevel > 2000f) aimLerp = 0.65f; 
                
                float newYaw = currentYaw + MathUtils.NormalizeAngle(perfectYaw - currentYaw) * aimLerp;
                float newPitch = Math.Clamp(currentPitch + MathUtils.NormalizeAngle(perfectPitch - currentPitch) * aimLerp, -89f, 89f);
                
                agent.Blackboard.DesiredAim = new QAngle(newPitch, newYaw, 0);
            }
        }

        private void InjectMotorCommands(BotAgent agent)
        {
            var pawn = agent.Pawn;
            if (pawn?.MovementServices == null) return;

            Vector dir = agent.Blackboard.DesiredMoveDirection;
            float speed = agent.Blackboard.DesiredSpeed;
            Vector currentVel = pawn.AbsVelocity!;
            
            float zVel = currentVel.Z;
            
            // Actually apply jump physics on the Z axis since Teleport overrides it!
            if ((agent.Blackboard.ButtonsToPress & (ulong)PlayerButtons.Jump) != 0)
            {
                bool isGrounded = ((uint)pawn.Flags & 1) != 0;
                
                // Cliff jump prevention: avoid yeeting off drops if the target is far and below us
                bool safeToJump = true;
                if (agent.Blackboard.CurrentTargetFact != null)
                {
                    Vector targetPos = agent.Blackboard.CurrentTargetFact.LastKnownPosition;
                    float zDiff = pawn.AbsOrigin!.Z - targetPos.Z;
                    float xyDist = (float)Math.Sqrt(Math.Pow(pawn.AbsOrigin.X - targetPos.X, 2) + Math.Pow(pawn.AbsOrigin.Y - targetPos.Y, 2));
                    
                    if (zDiff > 80f && xyDist > 200f) 
                    {
                        safeToJump = false; 
                    }
                }
                
                if (isGrounded && safeToJump)
                {
                    zVel = 300f; // Standard CS2 jump impulse
                }
            }
            
            Vector injectedVelocity = new Vector(dir.X * speed, dir.Y * speed, zVel);
            QAngle outAngles = agent.Blackboard.CurrentTargetFact != null ? agent.Blackboard.DesiredAim : pawn.EyeAngles!;
            
            pawn.Teleport(null, outAngles, injectedVelocity);
            pawn.MovementServices.Buttons.ButtonStates[0] |= agent.Blackboard.ButtonsToPress;
        }

        public override void Unload(bool hotReload)
        {
            RemoveListener<Listeners.OnTick>(OnTick);
            // CounterStrikeSharp usually automatically unregisters event handlers, but we clear our dictionaries
            agents.Clear();
            assignedNames.Clear();
        }
    }
    #endregion
}
