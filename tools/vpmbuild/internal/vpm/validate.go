package vpm

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

type Problem struct {
	Package string
	Msg     string
	Fatal   bool
}

func (p Problem) String() string {
	level := "warn"
	if p.Fatal {
		level = "error"
	}
	return fmt.Sprintf("%-5s %s: %s", level, p.Package, p.Msg)
}

// Validate checks everything that would make VCC or ALCOM silently drop a
// package. ALCOM is the stricter of the two, so these rules follow it: bad
// semver, a missing name/version, or an id/folder mismatch makes a package
// vanish from the listing with no error shown to the user.
func Validate(cfg *Config, root string) ([]Problem, error) {
	pkgs, err := cfg.LocalPackages(root)
	if err != nil {
		return nil, err
	}
	var probs []Problem
	for id, m := range pkgs {
		add := func(fatal bool, format string, args ...any) {
			probs = append(probs, Problem{Package: id, Msg: fmt.Sprintf(format, args...), Fatal: fatal})
		}

		if m.Name == "" {
			add(true, "package.json has no name")
		} else if m.Name != id {
			add(true, "name %q does not match folder %q", m.Name, id)
		}
		if cfg.PackagePrefix != "" && !strings.HasPrefix(id, cfg.PackagePrefix) {
			add(true, "id must start with %q", cfg.PackagePrefix)
		}
		if strings.ToLower(id) != id {
			add(true, "id must be lowercase")
		}
		if m.Version == "" {
			add(true, "no version")
		} else if _, ok := ParseVersion(m.Version); !ok {
			add(true, "version %q is not valid semver (x.y.z)", m.Version)
		}
		if m.DisplayName == "" {
			add(false, "no displayName; VCC will show the raw id")
		}
		if m.Unity == "" {
			add(false, "no unity field; VCC cannot filter by editor version")
		}
		if m.Description == "" {
			add(false, "no description")
		}
		if m.License == "" {
			add(false, "no license (SPDX id recommended)")
		}
		if m.Author == nil || m.Author.Name == "" {
			add(false, "no author.name")
		}

		dir := cfg.PackageDir(root, id)
		missing, err := missingMetas(dir)
		if err != nil {
			return nil, err
		}
		for _, rel := range missing {
			add(true, "missing .meta for %s (Unity will regenerate a new GUID on every install)", rel)
		}
	}
	return probs, nil
}

// missingMetas finds files and folders inside a package that Unity would not be
// able to track. A shipped package without stable .meta files breaks every
// reference a user has made to it the moment they update.
func missingMetas(dir string) ([]string, error) {
	var missing []string
	err := filepath.WalkDir(dir, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		name := d.Name()
		if skipNames[name] {
			if d.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if path == dir || strings.HasSuffix(name, ".meta") {
			return nil
		}
		if _, err := os.Stat(path + ".meta"); os.IsNotExist(err) {
			rel, _ := filepath.Rel(dir, path)
			missing = append(missing, filepath.ToSlash(rel))
		}
		return nil
	})
	return missing, err
}
