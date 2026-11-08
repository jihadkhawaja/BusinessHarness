# Desktop chat workspace

BusinessHarness Desktop uses Avalonia **12.1.4** and **.NET 10** for Windows,
macOS and Linux. Chat about PR/MR metadata, committed diffs, Jira tickets,
Confluence pages and other configured GET JSON sources.

## Run

Install the SDK from `global.json`, then run from the repository root:

Avalonia's build services collect build-time telemetry by default. To opt out,
set `AVALONIA_TELEMETRY_OPTOUT=1` in the build environment before restoring/building.

```powershell
git submodule update --init --recursive
dotnet restore BusinessHarness.slnx --locked-mode
dotnet run --project src/BusinessHarness.Desktop -c Release --no-restore
```

This is a separate application from the CLI global tool. Linux needs a graphical
desktop and [Avalonia native dependencies](https://docs.avaloniaui.net/docs/overview/supported-platforms).
A Windows build does not verify macOS/Linux layouts or native dependencies.
Installers, signing, notarization and automatic updates are not included.

Publish a portable framework-dependent directory:

```powershell
dotnet publish src/BusinessHarness.Desktop -c Release --no-restore --self-contained false -p:UseAppHost=false -o artifacts/desktop
dotnet artifacts/desktop/BusinessHarness.Desktop.dll
```

Keep the entire output, including `runtimes/`, together. The destination needs
.NET **and ASP.NET Core 10.0.14 or newer within .NET 10** for the connector library.

## AI access

Select **API key / token**, choose a provider and enter a model available to your account (or set `model` in policy),
in **Settings → AI Provider**, then **Connect**. `HARNESS_MODEL` initializes the model field;
`HARNESS_PROVIDER=anthropic` initializes the provider selection.

| Mode | Environment credential | Access |
| --- | --- | --- |
| OpenAI API | `OPENAI_API_KEY` | Responses API; API billing |
| Anthropic API | `ANTHROPIC_API_KEY` | Messages API; API billing |
| ChatGPT plan | `OPENAI_CHATGPT_ACCESS_TOKEN` | Authorized plan access token; Responses HTTP/SSE |

Paste a key into the masked field, import an explicitly selected `.credentials` /
JSON file, or set the launching process's environment. Credentials entered or
imported in the UI remain session-only until you choose **Save settings**. Previously used credentials
remain in the bounded in-memory redaction set until the app closes, so rotating a
token does not expose it in later conversation history. Imports accept one `OpenAI` or
`Anthropic` section containing `Endpoint`, `ApiKey` and `ChatModel`, with only the
fixed official endpoint accepted. Switching access modes clears the input field.
Connect checks local configuration; the first message contacts the provider.

**Subscription limitations:** ChatGPT mode requires a token provisioned through the
official [Sign in with ChatGPT plan usage flow](https://developers.openai.com/siwc/token-sharing-open-source/sign-in).
The **Account** switch shows the planned sign-in options, which are currently disabled. Browser sign-in, client registration and automatic refresh are **not implemented**.
A normal Codex session token is not a substitute. Renew credentials through the
official flow and restart the app when needed. Eligibility/quota depend on account
and preview availability. There is no automatic fallback to API billing. See
[inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
and [preview constraints](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations).
Signed-in Codex/Claude CLI sessions and Claude subscriptions are not connected.
Anthropic access requires an API key; see
[Anthropic credential use](https://code.claude.com/docs/en/legal-and-compliance#authentication-and-credential-use).
The app does not inspect other applications' credential files.

## Save settings

Choose **Save settings** after applying your connections. The app saves the selected
AI provider/model, trusted policy path, Jira site and account, and entered provider
and app credentials. It restores the saved configuration when opened by the same
OS user. Connect/Use token applies app fields to the current session first; unsaved
fields must be applied before saving. Imported or environment-only credentials are
saved only when explicitly present in the app's session settings. Changing or
disconnecting a connection updates the saved copy only after **Save settings** is
selected again.

The settings file is encrypted with AES-256-GCM using a random key protected by
the current user's Windows DPAPI, macOS Keychain, or Linux Secret Service/keyring.
Linux requires libsecret and an unlocked desktop keyring. If secure storage is
unavailable, saving fails without a plaintext fallback. **Forget saved settings**
removes the saved ciphertext and OS-protected key after confirmation, while leaving
the current session open. This protects data at rest; it does not prevent another
process running as the same unlocked OS user from accessing that user's vault.
Chats, attachments and environment-only secrets are not saved.

## Connect Jira and Confluence

In **Settings → Add an app → Jira & Confluence**, enter your Jira Cloud site
(`https://your-site.atlassian.net/`), Atlassian account email and personal API
token, then select **Connect**. Credentials stay in memory unless you save settings;
access is checked when you add a ticket or page. Use **Disconnect** to clear the
active connection. Create a personal token in your Atlassian account and use it
instead of your account password. API tokens grant the permissions of the issuing
user; use an appropriately restricted account. Direct tenant endpoints require
an unscoped API token; scoped Atlassian tokens use a different gateway.

Atlassian documents API tokens for personal scripts and manual REST calls, and
states that distributed cloud apps collecting customer API tokens do not meet its
cloud-app requirements. This direct mode is intended for personal use; review
[Atlassian's guidance](https://developer.atlassian.com/cloud/jira/platform/basic-auth-for-rest-apis/)
before distributing an app that collects them.

Use the paperclip beside the message box to add a Jira issue key or numeric
Confluence page ID. The selected item is sent to the AI provider with your chat.
The app reads Jira and Confluence directly from your selected Atlassian site; no
connection service or browser OAuth flow is required. Jira, GitHub and GitLab
access is read-only and scoped by the permissions on your own tokens.

## Connect more apps

In **Settings → Add an app → More apps**, choose a provider from the pinned
AgentConnector catalog. Enter its personal access token, API key or provisioned OAuth access token and, for an
instance-based provider, its trusted HTTPS API base. Choose **Use connection**;
then choose a read action, fill its fields and select **Add to chat**. The app
makes that request directly, without a connector server, and sends only the
selected, bounded result as an attachment. Each catalog provider has at least
one read action. Linear, monday.com, Shopify, Dropbox, Google Analytics and Cohere
use the [fixed reads listed in the connector configuration](configuration.md#fixed-connector-reads).
Connector tokens and API bases are included only when you explicitly save
encrypted settings. Obtain and renew access tokens with the provider.

Pipelines use the same pinned catalog through explicit trusted `referenceSources`
policy and CI-managed environment credentials. See [connector reference
configuration](configuration.md#agentconnector-reference-actions). Pipelines
have their own credentials. Untrusted PR text can supply only a validated
identifier; it cannot choose a connector, endpoint, credential or query expression.

## Add other context

- **Paperclip → Pull Request:** paste a GitHub or GitLab URL. Set the matching
  GitHub or GitLab read token separately in **Settings → Add an app → GitHub** or **GitLab**, then select **Use token**. Tokens are never reused across hosts.
- **Paperclip → Local Diff:** choose a repository and base/head refs. Only
  committed changes are read; nothing is fetched, checked out or executed.
- **Paperclip → Ticket / Page → From policy:** attach comma-separated Jira keys
  or configured `SOURCE:ID` values using an explicit trusted JSON policy selected
  in **Settings → Add an app → Trusted policy**, then apply it with the AI **Connect** button. Start with the [desktop example](../examples/harness.desktop.policy.json).
  Policy-based Jira uses `JIRA_EMAIL` and `JIRA_API_TOKEN`; other sources
  use their configured environment variables. Desktop connections are independent of CLI policy.

Review attachment chips and remove individual items with ×. Press **Enter** to
send, **Shift+Enter** for a newline, or use the send arrow. **New chat** clears
history and attachments. **Escape** closes settings or the attachment dialog.
Settings opens as a full page with a **Back to chat** button.
The sidebar collapses for narrow windows and can be toggled from the top bar.

Reads are explicit: remote search, issue/document edits, posting comments and
approvals are not implemented. Confluence HTML is displayed as plain text. PR
metadata and local diffs are independent evidence labelled with SHAs; verify
that they describe the same revision. Extend policy-based connections through
[trusted GET sources](configuration.md#dynamic-reference-sources).

## Behavior and limits

Chats and attachments stay in process memory without local persistence. Failed,
cancelled or incomplete replies add no half-turn and retain the draft. **Stop**
cancels an operation. No requests are automatically retried. Removing an attachment affects future
requests; prior discussion remains. **New chat** asks before clearing an existing conversation, draft or attachments.
Errors appear in a dismissible dialog; failed messages keep their draft.

Limits: 40 messages (20 completed exchanges), 32,000 characters per message,
20 attachments and 400,000 characters across messages/attachments and assembled
provider context. Redaction expansion and JSON encoding count. Existing diff and
reference limits also apply. Responses/SSE are bounded to 1 MB with the policy
timeout. Plan mode uses `store=false`, `stream=true`, omitting the unsupported
`max_output_tokens`; local output limits still apply. Replies appear only on
complete success. Refusals, tools, truncation and interrupted streams fail closed.

Chat is **advisory**. Models get no tools or filesystem access. Chat does not
validate findings, run gates, use MAGICore memory, generate review reports or
authorize feedback; use CLI `review` for those contracts. All evidence is untrusted.
Plain-text rendering prevents model HTML/links from executing. Redaction is
heuristic; approve external data flows before sharing confidential information.
