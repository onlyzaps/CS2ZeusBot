# ⚡ ZeusBotAI

**Aggressive GOAP-Driven Bot Intelligence for Counter-Strike 2**

Welcome to the **ZeusBotAI** core documentation. This CounterStrikeSharp plugin implements a custom Goal-Oriented Action Planning (GOAP) architecture to transform standard CS2 bots into terrifying, highly aggressive melee and taser-wielding hunters. 

Writing custom AI that fights CS2's native bot behavior is notoriously tricky. To solve this, ZeusBotAI uses a highly optimized **hybrid approach**: it allows the native CS2 AI to handle basic map navigation (navmesh) and only hijacks their motor controls the exact moment they enter active combat.

---

## 🧠 Architecture Breakdown

Below is a deep dive into the bot's brain, architecture, and engine hooks.

### 1. GOAP Core (`GoapAction` & `GoapGoal`)
At the foundation of the AI is a streamlined GOAP framework designed specifically for high-performance server ticks. 
* **`GoapAction`**: Defines what the bot can physically do (equip weapons, sprint, dodge). It includes methods to validate prerequisites (`CheckContextPrecondition`), execute logic per-tick (`Execute`), and determine completion (`IsDone`).
* **`GoapGoal`**: Defines what the bot *wants* to do (e.g., stay alive, kill the player). Priority is calculated dynamically every frame.

### 2. Fact & Memory Architecture
Bots don't just react; they remember. The memory system is broken down into three main components:
* **`Fact` & `WorkingMemory`**: This acts as the bot's short-term memory. Bots track the `LastKnownPosition` of players. If a target breaks line-of-sight, the bot's "Confidence" in that memory decays rapidly over time. If confidence hits zero (usually after a few seconds of hiding), the bot forgets the player exists.
* **`BotBlackboard`**: The central nervous system of the bot. It stores the bot's immediate physical intentions for the current server frame—desired movement direction, speed, aim angles (pitch/yaw), bitmasks for buttons (Jump/Duck), and internal combat cooldowns.

### 3. The Sensory System (`UpdateSensor`)
The `BotAgent` binds the physical CS2 bot (`CCSPlayerPawn`) to the GOAP brain, but `UpdateSensor` acts as the bot's eyes.
* **Anti-Wall-Staring:** To prevent bots from cross-map tracking players through walls, the sensor evaluates targets based on strict physical constraints. The target must be close (`< 750f`), on a similar elevation (`zDiff < 350f`), within the bot's FOV, and natively "spotted" by the CS2 engine. 
* **The Handoff Protocol:** If a player is spotted, the `ThreatLevel` spikes to `3000`, triggering the GOAP engine to forcefully take over the bot's physics. If the player hides, the threat drops to `10`, and control is seamlessly handed back to CS2's native pathfinding.

### 4. Dynamic Action System
This is where the bot's personality shines. The actions dictate highly aggressive, unpredictable combat loops:
* **Passive Traversal (`ActionTraverseMap`)**: When no threats are active, the bot sets its `DesiredSpeed` to `0f`. This acts as a signal to our physics injector to stand down, letting the native CS2 navmesh handle routing out of spawn.
* **Tactical Weapon Swapping (`ActionEquipZeus` / `ActionEquipKnife`)**: Bots actively manage their inventory. If a target is far, they pull out their knife to maximize sprint speed. As they cross the 450-unit threshold, they seamlessly hot-swap to prime the Zeus.
* **Unpredictable Combat Movement (`ActionApproachTarget` & `ActionEngageZeus`)**: Bots are programmed to be genuinely hard to hit. 
  * **Dynamic Fanning**: Using trigonometric offsets (`Math.Cos` and the bot's entity `Index`), bots fan out into a circle as they approach, preventing them from clumping in a single-file line.
  * **Evasive Maneuvers**: The bots utilize randomized state machines to actively B-hop, crouch-slide, micro-peek (ADAD strafe), and serpentine while rushing. 

### 5. The GOAP Planner
While traditional GOAP systems use heavy A* graph-search algorithms to reverse-engineer a plan from a goal state, this planner is optimized for CS2 64-tick servers. It operates as a highly modular, hardcoded `if/else` decision tree. This avoids massive performance costs while still outputting a predictable, ruthless queue of actions based on the current combat distance and weapon state.

### 6. Engine Hooks & Physics Hijacking
Overriding CS2 bot pathing natively is heavily restricted by the current Source 2 API. To enforce our Blackboard's desires, ZeusBotAI resorts to brute-force engine manipulation:
* **Organic Aim Math (`ProcessAgentIntelligence`)**: The bot features a custom aimbot that injects sine/cosine noise into its target vectors. Instead of locking rigidly to a skeletal bone, the crosshair wanders naturally around the chest and shoulders. Turn speed is also clamped, forcing smooth tracking rather than instantaneous snapping.
* **Motor & Input Injection (`InjectMotorCommands`)**: 
  * Every server tick, we use `pawn.Teleport()` to forcefully overwrite the bot's XYZ velocity in the direction the Blackboard demands. 
  * We manually flip bits in the `MovementServices.Buttons.ButtonStates` array. This allows the bot to natively execute actual engine commands like `+jump`, `+duck`, or `+attack` so the server properly registers the bot firing the Zeus or swinging the knife.

---

## 🚀 Summary
ZeusBotAI is a masterclass in working *around* engine limitations. By marrying CS2's native navmesh for macro-navigation with a bespoke, math-heavy GOAP physics hijacker for micro-combat, it creates a terrifyingly smooth and responsive PvE experience.
