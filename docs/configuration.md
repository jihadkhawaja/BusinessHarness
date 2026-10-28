# Configuration

Policy is loaded **only** from an explicit `--config PATH`. Use a policy from a trusted checkout or runner-managed location, not the PR branch. Unknown properties, invalid types and out-of-range limits are errors. Credentials never belong in policy JSON or CLI arguments.

```json
{
  "provider": "openai",
  "model": "YOUR_STRUCTURED_OUTPUT_MODEL",
  "failOn": "high",
  "maxFiles": 60,
  "maxDiffBytes": 200000,
  "maxFileDiffBytes": 64000,
  "timeoutSeconds": 120,
  "maxOutputTokens": 6000,
  "jiraSite": "https://your-team.atlassian.net",
  "jiraAcceptanceCriteriaField": "customfield_10001"
}
```

Omit Jira properties when not using Jira. The example model is deliberately a placeholder; select an API model supporting the provider's native JSON-schema output. `model` has no implicit default. A configured acceptance criteria field must be present and non-empty in each issue; the runner does not silently substitute description text for missing criteria.

| Property | Default | Allowed |
| --- | --- | --- |
| provider | openai | openai, anthropic |
| model | none | 1–120 characters; required for review/validate |
| failOn | high | info, low, medium, high, critical |
| maxFiles | 60 | 1–500; counts all changed files |
| maxDiffBytes | 200000 | 1000–1000000 UTF-8 bytes |
| maxFileDiffBytes | 64000 | 1000–maxDiffBytes |
| timeoutSeconds | 120 | 5–600, total execution deadline |
| maxOutputTokens | 6000 | 256–32000; provider/model must support the requested value |
| jiraSite | none | HTTPS tenant on atlassian.net; no custom paths/ports |
| jiraAcceptanceCriteriaField | none | customfield_ followed by digits |

Environment overrides: `HARNESS_PROVIDER`, `HARNESS_MODEL`, `HARNESS_JIRA_SITE`. Base/head defaults can come from `HARNESS_BASE` and `HARNESS_HEAD`; command arguments override those refs. PR/MR titles and descriptions supply reference identifiers through trusted routing rules. They never define policy, endpoints or commands.

| Secret variable | Purpose |
| --- | --- |
| OPENAI_API_KEY | OpenAI provider authentication |
| ANTHROPIC_API_KEY | Claude provider authentication |
| JIRA_EMAIL | Jira Cloud service account email |
| JIRA_API_TOKEN | Jira Cloud API token with issue-read permission |

Only the selected provider's API key is required. Use a dedicated Jira account with minimal project permissions and scoped token. Jira description text is extracted from Atlassian Document Format. Attachments are not fetched. Put Jira requirements in description or the configured field. Jira keys in the PR/MR title and description are discovered automatically when Jira is configured. Up to 20 distinct references can be resolved per review, including optional explicit `--issue` and `--reference` selections.

`validate` checks policy and required environment-variable presence; it makes no API calls and does not assert that credentials work. Use the first `review` run to verify remote authentication.

Limits, binary changes, symlink changes, submodule changes, malformed output, refusals, and incomplete generation stop the run. Sensitive filenames produce a high-severity BH002 finding and are withheld from providers. Deleted files remain in diff context; model findings must refer to added lines of an inspected file. Local BH001 credential findings and BH002 findings are retained regardless of model output. Change `failOn` only through trusted policy.

## Dynamic reference sources

`referenceSources` connects any service exposing a GET JSON API. Configure routing once in trusted policy; agents then receive references discovered from each PR/MR title and description. A source does not require a dedicated CLI command or a Jira adapter. For APIs requiring GraphQL, POST, HTML rendering or other protocols, implement `IReferenceResolver` or expose a trusted JSON gateway.

```json
{
  "referenceSources": [
    {
      "name": "specs",
      "pattern": "https://docs\\.example\\.com/specs/(?<id>[A-Za-z0-9-]+)",
      "endpoint": "https://api.example.com/specs/{id}",
      "titlePath": "data.title",
      "contentPath": "data.content",
      "tokenEnvironment": "SPEC_API_TOKEN"
    },
    {
      "name": "tracker",
      "pattern": "(?<![A-Za-z0-9_])WORK-(?<id>[1-9][0-9]*)(?![A-Za-z0-9_])",
      "endpoint": "https://tracker.example.com/api/issues/{id}",
      "titlePath": "title",
      "contentPath": "description",
      "tokenEnvironment": "TRACKER_API_TOKEN",
      "authenticationHeader": "X-Api-Key",
      "authenticationScheme": ""
    }
  ]
}
```

A PR titled `WORK-42: implement export` with `https://docs.example.com/specs/export-v2` in its description fetches both sources automatically. Review assessments use `tracker:42` and `specs:export-v2` as identifiers. The output field `issueKey` remains for report/schema compatibility and now identifies any reference.

