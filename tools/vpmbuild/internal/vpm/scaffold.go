package vpm

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// NewPackage lays out a package the way Unity expects it: a manifest, an
// Editor assembly, and a .meta for every single file. Unity generates metas on
// import, but a package folder that ships without them gets fresh GUIDs on
// every machine, so they are written up front.
func NewPackage(cfg *Config, root, id, displayName, description, asmName string) (string, error) {
	if cfg.PackagePrefix != "" && !strings.HasPrefix(id, cfg.PackagePrefix) {
		return "", fmt.Errorf("id %q must start with %q", id, cfg.PackagePrefix)
	}
	dir := cfg.PackageDir(root, id)
	if _, err := os.Stat(dir); err == nil {
		return "", fmt.Errorf("%s already exists", dir)
	}
	// An id with no separators ("importguard") cannot be cased automatically --
	// pascal() would produce "Importguard" -- so -asm overrides the guess.
	short := pascal(strings.TrimPrefix(id, cfg.PackagePrefix))
	if asmName != "" {
		short = asmName
	}
	ns := cfg.Namespace
	if ns == "" {
		ns = pascal(strings.Split(strings.TrimSuffix(cfg.PackagePrefix, "."), ".")[0])
	}
	asmName = ns + "." + short

	m := Manifest{
		Name:            id,
		DisplayName:     displayName,
		Version:         "0.1.0",
		Unity:           "2022.3",
		Description:     description,
		License:         "MIT",
		Author:          &Author{Name: cfg.Author, URL: cfg.AuthorURL},
		VPMDependencies: map[string]string{},
		ChangelogURL:    fmt.Sprintf("https://github.com/%s/blob/master/%s/%s/CHANGELOG.md", cfg.GitHubRepo, cfg.PackagesDir, id),
	}
	manifestJSON, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return "", err
	}

	files := map[string]string{
		"package.json":                         string(manifestJSON) + "\n",
		"README.md":                            fmt.Sprintf("# %s\n\n%s\n", displayName, description),
		"CHANGELOG.md":                         "# Changelog\n\n## [Unreleased]\n\n## [0.1.0]\n- Initial package.\n",
		"Editor/" + asmName + ".Editor.asmdef": asmdef(asmName+".Editor", true),
		"Editor/" + short + "Menu.cs":          editorStub(asmName+".Editor", short, displayName),
	}
	for rel, content := range files {
		p := filepath.Join(dir, filepath.FromSlash(rel))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			return "", err
		}
		if err := os.WriteFile(p, []byte(content), 0o644); err != nil {
			return "", err
		}
	}
	if err := writeMetas(dir); err != nil {
		return "", err
	}
	return dir, nil
}

// pascal turns "avatar-tools" into "AvatarTools".
func pascal(s string) string {
	var b strings.Builder
	for _, word := range strings.FieldsFunc(s, func(r rune) bool { return r == '-' || r == '_' || r == '.' }) {
		b.WriteString(strings.ToUpper(word[:1]) + word[1:])
	}
	return b.String()
}

func asmdef(name string, editorOnly bool) string {
	def := map[string]any{
		"name":               name,
		"rootNamespace":      name,
		"references":         []string{},
		"includePlatforms":   []string{},
		"autoReferenced":     true,
		"noEngineReferences": false,
	}
	if editorOnly {
		def["includePlatforms"] = []string{"Editor"}
	}
	b, _ := json.MarshalIndent(def, "", "    ")
	return string(b) + "\n"
}

func editorStub(ns, short, displayName string) string {
	return fmt.Sprintf(`using UnityEditor;
using UnityEngine;

namespace %s
{
    /// <summary>Placeholder entry point so the package has something to verify against.</summary>
    internal static class %sMenu
    {
        [MenuItem("Tools/%s")]
        private static void Ping()
        {
            Debug.Log("%s is installed.");
        }
    }
}
`, ns, short, displayName, displayName)
}

// writeMetas creates a .meta for every file and folder that lacks one.
func writeMetas(root string) error {
	return filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if path == root || strings.HasSuffix(d.Name(), ".meta") {
			return nil
		}
		metaPath := path + ".meta"
		if _, err := os.Stat(metaPath); err == nil {
			return nil
		}
		return os.WriteFile(metaPath, []byte(metaFor(d.IsDir())), 0o644)
	})
}

func metaFor(isDir bool) string {
	if isDir {
		return "fileFormatVersion: 2\nguid: " + newGUID() + "\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
	}
	return "fileFormatVersion: 2\nguid: " + newGUID() + "\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
}

func newGUID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b[:])
}
