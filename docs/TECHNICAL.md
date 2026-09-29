# ParaSoft Airbraking Technologies: technical notes

How ParaSoft turns a parachute's canopy into a softbody, and why each piece is the way it
is. For installing and using it, see the [README](../README.md).

- [The one rule: drag is not ours](#the-one-rule-drag-is-not-ours)
- [Reading the canopy out of the part](#reading-the-canopy-out-of-the-part)
- [Fitting: finding round canopies in any model](#fitting-finding-round-canopies-in-any-model)
- [The lattice](#the-lattice)
- [Embedding: making the original mesh follow](#embedding-making-the-original-mesh-follow)
- [The cloth solver](#the-cloth-solver)
- [Canopy aerodynamics](#canopy-aerodynamics)
- [Deployment](#deployment)
- [Environment](#environment)
- [Collisions](#collisions)
- [Drawing](#drawing)
- [Parachute modules](#parachute-modules)
- [Performance](#performance)
- [Testing without the game](#testing-without-the-game)
- [Known limits](#known-limits)

---

## The one rule: drag is not ours

ParaSoft never applies a force to a craft. Stock `ModuleParachute`, RealChute's
`RealChuteModule` and FAR's `RealChuteFAR` go on computing and applying exactly the drag
they always have; their drag cubes, `AddForceAtPosition` calls and every number a descent
calculator or landing predictor reads are untouched. There is no Harmony patch in this mod.

The softbody's anchor is kinematic: the canopy is pulled along by the part, pushed around
by the air, gravity and anything it touches, and never pushes back. That keeps descent
rates exactly what the parachute mod was tuned for, keeps FAR's aerodynamics FAR's, and
means a bug in ParaSoft can only ever look wrong, never fly wrong.

It also shapes one design decision throughout: the canopy is never *shown* more open
than the module's own drag says it is. Each module reports how far through its deployment
it is as a drag area; ParaSoft limits the hem's circumference to `sqrt(current area /
full area)` of its design length (see [Reefing](#reefing)), so what you see and what the
craft feels stay in step even though they are computed separately.

## Reading the canopy out of the part

A parachute model does not store what its open canopy looks like. Stock and RealChute
canopies are scaled open by their deploy animations - the Mk16's canopy transform goes
from a scale of 0.001 to (0.01, 0.01, 0.1) semi-deployed to 0.1 open. ReStock's canopies
are skinned meshes pulled open by bones. So `CanopyCapture`:

1. snapshots every transform under the part;
2. switches the canopy's GameObject (and any inactive parents) on;
3. plays the module's semi-deploy clip on layer 0 and its full-deploy clip on layer 1,
   both at their last frame, and samples once - the full clip wins wherever both animate
   the same channel;
4. bakes every `MeshRenderer` and `SkinnedMeshRenderer` under the canopy transform into
   the canopy's frame (its position and rotation, *not* its scale). Skinned meshes are
   skinned on the CPU from the bones' current poses and the mesh's bind poses rather than
   with `BakeMesh`, whose treatment of transform scale changed between Unity versions;
5. puts every transform, animation state and active flag back.

All of this happens inside one call, so nothing is ever rendered in the sampled pose.

The fitted result is cached by module, canopy name, vertex count and deployed size to the
centimetre, so every Mk16 on a craft, and every RealChute of the same model and diameter,
shares one fit.

## Fitting: finding round canopies in any model

`CanopyFitter` works from geometry alone, because models do not label which vertices are
fabric and which are lines, where the hem is, or even how many canopies they contain.

- **Fabric vs lines.** Lines converge on the canopy transform's origin, fabric sits far
  out, so vertices beyond 45% of the farthest distance are fabric.
- **How many canopies.** K-means (farthest-point seeded) on the fabric vertices, for
  k = 1..7, or the count RealChute reports for its model. A count is accepted only if every
  canopy it produces is convincingly round: sector-radius ratio under 1.12 and fit error
  under 4% of the radius. A real single canopy fits to about 1%; three canopies forced
  into one are lopsided by 20% or more. If no count passes, the least-bad loose fit is
  used.
- **Axis.** The centroid of a round canopy's dome lies on its axis. The axis estimate and
  the dome/line split depend on each other, so they are iterated.
- **Hem.** The lowest part of the widest band: lines rise to it, fabric rises from it.
- **Profile.** Distance from a centre point just below the hem, binned by polar angle.
  Polar about that point, every canopy from a flat circular to a deep conical one is a
  single-valued curve, where a height-over-radius profile would fold back on itself at a
  hemispherical canopy's vertical skirt. The median of each bin is taken (double-sided
  fabric has two layers), gaps are interpolated, and the curve is resampled to 33 points
  evenly spaced by arc length from the vent edge to the hem.
- **Vent.** An open crown keeps its real vent. A closed crown gets a vent of 4% of the
  radius, because the lattice needs a ring to hang from - and every real canopy has one.

Models that are not round (the stock EVA parafoil, rectangular mod canopies) fail the
roundness test and simply keep their module's own animation.

Every parachute model in a typical install fits - see
[Testing without the game](#testing-without-the-game) for the numbers.

## The lattice

`CanopyLattice` places particles on the radial seams, where a real canopy's load tapes
run, at evenly spaced rings from the vent edge to the hem, then down each suspension line
to the confluence. One more particle is the pilot chute, bridled to the vent.

| Quality | Gores | Rings | Line segments | Particles |
|---|---|---|---|---|
| Low    | 12 | 5 | 3 | 98 |
| Medium | 20 | 7 | 4 | 222 |
| High   | 28 | 9 | 5 | 394 |

Constraints, all distance constraints:

| Kind | Between | Behaviour |
|---|---|---|
| Seam | ring k and k+1 on one seam | resists stretch, slack in compression |
| Hoop | neighbouring seams on one ring | ditto; the hem's are the reefing line |
| Shear | panel diagonals | ditto, softer |
| Bend | two cells apart | only resists folding flat (below 60% of rest); a streamer can straighten |
| Line | line segments, and pilot bridle | ditto, stiffest |

Fabric and cord only ever pull. Everything else about the canopy's shape comes from the
air pressure on it, which is what makes it collapse when the air stops.

## Embedding: making the original mesh follow

The player sees the part's own canopy mesh, not the lattice. `CanopyEmbedding` ties each
of its vertices to the lattice:

- a **fabric** vertex by where it sits on the canopy surface - its angle around the axis
  gives a fractional gore, its nearest point on the profile a fractional ring - plus its
  offset from the bilinearly interpolated lattice surface in that spot's local frame
  (hoop direction, meridian direction, normal);
- a **line** vertex by how far it is down the lines and which two lines it sits between.

Normals and tangents are stored in the same local frames. Evaluating on the rest lattice
reproduces the model to about a ten-millionth of the canopy radius, and moving the whole
lattice rigidly moves every vertex rigidly - both checked on every model in the install.
Because offsets live in local frames, seams, scallops, double-sided fabric and lines
modelled as thin tubes all keep their shape as the canopy moves.

## The cloth solver

`CanopySim` is extended position-based dynamics (XPBD) with substeps: by default four per
50 Hz physics frame, two Gauss-Seidel passes each.

**Frame.** Positions are relative to the anchor (the canopy transform's origin) on world
axes; velocities are relative to the anchor's velocity. The canopy is therefore immune to
KSP's floating origin and Krakensbane shifts, which move the anchor and canopy together,
and coordinates stay small. When the anchor's velocity changes between frames, the
canopy feels the opposite change - the craft being yanked by staging drags the canopy, it
does not teleport it.

**Masses.** Fabric at 0.05 kg/m^2 (or RealChute's material areal density), cord at
0.012 kg/m. Every fabric particle also carries **apparent mass**: the air a canopy drags
along, 0.35 x air density x canopy radius per square metre of fabric. It is why canopies
swing slowly rather than snap about. It only adds inertia, not weight, and it vanishes in
vacuum. It is also what keeps light fabric stable when the air pushing on it is hundreds of
times its weight: without it, the explicit aerodynamics would need microsecond substeps.

**Implicit aerodynamic damping.** Each particle's aerodynamic force is divided by
`m + h c` rather than `m`, where `c` is the derivative of its drag with respect to its
velocity - linearised backward Euler. Fabric at 250 m/s is stable at 200 substeps a
second because of this.

**Stiffness.** Compliance is `rest length / (EA x scale)`, with EA 40 kN for seams, 20 kN
hoops, 4 kN shear, 60 kN lines, and scale growing with the canopy's radius squared -
bigger canopies have stronger tapes, and carry more load.

**Long-range attachments.** Nothing may be further from the anchor than 1.03 times the
length of line and seam it hangs from. A chain of light cord particles holding a canopy
dozens of times heavier is the worst case for Gauss-Seidel: left alone, the lines stretch
40% and snap back like bungee cord. The tether caps that exactly and cheaply. The 3% slack
matters - a tether held exactly at length fights every sideways correction and leaks
inward momentum until the canopy recoils.

**Recovery.** A non-finite particle, or one 50 canopy-lengths away, resets the canopy to
open and trailing the airflow and counts a recovery. The tests require zero recoveries in
every scenario, including a 250 m/s sea-level opening and Mach 1.9 on Mars.

## Canopy aerodynamics

Pressure acts along each fabric triangle's outward normal `n`. With `w` the airflow
direction relative to the triangle and `s = n . w` (positive when air strikes the inside):

```
external   Cout = -Cbase                              if s >= 0 (leeward, in the wake)
                  -Cbase + (Cw + Cbase) s^2           if s <  0 (outer face windward)
flat plate Cn   = Cflat s |s|
dp = fill x q_inf x (Cin - Cout)  +  (1 - fill) x q_local x Cn
```

`Cin = 1` (stagnation), `Cbase = 0.4` (wake suction), `Cw = 1`, `Cflat = 1.2`. A fully
inflated hemisphere facing the flow gets a uniform 1.4 q across it - a drag coefficient of
1.4 on projected area, which is what real hemispherical canopies measure. A limp canopy
streaming in the flow gets only flat-plate forces, and flutters. Skin friction
(`Cf = 0.015`) acts along the fabric, and lines get cylinder crossflow drag.

**Fill** is the fraction of stagnation pressure inside the canopy. It rises towards

```
(1 - porosity) x smoothstep(0.05, 0.6, alignment) x clamp(2.5 sqrt(mouth), 0, 1) x clamp(q / q_support, 0, 1)
```

where *alignment* is how squarely the mouth faces into the airflow, *mouth* is the hem's
opening area over its design area, and `q_support` is the pressure needed to hold the
fabric's weight up. It fills with time constant `fillDistance x diameter / speed` - real
round canopies fill in two to eight diameters of travel - and empties at twice that rate,
but never slower than a second: once the air stops, a canopy collapses whatever its size.

**Mach.** Above Mach 0.8 the pressure coefficient falls to 0.6 of its value by Mach 2.5,
as parachute drag does. Above Mach 1.05 the internal pressure "breathes" by up to 18% at a
few hertz - the standing shock ahead of a supersonic canopy periodically collapses, which
every Mars parachute test shows.

**Turbulence.** Three sinusoidal gust modes, scaled by the canopy's diameter and speed,
sampled once per frame per triangle. The intensity setting scales them; wind speed raises
them.

## Deployment

The pack is fired off the part at 12 m/s (`ejectSpeed`), away from the part through its
cap, and becomes the deployment bag: the pilot particle, carrying the mass of everything
still packed, dragged by the pilot chute (2% of the canopy's drag area, half as much again
while the bag is on).

Every particle has a **payout distance**: how far along the structure from the confluence
it is, summed from the actual segment lengths. Lines come out first, then the hem, then
each ring to the crown, each let go when the bag is far enough away to have pulled it out.
Everything already out lies straight behind the bag, as lines under extraction tension do,
with the gores spread a little around the pull so the streamer has a mouth for air to
find. The layout places every seam and line segment at exactly its rest length, so nothing
is pre-stretched at release.

When the crown leaves the bag the snatch takes out the bag's outward momentum, the pilot
chute carries on at its own mass, and the canopy is on its own: air finds the mouth, the
fill rises, pressure pushes the gores out, and it inflates - crown first, as real ones do.

<p align="center"><img src="images/deploy-sequence.png" width="900" alt="Deployment sequence"></p>

### Reefing

The hem's hoop constraints are the reefing line. Their length follows
`sqrt(module's current drag area / its full drag area)`, never below 5% while
semi-deployed. For stock that is `areaSemi` then a lerp to `areaDeployed` along the
module's own `lerpTime`; for RealChute and RealChuteLite it is their `currentArea` over the
full area. A reefed canopy is held small, fills partially through its narrow mouth, and
opens with a proper disreef when the module moves to fully deployed. The reefing line can
let out from nothing to full length in a quarter of a second, and be taken in over half a
second.

### Cutting

When the module cuts the canopy, it is handed to the flight scene's system as debris: the
lines let go of the part and the canopy flies on by itself for the configured time. With
no anchor, its frame's origin is kept relative to the planet's centre - which floating
origin and Krakensbane move along with everything else - and its velocity follows the
canopy's, re-centring if it drifts. A part destroyed with its canopy out does the same.

## Environment

Every physics frame, per canopy:

| Quantity | Source |
|---|---|
| Anchor velocity | the part's rigidbody point velocity (its parent's, for physicsless parts) + Krakensbane frame velocity |
| Air velocity | zero in KSP's rotating frame, the surface's rotation velocity when it is inertial, plus wind |
| Wind | Kerbal Weather Project's world-space wind vector (`KerbalWxClimo`/`KerbalWxPoint.windVectorWS`, whichever mode is on) - the same vector KWP subtracts from part velocities in its own aerodynamics; otherwise any wind function registered with FAR's `FARAtmosphere.GetWind` |
| Density | the part's `atmDensity`, else the vessel's |
| Speed of sound | the vessel's |
| Gravity | `FlightGlobals.getGeeForceAtPosition` |

The fictitious forces of KSP's rotating frame act on the craft and canopy alike and are
left out: they would only shift the canopy relative to the craft by millimetres.

This is what makes the same canopy behave differently on different worlds. Nothing is
special-cased per planet: on Duna the dynamic pressure at a given speed is a sixtieth of
Kerbin's and the apparent mass a sixtieth too, so canopies need far more speed to fill,
swing faster and sag more; on Laythe or Eve they are sluggish and fat; in vacuum there is
no pressure and no apparent mass, and the canopy leaves its bag and hangs limp in whatever
shape its momentum leaves it.

## Collisions

`Surroundings` rebuilds each canopy's collision world every frame, in the canopy's frame:

- **Ground** - a 5x5 grid of raycasts onto layers 15 and 28 under the canopy, refreshed
  every 0.3 s or when it moves half a cell. That picks up PQS terrain, KSC buildings and
  Parallax's collideable scatter alike, so a canopy can come down on the VAB roof. Skipped
  entirely when the canopy is well above anything.
- **Sea** - a height field at sea level near an ocean. With Scatterer's craft wave
  interactions on, the heights come from Scatterer's own GPU wave simulation (below);
  otherwise from the water level Scatterer (or nothing) writes into the parachute part's
  `PartBuoyancy`.
- **Parts, kerbals, other vessels** - colliders from an overlap query on layers 0, 16, 17
  and 19, as spheres, capsules and oriented boxes; a convex mesh collider becomes its mesh
  bounds. Colliders on the parachute's own part are ignored until the craft is down: in
  flight the lines start inside it.

Solids push fabric out along the shortest way, with Coulomb friction against the solid's
own motion. Water is soft: fabric below the surface is carried a third of the way back up
each substep and dragged hard towards the water's motion, so it floats low and rides the
waves.

Canopies also push each other: each inflated canopy is a sphere to the others, and fabric
inside another's sphere is pushed out. That is what spreads a cluster.

None of this pushes back. A canopy draping over a lander does not move the lander.

### Scatterer's waves

Scatterer simulates its ocean on the GPU and, when its craft wave interactions are on,
answers wave-height queries for floating parts with a compute shader (`FindHeights`) and
an async readback, writing the result into each part's `PartBuoyancy.waterLevel`. A canopy
lying on the sea is not a part, so ParaSoft runs its own *instance* of that shader over an
8x8 grid under the canopy - fed the same wave textures (`m_fourierBuffer0/3/4[m_idx]`),
choppiness and grid sizes Scatterer feeds its own, with positions mapped into ocean space
exactly as Scatterer maps parts. Instancing the shader keeps its buffer bindings separate
from Scatterer's. The readback lands a frame or two later; waves move slowly enough that
it does not show.

Every member involved is found by reflection and checked on startup against the installed
Scatterer; any mismatch or error turns wave sampling off for the session, and canopies
fall back to the part's water level.

## Drawing

`CanopyRenderer` gives each original renderer a stand-in: a plain `MeshRenderer` on a
dynamic copy of its mesh, placed at the anchor with identity rotation. Its materials are
the original's, by reference, re-checked every frame (cheaply, by the first material's
reference, and fully every 30 frames), and the original's `MaterialPropertyBlock` is
copied every frame. So Custom Parachute Message's encoded-canopy shader (it is UV-driven
and normal-mapped; UVs and tangents are carried over), RealChute's texture picker,
TexturesUnlimited recolours, KSP's heat glow and part highlighting all keep working, as
do Deferred and TUFX.

The original renderer is hidden with `forceRenderingOff`, not disabled, so parachute
modules that switch their canopy on and off - all of them - are unaffected.

## Parachute modules

| Module | From | State | Open fraction | Notes |
|---|---|---|---|---|
| `ModuleParachute` | stock | `deploymentState` by name | `areaSemi`, `areaDeployed`, private `lerpTime` | canopy via private `canopy` field |
| `ModuleEvaChute` | stock | as above | as above | a parafoil: fails the round test and keeps its own animation |
| `RealChuteModule` | RealChute | each `Parachute.DeploymentState` | `currentArea / DeployedArea` | one slot per `Parachute`; material areal density; `canopyCount` guides the fitter |
| `RealChuteFAR` | FAR | `DeploymentState` | `currentArea / area(deployedDiameter)` | nylon areal density |

All by reflection, by name - no hard references, so ParaSoft loads with or without any of
them. With both RealChute and FAR, RealChute's patches convert stock chutes and FAR's
convert whatever is left, and ModuleManager's `LAST[ParaSoft]` pass sees the result.

## Performance

Measured by `Tools/Test.ps1` on .NET Framework (KSP's Mono is somewhat slower):

| | Low | Medium | High |
|---|---|---|---|
| Solver, per canopy per physics frame | 0.13 ms | 0.29 ms | 0.53 ms |

Moving a 10,000-vertex canopy mesh (ReStock's Mk16-XL is 9,360) costs about 1.2 ms, paid
only when the simulation has actually stepped - physics is 50 Hz, rendering usually
faster - and not at all for canopies no camera saw last frame (their bounds are still kept
honest from the lattice so culling notices when they come back).

Beyond `lodDistance` (600 m) from the camera a canopy steps at half rate; beyond
`freezeDistance` (2.5 km) it pauses. At most `maxCanopies` (24) canopies simulate at once;
any more deploy with their module's own animation.

## Testing without the game

`Tools/Test.ps1` needs nothing but the .NET Framework compiler.

**Solver behaviour** (`Tools/Tests.cs`, also run by CI): steady descent inflates with the
crown above the craft; a pack deployment is fully out in under half a second and 92% full;
vacuum never fills; Duna still fills at speed; a 10 m/s crosswind on a 7 m/s descent leans
the canopy about 53 degrees downwind, as the relative wind predicts; reefing holds the hem
to under 3% of its area and disreefing opens it; a landed canopy lies flat and empties;
fabric floats on waves; an obstacle is flowed around; a cut canopy flies away; Mach 1.9 and
a 250 m/s sea-level opening need no recoveries; a sudden change in the craft's velocity
drags the canopy rather than teleporting it; embedded mesh vertices stay on the simulated
canopy; and the timings above.

**Real models** (`Tools/ModelReport.cs`, with a KSP install): reads each parachute model
with a small `.mu` reader (`Tools/MuModel.cs`), plays its deploy clips, skins and bakes
it, fits it, builds the lattice, embeds every vertex, and checks that the embedding
reproduces the model at rest and moves it rigidly with a rigid lattice. From the
development install:

| Model | Canopies | Vertices | Fit error | Symmetry |
|---|---|---|---|---|
| Stock Mk16, Mk2-R, Mk25, Mk12-R, Mk16-XL | 1 | 532-728 | 1.0-1.1% | 1.02-1.04 |
| RealChute single (both) | 1 | 1,209 | 1.0% | 1.02 |
| RealChute triple (both) | 3 | 3,619 | 1.1% | 1.02-1.03 |
| ReStock Mk16, Mk2-R, Mk12-R | 1 | 2,604-3,084 | 0.5-0.6% | 1.00 |
| ReStock Mk16-XL | 3 | 9,360 | 1.0% | 1.02-1.03 |
| ReStock Mk25 | 2 | 5,316 | 0.8% | 1.02 |
| Custom Parachute Message "Encoded" | 1 | 1,499 | 0.4% | 1.01 |

Side views of every fit land in `build/fits/`.

**Pictures** (`Tools/Render.cs`) draws the solver's canopies - the images in this document
and the README come from it, as does the stock Mk16 mesh deformed in a crosswind.

## Known limits

- Round canopies only. Parafoils - including the stock EVA parachute - and anything else
  that is not a surface of revolution keep their module's own animation.
- The canopy does not collide with itself (only with other canopies).
- Collisions are one-way by design: canopies never push craft.
- Scatterer wave sampling depends on Scatterer's internals; a Scatterer update that renames
  them turns it off (with a line in the log) rather than breaking anything.
- Kerbal Weather Project computes one wind vector for the active vessel's location;
  canopies on other vessels in physics range see the same wind, exactly as KWP's own
  aerodynamics do.
