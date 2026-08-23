package vpm

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// BuildListing reconstructs the whole listing from published GitHub releases.
//
// Releases are the single source of truth on purpose: nothing is carried over
// from a previous index.json, so the listing cannot drift, and a rebuild after
// any failure produces the identical file. The rule that follows from it is
// that a published release must never be deleted -- doing so removes a version
// from the listing and breaks every project whose vpm-manifest.json locks it.
func BuildListing(cfg *Config, includePrerelease bool) (*Listing, []string, error) {
	gh := NewGitHub(cfg.GitHubRepo)
	releases, err := gh.Releases()
	if err != nil {
		return nil, nil, err
	}

	listing := &Listing{
		Name:      cfg.Name,
		ID:        cfg.ID,
		URL:       cfg.URL,
		Author:    cfg.Author,
		Packages:  map[string]*ListedPackage{},
		Published: map[string]map[string]time.Time{},
	}
	var notes []string

	for _, r := range releases {
		if r.Prerelease && !includePrerelease {
			notes = append(notes, fmt.Sprintf("skipped prerelease %s", r.TagName))
			continue
		}
		manifestAsset, zipAsset := pickAssets(r)
		if manifestAsset == nil || zipAsset == nil {
			notes = append(notes, fmt.Sprintf("skipped %s: needs both a package.json and a .zip asset", r.TagName))
			continue
		}
		m, err := gh.FetchManifest(manifestAsset.BrowserDownloadURL)
		if err != nil {
			return nil, nil, err
		}
		if m.Name == "" || m.Version == "" {
			notes = append(notes, fmt.Sprintf("skipped %s: manifest has no name or version", r.TagName))
			continue
		}
		// The zip URL from the release wins over whatever was baked in at pack
		// time, so a renamed repo or a re-uploaded asset self-heals.
		m.URL = zipAsset.BrowserDownloadURL

		p := listing.Packages[m.Name]
		if p == nil {
			p = &ListedPackage{Versions: map[string]*Manifest{}}
			listing.Packages[m.Name] = p
		}
		if _, dup := p.Versions[m.Version]; dup {
			notes = append(notes, fmt.Sprintf("duplicate %s %s (tag %s) ignored", m.Name, m.Version, r.TagName))
			continue
		}
		p.Versions[m.Version] = m
		if listing.Published[m.Name] == nil {
			listing.Published[m.Name] = map[string]time.Time{}
		}
		listing.Published[m.Name][m.Version] = r.CreatedAt
	}
	return listing, notes, nil
}

// BuildLocalListing builds a listing from the working tree instead of from
// published releases. The download URLs it writes point at releases that may
// not exist yet, so this is for previewing the site and eyeballing the JSON --
// never for publishing.
func BuildLocalListing(cfg *Config, root string) (*Listing, error) {
	pkgs, err := cfg.LocalPackages(root)
	if err != nil {
		return nil, err
	}
	l := &Listing{
		Name:      cfg.Name,
		ID:        cfg.ID,
		URL:       cfg.URL,
		Author:    cfg.Author,
		Packages:  map[string]*ListedPackage{},
		Published: map[string]map[string]time.Time{},
	}
	for id, m := range pkgs {
		m.URL = DownloadURL(cfg.GitHubRepo, id, m.Version)
		l.Packages[id] = &ListedPackage{Versions: map[string]*Manifest{m.Version: m}}
	}
	return l, nil
}

func pickAssets(r Release) (manifest, zip *Asset) {
	for i := range r.Assets {
		a := &r.Assets[i]
		switch {
		case a.Name == "package.json":
			manifest = a
		case strings.HasSuffix(a.Name, ".zip") && zip == nil:
			zip = a
		}
	}
	return manifest, zip
}

func WriteListing(l *Listing, path string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	b, err := json.MarshalIndent(l, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, append(b, '\n'), 0o644)
}

// Latest returns each package's newest version, for the landing page.
func (l *Listing) Latest() []*Manifest {
	var out []*Manifest
	for _, p := range l.Packages {
		var versions []string
		for v := range p.Versions {
			versions = append(versions, v)
		}
		sorted := SortVersionsDesc(versions)
		if len(sorted) == 0 {
			continue
		}
		out = append(out, p.Versions[sorted[0]])
	}
	for i := 1; i < len(out); i++ {
		for j := i; j > 0 && strings.ToLower(displayOf(out[j])) < strings.ToLower(displayOf(out[j-1])); j-- {
			out[j], out[j-1] = out[j-1], out[j]
		}
	}
	return out
}

func displayOf(m *Manifest) string {
	if m.DisplayName != "" {
		return m.DisplayName
	}
	return m.Name
}

// VersionsOf lists a package's versions newest first.
func (l *Listing) VersionsOf(id string) []string {
	p := l.Packages[id]
	if p == nil {
		return nil
	}
	var versions []string
	for v := range p.Versions {
		versions = append(versions, v)
	}
	return SortVersionsDesc(versions)
}
