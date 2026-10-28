# BusinessHarness

A .NET 10 / C# pipeline automation runner for repository reviews, dynamic reference validation and optional PR/MR feedback. Run the same CLI in **GitHub Actions, GitLab CI, Azure DevOps, or Bitbucket Pipelines**.

Implemented:

- AI diff review through OpenAI's Responses API or Claude's Messages API, with native structured outputs.
- Automatic reference discovery from PR/MR titles and descriptions: native Jira Cloud plus configurable JSON API sources for tickets, specifications and other documents.
- Optional GitHub review comments, approvals and change requests; GitLab comments, approvals and removal of the bot’s approval on gate failure.
- Durable MAGICore memory with configurable global, repository and session scopes, plus allowed agent mentions and review follow-ups from GitHub, GitLab, Jira or normalized events.
- Offline credential-pattern scanning, redaction, sensitive-file withholding, and severity gates.
- JSON, escaped Markdown, and SARIF 2.1.0 reports; actionable errors and stable exit codes.
- A packable .NET tool, a non-root container, a GitHub composite action, and four CI examples.

This is a working initial implementation, **not yet published** to NuGet, a container registry, or an action marketplace. API integrations have contract tests; live authentication requires your credentials.

## Build and run

Requirements: .NET SDK **10.0.403** (or a newer patch in that feature band) and Git. C# uses the latest language version supported by the selected SDK. MAGICore is built from a pinned Git submodule, with version-locked supporting NuGet dependencies; the test framework is centrally pinned to the latest stable version verified on 2026-10-28.

```powershell
git submodule update --init --recursive
dotnet restore BusinessHarness.slnx --locked-mode
dotnet build BusinessHarness.slnx -c Release --no-restore
dotnet test --solution BusinessHarness.slnx -c Release --no-build
dotnet run --project src/BusinessHarness.Cli -- --help
```

Scan a repository without credentials or network requests:

```powershell
dotnet run --project src/BusinessHarness.Cli -- scan --repo C:/code/my-project --base origin/main --head HEAD --output artifacts/scan
```

Both commits and their merge base must exist locally. The runner compares **merge-base(base, head) → head**, ignoring uncommitted files. Fetch full history in CI before invoking the runner. On a new repository, make at least a baseline and change commit first.

PR reviews cover the combined additions, modifications and deletions across **all commits in the PR**, relative to its target branch (`main`, `master`, or another branch). Both OpenAI and Claude receive only changed-file patches with three surrounding context lines, PR metadata and linked Jira/specification documents; unchanged repository files and target-only changes are outside the review. Jira/spec checks use this same diff evidence. Requirements that need unseen code or runtime verification remain `not_verifiable` and fail the gate.

Configure API secrets in your shell or CI secret store, then review:

```powershell
# Supply OPENAI_API_KEY securely through your CI secret store or shell environment.
$env:HARNESS_PROVIDER = 'openai'
$env:HARNESS_MODEL = 'YOUR_STRUCTURED_OUTPUT_MODEL'
dotnet run --project src/BusinessHarness.Cli -- validate
dotnet run --project src/BusinessHarness.Cli -- review --repo C:/code/my-project --base origin/main --head HEAD
```

For Claude, use `HARNESS_PROVIDER=anthropic`, a compatible `HARNESS_MODEL`, and `ANTHROPIC_API_KEY`. OpenAI support is a direct API adapter: it does not launch the Codex CLI or use a ChatGPT subscription. Claude support similarly uses the API rather than Claude Code. Model identifiers are explicit so account availability and model upgrades remain under your control.

In GitHub/GitLab CI, the harness discovers the current PR/MR, fetches its title and description, resolves configured references, and supplies their content to the selected review provider. Configure `GITHUB_TOKEN` or `GITLAB_TOKEN` for metadata access. Outside CI, pass `--change URL`. Jira keys are discovered when `HARNESS_JIRA_SITE`, `JIRA_EMAIL` and `JIRA_API_TOKEN` are configured. Other reference systems use trusted `referenceSources` routing configuration; no manual ticket selection or new CLI command is needed for each source.

```powershell
dotnet run --project src/BusinessHarness.Cli -- review --base origin/main --head HEAD --change https://github.com/ORG/REPO/pull/123 --config C:/trusted/harness.policy.json --feedback auto
```

`--feedback none` (default) writes local reports, `comment` publishes the review, and `auto` also submits the gate decision. Every resolved reference must receive a `met` assessment for the gate to pass; `partial`, `missing` and `not_verifiable` fail it. Manual `--reference SOURCE:ID` and legacy `--issue PROJ-123` remain optional. See [configuration](docs/configuration.md) for source routing, custom Jira acceptance criteria and feedback permissions.

