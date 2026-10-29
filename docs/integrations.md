# CI integration and distribution

For a first-time setup, start with the illustrated
[GitHub / GitLab + Jira walkthrough](ci-setup.md). It covers credentials, pipeline
setup, the first review, and troubleshooting; this page is the integration reference.

The CLI contract is identical across vendors. A completed review returns 0/1; integration errors return 2–10. Always retain the report directory even on failure. No organization/repository IDs, hosted image names, model IDs or tokens have been invented for you; replace the marked placeholders before deploying.

## PR review scope

Every platform and provider uses **merge-base(target, source) → source head**:
the aggregate PR/MR diff across all commits, including additions, modifications,
and deletions. Three surrounding lines provide context. Unchanged files,
target-only changes, and uncommitted content remain outside provider input.
Findings must anchor to actual added lines. Jira and other linked specifications
use the same evidence; unseen code and runtime behavior remain `not_verifiable`.

Schedule on PR/MR creation and source updates without file-path filters. Rerun
after metadata edits that change linked references; hosts without such pipeline
events need a manual rerun or authenticated webhook integration. Secret access
still follows the platform's approval and fork protections.

## This repository's automatic PR review

[pr-review.yml](../.github/workflows/pr-review.yml) schedules reviews for PRs
targeting `main` on open, reopen, source push, title/description edit, and readiness
for review. Its `pull_request_target` workflow builds only the trusted workflow
revision from the base branch, then fetches PR commits as Git objects. It never
checks out or executes PR files, hooks, scripts, tests, or submodules. Fresh
GitHub-hosted runners post `--feedback comment` results to the PR Conversation
tab and retain reports for seven days. The host token has pull-requests write
permission. Configure the [GitHub App](github-app.md) for the BusinessHarness bot
identity; otherwise comments use github-actions[bot].
The separate [mention workflow](../.github/workflows/review.yml) remains available
for [maintainer-requested review comments](#test-reviews-in-this-repository).

Use the `ai-review` environment, restricted to `main`, with `HARNESS_MODEL` and
the selected provider's API key. `HARNESS_PROVIDER` defaults to `openai`; use
`anthropic` with `ANTHROPIC_API_KEY` for Claude. For Jira discovery, set variable
`HARNESS_JIRA_SITE` and secrets `JIRA_EMAIL` and `JIRA_API_TOKEN`, then include
issue keys in PR metadata. Without Jira configuration or other configured
sources, reviews contain no reference assessments. Custom acceptance-criteria
fields and other sources require an explicit `--config` from the trusted checkout.
Missing credentials/model configuration fail the job. Memory is disabled for
automatic runs. Fork PRs can trigger the automatic review, but cannot supply
executable workflow, harness, or policy code to the job.

Protect privileged workflow changes through maintainer review and configure
environment approvals where supported. Keep credentials unavailable to PR branch
workflows. Automatic runs send bounded review context to the configured provider;
they do not replace the build/test checks in [ci.yml](../.github/workflows/ci.yml).
The event runs in base-branch context and does not publish a separate status on
the PR source SHA; add a trusted status publisher before using it as a required
PR-head merge check. The workflow becomes active after reaching `main`.

## NuGet.org releases

The public package ID is `BusinessHarness`; its executable is `businessharness`.
Install the latest stable release with `dotnet tool install --global BusinessHarness`.
Update an existing installation with `dotnet tool update --global BusinessHarness`.
For reproducible CI runs, pin a reviewed version, for example
`dotnet tool install --global BusinessHarness --version 0.1.0`.
The package includes the BusinessHarness license, third-party notices and
MAGICore's Apache 2.0 license. See [LICENSE](../LICENSE) for BusinessHarness usage
and commercial licensing terms, including the employer exception.

[publish-nuget.yml](../.github/workflows/publish-nuget.yml) runs on a **published,
stable GitHub Release**, starting with `v0.1.0`. Pushing a tag alone does not publish
a package. The tag must be `vMAJOR.MINOR.PATCH`, match `Directory.Build.props` and
point to a commit on `main`. The workflow performs the full verification and offline
smoke checks, then installs and runs the packaged tool before uploading an immutable
workflow artifact. A separate job with no source checkout exchanges GitHub OIDC for
a short-lived NuGet credential and pushes that artifact. The package is attached to
the GitHub Release after successful publication. PRs do not run this workflow.

Maintainer setup:

1. Create the GitHub environment `nuget`, restricted to version tags `v*` (tag
   rules, not branch rules). Set its variable `NUGET_USER` to `jihadkhawaja`, the
   NuGet.org username that creates the policy. Use required reviewers if the
   repository's plan supports them. Protect workflow changes, release creation
   and version tags so only trusted maintainers can publish.
2. On NuGet.org, sign in as that user and create a **Trusted Publishing** policy:
   repository owner `jihadkhawaja`, repository `BusinessHarness`, workflow file
   `publish-nuget.yml` (filename only), environment `nuget`. Select the intended
   package owner. Where scopes are offered, allow new packages and new versions
   only for the exact package pattern `BusinessHarness`. No stored API key is needed.
3. Set `<Version>` in `Directory.Build.props` to the next unused stable version
   (for example, `0.1.3`), then merge the release changes into `main`. Run
   `./scripts/Verify.ps1` and `./scripts/Smoke-Test.ps1` on that commit before
   creating its matching tag (for example, `v0.1.3`) and publishing its GitHub
   Release. Observe the `Publish NuGet` workflow and verify the package on
   NuGet.org; the public index and download badges can lag.

New policies for private repositories can require activation within seven days.
See [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
The GitHub repository can remain private while the NuGet package is public;
repository links and the GitHub build badge may require repository access.
Publication intentionally fails if the package version already exists. If a push
succeeded before a later step failed, inspect NuGet.org before rerunning; never
replace a released version or move its tag. Use a new version for package changes.

If validation reports that the release tag does not match `Directory.Build.props`,
inspect the file **at the tagged commit**: the workflow checks out that tag, not
the latest `main`. For example, `v0.1.1` containing `<Version>0.1.0</Version>`
cannot publish `0.1.1`. Rerunning the failed workflow after changing `main` still
checks out the same old commit. Commit the next unused version (for example,
`0.1.3`) on `main`, verify it, and create the matching new tag and GitHub Release.
Keep existing release tags unchanged.

## Local packaging and containers

From a trusted release checkout:

```powershell
git submodule update --init --recursive
dotnet restore BusinessHarness.slnx --locked-mode
dotnet build BusinessHarness.slnx -c Release --no-restore
dotnet test --solution BusinessHarness.slnx -c Release --no-build
dotnet pack src/BusinessHarness.Cli -c Release --no-build -o artifacts/packages
docker build --pull -t YOUR_REGISTRY/businessharness:0.1.0 .
```

Use the release workflow above to publish to NuGet.org, or push the image to your registry using a separate trusted release process. Pin consumers to an exact package version, action commit, or image digest. Publishing credentials do not belong in review jobs. The GitHub action fetches its trusted action revision and initializes the pinned submodule outside the reviewed checkout before building; the caller does not have to install .NET manually. Container consumers need no .NET installation.

Example Linux container invocation after building the local image:

```bash
mkdir -p /tmp/businessharness-results
docker run --rm --read-only --cap-drop=ALL --security-opt=no-new-privileges \
  --user "$(id -u):$(id -g)" \
  --mount "type=bind,source=$PWD,target=/repo,readonly" \
  --mount "type=bind,source=/tmp/businessharness-results,target=/reports" \
  -e OPENAI_API_KEY -e HARNESS_MODEL \
  businessharness:0.1.0 review --repo /repo --base origin/main --head HEAD --output /reports
```

The report mount must be writable by the selected UID. The source mount must include `.git` and all required objects. A linked worktree may reference Git metadata outside its folder; include that metadata at matching paths or use a normal CI clone. Git is bundled in the image. For restricted egress, allow the selected provider API and, only when configured, your Jira Cloud tenant, approved reference API endpoints and GitHub/GitLab host APIs. Building the image also needs Microsoft container registries, nuget.org and OS package repositories.

## Platform specifics

- **GitHub:** copy [github.yml](../examples/pipelines/github.yml), replace the action ref, configure `HARNESS_MODEL`, and store `OPENAI_API_KEY` as an environment secret under an `ai-review` environment with required reviewers. `fetch-depth: 0` supplies the source, target and merge base. The trusted-base example schedules PR creation, source updates, and metadata edits, including forks. Never execute PR code; protect workflow definitions and environment secret access. Set HARNESS_PROVIDER and the matching key for Claude. Configure Jira variables or trusted `referenceSources` for automatic discovery. The action supplies its GitHub token for metadata access. Set `feedback: comment` or `feedback: auto` and `pull-requests: write` to publish feedback. A separate eligible bot token can be passed through `github-token`. The composite uses Bash and requires a runner with Bash and Git (GitHub-hosted Ubuntu is recommended).
- **GitLab:** copy/include [gitlab.yml](../examples/pipelines/gitlab.yml) in a trusted pipeline, define `HARNESS_IMAGE` as an image digest, and configure protected/masked provider variables and model. The image entrypoint is cleared so GitLab can execute its shell script. Configure `GITLAB_TOKEN` for MR metadata access and an eligible bot with `api` scope if enabling `--feedback comment` or `--feedback auto`. Same-project MR reviews run automatically. Detached pipelines use CI_COMMIT_SHA; merged-result/merge-train pipelines use CI_MERGE_REQUEST_SOURCE_BRANCH_SHA to exclude synthetic target changes. Forks retain secret isolation. Protected secrets may be unavailable unless both branches and the user meet GitLab's protected-resource rules; the runner reports this as configuration failure rather than silently skipping review.
- **Azure DevOps:** use [azure-devops.yml](../examples/pipelines/azure-devops.yml) as a branch build-validation pipeline for Azure Repos. The example assumes the normal PR merge-commit checkout, using parent 1 as target and parent 2 as source. If your checkout strategy differs, pass explicit fetched commit IDs instead. Define the image, model and secret variable. Protect the YAML/template and secret access through your organization policies.
- **Bitbucket:** copy [bitbucket.yml](../examples/pipelines/bitbucket.yml), replace the image digest and configure model plus secured provider variable. Bitbucket's PR preparation can merge the target into the working tree; the example deliberately passes `BITBUCKET_COMMIT` as source and the destination commit as base, rather than using that synthetic checkout HEAD. Fetch the destination commit outside the runner first. Keep fork pipelines isolated from secrets.

Reports are always written locally; optional `comment` or `auto` feedback publishes only after validation. See [configuration](configuration.md) for dynamic sources, private hosts and feedback semantics. Azure DevOps and Bitbucket retain local reports and exit-code gates; native metadata/feedback adapters currently cover GitHub and GitLab. SARIF can be uploaded separately with the hosting platform’s approved publisher and required permissions. Do not execute model output as workflow commands.

To customize published review comments, set the composite action's
`comment-template` input to an absolute path from a trusted checkout or
runner-managed directory. CLI/container callers use `--comment-template PATH`.
Alternatively, set `reviewCommentTemplate` in trusted policy. Start from the
[default template](../src/BusinessHarness.Infrastructure/Templates/review-comment.md); see
[template configuration](configuration.md#review-comment-templates) for placeholders,
limits, and precedence. The action resolves relative paths from its own directory,
so prefer absolute paths. Do not select a template from the PR branch being reviewed.

## Pipeline skill selection

Store the [example skills policy](../examples/harness.skills.policy.json) in your
protected pipeline checkout and adapt its bank to your team. Set `HARNESS_MODEL`
and the provider's secret as usual. In the trusted-base
[GitHub example](../examples/pipelines/github.yml), the review step can select:

```yaml
with:
  config: ${{ github.workspace }}/examples/harness.skills.policy.json
  skills: dotnet,api-security
```

The `skills` action input overrides `enabledSkills` in that explicit policy.
Omit it to use the policy defaults, select `database` for a migration review,
or pass `none` for a review without supplemental skills. The config path must
refer to your trusted checkout; never check out the PR to load its skill bank.
Use protected pipeline variables or maintainer-controlled dispatch inputs when
varying the selected names per request. Selection does not change job permissions
or grant approval authority.

GitLab, Azure DevOps, Bitbucket and local/container invocations use the same CLI:

```bash
businessharness review --repo /repo --base "$TARGET_SHA" --head "$SOURCE_SHA" \
  --config /trusted/policy.json --skills dotnet,database --output /reports
```

Provision `/trusted/policy.json` through trusted pipeline configuration and mount
it read-only for containers. The enabled subset is supplied to either provider;
the model applies relevant skills on demand within the existing diff review.
There is no skill script execution or external skill fetching. See
[the skills contract](configuration.md#review-skills-bank) for limits and precedence.

## Persistent memory and follow-up events

Enable trusted `memory` settings and provide a persistent writable directory separate from the reviewed checkout and reports. Ephemeral runners and containers need an explicitly provisioned memory volume; a temporary workspace does not provide long-term storage. See [memory setup](memory.md), the [memory policy example](../examples/harness.memory.policy.json), and the [GitHub comment workflow](../examples/pipelines/github-follow-up.yml). Authenticated Jira/GitLab webhook receivers can invoke `follow-up --event PATH`, followed by a normal review, or `review --event PATH` for immediate re-review. Repository and actor routing remain controlled by trusted policy. No webhook endpoint is registered automatically.

## Test reviews in this repository

The repository's [review workflow](../.github/workflows/review.yml) accepts a
Conversation comment on an open PR from `jihadkhawaja` containing:

```text
@businessharness please review this PR and check the error handling.
```

`@businessharness` is a configured text trigger, not a registered GitHub account.
GitHub Actions runs the harness and posts a regular PR Conversation comment.
With the [GitHub App](github-app.md) configured, the author is
`businessharness[bot]`; otherwise it is `github-actions[bot]`.
This workflow supports Conversation comments, including edits; inline diff
comments and submitted review bodies do not trigger it. Each accepted event can
publish another review. Comments from other accounts and comments on issues are
skipped. The CLI also checks the complete mention and actor against the
[trusted policy](../.github/businessharness.policy.json).

The workflow must first be merged into `main`. In the `ai-review` GitHub
environment, configure secret `OPENAI_API_KEY` and variable `HARNESS_MODEL` with
an API model supporting native structured outputs. Keep the environment restricted
to `main`; its API credentials must not be available to PR branch workflows.
Protect changes to workflow and policy files through maintainer review. To permit
another caller, update both the workflow's actor checks and the policy allowlist.
No separate bot account, GitHub App, self-hosted runner, or approval permission is
required for comment feedback.

For a manual test, choose **Actions → BusinessHarness review → Run workflow**,
select `main`, and enter an open PR number. Alternatively:

```powershell
gh workflow run review.yml --repo jihadkhawaja/BusinessHarness --ref main -f pr_number=123
```

Replace `123` with the actual PR number. Manual runs are restricted to the same
maintainer account. Both paths use fresh GitHub-hosted runners, a pinned trusted
harness revision, and policy from the workflow's `main` commit. PR commits are
fetched as Git objects for comparison; their files are never checked out or
executed. The provider receives the bounded review context and the selected
comment. Feedback posts to the Conversation tab with `comment`, so this workflow does not approve PRs or request
changes automatically. Fork PRs can be reviewed when explicitly requested by the
allowed maintainer; fork authors cannot invoke the secret-bearing job themselves.

The workflow enables session memory in the runner's temporary directory to ingest
the triggering comment. That memory is discarded after the job, so later comments
do not recall earlier runs. For durable history, provision isolated persistent
storage and follow the [persistent follow-up example](../examples/pipelines/github-follow-up.yml).

Check the workflow log for `Follow-up remembered with source and actor provenance.`
on accepted mentions and `Pull/merge request feedback published.` on a completed
publication. Reports are uploaded for seven days even when the review gate fails.
A gate failure can publish findings and still fail the job; configuration, API,
stale PR metadata, or response-validation failures can prevent publication. Report
artifacts contain review context and should be handled accordingly.
