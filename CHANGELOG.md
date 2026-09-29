# Changelog

All notable changes to ParaSoft Airbraking Technologies.

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
