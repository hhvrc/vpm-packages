package vpm

import (
	"strconv"
	"strings"
)

// Version is the subset of semver VPM cares about. VRChat calls their scheme
// "Branding.Breaking.Bumps" rather than semver, but the wire format is the same
// and VCC/ALCOM sort with semver rules, prerelease included.
type Version struct {
	Major, Minor, Patch int
	Prerelease          string
	Build               string
	Raw                 string
}

func ParseVersion(s string) (Version, bool) {
	v := Version{Raw: s}
	rest := s
	if i := strings.IndexByte(rest, '+'); i >= 0 {
		v.Build, rest = rest[i+1:], rest[:i]
	}
	if i := strings.IndexByte(rest, '-'); i >= 0 {
		v.Prerelease, rest = rest[i+1:], rest[:i]
	}
	parts := strings.Split(rest, ".")
	if len(parts) != 3 {
		return v, false
	}
	var err error
	if v.Major, err = strconv.Atoi(parts[0]); err != nil {
		return v, false
	}
	if v.Minor, err = strconv.Atoi(parts[1]); err != nil {
		return v, false
	}
	if v.Patch, err = strconv.Atoi(parts[2]); err != nil {
		return v, false
	}
	return v, true
}

func (v Version) IsPrerelease() bool { return v.Prerelease != "" }

// Compare returns -1, 0 or 1. Prerelease identifiers are compared per semver
// §11: numeric identifiers numerically, others lexically, numeric < alpha,
// and a version with a prerelease sorts below the same version without one.
func (v Version) Compare(o Version) int {
	if c := cmpInt(v.Major, o.Major); c != 0 {
		return c
	}
	if c := cmpInt(v.Minor, o.Minor); c != 0 {
		return c
	}
	if c := cmpInt(v.Patch, o.Patch); c != 0 {
		return c
	}
	switch {
	case v.Prerelease == "" && o.Prerelease == "":
		return 0
	case v.Prerelease == "":
		return 1
	case o.Prerelease == "":
		return -1
	}
	a, b := strings.Split(v.Prerelease, "."), strings.Split(o.Prerelease, ".")
	for i := 0; i < len(a) && i < len(b); i++ {
		an, aErr := strconv.Atoi(a[i])
		bn, bErr := strconv.Atoi(b[i])
		switch {
		case aErr == nil && bErr == nil:
			if c := cmpInt(an, bn); c != 0 {
				return c
			}
		case aErr == nil: // numeric identifiers sort below alphanumeric ones
			return -1
		case bErr == nil:
			return 1
		default:
			if c := strings.Compare(a[i], b[i]); c != 0 {
				return c
			}
		}
	}
	return cmpInt(len(a), len(b))
}

func cmpInt(a, b int) int {
	switch {
	case a < b:
		return -1
	case a > b:
		return 1
	}
	return 0
}

// SortVersionsDesc sorts raw version strings newest first. Unparseable
// versions sink to the bottom rather than panicking; validate catches them.
func SortVersionsDesc(raw []string) []string {
	out := append([]string(nil), raw...)
	parsed := make(map[string]Version, len(out))
	for _, s := range out {
		v, ok := ParseVersion(s)
		if !ok {
			v = Version{Major: -1, Raw: s}
		}
		parsed[s] = v
	}
	for i := 1; i < len(out); i++ {
		for j := i; j > 0 && parsed[out[j]].Compare(parsed[out[j-1]]) > 0; j-- {
			out[j], out[j-1] = out[j-1], out[j]
		}
	}
	return out
}
