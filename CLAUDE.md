# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

The HeavenVR VPM (VRChat Package Manager) listing: a Go generator that turns
GitHub releases into the static `index.json` served at https://vpm.heavenvr.tech,
plus the Unity 2022.3.22f1 project where the packages are developed.

## Packages

- `tech.heavenvr.importguard` -- ImportGuard. Inspects `.unitypackage` imports
  before they land; password-protected packages are planned, not built. Renamed from
  PackageGuard before first release, so no `legacyPackages` migration is needed.
  The password feature is a distribution gate, not DRM: the ciphertext and the
  code that decrypts it both ship to the user, so derive the key from the
  password and do not store it, but do not invest in hardening past that.

## Invariants

- **A published release is permanent.** The listing is rebuilt from releases, so
  deleting one removes a version users have locked in `vpm-manifest.json`.
  Bump versions, never re-tag.
- **This repo emits data, not pages.** `vpmbuild listing` writes `index.json`
  (spec-shaped, never add fields to it) and `site.json` (presentation: README
  HTML, release dates, source URLs). The pages live in the SvelteKit app at
  `heavenvr.tech/vpm`; `vpm.heavenvr.tech/` only redirects there.
- **The listing is a static file.** No server, no database. If dynamic behaviour
  is ever wanted (private packages, download stats), that belongs in the existing
  C# API at `api.heavenvr.tech`, not here.
- **Every file in a package needs a `.meta`.** Missing metas mean new GUIDs on
  every user's machine, which silently breaks their references on update.
  `vpmbuild validate` treats this as fatal.
- **ALCOM is the strict client.** It drops malformed packages without an error
  message where VCC tolerates them. Validate against ALCOM's rules, not VCC's.

## Commands

```bash
go run ./tools/vpmbuild validate          # check every local package
go run ./tools/vpmbuild listing -local    # preview from the working tree
go run ./tools/vpmbuild listing           # real build; needs GITHUB_TOKEN
go run ./tools/vpmbuild new <id>          # scaffold a package
go vet ./tools/vpmbuild/...
```

`vpmbuild` finds `listing.json` by walking up from the working directory, so it
runs from anywhere in the repo.

## Conventions

- Package ids: `tech.heavenvr.<name>`, lowercase, no separators inside the name
  (`importguard`, not `import-guard`). Pass `-asm ImportGuard` to `vpmbuild new`
  so the asmdef and namespace get the right casing, since the id cannot carry it.
- Release tags: `<package-id>-<version>` (dashes, not slashes -- slashes in tags
  end up percent-encoded in asset URLs).
- C# namespaces and asmdefs: `HeavenVR.<PascalName>[.Editor]`, from
  `namespace` in `listing.json`.
- Zips are built deterministically (fixed 1980 timestamps, sorted entries) so the
  same tree always produces the same `zipSHA256`.
- The Go tool uses the standard library only. Keep it that way.
- Licensing is GPL-3.0-or-later: repo root `LICENSE`, plus a `LICENSE.md` inside
  every package (packages get extracted into user projects on their own), and
  `"license": "GPL-3.0-or-later"` in each `package.json`. Copying GPL code into a
  package is fine; copying it into anything shipped under other terms is not.
