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
        AiOperationContext context,
        CancellationToken cancellationToken);
}
```

- **Global Retry Budget**: At most **two** provider calls per AI purpose (1 initial call + at most 1 repair or rate-limit retry). Provider adapter default `MaxAttempts` is set to 1 to eliminate nested retry multiplication.
- **Repair Cycle**: If attempt 1 fails server semantic validation with a repairable issue, attempt 2 injects a focused repair prompt specifying the exact contract violation and instructions to correct it.
- **Non-Repairable Failures**: Syntax parsing failures, malformed JSON, unrecoverable semantic violations, or non-transient HTTP errors fail fast without a second call.
- **Error Normalization**: Maps failures to canonical `BusinessException` with `BusinessErrorKind.ExternalFailure` and safe error codes (`AI_OUTPUT_INVALID`, `AI_RATE_LIMITED`, `AI_PROVIDER_UNAVAILABLE`).

Current internal implementation:

- `GeminiAiProvider` là provider AI của application cho Development/internal testing bằng development API key/quota. Gemini SDK/HTTP types chỉ ở `Nexora.Integrations`; model identifier từ configuration; key từ secret configuration; output map sang Nexora-owned schema.
- Automated tests that need deterministic provider behavior register a test-project-only provider (`TestAiProvider`); no test double is part of the application runtime or normal development configuration.

Gemini không phải production choice mặc định. DEC-01 vẫn quyết định production provider/model và budgets.

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
  - Exact four rubric criteria: `correctness`, `structure`, `completeness`, `clarity` (case-insensitive matching from provider, normalized lowercase and trimmed).
  - Sub-scores bounded strictly to 0–100 with non-blank grounded evidence.
  - Overall score computed server-side via canonical weights (Relevance/Correctness 40%, Structure 25%, Completeness 20%, Clarity 15%).
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

Record model, prompt/rubric/schema version, input/output token count, latency, estimated cost, job outcome and correlation ID. **DEC-01 does not block internal Gemini development testing or Phases 0–3.** It blocks real production AI traffic until Product Owner approves (a) production provider/model, (b) per-user daily/monthly budget, (c) global daily budget, (d) alert thresholds and (e) circuit-break action. Initial engineering defaults for development/staging only: alert at 70% configured daily budget, reject new AI jobs at 90%, circuit-break after 10 provider failures in 5 minutes; production values must replace them. Never run three model evaluations per answer in MVP without an explicit product experiment and budget approval.
