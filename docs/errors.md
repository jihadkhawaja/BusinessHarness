# Error handling and integration recovery

Every expected operational failure is a typed `HarnessException` with a stable error ID, category and recovery hint. The CLI is the exception boundary: it converts unexpected OS, JSON and cancellation errors to safe diagnostics. Never branch your pipeline on free-text error messages; use exit codes or `error.json`.

| Exit | Error ID | Meaning | Recovery |
| --- | --- | --- | --- |
| 0 | — | Selected mode completed and gate passed | Read report mode and scope |
| 1 | — | Completed; findings or reference assessments failed the gate | Inspect review.json/review.md |
| 2 | BH1002 | Invalid arguments, policy, model, or missing variables | Run --help / validate; check secret availability |
| 3 | BH1003 | Git missing, invalid refs/history, unsupported content | Install Git; fetch source, target and merge history; review binary/link changes separately |
| 4 | BH1004 | HTTP 401/403 from a provider, reference source or code host | Check scope, token, model/project access and protected pipeline rules |
| 5 | BH1005 | HTTP/service/network failure | Check endpoint availability, DNS, proxy, TLS and quota; HTTP status is included |
| 6 | BH1006 | Refused, malformed, incomplete or invalid review/API data | Choose compatible model, adjust token limit, inspect issue fields |
| 7 | BH1007 | File/diff/response/issue size limit | Split the change or adjust bounded trusted policy |
| 8 | BH1008 | Filesystem access, locks, or linked output path | Use a dedicated writable real directory |
| 9 | BH1009 | User cancellation or total timeout | Rerun or adjust timeout within policy limits |
| 10 | BH1010 | Unexpected internal error | File a sanitized reproduction with version and correlation ID |

Invalid review comment templates use BH1002, including unknown, missing or repeated
placeholders, unsupported control characters, invalid UTF-8, and templates above
16,000 characters. Missing/unreadable template files and linked paths use BH1008.
Rendered comments above 60,000 characters use BH1007 and are withheld before any
host write. See [template configuration](configuration.md#review-comment-templates).

Invalid skill banks or selections use BH1002: null/duplicate entries, unknown
enabled names, invalid controls, and per-entry or aggregate size limits are
configuration errors. The same bounds apply after redaction. No provider or host
call is made for invalid CLI configuration. See [review skills](configuration.md#review-skills-bank).

Example `error.json`:

```json
{
  "schemaVersion": "1.0",
  "errorCode": "BH1004",
  "category": "Authentication",
  "stage": "review",
  "message": "Jira rejected authentication or access (HTTP 403).",
  "hint": "Check the secret variable, token scope, project/model access, and whether this pipeline is allowed to receive secrets.",
  "exitCode": 4,
  "correlationId": "1d2815a56abc4516ad00fd7979d581bbd"
}
```

Stage identifies the broad CLI phase: arguments, reports, configuration, or review. Category and service-specific message identify the failure within review. A correlation ID labels this invocation; it is not a provider request ID or a promise of a remote logging service.

The report directory's four runner-owned filenames are cleared at the start and on operational failure. Writes use temporary files followed by replacement. A failure cannot leave an old passing report in that directory. Use one dedicated output directory per concurrent invocation. If parsing fails before an output directory is selected, or if it is unwritable, stderr is the available diagnostic channel. Upload artifacts with your CI system's `always` equivalent.

HTTP failures do not log response bodies, stack traces, authorization headers, API keys or raw diffs. A 429 is reported with quota guidance. Requests are not automatically retried, avoiding repeated billable review requests; the pipeline owner controls retries. Timeouts cover Git, issue requests, provider generation and reporting.
