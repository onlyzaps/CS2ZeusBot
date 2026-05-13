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
    //  ZEUS BOT AI v5  -  "Indistinguishable from a player"
    //
    //  This plugin gives Counter-Strike 2 bots an entirely custom brain that
    //  the plugin drives from spawn to death. It has three big pillars:
    //
    //    1. A self-learning NAV GRAPH built from every alive player's
    //       movement (humans and bots). The graph is a 3D voxel grid of
    //       walkable cells with traversal edges. The graph persists to
    //       disk per-map, so bots get smarter every round and across
    //       server restarts. No reliance on Source 2 navmesh exposure.
    //
    //    2. AUDIO SIMULATION with line-through-graph occlusion. Gunfire,
    //       footsteps and deaths emit sound events. Each bot computes
    //       *its own* perceived audibility per sound and can pinpoint
    //       direction with skill-dependent error. Audible footsteps spawn
    //       fuzzy enemy intel.
    //
    //    3. A continuous BRAIN (Roam / Probe / Engage / Reposition) with
    //       smooth cognitive scalars (Alert / Commit / Intensity / Stealth)
    //       so motion never feels like a switch flipped. Personality, head
    //       sweeps, pre-aim of map hot spots, micro-pauses, reaction time,
    //       and aim noise complete the human impression.
    //
    //  Single-file by design - drop into the CounterStrikeSharp plugins
    //  folder, no extra assets needed (graph data files are auto-created).
    // =========================================================================

    #region Math utilities

    public static class MathUtils
    {
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public const float Rad2Deg = (float)(180.0 / Math.PI);

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
            float pitchRad = angles.X * Deg2Rad;
            float yawRad = angles.Y * Deg2Rad;
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
            float k = 1f - (float)Math.Exp(-rate * dt);
            return current + (target - current) * k;
        }

        // CS:S Vector is a reference type backed by live entity memory; snapshot
        // whenever we want to *remember* a position rather than continuously
        // poll the entity.
        public static Vector Snap(Vector v) => new Vector(v.X, v.Y, v.Z);
    }

    #endregion

    #region Nav graph

    // -------------------------------------------------------------------------
    //  GridCoord - integer key for a 3D cell of the world.
    //  We use a cubic voxel grid so cells are easy to hash and reason about.
    //  CELL_SIZE was chosen to match a comfortable "step" for a CS2 player
    //  (about 1.5x the hull width) and to give A* enough resolution to find
    //  meaningful paths without exploding node counts.
    // -------------------------------------------------------------------------
    public struct GridCoord : IEquatable<GridCoord>
    {
        public const float CELL_SIZE = 96f;

        public int X, Y, Z;
        public GridCoord(int x, int y, int z) { X = x; Y = y; Z = z; }

        public static GridCoord FromWorld(Vector v) =>
            new GridCoord(
                (int)Math.Floor(v.X / CELL_SIZE),
                (int)Math.Floor(v.Y / CELL_SIZE),
                (int)Math.Floor(v.Z / CELL_SIZE));

        public Vector ToWorldCenter() =>
            new Vector(
                (X + 0.5f) * CELL_SIZE,
                (Y + 0.5f) * CELL_SIZE,
                (Z + 0.5f) * CELL_SIZE);

        public bool Equals(GridCoord o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object? o) => o is GridCoord g && Equals(g);
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791));
        public override string ToString() => $"{X},{Y},{Z}";
    }

    // -------------------------------------------------------------------------
    //  NavCell - one walkable voxel observed in the map.  We keep a running
    //  centroid of all positions sampled inside the cell, plus information
    //  about strategic value: how many enemy sightings happened here, how
    //  many deaths, how often it lies on a route between bombsites, etc.
    // -------------------------------------------------------------------------
    public class NavCell
    {
        public GridCoord Coord;
        public float Cx, Cy, Cz;          // running centroid of observed positions
        public int Samples;               // how many observations contributed
        public HashSet<GridCoord> Neighbors = new();
        public float HotSpot;             // strategic / fight density (0..)
        public float Danger;              // recent enemy presence (decays)
        public float LastSeenTime;        // last time a player was observed here

        public Vector Center => new Vector(Cx, Cy, Cz);

        public void Observe(Vector pos, float time)
        {
            Samples++;
            // exponential moving average so cells track their actual centroid
            float k = Samples == 1 ? 1f : 0.1f;
            Cx += (pos.X - Cx) * k;
            Cy += (pos.Y - Cy) * k;
            Cz += (pos.Z - Cz) * k;
            LastSeenTime = time;
        }
    }

    // -------------------------------------------------------------------------
    //  Persistence DTOs (for JSON serialization of the nav graph).
    // -------------------------------------------------------------------------
    public class NavCellDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public float Cx { get; set; }
        public float Cy { get; set; }
        public float Cz { get; set; }
        public int Samples { get; set; }
        public float HotSpot { get; set; }
        public int[] NX { get; set; } = Array.Empty<int>();
        public int[] NY { get; set; } = Array.Empty<int>();
        public int[] NZ { get; set; } = Array.Empty<int>();
    }
    public class NavGraphDto
    {
        public string Map { get; set; } = "";
        public int Version { get; set; } = 1;
        public List<NavCellDto> Cells { get; set; } = new();
    }

    // -------------------------------------------------------------------------
    //  NavGraph - the entire learnt map.  Cells are added by observing
    //  player movement; edges are added when the same player walks from
    //  one cell to another within a tight time window (so we don't connect
    //  cells across teleports, ladders we can't see, or screen-side
    //  spectator clipping).
    // -------------------------------------------------------------------------
    public class NavGraph
    {
        public Dictionary<GridCoord, NavCell> Cells = new();
        public string MapName = "";
        public bool Dirty;

        // Reverse adjacency cache for nicer lookup (unused for now, kept for
        // future use such as flood-fill defensive zones).
        public NavCell GetOrCreate(GridCoord c, Vector observed, float time)
        {
            if (!Cells.TryGetValue(c, out var cell))
            {
                cell = new NavCell
                {
                    Coord = c,
                    Cx = observed.X,
                    Cy = observed.Y,
                    Cz = observed.Z,
                    LastSeenTime = time
                };
                Cells[c] = cell;
                Dirty = true;
            }
            cell.Observe(observed, time);
            return cell;
        }

        public void LinkBidirectional(GridCoord a, GridCoord b)
        {
            if (!Cells.TryGetValue(a, out var ca) || !Cells.TryGetValue(b, out var cb)) return;
            if (ca.Neighbors.Add(b)) Dirty = true;
            if (cb.Neighbors.Add(a)) Dirty = true;
        }

        public NavCell? Nearest(Vector pos, float maxDist = 256f)
        {
            // Search a small expanding cube around the query cell.
            GridCoord home = GridCoord.FromWorld(pos);
            NavCell? best = null;
            float bestSq = maxDist * maxDist;
            for (int radius = 0; radius <= 3; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)), Math.Abs(dz)) != radius) continue;
                    var key = new GridCoord(home.X + dx, home.Y + dy, home.Z + dz);
                    if (!Cells.TryGetValue(key, out var cell)) continue;
                    float ddx = cell.Cx - pos.X, ddy = cell.Cy - pos.Y, ddz = cell.Cz - pos.Z;
                    float sq = ddx * ddx + ddy * ddy + ddz * ddz;
                    if (sq < bestSq) { bestSq = sq; best = cell; }
                }
                if (best != null && radius >= 1) break;
            }
            return best;
        }

        // -----------------------------------------------------------------
        //  Track per-player position history to learn graph edges.  We only
        //  link cells when the same actor was in both within ~0.6s - that
        //  rules out teleporting, dying-and-respawning, and round resets.
        // -----------------------------------------------------------------
        private readonly Dictionary<uint, (GridCoord coord, float time)> _lastSeenByActor = new();

        public void ObservePlayer(uint actorIndex, Vector pos, float time)
        {
            GridCoord coord = GridCoord.FromWorld(pos);
            GetOrCreate(coord, pos, time);

            if (_lastSeenByActor.TryGetValue(actorIndex, out var prev))
            {
                if (!prev.coord.Equals(coord) && time - prev.time < 0.6f)
                {
                    // Reject obviously bogus edges (manhattan jumps > 2 cells)
                    int adx = Math.Abs(prev.coord.X - coord.X);
                    int ady = Math.Abs(prev.coord.Y - coord.Y);
                    int adz = Math.Abs(prev.coord.Z - coord.Z);
                    if (adx + ady + adz <= 4 && adz <= 2)
                    {
                        // Make sure the prev cell exists too (we sampled it earlier)
                        if (Cells.ContainsKey(prev.coord)) LinkBidirectional(prev.coord, coord);
                    }
                }
            }
            _lastSeenByActor[(uint)actorIndex] = (coord, time);
        }

        public void ForgetActor(uint actorIndex) => _lastSeenByActor.Remove(actorIndex);

        public void RecordHotSpot(Vector pos, float amount)
        {
            var c = Nearest(pos, 200f);
            if (c == null) return;
            c.HotSpot = Math.Min(c.HotSpot + amount, 50f);
        }

        public void DecayHotspots(float dt)
        {
            // Slow decay so hot spots reflect long-term map knowledge.
            float k = (float)Math.Exp(-dt * 0.002f);
            foreach (var c in Cells.Values)
            {
                c.HotSpot *= k;
                c.Danger = Math.Max(0f, c.Danger - dt * 0.5f);
            }
        }

        public NavGraphDto ToDto()
        {
            var dto = new NavGraphDto { Map = MapName, Version = 1 };
            foreach (var c in Cells.Values)
            {
                var nx = new int[c.Neighbors.Count];
                var ny = new int[c.Neighbors.Count];
                var nz = new int[c.Neighbors.Count];
                int i = 0;
                foreach (var n in c.Neighbors) { nx[i] = n.X; ny[i] = n.Y; nz[i] = n.Z; i++; }
                dto.Cells.Add(new NavCellDto
                {
                    X = c.Coord.X, Y = c.Coord.Y, Z = c.Coord.Z,
                    Cx = c.Cx, Cy = c.Cy, Cz = c.Cz,
                    Samples = c.Samples, HotSpot = c.HotSpot,
                    NX = nx, NY = ny, NZ = nz
                });
            }
            return dto;
        }

        public static NavGraph FromDto(NavGraphDto dto)
        {
            var g = new NavGraph { MapName = dto.Map };
            foreach (var cd in dto.Cells)
            {
                var coord = new GridCoord(cd.X, cd.Y, cd.Z);
                var cell = new NavCell
                {
                    Coord = coord,
                    Cx = cd.Cx, Cy = cd.Cy, Cz = cd.Cz,
                    Samples = cd.Samples,
                    HotSpot = cd.HotSpot
                };
                for (int i = 0; i < cd.NX.Length; i++)
                    cell.Neighbors.Add(new GridCoord(cd.NX[i], cd.NY[i], cd.NZ[i]));
                g.Cells[coord] = cell;
            }
            return g;
        }
    }

    #endregion

    #region A* path planner

    // -------------------------------------------------------------------------
    //  A simple, allocation-light A* over the NavGraph.  The cost function
    //  blends distance with a danger penalty so bots prefer "clean" routes
    //  to active fight cells.
    // -------------------------------------------------------------------------
    public static class AStar
    {
        public class Node
        {
            public GridCoord Coord;
            public float G;       // cost from start
            public float F;       // G + heuristic
            public GridCoord? From;
        }

        public static List<Vector>? FindPath(NavGraph g, Vector startPos, Vector endPos, float dangerWeight = 80f, int maxExpansions = 4000)
        {
            var startCell = g.Nearest(startPos, 256f);
            var endCell = g.Nearest(endPos, 384f);
            if (startCell == null || endCell == null) return null;
            if (startCell.Coord.Equals(endCell.Coord))
            {
                return new List<Vector> { endCell.Center };
            }

            var open = new PriorityQueue<GridCoord, float>();
            var nodes = new Dictionary<GridCoord, Node>();
            var closed = new HashSet<GridCoord>();

            var startNode = new Node { Coord = startCell.Coord, G = 0, F = Heuristic(startCell.Center, endCell.Center) };
            nodes[startCell.Coord] = startNode;
            open.Enqueue(startCell.Coord, startNode.F);

            int expansions = 0;
            while (open.Count > 0 && expansions < maxExpansions)
            {
                var current = open.Dequeue();
                if (current.Equals(endCell.Coord))
                    return Reconstruct(g, nodes, current);
                if (!closed.Add(current)) continue;
                expansions++;
                if (!g.Cells.TryGetValue(current, out var cell)) continue;
                var curNode = nodes[current];

                foreach (var nb in cell.Neighbors)
                {
                    if (closed.Contains(nb)) continue;
                    if (!g.Cells.TryGetValue(nb, out var nbCell)) continue;

                    float step = (cell.Center - nbCell.Center).Length();
                    float dangerCost = nbCell.Danger * dangerWeight;
                    float tentativeG = curNode.G + step + dangerCost;

                    if (!nodes.TryGetValue(nb, out var nbNode) || tentativeG < nbNode.G)
                    {
                        nbNode = new Node
                        {
                            Coord = nb,
                            G = tentativeG,
                            F = tentativeG + Heuristic(nbCell.Center, endCell.Center),
                            From = current
                        };
                        nodes[nb] = nbNode;
                        open.Enqueue(nb, nbNode.F);
                    }
                }
            }
            return null;
        }

        private static float Heuristic(Vector a, Vector b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            // Mild Z bias: vertical moves are more expensive
            return (float)Math.Sqrt(dx * dx + dy * dy) + Math.Abs(dz) * 1.3f;
        }

        private static List<Vector> Reconstruct(NavGraph g, Dictionary<GridCoord, Node> nodes, GridCoord end)
        {
            var raw = new List<GridCoord>();
            GridCoord? cur = end;
            while (cur.HasValue)
            {
                raw.Add(cur.Value);
                cur = nodes[cur.Value].From;
            }
            raw.Reverse();
            // String-pull: drop collinear cells so we don't waypoint every voxel.
            var simplified = new List<Vector>();
            for (int i = 0; i < raw.Count; i++)
            {
                if (i == 0 || i == raw.Count - 1) { simplified.Add(g.Cells[raw[i]].Center); continue; }
                var a = g.Cells[raw[i - 1]].Center;
                var b = g.Cells[raw[i]].Center;
                var c = g.Cells[raw[i + 1]].Center;
                var d1 = MathUtils.Normalize(b - a);
                var d2 = MathUtils.Normalize(c - b);
                if (MathUtils.Dot(d1, d2) < 0.985f) simplified.Add(b);
            }
            return simplified;
        }
    }

    #endregion

    #region Audio simulation

    public enum SoundKind { Gunfire, Footstep, Death, Bomb, Reload, Spotted }

    public class SoundEvent
    {
        public Vector Position = new Vector(0, 0, 0);
        public float Time;
        public int Team;       // team of emitter (so bots can prefer enemy sounds)
        public uint EmitterId; // optional - so we can avoid hearing ourselves
        public SoundKind Kind;
        public float BaseLoudness; // 0..1
    }

    // -------------------------------------------------------------------------
    //  AudioSim - tracks emitted sounds and computes audibility on demand.
    //  We use the nav graph as a free occlusion oracle: if the straight line
    //  from emitter to listener crosses cells that *don't* exist in the
    //  graph (unwalkable space = walls), that's evidence of occlusion.
    // -------------------------------------------------------------------------
    public static class AudioSim
    {
        public static readonly List<SoundEvent> Events = new();

        public static void Emit(SoundKind kind, Vector pos, int team, uint emitter, float now, float loud)
        {
            Events.Add(new SoundEvent
            {
                Position = MathUtils.Snap(pos),
                Time = now,
                Team = team,
                EmitterId = emitter,
                Kind = kind,
                BaseLoudness = loud,
            });
            if (Events.Count > 256) Events.RemoveAt(0);
        }

        public static void Prune(float now)
        {
            Events.RemoveAll(s => now - s.Time > 12f);
        }

        // Returns audibility 0..1 of a sound at the listener position, given
        // the current nav graph and an age factor.  >0.05 is roughly the
        // threshold where a listener can pick out direction.
        public static float Audibility(SoundEvent ev, Vector listenerPos, NavGraph graph, float now)
        {
            float age = now - ev.Time;
            if (age < 0f) age = 0f;
            float lifetime = ev.Kind switch
            {
                SoundKind.Gunfire => 2.5f,
                SoundKind.Death => 4f,
                SoundKind.Footstep => 0.6f,
                SoundKind.Bomb => 5f,
                SoundKind.Reload => 1.5f,
                SoundKind.Spotted => 3f,
                _ => 2f,
            };
            if (age > lifetime) return 0f;
            float ageFactor = 1f - age / lifetime;

            float dist = (ev.Position - listenerPos).Length();
            // Distance attenuation: roughly inverse-quadratic with a 2200u
            // cap (after that nothing in CS2 really matters).
            float maxRange = ev.Kind switch
            {
                SoundKind.Gunfire => 2400f,
                SoundKind.Death => 1800f,
                SoundKind.Footstep => 900f,
                SoundKind.Bomb => 3000f,
                SoundKind.Reload => 700f,
                SoundKind.Spotted => 1500f,
                _ => 1500f,
            };
            if (dist > maxRange) return 0f;
            float distAtten = 1f - dist / maxRange;
            distAtten *= distAtten;

            float occlusion = EstimateOcclusion(ev.Position, listenerPos, graph);

            return MathUtils.Clamp01(ev.BaseLoudness * ageFactor * distAtten * occlusion);
        }

        // Walk a straight line between emitter and listener in nav-graph
        // space; if many cells along the line don't exist as graph cells we
        // treat that as walls in between.  Cheap and surprisingly effective.
        public static float EstimateOcclusion(Vector a, Vector b, NavGraph graph)
        {
            float dist = (b - a).Length();
            int samples = Math.Clamp((int)(dist / 64f), 2, 24);
            int known = 0;
            for (int i = 1; i < samples - 1; i++)
            {
                float t = i / (float)(samples - 1);
                var p = new Vector(
                    a.X + (b.X - a.X) * t,
                    a.Y + (b.Y - a.Y) * t,
                    a.Z + (b.Z - a.Z) * t);
                var g = GridCoord.FromWorld(p);
                if (graph.Cells.ContainsKey(g)
                    || graph.Cells.ContainsKey(new GridCoord(g.X, g.Y, g.Z - 1))
                    || graph.Cells.ContainsKey(new GridCoord(g.X, g.Y, g.Z + 1)))
                    known++;
            }
            float frac = samples <= 2 ? 1f : known / (float)(samples - 2);
            // Map known-fraction to occlusion: 100% known cells = full
            // audibility, 30% known = heavily muffled (around 0.3 audible).
            return MathUtils.Clamp01(0.15f + frac * 0.85f);
        }
    }

    #endregion

    #region Personality

    public class Personality
    {
        public float Aggression;
        public float Patience;
        public float Skill;
        public float ReactionTime;
        public float AimNoise;
        public float LaneOffset;
        public float PeekJitter;
        public float JumpBias;
        public float CrouchBias;
        public float StealthBias;
        public float Curiosity;     // how likely to investigate sounds
        public float MicroPauseBias;// likelihood of brief stop-and-look mid-route

        public static Personality RollFor(uint seed)
        {
            unchecked
            {
                uint s = seed * 2654435761u;
                Random r = new Random((int)s);
                return new Personality
                {
                    Aggression     = (float)(r.NextDouble() * 0.55 + 0.25),
                    Patience       = (float)(r.NextDouble() * 0.55 + 0.25),
                    Skill          = (float)(r.NextDouble() * 0.4  + 0.55),
                    ReactionTime   = (float)(r.NextDouble() * 0.16 + 0.13),
                    AimNoise       = (float)(r.NextDouble() * 5.0  + 3.0),
                    LaneOffset     = (float)(r.NextDouble() * Math.PI * 2),
                    PeekJitter     = (float)(r.NextDouble() * 0.5  + 0.4),
                    JumpBias       = (float)(r.NextDouble() * 0.6  + 0.3),
                    CrouchBias     = (float)(r.NextDouble() * 0.6  + 0.2),
                    StealthBias    = (float)(r.NextDouble() * 0.7  + 0.2),
                    Curiosity      = (float)(r.NextDouble() * 0.7  + 0.2),
                    MicroPauseBias = (float)(r.NextDouble() * 0.55 + 0.2),
                };
            }
        }
    }

    #endregion

    #region Enemy memory

    public enum IntelSource { Visual, Audio, Teammate }

    public class EnemyTrack
    {
        public CCSPlayerController Subject = null!;
        public Vector LastSeenPos = new Vector(0, 0, 0);
        public Vector LastSeenVel = new Vector(0, 0, 0);
        public float LastSeenTime = -999f;
        public float FirstContactTime = -999f;
        public bool CurrentlyVisible;
        public float VisibleStreak;
        public float Confidence;
        public float Threat;
        public IntelSource Source = IntelSource.Visual;
        public float PositionUncertainty; // radius of plausible position in units

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

    public static class TacticalIntel
    {
        public static Vector? PlantedBomb;
        public static float PlantedBombTime = -999f;
        public static Vector? DroppedBomb;
        public static float DroppedBombTime = -999f;

        public static Dictionary<int, List<Vector>> TeamSpawns = new();
        public static Dictionary<int, Vector> TeamSpawnCenter = new();

        public static void RegisterSpawn(int team, Vector posLive)
        {
            if (!TeamSpawns.ContainsKey(team)) TeamSpawns[team] = new List<Vector>();
            var list = TeamSpawns[team];
            if (list.Count >= 12) return;
            foreach (var p in list)
                if ((p - posLive).Length() < 64f) return;
            var pos = MathUtils.Snap(posLive);
            list.Add(pos);
            float x = 0, y = 0, z = 0;
            foreach (var p in list) { x += p.X; y += p.Y; z += p.Z; }
            TeamSpawnCenter[team] = new Vector(x / list.Count, y / list.Count, z / list.Count);
        }

        public static void Reset()
        {
            PlantedBomb = null;
            DroppedBomb = null;
            PlantedBombTime = -999f;
            DroppedBombTime = -999f;
        }

        public static Vector MapCenter()
        {
            if (TeamSpawnCenter.TryGetValue(2, out var t) && TeamSpawnCenter.TryGetValue(3, out var ct))
                return MathUtils.Lerp(t, ct, 0.5f);
            if (TeamSpawnCenter.Count > 0) return TeamSpawnCenter.Values.First();
            return new Vector(0, 0, 0);
        }
    }

    #endregion

    #region The ZeusBot

    public enum BotPhase { Roam, Probe, Engage, Reposition, Investigate }

    public class ZeusBot
    {
        public CCSPlayerController Controller;
        public CCSPlayerPawn Pawn => Controller.PlayerPawn.Value!;
        public Personality Personality;

        public Dictionary<uint, EnemyTrack> Tracks = new();
        public EnemyTrack? PrimaryTarget;

        // === Cognitive scalars ============================================
        public float Alert;
        public float Commit;
        public float CombatIntensity;
        public float Stealth;
        public float Curiosity;       // current desire to investigate audio

        // === Phase =======================================================
        public BotPhase Phase = BotPhase.Roam;
        public float PhaseEnteredAt;
        public Vector? InvestigateTarget;   // sound or stale intel position
        public float InvestigateUntil;

        // === Aim =========================================================
        public QAngle AimGoal = new QAngle(0, 0, 0);
        public float ReactionRemaining;
        public uint? LockedTargetIndex;
        public float HeadSweepUntil;
        public Vector? HeadSweepFocus;
        public float NextHeadSweepAt;

        // === Path follower ==============================================
        public List<Vector> Path = new();
        public int PathIndex;
        public Vector FinalGoal = new Vector(0, 0, 0);
        public bool HaveGoal;
        public float NextRepathAt;
        public float StuckTimer;
        public Vector LastPos = new Vector(0, 0, 0);
        public float StuckDodgeUntil;
        public float StuckDodgeYaw;

        // === Combat micro =============================================
        public float NextMicroChangeAt;
        public int MicroPattern;
        public float StrafeDir = 1f;
        public float JumpCooldown;
        public float DuckUntil;
        public float FireCooldown;
        public bool HoldingAngle;
        public float HoldUntil;
        public Vector? HoldPosition;
        public Vector? HoldFacing;

        // === Weapon ===================================================
        public string DesiredWeapon = "knife";
        public float DesiredWeaponSince;
        public float WeaponSwitchCooldown;

        // === Footstep audio emission =================================
        public float NextFootstepAt;

        // === Events ===================================================
        public float SpawnedAt;
        public float LastHurtAt = -999f;
        public Vector? LastHurtFrom;
        public float LastKillAt = -999f;

        // === Per-tick outputs ========================================
        public Vector DesiredMove = new Vector(0, 0, 0);
        public float DesiredSpeed;
        public ulong PendingButtons;
        public bool WantWalk;

        public float NoisePhase;
        // Tiny per-tick humanization noise on speed.
        public float SpeedJitter;

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

        public void ClearPath()
        {
            Path.Clear();
            PathIndex = 0;
            NextRepathAt = 0f;
        }
    }

    #endregion

    #region Brain - sensors, decision, motor

    public static class Brain
    {
        // ---------------------------------------------------------------------
        //  Per-tick entry point.
        // ---------------------------------------------------------------------
        public static void Tick(ZeusBot bot, List<CCSPlayerController> allPlayers, NavGraph graph, float now, float dt)
        {
            if (bot.Pawn == null || !bot.Pawn.IsValid) return;

            // Wipe per-frame outputs
            bot.DesiredMove = new Vector(0, 0, 0);
            bot.DesiredSpeed = 0f;
            bot.PendingButtons = 0;
            bot.WantWalk = false;

            UpdateSensors(bot, allPlayers, graph, now, dt);
            UpdateAudio(bot, graph, now, dt);
            ChooseTarget(bot, now);
            UpdateScalars(bot, now, dt);
            UpdatePhase(bot, now);
            UpdateGoal(bot, graph, now);
            UpdatePath(bot, graph, now, dt);
            UpdateMotor(bot, graph, now, dt);
            UpdateAim(bot, graph, now, dt);
            UpdateWeapon(bot, now);
            UpdateFire(bot, now);
            EmitFootsteps(bot, now);
        }

        // =====================================================================
        //  Vision
        // =====================================================================
        private static void UpdateSensors(ZeusBot bot, List<CCSPlayerController> allPlayers, NavGraph graph, float now, float dt)
        {
            Vector myPos = bot.Pawn.AbsOrigin!;
            Vector myFwd = MathUtils.GetForwardVector(bot.Pawn.EyeAngles!);

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
                bool inFov = fovDot > -0.1f;
                bool seesWell = fovDot > 0.4f;

                bool canSee = spotted && plausibleSameFloor && (inFov || dist < 220f);

                // Mark the cell containing the visible enemy as dangerous so
                // other bots route around it.
                if (canSee)
                {
                    var cell = graph.Nearest(ePos, 200f);
                    if (cell != null) { cell.Danger = Math.Min(2f, cell.Danger + dt * 4f); cell.HotSpot += 0.01f; }
                }

                if (canSee)
                {
                    if (!track.CurrentlyVisible) track.FirstContactTime = now;
                    track.CurrentlyVisible = true;
                    track.VisibleStreak += dt;
                    track.LastSeenPos = MathUtils.Snap(ePos);
                    track.LastSeenVel = MathUtils.Snap(eVel);
                    track.LastSeenTime = now;
                    track.Source = IntelSource.Visual;
                    track.PositionUncertainty = 0f;
                    float climbRate = seesWell ? 6f : 3f;
                    track.Confidence = MathUtils.SmoothApproach(track.Confidence, 1f, climbRate, dt);
                }
                else
                {
                    track.VisibleStreak = 0f;
                    float age = now - track.LastSeenTime;
                    float target = MathUtils.Clamp01(1.5f - age * 0.5f);
                    track.Confidence = MathUtils.SmoothApproach(track.Confidence, target, 1.2f, dt);
                    // Uncertainty grows as time passes since last sighting.
                    track.PositionUncertainty += MathUtils.Length(track.LastSeenVel) * dt * 0.6f;
                }

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
                track.Threat = MathUtils.SmoothApproach(track.Threat, rawThreat, 4f, dt);
            }
        }

        // =====================================================================
        //  Audio - turn audible sounds into "fuzzy" enemy intel and into
        //  cognitive pressure (Alert).
        // =====================================================================
        private static void UpdateAudio(ZeusBot bot, NavGraph graph, float now, float dt)
        {
            Vector me = bot.Pawn.AbsOrigin!;
            foreach (var ev in AudioSim.Events)
            {
                if (ev.EmitterId == bot.Controller.Index) continue;
                if (ev.Team == bot.Controller.TeamNum) continue; // ignore friendlies

                float aud = AudioSim.Audibility(ev, me, graph, now);
                if (aud < 0.04f) continue;

                // Only "intel-generating" sounds spawn or refresh fuzzy tracks.
                if (ev.Kind == SoundKind.Footstep || ev.Kind == SoundKind.Gunfire
                    || ev.Kind == SoundKind.Bomb || ev.Kind == SoundKind.Spotted
                    || ev.Kind == SoundKind.Reload)
                {
                    // Skill-dependent direction error: less skilled bots
                    // mis-locate sounds.
                    float err = (1f - bot.Personality.Skill) * 250f * (1f - aud);
                    Random r = new Random((int)(ev.Time * 1000 + bot.Controller.Index));
                    float ang = (float)(r.NextDouble() * Math.PI * 2);
                    Vector noisyPos = new Vector(
                        ev.Position.X + (float)Math.Cos(ang) * err,
                        ev.Position.Y + (float)Math.Sin(ang) * err,
                        ev.Position.Z);

                    // Find a synthetic actor-id for the sound (we don't know
                    // which enemy made it). Use a stable hash so repeated
                    // sounds from the same area refresh the same fuzzy track.
                    var gridKey = GridCoord.FromWorld(noisyPos);
                    uint pseudoId = (uint)(0x80000000u
                        ^ (uint)gridKey.X
                        ^ ((uint)gridKey.Y << 8)
                        ^ ((uint)gridKey.Z << 16));

                    if (!bot.Tracks.TryGetValue(pseudoId, out var track))
                    {
                        track = new EnemyTrack { Subject = null!, Source = IntelSource.Audio };
                        bot.Tracks[pseudoId] = track;
                    }
                    if (track.Source != IntelSource.Visual)
                    {
                        track.LastSeenPos = noisyPos;
                        track.LastSeenVel = new Vector(0, 0, 0);
                        track.LastSeenTime = now;
                        track.PositionUncertainty = 100f + err;
                        // Confidence from audio is bounded - we never "see"
                        // them through hearing alone.
                        float audConf = MathUtils.Clamp01(aud * 0.6f);
                        track.Confidence = Math.Max(track.Confidence, audConf);
                        // Threat is lower than visual threat at equal aud.
                        track.Threat = Math.Max(track.Threat, audConf * 0.6f);
                    }
                }
            }
        }

        // =====================================================================
        //  Target selection
        // =====================================================================
        private static void ChooseTarget(ZeusBot bot, float now)
        {
            EnemyTrack? best = null;
            float bestScore = 0f;
            foreach (var t in bot.Tracks.Values)
            {
                if (t.Threat < 0.05f) continue;
                float score = t.Threat + (t.CurrentlyVisible ? 0.15f : 0f);
                if (bot.PrimaryTarget == t) score += 0.12f;
                // Slight preference for tracks we can actually shoot - visual
                // contacts over audio contacts.
                if (t.Source == IntelSource.Visual) score += 0.05f;
                if (score > bestScore) { bestScore = score; best = t; }
            }

            if (best == null)
            {
                bot.PrimaryTarget = null;
                bot.LockedTargetIndex = null;
                return;
            }
            uint? newKey = best.Subject?.Index;
            if (bot.LockedTargetIndex != newKey && best.Source == IntelSource.Visual)
            {
                bot.LockedTargetIndex = newKey;
                bot.ReactionRemaining = bot.Personality.ReactionTime
                    * (1.2f - 0.6f * bot.Personality.Skill);
            }
            bot.PrimaryTarget = best;
        }

        // =====================================================================
        //  Cognitive scalars
        // =====================================================================
        private static void UpdateScalars(ZeusBot bot, float now, float dt)
        {
            bot.ReactionRemaining = Math.Max(0f, bot.ReactionRemaining - dt);

            // Build sound pressure from currently audible enemy sounds.
            Vector me = bot.Pawn.AbsOrigin!;
            float soundPressure = 0f;
            foreach (var ev in AudioSim.Events)
            {
                if (ev.Team == bot.Controller.TeamNum) continue;
                if (ev.EmitterId == bot.Controller.Index) continue;
                // For pressure we don't recompute occlusion every tick (we do
                // it in UpdateAudio); approximate with distance/age only.
                float age = now - ev.Time;
                if (age > 5f) continue;
                float dist = (ev.Position - me).Length();
                if (dist > 2200f) continue;
                float ageF = MathUtils.Clamp01(1f - age / 5f);
                float distF = MathUtils.Clamp01(1f - dist / 2200f);
                float weight = ev.Kind == SoundKind.Gunfire ? 1f
                            : ev.Kind == SoundKind.Death ? 0.7f
                            : ev.Kind == SoundKind.Bomb ? 0.9f
                            : ev.Kind == SoundKind.Footstep ? 0.5f
                            : 0.5f;
                soundPressure = Math.Max(soundPressure, ageF * distF * weight);
            }

            float targetThreat = bot.PrimaryTarget?.Threat ?? 0f;
            float visualThreat = (bot.PrimaryTarget != null && bot.PrimaryTarget.Source == IntelSource.Visual)
                ? bot.PrimaryTarget.Threat : 0f;

            float intensityTarget = MathUtils.Clamp01(Math.Max(visualThreat, soundPressure * 0.55f));
            bot.CombatIntensity = MathUtils.SmoothApproach(bot.CombatIntensity, intensityTarget, 3.5f, dt);

            float alertTarget = MathUtils.Clamp01(
                  targetThreat
                + soundPressure * 0.7f
                + ((now - bot.LastHurtAt) < 4f ? 0.5f : 0f));
            bot.Alert = MathUtils.SmoothApproach(bot.Alert, alertTarget, 1.6f, dt);

            float commitTarget =
                  bot.Personality.Aggression * 0.6f
                + (visualThreat > 0.3f ? 0.4f : 0f)
                - ((now - bot.LastHurtAt) < 2.5f ? 0.5f : 0f);
            commitTarget -= (1f - targetThreat) * (1f - bot.Personality.Aggression) * 0.3f;
            bot.Commit = MathUtils.SmoothApproach(bot.Commit, MathUtils.Clamp01(commitTarget), 2.0f, dt);

            float stealthTarget = bot.Personality.StealthBias * MathUtils.Clamp01(bot.Alert * 1.4f)
                                - bot.Personality.Aggression * 0.4f
                                - bot.CombatIntensity * 0.8f;
            bot.Stealth = MathUtils.SmoothApproach(bot.Stealth, MathUtils.Clamp01(stealthTarget), 2.5f, dt);

            // Curiosity climbs with heard sounds, drains when we're in combat.
            float curTarget = soundPressure * bot.Personality.Curiosity - bot.CombatIntensity;
            bot.Curiosity = MathUtils.SmoothApproach(bot.Curiosity, MathUtils.Clamp01(curTarget), 1.2f, dt);
        }

        // =====================================================================
        //  Phase
        // =====================================================================
        private static void UpdatePhase(ZeusBot bot, float now)
        {
            BotPhase next = bot.Phase;
            bool haveLive = bot.PrimaryTarget != null && bot.PrimaryTarget.Threat > 0.35f && bot.PrimaryTarget.Source == IntelSource.Visual;
            bool haveStale = bot.PrimaryTarget != null && bot.PrimaryTarget.Threat > 0.10f;
            bool recentlyKilled = (now - bot.LastKillAt) < 2.5f;
            bool recentlyHurt = (now - bot.LastHurtAt) < 2.0f;
            bool wantInvestigate = bot.Curiosity > 0.55f && bot.PrimaryTarget == null;

            if (haveLive && !recentlyKilled) next = BotPhase.Engage;
            else if (recentlyKilled || recentlyHurt) next = BotPhase.Reposition;
            else if (haveStale || bot.Alert > 0.45f) next = BotPhase.Probe;
            else if (wantInvestigate) next = BotPhase.Investigate;
            else next = BotPhase.Roam;

            if (bot.Phase == BotPhase.Engage && next != BotPhase.Engage && (now - bot.PhaseEnteredAt) < 0.4f)
                return;
            bot.TransitionTo(next, now);
        }

        // =====================================================================
        //  Pick a goal point on the map.
        // =====================================================================
        private static void UpdateGoal(ZeusBot bot, NavGraph graph, float now)
        {
            Vector me = bot.Pawn.AbsOrigin!;
            int myTeam = bot.Controller.TeamNum;
            int enemyTeam = myTeam == 2 ? 3 : 2;
            Vector? newGoal = null;

            switch (bot.Phase)
            {
                case BotPhase.Engage:
                    if (bot.PrimaryTarget != null)
                    {
                        Vector predicted = bot.PrimaryTarget.Predict(now);
                        Vector offset = OffsetForFanOut(bot);
                        newGoal = new Vector(predicted.X + offset.X, predicted.Y + offset.Y, predicted.Z);
                    }
                    break;
                case BotPhase.Probe:
                    if (bot.PrimaryTarget != null)
                    {
                        Vector predicted = bot.PrimaryTarget.Predict(now);
                        Vector toward = MathUtils.Normalize(predicted - me);
                        if (MathUtils.Length(toward) < 0.1f) toward = new Vector(1, 0, 0);
                        float standoff = 200f + bot.Personality.Patience * 140f;
                        float dist = (predicted - me).Length();
                        newGoal = dist > standoff
                            ? new Vector(predicted.X - toward.X * standoff, predicted.Y - toward.Y * standoff, predicted.Z)
                            : predicted;
                    }
                    break;
                case BotPhase.Reposition:
                    {
                        Vector anchor = bot.LastHurtFrom ?? bot.PrimaryTarget?.LastSeenPos ?? me;
                        Vector away = MathUtils.Normalize(me - anchor);
                        if (MathUtils.Length(away) < 0.1f) away = new Vector(1, 0, 0);
                        Vector lateral = MathUtils.RotateXY(away, (bot.Controller.Index % 2 == 0) ? 1.2f : -1.2f);
                        newGoal = new Vector(me.X + lateral.X * 280f, me.Y + lateral.Y * 280f, me.Z);
                    }
                    break;
                case BotPhase.Investigate:
                    if (bot.InvestigateTarget == null || now > bot.InvestigateUntil)
                    {
                        var loudest = LoudestEnemySound(bot, now);
                        if (loudest != null)
                        {
                            bot.InvestigateTarget = MathUtils.Snap(loudest.Position);
                            bot.InvestigateUntil = now + 6f;
                        }
                    }
                    newGoal = bot.InvestigateTarget;
                    break;
                case BotPhase.Roam:
                    if (!bot.HaveGoal
                        || now > bot.NextRepathAt
                        || (bot.FinalGoal - me).Length() < 140f)
                    {
                        newGoal = PickObjective(bot, enemyTeam, graph, now);
                        bot.NextRepathAt = now + 5f + (float)(new Random((int)(bot.Controller.Index + now * 100)).NextDouble() * 3.0);
                    }
                    break;
            }

            if (newGoal != null)
            {
                if (!bot.HaveGoal
                    || (newGoal - bot.FinalGoal).Length() > 220f
                    || bot.Path.Count == 0)
                {
                    bot.FinalGoal = newGoal;
                    bot.HaveGoal = true;
                    bot.ClearPath();
                }
            }
        }

        private static Vector OffsetForFanOut(ZeusBot bot)
        {
            float angle = bot.Controller.Index * 0.97f + bot.Personality.LaneOffset;
            float radius = 70f + bot.Personality.Aggression * 30f;
            return new Vector((float)Math.Cos(angle) * radius, (float)Math.Sin(angle) * radius, 0f);
        }

        private static SoundEvent? LoudestEnemySound(ZeusBot bot, float now)
        {
            SoundEvent? best = null;
            float bestScore = 0f;
            foreach (var ev in AudioSim.Events)
            {
                if (ev.Team == bot.Controller.TeamNum) continue;
                if (ev.EmitterId == bot.Controller.Index) continue;
                float age = now - ev.Time;
                if (age > 6f) continue;
                float score = ev.BaseLoudness * (1f - age / 6f);
                if (score > bestScore) { bestScore = score; best = ev; }
            }
            return best;
        }

        private static Vector PickObjective(ZeusBot bot, int enemyTeam, NavGraph graph, float now)
        {
            if (TacticalIntel.PlantedBomb != null && (now - TacticalIntel.PlantedBombTime) < 60f)
                return TacticalIntel.PlantedBomb;
            if (TacticalIntel.DroppedBomb != null && (now - TacticalIntel.DroppedBombTime) < 30f)
                return TacticalIntel.DroppedBomb;

            // Heaviest stale-intel cell otherwise.
            EnemyTrack? bestStale = null;
            foreach (var t in bot.Tracks.Values)
                if (t.Confidence > 0.2f && (bestStale == null || t.Confidence > bestStale.Confidence))
                    bestStale = t;
            if (bestStale != null) return bestStale.Predict(now);

            // Strategic hot cell - somewhere fights have happened.
            NavCell? hottest = null;
            float bestScore = 0f;
            foreach (var c in graph.Cells.Values)
            {
                if (c.HotSpot < 0.5f) continue;
                float distScore = 1f / (1f + (c.Center - bot.Pawn.AbsOrigin!).Length() / 1500f);
                float score = c.HotSpot * (0.4f + 0.6f * distScore);
                if (score > bestScore) { bestScore = score; hottest = c; }
            }
            if (hottest != null) return hottest.Center;

            // Fallback - midpoint toward enemy spawn.
            Vector center = TacticalIntel.MapCenter();
            if (TacticalIntel.TeamSpawnCenter.TryGetValue(enemyTeam, out var enemySpawn))
                return MathUtils.Lerp(center, enemySpawn, 0.35f);
            return center;
        }

        // =====================================================================
        //  Path generation & maintenance
        // =====================================================================
        private static void UpdatePath(ZeusBot bot, NavGraph graph, float now, float dt)
        {
            if (!bot.HaveGoal) return;
            Vector me = bot.Pawn.AbsOrigin!;

            // Build a fresh path if needed.
            bool needPath = bot.Path.Count == 0
                || (bot.PathIndex >= bot.Path.Count)
                || now > bot.NextRepathAt;

            if (needPath)
            {
                var path = AStar.FindPath(graph, me, bot.FinalGoal);
                if (path != null && path.Count > 0)
                {
                    bot.Path = path;
                    bot.PathIndex = 0;
                    // Add the final goal as the final waypoint, in case A*
                    // ended at a centroid slightly off the goal.
                    if ((bot.Path[bot.Path.Count - 1] - bot.FinalGoal).Length() > 96f)
                        bot.Path.Add(bot.FinalGoal);
                    bot.NextRepathAt = now + 3.0f;
                }
                else
                {
                    // Graph doesn't know yet - we'll fall back to direct
                    // steering toward the goal in the motor layer.
                    bot.Path.Clear();
                    bot.NextRepathAt = now + 1.0f;
                }
            }

            // Advance to next waypoint when close enough.
            while (bot.PathIndex < bot.Path.Count)
            {
                var wp = bot.Path[bot.PathIndex];
                float d2 = (wp.X - me.X) * (wp.X - me.X) + (wp.Y - me.Y) * (wp.Y - me.Y);
                if (d2 < 80f * 80f) bot.PathIndex++;
                else break;
            }
        }

        // =====================================================================
        //  Motor - turn the current path + phase into per-tick movement.
        // =====================================================================
        private static void UpdateMotor(ZeusBot bot, NavGraph graph, float now, float dt)
        {
            var pawn = bot.Pawn;
            Vector me = pawn.AbsOrigin!;
            bool grounded = ((uint)pawn.Flags & 1) != 0;

            // Stuck detection (used by both path-followed roam and combat).
            Vector vel = pawn.AbsVelocity ?? new Vector(0, 0, 0);
            float horizSpeed = MathUtils.LengthXY(vel);
            bool wantingToMove = bot.HaveGoal && (bot.FinalGoal - me).Length() > 96f;
            if (wantingToMove && horizSpeed < 50f) bot.StuckTimer += dt;
            else bot.StuckTimer = Math.Max(0f, bot.StuckTimer - dt * 2.5f);

            // Pick the steering target this tick.
            Vector steer = bot.FinalGoal;
            if (bot.Path.Count > 0 && bot.PathIndex < bot.Path.Count) steer = bot.Path[bot.PathIndex];

            Vector heading;
            if (wantingToMove)
            {
                Vector d = steer - me; d.Z = 0;
                heading = MathUtils.Normalize(d);
            }
            else heading = new Vector(0, 0, 0);

            // If we've been stuck a moment, try a randomized dodge yaw and a
            // little jump. Persisting the dodge yaw briefly avoids the bot
            // wiggling rapidly back and forth.
            if (bot.StuckTimer > 0.6f)
            {
                if (now > bot.StuckDodgeUntil)
                {
                    Random r = new Random((int)(bot.Controller.Index * 991 + now * 100));
                    bot.StuckDodgeYaw = (float)((r.NextDouble() * 2 - 1) * Math.PI * 0.55);
                    bot.StuckDodgeUntil = now + 0.5f;
                    if (grounded && bot.JumpCooldown < now)
                    {
                        bot.PendingButtons |= (ulong)PlayerButtons.Jump;
                        bot.JumpCooldown = now + 0.45f;
                    }
                }
                heading = MathUtils.RotateXY(heading, bot.StuckDodgeYaw);
                if (bot.StuckTimer > 2.0f)
                {
                    // Force a replan and pull a fresh path next tick.
                    bot.ClearPath();
                    bot.NextRepathAt = 0f;
                }
            }

            // Phase-specific shaping. Combat overrides direct heading with
            // its own movement choices.
            switch (bot.Phase)
            {
                case BotPhase.Roam:
                    ShapeRoam(bot, ref heading, now, dt); break;
                case BotPhase.Investigate:
                    ShapeRoam(bot, ref heading, now, dt);
                    bot.WantWalk = true; // sneak when investigating
                    bot.DesiredSpeed = Math.Min(bot.DesiredSpeed, 150f);
                    break;
                case BotPhase.Probe:
                    ShapeProbe(bot, ref heading, grounded, now, dt); break;
                case BotPhase.Engage:
                    ShapeEngage(bot, ref heading, grounded, now, dt); break;
                case BotPhase.Reposition:
                    ShapeReposition(bot, ref heading, now, dt); break;
            }

            // Micro-pauses: occasionally a roamer will stop briefly to look
            // around (very human). Only when not in combat.
            if (bot.Phase == BotPhase.Roam || bot.Phase == BotPhase.Investigate)
            {
                float pauseChance = bot.Personality.MicroPauseBias * 0.35f;
                if (bot.NextHeadSweepAt < now && bot.HeadSweepUntil < now)
                {
                    if (new Random((int)(bot.Controller.Index + now * 10)).NextDouble() < pauseChance * dt * 4f)
                    {
                        bot.HeadSweepUntil = now + 0.6f + bot.Personality.Patience * 0.7f;
                        bot.NextHeadSweepAt = now + 5f;
                        // pick a hot cell to glance toward
                        bot.HeadSweepFocus = PickHeadSweepFocus(bot, graph);
                    }
                }
                if (bot.HeadSweepUntil > now)
                {
                    bot.DesiredSpeed = Math.Min(bot.DesiredSpeed, 60f);
                    bot.WantWalk = true;
                }
            }

            bot.DesiredMove = heading;

            // Small per-tick humanization noise on the issued speed so we
            // don't move at exactly the same rate every tick.
            bot.SpeedJitter = MathUtils.Lerp(bot.SpeedJitter, (float)(new Random((int)(bot.Controller.Index * 13 + now * 11)).NextDouble() - 0.5) * 16f, MathUtils.Clamp01(dt * 4f));
        }

        private static Vector? PickHeadSweepFocus(ZeusBot bot, NavGraph graph)
        {
            // Pick a known hot cell within reasonable distance to glance at.
            Vector me = bot.Pawn.AbsOrigin!;
            NavCell? pick = null;
            float bestScore = 0f;
            int considered = 0;
            foreach (var c in graph.Cells.Values)
            {
                if (c.HotSpot < 0.4f) continue;
                float dist = (c.Center - me).Length();
                if (dist < 200f || dist > 1500f) continue;
                if (++considered > 80) break;
                float s = c.HotSpot / (1f + dist / 800f);
                if (s > bestScore) { bestScore = s; pick = c; }
            }
            return pick?.Center;
        }

        private static void ShapeRoam(ZeusBot bot, ref Vector heading, float now, float dt)
        {
            float baseSpeed = MathUtils.Lerp(250f, 130f, bot.Stealth);
            bot.DesiredSpeed = baseSpeed + bot.SpeedJitter;
            bot.WantWalk = bot.Stealth > 0.55f;
            float wander = (float)Math.Sin(now * 0.7f + bot.NoisePhase) * 0.10f;
            heading = MathUtils.RotateXY(heading, wander);
        }

        private static void ShapeProbe(ZeusBot bot, ref Vector heading, bool grounded, float now, float dt)
        {
            float speed = MathUtils.Lerp(160f, 220f, bot.Commit);
            Vector me = bot.Pawn.AbsOrigin!;
            float distToGoal = (bot.FinalGoal - me).Length();

            if (distToGoal < 100f)
            {
                if (!bot.HoldingAngle)
                {
                    bot.HoldingAngle = true;
                    bot.HoldUntil = now + 1.2f + bot.Personality.Patience * 2.5f;
                    bot.HoldPosition = MathUtils.Snap(me);
                    bot.HoldFacing = bot.PrimaryTarget != null
                        ? MathUtils.Normalize(bot.PrimaryTarget.Predict(now) - me)
                        : heading;
                }
            }
            if (bot.HoldingAngle && now < bot.HoldUntil)
            {
                bot.DesiredSpeed = 0f;
                heading = new Vector(0, 0, 0);
                return;
            }
            else if (bot.HoldingAngle && now >= bot.HoldUntil)
            {
                bot.HoldingAngle = false;
            }

            bot.DesiredSpeed = speed + bot.SpeedJitter;
            float wander = (float)Math.Sin(now * 1.2f + bot.NoisePhase) * 0.2f * bot.Personality.PeekJitter;
            heading = MathUtils.RotateXY(heading, wander);
            bot.WantWalk = bot.Stealth > 0.4f && bot.CombatIntensity < 0.25f;
        }

        private static void ShapeEngage(ZeusBot bot, ref Vector heading, bool grounded, float now, float dt)
        {
            float micro = MathUtils.Clamp01(0.5f * bot.CombatIntensity + 0.5f * bot.Commit);
            float speed = MathUtils.Lerp(210f, 260f, micro);

            Vector me = bot.Pawn.AbsOrigin!;
            Vector targetPos = bot.PrimaryTarget?.Predict(now) ?? (me + heading * 200f);
            float dist = (targetPos - me).Length();

            if (now > bot.NextMicroChangeAt)
            {
                Random r = new Random((int)(bot.Controller.Index * 7 + now * 100));
                bot.MicroPattern = r.Next(0, 4);
                bot.StrafeDir = r.NextDouble() > 0.5 ? 1f : -1f;
                bot.NextMicroChangeAt = now + MathUtils.Lerp(0.85f, 0.35f, micro);
            }

            Vector toTarget = MathUtils.Normalize(targetPos - me);
            Vector right = new Vector(-toTarget.Y, toTarget.X, 0f);

            float idealRange = 150f;
            float rangeError = dist - idealRange;
            float forwardWeight = MathUtils.Clamp(rangeError / 200f, -0.4f, 1f);
            float lateral = 0f;

            switch (bot.MicroPattern)
            {
                case 0:
                    lateral = bot.StrafeDir * MathUtils.Lerp(0.4f, 1.0f, micro); break;
                case 1:
                    forwardWeight = MathUtils.Clamp(forwardWeight + 0.4f, -0.4f, 1f);
                    lateral = bot.StrafeDir * 0.6f;
                    if (grounded && bot.JumpCooldown < now && bot.Personality.JumpBias > 0.45f && micro > 0.4f)
                    {
                        bot.PendingButtons |= (ulong)PlayerButtons.Jump;
                        bot.JumpCooldown = now + MathUtils.Lerp(0.9f, 0.4f, micro);
                    }
                    break;
                case 2:
                    lateral = bot.StrafeDir * 0.5f;
                    if (bot.Personality.CrouchBias > 0.5f && dist < 260f)
                        bot.DuckUntil = Math.Max(bot.DuckUntil, now + 0.35f);
                    break;
                case 3:
                    lateral = bot.StrafeDir * 1.1f;
                    forwardWeight *= 0.4f; break;
            }

            if (dist < idealRange) forwardWeight = MathUtils.Clamp(forwardWeight, -0.3f, 0.1f);

            heading = MathUtils.Normalize(toTarget * forwardWeight + right * lateral);
            bot.DesiredSpeed = speed + bot.SpeedJitter;
            if (bot.DuckUntil > now) bot.PendingButtons |= (ulong)PlayerButtons.Duck;
        }

        private static void ShapeReposition(ZeusBot bot, ref Vector heading, float now, float dt)
        {
            bool recentlyHurt = (now - bot.LastHurtAt) < 1.5f;
            bot.DesiredSpeed = (recentlyHurt ? 200f : 245f) + bot.SpeedJitter;
            bot.WantWalk = false;
        }

        // =====================================================================
        //  Aim - target tracking with reaction, noise, sweep, pre-aim.
        // =====================================================================
        private static void UpdateAim(ZeusBot bot, NavGraph graph, float now, float dt)
        {
            var pawn = bot.Pawn;
            if (pawn.EyeAngles == null) return;

            float curYaw = pawn.EyeAngles.Y;
            float curPitch = pawn.EyeAngles.X;
            QAngle desired;

            if (bot.PrimaryTarget != null
                && bot.PrimaryTarget.Source == IntelSource.Visual
                && bot.ReactionRemaining <= 0f)
            {
                desired = AimAtPosition(bot, bot.PrimaryTarget.Predict(now), now);
            }
            else if (bot.PrimaryTarget != null && bot.PrimaryTarget.ReactionPending(bot))
            {
                desired = bot.AimGoal;
            }
            else if (bot.HeadSweepFocus != null && bot.HeadSweepUntil > now)
            {
                desired = AimAtPosition(bot, bot.HeadSweepFocus, now);
            }
            else if (bot.PrimaryTarget != null)
            {
                // Audio / stale intel: aim toward last-known with extra wide
                // noise so we *pre-aim* without snapping to a perfect target.
                desired = AimAtPosition(bot, bot.PrimaryTarget.LastSeenPos, now, extraNoise: 4f);
            }
            else if (bot.HoldFacing != null && bot.HoldingAngle)
            {
                Vector f = bot.HoldFacing;
                float yaw = (float)(Math.Atan2(f.Y, f.X) * MathUtils.Rad2Deg);
                desired = new QAngle(0f, yaw, 0f);
            }
            else if (bot.DesiredSpeed > 1f)
            {
                Vector m = bot.DesiredMove;
                float yaw = (float)(Math.Atan2(m.Y, m.X) * MathUtils.Rad2Deg);
                float pitch = (float)Math.Sin(now * 0.4f + bot.NoisePhase) * 4f;
                desired = new QAngle(pitch, yaw, 0f);
            }
            else
            {
                // Idle - gentle sweep across known hot cells.
                if (bot.HeadSweepFocus == null || now > bot.NextHeadSweepAt)
                {
                    bot.HeadSweepFocus = PickHeadSweepFocus(bot, graph);
                    bot.NextHeadSweepAt = now + 3f + bot.Personality.Patience * 3f;
                }
                desired = bot.HeadSweepFocus != null ? AimAtPosition(bot, bot.HeadSweepFocus, now) : bot.AimGoal;
            }

            bot.AimGoal = desired;

            float skillBoost = 0.5f + bot.Personality.Skill * 0.5f;
            float turnRate = MathUtils.Lerp(180f, 540f, bot.CombatIntensity) * skillBoost;
            float maxStep = turnRate * dt;

            float yawDiff = MathUtils.NormalizeAngle(desired.Y - curYaw);
            float pitchDiff = MathUtils.NormalizeAngle(desired.X - curPitch);
            float newYaw = curYaw + MathUtils.Clamp(yawDiff, -maxStep, maxStep);
            float newPitch = MathUtils.Clamp(curPitch + MathUtils.Clamp(pitchDiff, -maxStep, maxStep), -89f, 89f);
            bot.AimGoal = new QAngle(newPitch, newYaw, 0f);
        }

        private static QAngle AimAtPosition(ZeusBot bot, Vector tPos, float now, float extraNoise = 0f)
        {
            var pawn = bot.Pawn;
            Vector me = pawn.AbsOrigin!;
            float eyeHeight = ((uint)pawn.Flags & 2) != 0 ? 46f : 64f;
            float myEyeZ = me.Z + eyeHeight;
            float aimZ = tPos.Z + 52f;

            float noiseAmp = bot.Personality.AimNoise * (1f - bot.Personality.Skill * 0.6f) + extraNoise;
            noiseAmp *= MathUtils.Lerp(0.6f, 1.4f, bot.CombatIntensity);
            float n1 = (float)Math.Sin(now * 4.1f + bot.NoisePhase);
            float n2 = (float)Math.Cos(now * 3.3f + bot.NoisePhase * 1.7f);
            float n3 = (float)Math.Sin(now * 5.0f + bot.NoisePhase * 0.5f);

            float dx = tPos.X - me.X, dy = tPos.Y - me.Y, dz = aimZ - myEyeZ;
            float horiz = (float)Math.Sqrt(dx * dx + dy * dy);
            float yaw = horiz > 4f ? (float)(Math.Atan2(dy, dx) * MathUtils.Rad2Deg) : pawn.EyeAngles!.Y;
            float pitch = (float)(Math.Atan2(-dz, Math.Max(horiz, 1f)) * MathUtils.Rad2Deg);
            yaw += n1 * noiseAmp + n3 * 0.5f;
            pitch += n2 * noiseAmp * 0.6f;
            pitch = MathUtils.Clamp(pitch, -89f, 89f);
            return new QAngle(pitch, yaw, 0f);
        }

        // =====================================================================
        //  Weapon
        // =====================================================================
        private static void UpdateWeapon(ZeusBot bot, float now)
        {
            if (!bot.HasWeapon("taser")) return;
            string desired = "knife";
            if (bot.PrimaryTarget != null)
            {
                float dist = (bot.Pawn.AbsOrigin! - bot.PrimaryTarget.Predict(now)).Length();
                if (bot.CombatIntensity > 0.45f || dist < 320f || bot.HoldingAngle) desired = "taser";
            }
            if (desired == "knife" && bot.Alert > 0.7f && bot.HoldingAngle) desired = "taser";

            if (desired != bot.DesiredWeapon)
            {
                bot.DesiredWeapon = desired;
                bot.DesiredWeaponSince = now;
            }

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

        // =====================================================================
        //  Fire
        // =====================================================================
        private static void UpdateFire(ZeusBot bot, float now)
        {
            if (bot.FireCooldown > now) return;
            if (bot.WeaponSwitchCooldown > now) return;
            if (!bot.HoldingWeapon("taser")) return;
            if (bot.PrimaryTarget == null) return;
            if (bot.PrimaryTarget.Source != IntelSource.Visual) return;
            if (!bot.PrimaryTarget.CurrentlyVisible) return;
            if (bot.ReactionRemaining > 0f) return;

            Vector me = bot.Pawn.AbsOrigin!;
            Vector tPos = bot.PrimaryTarget.LastSeenPos;
            float dist = (me - tPos).Length();
            if (dist > 240f) return;

            float eyeHeight = ((uint)bot.Pawn.Flags & 2) != 0 ? 46f : 64f;
            Vector head = new Vector(me.X, me.Y, me.Z + eyeHeight);
            Vector toTarget = MathUtils.Normalize(new Vector(tPos.X, tPos.Y, tPos.Z + 40f) - head);
            Vector fwd = MathUtils.GetForwardVector(bot.Pawn.EyeAngles!);
            float dot = MathUtils.Dot(fwd, toTarget);

            float needed = dist < 120f ? 0.94f : (dist < 180f ? 0.97f : 0.985f);
            if (dot < needed) return;

            bot.PendingButtons |= (ulong)PlayerButtons.Attack;
            bot.FireCooldown = now + 0.45f;
            // Firing the zeus emits a loud sound.
            AudioSim.Emit(SoundKind.Gunfire, me, bot.Controller.TeamNum, bot.Controller.Index, now, 0.55f);
        }

        // =====================================================================
        //  Footstep emission (so other bots can hear running players).
        // =====================================================================
        private static void EmitFootsteps(ZeusBot bot, float now)
        {
            if (bot.NextFootstepAt > now) return;
            float horizSpeed = MathUtils.LengthXY(bot.Pawn.AbsVelocity ?? new Vector(0, 0, 0));
            if (horizSpeed < 50f) { bot.NextFootstepAt = now + 0.2f; return; }
            if (bot.WantWalk) { bot.NextFootstepAt = now + 0.55f; return; } // walking - silent
            // ~2 footsteps/second when running
            bot.NextFootstepAt = now + 0.45f;
            AudioSim.Emit(SoundKind.Footstep, bot.Pawn.AbsOrigin!, bot.Controller.TeamNum, bot.Controller.Index, now, 0.5f);
        }
    }

    // Helper - small extension on EnemyTrack for cleaner aim flow.
    public static class EnemyTrackExtensions
    {
        public static bool ReactionPending(this EnemyTrack t, ZeusBot bot) =>
            t.Source == IntelSource.Visual && bot.ReactionRemaining > 0f;
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
        public override string ModuleName => "Zeus Bot AI (Self-Learning Nav)";
        public override string ModuleVersion => "5.0.0";
        public override string ModuleAuthor => "ZeusBotAI";

        private bool botsEnabled = true;
        private ZeusBotConfig _config = new ZeusBotConfig();

        private readonly Dictionary<uint, ZeusBot> bots = new();
        private NavGraph nav = new NavGraph();
        private float nextNavSaveAt;
        private string currentMap = "";
        private int tickCounter;

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
            RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
            RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
            RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
            RegisterEventHandler<EventWeaponReload>(OnWeaponReload);
            RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
            RegisterEventHandler<EventBombDefused>(OnBombDefused);
            RegisterEventHandler<EventBombPickup>(OnBombPickup);
            RegisterEventHandler<EventBombDropped>(OnBombDropped);
            RegisterEventHandler<EventBombExploded>(OnBombExploded);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventPlayerFootstep>(OnPlayerFootstep);

            AddCommandListener("say", OnPlayerChat);
            AddCommandListener("say_team", OnPlayerChat);

            // If we hot-reload mid-map, jumpstart the graph for whatever map
            // we're already on.
            Server.NextFrame(() =>
            {
                string map = Server.MapName ?? "";
                if (!string.IsNullOrEmpty(map)) BeginMap(map);
                foreach (var p in Utilities.GetPlayers())
                    if (p.IsBot) EnsureBotName(p);
            });

            AddCommand("zeusbots", "Enable Zeus Bots", (p, _) => { if (CheckCommandPermission(p)) { botsEnabled = true; Server.PrintToChatAll("Zeus Bots Enabled via Console Command"); } });
            AddCommand("normalbots", "Disable Zeus Bots", (p, _) => { if (CheckCommandPermission(p)) { botsEnabled = false; Server.PrintToChatAll("Zeus Bots Disabled via Console Command"); } });
            AddCommand("removeallbots", "Kick all bots", (p, _) => { if (CheckCommandPermission(p)) { Server.ExecuteCommand("bot_kick"); Server.PrintToChatAll("All bots kicked via Console Command"); } });
            AddCommand("addtbot", "Add T Bot", (p, _) => { if (CheckCommandPermission(p)) { Server.ExecuteCommand("bot_add_t"); Server.PrintToChatAll("Terrorist Bot Added via Console Command"); } });
            AddCommand("addctbot", "Add CT Bot", (p, _) => { if (CheckCommandPermission(p)) { Server.ExecuteCommand("bot_add_ct"); Server.PrintToChatAll("CT Bot Added via Console Command"); } });
            AddCommand("zeusbotsnav", "Show learned nav stats", (p, _) => { if (CheckCommandPermission(p)) { Server.PrintToChatAll($" \x0C[ZeusBots]\x01 nav: {nav.Cells.Count} cells learnt on \x0C{currentMap}\x01"); } });

            Console.WriteLine("[Zeus Bot] v5 (self-learning nav) loaded.");
        }

        public override void Unload(bool hotReload)
        {
            SaveNav();
            RemoveListener<Listeners.OnTick>(OnTick);
            RemoveListener<Listeners.OnMapStart>(OnMapStart);
            RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
            bots.Clear();
            assignedNames.Clear();
        }

        // -------------------- nav persistence --------------------

        private string NavPathFor(string map)
        {
            string dir = Path.Combine(ModuleDirectory, "data");
            try { Directory.CreateDirectory(dir); } catch { }
            string safe = string.Concat(map.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
            return Path.Combine(dir, $"nav_{safe}.json");
        }

        private void BeginMap(string map)
        {
            currentMap = map;
            nav = new NavGraph { MapName = map };
            string path = NavPathFor(map);
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var dto = JsonSerializer.Deserialize<NavGraphDto>(json);
                    if (dto != null && dto.Cells.Count > 0)
                    {
                        nav = NavGraph.FromDto(dto);
                        nav.MapName = map;
                        Console.WriteLine($"[ZeusBots] Loaded nav for {map}: {nav.Cells.Count} cells.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ZeusBots] Failed to load nav: {ex.Message}");
                }
            }
        }

        private void SaveNav()
        {
            if (string.IsNullOrEmpty(currentMap) || nav.Cells.Count == 0) return;
            if (!nav.Dirty) return;
            try
            {
                var dto = nav.ToDto();
                string json = JsonSerializer.Serialize(dto);
                File.WriteAllText(NavPathFor(currentMap), json);
                nav.Dirty = false;
                Console.WriteLine($"[ZeusBots] Saved nav for {currentMap}: {nav.Cells.Count} cells.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZeusBots] Nav save error: {ex.Message}");
            }
        }

        // -------------------- config --------------------

        private void LoadConfig()
        {
            string configPath = Path.Combine(ModuleDirectory, "../../configs/plugins/ZeusBotAI/config.json");
            if (!File.Exists(configPath)) configPath = Path.Combine(ModuleDirectory, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<ZeusBotConfig>(File.ReadAllText(configPath));
                    if (cfg != null) _config = cfg;
                }
                catch (Exception ex) { Console.WriteLine($"[ZeusBotAI] Error loading config: {ex.Message}"); }
            }
        }

        private void SaveConfig()
        {
            string configPath = Path.Combine(ModuleDirectory, "../../configs/plugins/ZeusBotAI/config.json");
            if (!File.Exists(configPath)) configPath = Path.Combine(ModuleDirectory, "config.json");
            try
            {
                File.WriteAllText(configPath, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
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
                player.PrintToChat(" \x0C!zeusbotsnav\x01 - Show learned nav stats");
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
            else if (Eq(text, "zeusbotsnav") || Eq(text, "!zeusbotsnav"))
            { if (CheckCommandPermission(player)) { player.PrintToChat($" \x0C[ZeusBots]\x01 nav: {nav.Cells.Count} cells learnt on \x0C{currentMap}\x01"); } }
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
                assignedNames[bot.Index] = avail.Count > 0 ? avail[rnd.Next(avail.Count)] : BotNames[rnd.Next(BotNames.Length)];
            }
            string desired = assignedNames[bot.Index];
            if (bot.PlayerName != desired)
            {
                bot.PlayerName = desired;
                Utilities.SetStateChanged(bot, "CBasePlayerController", "m_iszPlayerName");
            }
        }

        // -------------------- map events --------------------

        private void OnMapStart(string mapName)
        {
            SaveNav(); // save anything we had pending from the previous map
            bots.Clear();
            TacticalIntel.Reset();
            TacticalIntel.TeamSpawns.Clear();
            TacticalIntel.TeamSpawnCenter.Clear();
            AudioSim.Events.Clear();
            BeginMap(mapName);
        }

        private void OnMapEnd()
        {
            SaveNav();
        }

        private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            AudioSim.Events.Clear();
            TacticalIntel.Reset();
            foreach (var b in bots.Values)
            {
                b.Tracks.Clear();
                b.PrimaryTarget = null;
                b.Alert = 0f;
                b.Commit = 0f;
                b.CombatIntensity = 0f;
                b.HoldingAngle = false;
                b.ClearPath();
                b.HaveGoal = false;
            }
            return HookResult.Continue;
        }

        // -------------------- player events --------------------

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
            if (@event.Userid != null)
            {
                if (@event.Userid.IsBot && assignedNames.ContainsKey(@event.Userid.Index))
                    assignedNames.Remove(@event.Userid.Index);
                nav.ForgetActor(@event.Userid.Index);
            }
            return HookResult.Continue;
        }

        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            var controller = @event.Userid;
            if (controller == null || !controller.IsValid) return HookResult.Continue;

            // Spawn location feeds both spawn-centroid intel and the nav graph
            Server.NextFrame(() =>
            {
                if (!controller.IsValid) return;
                var pawn = controller.PlayerPawn.Value;
                if (pawn != null && pawn.IsValid && pawn.AbsOrigin != null)
                {
                    TacticalIntel.RegisterSpawn(controller.TeamNum, pawn.AbsOrigin);
                    nav.ObservePlayer(controller.Index, pawn.AbsOrigin, Server.CurrentTime);
                }
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
                    AudioSim.Emit(SoundKind.Death, vPawn.AbsOrigin, victim.TeamNum, victim.Index, Server.CurrentTime, 0.8f);
                    nav.RecordHotSpot(vPawn.AbsOrigin, 1.5f);
                }
                nav.ForgetActor(victim.Index);
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
            float loud = 1f;
            string wep = @event.Weapon ?? string.Empty;
            if (wep.Contains("knife")) loud = 0.25f;
            else if (wep.Contains("taser")) loud = 0.55f;
            AudioSim.Emit(SoundKind.Gunfire, pawn.AbsOrigin, shooter.TeamNum, shooter.Index, Server.CurrentTime, loud);
            nav.RecordHotSpot(pawn.AbsOrigin, 0.4f);
            return HookResult.Continue;
        }

        private HookResult OnWeaponReload(EventWeaponReload @event, GameEventInfo info)
        {
            var p = @event.Userid;
            if (p == null || !p.IsValid) return HookResult.Continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null || pawn.AbsOrigin == null) return HookResult.Continue;
            AudioSim.Emit(SoundKind.Reload, pawn.AbsOrigin, p.TeamNum, p.Index, Server.CurrentTime, 0.35f);
            return HookResult.Continue;
        }

        private HookResult OnPlayerFootstep(EventPlayerFootstep @event, GameEventInfo info)
        {
            // The engine actually fires this for humans too - free intel.
            var p = @event.Userid;
            if (p == null || !p.IsValid) return HookResult.Continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn == null || pawn.AbsOrigin == null) return HookResult.Continue;
            AudioSim.Emit(SoundKind.Footstep, pawn.AbsOrigin, p.TeamNum, p.Index, Server.CurrentTime, 0.5f);
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
                AudioSim.Emit(SoundKind.Bomb, pawn.AbsOrigin, p.TeamNum, p.Index, Server.CurrentTime, 0.9f);
                nav.RecordHotSpot(pawn.AbsOrigin, 4f);
            }
            return HookResult.Continue;
        }

        private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
        {
            TacticalIntel.PlantedBomb = null;
            return HookResult.Continue;
        }

        private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
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
            float now = Server.CurrentTime;
            float dt = Server.TickInterval;
            tickCounter++;

            AudioSim.Prune(now);
            nav.DecayHotspots(dt);

            var players = Utilities.GetPlayers();
            var alive = players.Where(p => p != null && p.IsValid && p.PawnIsAlive).ToList();

            // 1) Learn graph from every alive player every few ticks.
            //    This is what makes the bots "know" the map - and humans
            //    teach it for us.
            if (tickCounter % 4 == 0)
            {
                foreach (var p in alive)
                {
                    var pawn = p.PlayerPawn.Value;
                    if (pawn?.AbsOrigin == null) continue;
                    nav.ObservePlayer(p.Index, pawn.AbsOrigin, now);
                }
            }

            // 2) Periodic save of learnt graph.
            if (now > nextNavSaveAt)
            {
                SaveNav();
                nextNavSaveAt = now + 60f;
            }

            if (!botsEnabled) return;

            var aliveBots = alive.Where(p => p.IsBot).ToList();
            var stale = bots.Keys.Where(k => !aliveBots.Any(b => b.Index == k)).ToList();
            foreach (var k in stale) bots.Remove(k);

            foreach (var bot in aliveBots)
            {
                if (!bots.TryGetValue(bot.Index, out var z))
                {
                    z = new ZeusBot(bot, now);
                    bots[bot.Index] = z;
                }
                Brain.Tick(z, alive, nav, now, dt);
                InjectMotor(z);
            }
        }

        // ---------------------------------------------------------------------
        //  Push the bot's per-tick desired state into the engine. We always
        //  control the body - we never hand off to native CS2 bot AI while
        //  the plugin is enabled.
        // ---------------------------------------------------------------------
        private void InjectMotor(ZeusBot z)
        {
            var pawn = z.Pawn;
            if (pawn?.MovementServices == null || !pawn.IsValid) return;

            QAngle outAngles = z.AimGoal;
            Vector vel = pawn.AbsVelocity ?? new Vector(0, 0, 0);
            Vector injected = new Vector(vel.X, vel.Y, vel.Z);

            float speed = z.DesiredSpeed;
            if (z.WantWalk && speed > 130f) speed = 130f;

            if (speed > 1f && z.DesiredMove.Length() > 0.1f)
            {
                Vector dir = MathUtils.Normalize(z.DesiredMove);
                injected.X = dir.X * speed;
                injected.Y = dir.Y * speed;
            }
            else
            {
                injected.X *= 0.5f;
                injected.Y *= 0.5f;
            }

            bool grounded = ((uint)pawn.Flags & 1) != 0;
            if (grounded) injected.Z = -15f;

            if ((z.PendingButtons & (ulong)PlayerButtons.Jump) != 0)
            {
                if (grounded) injected.Z = 300f;
                z.PendingButtons &= ~((ulong)PlayerButtons.Jump);
            }

            pawn.MovementServices.Buttons.ButtonStates[0] |= z.PendingButtons;

            QAngle bodyAngles = new QAngle(0f, outAngles.Y, outAngles.Z);
            pawn.Teleport(null, bodyAngles, injected);

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

