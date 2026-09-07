# AI Integration Specification — Nexora

**Status:** Approved implementation baseline; production provider/budgets deferred  
**Last updated:** 2026-09-07

## 1. Allowed AI capabilities in MVP

- Parse JD and CV to a defined schema.
- Compare CV/JD with evidence-based gaps and suggestions.
- Generate one mock-interview question from session context, with behavioral questions favoring evidence-rich scenarios.
- Evaluate an answer by explicit rubric, add structured STAR coaching when applicable, and generate a coaching report.
- Transform/evaluate STAR and case answers without inventing achievements.

Nexora is a practice product: AI output is coaching guidance, not hiring truth or real-interview covert assistance.

## 2. Provider contract and structured execution layer

```csharp
public interface IAiProvider
{
    string ModelVersion { get; }

    Task<T> GenerateStructuredAsync<T>(
        AiRequest request,
        CancellationToken cancellationToken);
}
```

`AiRequest` contains purpose, approved prompt template version, untrusted user text delimiters, expected JSON schema, optional per-operation instructions, max tokens and correlation ID. Provider-specific SDK types never escape to controller/business API.

### 2.1 Structured AI execution layer (`IStructuredAiExecutor`)

Business logic interacts with AI operations through `IStructuredAiExecutor` and definitions from `AiOperationCatalog`:

```csharp
public interface IStructuredAiExecutor
{
    Task<AiExecutionResult<T>> ExecuteAsync<T>(
        AiOperationDefinition<T> operation,
        string untrustedInput,
        AiOperationContext context,
        CancellationToken cancellationToken);
}
```

- **Global Retry Budget**: At most **two** provider calls per AI purpose (1 initial call + at most 1 repair or rate-limit retry). Provider adapter default `MaxAttempts` is set to 1 to eliminate nested retry multiplication.
- **Repair Cycle**: If attempt 1 fails server semantic validation with a repairable issue, attempt 2 injects a focused repair prompt specifying the exact contract violation and instructions to correct it.
- **Non-Repairable Failures**: Syntax parsing failures, malformed JSON, unrecoverable semantic violations, or non-transient HTTP errors fail fast without a second call.
- **Error Normalization**: Maps failures to canonical `BusinessException` with `BusinessErrorKind.ExternalFailure` and safe error codes (`AI_OUTPUT_INVALID`, `AI_RATE_LIMITED`, `AI_PROVIDER_UNAVAILABLE`).

Current internal implementation:

- `GeminiAiProvider` remains the default text provider for Development/internal testing with a development API key/quota. Gemini SDK/HTTP types stay in `Nexora.Integrations`; the model identifier comes from configuration; the key comes from secret configuration; output is mapped to Nexora-owned schemas.
- `DeepSeekAiProvider` is an optional official DeepSeek V4 Flash text adapter. Select it with `Ai:Provider=deepseek`; the default remains `gemini`. The adapter calls `https://api.deepseek.com/chat/completions` directly with the OpenAI-compatible Chat Completions contract, `thinking`, `reasoning_effort` and JSON mode. It is approved for local/development evaluation only; it is not a production provider decision.
- Provider selection is fail-closed: only `gemini` and `deepseek` are accepted and there is no automatic fallback between providers. `Nexora.Api` and `Nexora.Worker` resolve the same selected `IAiProvider`.
- Automated tests that need deterministic provider behavior register a test-project-only provider (`TestAiProvider`); no test double is part of the application runtime or normal development configuration.

The document extraction fallback is deliberately independent: `IDocumentOcrProvider` remains `GeminiDocumentOcrProvider` even when `Ai:Provider=deepseek`. Local development therefore keeps both Gemini (OCR) and DeepSeek (text) credentials in secret configuration.

### 2.2 DeepSeek reasoning policy and request safety

DeepSeek uses one provider call per `IAiProvider.GenerateStructuredAsync` invocation (`Ai:DeepSeek:MaxAttempts=1`). `IStructuredAiExecutor` owns the initial call and at most one repair call, so a repair never multiplies into nested provider retries. The operation policy is explicit and configuration-bound:

| Purpose | Thinking | Effort | Development rationale |
| --- | --- | --- | --- |
| `resume.profile` | disabled | — | inexpensive extraction |
| `resume.analysis` | enabled | low | useful comparison with bounded reasoning |
| `interview.first-question` | disabled | — | deterministic generation |
| `interview.evaluate` | enabled | high | core rubric/STAR semantic correctness |
| `interview.followup` | disabled | — | deterministic follow-up generation |
| `interview.report` | enabled | low | report synthesis with bounded reasoning |
| `scenario.evaluate` | enabled | low | scenario coaching with bounded reasoning |
| `star.evaluate` | enabled | high | standalone STAR semantic correctness |

