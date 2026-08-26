# 006 — Map pin landed off the edge of the map in a housing subdivision

**Status:** fixed in 0.81.36.1 (shipped broken in 0.81.36.0)
**Keywords:** map pin, flag marker, SetFlagMapMarker, AgentMap, housing, subdivision, Empyreum,
TerritoryType.Map, CurrentMapId, map offset, sizefactor

---

## Symptom

The first real use of "Allow map pin" — a challenge in **Empyreum** (territory 979) — put the flag
far outside the map, up in the top-left corner of the map window, with the actual map parchment
sitting in the bottom-right. The pin was not merely inaccurate; it was nowhere near the map.

## Root cause

**A territory does not have one map.** `TerritoryType.Map` returns a single Map row, and for a
residential district that row is the WARD map. The player was standing in the **subdivision**,
which is a different Map row with completely different coordinate offsets.

The diagnostic (added while chasing this) said it outright:

```
[MapDiag] territory=979 sheetMap=679 key='r1h1/01' sizeFactor=200 offsetX=0   offsetY=0
          player=(-918.572, -479.192)
[MapDiag] agent curTerr=979 curMap=680 key='r1h1/02' sizeFactor=200 offsetX=702 offsetY=655
```

`SetFlagMapMarker` takes world coordinates and converts them internally **using the map id it is
given**. Handed the ward map, it applied offsets of (0, 0) to a position that needed (702, 655).

Working it through with sizeFactor 200 confirms the screenshot exactly:

| Map | Offsets | Resulting map coord |
|-----|---------|---------------------|
| 680 `r1h1/02` (subdivision — correct) | 702, 655 | ~(6.9, 14.8) — on the map |
| 679 `r1h1/01` (ward — what we used)   | 0, 0     | ~(−7.1, 1.7) — off the left edge, near the top |

## Why it was not caught

The API research was thorough about the part that *looked* risky — whether `SetFlagMapMarker`
wanted world or map coordinates — and got that right by reading the decompiled source. It never
questioned `TerritoryType.Map`, because "a territory has a map" is such an obvious-seeming
one-to-one that it did not present as an assumption at all.

The confidence note on the shipping message did flag it: *"whether `MapIdFor` returns a sensible row
for instanced or housing territories — I did not check the odd ones."* The gap was correctly
identified and shipped anyway.

## Fix

`MapPinService.MapIdFor` now resolves in order of authority, sheet **last**:

1. `ChallengeArea.MapId` — captured at authoring, when the author was standing on the spot. The
   only source that is right when the challenge and the player are on different sub-maps.
2. `AgentMap.CurrentMapId` — the map the game currently has live. Right whenever the player and the
   challenge share a sub-map, which covers every area authored before the field existed.
3. `PlayerStateReader.MapIdFor` (the sheet) — the fallback, no longer the answer.

`ChallengeArea` gained a `MapId`, captured by "Add at my position", "Move to me" and the race slots.
The area editor offers "Set map from here" when it is 0, since dragging a centre by hand cannot
know which sub-map it landed on.

## Lessons

- **A territory can present several maps.** Housing wards and subdivisions are the common case;
  do not assume `TerritoryType.Map` is the map the player is looking at. `AgentMap.CurrentMapId` is
  what the game itself thinks.
- **Capture map identity with the position, at authoring time.** Same reasoning as `TerritoryId`:
  the moment the author is standing on the spot is the one moment the answer is certainly right.
  Deriving it later means deriving it from incomplete information.
- **A named gap in a confidence note is a to-do, not a disclaimer.** "I did not check housing
  territories" was written, shipped, and then reported by the first person to try it.
- **Instrument rather than reason** when a coordinate lands somewhere unexpected. One log line
  carrying both candidate map ids, their offsets and the player position settled in one reload what
  three plausible hypotheses could not. `MapPinService.LogZoneDiagnostics` is kept for that reason.
