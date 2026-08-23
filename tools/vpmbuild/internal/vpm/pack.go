package vpm

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"time"
)

// zipEpoch keeps archives byte-identical across builds: the same tree must
// always hash to the same zipSHA256, or CI reruns would invalidate caches.
var zipEpoch = time.Date(1980, 1, 1, 0, 0, 0, 0, time.UTC)

// skipNames are never shipped inside a package zip.
var skipNames = map[string]bool{
	".git": true, ".github": true, ".DS_Store": true, "Thumbs.db": true,
}

// Pack zips a package directory. VPM zips are flat: package.json sits at the
// zip root, not inside a <id>/ folder, because VCC unpacks straight into
// Packages/<id>/.
func Pack(srcDir, outZip string) (sha string, err error) {
	if err := os.MkdirAll(filepath.Dir(outZip), 0o755); err != nil {
		return "", err
	}
	f, err := os.Create(outZip)
	if err != nil {
		return "", err
	}
	defer func() {
		if cerr := f.Close(); err == nil {
			err = cerr
		}
	}()

	h := sha256.New()
	zw := zip.NewWriter(io.MultiWriter(f, h))

	files, err := collect(srcDir)
	if err != nil {
		return "", err
	}
	for _, rel := range files {
		if err := addFile(zw, srcDir, rel); err != nil {
			return "", err
		}
	}
	if err := zw.Close(); err != nil {
		return "", err
	}
	return hex.EncodeToString(h.Sum(nil)), nil
}

// collect walks the package and returns slash-separated relative paths, sorted
// so archive entry order is stable.
func collect(root string) ([]string, error) {
	var out []string
	err := filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if skipNames[d.Name()] {
			if d.IsDir() {
				return filepath.SkipDir
			}
			return nil
		}
		if d.IsDir() {
			return nil
		}
		rel, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		out = append(out, filepath.ToSlash(rel))
		return nil
	})
	if err != nil {
		return nil, err
	}
	sort.Strings(out)
	return out, nil
}

func addFile(zw *zip.Writer, root, rel string) error {
	src, err := os.Open(filepath.Join(root, filepath.FromSlash(rel)))
	if err != nil {
		return err
	}
	defer src.Close()

	hdr := &zip.FileHeader{Name: rel, Method: zip.Deflate, Modified: zipEpoch}
	hdr.SetMode(0o644)
	w, err := zw.CreateHeader(hdr)
	if err != nil {
		return err
	}
	_, err = io.Copy(w, src)
	return err
}

// PackRelease packs a package and writes the two release assets alongside it:
// the zip, and the package.json that will be copied verbatim into the listing
// (with url and zipSHA256 filled in).
func PackRelease(cfg *Config, root, id, outDir string) (*Manifest, error) {
	dir := cfg.PackageDir(root, id)
	m, err := LoadManifest(filepath.Join(dir, "package.json"))
	if err != nil {
		return nil, err
	}
	if m.Name != id {
		return nil, fmt.Errorf("%s: package.json name is %q but folder is %q", id, m.Name, id)
	}

	zipPath := filepath.Join(outDir, ZipName(id, m.Version))
	sha, err := Pack(dir, zipPath)
	if err != nil {
		return nil, err
	}

	m.ZipSHA256 = sha
	m.URL = DownloadURL(cfg.GitHubRepo, id, m.Version)

	b, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return nil, err
	}
	if err := os.WriteFile(filepath.Join(outDir, "package.json"), append(b, '\n'), 0o644); err != nil {
		return nil, err
	}
	return m, nil
}

// DownloadURL is where the zip will live once the release is published. It has
// to be predictable at pack time so the hash and the URL ship together.
func DownloadURL(repo, id, version string) string {
	return fmt.Sprintf("https://github.com/%s/releases/download/%s/%s",
		repo, ReleaseTag(id, version), ZipName(id, version))
}