This table is the authoritative cost-aware baseline for the provider. In particular, `interview.report` and `scenario.evaluate` use `low`; no older example or test label that says otherwise should override this Section 7 policy.

When thinking is disabled, `thinking.type=disabled` is sent and `reasoning_effort` is omitted. When enabled, `thinking.type=enabled` and one of `low`, `high` or manual-only `max` is sent. No operation defaults to `max`, and repair does not escalate effort. Unknown purposes or invalid policy values fail closed before an HTTP call.

The trusted system message contains operation metadata, the approved instructions and the exact Nexora-owned JSON schema. The untrusted CV/JD/answer/scenario text is sent only as the user message. The adapter requires nonblank `choices[0].message.content`, deserializes only that JSON content, ignores `reasoning_content`, and never strips fences or fabricates defaults. HTTP/network/timeout failures are normalized to `AiProviderFailureKind` without copying the provider body into exceptions.

For paid-provider safety, DeepSeek logs metadata-only usage telemetry when the response supplies it: purpose, provider-aware model version, thinking, reasoning effort, latency, prompt/cache hit/cache miss/completion/reasoning/total tokens and finish reason. It never logs keys, authorization headers, prompts, candidate text, response content or `reasoning_content`, and missing usage is not a request failure. Pricing conversion remains outside the provider because DeepSeek pricing can change. DEC-01 still controls production provider/model and budgets and therefore blocks production AI enablement, not local development or integration tests.

The adapter and executor must never copy a provider response body, credential, prompt, or candidate answer text into an API response or log. Only safe diagnostics (`failureReason`, `stage`, `attempt`, `correlationId`) are recorded.

Document fallback is a separate `IDocumentOcrProvider` boundary. `GeminiDocumentOcrProvider` receives the original document only after the local extraction quality gate is suspicious/failed, and returns faithful extracted text plus the compact resume profile in one document-understanding response. It is not used for normal text PDF/DOCX extraction and is not a production OCR decision.

## 3. Job contract

| Job | Input | Output/state | Quota point |
| --- | --- | --- | --- |
| ExtractResume | stored file ID | uploaded → extracting → ready/failed, with `ocr_fallback` when the local quality gate rejects text | none |
| AnalyzeResume | resume/JD versions | analysis completed/failed | Theo entitlement riêng nếu plan định nghĩa; không dùng nhầm interview reservation |
| StartInterview | interview context | session starting → active hoặc failed | API transaction reserves + creates `starting` session/job; worker success transaction persists validated first usable question + consumes + activates; terminal pre-activation failure transaction voids + fails |
| EvaluateAnswer | question/answer snapshot | evaluation + next action | included in session entitlement |
| BuildReport | completing session | one immutable report; completed/terminal job failure | included after interview consume; retry idempotent và không charge thêm |

StartInterview retries must not duplicate session, question, usage event or job effect. Consumption is determined by successful question persistence plus `starting → active`, not browser receipt. After activation, disconnect/refresh/navigation/no answer or later AI/report failure does not automatically void usage; terminal report failure retains the BR-08 adjustment/support rule.

## 4. Output quality, safety and recovery rules

- **Canonical Rubric Validation**: Structured evaluation output must pass JSON schema + server semantic validation:
  - Scoring operations (`interview.evaluate`, `interview.report`, `scenario.evaluate`, `star.evaluate`) must explicitly return root `scoreScale: "0-100"`. Missing or non-exact values are repairable semantic failures; variants such as `1-5` or `0-100 ` are not normalized into validity.
  - Exact four rubric criteria: `correctness`, `structure`, `completeness`, `clarity` (case-insensitive matching from provider, normalized lowercase and trimmed).
  - Sub-scores bounded strictly to 0–100 with non-blank grounded evidence.
  - Overall score computed server-side via canonical weights (Relevance/Correctness 40%, Structure 25%, Completeness 20%, Clarity 15%).
- **Validation integrity and operation ownership**:
  - Deterministic normalization may repair representation only, such as trimming text or normalizing an absent STAR component to `detected = false`, `score = 0`, empty evidence and neutral feedback.
  - The server must never invent candidate-specific strengths, gaps, recommendations, report findings, scenario dimensions, evidence or coaching content to make an invalid AI response pass.
  - Missing required semantic content is repairable once through the structured executor; a second invalid response becomes the normalized `AI_OUTPUT_INVALID` failure for that workflow.
  - Each operation owns its required arrays and cardinality. Provider adapters enforce transport/schema mechanics and must not add generic array-count instructions that conflict with an operation contract.
  - Generated first and follow-up questions longer than 2,000 characters are invalid and enter repair handling; provider output is not silently truncated into a valid question.
