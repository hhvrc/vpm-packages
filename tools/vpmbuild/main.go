// Command vpmbuild builds a VRChat Package Manager (VPM) listing.
//
//	vpmbuild validate            check every local package the way ALCOM would
//	vpmbuild new <id>            scaffold a new package under Unity/Packages
//	vpmbuild pack <id>           zip a package + emit its release package.json
//	vpmbuild listing             rebuild index.json + site.json from GitHub releases
//	vpmbuild tag <id>            print the release tag for a package's current version
//
// The listing is a static file. There is no server and no database: CI runs
// this, the output is uploaded, and VCC/ALCOM just GET it.
package main

import (
	"flag"
	"fmt"
	"os"
	"path/filepath"

	"tech.heavenvr/vpmbuild/internal/vpm"
)

func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "vpmbuild:", err)
		os.Exit(1)
	}
}

func run(args []string) error {
	if len(args) == 0 {
		usage()
		return fmt.Errorf("no command given")
	}
	cmd, rest := args[0], args[1:]
	switch cmd {
	case "validate":
		return cmdValidate(rest)
	case "new":
		return cmdNew(rest)
	case "pack":
		return cmdPack(rest)
	case "listing":
		return cmdListing(rest)
	case "tag":
		return cmdTag(rest)
	case "help", "-h", "--help":
		usage()
		return nil
	default:
		usage()
		return fmt.Errorf("unknown command %q", cmd)
	}
}

func usage() {
	fmt.Fprint(os.Stderr, `vpmbuild - build a VPM package listing

  validate                 verify every local package (run this before releasing)
  new <id> [flags]         scaffold a package under the packages directory
  pack <id> [flags]        build the release zip and package.json
  listing [flags]          rebuild index.json + site.json from GitHub releases
  tag <id>                 print the git tag for the package's current version

Common flags:
  -config string   path to listing.json (default "listing.json", searched upward)
  -out string      output directory (default "dist")
`)
}

// commonFlags wires the config lookup every subcommand shares.
func commonFlags(fs *flag.FlagSet) *string {
	return fs.String("config", "", "path to listing.json (default: nearest one upward)")
}

// loadConfig finds listing.json by walking up from the working directory, so
// the tool works from the repo root or from inside tools/vpmbuild.
func loadConfig(explicit string) (*vpm.Config, string, error) {
	if explicit != "" {
		cfg, err := vpm.LoadConfig(explicit)
		if err != nil {
			return nil, "", err
		}
		root, err := filepath.Abs(filepath.Dir(explicit))
		return cfg, root, err
	}
	dir, err := os.Getwd()
	if err != nil {
		return nil, "", err
	}
	for {
		candidate := filepath.Join(dir, "listing.json")
		if _, err := os.Stat(candidate); err == nil {
			cfg, err := vpm.LoadConfig(candidate)
			return cfg, dir, err
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return nil, "", fmt.Errorf("no listing.json found (use -config)")
		}
		dir = parent
	}
}

// parseArgs parses flags that appear before, between or after positional
// arguments. Go's flag package stops at the first non-flag, and "new <id>
// -display ..." reads more naturally than the other order.
func parseArgs(fs *flag.FlagSet, args []string) ([]string, error) {
	var positional []string
	for len(args) > 0 {
		if err := fs.Parse(args); err != nil {
			return nil, err
		}
		rest := fs.Args()
		if len(rest) == 0 {
			break
		}
		positional = append(positional, rest[0])
		args = rest[1:]
	}
	return positional, nil
}

func cmdValidate(args []string) error {
	fs := flag.NewFlagSet("validate", flag.ExitOnError)
	config := commonFlags(fs)
	strict := fs.Bool("strict", false, "treat warnings as errors")
	if _, err := parseArgs(fs, args); err != nil {
		return err
	}
	cfg, root, err := loadConfig(*config)
	if err != nil {
		return err
	}
	probs, err := vpm.Validate(cfg, root)
	if err != nil {
		return err
	}
	fatal := 0
	for _, p := range probs {
		fmt.Println(p)
		if p.Fatal {
			fatal++
		}
	}
	switch {
	case fatal > 0:
		return fmt.Errorf("%d blocking problem(s)", fatal)
	case *strict && len(probs) > 0:
		return fmt.Errorf("%d warning(s) with -strict", len(probs))
	case len(probs) == 0:
		fmt.Println("all packages valid")
	}
	return nil
}

