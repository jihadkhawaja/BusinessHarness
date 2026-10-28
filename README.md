# BusinessHarness

[![Build and security checks](https://github.com/jihadkhawaja/BusinessHarness/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/jihadkhawaja/BusinessHarness/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/BusinessHarness.svg)](https://www.nuget.org/packages/BusinessHarness)
[![NuGet total downloads](https://img.shields.io/nuget/dt/BusinessHarness.svg)](https://www.nuget.org/packages/BusinessHarness)
[![Security policy](https://img.shields.io/badge/security-policy-blue)](SECURITY.md)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/badge/license-personal%20non--commercial-blue)](LICENSE)

Review code changes, check them against tickets and specs, and catch potential issues before merging. BusinessHarness is a .NET 10 CLI that runs locally or in GitHub Actions, GitLab CI, Azure DevOps, and Bitbucket Pipelines.

- **AI reviews** with OpenAI or Claude, plus an offline credential-pattern scan.
- **Ticket and spec checks** using Jira Cloud or configured JSON APIs.
- **Optional feedback and memory** for PR/MR comments, approvals, and follow-up reviews.
- **Reports you can use** in JSON, Markdown, and SARIF.

## Quick start

Requires .NET 10 and Git.

```powershell
dotnet tool install --global BusinessHarness --version 0.1.0
businessharness --help
```

Scan a repository without credentials or network requests:

```powershell
businessharness scan --repo C:/code/my-project --base origin/main --head HEAD
```

The scan compares committed changes from the common ancestor of the two refs to `HEAD`. Both refs and their shared history must be available locally; uncommitted changes are ignored.

PR reviews cover additions, modifications, and deletions across all PR commits.
Both providers receive the same bounded diff and linked ticket/specification
evidence. Unseen code and runtime behavior cannot establish a passing assessment.

For an AI review, supply `OPENAI_API_KEY` through your shell environment or CI secret store, then choose a model with structured output support:

```powershell
$env:HARNESS_PROVIDER = 'openai'
$env:HARNESS_MODEL = 'YOUR_STRUCTURED_OUTPUT_MODEL'
businessharness validate
businessharness review --repo C:/code/my-project --base origin/main --head HEAD
```

For Claude, use `HARNESS_PROVIDER=anthropic` and `ANTHROPIC_API_KEY`. Both providers use their APIs and require API credentials.

Results are saved to `harness-results` by default. Host feedback is off unless you enable it. See [configuration](docs/configuration.md) for references, feedback permissions, and review settings.

## Use in CI

Start with an example for your platform:

[GitHub Actions](examples/pipelines/github.yml) · [GitLab CI](examples/pipelines/gitlab.yml) · [Azure DevOps](examples/pipelines/azure-devops.yml) · [Bitbucket Pipelines](examples/pipelines/bitbucket.yml)

Replace the marked action/image placeholders and configure your CI secrets before running. See the [integration guide](docs/integrations.md) for setup, containers, and publishing.

This repository has [automatic PR reviews](.github/workflows/pr-review.yml) and
[maintainer-triggered review comments](.github/workflows/review.yml). See
[automatic review setup](docs/integrations.md#this-repositorys-automatic-pr-review)
and [mention testing](docs/integrations.md#test-reviews-in-this-repository).

Use the [BusinessHarness GitHub App](docs/github-app.md) for a dedicated bot
identity and concise findings in the PR Conversation tab, focused on security,
maintainability, correctness, and available specifications.

## Learn more

- [Configuration](docs/configuration.md) — providers, tickets, specs, and feedback.
- [Memory](docs/memory.md) — opt-in review history and follow-ups.
- [Errors and recovery](docs/errors.md) — exit codes and troubleshooting.
- [Security](SECURITY.md) and [architecture](docs/architecture.md) — how reviews are bounded and protected.

## Contribute

Use the SDK selected by [global.json](global.json), Git, and PowerShell 7. From the repository root:

```powershell
git submodule update --init --recursive
./scripts/Verify.ps1
./scripts/Smoke-Test.ps1
```

See [AGENTS.md](AGENTS.md) for contributor guidance and [docs/codex.md](docs/codex.md) for Codex review agents.

## License

Copyright (c) 2026 **Jihad Khawaja**. Free for personal, non-commercial use. Commercial or organizational use requires a paid license; contact [jihadkhawaja@outlook.com](mailto:jihadkhawaja@outlook.com).

See [LICENSE](LICENSE) for full terms and [third-party notices](THIRD-PARTY-NOTICES.md) for dependency licenses.
