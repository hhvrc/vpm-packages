package vpm

import (
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
	"unicode"
)

// SiteMeta is everything heavenvr.tech/vpm needs to render package pages that
// index.json deliberately does not carry.
//
// index.json is the VPM wire format and stays exactly spec-shaped, so anything
// presentational lives here instead. Shipping it as a second static file means
// the frontend makes one fetch per render and never touches the GitHub API.
type SiteMeta struct {
	Generator string                  `json:"generator"`
	ListingID string                  `json:"listingId"`
	Packages  map[string]*PackageMeta `json:"packages"`
}

type PackageMeta struct {
	// ReadmeHTML is the package README rendered from Markdown. It is generated
	// from repository content, never from user input.
	ReadmeHTML string `json:"readmeHtml,omitempty"`
	SourceURL  string `json:"sourceUrl,omitempty"`
	// Published maps version -> release date, newest versions included.
	Published map[string]time.Time `json:"published,omitempty"`
}

// BuildSiteMeta collects presentation data for every package in the listing.
func BuildSiteMeta(cfg *Config, l *Listing, root string) *SiteMeta {
	meta := &SiteMeta{
		Generator: "vpmbuild",
		ListingID: cfg.ID,
		Packages:  map[string]*PackageMeta{},
	}
	for id := range l.Packages {
		meta.Packages[id] = &PackageMeta{
			ReadmeHTML: Markdown(dropTitle(readme(cfg, root, id))),
			SourceURL:  sourceURL(cfg, id),
			Published:  l.Published[id],
		}
	}
	return meta
}

func WriteSiteMeta(m *SiteMeta, path string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	b, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return err
	}
	return os.WriteFile(path, append(b, '\n'), 0o644)
}

func sourceURL(cfg *Config, id string) string {
	if cfg.GitHubRepo == "" {
		return ""
	}
	return "https://github.com/" + cfg.GitHubRepo + "/tree/master/" + cfg.PackagesDir + "/" + id
}

// readme prefers the working tree so a local preview shows unpublished edits,
// and falls back to the published copy on GitHub when building in CI.
func readme(cfg *Config, root, id string) string {
	if root != "" {
		local := filepath.Join(cfg.PackageDir(root, id), "README.md")
		if b, err := os.ReadFile(local); err == nil {
			return string(b)
		}
	}
	if cfg.GitHubRepo == "" {
		return ""
	}
	url := strings.Join([]string{
		"https://raw.githubusercontent.com", cfg.GitHubRepo, "master", cfg.PackagesDir, id, "README.md",
	}, "/")
	client := &http.Client{Timeout: 20 * time.Second}
	resp, err := client.Get(url)
	if err != nil {
		return ""
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return ""
	}
	b, err := io.ReadAll(io.LimitReader(resp.Body, 512<<10))
	if err != nil {
		return ""
	}
	return string(b)
}

// dropTitle removes a README's leading "# Title" line, since the page already
// shows the package name as its heading.
func dropTitle(md string) string {
	trimmed := strings.TrimLeftFunc(md, unicode.IsSpace)
	if !strings.HasPrefix(trimmed, "# ") {
		return md
	}
	if _, rest, ok := strings.Cut(trimmed, "\n"); ok {
		return rest
	}
	return ""
}

// CopyStatic copies extra files (_headers, _redirects) into the output
// directory alongside the generated JSON.
func CopyStatic(srcDir, outDir string) (int, error) {
	entries, err := os.ReadDir(srcDir)
	if err != nil {
		if os.IsNotExist(err) {
			return 0, nil
		}
		return 0, err
	}
	n := 0
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		b, err := os.ReadFile(filepath.Join(srcDir, e.Name()))
		if err != nil {
			return n, err
		}
		if err := os.WriteFile(filepath.Join(outDir, e.Name()), b, 0o644); err != nil {
			return n, err
		}
		n++
	}
	return n, nil
}
