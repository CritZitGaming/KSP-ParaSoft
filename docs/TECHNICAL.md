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

- **Fabric vs lines.** Lines and risers converge towards the canopy transform's origin,
  fabric sits far out, so vertices beyond 45% of the farthest distance are fabric.
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
- **Where the lines meet.** Few models bring the lines right to the part. Most gather them
  a few metres out and hang that point from the part on a riser: 2.3 m on the stock Mk16,
  4-5 m on RealChute's and ReStock's, 5-8 m on Boring Crew Services' Starliner chutes,
  9.7 m on Custom Parachute Message's canopy. In a cluster the meeting point is off each
  canopy's own axis. The lowest line vertex says nothing, because the riser is lower still.
  So every line vertex is taken to lie on a straight line from the hem, and the point
  nearest all of those lines is found by least squares. A first guess comes from extending
  each vertex down its line to the axis and taking the median. It is then refined in 3D:
  for each vertex, the ray from the current guess out through it says which hem point its
  line comes from, the riser and fittings on the part's side are excluded, and outliers
  are trimmed. This works whether a line is modelled with vertices all along it or only
  at its two ends, as the Starliner's are.
- **Shared risers.** If a cluster's canopies meet at one point (within a tenth of a
  radius), they are given exactly that point and the solver keeps their confluences
  together. If each has its own riser but the risers share a strap from the part (ReStock's
  Mk16-XL and Mk25 join theirs 1.4 m out, the Starliner at its swivel 0.7 m out), the
  junction is where vertices stop hugging the cluster's mean line.

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

The riser is not cloth. It is a strap that the canopy's pull holds straight, so the
confluence is simply kept within the riser's length of the anchor. Once cut, the riser
trails loose below the confluence.

## Embedding: making the original mesh follow

The player sees the part's own canopy mesh, not the lattice. `CanopyEmbedding` ties each
of its vertices to the lattice:

- a **fabric** vertex by where it sits on the canopy surface - its angle around the axis
  gives a fractional gore, its nearest point on the profile a fractional ring - plus its
  offset from the bilinearly interpolated lattice surface in that spot's local frame
  (hoop direction, meridian direction, normal);
- a **line** vertex by how far it is down the lines and which two lines it sits between.
  Which lines is found by following the ray from the confluence out through the vertex to
  the hem, since the confluence need not be on the axis;
- a **riser** vertex by how far it is along the riser. That riser runs from the part (or
  where a cut riser trails to), through the cluster's shared junction if there is one, to
  the confluence. The junction lies out along the canopies' combined pull.

Normals and tangents are stored in the same local frames. Evaluating on the rest lattice
reproduces the model to about a ten-millionth of the canopy radius, and moving the whole
lattice rigidly moves every vertex rigidly - both checked on every model in the install.
Because offsets live in local frames, seams, scallops, double-sided fabric and lines
modelled as thin tubes all keep their shape as the canopy moves.