Each source needs a unique lowercase `name` (up to 32 characters; `jira` is reserved), a .NET regex with named capture `id`, and a fixed trusted HTTPS `endpoint` template containing `{id}` in its path or query. Captured identifiers are URL encoded and cannot set the host, authentication or command. `titlePath` and `contentPath` are dot-separated JSON property paths, defaulting to `title` and `body`; both must resolve to non-empty strings. Tokens are optional for public APIs. `authenticationHeader` supports `Authorization` (default), `PRIVATE-TOKEN` and `X-Api-Key`; `authenticationScheme` supports `Bearer` (default), `Basic` and an empty prefix. For Basic authentication, supply the complete base64 credential value through the secret variable.

Configured source matches take precedence over Jira discovery for overlapping text. Duplicate references are fetched once per run; distinct sources retain distinct identifiers. Unknown links are left as metadata and are not fetched. References inside fetched documents are not recursively followed. Missing documents, invalid content, regex timeouts, authentication errors and limits stop the review. Limits: 20 references, 32,000 characters per document, 128,000 characters combined, 64,000 characters of PR/MR metadata, 100 ms per regex operation and 1 MB per HTTP response.

## Pull/merge request metadata and feedback

In public GitHub Actions PR workflows and GitLab MR pipelines, standard CI variables identify the current request. Outside CI use `--change https://github.com/ORG/REPO/pull/123` or `--change https://gitlab.com/GROUP/REPO/-/merge_requests/123`. No title, description or ticket CLI argument is required. `scan` stays offline even inside CI.

For self-managed hosts, custom credentials or explicit request configuration, use trusted policy:

```json
{
  "changeRequest": {
    "platform": "gitlab",
    "apiBase": "https://gitlab.example.com/api/v4/",
    "tokenEnvironment": "REVIEW_BOT_TOKEN"
  }
}
```

`project` and `number` are optional inside CI: the harness fills them from each run’s metadata, so a private-host policy can be reused across requests. Outside CI supply both explicitly. `platform` is `github` or `gitlab`. Defaults are `https://api.github.com/` and `https://gitlab.com/api/v4/`, using `GITHUB_TOKEN` and `GITLAB_TOKEN` respectively. `apiBase` must end in `/`; GitHub Enterprise commonly uses `/api/v3/`. Explicit public `--change` URLs override configured identity and use default public-host credentials. Self-managed CI requires explicit trusted policy rather than deriving credential destinations from CI URLs.

The host target/base determines the diff when a request is connected. `--head` must resolve to its source head SHA; synthetic merge commits are rejected. Both commits and their common history must exist locally. Before publishing, the harness re-fetches metadata and withholds feedback if the head, base, title, description, draft status or open state changed.

| `--feedback` | GitHub | GitLab |
| --- | --- | --- |
| `none` (default) | Local reports only | Local reports only |
| `comment` | PR Conversation comment | MR note |
| `auto`, gate passes | APPROVE with reviewed commit ID | MR note, then approve with reviewed SHA |
| `auto`, gate fails | REQUEST_CHANGES with reviewed commit ID | Remove this bot's approval, then MR note; failed CI job supplies the blocking gate |

Draft requests and reviews with withheld files receive comments without automatic approval. Feedback is emitted only after result validation and report writing. Metadata access requires a host token; GitHub feedback requires `pull-requests: write` and approval permission in repository/organization settings. GitLab needs an eligible bot token with `api` scope for feedback; `CI_JOB_TOKEN` is not substituted for an approval-capable token. Host rules may prevent self-approval. GitLab's unapprove operation removes only the caller's approval; configure required successful pipelines to block merges on a failed review. It does not set GitLab's separate requested-changes reviewer state.

Publication is not transactional: GitLab note creation may succeed before approval fails. No write request is retried automatically, and rerunning may produce another comment. Reports describe the review gate; a publishing error also creates `error.json` and returns a nonzero integration exit code, even when the local gate passed. No live write occurs unless a caller selects `comment` or `auto`.

## Long-term memory

The optional `memory` object configures durable MAGICore memory, global/repository/session scopes, retrieval limits, retention, and allowed agent mentions. Global defaults can come from `HARNESS_MEMORY_CONFIG`; explicit repository memory settings replace them, and invocation settings can override scope/session. See [memory setup](memory.md) for the full contract, follow-up payloads, container persistence and examples.

## Review presentation

Findings include a category: `security`, `maintainability`, or `correctness`.
Provider schemas require it; older serialized findings without the additive
property default to `correctness`. Unknown categories fail validation. Local
credential and sensitive-path findings are categorized as `security`. JSON and
SARIF preserve the category; Markdown uses the fixed [review template](github-app.md#review-template).
Every finding retains its exact added-line anchor and existing severity gate.
