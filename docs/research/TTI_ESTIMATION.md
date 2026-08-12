# Early Time-To-Impact estimation

This document records the empirical basis for the production early-TTI estimator introduced after v1.1.6.

## Goal

v1.1.6 reads the game's mechanical Time-To-Impact dial after a gun reaches `WaitingForFire`. The value is accurate, but it appears late in the firing workflow.

The early estimator keeps the game ballistic calculator as the source of the actual firing solution, but derives the HUD/FirePlan flight-time value immediately after the FirePlan is committed.

## Observed model

Repeated in-game measurements show that, for a fixed powder charge, Time-To-Impact is proportional to target range:

```text
TTI(seconds) = distance(km) * secondsPerKm(charge)
```

Equivalently:

```text
speed(m/s) = 1000 / secondsPerKm(charge)
```

Within the tested ranges, elevation does not require an additional TTI correction. The same charge produced a stable `TTI / distance` ratio across substantially different elevations.

This is an empirical model of the game's behavior, not a claim about real-world exterior ballistics.

## Production coefficients

| Charge | seconds/km | Approx. equivalent speed |
|---|---:|---:|
| C1 | 4.758869 | 210.13 m/s |
| C2 | 3.830061 | 261.09 m/s |
| C3 | 2.613011 | 382.70 m/s |
| C4 | 1.894451 | 527.86 m/s |
| C5 | 1.540442 | 649.16 m/s |
| C6 | 1.427168 | 700.69 m/s |

Implementation: `IronNestFCS.Logic/FCS/TimeToImpactEstimator.cs`.

## Measurement notes

### C1

Multiple independent samples were available. The production coefficient uses the median of the observed per-shot ratios to reduce sensitivity to the two-decimal dial/log precision.

| Distance | Official TTI | TTI / km |
|---:|---:|---:|
| 4.494 km | 21.39 s | 4.75968 |
| 3.152 km | 14.99 s | 4.75571 |
| 2.954 km | 14.06 s | 4.75965 |
| 3.121 km | 14.85 s | 4.75809 |

Production value: `4.758869 s/km`.

### C2

| Distance | Official TTI |
|---:|---:|
| 5.001 km | 19.13 s |
| 9.490 km | 36.36 s |

Production value is the least-squares slope through the origin: `3.830061 s/km`.

### C3

| Distance | Official TTI |
|---:|---:|
| 10.005 km | 26.14 s |
| 14.998 km | 39.20 s |
| 14.496 km | 37.87 s |

Production value is the least-squares slope through the origin: `2.613011 s/km`.

### C4

| Distance | Official TTI |
|---:|---:|
| 15.106 km | 28.63 s |
| 15.995 km | 30.29 s |

Production value is the least-squares slope through the origin: `1.894451 s/km`.

### C5

The same 20.783 km solution was observed twice with official TTI values of 32.01 s and 32.02 s.

Production value: `1.540442 s/km`.

### C6

C6 was deliberately tested at both short and longer ranges. This also verified that using maximum charge at short range remains a valid in-game ballistic solution when the resulting elevation is within the gun's physical range.

Final wide-range validation set:

| Distance | Elevation | Official TTI | TTI / km |
|---:|---:|---:|---:|
| 4.516 km | 9.03° | 6.45 s | 1.42826 |
| 9.512 km | 19.02° | 13.56 s | 1.42557 |
| 14.500 km | 29.00° | 20.69 s | 1.42690 |
| 17.496 km | 34.99° | 24.98 s | 1.42775 |

Production value is the least-squares slope through the origin for this set: `1.427168 s/km`.

Additional near-range C6 samples around 4.3–5.2 km independently produced the same ratio within dial/log precision.

## Runtime behavior

1. `FirePlanner` obtains the game's normal ballistic solution and chooses a gun/charge exactly as before.
2. After the immutable `FirePlan` is created, `TimeToImpactEstimator.TryEstimateSeconds()` uses the selected charge and task distance.
3. The value is stored through `FirePlan.TrySetEstimatedFlightSeconds()` and becomes available to the HUD immediately.
4. The existing `TimeToImpactReader` remains read-only and can still populate the value at `WaitingForFire` if an early estimate is unavailable.

The estimator does not choose targets, charges, guns, elevations, or fire order. It only supplies descriptive flight-time data for the already selected FirePlan.

## Probe cleanup

The development branches used temporary timing, dial and validation probes to establish the model. Those probes and validation-only logs are intentionally excluded from the production feature branch. The release implementation keeps only the estimator, the FirePlan initialization hook, the existing dial fallback, and documentation.
