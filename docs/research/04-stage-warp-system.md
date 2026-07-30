# Stage Warp System

Flow: Launcher → TCP WarpRequest → server validates → WarpCommand → bridge sets WARP_PENDING → module consumes next frame.

Blocked when `dolphinState` is LOADING or WARPING.

Level data: `assets/levels.ntsc-u.json` validated against RAScript courseIDs (`python tools/verify_levels.py`). The catalog must name **every** playable area ID the Connected Players roster can show — plaza secrets (20–24, 29), stage secrets (31–33, 40–42, 44, 46–48, 50–51), Noki undersea (16), Corona (52), and boss arenas (55–60). Missing entries fall through to `Course {id}` in `LevelCatalog.GetCourseName`. Episode titles live in `assets/episode-names.ntsc-u.json` and must stay aligned with the levels catalog.

**Noki Undersea (16):** `mareUndersea.arc` has a single load scenario (episode **0**). Do not list catalog episodes 3/7 as warpable — those are bay mission contexts for natural waterfall dives (`normalizeMareUnderseaNextSceneForLoad` forces load 0). Host / warp-everyone / Hide & Seek must only select episode 0; `resolveWarpTarget` also forces load 0 if a stale client sends 3/7.

## Loading-zone episode 0xFF → title screen (CRITICAL)

**Symptom:** Entering many plaza/stage loading zones (secrets, bosses, mini-games) boots to title (area 15).

**Root cause:** `performSmsoMoveStage` treated `next=area/255` as shine episode-select (Delay Context 8). Single-episode load areas have no episode menu — select fails and lands on options/title. dolphin.log: `moveStage next=23/255 cur=1/2` → `Stage init area=15` (Red Coin Field). Same pattern for Sand Bird `33/255`, Petey `55/255`, etc.

**Fix (`module/src/stage_guard.cpp`):**

| next scene | routing |
|------------|---------|
| area 15 (options) | vanilla `moveStage` |
| plaza hub return `1/255` | `moveStage` (not episode-select) |
| single-episode load area `/255` | force ep **0** + `moveStage` |
| multi-episode shine stage `/255` from exit | episode-select (Delay 8) |
| casino `14/255` | normalize to load 0/1 from mission |

Single-episode set = `BetterSMS::Stage::isExStage` **or** explicit allowlist covering plaza secrets (20–24, 29), stage secrets (31–33, 40–42, 46–48, 50–51), Corona (52), bosses (55–60), Blooper surf (30), airstrip (0). Casino (14) / hotel (7) / pinna park (13) stay on multi-episode paths.

**Verify:** plaza → Red Coin Field / Super Slide / Turbo Track logs `single-ep load area N (ep 0xFF) → moveStage ep0` and loads the secret — never `Stage init area=15`.
