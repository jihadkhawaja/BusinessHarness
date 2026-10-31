# BusinessHarness website

Public landing page for https://businessharness.net/.

This standalone branch contains only public website files. The .NET application,
contributor instructions, and full documentation remain on `main` in the same
repository. Keep product claims and licensing text aligned with those sources.

The deployment section describes self-managed CLI, internal CI, and container
execution. AI reviews still send context to OpenAI or Claude using the company's
own credentials; only the offline scan works without provider calls. Keep these
claims aligned with `main`'s `docs/self-hosting.md` and `SECURITY.md`. Publish the
linked deployment guide on `main` before publishing website changes that link to it.

The integrations section describes reference retrieval through the pinned
AgentConnector dependency: Jira tickets, Confluence pages, Zendesk requests, and
other compatible configured GET actions or JSON APIs. These sources provide text
for review; OpenAI and Claude remain the AI review providers. Keep scope and
setup claims aligned with `main`'s `docs/configuration.md` and `docs/integrations.md`.
Do not imply all upstream catalog actions, write operations, or OAuth lifecycle
management are available through BusinessHarness.

## Development

No dependencies or build step are required. Serve this folder using a local static
HTTP server, for example `python -m http.server 4173 --bind 127.0.0.1`, and open
http://127.0.0.1:4173/. Check desktop and narrow mobile layouts, keyboard-operated
demo tabs, copy commands, navigation, and FAQ disclosure controls before publishing.

The theme follows the system's light/dark preference through CSS
`prefers-color-scheme` and updates automatically when that preference changes.
Light mode uses the original warm background; dark mode uses charcoal and green
surfaces with brighter text and accents. Theme colors for browser chrome also
follow the preference. Check both themes on desktop and mobile, including button
and keyboard-focus contrast. No theme setting is stored or handled by JavaScript.
When CSS changes, refresh the stylesheet's `v` query in `index.html` with the first
12 characters of its SHA-256 hash so cached styles do not delay the update.

`site.js` only controls tabs and clipboard interaction. The page has no analytics,
external scripts, remote fonts, forms, tracking, or backend. Terminal output is an
illustrative example, not a live review. GitHub repository links may require access
while the application repository is private.

## Publishing

GitHub Pages publishes the root of `codex/website` through its managed branch
deployment. `.nojekyll` disables Jekyll processing; `CNAME` retains the custom domain.
Push website changes to this branch to deploy. No custom workflow, provider token,
Cloudflare credential, or application build is needed.

Pages settings must retain the custom domain `businessharness.net` and HTTPS
enforcement. Cloudflare DNS uses GitHub Pages' documented four A records, four AAAA
records, and a DNS-only `www` CNAME to `jihadkhawaja.github.io`. GitHub Pages handles
HTTP-to-HTTPS redirection when HTTPS enforcement is enabled.

Copyright (c) 2026 Jihad Khawaja. Licensed under the [MIT License](LICENSE).