## Remember reviews and follow-ups

Memory is opt-in. Set `memory.enabled` and `scope` (`global`, `repository` or `session`) in trusted policy. `HARNESS_MEMORY_CONFIG` selects global defaults; repository `--config` and per-invocation `--memory-scope`/`--session` override them. Store memory outside the reviewed checkout in a persistent directory.

```powershell
businessharness follow-up --config C:/trusted/harness.policy.json --event C:/events/comment.json --session pr:123
businessharness memory-search --config C:/trusted/harness.policy.json --session pr:123 --query "timestamp precision"
```

`review --event PATH` remembers an allowed agent mention and immediately re-reviews with recalled context. Actor allowlists, source provenance, expiration and bounded recall apply. See [memory setup and examples](docs/memory.md), [example policy](examples/harness.memory.policy.json) and [PR follow-up workflow](examples/pipelines/github-follow-up.yml). MAGICore comes from `external/MAGICore` as a real Git submodule and project reference, not its NuGet package.

## Integrate

| Platform | Integration |
| --- | --- |
| GitHub | [Composite action example](examples/pipelines/github.yml) |
| GitLab | [Container job](examples/pipelines/gitlab.yml) |
| Azure DevOps | [Container step and artifact publishing](examples/pipelines/azure-devops.yml) |
| Bitbucket | [Container pipeline](examples/pipelines/bitbucket.yml) |

First publish the action repository or build and push the image to your registry. Replace the clearly marked action/image placeholders and pin the resulting commit/digest. Configure the CI system's secret and approval rules. The examples default to local reports and pipeline gates. Enable `feedback: comment` or `feedback: auto` with the required host permissions to publish feedback.

This repository's [PR review workflow](.github/workflows/review.yml) schedules code and linked-specification review for every PR, including source updates and title/description edits. Configure the `ai-review` environment, model/provider variables and API secrets before enabling it; see [review setup](docs/integrations.md#this-repositorys-pr-review). Its review scope is independent of the full build/test checks in `ci.yml`.

Package locally without publishing:

```powershell
dotnet pack src/BusinessHarness.Cli -c Release -o artifacts/packages
dotnet tool install BusinessHarness.Tool --tool-path artifacts/tools --configfile examples/NuGet.Local.Config --version 0.1.0
./artifacts/tools/businessharness --help
docker build --pull -t businessharness:0.1.0 .
```

Container usage and publishing instructions are in [integration guidance](docs/integrations.md).

For deliberate dependency maintenance, run `./scripts/Update-Dependencies.ps1`, review its central version/lock-file changes, then rerun tests and vulnerability checks. Normal builds do not float versions.

Run `./scripts/Verify.ps1` for the complete build/test/format/audit/package check, then `./scripts/Smoke-Test.ps1` for end-to-end CLI checks. After building the image, `./scripts/Smoke-Test.ps1 -ContainerImage businessharness:0.1.0` tests the same cases in a container with networking disabled, a read-only root filesystem and read-only source mount.

For Codex contributions, follow [AGENTS.md](AGENTS.md). Project agents for security review, validation and supply-chain review are defined in [`.codex/agents`](.codex/agents); see [usage and review examples](docs/codex.md).

## Reports and errors

`review.json`, `review.md`, and `review.sarif` are written on a completed scan/review, including a failed quality gate. Integration failures write `error.json` once a writable output directory has been selected. No raw diffs, credentials, or HTTP bodies are logged. Exit `0` means the selected mode passed; offline scan is explicitly labeled and does not claim an AI review.

```text
BH1003 [Git]: Git could not resolve or compare the requested commits.
Fix: Check --repo, --base and --head. Fetch both commits and their common history...
Correlation: <run identifier>
```

See [error codes and recovery](docs/errors.md), [architecture](docs/architecture.md), [security boundaries](SECURITY.md), [research and design decisions](docs/research.md), and [verification results](docs/verification.md).

## License

Copyright (c) 2026 **Jihad Khawaja**. All rights reserved.

BusinessHarness is free for **individuals using it personally for non-commercial purposes**. Commercial use and use by or on behalf of an enterprise or other organization require a **separate paid license**.

For commercial or enterprise licensing, contact [jihadkhawaja@outlook.com](mailto:jihadkhawaja@outlook.com). See [LICENSE](LICENSE) for the complete terms. Third-party dependencies retain their own licenses; see [third-party notices](THIRD-PARTY-NOTICES.md) for MAGICore’s Apache 2.0 license.
