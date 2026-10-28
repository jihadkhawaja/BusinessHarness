# CI integration and distribution

The CLI contract is identical across vendors. A completed review returns 0/1; integration errors return 2–10. Always retain the report directory even on failure. No organization/repository IDs, hosted image names, model IDs or tokens have been invented for you; replace the marked placeholders before deploying.

## NuGet.org releases

The public package ID is `BusinessHarness`; its executable is `businessharness`.
Install an exact version with `dotnet tool install --global BusinessHarness --version 0.1.0`.
The package includes the personal non-commercial license, third-party notices and
MAGICore's Apache 2.0 license. Public distribution does not grant commercial rights.

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
3. Merge the release changes into `main`, create tag `v0.1.0` on the verified release
   commit, and publish its GitHub Release. Observe the `Publish NuGet` workflow and
   verify the package on NuGet.org; the public index and download badges can lag.

New policies for private repositories can require activation within seven days.
See [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
The GitHub repository can remain private while the NuGet package is public;
repository links and the GitHub build badge may require repository access.
Publication intentionally fails if the package version already exists. If a push
succeeded before a later step failed, inspect NuGet.org before rerunning; never
replace a released version or move its tag. Use a new version for package changes.

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

- **GitHub:** copy [github.yml](../examples/pipelines/github.yml), replace the action ref, configure `HARNESS_MODEL`, and store `OPENAI_API_KEY` as an environment secret under an `ai-review` environment with required reviewers. `fetch-depth: 0` supplies the source, target and merge base. The example restricts forks but still requires trusted workflow definitions and environment approvals. Change provider/key for Claude. Configure Jira variables or trusted `referenceSources` for automatic discovery. The action supplies its GitHub token for metadata access. Set `feedback: comment` or `feedback: auto` and `pull-requests: write` to publish feedback. A separate eligible bot token can be passed through `github-token`. The composite uses Bash and requires a runner with Bash and Git (GitHub-hosted Ubuntu is recommended).
- **GitLab:** copy/include [gitlab.yml](../examples/pipelines/gitlab.yml) in a trusted pipeline, define `HARNESS_IMAGE` as an image digest, and configure protected/masked provider variables and model. The image entrypoint is cleared so GitLab can execute its shell script. Configure `GITLAB_TOKEN` for MR metadata access and an eligible bot with `api` scope if enabling `--feedback comment` or `--feedback auto`. Standard detached MR pipelines are supported by the shown refs. Merged-result/merge-train policies should pass their intended source commit explicitly. Protected secrets may be unavailable unless both branches and the user meet GitLab's protected-resource rules; the runner reports this as configuration failure rather than silently skipping review.
- **Azure DevOps:** use [azure-devops.yml](../examples/pipelines/azure-devops.yml) as a branch build-validation pipeline for Azure Repos. The example assumes the normal PR merge-commit checkout, using parent 1 as target and parent 2 as source. If your checkout strategy differs, pass explicit fetched commit IDs instead. Define the image, model and secret variable. Protect the YAML/template and secret access through your organization policies.
- **Bitbucket:** copy [bitbucket.yml](../examples/pipelines/bitbucket.yml), replace the image digest and configure model plus secured provider variable. Bitbucket's PR preparation can merge the target into the working tree; the example deliberately passes `BITBUCKET_COMMIT` as source and the destination commit as base, rather than using that synthetic checkout HEAD. Fetch the destination commit outside the runner first. Keep fork pipelines isolated from secrets.

Reports are always written locally; optional `comment` or `auto` feedback publishes only after validation. See [configuration](configuration.md) for dynamic sources, private hosts and feedback semantics. Azure DevOps and Bitbucket retain local reports and exit-code gates; native metadata/feedback adapters currently cover GitHub and GitLab. SARIF can be uploaded separately with the hosting platform’s approved publisher and required permissions. Do not execute model output as workflow commands.

## Persistent memory and follow-up events

Enable trusted `memory` settings and provide a persistent writable directory separate from the reviewed checkout and reports. Ephemeral runners and containers need an explicitly provisioned memory volume; a temporary workspace does not provide long-term storage. See [memory setup](memory.md), the [memory policy example](../examples/harness.memory.policy.json), and the [GitHub comment workflow](../examples/pipelines/github-follow-up.yml). Authenticated Jira/GitLab webhook receivers can invoke `follow-up --event PATH`, followed by a normal review, or `review --event PATH` for immediate re-review. Repository and actor routing remain controlled by trusted policy. No webhook endpoint is registered automatically.
