# FSC Modularization Plan

Baseline: `f3d2173022cc0433f3adb9347ec17d20b1a304ae`

Status: **full extraction landed on `refactor/fsc-modularization`; compile/runtime validation pending.**

This branch exists only to reduce `FSC.cs` responsibilities without intentionally changing runtime behavior. Per the chosen refactor strategy, the modules were extracted as one complete structural pass; intermediate commits are not treated as independently runnable checkpoints. The branch head is the validation unit.

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

## Extracted modules

### `Scheduling/FirePriorityModels.cs`
Pure state/data types formerly nested in `FSC`:
- `GunTaskMode`
- `FirePriorityGunPhase`
- `FireReadyEstimate`
- `FirePriorityCandidate`
- `FirePrioritySession`
- `TurretReservation`

### `Scheduling/FireReadyEstimator.cs`
Pure ETA calculation and formatting:
- load/elevation/azimuth ETA calculation
- alignment fallback
- ETA detail formatting
- original-order tie breaking

Inputs are snapshots; this module does not mutate game state.

### `Scheduling/FirePriorityCoordinator.cs`
Owns arbitration state and transitions:
- left/right candidates
- session
- winner/second
- provisional flag
- generation
- state-gate fallback
- pair resolution
- abnormal-task invalidation
- turret-lane ownership state
- late hard commit
- post-shot Second promotion

The Promise.all-like synchronized arbitration semantics are preserved.

### `Scheduling/TurretScheduler.cs`
Owns the physical shared turret lane:
- turret coroutine lock
- reservation lifecycle
- queue eligibility
- Second prequeue
- provisional preemption
- cancellation-safe F9/generation checks
- physical rotation and release

It does not own trigger-console interactions.

### `Execution/GunTaskRunner.cs`
Owns a single gun task pipeline:
- ballistic solve
- requisition
- physical load/reload
- elevation
- concurrent turret reservation
- wait for turret readiness
- trigger-console execution only after hard commit
- post-shot recovery handoff

It delegates global arbitration and queue ownership instead of storing them itself.

### `Scheduling/TaskDispatcher.cs`
Owns:
- pending/recent queues
- left/right active slots
- physical-state-based gun selection
- physical recovery gate
- requeue/retry/reclassification
- task result counters and slot release

Physical state remains authoritative.

### `Infrastructure/SharedConsoleCoordinator.cs`
Owns the three non-turret shared console locks:
- ballistic
- requisition
- trigger

Also owns F9 fire-control baseline reset and background powder replenishment.

### `Infrastructure/SceneExposureService.cs`
Owns the optional map-entity exposure loop.

### `FSC.cs`
Reduced to the composition root/public facade:
- bind/update/dispose lifecycle
- dependency wiring
- public UI/scene API
- coroutine tracking

## Validation gates

Before merging back to `master`:

- local `dotnet build` against the game directory
- normal dual-gun continuous fire test
- synchronized arbitration / ETA order test
- Second prequeue handoff test
- F9 during ballistic calculation
- F9 during shell loading
- F9 during powder staging
- F9 during turret slew
- F9 before Review/Arm
- verify existing `[FCS Stall]`, `[FCS ReloadTrace]`, `[FCS BALLISTIC TRACE]`, and `[FCS PrepProbe]` diagnostics remain readable

Any behavioral bug discovered during validation should be fixed deliberately and called out separately from the structural extraction before this branch is merged.