func cmdNew(args []string) error {
	fs := flag.NewFlagSet("new", flag.ExitOnError)
	config := commonFlags(fs)
	display := fs.String("display", "", "display name shown in VCC")
	desc := fs.String("description", "", "one-line description")
	asm := fs.String("asm", "", "assembly/namespace name, e.g. ImportGuard (default: derived from the id)")
	pos, err := parseArgs(fs, args)
	if err != nil {
		return err
	}
	if len(pos) != 1 {
		return fmt.Errorf("usage: vpmbuild new <package-id> -display \"Name\"")
	}
	id := pos[0]
	cfg, root, err := loadConfig(*config)
	if err != nil {
		return err
	}
	if *display == "" {
		*display = id
	}
	dir, err := vpm.NewPackage(cfg, root, id, *display, *desc, *asm)
	if err != nil {
		return err
	}
	fmt.Println("created", dir)
	return nil
}

func cmdPack(args []string) error {
	fs := flag.NewFlagSet("pack", flag.ExitOnError)
	config := commonFlags(fs)
	out := fs.String("out", "dist", "output directory")
	pos, err := parseArgs(fs, args)
	if err != nil {
		return err
	}
	if len(pos) != 1 {
		return fmt.Errorf("usage: vpmbuild pack <package-id>")
	}
	cfg, root, err := loadConfig(*config)
	if err != nil {
		return err
	}
	outDir := absUnder(root, *out)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return err
	}
	m, err := vpm.PackRelease(cfg, root, pos[0], outDir)
	if err != nil {
		return err
	}
	fmt.Printf("packed %s %s\n  zip    %s\n  sha256 %s\n  tag    %s\n",
		m.Name, m.Version, filepath.Join(outDir, vpm.ZipName(m.Name, m.Version)),
		m.ZipSHA256, vpm.ReleaseTag(m.Name, m.Version))
	return nil
}

func cmdListing(args []string) error {
	fs := flag.NewFlagSet("listing", flag.ExitOnError)
	config := commonFlags(fs)
	out := fs.String("out", "dist", "output directory")
	static := fs.String("static", "site", "directory of extra files to copy into the output")
	prerelease := fs.Bool("prerelease", false, "include GitHub prereleases")
	jsonOnly := fs.Bool("json-only", false, "skip site.json; write only the listing")
	local := fs.Bool("local", false, "preview from the working tree instead of GitHub releases")
	if _, err := parseArgs(fs, args); err != nil {
		return err
	}
	cfg, root, err := loadConfig(*config)
	if err != nil {
		return err
	}
	var listing *vpm.Listing
	if *local {
		listing, err = vpm.BuildLocalListing(cfg, root)
		if err != nil {
			return err
		}
		fmt.Fprintln(os.Stderr, "note: -local preview; download URLs point at releases that may not exist")
	} else {
		var notes []string
		listing, notes, err = vpm.BuildListing(cfg, *prerelease)
		if err != nil {
			return err
		}
		for _, n := range notes {
			fmt.Fprintln(os.Stderr, "note:", n)
		}
	}
	outDir := absUnder(root, *out)
	if err := vpm.WriteListing(listing, filepath.Join(outDir, "index.json")); err != nil {
		return err
	}
	versions := 0
	for _, p := range listing.Packages {
		versions += len(p.Versions)
	}
	fmt.Printf("wrote %s: %d package(s), %d version(s)\n",
		filepath.Join(outDir, "index.json"), len(listing.Packages), versions)

	if !*jsonOnly {
		meta := vpm.BuildSiteMeta(cfg, listing, root)
		if err := vpm.WriteSiteMeta(meta, filepath.Join(outDir, "site.json")); err != nil {
			return err
		}
		fmt.Println("wrote", filepath.Join(outDir, "site.json"))
	}

	n, err := vpm.CopyStatic(absUnder(root, *static), outDir)
	if err != nil {
		return err
	}
	if n > 0 {
		fmt.Printf("copied %d static file(s) from %s\n", n, *static)
	}
	return nil
}

func cmdTag(args []string) error {
	fs := flag.NewFlagSet("tag", flag.ExitOnError)
	config := commonFlags(fs)
	pos, err := parseArgs(fs, args)
	if err != nil {
		return err
	}
	if len(pos) != 1 {
		return fmt.Errorf("usage: vpmbuild tag <package-id>")
	}
	cfg, root, err := loadConfig(*config)
	if err != nil {
		return err
	}
	m, err := vpm.LoadManifest(filepath.Join(cfg.PackageDir(root, pos[0]), "package.json"))
	if err != nil {
		return err
	}
	fmt.Println(vpm.ReleaseTag(m.Name, m.Version))
	return nil
}

func absUnder(root, p string) string {
	if filepath.IsAbs(p) {
		return p
	}
	return filepath.Join(root, p)
}
