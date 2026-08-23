package vpm

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"time"
)

type Release struct {
	TagName    string    `json:"tag_name"`
	Draft      bool      `json:"draft"`
	Prerelease bool      `json:"prerelease"`
	CreatedAt  time.Time `json:"created_at"`
	Assets     []Asset   `json:"assets"`
}

type Asset struct {
	Name               string `json:"name"`
	BrowserDownloadURL string `json:"browser_download_url"`
	Size               int64  `json:"size"`
}

type GitHub struct {
	Repo   string
	Token  string
	Client *http.Client
}

func NewGitHub(repo string) *GitHub {
	return &GitHub{
		Repo:   repo,
		Token:  os.Getenv("GITHUB_TOKEN"),
		Client: &http.Client{Timeout: 60 * time.Second},
	}
}

func (g *GitHub) get(url string) (*http.Response, error) {
	req, err := http.NewRequest(http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "vpmbuild")
	req.Header.Set("Accept", "application/vnd.github+json")
	if g.Token != "" {
		req.Header.Set("Authorization", "Bearer "+g.Token)
	}
	resp, err := g.Client.Do(req)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(io.LimitReader(resp.Body, 2048))
		resp.Body.Close()
		return nil, fmt.Errorf("GET %s: %s: %s", url, resp.Status, body)
	}
	return resp, nil
}

// Releases returns every published release, oldest page first. Drafts are
// skipped; prereleases are kept because a listing may want to expose them.
func (g *GitHub) Releases() ([]Release, error) {
	var all []Release
	for page := 1; ; page++ {
		url := fmt.Sprintf("https://api.github.com/repos/%s/releases?per_page=100&page=%d", g.Repo, page)
		resp, err := g.get(url)
		if err != nil {
			return nil, err
		}
		var batch []Release
		err = json.NewDecoder(resp.Body).Decode(&batch)
		resp.Body.Close()
		if err != nil {
			return nil, err
		}
		for _, r := range batch {
			if !r.Draft {
				all = append(all, r)
			}
		}
		if len(batch) < 100 {
			return all, nil
		}
	}
}

func (g *GitHub) FetchManifest(url string) (*Manifest, error) {
	resp, err := g.get(url)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	var m Manifest
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&m); err != nil {
		return nil, fmt.Errorf("%s: %w", url, err)
	}
	return &m, nil
}
