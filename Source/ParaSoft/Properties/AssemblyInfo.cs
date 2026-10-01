using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("ParaSoft")]
[assembly: AssemblyDescription("Softbody parachute canopies for Kerbal Space Program - ParaSoft Airbraking Technologies")]
[assembly: AssemblyProduct("ParaSoft Airbraking Technologies")]
[assembly: AssemblyCopyright("Copyright (c) 2026 CritZitGaming")]
[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

// No KSPAssemblyDependency on RealChute, FAR, Kerbal Weather Project or Scatterer: every
// one of them is optional and found by reflection at runtime, so a hard reference would
// only stop ParaSoft loading for players who do not have them.
[assembly: KSPAssembly("ParaSoft", 1, 1)]
