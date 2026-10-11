# BTD6-Performance-Mod

> [!WARNING]
> I am aware of some sort of client-side anti-tamper checks BTD6 has on launch and some form of server-side anticheat.
> This mod does not attempt to evade any such checks and may get flagged.

An all-in-one performance mod for BTD6 I wrote for my friend. Developed by marking native hot-path functions with hooks
to develop a quasi-profiler and timing them, afterward developing patches for expensive subroutines.  

#### Room for improvement

Waiting 2-3 hours for each profiling session makes it difficult to curate significant performance improvements and most experimental patches were difficult to make significant. Making some routines (i.e `Scene`, `CollisionChecker` & `ProcessRootObject`) multi-threaded would lead to a worthwhile speed boost however it is hard to maintain behaviour.  
  
## Installation

Download `BTD6-Patches.dll` and `BTD6-Patches.json` from this repository's Releases page, then download [MelonLoader](https://github.com/lavagang/melonloader) onto BTD6 and move the dll into Mods and the json into UserData.

## Timings

Before → after, per call unless stated otherwise.

Earlier measurements across previous builds; not all apply to the current game version:

- Simulation iterator `Current` getter: **10.40 ns → 9.44 ns**
- Collision callback iterator getter: **10.40 ns → 10.21 ns**
- Weapon behavior iterator getter: **10.40 ns → 9.86 ns**
- Model presentation iterator getter: **10.40 ns → 9.44 ns**
- Weapon animation iterator getter: **10.40 ns → 9.39 ns**
- Collision checker iterator getter: **10.40 ns → 9.91 ns**
- Deep candidate interface lookup: **26.78 ns → 5.23 ns**
- Bloon transform accessor: **3.96 ns → 3.17 ns**
- Inactive buff geometry/icon check: **5.41 ns → 3.97 ns**
- Banana placement query, identity case: **11.59 ns → 11.35 ns**
- Buff child-binding lookup, two children: **31.56 ns → 13.37 ns**
- Buff mutator binding lookup: **31.56 ns → 20.09 ns**
- Straight movement vector operator pair: **21.77 ns → 21.00 ns**
- Complete straight movement operator block: **13.20 ns → 13.55 ns**
- Model display-space conversion: **7.33 ns → 2.70 ns**
- Buff mutation membership lookup: **12.76 ns → 8.26 ns**
- Collision grid X/Y coordinate block: **5.30 ns → 3.31 ns**
- Zoned `Run`, one million passes with 16 objects each: **~54 ms → ~48 ms**

Collision callbacks:

- 1 interface: **11.06 ns → 11.08 ns**
- 8 interfaces: **12.65 ns → 13.32 ns**
- 16 interfaces: **15.77 ns → 15.14 ns**
- 64 interfaces: **31.14 ns → 13.86 ns**

Position lookups:

- Index 0: **6.40 ns → 5.22 ns**
- Index 1: **5.60 ns → 5.60 ns**
- Index 3: **6.39 ns → 5.48 ns**
- Index 7: **9.10 ns → 5.39 ns**
- Index 15: **13.93 ns → 5.69 ns**

Interface lookups:

- Count 1, index 0: **10.95 ns → 11.25 ns**
- Count 3, index 0: **11.08 ns → 11.25 ns**
- Count 3, index 2: **11.64 ns → 11.47 ns**
- Count 4, index 0: **10.91 ns → 10.97 ns**
- Count 4, index 3: **11.80 ns → 11.70 ns**
- Count 15, index 0: **10.99 ns → 11.03 ns**
- Count 15, index 14: **13.82 ns → 13.90 ns**
- Count 16, index 0: **10.97 ns → 10.99 ns**
- Count 16, index 15: **14.75 ns → 12.88 ns**
- Count 64, index 0: **11.01 ns → 10.98 ns**
- Count 64, index 63: **29.03 ns → 12.73 ns**

Lineage indexing (lineage × history size):

- 128 × 512, no match: **7.206 µs → 0.710 µs**
- 128 × 512, first match: **0.00495 µs → 0.00650 µs**
- 128 × 512, last match: **7.158 µs → 0.581 µs**
- 64 × 4,096, no match: **28.398 µs → 3.658 µs**
- 64 × 4,096, first match: **0.00495 µs → 0.00650 µs**
- 64 × 4,096, last match: **28.930 µs → 3.732 µs**
- 128 × 8,192, no match: **115.886 µs → 7.757 µs**
- 128 × 8,192, first match: **0.00690 µs → 0.00615 µs**
- 128 × 8,192, last match: **115.196 µs → 8.365 µs**

Experimental parallel lineage search (disabled; history size):

- 4,096, no match: **3.868 µs → 3.441 µs**
- 4,096, first match: **0.0054 µs → 0.0106 µs**
- 4,096, last match: **3.434 µs → 3.579 µs**
- 131,072, no match: **116.861 µs → 78.313 µs**
- 131,072, first match: **0.0074 µs → 0.0158 µs**
- 131,072, last match: **115.873 µs → 78.650 µs**
- 262,144, no match: **229.760 µs → 152.682 µs**
- 262,144, first match: **0.0050 µs → 0.0106 µs**
- 262,144, last match: **232.357 µs → 148.596 µs**

Only measured changes are listed.
