# FSC Modularization Plan

Baseline: `f3d2173022cc0433f3adb9347ec17d20b1a304ae`

This branch exists only to reduce `FSC.cs` responsibilities without changing runtime behavior.

## Non-negotiable behavior invariants

- Keep the current four shared-resource locks and their semantics:
  - ballistic console
  - requisition console
  - trigger console
  - turret
- Keep synchronized two-gun fire-priority arbitration.
- Keep provisional winner semantics.
- Keep Second turret prequeue semantics.
- Keep the late hard-commit boundary immediately before Review Console work.
- Do not wait for LocalReady before beginning shared turret work.
- Keep F9 generation/cancellation semantics.
- Local task failure must not globally invalidate the healthy gun.
- Do not infer physical reload state from task state.
- Do not change ETA constants or the 4 deg/s : 2 deg/s azimuth/elevation model.
- Do not change trigger safety ordering.

## Target modules

### 1. `Scheduling/FirePriorityModels.cs`
Pure state/data types currently nested in `FSC`:
- `GunTaskMode`
- `FirePriorityGunPhase`
- `FireReadyEstimate`
- `FirePriorityCandidate`
- `FirePrioritySession`
- `TurretReservation`

No runtime behavior should change in this step.

### 2. `Scheduling/FireReadyEstimator.cs`
Pure ETA calculation and formatting:
- load/elevation/azimuth ETA calculation
- alignment fallback
- ETA detail formatting

Inputs are snapshots; this module must not mutate game state.

### 3. `Scheduling/FirePriorityCoordinator.cs`
Owns arbitration state and transitions:
- left/right candidates
- session
- winner/second
- provisional flag
- generation
- state-gate fallback
- pair resolution
- abnormal-task invalidation
- post-shot promotion

This module must preserve the existing Promise.all-like synchronized arbitration semantics.

### 4. `Scheduling/TurretScheduler.cs`
Owns shared turret-lane coordination:
- turret reservation lifecycle
- queue eligibility
- winner ownership
- Second prequeue
- provisional preemption
- hard-commit handoff

It must not own trigger-console interactions.

### 5. `Execution/GunTaskRunner.cs`
Owns a single gun task pipeline:
- ballistic solve
- requisition
- physical load/reload
- elevation
- waiting for turret readiness
- trigger-console execution after hard commit
- post-shot recovery

It delegates arbitration/turret decisions instead of owning global scheduling state.

### 6. `Scheduling/TaskDispatcher.cs`
Owns:
- queue dispatch
- physical-state-based gun selection
- requeue/retry/reclassification
- slot release

It must continue to treat physical state as authoritative.

### 7. `FSC.cs`
Final role:
- bind/update/dispose lifecycle
- dependency wiring
- public facade used by UI/scene interaction
- starting background loops

## Refactor sequence

Each stage should compile independently and be reviewable as a behavior-preserving move.

1. Extract data-only types.
2. Extract pure ETA functions.
3. Extract fire-priority coordinator state/transitions.
4. Extract turret scheduler.
5. Extract per-gun task runner.
6. Extract dispatcher.
7. Reduce `FSC.cs` to composition/lifecycle.

Do not combine behavioral fixes with these commits. Any discovered runtime bug should be fixed on `master` first and then merged/rebased into this branch before continuing structural work.

## Validation gates

After each structural stage:

- local `dotnet build` against the game directory
- no changes to public UI-visible semantics unless purely textual
- normal dual-gun continuous fire test
- same-target-order arbitration test
- Second prequeue handoff test
- F9 during ballistic calculation
- F9 during shell loading
- F9 during powder staging
- F9 during turret slew
- F9 before Review/Arm

Only merge back to `master` after the branch passes the same gameplay scenarios as its baseline.
