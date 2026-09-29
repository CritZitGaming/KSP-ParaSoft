# CKAN metadata

`ParaSoft.netkan` is this mod's CKAN indexing metadata. It lives here so it is version
controlled alongside the mod, but **CKAN does not read it from this repo** — it has to be
submitted to the CKAN metadata index.

## Prerequisite

A published GitHub release with an attached zip named `ParaSoft-<version>.zip` containing
`ParaSoft/` at the zip root. The `Release` workflow in `.github/workflows/` builds and
attaches this automatically when you push a `v<version>` tag.

## Getting listed — two routes

CKAN requires the mod author's permission to index a mod. You are the author, so say so
either way.

**Pull request (uses this file as written):**

1. Fork <https://github.com/KSP-CKAN/NetKAN>.
2. Copy `ParaSoft.netkan` into that fork's `NetKAN/` directory.
3. Open a pull request. CI validates and inflates the file on the PR; if it builds cleanly
   a maintainer merges it.

Note that NetKAN requires all contributions to that repo be made under CC-0. That covers
the metadata file only, not the mod.

**Issue (CKAN's "express" route):**

Open an issue on <https://github.com/KSP-CKAN/NetKAN/issues> giving the repo URL, that
you're the author, and the dependencies. The CKAN team writes the metadata for you. Slower,
but you don't have to get the netkan right yourself.

Either way, once it's merged the indexer picks up every future release automatically.

## A note on `homepage`

CKAN's guide says `homepage` should point to a KSP forum support thread. There isn't one,
so it currently points at the GitHub repo. If you post a release thread on the KSP forum,
change `homepage` to that URL — it's what CKAN shows players looking for support.

## A note on `depends`

ModuleManager is a hard dependency, not a recommendation. Without it the part module is
never attached to anything and the mod does nothing at all, silently. Listing it under
`depends` is what stops a player installing this on its own and wondering why nothing
happens.

Everything else ParaSoft works with - RealChute, FAR, Kerbal Weather Project, Scatterer,
Custom Parachute Message, ReStock, KerbalFX - is under `supports`, not `recommends`: each
is found at runtime if present, and none of them is something a player should be nudged
into installing for a parachute mod.

## How the pieces connect

| Field | Reads from | Must stay in sync with |
|---|---|---|
| `$kref` … `version_from_asset` | the release asset filename | the zip name the workflow builds |
| `$vref` `ksp-avc` | `ParaSoft/ParaSoft.version` inside the zip | the git tag, minus its leading `v` |
| `install: find: ParaSoft` | the `ParaSoft` directory in the zip | the mod's folder name |

The version number therefore appears in four places for every release — the git tag, the zip
filename, `ParaSoft.version`, and `AssemblyInfo.cs`. The release workflow fails the build if
any of them disagree.

## After the first release

Nothing further is needed. Once the netkan is merged, tagging a new release is enough — the
CKAN indexer polls GitHub and publishes the new version on its own.

## When the listing is approved

The root `README.md` carries a "not on CKAN yet" notice in two places — a banner under the
badges and a note in the Install section. Both are wrapped in markers:

```
<!-- CKAN-PENDING:START ... -->
<!-- CKAN-PENDING:END -->
```

Delete everything between and including those markers. What's left underneath is already
written for the approved state, so nothing else needs editing.

```bash
grep -n "CKAN-PENDING" README.md
```
