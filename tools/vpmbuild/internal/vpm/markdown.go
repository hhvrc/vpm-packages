package vpm

import (
	"html"
	"regexp"
	"strings"
)

// Markdown renders the small subset of Markdown that package READMEs actually
// use: headings, paragraphs, lists, fenced and inline code, links, images,
// bold, italic and rules.
//
// A real parser would be a dependency, and this tool has none on purpose. Every
// piece of source text is HTML-escaped before any tag is emitted, so a README
// cannot inject markup into the generated page.
func Markdown(src string) string {
	var out strings.Builder
	lines := strings.Split(strings.ReplaceAll(src, "\r\n", "\n"), "\n")

	inCode, inList := false, false
	var para []string

	flushPara := func() {
		if len(para) > 0 {
			out.WriteString("<p>" + inline(strings.Join(para, " ")) + "</p>\n")
			para = nil
		}
	}
	closeList := func() {
		if inList {
			out.WriteString("</ul>\n")
			inList = false
		}
	}

	for _, line := range lines {
		trimmed := strings.TrimSpace(line)

		if strings.HasPrefix(trimmed, "```") {
			flushPara()
			closeList()
			if inCode {
				out.WriteString("</code></pre>\n")
			} else {
				out.WriteString("<pre><code>")
			}
			inCode = !inCode
			continue
		}
		if inCode {
			out.WriteString(html.EscapeString(line) + "\n")
			continue
		}

		switch {
		case trimmed == "":
			flushPara()
			closeList()

		case strings.HasPrefix(trimmed, "#"):
			flushPara()
			closeList()
			level := min(len(trimmed)-len(strings.TrimLeft(trimmed, "#")), 6)
			text := inline(strings.TrimSpace(trimmed[level:]))
			tag := "h" + string(rune('0'+level))
			out.WriteString("<" + tag + ">" + text + "</" + tag + ">\n")

		case trimmed == "---" || trimmed == "***" || trimmed == "___":
			flushPara()
			closeList()
			out.WriteString("<hr>\n")

		case strings.HasPrefix(trimmed, "- "), strings.HasPrefix(trimmed, "* "):
			flushPara()
			if !inList {
				out.WriteString("<ul>\n")
				inList = true
			}
			out.WriteString("<li>" + inline(trimmed[2:]) + "</li>\n")

		default:
			closeList()
			para = append(para, trimmed)
		}
	}
	flushPara()
	closeList()
	if inCode {
		out.WriteString("</code></pre>\n")
	}
	return out.String()
}

var (
	reImage  = regexp.MustCompile(`!\[([^\]]*)\]\(([^)\s]+)\)`)
	reLink   = regexp.MustCompile(`\[([^\]]+)\]\(([^)\s]+)\)`)
	reCode   = regexp.MustCompile("`([^`]+)`")
	reBold   = regexp.MustCompile(`\*\*([^*]+)\*\*`)
	reItalic = regexp.MustCompile(`(^|[^*])\*([^*]+)\*`)
)

// inline escapes the text first, then re-introduces the handful of tags the
// syntax allows. URLs are checked so a link cannot smuggle in javascript:.
func inline(s string) string {
	s = html.EscapeString(s)
	s = reImage.ReplaceAllStringFunc(s, func(m string) string {
		g := reImage.FindStringSubmatch(m)
		if !safeURL(g[2]) {
			return g[1]
		}
		return `<img src="` + g[2] + `" alt="` + g[1] + `">`
	})
	s = reLink.ReplaceAllStringFunc(s, func(m string) string {
		g := reLink.FindStringSubmatch(m)
		if !safeURL(g[2]) {
			return g[1]
		}
		return `<a href="` + g[2] + `">` + g[1] + `</a>`
	})
	s = reCode.ReplaceAllString(s, "<code>$1</code>")
	s = reBold.ReplaceAllString(s, "<strong>$1</strong>")
	s = reItalic.ReplaceAllString(s, "$1<em>$2</em>")
	return s
}

func safeURL(u string) bool {
	lower := strings.ToLower(strings.TrimSpace(u))
	switch {
	case strings.HasPrefix(lower, "https://"), strings.HasPrefix(lower, "http://"):
		return true
	case strings.HasPrefix(lower, "#"), strings.HasPrefix(lower, "/"):
		return true
	case strings.Contains(lower, ":"):
		return false // javascript:, data:, vbscript: and friends
	default:
		return true // relative path
	}
}