That only holds if each cord vertex was matched to a cord that is really where it is.
A line vertex half a metre from its lattice line is carried half a metre off in whatever
direction that line's frame turns, and a thin cord becomes a ribbon. So the model report
also checks how far each line and riser vertex sits from its cord: under 14 cm for lines
and 6 cm for risers in every model tested. In 1.0, which assumed the lines met at the
part, it was up to 2 m.

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
plate      = 1 - fill / (1 - porosity)                (0 once the canopy is full)
dp = fill x q_inf x (Cin - Cout)  +  plate x q_local x Cn
```

`Cin = 1` (stagnation), `Cbase = 0.4` (wake suction), `Cw = 1`, `Cflat = 1.2`. A fully
inflated hemisphere facing the flow gets a uniform 1.4 q across it - a drag coefficient of
1.4 on projected area, which is what real hemispherical canopies measure. A limp canopy
streaming in the flow gets only flat-plate forces, and flutters. Skin friction
(`Cf = 0.015`) acts along the fabric, and lines get cylinder crossflow drag.

The plate term fades out as the canopy fills. In 1.0 it was weighted `1 - fill`, which left
8% of it on a full canopy. On a small, light canopy at speed that made the panels flutter
and rectified into a steady sideways force: a 4 m drogue on 13 m of line at 42 m/s glided
off 30 degrees to one side and stayed there.

**Stability.** Pressure on the cloth acts along each panel's normal, so on a full canopy it
adds up to a pull along the canopy's own axis, whichever way the air meets it. Nothing
then turns the canopy back into the wind, and nothing damps its swinging: it drifts and
swings like a solid canopy with no vent. Real round canopies with a vent and porous cloth
pull almost straight along the relative wind. So every substep the net pressure force on
the fabric is compared with the same force pointed along the air the canopy actually
meets, which includes its own sideways motion. `stability` (0.8) of the difference is
spread over the fabric by area. A canopy swinging sideways meets the air at an angle and is
pulled back. Across radii of 2-9 m, lines of 2.4-6 radii and speeds of 7-150 m/s, an open
canopy started 20 degrees out settles within 1-2 degrees of the flow; a big canopy at a
slow descent still sways a few degrees in gusts, as real ones do.

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
it is, summed from the actual segment lengths. The riser comes out first, then the lines,
then the hem, then each ring to the crown, each let go when the bag is far enough away to
have pulled it out. A cluster's canopies leave in their own bags, fanned out as the
model spreads them, so they do not open inside one another.
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
riser lets go of the part and the canopy flies on by itself. With no anchor, its frame's
origin is kept relative to the planet's centre - which floating origin and Krakensbane
move along with everything else - and its velocity follows the canopy's, re-centring if
it drifts. A part destroyed with its canopy out does the same.

A cut canopy is removed at the first of: `cutCanopyRange` (750 m) from both the camera and
the active craft; 15 seconds lying still (under 1 m/s, empty) on the ground or the sea; or
the "Cut canopies linger" time (30 s).

## Environment

Every physics frame, per canopy:

| Quantity | Source |
|---|---|
| Anchor velocity | the part's rigidbody point velocity (its parent's, for physicsless parts) + Krakensbane frame velocity, smoothed over 50 ms so a light part ringing on its joint does not buzz through the lines |
| Air velocity | zero in KSP's rotating frame, the surface's rotation velocity when it is inertial, plus the craft's share of the wind (below) |
| Wind | Kerbal Weather Project's world-space wind vector (`KerbalWxClimo`/`KerbalWxPoint.windVectorWS`, whichever mode is on) - the same vector KWP subtracts from part velocities in its own aerodynamics; otherwise any wind function registered with FAR's `FARAtmosphere.GetWind` |
| Density | the part's `atmDensity`, else the vessel's |
| Speed of sound | the vessel's |
| Gravity | `FlightGlobals.getGeeForceAtPosition` |

The fictitious forces of KSP's rotating frame act on the craft and canopy alike and are
left out: they would only shift the canopy relative to the craft by millimetres.

### How much of the wind

A canopy's lines point the way it pulls the craft. ParaSoft never pulls the craft, so the
canopy must agree with the drag the parachute module applies, and wind is where the two
can disagree. FAR applies the whole wind to every part. Kerbal Weather Project in stock
aerodynamics adds a wind correction per part but limits it by the part's mass, so a
parachute - a 20 kg part carrying the craft's whole weight - gets only a few percent of
it. RealChute computes its drag with no wind at all. A canopy that felt the whole wind
regardless would lean 50 degrees downwind above a craft whose parachute drag points
straight up.

So `CraftFlow` watches each craft. Its centre-of-mass acceleration, less gravity, the
rotating frame's fictitious accelerations and engine thrust, smoothed over 0.3 s, is what
the air is doing to it. The air must be blowing past the craft opposite to that push.
`WindResponse` finds the share `f` of the wind for which `velocity - f x wind` best lines
up against it, by least squares on the part across the push, clamped to 0..1. That share
is what its canopies are given: 1 under FAR, near 0 with RealChute, in between in stock
with KWP. It is trusted only while the air is clearly doing the pushing (at least 0.15 g
of it, and not on the ground); otherwise it relaxes back to the whole wind. So a landed
canopy on the ground feels the real wind, and so does a cut canopy, which is free.

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

**Canopies against canopies.** Once a frame, before anything steps, every open canopy's
contact volume is gathered in world space. That volume is a capsule from the hem's centre
towards the crown, 0.95 times as wide as the widest ring: nearly a sphere for an open
canopy, long and thin for a streamer. Each canopy is then given the others near it,
moved into its own frame: its cluster-mates, the craft's other chutes, other craft's, and
cut canopies drifting past. Two things follow:

- fabric that strays into another canopy's volume is pushed out every substep, with light
  friction - but only if the push dents it back towards its own canopy. Where two canopies
  overlap deeply, pushing the far side of one out of the other would drape it round the
  other and hold them together;
- a canopy crowding another (their volumes within 1.1 times their combined radii) is
  pushed away from it across the flow, 2.5 times its drag area times dynamic pressure
  at full overlap, spread over its fabric. That is the air squeezed out between cluster
  canopies, and it is what spreads a cluster: three canopies released from the same bag
  end up about 20 degrees apart, just touching.

A cluster whose lines meet at one point has its confluences put back together after each
step, where the pulls balance.

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
| Solver, per canopy per physics frame | 0.14 ms | 0.32 ms | 0.56 ms |

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
the canopy about 56 degrees downwind, as the relative wind predicts; reefing holds the hem
to under 3% of its area and disreefing opens it; a landed canopy lies flat and empties;
fabric floats on waves; an obstacle is flowed around; a cut canopy flies away; Mach 1.9 and
a 250 m/s sea-level opening need no recoveries; a sudden change in the craft's velocity
drags the canopy rather than teleporting it; embedded mesh vertices stay on the simulated
canopy; a small drogue on long lines at 42 m/s stays within 5 degrees of the flow (it
glided off 30 in 1.0); a canopy swung 20 degrees out comes back; three canopies released
from one bag spread apart without overlapping; a riser pays out first and then holds the
confluence at its length; the fitter finds a riser, and a cluster's shared strap, in
models built with lines drawn only at their ends; the wind share comes out right for
crafts feeling none, some and all of the wind; and the timings above.

**Real models** (`Tools/ModelReport.cs`, with a KSP install): reads each parachute model
with a small `.mu` reader (`Tools/MuModel.cs`), plays its deploy clips, skins and bakes
it, fits it, builds the lattice, embeds every vertex, and checks that the embedding
reproduces the model at rest, moves it rigidly with a rigid lattice, and puts every line
and riser vertex on its cord. From the development install:

| Model | Canopies | Vertices | Fit error | Symmetry | Riser | Cord offset (line / riser) |
|---|---|---|---|---|---|---|
| Stock Mk16 | 1 | 532 | 1.0% | 1.02 | 2.25 m | 0.00 / 0.06 m |
| Stock Mk2-R, Mk12-R, Mk16-XL | 1 | 720 | 1.0% | 1.03 | none | 0.01 / - m |
| Stock Mk25 | 1 | 728 | 1.1% | 1.04 | 4.3 m | 0.01 / 0.01 m |
| RealChute single (both) | 1 | 1,209 | 1.0% | 1.02 | 4.95 m | 0.01 / 0.01 m |
| RealChute triple (both) | 3 | 3,619 | 1.1% | 1.02-1.03 | 4.3 m each | 0.01 / 0.01 m |
| ReStock Mk16, Mk2-R, Mk12-R | 1 | 2,604-3,084 | 0.5-0.6% | 1.00 | 4.7 m | 0.01-0.04 / 0.03 m |
| ReStock Mk16-XL | 3 | 9,360 | 1.0% | 1.02-1.03 | 5.5 m, joined at 1.4 m | 0.10 / 0.03 m |
| ReStock Mk25 | 2 | 5,316 | 0.8% | 1.02 | 5.6 m, joined at 1.4 m | 0.08 / 0.03 m |
| Custom Parachute Message "Encoded" | 1 | 1,499 | 0.4% | 1.01 | 9.7 m | 0.04 / 0.05 m |
| Boring Crew Services Starliner main | 3 | 33,719 | 0.7% | 1.00 | 8.1 m, joined at 0.7 m | 0.14 / 0.05 m |
| Boring Crew Services Starliner drogue | 2 | 5,928 | 0.7% | 1.00 | 5.35 m | 0.02 / 0.01 m |

The Starliner models are fitted when Boring Crew Services is installed; their lines are
modelled with vertices only at their two ends, the case the 3D confluence search exists for.

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
  aerodynamics do (and as KWP applies no wind to those vessels, their share comes out
  near none).
- Risers are straight straps, not cloth: a slack riser shortens rather than sags.
- The wind share is worked out from the craft's whole acceleration, less gravity and
  engine thrust. RCS thrust, collisions and lift from the craft's own body are not
  separated out. They can only move the share between none and all of the wind, and the
  share is only trusted while the air clearly dominates.
