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
                // Decay confidence over time when out of sight
                Facts[key].Confidence -= deltaTime * 0.05f; 
                if (Facts[key].Confidence <= 0)
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

            foreach (var player in allPlayers)
            {
                if (player == Controller || !player.PawnIsAlive) continue;
                var otherPawn = player.PlayerPawn.Value;
                if (otherPawn == null || !otherPawn.IsValid) continue;

                Vector otherPos = otherPawn.AbsOrigin!;
                float dist = (myPos - otherPos).Length();
                Vector dirToOther = MathUtils.NormalizeVector(otherPos - myPos);
                
                if (player.TeamNum == Controller.TeamNum || player.IsBot)
                {
                    if (dist < 300f) // Keep track of allies to avoid crowding
                    {
                        Blackboard.NearbyAllies.Add(otherPos);
                    }
                    continue;
                }

                // Line of Sight & FOV Simulation for Enemies
                float dot = MathUtils.DotProduct(myForward, dirToOther);
                if (dist < 1500f && dot > 0.4f) // In FOV
                {
                    if (!Memory.Facts.ContainsKey(player.Index))
                        Memory.Facts[player.Index] = new Fact { Subject = player };

                    var fact = Memory.Facts[player.Index];
                    fact.LastKnownPosition = otherPos;
                    fact.Confidence = 1.0f; // Refresh confidence
                    
                    // Threat calculation - Dynamic prioritization
                    Vector enemyForward = MathUtils.GetForwardVector(otherPawn.EyeAngles!);
                    float enemyAimDot = MathUtils.DotProduct(enemyForward, dirToOther * -1);
                    fact.ThreatLevel = (1500f - dist) * 2f; // Distance is a huge factor
                    
                    if (enemyAimDot > 0.9f) fact.ThreatLevel += 3000f; // Aimed at!
                    if (dist < 300f) fact.ThreatLevel += 5000f; // Immediate combat priority
                    
                    // Trigger Interruption / Fear State if heavily threatened from afar
                    if (enemyAimDot > 0.95f && dist > 500f && Blackboard.FearTimer < currentTime)
                    {
                        Blackboard.FearTimer = currentTime + 1.2f;
                        Interrupt("High Threat Detected - Evasive Retreat");
                    }
                }
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
                if (w.Value != null && w.Value.DesignerName.Contains(name)) return true;
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
        
        public override bool CheckContextPrecondition(BotAgent agent) => agent.Blackboard.ActionCooldown <= Server.CurrentTime;
        
        public override bool IsValid(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        public override bool IsDone(BotAgent agent) => agent.Blackboard.CurrentTargetFact == null || agent.Blackboard.ActionCooldown > Server.CurrentTime;
        
        public override void Execute(BotAgent agent, float dt)
        {
            agent.Blackboard.DesiredMoveDirection = new Vector(0,0,0); 
            agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack2; 
            agent.Blackboard.ActionCooldown = Server.CurrentTime + 0.8f;
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
        
        public override bool CheckContextPrecondition(BotAgent agent) => agent.Blackboard.ActionCooldown <= Server.CurrentTime;

        public override bool IsDone(BotAgent agent) => agent.Blackboard.ActionCooldown > Server.CurrentTime || !HasWeapon(agent, "hegrenade");

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
                agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack;
                agent.Blackboard.ActionCooldown = Server.CurrentTime + 2.0f; // Triggers done state and puts on cooldown
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
                Vector dirAway = MathUtils.NormalizeVector(agent.Pawn.AbsOrigin! - agent.Blackboard.CurrentTargetFact.LastKnownPosition);
                // Dynamic, unpredictable evasion
                float dodgeSine = (float)Math.Sin(Server.CurrentTime * 7f + agent.Controller.Index);
                Vector strafe = new Vector(-dirAway.Y, dirAway.X, 0) * Math.Sign(dodgeSine); 
                
                // Keep away from allies to avoid easy collaterals/grouping
                Vector separation = new Vector(0,0,0);
                foreach (var allyPos in agent.Blackboard.NearbyAllies)
                {
                    Vector repulse = agent.Pawn.AbsOrigin! - allyPos;
                    if (repulse.Length() < 150f) 
                        separation += MathUtils.NormalizeVector(repulse) * 2f;
                }
                
                agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector((dirAway * 0.5f) + strafe + separation);
                agent.Blackboard.DesiredSpeed = 250f;
                
                // Advanced duck/jump matrix based on frame times
                if (dodgeSine > 0.8f) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Jump;
                else if (dodgeSine < -0.8f) agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
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
            return targetDist <= (hasZeus ? 190f : 65f); // Close the entire gap if forced to use knife
        }
        public override void Execute(BotAgent agent, float dt)
        {
            Vector targetPos = agent.Blackboard.CurrentTargetFact!.LastKnownPosition;
            Vector myPos = agent.Pawn.AbsOrigin!;
            float dist = (myPos - targetPos).Length();
            Vector dirToTarget = MathUtils.NormalizeVector(targetPos - myPos);
            
            // Unpredictable approach
            float dodgeSine = (float)Math.Sin(Server.CurrentTime * 4f + agent.Controller.Index);
            Vector strafe = new Vector(-dirToTarget.Y, dirToTarget.X, 0) * dodgeSine;

            // Group flanking/spacing: push away from allies
            Vector separation = new Vector(0,0,0);
            foreach (var allyPos in agent.Blackboard.NearbyAllies)
            {
                Vector repulse = myPos - allyPos;
                if (repulse.Length() < 200f)
                    separation += MathUtils.NormalizeVector(repulse) * 1.5f;
            }

            // Flanking arc logic: the closer we get, the more we strafe rather than approach straight
            float forwardWeight = (dist < 300f) ? 0.7f : 1.5f;

            agent.Blackboard.DesiredMoveDirection = MathUtils.NormalizeVector((dirToTarget * forwardWeight) + strafe + separation);
            agent.Blackboard.DesiredSpeed = 250f;
            
            if (dist < 250f && Math.Abs(dodgeSine) > 0.9f) 
            {
                // Quick dips when getting close to throw off headshots
                agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Duck;
            }
        }
    }

    public class ActionEngageZeus : GoapAction
    {
        private float attemptGiveUpTime = 0f;

        public ActionEngageZeus()
        {
            Name = "Engage with Zeus";
            Preconditions.Values[StateKey.TargetInZeusRange] = true;
            Preconditions.Values[StateKey.HasZeus] = true;
            Effects.Values[StateKey.TargetDead] = true;
        }
        
        public override bool CheckContextPrecondition(BotAgent agent) => agent.Blackboard.ActionCooldown <= Server.CurrentTime;
        
        public override bool IsValid(BotAgent agent) => agent.Blackboard.CurrentTargetFact != null;
        public override bool IsDone(BotAgent agent) => agent.Blackboard.CurrentTargetFact == null || agent.Blackboard.ActionCooldown > Server.CurrentTime || Server.CurrentTime > attemptGiveUpTime;
        
        public override void OnEnter(BotAgent agent) 
        {
            attemptGiveUpTime = Server.CurrentTime + 2.0f; // Give it 2 seconds max to line up the perfect shot
        }
        
        public override void Execute(BotAgent agent, float dt)
        {
            if (agent.Blackboard.CurrentTargetFact != null)
            {
                // Unpredictable firing strafe
                agent.Blackboard.DesiredSpeed = 100f; // Minimal speed to maintain accuracy but stay moving
                Vector dirToTarget = MathUtils.NormalizeVector(agent.Blackboard.CurrentTargetFact.LastKnownPosition - agent.Pawn.AbsOrigin!);
                float strafeVal = agent.Controller.Index % 2 == 0 ? 1f : -1f;
                agent.Blackboard.DesiredMoveDirection = new Vector(-dirToTarget.Y, dirToTarget.X, 0) * strafeVal;
                
                // Extremely proficient aim check before firing. Ensure crosshair is almost perfectly aligned
                Vector botHead = new Vector(agent.Pawn.AbsOrigin!.X, agent.Pawn.AbsOrigin!.Y, agent.Pawn.AbsOrigin!.Z + 64f);
                Vector enemyChest = new Vector(agent.Blackboard.CurrentTargetFact.LastKnownPosition.X, agent.Blackboard.CurrentTargetFact.LastKnownPosition.Y, agent.Blackboard.CurrentTargetFact.LastKnownPosition.Z + 40f);
                Vector exactDir = MathUtils.NormalizeVector(enemyChest - botHead);
                Vector currentForward = MathUtils.GetForwardVector(agent.Pawn.EyeAngles!);
                
                float aimDot = MathUtils.DotProduct(currentForward, exactDir);
                float dist = (botHead - enemyChest).Length();
                
                // Loosen dot product. 0.99f was way too tight and caused it to hold shots infinitely.
                if (aimDot > 0.96f || (dist < 150f && aimDot > 0.85f)) 
                {
                    agent.Blackboard.ButtonsToPress |= (ulong)PlayerButtons.Attack;
                    // Cooldown triggered specifically AFTER firing, letting the action finish and exit
                    agent.Blackboard.ActionCooldown = Server.CurrentTime + 1.2f; 
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
                
                // Ensure they explicitly equip the best weapon before trying to use it
                var activeWeapon = agent.Pawn?.WeaponServices?.ActiveWeapon.Value;
                if (activeWeapon == null || (!activeWeapon.DesignerName.Contains("taser") && !activeWeapon.DesignerName.Contains("knife")))
                {
                    if (hasZeus)
                    {
                        var equipAction = usableActions.FirstOrDefault(a => a is ActionEquipZeus);
                        if (equipAction != null) bestPlan.Add(equipAction);
                    }
                    else
                    {
                        var equipKnife = usableActions.FirstOrDefault(a => a is ActionEquipKnife);
                        if (equipKnife != null) bestPlan.Add(equipKnife);
                    }
                }

                // Push approach phase
                if (!startState.Values.GetValueOrDefault(StateKey.TargetInZeusRange, false))
                    bestPlan.Add(usableActions.First(a => a is ActionApproachTarget));
                
                // Push engagement phase
                if (hasZeus)
                {
                    var engageZeus = usableActions.FirstOrDefault(a => a is ActionEngageZeus);
                    if (engageZeus != null) bestPlan.Add(engageZeus);
                }
                else
                {
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

        public override void Load(bool hotReload)
        {
            RegisterListener<Listeners.OnTick>(OnTick);
            Console.WriteLine("[Zeus Bot GOAP] Loaded F.E.A.R. Architecture.");
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
                
                // Advanced target prediction - aim slightly ahead of velocity
                // (Assuming we had velocity, we just aim center mass for now)
                float dist = (targetPos - botPos).Length();
                
                float dx = targetPos.X - botPos.X;
                float dy = targetPos.Y - botPos.Y;
                float dz = (targetPos.Z + 40f) - (botPos.Z + 45f); // Aim lower torso for perfect zeus connects

                float perfectYaw = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
                float perfectPitch = (float)(Math.Atan2(-dz, Math.Sqrt(dx * dx + dy * dy)) * 180.0 / Math.PI);
                
                float currentYaw = agent.Pawn.EyeAngles!.Y;
                float currentPitch = agent.Pawn.EyeAngles.X;
                
                // Dynamic aim snapping - if very close or engaging, snap harder
                float aimLerp = 0.2f;
                if (dist < 300f || agent.CurrentAction is ActionEngageZeus) aimLerp = 0.8f; // Extremely proficient close range aim
                if (agent.Blackboard.CurrentTargetFact.ThreatLevel > 2000f) aimLerp = 0.6f; // Snap to high threats
                
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
            
            Vector injectedVelocity = new Vector(dir.X * speed, dir.Y * speed, currentVel.Z);
            
            QAngle outAngles = agent.Blackboard.CurrentTargetFact != null ? agent.Blackboard.DesiredAim : pawn.EyeAngles!;
            
            pawn.Teleport(null, outAngles, injectedVelocity);
            pawn.MovementServices.Buttons.ButtonStates[0] |= agent.Blackboard.ButtonsToPress;
        }

        public override void Unload(bool hotReload)
        {
            RemoveListener<Listeners.OnTick>(OnTick);
            agents.Clear();
        }
    }
    #endregion
}
