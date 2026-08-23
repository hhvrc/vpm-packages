# HeavenVR VPM Listing

The VRChat package listing served at **https://vpm.heavenvr.tech**, plus the Unity
project the packages are developed in.

Users install packages by adding one URL to VCC or ALCOM:

```
https://vpm.heavenvr.tech/index.json
```

## Layout

```
listing.json                     listing metadata (name, id, url, github repo)
Unity/                           the package development project (Unity 2022.3.22f1)
  Assets/                        scratch space: test avatars and scenes, never shipped
  Packages/
    tech.heavenvr.importguard/  ImportGuard: guards risky .unitypackage imports
    tech.heavenvr.*/             the packages this repo publishes -- tracked in git
    <everything else>/           VPM dependencies, restored by VCC/ALCOM, gitignored
    vpm-manifest.json            which VPM packages the dev project pulls in
site/                            extra files copied verbatim into the deploy (_headers)
wrangler.jsonc                   Cloudflare Worker that serves the built listing
tools/vpmbuild/                  the Go listing generator
```

Open `Unity/` in ALCOM (Add Project → point at it), not the repo root.

## How it works

VPM is VRChat's layer on top of Unity's package manager (UPM). A package is a
normal UPM package -- `package.json`, `Editor/`, `.meta` files -- and a *listing*
is one static JSON file mapping package ids to every published version of their
manifest, each with a `url` pointing at a downloadable zip.

Nothing is uploaded to VRChat. VCC/ALCOM fetch the listing, download the zip, and
unpack it into the user's `Packages/<id>/`.

This repo's flow:

1. Packages live in `Unity/Packages/tech.heavenvr.*` and are edited in place.
2. `Release package` (manual workflow) zips one package and attaches the zip plus
   its `package.json` to a GitHub release tagged `<id>-<version>`.
3. `Publish listing` rebuilds `index.json` **from scratch** by reading every
   GitHub release, then deploys it to Cloudflare.

Because step 3 derives everything from releases, the listing can always be
regenerated and can never drift from what is actually downloadable.

> **Never delete a published release.** Its version disappears from the listing,
> and every project whose `vpm-manifest.json` locks that version breaks.

## Surfaces

The listing and the pages people read live on different hosts on purpose.

| | Host | Serves |
| --- | --- | --- |
| Machine | `vpm.heavenvr.tech/index.json` | the public VPM listing |
| Machine | `vpm.heavenvr.tech/site.json` | README HTML and release dates for the site |
| Machine | `vpm.heavenvr.tech/u/<token>/...` | per-user listings and downloads (planned) |
| Human | `heavenvr.tech/vpm` | package browsing, install buttons, docs, tokens |

`vpm.heavenvr.tech/` redirects to `heavenvr.tech/vpm` (see `site/_redirects`),
because a listing URL pasted into a browser has to land somewhere useful.

Splitting them keeps package installs independent of the frontend: a site deploy
cannot break `index.json`, and the human pages get the real design system and the
existing WebAuthn login, which per-user private listings will need.

`vpmbuild listing` therefore emits data, not pages:

```
dist/index.json    the VPM listing, exactly spec-shaped
dist/site.json     per package: rendered README HTML, source URL, release dates
dist/_headers      content types, caching, CORS
dist/_redirects    / -> heavenvr.tech/vpm
```

`site.json` exists so the frontend renders a package page from one fetch and
never touches the GitHub API. Read both server-side in `+page.server.ts`: the
site's CSP `connect-src` does not allow `vpm.heavenvr.tech`, and server-side
fetches are edge-cacheable anyway.

## Working on a package

```bash
go build -o tools/vpmbuild/vpmbuild.exe ./tools/vpmbuild   # or use `go run ./tools/vpmbuild`

vpmbuild new tech.heavenvr.thing -display "Thing" -description "Does a thing."
vpmbuild validate                  # run before every release
vpmbuild listing -local            # preview index.json + site.json from the working tree
vpmbuild pack tech.heavenvr.thing  # build the release zip locally
```

`validate` enforces the rules that make a package silently vanish from ALCOM
(which is stricter than VCC): id must match the folder, lowercase, valid semver,
and every file needs a committed `.meta` so GUIDs stay stable across installs.

## Releasing

1. Bump `version` in the package's `package.json` and add a `CHANGELOG.md` entry.
2. Commit and push to `master`.
3. Actions → **Release package** → enter the package id → Run.
4. **Publish listing** fires automatically on the new release and redeploys.

## Hosting

`wrangler.jsonc` deploys `dist/` as an assets-only Cloudflare Worker bound
to `vpm.heavenvr.tech`. Required repo secrets: `CLOUDFLARE_API_TOKEN`,
`CLOUDFLARE_ACCOUNT_ID`.

This is the **interim** host. The listing is moving to `HeavenVr.Api`, which will
serve both the public listing and per-user private listings from the same place
(see below). Package zips stay on GitHub releases either way -- only who renders
the JSON changes.

## Private packages (planned)

Private packages cannot live in this repo: a public repo's releases are public
downloads. They get a second, private repo with the same layout and the same
`vpmbuild` tooling, cloned into `Unity/Packages/` so both are developed in one
Unity project (the public repo gitignores anything that is not `tech.heavenvr.*`
and tracked).

Serving, once the API side lands:

| URL | Contents |
| --- | --- |
| `vpm.heavenvr.tech/index.json` | public packages |
| `vpm.heavenvr.tech/u/<token>/index.json` | public + whatever that token is entitled to |
| `vpm.heavenvr.tech/u/<token>/dl/<id>-<version>.zip` | 302 to a short-lived GitHub asset URL |

The token sits in the path rather than in a request header. VCC and ALCOM both
support custom headers, and vrc-get provably sends them on the zip download too
(`package_installer.rs` merges repository and per-package headers), but
per-package `headers` is a vrc-get extension VRChat does not document, and VCC's
downloader is closed source. A token in the URL needs no client support at all
and works in `curl`.

## Adding the listing to VCC / ALCOM

Settings → Packages → Add Repository → paste the URL, or use the deep link the
landing page exposes:

```
vcc://vpm/addRepo?url=https%3A%2F%2Fvpm.heavenvr.tech%2Findex.json
```

## License

GPL-3.0-or-later. Each published package also ships its own `LICENSE.md`, since a
package is extracted into a user's project on its own, away from this repo.