- **Canonical ResumeProfile validation**:
  - `ResumeProfile` normalization and semantic validity are shared by `resume.profile`, cached-profile reads and Gemini document fallback.
  - `summary` is optional. A profile is useful when at least one normalized summary, skill, experience, education, project, certification or language remains within the configured bounds.
  - A fully empty profile is repairable during structured generation and invalid when returned from the one-call document fallback; no placeholder profile fields are synthesized.
- **Server-Authoritative STAR Normalization & Semantic Contract**:
  - Non-behavioral questions: `star.applicable` is server-normalized to `false` without failing evaluation; STAR component details are suppressed.
  - Behavioral questions: If model omits STAR or returns `applicable = false`, the executor marks the issue repairable and attempts repair once.
  - Standalone `star.evaluate` (Scenario/STAR feature): strictly requires `applicable = true`.
  - **Canonical STAR Instructions (`StarSemantics.CanonicalInstructions`)**: Unified single source of truth embedded in both `interview.answer.evaluate` (`interview-eval-v4`) and `star.evaluate` (`star-eval-v3`). Defines clear technical examples for Situation (system state/incident), Task (candidate's specific duty/ownership), Action (investigation/profiling/indexing/caching/code changes), and Result (latency reduction, recovery, metrics, lessons).
  - **Question-Focus Detachment**: Evaluator must scan the entire answer for all four components. Phrasing of the interview question must not constrain component detection.
  - **Evidence-First Extraction**: For every component:
    - If concrete evidence exists: `detected = true`, `evidence = "<exact quote>"`, `score = 1..100`.
    - If absent: `detected = false`, `evidence = ""`, `score = 0`.
    - Invariant: `detected = false` with `score > 0` or `detected = true` with empty evidence is strictly rejected by `StarComponentValidator`.
  - **Server-Authoritative Calculation**: Server recomputes `overallScore` using canonical weights (Situation 20%, Task 20%, Action 35%, Result 25%) and determines `missingElements` (`!detected || score < 60`).
- **Follow-up Aware Evaluation Context**:
  - `ResumeContextBuilder` supplies `question-sequence`, `is-follow-up`, and `followup-target-elements` derived from previous missing elements.
  - When `is-follow-up: true`, the evaluator evaluates all present components while giving special attention to how targeted missing elements from prior answers are addressed.
- **Follow-up Failure Isolation**:
  - Follow-up question generation failure must **never** fail or discard an already-evaluated candidate answer.
  - When follow-up question generation fails (AI rate-limit, invalid JSON, provider timeout), `PracticeService` falls back to a deterministic, Nexora-owned follow-up question (<= 2,000 chars) and persists the evaluation successfully.
- **Context Budgeting**:
  - `ResumeContextBuilder` prioritizes candidate answer text, question text, and metadata above background context.
  - Target JD and resume profile summaries are compacted to ensure the candidate's answer is never crowded out or truncated.
- **Preserve candidate facts**: If a metric/result is absent, suggest how to quantify it; never fabricate achievements.
- **Privacy & Logging Invariants**: Treat CV/JD/answer as untrusted input. Never log candidate answer text, prompt bodies, or raw provider responses.

## 5. Rubric baseline

| Criterion | Weight | Evidence expectation |
| --- | --- | --- |
| Relevance/technical correctness | 40% | Addresses skill and JD requirement. |
| Structure | 25% | Logical sequence; STAR for behavioural question. |
| Completeness | 20% | Covers question/expected points. |
| Communication clarity | 15% | Clear, concise, specific language. |

Server computes weighted overall score from validated sub-scores. Store rubric version; comparisons across changed rubrics must display the version.

## 6. Cost and observability

Record model, prompt/rubric/schema version, input/output token count, latency, estimated cost, job outcome and correlation ID. **DEC-01 does not block internal Gemini or optional DeepSeek development testing or Phases 0–3.** It blocks real production AI traffic until Product Owner approves (a) production provider/model, (b) per-user daily/monthly budget, (c) global daily budget, (d) alert thresholds and (e) circuit-break action. Initial engineering defaults for development/staging only: alert at 70% configured daily budget, reject new AI jobs at 90%, circuit-break after 10 provider failures in 5 minutes; production values must replace them. Never run three model evaluations per answer in MVP without an explicit product experiment and budget approval.
