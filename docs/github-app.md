# BusinessHarness GitHub App

The private [BusinessHarness App](https://github.com/apps/businessharness) is owned
by `jihadkhawaja`. It posts as `businessharness[bot]`. GitHub Actions remains the
authenticated event receiver and runner; no public webhook server is needed.

## Install and configure

Install the App only on `jihadkhawaja/BusinessHarness`. Request **Contents: read**,
**Pull requests: read and write**, and the mandatory **Metadata: read**. No Actions,
administration, workflow, account, or organization permission is required. Leave
webhooks and OAuth user authorization disabled for this Actions-based setup.

Generate a private key in the App's Credentials settings and store it directly
as environment secret `BUSINESSHARNESS_APP_PRIVATE_KEY` in `ai-review`. Do not
paste the key into chat, commit it, print it, or store it in policy. Set environment
variable `BUSINESSHARNESS_APP_CLIENT_ID` to the App's Client ID. Keep the existing
provider key and `HARNESS_MODEL` configuration. Restrict this environment to `main`.

Both review workflows mint a one-hour installation token restricted to this
repository with explicit contents-read and pull-requests-write permissions. The
pinned token action revokes it after the job. The token is used by deterministic
host adapters; it is never sent to the model. With no App Client ID configured,
the existing `github-actions[bot]` identity remains available. If a Client ID is
configured but its key or installation is invalid, the job fails instead of
silently posting under another identity.

## Request and receive a review

On an open PR, the allowed maintainer can post in the Conversation tab:

```text
@businessharness review this diff for security, maintainability, and linked specs.
```

`@businessharness[bot]` is also accepted. Both forms use the App's name; the
workflow and trusted memory policy enforce the actor allowlist and full handle.
The complete current PR diff is reviewed, including all its commits. The response
is a regular comment in that same PR's Conversation tab. Automatic PR reviews
also post there. Repeated requests produce new comments rather than editing a
different user's comment. Draft PRs are supported.

The `[agent]` entries in GitHub's picker are GitHub agent integrations, a separate
feature from registering a regular GitHub App. App registration does not guarantee
a particular autocomplete placement. The Actions text trigger works independently
of autocomplete. See [GitHub agent apps](https://docs.github.com/en/enterprise-cloud%40latest/copilot/concepts/agent-capabilities/about-agents/agent-apps).

## Review template

The renderer fixes the structure; models supply bounded, validated plain-text
content. Every finding has a severity, category, exact changed-file path and added
line, trigger/impact, and a concrete minimal fix. Categories are `security`,
`maintainability`, and `correctness`; security and substantive change risks receive
priority. Cosmetic preferences and speculative advice are excluded.

```markdown
## BusinessHarness review

**2 findings to address**

One sentence describing the material issues.

### Findings

- **high · security — Missing authorization check**
  src/Endpoint.cs:42 — Unauthenticated requests expose protected records. Require authorization before reading them.

### Specifications

- **SPEC-1 — partial**: The added validation covers format but not the required length bound.
```

The real template includes every validated finding and specification assessment.
It explicitly says when no specifications were supplied. A collapsed scope footer
records the compared commits, file coverage, withheld files, gate result, and
limits. Medium findings can coexist with a passing gate when `failOn` is `high`.
All untrusted markup and mentions remain escaped and known secrets redacted.

`--feedback comment` posts a Conversation comment on GitHub and a note on GitLab.
`--feedback auto` retains commit-bound GitHub reviews and GitLab approval handling.
The repository workflows select `comment`; automatic approval requires a separate
trusted policy decision. Fresh metadata/head checks and no-write-retry behavior
apply to both comment and review publication.
