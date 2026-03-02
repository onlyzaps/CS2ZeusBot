# ZeusBotAI
https://youtu.be/rWnfR2Fp8VQ

**Custom GOAP-Driven Bot AI for Counter-Strike 2**

This is a CounterStrikeSharp plugin I put together that completely overhauls how bots handle close-quarters combat. If you've messed with CS2 bots, you know they aren't great at aggressively rushing with melee weapons or tasers. 

To fix this, I implemented a custom Goal-Oriented Action Planning (GOAP) architecture. Because fighting the native CS2 bot pathing is a nightmare, this script uses a hybrid approach: it lets the native CS2 navmesh handle basic map traversal, and only hijacks the bot's motor controls the second they get into a fight.

Here’s a breakdown of how the brain actually works under the hood.

---

## 🧠 How It Works

### 1. The GOAP Core
I built a lightweight GOAP setup specifically designed not to tank server performance on 64-tick. 
* **`GoapAction`**: Things the bot can actually do (equip a knife, sprint, dodge). 
* **`GoapGoal`**: What the bot wants to achieve right now (e.g., survive, kill). Priority shifts dynamically every frame.

### 2. Memory & Blackboard
Bots don't just react instantly; they have a short-term memory system.
* **`WorkingMemory`**: Bots track your `LastKnownPosition`. If you break line-of-sight, the bot's "Confidence" in that memory decays over time. Hide long enough, and they literally forget you exist.
* **`BotBlackboard`**: This is the bot's scratchpad for the current tick. It stores where they want to move, how fast, where to aim, and what buttons they need to press (+jump, +duck).

### 3. Sensors (Bot Vision)
To stop the bots from blatantly tracking you through walls across the map, the sensory system has strict physical checks. A target has to be close (`< 750f`), on a similar floor (`zDiff < 350f`), in the bot's FOV, and natively "spotted" by the CS2 engine. 

Once a player is spotted, the `ThreatLevel` spikes, and my custom GOAP engine forcefully takes the wheel. If you hide, the threat drops, and the native CS2 navmesh takes over again so the bot doesn't just stand there staring at a wall.

### 4. Dynamic Movement & Actions
This is where the bots get actually dangerous. Instead of just W-keying at you, they have some logic to make them harder to hit:
* **Weapon Swapping**: If you're far away, they pull out the knife to maximize sprint speed. Once they get within 450 units, they hot-swap to the Zeus.
* **Fanning Out**: I used some basic trig (`Math.Cos` + the bot's entity index) so that if 5 bots rush you, they fan out in a circle instead of forming a single-file line.
* **Evasive Movement**: While rushing, they use randomized state machines to B-hop, crouch-slide, and ADAD strafe. 

### 5. The Planner
Standard GOAP systems use heavy A* graph-search algorithms to build a plan. That's way too heavy to run every tick for multiple bots on a CS2 server. Instead, the planner here is a highly optimized, hardcoded decision tree. It's cheap on the CPU but still spits out a solid queue of actions based on distance and what weapon the bot is holding.

### 6. Engine Hooks & Physics Hijacking
Working with the Source 2 API to override bot pathing is currently pretty restrictive. To actually make the bots do what the Blackboard wants, I had to use a few workarounds:
* **Organic Aiming**: Instead of locking right to a head bone, the aimbot injects sine/cosine noise into the target vectors. The crosshair naturally drifts around the upper chest/shoulders, and turn speed is clamped so it looks like smooth mouse tracking rather than an instant snap.
* **Motor Injection**: Every server tick, the script uses `pawn.Teleport()` to forcefully shove the bot's velocity in the direction it wants to go. 
* **Button Forcing**: To make the bots actually swing the knife or shoot the taser, the script manually flips bits inside the `MovementServices.Buttons.ButtonStates` array. This tricks the server into thinking the bot actually pressed the jump, duck, or attack keys.

---

Basically, getting custom bot movement right in CS2 means fighting the engine a bit, but this setup handles the macro/micro handoff really smoothly. Feel free to use or modify the movement logic for your own PvE modes!
