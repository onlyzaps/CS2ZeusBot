using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZeusBotAI
{
    // =========================================================================
    //  ZEUS BOT AI
    //  Full-lifecycle controller for Counter-Strike 2 zeus-only bots.
    //
    //  Goals:
    //    * The plugin owns every aspect of bot behaviour (no hand-off to the
    //      native CS2 bot brain mid-round).
    //    * Macro: bots play the map. They prioritise objectives, hug cover,
    //      respect sightlines, and approach with patience appropriate to the
    //      zeus's 1-shot, single-cell engagement window.
    //    * Micro: aim, movement, weapon-swap and firing are driven by smooth
    //      continuous scalars (alert / commit / combat intensity) rather than
    //      hard thresholds. There is no "combat mode toggle".
    //    * Personality: every bot has its own aggression / patience / skill
    //      so a group plays asymmetrically like real teammates.
    // =========================================================================

    #region Math utilities

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
            return new Vector(
                (float)(Math.Cos(yawRad) * Math.Cos(pitchRad)),
                (float)(Math.Sin(yawRad) * Math.Cos(pitchRad)),
                (float)(-Math.Sin(pitchRad)));
        }

        public static Vector Normalize(Vector v)
        {
            float l = (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return l < 1e-4f ? new Vector(0, 0, 0) : new Vector(v.X / l, v.Y / l, v.Z / l);
        }

        public static float Length(Vector v) => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        public static float LengthXY(Vector v) => (float)Math.Sqrt(v.X * v.X + v.Y * v.Y);

        public static float Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector Lerp(Vector a, Vector b, float t) =>
            new Vector(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
        public static float Clamp(float x, float lo, float hi) => x < lo ? lo : (x > hi ? hi : x);

        public static Vector RotateXY(Vector v, float radians)
        {
            float c = (float)Math.Cos(radians), s = (float)Math.Sin(radians);
            return new Vector(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }

        public static float SmoothApproach(float current, float target, float rate, float dt)
        {
            // Frame-rate-independent exponential approach. rate ~ 1/timeConstant.
            float k = 1f - (float)Math.Exp(-rate * dt);
            return current + (target - current) * k;
        }

        // CounterStrikeSharp's Vector is a reference type whose contents are
        // backed by live engine memory (pawn.AbsOrigin etc.). Whenever we
        // want to *remember* a position rather than continuously read the
        // entity, we must snapshot it into a managed copy.
        public static Vector Snap(Vector v) => new Vector(v.X, v.Y, v.Z);
    }

    #endregion

    #region Personality

    // Per-bot constants so a squad plays asymmetrically.
    public class Personality
    {
        public float Aggression;   // bias toward closing distance
        public float Patience;     // willingness to hold angles / sneak
        public float Skill;        // overall accuracy & reaction
        public float ReactionTime; // seconds before aim begins to track a fresh sighting
        public float AimNoise;     // base aim wander magnitude (degrees)
        public float LaneOffset;   // preferred angular offset around objectives (radians)
        public float PeekJitter;   // amount of left/right micro motion when peeking
        public float JumpBias;     // probability multiplier for jump-strafing
        public float CrouchBias;   // probability multiplier for crouch-peeking
        public float StealthBias;  // tendency to walk rather than sprint when alert is rising

        public static Personality RollFor(uint seed)
        {
            // Hash-style deterministic per-bot personality so re-spawns feel consistent
            unchecked
            {
                uint s = seed * 2654435761u;
                Random r = new Random((int)s);
                return new Personality
                {
                    Aggression   = (float)(r.NextDouble() * 0.55 + 0.25),
                    Patience     = (float)(r.NextDouble() * 0.55 + 0.25),
                    Skill        = (float)(r.NextDouble() * 0.4  + 0.55),
                    ReactionTime = (float)(r.NextDouble() * 0.16 + 0.14),
                    AimNoise     = (float)(r.NextDouble() * 5.0  + 3.0),
                    LaneOffset   = (float)(r.NextDouble() * Math.PI * 2),
                    PeekJitter   = (float)(r.NextDouble() * 0.5  + 0.4),
                    JumpBias     = (float)(r.NextDouble() * 0.6  + 0.3),
                    CrouchBias   = (float)(r.NextDouble() * 0.6  + 0.2),
                    StealthBias  = (float)(r.NextDouble() * 0.7  + 0.2),
                };
            }
        }
    }

    #endregion

    #region Enemy memory

    public class EnemyTrack
    {
        public CCSPlayerController Subject = null!;
        public Vector LastSeenPos = new Vector(0, 0, 0);
        public Vector LastSeenVel = new Vector(0, 0, 0);
        public float LastSeenTime = -999f;
        public float FirstContactTime = -999f;
        public bool CurrentlyVisible;
        public float VisibleStreak;     // seconds of continuous visibility
        public float Confidence;        // smoothed 0..1 (how reliable is our position estimate)
        public float Threat;            // smoothed 0..1 (how dangerous right now)

        public Vector Predict(float now)
        {
            float dt = MathUtils.Clamp(now - LastSeenTime, 0f, 2.5f);
            float vScale = 1f - dt / 2.5f;
            return new Vector(
                LastSeenPos.X + LastSeenVel.X * dt * vScale,
                LastSeenPos.Y + LastSeenVel.Y * dt * vScale,
                LastSeenPos.Z);
        }
    }

    #endregion

    #region Shared tactical intel

    public class SoundEvent
    {
        public Vector Position = new Vector(0, 0, 0);
        public float Time;
        public int TeamHint; // 0 unknown, 2 T, 3 CT
        public float Loudness; // 0..1 (gunfire = 1, footstep = 0.3, etc.)
    }

    // Information that is genuinely shared between bots. Bots use this in
    // place of a true sound system - teammates "tell" each other where they
    // last saw / fired, and where enemies died.
    public static class TacticalIntel
    {
        public static readonly List<SoundEvent> Sounds = new();
        public static Vector? PlantedBomb;
        public static float PlantedBombTime = -999f;
        public static Vector? DroppedBomb;
        public static float DroppedBombTime = -999f;

        // Discovered spawn centroids per team (built from spawn events).
        public static Dictionary<int, List<Vector>> TeamSpawns = new();
        public static Dictionary<int, Vector> TeamSpawnCenter = new();

        public static void RegisterSpawn(int team, Vector posLive)
        {
            if (!TeamSpawns.ContainsKey(team)) TeamSpawns[team] = new List<Vector>();
            // Avoid drift: only keep the first ~8 unique spawn samples per team.
            var list = TeamSpawns[team];
            if (list.Count >= 8) return;
            foreach (var p in list)
                if ((p - posLive).Length() < 64f) return;
            // Snapshot - the engine pointer would otherwise mutate.
            var pos = MathUtils.Snap(posLive);
            list.Add(pos);

            float x = 0, y = 0, z = 0;
            foreach (var p in list) { x += p.X; y += p.Y; z += p.Z; }
            TeamSpawnCenter[team] = new Vector(x / list.Count, y / list.Count, z / list.Count);
        }

        public static void Reset()
        {
            Sounds.Clear();
            PlantedBomb = null;
            DroppedBomb = null;
            PlantedBombTime = -999f;
            DroppedBombTime = -999f;
        }

        public static void RecordSound(Vector pos, float time, int team, float loud)
        {
            Sounds.Add(new SoundEvent { Position = MathUtils.Snap(pos), Time = time, TeamHint = team, Loudness = loud });
            if (Sounds.Count > 48) Sounds.RemoveAt(0);
        }

        public static void Prune(float now)
        {
            Sounds.RemoveAll(s => now - s.Time > 12f);
        }

        public static Vector MapCenter()
        {
            // Use the midpoint between team spawn centroids as a rough map centre.
            if (TeamSpawnCenter.TryGetValue(2, out var t) && TeamSpawnCenter.TryGetValue(3, out var ct))
                return MathUtils.Lerp(t, ct, 0.5f);
            if (TeamSpawnCenter.Count > 0) return TeamSpawnCenter.Values.First();
            return new Vector(0, 0, 0);
        }
    }

    #endregion

    #region The ZeusBot itself

    public enum BotPhase
    {
        Roam,        // No live target / nothing pressing. Move toward objective with stealth.
        Probe,       // Stale intel or audio cue. Advance carefully, hold angles short of corners.
        Engage,      // Have a real, threatening target. Push / strafe / zap window.
        Reposition,  // Just got a kill, lost line of sight, took damage. Move to fresh angle.
    }

    public class ZeusBot
    {
        public CCSPlayerController Controller;
        public CCSPlayerPawn Pawn => Controller.PlayerPawn.Value!;
        public Personality Personality;

        public Dictionary<uint, EnemyTrack> Tracks = new();
        public EnemyTrack? PrimaryTarget;

        // === Smoothed cognitive scalars (0..1 unless noted) ============
        public float Alert;            // general awareness; rises with intel/audio
        public float Commit;           // push aggressiveness toward objective / target
        public float CombatIntensity;  // immediate combat tension (drives micro)
        public float Stealth;          // tendency to walk vs sprint right now

        // === Phase ================================================
        public BotPhase Phase = BotPhase.Roam;
        public float PhaseEnteredAt;

        // === Aim state ============================================
        public QAngle AimGoal = new QAngle(0, 0, 0);
        public float AimAcquiredAt = -999f; // when aim "locks" on current target
        public float ReactionRemaining;     // seconds before aim is allowed to start tracking
        public uint? LockedTargetIndex;

        // === Movement state ========================================
        public Vector Destination = new Vector(0, 0, 0);
        public bool HaveDestination;
        public Vector LastPos = new Vector(0, 0, 0);
        public float StuckTimer;
        public float HeadingBiasRad;
        public float HeadingBiasUntil;
        public float NextWaypointPickAt;

        // === Combat micro =========================================
        public float NextMicroChangeAt;
        public int MicroPattern;
        public float StrafeDir = 1f;
        public float JumpCooldown;
        public float DuckUntil;
        public float FireCooldown;
        public float NextPeekAt;
        public bool HoldingAngle;
        public float HoldUntil;
        public Vector? HoldPosition;
        public Vector? HoldFacing;

        // === Weapon state =========================================
        public string DesiredWeapon = "knife"; // "knife" or "taser"
        public float DesiredWeaponSince;
        public float WeaponSwitchCooldown;

        // === Event memory =========================================
        public float SpawnedAt;
        public float LastHurtAt = -999f;
        public Vector? LastHurtFrom;
        public float LastKillAt = -999f;

        // === Frame outputs (cleared every tick) ===================
        public Vector DesiredMove = new Vector(0, 0, 0);
        public float DesiredSpeed;
        public ulong PendingButtons;
        public bool WantWalk;

        // === Per-bot noise seed ===================================
        public float NoisePhase;

        public ZeusBot(CCSPlayerController c, float now)
        {
            Controller = c;
            Personality = Personality.RollFor(c.Index);
            SpawnedAt = now;
            PhaseEnteredAt = now;
            NoisePhase = (c.Index * 0.7137f) % 6.2831f;
        }

        public bool HasWeapon(string designerSubstring)
        {
            if (Pawn?.WeaponServices?.MyWeapons == null) return false;
            foreach (var w in Pawn.WeaponServices.MyWeapons)
            {
                var weapon = w.Value;
                if (weapon != null && weapon.DesignerName != null && weapon.DesignerName.Contains(designerSubstring))
                    return true;
            }
            return false;
        }

        public bool HoldingWeapon(string designerSubstring)
        {
            var active = Pawn?.WeaponServices?.ActiveWeapon.Value;
            return active != null && active.DesignerName != null && active.DesignerName.Contains(designerSubstring);
        }

        public void TransitionTo(BotPhase p, float now)
        {
            if (Phase == p) return;
            Phase = p;
            PhaseEnteredAt = now;
        }
    }

    #endregion

    #region Brain - sensors

    public static class Brain
    {
        // ---------------------------------------------------------------------
        //  Per-tick entry point. Order matters: we sense the world, choose
        //  the threat we care about, update the smooth cognitive scalars,
        //  decide a high-level phase, pick a destination, then drive motor
        //  outputs (move, aim, weapon, fire).
        // ---------------------------------------------------------------------
        public static void Tick(ZeusBot bot, List<CCSPlayerController> allPlayers, float now, float dt)
        {
            if (bot.Pawn == null || !bot.Pawn.IsValid) return;

            // Reset per-frame outputs
            bot.DesiredMove = new Vector(0, 0, 0);
            bot.DesiredSpeed = 0f;
            bot.PendingButtons = 0;
            bot.WantWalk = false;

            UpdateSensors(bot, allPlayers, now, dt);
            ChooseTarget(bot, now);
            UpdateScalars(bot, now, dt);
            UpdatePhase(bot, now);
            UpdateDestination(bot, now);
            UpdateMotor(bot, now, dt);
            UpdateAim(bot, now, dt);
            UpdateWeapon(bot, now);
            UpdateFire(bot, now);
        }

        // ---------------------------------------------------------------------
        //  Vision / memory of all enemies.  We use the CS2 "spotted" flag as
        //  our visibility oracle (it's the only reliable visibility signal
        //  we can read cheaply) and gate it with FOV + distance + Z-delta so
        //  bots don't track through ceilings.
        //
        //  Crucially: we *don't* clamp visibility to a magic 750u range any
        //  more.  Bots see as far as a human would, but distance still
        //  weighs into Threat and the bot's response.
        // ---------------------------------------------------------------------
        private static void UpdateSensors(ZeusBot bot, List<CCSPlayerController> allPlayers, float now, float dt)
        {
            Vector myPos = bot.Pawn.AbsOrigin!;
            Vector myFwd = MathUtils.GetForwardVector(bot.Pawn.EyeAngles!);

            // Tick down existing tracks first
            var deadKeys = new List<uint>();
            foreach (var kv in bot.Tracks)
            {
                if (kv.Value.Subject == null || !kv.Value.Subject.IsValid || !kv.Value.Subject.PawnIsAlive)
                {
                    deadKeys.Add(kv.Key);
                    continue;
                }
                kv.Value.CurrentlyVisible = false;
            }
            foreach (var k in deadKeys) bot.Tracks.Remove(k);

            foreach (var p in allPlayers)
            {
                if (p == bot.Controller || !p.PawnIsAlive) continue;
                if (p.TeamNum == bot.Controller.TeamNum) continue;

                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid) continue;

                if (!bot.Tracks.TryGetValue(p.Index, out var track))
                {
                    track = new EnemyTrack { Subject = p };
                    bot.Tracks[p.Index] = track;
                }

                Vector ePos = pawn.AbsOrigin!;
                Vector eVel = pawn.AbsVelocity ?? new Vector(0, 0, 0);
                float dist = (myPos - ePos).Length();
                float zDiff = Math.Abs(myPos.Z - ePos.Z);
                Vector toEnemy = MathUtils.Normalize(ePos - myPos);
                float fovDot = MathUtils.Dot(myFwd, toEnemy);

                bool spotted = pawn.EntitySpottedState?.Spotted ?? false;
                bool plausibleSameFloor = zDiff < 220f || dist < 350f;
                bool inFov = fovDot > -0.1f; // ~190° awareness cone (peripheral)
                bool seesWell = fovDot > 0.4f;  // ~135° front cone where we trust shape

                bool canSee = spotted && plausibleSameFloor && (inFov || dist < 220f);

                if (canSee)
                {
                    if (!track.CurrentlyVisible) track.FirstContactTime = now;
                    track.CurrentlyVisible = true;
                    track.VisibleStreak += dt;
                    // Snapshot - otherwise these track the live entity.
                    track.LastSeenPos = MathUtils.Snap(ePos);
                    track.LastSeenVel = MathUtils.Snap(eVel);
                    track.LastSeenTime = now;
                    // Confidence climbs faster the more directly we see them.
                    float climbRate = seesWell ? 6f : 3f;
                    track.Confidence = MathUtils.SmoothApproach(track.Confidence, 1f, climbRate, dt);
                }
                else
                {
                    track.VisibleStreak = 0f;
                    // Confidence decays with time since last sight.
                    float age = now - track.LastSeenTime;
                    float target = MathUtils.Clamp01(1.5f - age * 0.5f);
                    track.Confidence = MathUtils.SmoothApproach(track.Confidence, target, 1.2f, dt);
                }

                // Threat is a continuous function of:
                //  - confidence (do we know where they are?)
                //  - proximity  (zeus only kills inside ~210u, so closeness matters a lot)
                //  - facing     (an enemy looking at us is more dangerous)
                //  - recency    (fresh sightings spike threat)
                float proximity = MathUtils.Clamp01(1f - dist / 1400f);
                float recency = MathUtils.Clamp01(1f - (now - track.LastSeenTime) / 4f);
                float facingThreat = 0.5f;
                if (track.CurrentlyVisible && pawn.EyeAngles != null)
                {
                    Vector eFwd = MathUtils.GetForwardVector(pawn.EyeAngles);
                    facingThreat = MathUtils.Clamp01(MathUtils.Dot(eFwd, MathUtils.Normalize(myPos - ePos)) * 0.5f + 0.5f);
                }
                float rawThreat = MathUtils.Clamp01(
                    track.Confidence * (0.35f + 0.45f * proximity + 0.2f * facingThreat) * (0.4f + 0.6f * recency));
                // Smooth so we don't oscillate between phases on flicker.
                track.Threat = MathUtils.SmoothApproach(track.Threat, rawThreat, 4f, dt);
            }
        }

        // ---------------------------------------------------------------------
        //  Pick the most pressing enemy. Highest threat wins, but we apply
        //  hysteresis so we don't whip between two roughly-equal targets.
        // ---------------------------------------------------------------------
        private static void ChooseTarget(ZeusBot bot, float now)
        {
            EnemyTrack? best = null;
            float bestScore = 0f;

            foreach (var t in bot.Tracks.Values)
            {
                if (t.Threat < 0.05f) continue;
                float score = t.Threat + (t.CurrentlyVisible ? 0.15f : 0f);
                if (bot.PrimaryTarget == t) score += 0.12f; // hysteresis
                if (score > bestScore) { bestScore = score; best = t; }
            }

            if (best == null)
            {
                bot.PrimaryTarget = null;
                bot.LockedTargetIndex = null;
                return;
            }

            // If we're switching to a new target, start the reaction timer so
            // the aim doesn't snap to them instantly.
            if (bot.LockedTargetIndex != best.Subject.Index)
            {
                bot.LockedTargetIndex = best.Subject.Index;
                bot.ReactionRemaining = bot.Personality.ReactionTime
                    * (1.2f - 0.6f * bot.Personality.Skill);
            }
            bot.PrimaryTarget = best;
        }

        // ---------------------------------------------------------------------
        //  Smoothly drive the cognitive scalars from sensor data + memory.
        //  No hard thresholds: alert/commit/intensity slide between 0 and 1
        //  and every other system reads them.
        // ---------------------------------------------------------------------
        private static void UpdateScalars(ZeusBot bot, float now, float dt)
        {
            bot.ReactionRemaining = Math.Max(0f, bot.ReactionRemaining - dt);

            // CombatIntensity: dominated by the current primary target's threat
            // plus any recent gunfire near us.
            float targetThreat = bot.PrimaryTarget?.Threat ?? 0f;
            float soundPressure = 0f;
            Vector me = bot.Pawn.AbsOrigin!;
            foreach (var s in TacticalIntel.Sounds)
            {
                float age = now - s.Time;
                if (age > 6f) continue;
                float dist = (s.Position - me).Length();
                if (dist > 1800f) continue;
                float ageFactor = MathUtils.Clamp01(1f - age / 6f);
                float distFactor = MathUtils.Clamp01(1f - dist / 1800f);
                soundPressure = Math.Max(soundPressure, ageFactor * distFactor * s.Loudness);
            }
            float intensityTarget = MathUtils.Clamp01(Math.Max(targetThreat, soundPressure * 0.7f));
            bot.CombatIntensity = MathUtils.SmoothApproach(bot.CombatIntensity, intensityTarget, 3.5f, dt);

            // Alert: like intensity but with longer memory. Recent hurt, recent
            // teammate death, and any audio still count even after the spike
            // fades.
            float alertTarget = MathUtils.Clamp01(
                  intensityTarget
                + soundPressure * 0.5f
                + ((now - bot.LastHurtAt) < 4f ? 0.4f : 0f));
            bot.Alert = MathUtils.SmoothApproach(bot.Alert, alertTarget, 1.6f, dt);

            // Commit: rises with personality aggression and a fresh sighting,
            // drops when we just took damage (back off & re-angle).
            float commitTarget =
                  bot.Personality.Aggression * 0.6f
                + (targetThreat > 0.3f ? 0.4f : 0f)
                - ((now - bot.LastHurtAt) < 2.5f ? 0.5f : 0f);
            // Stealth-leaning personalities don't fully commit unless they
            // have a high-confidence target.
            commitTarget -= (1f - targetThreat) * (1f - bot.Personality.Aggression) * 0.3f;
            commitTarget = MathUtils.Clamp01(commitTarget);
            bot.Commit = MathUtils.SmoothApproach(bot.Commit, commitTarget, 2.0f, dt);

            // Stealth: walk vs sprint. Patient bots in mid-alert situations
            // walk; aggressive bots run almost always.
            float stealthTarget =
                  bot.Personality.StealthBias * MathUtils.Clamp01(bot.Alert * 1.4f)
                - bot.Personality.Aggression * 0.4f;
            // When in genuine combat (intensity high) stealth drops away.
            stealthTarget -= bot.CombatIntensity * 0.8f;
            stealthTarget = MathUtils.Clamp01(stealthTarget);
            bot.Stealth = MathUtils.SmoothApproach(bot.Stealth, stealthTarget, 2.5f, dt);
        }

        // ---------------------------------------------------------------------
        //  High-level phase. Phases overlap (motor params blend between
        //  them via the cognitive scalars) but having a label lets the
        //  destination picker, the peek logic, and weapon hysteresis all
        //  reason about the *kind* of situation we're in.
        // ---------------------------------------------------------------------
        private static void UpdatePhase(ZeusBot bot, float now)
        {
            BotPhase next = bot.Phase;

            bool haveLiveTarget = bot.PrimaryTarget != null && bot.PrimaryTarget.Threat > 0.35f;
            bool haveStaleIntel = bot.PrimaryTarget != null && bot.PrimaryTarget.Threat > 0.10f;
            bool recentlyKilled = (now - bot.LastKillAt) < 2.5f;
            bool recentlyHurt   = (now - bot.LastHurtAt) < 2.0f;

            if (haveLiveTarget && !recentlyKilled)
                next = BotPhase.Engage;
            else if (recentlyKilled || recentlyHurt)
                next = BotPhase.Reposition;
            else if (haveStaleIntel || bot.Alert > 0.45f)
                next = BotPhase.Probe;
            else
                next = BotPhase.Roam;

            // Don't bounce in/out of Engage instantly: require a small dwell.
            if (bot.Phase == BotPhase.Engage && next != BotPhase.Engage
                && (now - bot.PhaseEnteredAt) < 0.4f)
                return;

            bot.TransitionTo(next, now);
        }

        // ---------------------------------------------------------------------
        //  Pick a meaningful destination based on what the bot actually
        //  knows about the world. The aim here is *macro* play: bots should
        //  push toward the bomb/site, lurk between cover points, hunt down
        //  predicted enemy positions, etc.
        // ---------------------------------------------------------------------
        private static void UpdateDestination(ZeusBot bot, float now)
        {
            Vector me = bot.Pawn.AbsOrigin!;
            int myTeam = bot.Controller.TeamNum;
            int enemyTeam = myTeam == 2 ? 3 : 2;

            // Engage: chase / shadow the primary target's predicted position.
            if (bot.Phase == BotPhase.Engage && bot.PrimaryTarget != null)
            {
                Vector predicted = bot.PrimaryTarget.Predict(now);
                Vector offset = OffsetForFanOut(bot, predicted);
                bot.Destination = new Vector(predicted.X + offset.X, predicted.Y + offset.Y, predicted.Z);
                bot.HaveDestination = true;
                return;
            }

            // Reposition: move sideways away from where we last took damage
            // (or where we just got a kill) so we don't get traded by a peek.
            if (bot.Phase == BotPhase.Reposition)
            {
                Vector anchor = bot.LastHurtFrom ?? bot.PrimaryTarget?.LastSeenPos ?? me;
                Vector away = MathUtils.Normalize(me - anchor);
                if (MathUtils.Length(away) < 0.1f) away = new Vector(1, 0, 0);
                // Strafe sideways more than straight back so we present a
                // new angle.
                Vector lateral = MathUtils.RotateXY(away, (bot.Controller.Index % 2 == 0) ? 1.2f : -1.2f);
                bot.Destination = new Vector(me.X + lateral.X * 280f, me.Y + lateral.Y * 280f, me.Z);
                bot.HaveDestination = true;
                return;
            }

            // Probe: aim for the last-known position of the strongest stale
            // contact, but stop *short* of it (the corner-hold behaviour is
            // applied later in UpdateMotor).
            if (bot.Phase == BotPhase.Probe && bot.PrimaryTarget != null)
            {
                Vector predicted = bot.PrimaryTarget.Predict(now);
                Vector toward = MathUtils.Normalize(predicted - me);
                if (MathUtils.Length(toward) < 0.1f) toward = new Vector(1, 0, 0);
                // Stop ~160-260u short depending on patience.
                float standoff = 160f + bot.Personality.Patience * 100f;
                float dist = (predicted - me).Length();
                if (dist > standoff)
                {
                    Vector approach = predicted - toward * standoff;
                    bot.Destination = new Vector(approach.X, approach.Y, predicted.Z);
                }
                else
                {
                    bot.Destination = predicted;
                }
                bot.HaveDestination = true;
                return;
            }

            // Roam: the objective layer.
            // Refresh destination occasionally, or when we arrived, or when
            // the objective fundamentally changed.
            if (!bot.HaveDestination
                || (bot.Destination - me).Length() < 96f
                || now > bot.NextWaypointPickAt)
            {
                Vector objective = PickObjective(bot, enemyTeam, now);
                // Personality-driven lane offset so a group of bots fans out
                // across an approach rather than file-walking into the site.
                float radius = 220f + bot.Personality.Patience * 200f;
                float angle = bot.Personality.LaneOffset + (float)Math.Sin(now * 0.07f + bot.NoisePhase) * 0.8f;
                Vector offset = new Vector(
                    (float)Math.Cos(angle) * radius,
                    (float)Math.Sin(angle) * radius,
                    0f);
                bot.Destination = new Vector(objective.X + offset.X, objective.Y + offset.Y, objective.Z);
                bot.HaveDestination = true;
                bot.NextWaypointPickAt = now + 4.5f + (float)(new Random((int)(bot.Controller.Index + now * 100)).NextDouble() * 3.0);
            }
        }

        private static Vector OffsetForFanOut(ZeusBot bot, Vector target)
        {
            // When multiple bots converge on the same target, fan them out
            // around it so they don't form a conga line.
            float angle = bot.Controller.Index * 0.97f + bot.Personality.LaneOffset;
            float radius = 70f + bot.Personality.Aggression * 30f;
            return new Vector((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
        }

        private static Vector PickObjective(ZeusBot bot, int enemyTeam, float now)
        {
            // Priority cascade for macro play:
            // 1. Live planted bomb (must rotate to defuse / defend).
            // 2. Dropped bomb (T to pick up; CT to deny).
            // 3. Strongest stale enemy intel.
            // 4. Loud recent sound from anyone.
            // 5. Map midpoint biased toward enemy spawn (default push).
            int myTeam = bot.Controller.TeamNum;

            if (TacticalIntel.PlantedBomb != null && (now - TacticalIntel.PlantedBombTime) < 60f)
                return TacticalIntel.PlantedBomb;

            if (TacticalIntel.DroppedBomb != null && (now - TacticalIntel.DroppedBombTime) < 30f)
                return TacticalIntel.DroppedBomb;

            // Strongest stale enemy intel that isn't already our primary
            EnemyTrack? bestStale = null;
            foreach (var t in bot.Tracks.Values)
                if (t.Confidence > 0.2f && (bestStale == null || t.Confidence > bestStale.Confidence))
                    bestStale = t;
            if (bestStale != null) return bestStale.Predict(now);

            // Loud recent sound
            SoundEvent? bestSound = null;
            float bestScore = 0f;
            foreach (var s in TacticalIntel.Sounds)
            {
                float age = now - s.Time;
                if (age > 8f) continue;
                float score = s.Loudness * (1f - age / 8f);
                if (score > bestScore) { bestScore = score; bestSound = s; }
            }
            if (bestSound != null && bestScore > 0.2f) return bestSound.Position;

            // Default: push toward enemy spawn / map centre.
            Vector center = TacticalIntel.MapCenter();
            if (TacticalIntel.TeamSpawnCenter.TryGetValue(enemyTeam, out var enemySpawn))
                return MathUtils.Lerp(center, enemySpawn, 0.35f);
            return center;
        }

        // ---------------------------------------------------------------------
        //  Compose motor outputs. This is the heart of the "no on/off
        //  switch" promise: speed, lateral jitter, jump frequency and
        //  crouch frequency all scale continuously with intensity / commit /
        //  stealth.
        //
        //  We also implement *holding angles*: in Probe mode close to the
        //  predicted enemy spot, we stop short, face the angle, and wait.
        //  When the enemy doesn't appear, patience runs out and we creep
        //  forward again.
        // ---------------------------------------------------------------------
        private static void UpdateMotor(ZeusBot bot, float now, float dt)
        {
            var pawn = bot.Pawn;
            Vector me = pawn.AbsOrigin!;
            bool grounded = ((uint)pawn.Flags & 1) != 0;
            float intensity = bot.CombatIntensity;

            // ------- Stuck detection -------
            // We measure horizontal travel since last frame. If we wanted
            // speed and didn't get it, escalate detour bias.
            Vector vel = pawn.AbsVelocity ?? new Vector(0, 0, 0);
            float horizSpeed = MathUtils.LengthXY(vel);
            // Smoothed: stuck timer only ticks when we *should* be moving.
            if (bot.HaveDestination && (bot.Destination - me).Length() > 64f && horizSpeed < 55f)
                bot.StuckTimer += dt;
            else
                bot.StuckTimer = Math.Max(0f, bot.StuckTimer - dt * 2f);

            if (bot.StuckTimer > 0.45f && now > bot.HeadingBiasUntil)
            {
                // Rotate intended heading and try a small jump to clear ledges.
                Random r = new Random((int)(bot.Controller.Index * 991 + now * 100));
                float rot = (float)((r.NextDouble() * 2 - 1) * Math.PI * 0.6); // ±108°
                if (bot.StuckTimer > 1.5f) rot = (float)((r.NextDouble() > 0.5 ? 1 : -1) * Math.PI * 0.7);
                bot.HeadingBiasRad = rot;
                bot.HeadingBiasUntil = now + 0.7f;
                if (grounded && bot.JumpCooldown < now)
                {
                    bot.PendingButtons |= (ulong)PlayerButtons.Jump;
                    bot.JumpCooldown = now + 0.5f;
                }
            }
            if (now > bot.HeadingBiasUntil)
                bot.HeadingBiasRad = MathUtils.SmoothApproach(bot.HeadingBiasRad, 0f, 2.5f, dt);

            // ------- Default heading: toward destination -------
            Vector heading = new Vector(0, 0, 0);
            if (bot.HaveDestination)
            {
                Vector d = bot.Destination - me;
                d.Z = 0;
                heading = MathUtils.Normalize(d);
            }
            heading = MathUtils.RotateXY(heading, bot.HeadingBiasRad);

            // ------- Phase-specific motor shaping -------
            switch (bot.Phase)
            {
                case BotPhase.Roam:
                    MotorRoam(bot, ref heading, grounded, now, dt);
                    break;
                case BotPhase.Probe:
                    MotorProbe(bot, ref heading, grounded, now, dt);
                    break;
                case BotPhase.Engage:
                    MotorEngage(bot, ref heading, grounded, now, dt);
                    break;
                case BotPhase.Reposition:
                    MotorReposition(bot, ref heading, grounded, now, dt);
                    break;
            }

            bot.DesiredMove = heading;
        }

        private static void MotorRoam(ZeusBot bot, ref Vector heading, bool grounded, float now, float dt)
        {
            // Knife-out sprint pace, walk when stealth is high.
            float baseSpeed = MathUtils.Lerp(250f, 130f, bot.Stealth);
            bot.DesiredSpeed = baseSpeed;
            bot.WantWalk = bot.Stealth > 0.55f;
            // Light wandering to avoid laser-straight paths
            float wander = (float)Math.Sin(now * 0.9f + bot.NoisePhase) * 0.15f;
            heading = MathUtils.RotateXY(heading, wander);
        }

        private static void MotorProbe(ZeusBot bot, ref Vector heading, bool grounded, float now, float dt)
        {
            // Hold-angle behaviour: stop short of the predicted enemy spot.
            float speed = MathUtils.Lerp(170f, 220f, bot.Commit);

            float distToDest = bot.HaveDestination ? (bot.Destination - bot.Pawn.AbsOrigin!).Length() : 9999f;

            if (distToDest < 80f)
            {
                if (!bot.HoldingAngle)
                {
                    bot.HoldingAngle = true;
                    // patience-scaled hold duration
                    bot.HoldUntil = now + 1.2f + bot.Personality.Patience * 2.5f;
                    bot.HoldPosition = MathUtils.Snap(bot.Pawn.AbsOrigin!);
                    bot.HoldFacing = bot.PrimaryTarget != null
                        ? MathUtils.Normalize(bot.PrimaryTarget.Predict(now) - bot.Pawn.AbsOrigin!)
                        : heading;
                }
            }
            if (bot.HoldingAngle && now < bot.HoldUntil)
            {
                // Stand still and zap-pre-aim. Micro jiggle peek occasionally.
                bot.DesiredSpeed = 0f;
                // Small lateral hop to bait peekers
                if (now > bot.NextPeekAt)
                {
                    bot.StrafeDir = -bot.StrafeDir;
                    bot.NextPeekAt = now + 0.6f + (float)Math.Sin(bot.NoisePhase + now) * 0.2f;
                }
                Vector facing = bot.HoldFacing ?? heading;
                Vector right = new Vector(-facing.Y, facing.X, 0f);
                Vector jiggle = right * (bot.StrafeDir * bot.Personality.PeekJitter * 0.25f);
                heading = MathUtils.Normalize(facing * 0.05f + jiggle);
                bot.DesiredSpeed = 90f * bot.Personality.PeekJitter;
                return;
            }
            else if (bot.HoldingAngle && now >= bot.HoldUntil)
            {
                bot.HoldingAngle = false;
            }

            bot.DesiredSpeed = speed;
            // Slight serpentine
            float wander = (float)Math.Sin(now * 1.3f + bot.NoisePhase) * 0.25f * bot.Personality.PeekJitter;
            heading = MathUtils.RotateXY(heading, wander);
            bot.WantWalk = bot.Stealth > 0.4f && bot.CombatIntensity < 0.25f;
        }

        private static void MotorEngage(ZeusBot bot, ref Vector heading, bool grounded, float now, float dt)
        {
            // The micro layer.  Speed, jitter, jump rate, and crouch rate
            // all scale with CombatIntensity * Commit so behaviour ramps
            // smoothly from a measured peek-shot to a full berserk rush.
            float micro = MathUtils.Clamp01(0.5f * bot.CombatIntensity + 0.5f * bot.Commit);
            float speed = MathUtils.Lerp(210f, 260f, micro);

            Vector me = bot.Pawn.AbsOrigin!;
            Vector targetPos = bot.PrimaryTarget?.Predict(now) ?? (me + heading * 200f);
            float dist = (targetPos - me).Length();

            // Pull the micro-pattern at slow intervals so motion looks
            // intentional rather than seizing.  Higher intensity = faster
            // re-picks, but never faster than ~3Hz.
            if (now > bot.NextMicroChangeAt)
            {
                Random r = new Random((int)(bot.Controller.Index * 7 + now * 100));
                bot.MicroPattern = r.Next(0, 4);
                bot.StrafeDir = r.NextDouble() > 0.5 ? 1f : -1f;
                bot.NextMicroChangeAt = now + MathUtils.Lerp(0.85f, 0.35f, micro);
            }

            Vector toTarget = MathUtils.Normalize(targetPos - me);
            Vector right = new Vector(-toTarget.Y, toTarget.X, 0f);

            // Hold-distance: zeus loses to anything inside ~250u that isn't
            // already pre-aimed, but is also a guaranteed kill <230u with a
            // good zap.  So we bias our movement to land *inside* zeus range
            // but never further than necessary - we stop pressing forward
            // once we're inside, and start strafing for the zap window.
            float idealRange = 150f;
            float rangeError = dist - idealRange; // positive => need to close
            float forwardWeight = MathUtils.Clamp(rangeError / 200f, -0.4f, 1f);

            float lateral = 0f;
            switch (bot.MicroPattern)
            {
                case 0:
                    // ADAD strafe with a slight forward push when far.
                    lateral = bot.StrafeDir * MathUtils.Lerp(0.4f, 1.0f, micro);
                    break;
                case 1:
                    // Aggressive shoulder swing - more forward push.
                    forwardWeight = MathUtils.Clamp(forwardWeight + 0.4f, -0.4f, 1f);
                    lateral = bot.StrafeDir * 0.6f;
                    if (grounded && bot.JumpCooldown < now
                        && bot.Personality.JumpBias > 0.45f
                        && micro > 0.4f)
                    {
                        bot.PendingButtons |= (ulong)PlayerButtons.Jump;
                        bot.JumpCooldown = now + MathUtils.Lerp(0.9f, 0.4f, micro);
                    }
                    break;
                case 2:
                    // Crouch slide - drop low to confuse aim during the zap window.
                    lateral = bot.StrafeDir * 0.5f;
                    if (bot.Personality.CrouchBias > 0.5f && dist < 260f)
                    {
                        bot.DuckUntil = Math.Max(bot.DuckUntil, now + 0.35f);
                    }
                    break;
                case 3:
                    // Wide swing peek: arc around target rather than charging.
                    lateral = bot.StrafeDir * 1.1f;
                    forwardWeight *= 0.4f;
                    break;
            }

            // If we're already inside ideal range, prefer pure strafing so
            // we don't run face-first into the target.
            if (dist < idealRange) forwardWeight = MathUtils.Clamp(forwardWeight, -0.3f, 0.1f);

            Vector mvmt = MathUtils.Normalize(toTarget * forwardWeight + right * lateral);
            heading = mvmt;
            bot.DesiredSpeed = speed;

            // Apply pending duck request
            if (bot.DuckUntil > now)
                bot.PendingButtons |= (ulong)PlayerButtons.Duck;
        }

        private static void MotorReposition(ZeusBot bot, ref Vector heading, bool grounded, float now, float dt)
        {
            // Brisk lateral exit. Don't sprint blindly into the open: walk
            // briefly if we were just hurt.
            bool recentlyHurt = (now - bot.LastHurtAt) < 1.5f;
            float speed = recentlyHurt ? 200f : 245f;
            bot.DesiredSpeed = speed;
            bot.WantWalk = false; // need to actually move, not creep
        }

        // ---------------------------------------------------------------------
        //  Aim. We assemble a "perfect" aim point with a small organic
        //  offset, then drive the eye angles toward it at a turn rate
        //  determined by intensity, skill, and how long the bot has been
        //  reacting to this target.
        //
        //  Crucially we gate the *first* tracking with a personality-driven
        //  reaction time so the bot doesn't instant-snap whenever sensors
        //  flag a new enemy.
        // ---------------------------------------------------------------------
        private static void UpdateAim(ZeusBot bot, float now, float dt)
        {
            var pawn = bot.Pawn;
            if (pawn.EyeAngles == null) return;

            float curYaw = pawn.EyeAngles.Y;
            float curPitch = pawn.EyeAngles.X;

            QAngle desired;

            if (bot.PrimaryTarget != null && bot.ReactionRemaining <= 0f)
            {
                // Aim at predicted position
                Vector me = pawn.AbsOrigin!;
                Vector tPos = bot.PrimaryTarget.Predict(now);

                // Choose look height. Crouching enemies are lower; we don't
                // know reliably, so we aim for upper chest with noise.
                float eyeHeight = ((uint)pawn.Flags & 2) != 0 ? 46f : 64f;
                float myEyeZ = me.Z + eyeHeight;
                float aimZ = tPos.Z + 52f;

                // Organic noise scaled by intensity (more chaotic close-up)
                // and by personality.AimNoise, dampened by skill.
                float noiseAmp = bot.Personality.AimNoise * (1f - bot.Personality.Skill * 0.6f);
                noiseAmp *= MathUtils.Lerp(0.6f, 1.4f, bot.CombatIntensity);
                float n1 = (float)Math.Sin(now * 4.1f + bot.NoisePhase);
                float n2 = (float)Math.Cos(now * 3.3f + bot.NoisePhase * 1.7f);
                float n3 = (float)Math.Sin(now * 5.0f + bot.NoisePhase * 0.5f);

                float dx = tPos.X - me.X;
                float dy = tPos.Y - me.Y;
                float dz = aimZ - myEyeZ;
                float horiz = (float)Math.Sqrt(dx * dx + dy * dy);

                float perfectYaw = horiz > 4f ? (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI) : curYaw;
                float perfectPitch = (float)(Math.Atan2(-dz, Math.Max(horiz, 1f)) * 180.0 / Math.PI);

                // Add noise *in angle space* so it scales nicely with range.
                perfectYaw   += n1 * noiseAmp;
                perfectPitch += n2 * noiseAmp * 0.6f;
                perfectPitch = MathUtils.Clamp(perfectPitch, -89f, 89f);

                // Drift the perfect aim by a tiny amount so the crosshair
                // "settles" rather than locking.
                perfectYaw   += n3 * 0.5f;

                desired = new QAngle(perfectPitch, perfectYaw, 0f);
                bot.AimGoal = desired;
            }
            else if (bot.PrimaryTarget != null && bot.ReactionRemaining > 0f)
            {
                // We've spotted them but our human hasn't reacted yet -
                // keep aim where it is, only updating slowly.
                desired = bot.AimGoal;
            }
            else if (bot.HoldFacing != null && bot.HoldingAngle)
            {
                // Pre-aim the hold direction
                Vector f = bot.HoldFacing;
                float yaw = (float)(Math.Atan2(f.Y, f.X) * 180.0 / Math.PI);
                desired = new QAngle(0f, yaw, 0f);
                bot.AimGoal = desired;
            }
            else if (bot.DesiredSpeed > 1f)
            {
                // No target - look where we're moving.
                Vector m = bot.DesiredMove;
                float yaw = (float)(Math.Atan2(m.Y, m.X) * 180.0 / Math.PI);
                // Look mostly forward, allow a tiny pitch wander
                float pitch = (float)Math.Sin(now * 0.4f + bot.NoisePhase) * 4f;
                desired = new QAngle(pitch, yaw, 0f);
                bot.AimGoal = desired;
            }
            else
            {
                desired = bot.AimGoal;
            }

            // Turn rate scales with combat intensity and skill. Far below
            // 360°/s in calm states; up to ~600°/s during a close engagement
            // for a skilled bot. Real players can't beat physical limits.
            float skillBoost = 0.5f + bot.Personality.Skill * 0.5f;
            float turnRate = MathUtils.Lerp(180f, 540f, bot.CombatIntensity) * skillBoost;
            float maxStep = turnRate * dt;

            float yawDiff = MathUtils.NormalizeAngle(desired.Y - curYaw);
            float pitchDiff = MathUtils.NormalizeAngle(desired.X - curPitch);

            float newYaw = curYaw + MathUtils.Clamp(yawDiff, -maxStep, maxStep);
            float newPitch = MathUtils.Clamp(curPitch + MathUtils.Clamp(pitchDiff, -maxStep, maxStep), -89f, 89f);

            bot.AimGoal = new QAngle(newPitch, newYaw, 0f);
        }

        // ---------------------------------------------------------------------
        //  Weapon hysteresis. Knife is faster (250u/s+) and used while
        //  roaming. Zeus is the only thing that can kill; we draw it once
        //  we believe a target is in zeus range.  The desire must hold for
        //  ~0.4s before we actually switch, so flickering threat doesn't
        //  cause weapon-spazzing.
        // ---------------------------------------------------------------------
        private static void UpdateWeapon(ZeusBot bot, float now)
        {
            if (!bot.HasWeapon("taser")) return;

            string desired = "knife";

            if (bot.PrimaryTarget != null)
            {
                float dist = (bot.Pawn.AbsOrigin! - bot.PrimaryTarget.Predict(now)).Length();
                // Want zeus out when:
                //  * actually engaging (intensity high), OR
                //  * inside zeus effective range (~280u), OR
                //  * holding an angle (we'd rather pre-aim with the weapon).
                if (bot.CombatIntensity > 0.45f || dist < 320f || bot.HoldingAngle)
                    desired = "taser";
            }
            // If alert is high and we're close to predicted intel, also
            // ready the zeus.
            if (desired == "knife" && bot.Alert > 0.7f && bot.HoldingAngle) desired = "taser";

            if (desired != bot.DesiredWeapon)
            {
                bot.DesiredWeapon = desired;
                bot.DesiredWeaponSince = now;
            }

            // Only actually switch if the desire is stable for a moment and
            // our switch cooldown elapsed.
            if (!bot.HoldingWeapon(bot.DesiredWeapon)
                && (now - bot.DesiredWeaponSince) > 0.35f
                && bot.WeaponSwitchCooldown < now)
            {
                SwapToWeapon(bot, bot.DesiredWeapon);
                bot.WeaponSwitchCooldown = now + 0.6f;
            }
        }

        private static void SwapToWeapon(ZeusBot bot, string designerSubstring)
        {
            var ws = bot.Pawn.WeaponServices;
            if (ws?.MyWeapons == null) return;
            foreach (var wh in ws.MyWeapons)
            {
                var w = wh.Value;
                if (w != null && w.DesignerName != null && w.DesignerName.Contains(designerSubstring))
                {
                    ws.ActiveWeapon.Raw = wh.Raw;
                    Utilities.SetStateChanged(bot.Pawn, "CBasePlayerPawn", "m_pWeaponServices");
                    return;
                }
            }
        }

        // ---------------------------------------------------------------------
        //  Firing. The zap is short range and has a meaningful cooldown.
        //  We fire when:
        //    1. Target is alive, currently spotted, inside zeus range.
        //    2. Our aim is close enough to actually hit (angle threshold).
        //    3. Reaction window has elapsed.
        //    4. We're holding the zeus.
        //  We don't try to fire while still pulling the weapon out - the
        //  switch cooldown blocks attack briefly.
        // ---------------------------------------------------------------------
        private static void UpdateFire(ZeusBot bot, float now)
        {
            if (bot.FireCooldown > now) return;
            if (bot.WeaponSwitchCooldown > now) return;
            if (!bot.HoldingWeapon("taser")) return;
            if (bot.PrimaryTarget == null) return;
            if (!bot.PrimaryTarget.CurrentlyVisible) return;
            if (bot.ReactionRemaining > 0f) return;

            Vector me = bot.Pawn.AbsOrigin!;
            Vector tPos = bot.PrimaryTarget.LastSeenPos;
            float dist = (me - tPos).Length();
            if (dist > 240f) return; // outside zeus range; don't waste it

            // Compute aim accuracy
            float eyeHeight = ((uint)bot.Pawn.Flags & 2) != 0 ? 46f : 64f;
            Vector head = new Vector(me.X, me.Y, me.Z + eyeHeight);
            Vector toTarget = MathUtils.Normalize(new Vector(tPos.X, tPos.Y, tPos.Z + 40f) - head);
            Vector fwd = MathUtils.GetForwardVector(bot.Pawn.EyeAngles!);
            float dot = MathUtils.Dot(fwd, toTarget);

            // Tolerance grows tighter as range grows.
            float needed = dist < 120f ? 0.94f : (dist < 180f ? 0.97f : 0.985f);
            if (dot < needed) return;

            bot.PendingButtons |= (ulong)PlayerButtons.Attack;
            // Zeus has a substantial reload-style cooldown but we don't try
            // to model the ammo; we just don't spam-click.
            bot.FireCooldown = now + 0.45f;
        }
    }

    #endregion

    #region Plugin shell

    public class ZeusBotConfig
    {
        [JsonPropertyName("PLAYERS_MANAGE_BOTS")]
        public bool PlayersManageBots { get; set; } = true;
    }

    public class ZeusBotAIGoapPlugin : BasePlugin
    {
        public override string ModuleName => "Zeus Bot AI (Continuous Lifecycle)";
        public override string ModuleVersion => "4.0.0";
        public override string ModuleAuthor => "ZeusBotAI";

        private bool botsEnabled = true;
        private ZeusBotConfig _config = new ZeusBotConfig();

        private readonly Dictionary<uint, ZeusBot> bots = new();

        private static readonly string[] BotNames = new string[]
        {
            "Zappy", "Sparky", "Zeus", "Bolt", "Zip", "Static", "Flicker", "Thor", "Jolt", "Volt",
            "Watt",  "Amp",    "Ohm",  "Tesla","Arc", "Ion",    "Surge",   "Livewire","Shorty","Buzzer",
            "Glow",  "Indra",  "Raiju","Flash","Shock","Plasma","Neon",    "Zap","Blitz","Storm"
        };
        private readonly Dictionary<uint, string> assignedNames = new();
        private readonly Random rnd = new();

        public override void Load(bool hotReload)
        {
            LoadConfig();

            RegisterListener<Listeners.OnTick>(OnTick);
            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
            RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
            RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
            RegisterEventHandler<EventBombDefused>(OnBombDefused);
            RegisterEventHandler<EventBombPickup>(OnBombPickup);
            RegisterEventHandler<EventBombDropped>(OnBombDropped);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);

            AddCommandListener("say", OnPlayerChat);
            AddCommandListener("say_team", OnPlayerChat);

            Server.NextFrame(() =>
            {
                foreach (var p in Utilities.GetPlayers())
                    if (p.IsBot) EnsureBotName(p);
            });

            AddCommand("zeusbots", "Enable Zeus Bots", (p, _) => { if (CheckCommandPermission(p)) { botsEnabled = true; Server.PrintToChatAll("Zeus Bots Enabled via Console Command"); } });
            AddCommand("normalbots", "Disable Zeus Bots", (p, _) => { if (CheckCommandPermission(p)) { botsEnabled = false; Server.PrintToChatAll("Zeus Bots Disabled via Console Command"); } });
            AddCommand("removeallbots", "Kick all bots", (p, _) => { if (CheckCommandPermission(p)) { Server.ExecuteCommand("bot_kick"); Server.PrintToChatAll("All bots kicked via Console Command"); } });
            AddCommand("addtbot", "Add T Bot", (p, _) => { if (CheckCommandPermission(p)) { Server.ExecuteCommand("bot_add_t"); Server.PrintToChatAll("Terrorist Bot Added via Console Command"); } });
            AddCommand("addctbot", "Add CT Bot", (p, _) => { if (CheckCommandPermission(p)) { Server.ExecuteCommand("bot_add_ct"); Server.PrintToChatAll("CT Bot Added via Console Command"); } });

            Console.WriteLine("[Zeus Bot] Continuous lifecycle brain v4 loaded.");
        }

        public override void Unload(bool hotReload)
        {
            RemoveListener<Listeners.OnTick>(OnTick);
            RemoveListener<Listeners.OnMapStart>(OnMapStart);
            bots.Clear();
            assignedNames.Clear();
        }

        // -------------------- config --------------------

        private void LoadConfig()
        {
            string configPath = Path.Combine(ModuleDirectory, "../../configs/plugins/ZeusBotAI/config.json");
            if (!File.Exists(configPath))
                configPath = Path.Combine(ModuleDirectory, "config.json");

            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var cfg = JsonSerializer.Deserialize<ZeusBotConfig>(json);
                    if (cfg != null)
                    {
                        _config = cfg;
                        Console.WriteLine($"[ZeusBotAI] Config Loaded: PLAYERS_MANAGE_BOTS={_config.PlayersManageBots}");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"[ZeusBotAI] Error loading config: {ex.Message}"); }
            }
            else
            {
                Console.WriteLine($"[ZeusBotAI] Config not found at {configPath}, using defaults.");
            }
        }

        private void SaveConfig()
        {
            string configPath = Path.Combine(ModuleDirectory, "../../configs/plugins/ZeusBotAI/config.json");
            if (!File.Exists(configPath))
                configPath = Path.Combine(ModuleDirectory, "config.json");
            try
            {
                string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
                Console.WriteLine($"[ZeusBotAI] Config Saved: PLAYERS_MANAGE_BOTS={_config.PlayersManageBots}");
            }
            catch (Exception ex) { Console.WriteLine($"[ZeusBotAI] Error saving config: {ex.Message}"); }
        }

        // -------------------- permissions / chat --------------------

        private bool CheckCommandPermission(CCSPlayerController? player)
        {
            if (player == null) return true;
            if (_config.PlayersManageBots) return true;
            if (AdminManager.PlayerHasPermissions(player, "@css/generic")) return true;
            player.PrintToChat(" \x02[ZeusBots] You do not have permission to use this command.");
            return false;
        }

        private void PrintHelpMessage(CCSPlayerController player)
        {
            bool isAdmin = AdminManager.PlayerHasPermissions(player, "@css/generic");
            bool canManage = isAdmin || _config.PlayersManageBots;
            if (!canManage) return;

            player.PrintToChat(" \x01--- \x0CZeusBotAI Commands\x01 ---");
            if (isAdmin)
            {
                player.PrintToChat(" \x0C!playersmanage\x01 - Allow players to manage bots");
                player.PrintToChat(" \x0C!adminsmanage\x01 - Restrict management to admins");
            }
            if (canManage)
            {
                player.PrintToChat(" \x0C!zeusbots\x01 - Enable Zeus Bots");
                player.PrintToChat(" \x0C!normalbots\x01 - Disable Zeus Bots");
                player.PrintToChat(" \x0C!addtbot\x01 - Add Terrorist Bot");
                player.PrintToChat(" \x0C!addctbot\x01 - Add Counter-Terrorist Bot");
                player.PrintToChat(" \x0C!removeallbots\x01 - Kick all bots");
                player.PrintToChat(" \x0C!help\x01 - Show this help");
            }
        }

        private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid) return HookResult.Continue;
            string text = info.GetArg(1).Trim();
            if (text.StartsWith("\"") && text.EndsWith("\"") && text.Length >= 2)
                text = text.Substring(1, text.Length - 2);

            bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            if (Eq(text, "zeusbots") || Eq(text, "!zeusbots"))
            { if (CheckCommandPermission(player)) { botsEnabled = true; Server.PrintToChatAll($"Zeus Bots Enabled by {player.PlayerName}"); } }
            else if (Eq(text, "normalbots") || Eq(text, "!normalbots"))
            { if (CheckCommandPermission(player)) { botsEnabled = false; Server.PrintToChatAll($"Zeus Bots Disabled by {player.PlayerName}"); } }
            else if (Eq(text, "removeallbots") || Eq(text, "!removeallbots"))
            { if (CheckCommandPermission(player)) { Server.ExecuteCommand("bot_kick"); Server.PrintToChatAll($"All bots kicked by {player.PlayerName}"); } }
            else if (Eq(text, "addtbot") || Eq(text, "!addtbot"))
            { if (CheckCommandPermission(player)) { Server.ExecuteCommand("bot_add_t"); Server.PrintToChatAll($"Terrorist Bot Added by {player.PlayerName}"); } }
            else if (Eq(text, "addctbot") || Eq(text, "!addctbot"))
            { if (CheckCommandPermission(player)) { Server.ExecuteCommand("bot_add_ct"); Server.PrintToChatAll($"CT Bot Added by {player.PlayerName}"); } }
            else if (Eq(text, "playersmanage") || Eq(text, "!playersmanage"))
            {
                if (AdminManager.PlayerHasPermissions(player, "@css/generic"))
                { _config.PlayersManageBots = true; SaveConfig(); Server.PrintToChatAll(" \x0C[ZeusBots] Config Updated: Players can now manage bots."); }
                else player.PrintToChat(" \x02[ZeusBots] You do not have permission to use this command.");
            }
            else if (Eq(text, "adminsmanage") || Eq(text, "!adminsmanage"))
            {
                if (AdminManager.PlayerHasPermissions(player, "@css/generic"))
                { _config.PlayersManageBots = false; SaveConfig(); Server.PrintToChatAll(" \x0C[ZeusBots] Config Updated: Only admins can now manage bots."); }
                else player.PrintToChat(" \x02[ZeusBots] You do not have permission to use this command.");
            }
            else if (Eq(text, "help") || Eq(text, "!help"))
            {
                PrintHelpMessage(player);
            }

            return HookResult.Continue;
        }

        // -------------------- bot naming --------------------

        private void EnsureBotName(CCSPlayerController bot)
        {
            if (bot == null || !bot.IsValid || !bot.IsBot) return;
            if (!assignedNames.ContainsKey(bot.Index))
            {
                var used = new HashSet<string>(assignedNames.Values);
                var avail = BotNames.Where(n => !used.Contains(n)).ToList();
                assignedNames[bot.Index] = avail.Count > 0
                    ? avail[rnd.Next(avail.Count)]
                    : BotNames[rnd.Next(BotNames.Length)];
            }
            string desired = assignedNames[bot.Index];
            if (bot.PlayerName != desired)
            {
                bot.PlayerName = desired;
                Utilities.SetStateChanged(bot, "CBasePlayerController", "m_iszPlayerName");
            }
        }

        // -------------------- event handlers --------------------

        private void OnMapStart(string mapName)
        {
            bots.Clear();
            TacticalIntel.Reset();
            TacticalIntel.TeamSpawns.Clear();
            TacticalIntel.TeamSpawnCenter.Clear();
        }

        private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            // Forget intel between rounds but keep spawn knowledge.
            TacticalIntel.Reset();
            foreach (var b in bots.Values)
            {
                b.Tracks.Clear();
                b.PrimaryTarget = null;
                b.Alert = 0f;
                b.Commit = 0f;
                b.CombatIntensity = 0f;
                b.HoldingAngle = false;
                b.HaveDestination = false;
            }
            return HookResult.Continue;
        }

        private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
        {
            var p = @event.Userid;
            if (p == null || !p.IsValid) return HookResult.Continue;
            if (!p.IsBot) PrintHelpMessage(p);
            else EnsureBotName(p);
            return HookResult.Continue;
        }

        private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            if (@event.Userid != null && @event.Userid.IsBot && assignedNames.ContainsKey(@event.Userid.Index))
                assignedNames.Remove(@event.Userid.Index);
            return HookResult.Continue;
        }

        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            var controller = @event.Userid;
            if (controller == null || !controller.IsValid) return HookResult.Continue;

            // Register spawn position for objective-discovery for both bots & humans
            Server.NextFrame(() =>
            {
                if (!controller.IsValid) return;
                var pawn = controller.PlayerPawn.Value;
                if (pawn != null && pawn.IsValid && pawn.AbsOrigin != null)
                    TacticalIntel.RegisterSpawn(controller.TeamNum, pawn.AbsOrigin);
            });

            if (!controller.IsBot || !botsEnabled) return HookResult.Continue;

            if (bots.ContainsKey(controller.Index)) bots.Remove(controller.Index);

            if (!assignedNames.ContainsKey(controller.Index))
                assignedNames[controller.Index] = BotNames[rnd.Next(BotNames.Length)];
            string desired = assignedNames[controller.Index];

            Server.NextFrame(() =>
            {
                if (!controller.IsValid) return;
                controller.PlayerName = desired;
                Utilities.SetStateChanged(controller, "CBasePlayerController", "m_iszPlayerName");

                var pawn = controller.PlayerPawn.Value;
                if (pawn != null && pawn.IsValid)
                {
                    bool hasZeus = false;
                    if (pawn.WeaponServices?.MyWeapons != null)
                        foreach (var w in pawn.WeaponServices.MyWeapons)
                            if (w.Value != null && w.Value.DesignerName != null && w.Value.DesignerName.Contains("taser"))
                            { hasZeus = true; break; }
                    if (!hasZeus) controller.GiveNamedItem("weapon_taser");
                }

                bots[controller.Index] = new ZeusBot(controller, Server.CurrentTime);
            });

            return HookResult.Continue;
        }

        private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
        {
            var victim = @event.Userid;
            var attacker = @event.Attacker;
            if (victim == null || !victim.IsValid) return HookResult.Continue;

            // Bot got hurt - record so it can reposition.
            if (victim.IsBot && bots.TryGetValue(victim.Index, out var bot))
            {
                bot.LastHurtAt = Server.CurrentTime;
                if (attacker != null && attacker.IsValid)
                {
                    var aPawn = attacker.PlayerPawn.Value;
                    if (aPawn != null && aPawn.AbsOrigin != null) bot.LastHurtFrom = MathUtils.Snap(aPawn.AbsOrigin);
                }
            }
            return HookResult.Continue;
        }

        private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
        {
            var victim = @event.Userid;
            var attacker = @event.Attacker;
            if (victim != null && victim.IsValid)
            {
                var vPawn = victim.PlayerPawn.Value;
                if (vPawn != null && vPawn.AbsOrigin != null)
                {
                    // A death is a very loud event - everyone hears it.
                    TacticalIntel.RecordSound(vPawn.AbsOrigin, Server.CurrentTime, victim.TeamNum, 0.8f);
                }
            }

            if (attacker != null && attacker.IsValid && attacker.IsBot
                && bots.TryGetValue(attacker.Index, out var bot))
            {
                bot.LastKillAt = Server.CurrentTime;
            }
            return HookResult.Continue;
        }

        private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
        {
            var shooter = @event.Userid;
            if (shooter == null || !shooter.IsValid) return HookResult.Continue;
            var pawn = shooter.PlayerPawn.Value;
            if (pawn == null || pawn.AbsOrigin == null) return HookResult.Continue;
            // Loudness varies by weapon - knives & tasers are quieter.
            float loud = 1f;
            string wep = @event.Weapon ?? string.Empty;
            if (wep.Contains("knife")) loud = 0.25f;
            else if (wep.Contains("taser")) loud = 0.5f;
            TacticalIntel.RecordSound(pawn.AbsOrigin, Server.CurrentTime, shooter.TeamNum, loud);
            return HookResult.Continue;
        }

        private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
        {
            var p = @event.Userid;
            if (p == null) return HookResult.Continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn != null && pawn.AbsOrigin != null)
            {
                TacticalIntel.PlantedBomb = MathUtils.Snap(pawn.AbsOrigin);
                TacticalIntel.PlantedBombTime = Server.CurrentTime;
            }
            return HookResult.Continue;
        }

        private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
        {
            TacticalIntel.PlantedBomb = null;
            return HookResult.Continue;
        }

        private HookResult OnBombPickup(EventBombPickup @event, GameEventInfo info)
        {
            TacticalIntel.DroppedBomb = null;
            return HookResult.Continue;
        }

        private HookResult OnBombDropped(EventBombDropped @event, GameEventInfo info)
        {
            var p = @event.Userid;
            if (p != null)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn != null && pawn.AbsOrigin != null)
                {
                    TacticalIntel.DroppedBomb = MathUtils.Snap(pawn.AbsOrigin);
                    TacticalIntel.DroppedBombTime = Server.CurrentTime;
                }
            }
            return HookResult.Continue;
        }

        // -------------------- per-tick driver --------------------

        private void OnTick()
        {
            if (!botsEnabled) return;

            float now = Server.CurrentTime;
            float dt = Server.TickInterval;

            TacticalIntel.Prune(now);

            var players = Utilities.GetPlayers();
            var alive = players.Where(p => p != null && p.IsValid && p.PawnIsAlive).ToList();
            var aliveBots = alive.Where(p => p.IsBot).ToList();

            // Clean up dead/disconnected
            var stale = bots.Keys.Where(k => !aliveBots.Any(b => b.Index == k)).ToList();
            foreach (var k in stale) bots.Remove(k);

            foreach (var bot in aliveBots)
            {
                if (!bots.TryGetValue(bot.Index, out var z))
                {
                    z = new ZeusBot(bot, now);
                    bots[bot.Index] = z;
                }

                Brain.Tick(z, alive, now, dt);
                InjectMotor(z);
            }
        }

        // ---------------------------------------------------------------------
        //  Push the bot's per-tick desired state into the engine. We always
        //  control the body - we never hand off to the native CS2 bot AI
        //  while the plugin is enabled.
        // ---------------------------------------------------------------------
        private void InjectMotor(ZeusBot z)
        {
            var pawn = z.Pawn;
            if (pawn?.MovementServices == null || !pawn.IsValid) return;

            QAngle outAngles = z.AimGoal;
            Vector vel = pawn.AbsVelocity ?? new Vector(0, 0, 0);
            Vector injected = new Vector(vel.X, vel.Y, vel.Z);

            float speed = z.DesiredSpeed;
            // Walk modifier: keeps footsteps below shift-walk threshold (~135u/s)
            if (z.WantWalk && speed > 130f) speed = 130f;

            if (speed > 1f && z.DesiredMove.Length() > 0.1f)
            {
                Vector dir = MathUtils.Normalize(z.DesiredMove);
                injected.X = dir.X * speed;
                injected.Y = dir.Y * speed;
            }
            else
            {
                // Letting bot stand still: drain horizontal velocity smoothly.
                injected.X *= 0.5f;
                injected.Y *= 0.5f;
            }

            bool grounded = ((uint)pawn.Flags & 1) != 0;
            if (grounded) injected.Z = -15f;

            // Jump request: convert button into an actual velocity impulse.
            if ((z.PendingButtons & (ulong)PlayerButtons.Jump) != 0)
            {
                if (grounded)
                    injected.Z = 300f;
                z.PendingButtons &= ~((ulong)PlayerButtons.Jump);
            }

            // Apply other buttons (duck / attack) through the engine.
            pawn.MovementServices.Buttons.ButtonStates[0] |= z.PendingButtons;

            // Teleport applies the velocity & body yaw without leaning the hull.
            QAngle bodyAngles = new QAngle(0f, outAngles.Y, outAngles.Z);
            pawn.Teleport(null, bodyAngles, injected);

            // Eye angles separately so view tracking is independent of body yaw.
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
    }

    #endregion
}
