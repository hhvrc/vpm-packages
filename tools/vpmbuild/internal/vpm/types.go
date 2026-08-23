package vpm

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

// Manifest is a VPM package manifest (Packages/<id>/package.json).
//
// It is a superset of Unity's UPM manifest: `unity`, `displayName`, `description`,
// `samples` and friends come from UPM, while `vpmDependencies`, `url`, `zipSHA256`,
// `legacyFolders`, `legacyFiles` and `legacyPackages` are VPM additions.
//
// Unknown fields are preserved through Extra so third-party conventions
// (VRLabs' vccRepoCategory, media.previewImage, ...) survive a round-trip.
type Manifest struct {
	Name             string            `json:"name"`
	DisplayName      string            `json:"displayName,omitempty"`
	Version          string            `json:"version"`
	Unity            string            `json:"unity,omitempty"`
	Description      string            `json:"description,omitempty"`
	License          string            `json:"license,omitempty"`
	Author           *Author           `json:"author,omitempty"`
	VPMDependencies  map[string]string `json:"vpmDependencies,omitempty"`
	Dependencies     map[string]string `json:"dependencies,omitempty"`
	LegacyFolders    map[string]string `json:"legacyFolders,omitempty"`
	LegacyFiles      map[string]string `json:"legacyFiles,omitempty"`
	LegacyPackages   []string          `json:"legacyPackages,omitempty"`
	ChangelogURL     string            `json:"changelogUrl,omitempty"`
	DocumentationURL string            `json:"documentationUrl,omitempty"`
	URL              string            `json:"url,omitempty"`
	ZipSHA256        string            `json:"zipSHA256,omitempty"`
	HideInEditor     *bool             `json:"hideInEditor,omitempty"`

	Extra map[string]json.RawMessage `json:"-"`
}

type Author struct {
	Name  string `json:"name"`
	Email string `json:"email,omitempty"`
	URL   string `json:"url,omitempty"`
}

// known mirrors the tagged fields above; anything else lands in Extra.
var known = map[string]bool{
	"name": true, "displayName": true, "version": true, "unity": true,
	"description": true, "license": true, "author": true, "vpmDependencies": true,
	"dependencies": true, "legacyFolders": true, "legacyFiles": true,
	"legacyPackages": true, "changelogUrl": true, "documentationUrl": true,
	"url": true, "zipSHA256": true, "hideInEditor": true,
}

func (m *Manifest) UnmarshalJSON(b []byte) error {
	type alias Manifest
	var a alias
	if err := json.Unmarshal(b, &a); err != nil {
		return err
	}
	*m = Manifest(a)

	var all map[string]json.RawMessage
	if err := json.Unmarshal(b, &all); err != nil {
		return err
	}
	for k, v := range all {
		if !known[k] {
			if m.Extra == nil {
				m.Extra = map[string]json.RawMessage{}
			}
			m.Extra[k] = v
		}
	}
	return nil
}

func (m Manifest) MarshalJSON() ([]byte, error) {
	type alias Manifest
	b, err := json.Marshal(alias(m))
	if err != nil {
		return nil, err
	}
	if len(m.Extra) == 0 {
		return b, nil
	}
	var merged map[string]json.RawMessage
	if err := json.Unmarshal(b, &merged); err != nil {
		return nil, err
	}
	for k, v := range m.Extra {
		if _, clash := merged[k]; !clash {
			merged[k] = v
		}
	}
	return json.Marshal(merged)
}

// Listing is the JSON document VCC and ALCOM fetch from the repository URL.
type Listing struct {
	Name     string                    `json:"name"`
	ID       string                    `json:"id"`
	URL      string                    `json:"url"`
	Author   string                    `json:"author"`
	Packages map[string]*ListedPackage `json:"packages"`

	// Published carries release dates for the generated site. It is not part of
	// the VPM format, so it never reaches index.json.
	Published map[string]map[string]time.Time `json:"-"`
}

type ListedPackage struct {
	Versions map[string]*Manifest `json:"versions"`
}

// Config is listing.json at the repo root: everything about this listing that
// is not derived from the packages themselves.
type Config struct {
	Name          string `json:"name"`
	ID            string `json:"id"`
	URL           string `json:"url"`
	Author        string `json:"author"`
	AuthorURL     string `json:"authorUrl,omitempty"`
	Description   string `json:"description,omitempty"`
	SiteURL       string `json:"siteUrl,omitempty"`
	GitHubRepo    string `json:"githubRepo"`
	PackagesDir   string `json:"packagesDir"`
	PackagePrefix string `json:"packagePrefix,omitempty"`
	// Namespace is the C# root namespace and asmdef prefix new packages get.
	// Derived casing from the package id would give "Heavenvr", so it is set
	// explicitly.
	Namespace string `json:"namespace,omitempty"`
}

func LoadConfig(path string) (*Config, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var c Config
	if err := json.Unmarshal(b, &c); err != nil {
		return nil, fmt.Errorf("%s: %w", path, err)
	}
	if c.PackagesDir == "" {
		c.PackagesDir = "Unity/Packages"
	}
	return &c, nil
}

func LoadManifest(path string) (*Manifest, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	var m Manifest
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, fmt.Errorf("%s: %w", path, err)
	}
	return &m, nil
}

// LocalPackages returns every package directory under the configured packages
// directory that carries a package.json and matches the configured prefix.
func (c *Config) LocalPackages(root string) (map[string]*Manifest, error) {
	dir := filepath.Join(root, filepath.FromSlash(c.PackagesDir))
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil, err
	}
	out := map[string]*Manifest{}
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		if c.PackagePrefix != "" && !hasPrefix(e.Name(), c.PackagePrefix) {
			continue
		}
		mp := filepath.Join(dir, e.Name(), "package.json")
		if _, err := os.Stat(mp); err != nil {
			continue
		}
		m, err := LoadManifest(mp)
		if err != nil {
			return nil, err
		}
		out[e.Name()] = m
	}
	return out, nil
}

func (c *Config) PackageDir(root, id string) string {
	return filepath.Join(root, filepath.FromSlash(c.PackagesDir), id)
}

func hasPrefix(s, p string) bool { return len(s) >= len(p) && s[:len(p)] == p }

// ReleaseTag is the git tag a package version is published under. One monorepo
// holds many packages, so the id has to be part of the tag. A '-' separator is
// deliberate: slashes in tags survive git fine but land percent-encoded in
// release asset URLs, which some clients then fail to follow.
func ReleaseTag(id, version string) string { return id + "-" + version }

func ZipName(id, version string) string { return id + "-" + version + ".zip" }
