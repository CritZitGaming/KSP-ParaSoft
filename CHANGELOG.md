# Changelog

All notable changes to ParaSoft Airbraking Technologies.

## 1.1.0 - 2026-09-30

Fixes from the first in-game flights (a Boring Crew Services Starliner, with Kerbal
Weather Project).

- **Lines and risers keep their shape.** Most parachute models hang their lines from a
  riser (shock cord) a few metres out from the part: stock Mk16 and Mk25, RealChute,
  ReStock, Custom Parachute Message and the Starliner's 5-8 m risers. 1.0 assumed the lines
  met at the part, so the riser and the ends of the lines were tied to the wrong places,
  up to 2 m out, and were torn into ribbons and tangles as the canopy moved. The fitter
  now finds the actual 3D point where the lines meet, even off the canopy's axis and for
  lines modelled with vertices only at their ends. Risers are simulated and drawn as
  straps from the part to that point. Where a cluster's risers share one strap from the part
  (ReStock's Mk16-XL and Mk25, the Starliner's swivel), that joint is modelled too. Every
  line vertex in every model tested now sits within 14 cm of its line (was up to 2 m).
- **Canopies trail the airflow instead of hanging off to one side.** A fully open canopy
  was only weakly stable, and a small drogue on long lines glided about 30 degrees off the
  flow at speed. Two fixes: plate drag, meant for loose fabric, no longer acts on a full
  canopy, where it made the panels flutter. And an open canopy's pull now lines up with the
  air it actually meets (new `stability` setting, 0.8), which also damps its swinging. The
  same drogue now holds within half a degree.
- **Wind moves the canopy only as much as it moves the craft.** ParaSoft never pushes the
  craft, so the canopy has to agree with the parachute module's own drag. Kerbal Weather
  Project in stock aerodynamics only applies a fraction of the wind's force to parachutes,
  and RealChute ignores wind, so canopies used to stream off sideways from a craft falling
  straight down. Each craft's own acceleration now tells ParaSoft how much of the wind it
  is really feeling, and its canopies get that share: all of it under FAR, little or none
  with RealChute. Once the craft is down, canopies get the full wind again.
- **Canopies no longer pass through each other.** Each open canopy is now a solid
  volume to every other one - its cluster-mates, the craft's other chutes, other craft's,
  and cut canopies - checked every substep instead of nudged once a frame. Crowded
  canopies are pushed apart across the flow, so clusters spread the way real ones do. A
  cluster's canopies also leave the part fanned out in their own bags.
- **Cut canopies are cleared up.** Besides the time limit, a cut canopy is now removed once
  it is 750 m from both the camera and your craft (`cutCanopyRange` in `Settings.cfg`), or
  after lying still on the ground or the sea for 15 seconds.
- The anchor's velocity is lightly smoothed, so a parachute part's joint vibration no
  longer buzzes through the lines.
- The log now reports each canopy model's riser length.

## 1.0.0 - 2026-09-29

First release.

- Softbody canopies for stock `ModuleParachute`, RealChute's `RealChuteModule` (including
  dual main/drogue parts) and FAR's `RealChuteFAR`. Drag is never touched: the parachute
  module keeps all of its own physics.
- The part's own canopy mesh is deformed, so its textures, RealChute's canopy choices,
  Custom Parachute Message's encoded canopies and TexturesUnlimited recolours carry over.
- Canopy shape read from the model at runtime, including skinned (ReStock) and cluster
  (RealChute triple, ReStock Mk16-XL) canopies. Non-round canopies keep their own animation.
- Realistic deployment: pack ejection, line stretch, streamer, crown-first inflation;
  reefing and disreefing that follow the module's own drag.
- Ram-air inflation with apparent mass, porosity, Mach effects and supersonic breathing;
  behaves differently in thick air, thin air and vacuum with nothing special-cased.
- Wind from Kerbal Weather Project, or any wind registered with FAR; small-scale turbulence.
- Collisions with terrain, KSC buildings, Parallax scatter, parts, kerbals and other
  vessels; floating on the sea, riding Scatterer's waves when its wave interactions are on;
  canopy-to-canopy contact in clusters.
- Cut canopies fly on as debris for a configurable time.
- Settings in the stock difficulty screen; tuning in `Settings.cfg`; KerbalFX AeroFX patch.
