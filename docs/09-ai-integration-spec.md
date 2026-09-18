# AI Integration Specification — Nexora

**Status:** Approved implementation baseline; production provider/budgets deferred  
**Last updated:** 2026-09-08

## 1. Allowed AI capabilities in MVP

- Parse JD and CV to a defined schema.
- Compare CV/JD with evidence-based gaps and suggestions.
- Generate one mock-interview question from session context, with behavioral questions favoring evidence-rich scenarios.
- Evaluate an answer by explicit rubric, add structured STAR coaching when applicable, and generate a coaching report.
- Transform/evaluate STAR and case answers without inventing achievements.

Nexora is a practice product: AI output is coaching guidance, not hiring truth or real-interview covert assistance.

## AI output language — Vietnamese MVP

Nexora MVP is Vietnamese-only. Every user-facing AI-generated natural-language
field is written in Vietnamese regardless of the language of the role, CV, job
description, candidate answer, company, industry, scenario, transcript or
source document.

Input/source content may be Vietnamese, English, Chinese, Japanese or mixed.
Technical identifiers, technology names, proper nouns, acronyms, programming
languages and code terms may remain unchanged when appropriate. JSON field
names, enums, statuses, purpose names, schema versions, score scales, rubric
keys and competency codes remain canonical and are not translated.

The policy is centralized in `AiLanguagePolicy.VietnameseUserFacingInstruction`
and is included by every active user-facing structured operation: `resume.profile`,
both `resume.analysis` modes, `interview.first-question`,
`interview.followup`, `interview.evaluate`, `interview.report`,
`scenario.evaluate` and `star.evaluate`. Localization and user-selected output
language are out of scope for this MVP.

`GeminiDocumentOcrProvider` is intentionally excluded from this natural-language
output policy because it is a document-extraction fallback: its contract is to
preserve source text faithfully rather than translate it. The extracted source
is not a user-facing coaching output.

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

`AiRequest` contains purpose, approved prompt template version, untrusted user text delimiters, expected JSON schema, optional per-operation instructions, max tokens, correlation ID and an optional provider-neutral per-attempt reasoning-effort override. Provider-specific SDK types never escape to controller/business API. Provider exceptions may carry a provider-neutral retry hint; the Business layer does not reference a concrete provider.

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

- **Global Retry Budget**: At most **two** provider calls per AI purpose (1 initial call + at most 1 bounded retry). A retry may be a semantic repair, an existing transient/provider retry, or the explicit reasoning-budget fallback described below. Provider adapter default `MaxAttempts` is set to 1 to eliminate nested retry multiplication.
- **Repair Cycle**: If attempt 1 fails server semantic validation with a repairable issue, attempt 2 injects a focused repair prompt specifying the exact contract violation and instructions to correct it.
- **Provider Failure Retry**: Existing retryable provider failures retain the bounded retry behavior. A generic `InvalidResponse` retry keeps the configured reasoning policy; it is not automatically downgraded to low. Non-retryable HTTP, authentication and configuration failures remain terminal.
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

#### 2.2.1 Confirmed reasoning-budget fallback

The configured policy is always used for the first attempt. `interview.evaluate` and `star.evaluate` therefore remain **high** by default; this correction does not lower defaults or change their `MaxOutputTokens`. `resume.analysis` starts at an effective 4,096 output-token budget and may use one 8,192-token retry only after an explicit provider-reported truncation hint; both attempts keep reasoning **low** and both values are hard-capped by the operation abstraction.

DeepSeek may attach the provider-neutral `LowerReasoningEffort` retry hint only for an **effective high** policy and only when all of the following are true: `thinking` is enabled, `reasoning_effort` is `high`, `finish_reason` is `length`, the structured content is absent/blank or cannot be deserialized, and non-null usage metadata shows both `completion_tokens` and `reasoning_tokens` at or above the request `MaxOutputTokens` with reasoning at least as large as completion. Missing or inconclusive usage, a generic malformed response, a semantic validation failure, timeout, rate limit or other provider failure keeps existing retry semantics and never selects this fallback. A `finish_reason=length` response is rejected before content deserialization for every operation, including parseable JSON prefixes. Low-policy operations (`resume.analysis`, `interview.report`, `scenario.evaluate`) use the provider-neutral `OutputTruncated` hint for ordinary truncation; `resume.analysis` opts into its single larger-budget retry, while `scenario.evaluate` uses a 4,000-token budget on both of its bounded attempts. No truncated body is repaired or fabricated into a success.

When the executor receives that explicit high-policy hint on attempt 1, it keeps the original operation, input, schema, instructions and token budget, leaves semantic validation state unset, and performs exactly one retry with the per-attempt `Low` override. For `resume.analysis`, an explicit `OutputTruncated` hint instead selects the operation's validated 8,192-token second-attempt budget; `scenario.evaluate` retains its validated 4,000-token budget on an output-truncation retry. Semantic repair, timeout, rate-limit, auth/configuration and generic malformed responses do not select an operation-specific truncation budget. The override can only lower an enabled policy; it cannot enable disabled thinking, upgrade an effort, mutate configuration or change appsettings. `DeepSeekAiProvider` still makes one HTTP request per invocation (`MaxAttempts=1`), and `GeminiAiProvider` is validated to the same single-adapter-attempt boundary; both adapters map provider truncation metadata to the neutral hint before deserializing content. The global maximum therefore remains two calls and there is no third or hidden provider retry. A normal high-policy success remains one call, while a successful fallback reports `Attempts=2` and `RepairUsed=false`.

`RepairUsed` continues to mean that `BuildRepairInstructions` was used for a Nexora semantic repair. Semantic repair remains on the normal configured policy and is never treated as reasoning fallback. The fallback adds no additional default paid call; it is only the single, positively-triggered second attempt within the existing budget.

When thinking is disabled, `thinking.type=disabled` is sent and `reasoning_effort` is omitted. When enabled, `thinking.type=enabled` and one of `low`, `high` or manual-only `max` is sent. No operation defaults to `max`, and repair does not escalate effort. Unknown purposes or invalid policy values fail closed before an HTTP call.

The trusted system message contains operation metadata, the approved instructions and the exact Nexora-owned JSON schema. The untrusted CV/JD/answer/scenario text is sent only as the user message. The adapter requires nonblank `choices[0].message.content`, deserializes only that JSON content, ignores `reasoning_content`, and never strips fences or fabricates defaults. HTTP/network/timeout failures are normalized to `AiProviderFailureKind` without copying the provider body into exceptions.

For paid-provider safety, DeepSeek logs metadata-only usage telemetry when the response supplies it: purpose, provider-aware model version, thinking, reasoning effort, latency, prompt/cache hit/cache miss/completion/reasoning/total tokens and finish reason. Confirmed reasoning exhaustion is separately observable through safe metadata (`configuredEffort`, `finishReason`, `reasoningTokens`, `completionTokens`, `maxOutputTokens`, `outcome=reasoning_budget_exhausted`); the executor logs the corresponding effective low retry and reason. It never logs keys, authorization headers, prompts, candidate text, response content or `reasoning_content`, and missing usage is not a request failure. Pricing conversion remains outside the provider because DeepSeek pricing can change. DEC-01 still controls production provider/model and budgets and therefore blocks production AI enablement, not local development or integration tests.

The adapter and executor must never copy a provider response body, credential, prompt, or candidate answer text into an API response or log. Only safe diagnostics (`failureReason`, `stage`, `attempt`, `correlationId`) are recorded.

Document fallback is a separate `IDocumentOcrProvider` boundary. `GeminiDocumentOcrProvider` receives the original document only after the local extraction quality gate is suspicious/failed, and returns faithful extracted text plus the compact resume profile in one document-understanding response. It is not used for normal text PDF/DOCX extraction and is not a production OCR decision.

### 2.3 Resume analysis v2 modes

`resume.analysis` is one provider-neutral purpose with an explicit mode selected by the persisted analysis command. `job_targeted` uses the cached `ResumeProfile` plus the selected JobDescription and persists prompt/schema versions `resume-analysis-job-targeted-v3` / `analysis-job-targeted-v2`. It returns a 0-100 `matchScore`, grounded matched/missing skills, strengths, gaps, recommendations, section feedback and the required breakdown dimensions `technicalSkillMatch`, `experienceRelevance`, `impactEvidence`, `clarity`, `structure`.

`field_benchmark` uses the same cached profile plus the required `industry`, `targetRole` and `seniority` context, with no JobDescription. It persists prompt/schema versions `resume-analysis-field-benchmark-v3` / `analysis-field-benchmark-v2` and returns a 0-100 `readinessScore`, grounded strengths, gaps, recommendations, section feedback and `technicalFoundation`, `projectEvidence`, `experiencePresentation`, `impactAchievements`, `clarity`, `roleAlignment` breakdown dimensions. The mode is included in the operation metadata and must match the response; provider-specific fields or concepts do not enter Business/API contracts.

Both schemas are strict (`additionalProperties: false`) and require bounded collections, exact breakdown keys and server-side semantic validation. Strengths, gaps, recommendations and section feedback are non-empty; matched/missing skills may be empty when no evidence exists. A `finish_reason=length` response is rejected before deserialization; the existing executor may make one truncation retry at 8,192 tokens after the 4,096-token first attempt. Semantic repair and provider retries remain within the global two-call ceiling. A valid cached profile is serialized as a per-analysis snapshot with its model/prompt/schema provenance; OCR fallback profiles remain unversioned until the canonical text profile operation regenerates them, so no profile AI call is made again for each analysis mode once the cache is current.

`sectionFeedback` is a bounded array of grounded strings. Each analysis also persists the nullable `RubricVersion` selected by its operation and exposes it with the other safe execution metadata; `ProfileSnapshot` remains private and is excluded from privacy exports.

## 3. Job contract

| Job | Input | Output/state | Quota point |
| --- | --- | --- | --- |
| ExtractResume | stored file ID | uploaded → extracting → ready/failed, with `ocr_fallback` when the local quality gate rejects text | none |
| AnalyzeResume | resume version + explicit `job_targeted`/`field_benchmark` context | analysis completed/failed with mode-specific schema and versioned profile snapshot | Theo entitlement riêng nếu plan định nghĩa; không dùng nhầm interview reservation |
| StartInterview | interview context | session starting → active hoặc failed | API transaction reserves + creates `starting` session/job; worker success transaction persists validated first usable question + consumes + activates; terminal pre-activation failure transaction voids + fails |
| EvaluateAnswer | question/answer snapshot | evaluation + canonical next primary (Q1–Q3) when allowed | included in session entitlement |
| ContinueInterview | active session after free cap | server entitlement check, then one paid primary or evidence-driven behavioral follow-up in the same session | no new session reservation |
| BuildReport | completing session | one immutable report; completed/terminal job failure | included after interview consume; retry idempotent và không charge thêm |

StartInterview retries must not duplicate session, question, usage event or job effect. Consumption is determined by successful question persistence plus `starting → active`, not browser receipt. After activation, disconnect/refresh/navigation/no answer or later AI/report failure does not automatically void usage; terminal report failure retains the BR-08 adjustment/support rule.

For A7, the first three issued questions are always server-owned primary
questions with topics `self_introduction`, `behavioral_star` and
`motivation_role_fit`. The answer path never pre-generates a paid question and
does not call the follow-up operation merely because `sequence > 1`. After the
third answered question, `/interviews/{id}/continue` re-checks the current
entitlement before any paid AI call. Its first paid question is generated by
the regular first-question operation with a server-selected topic; a follow-up
operation is reserved for a paid behavioral primary whose saved STAR evaluation
has missing elements. A failed continuation leaves prior answers intact and
does not consume another interview reservation.

## 4. Output quality, safety and recovery rules

- **Canonical Rubric Validation**: Structured evaluation output must pass JSON schema + server semantic validation:
  - Scoring operations (`interview.evaluate`, `interview.report`, `scenario.evaluate`, `star.evaluate`) must explicitly return root `scoreScale: "0-100"`. Missing or non-exact values are repairable semantic failures; variants such as `1-5` or `0-100 ` are not normalized into validity.
  - Exact four rubric criteria: `correctness`, `structure`, `completeness`, `clarity` (case-insensitive matching from provider, normalized lowercase and trimmed).
  - Sub-scores bounded strictly to 0–100 with non-blank grounded evidence.
  - Overall score computed server-side via canonical weights (Relevance/Correctness 40%, Structure 25%, Completeness 20%, Clarity 15%).
- **Validation integrity and operation ownership**:
  - Deterministic normalization may repair representation only, such as trimming text or normalizing an absent STAR component to `detected = false`, `score = 0`, empty evidence and neutral feedback.
  - The server must never invent candidate-specific strengths, gaps, recommendations, report findings, scenario dimensions, evidence or coaching content to make an invalid AI response pass.
  - Missing required semantic content is repairable once through the structured executor; a second invalid response becomes the normalized `AI_OUTPUT_INVALID` failure for that workflow, except for the narrow `interview.evaluate` coaching fallback documented below.
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
  - **Canonical STAR Instructions (`StarSemantics.CanonicalInstructions`)**: Unified single source of truth embedded in both `interview.answer.evaluate` (`interview-eval-v10`) and `star.evaluate` (`star-eval-v4`). Defines clear technical examples for Situation (system state/incident), Task (candidate's specific duty/ownership), Action (investigation/profiling/indexing/caching/code changes), and Result (latency reduction, recovery, metrics, lessons).
  - **Question-Focus Detachment**: Evaluator must scan the entire answer for all four components. Phrasing of the interview question must not constrain component detection.
  - **Evidence-First Extraction**: For every component:
    - If concrete evidence exists: `detected = true`, `evidence = "<exact quote>"`, `score = 1..100`.
    - If absent: `detected = false`, `evidence = ""`, `score = 0`.
    - Invariant: `detected = false` with `score > 0` or `detected = true` with empty evidence is strictly rejected by `StarComponentValidator`.
  - **Server-Authoritative Calculation**: Server recomputes `overallScore` using canonical weights (Situation 20%, Task 20%, Action 35%, Result 25%) and determines `missingElements` (`!detected || score < 60`).
  - **Follow-up Aware Evaluation Context**:
  - `ResumeContextBuilder` supplies `question-sequence`, `is-follow-up`, `question-topic`, and `followup-target-elements` derived from persisted question lineage and previous missing elements.
  - `is-follow-up` is server-derived from `InterviewQuestion.kind`; `sequence > 1` never establishes follow-up semantics. When `is-follow-up: true`, the evaluator evaluates all present components while giving special attention to how targeted missing elements from the parent answer are addressed.
- **Follow-up Failure Isolation**:
  - A paid continuation follow-up is generated only after the preceding answer and its evaluation are already persisted. AI rate-limit, invalid JSON or provider timeout therefore leaves that answer intact and returns the normalized provider/AI failure; it must not fabricate a deterministic question or discard the saved answer.
  - Retrying the same logical continuation with its `Idempotency-Key` may retry the bounded AI operation because no continuation resource is recorded until a question is persisted. A successful retry creates at most one follow-up through the session/key concurrency constraints.
  - **Report STAR story summary**:
  - Realtime answer evaluation remains independent per answer for immediate coaching. `report.starSummary` is a deterministic story-level view built from the persisted evaluations; it does not trigger another AI operation.
  - Questions are grouped by the explicit `ParentQuestionId` chain. Independent primary questions form independent story roots; sequence ordering never merges them. The report resolves each answer to its root and selects the deterministic story with the most applicable evaluations (ties use the lowest root sequence), preserving the existing public summary shape.
  - For each Situation/Task/Action/Result component, only valid `detected = true` evaluations with nonblank evidence contribute. The merged component keeps the highest grounded score across the chain; if none is available it is `score = 0`, `detected = false`. A follow-up can strengthen a component but cannot lower unrelated primary-story evidence.
  - `componentAverages` keeps its existing API name but contains the four merged story component scores. `applicableAnswers` remains the count of valid applicable answer evaluations contributing to the summary (not the number of independent stories). The story `averageScore` is recomputed server-side with the canonical 20/20/35/25 STAR weights; persisted per-answer `overallScore` values are not averaged.
  - `recurringIssues` is recomputed from the merged components (`detected = false` or `score < 60`); raw per-answer `missingElements` are never unioned, so a follow-up can resolve an earlier missing component. Coaching priorities use feedback attached to the selected merged/weak component evidence, in deterministic weakness order, distinct and capped at three; historical `coachingTips` are not concatenated.
  - **Per-answer coaching (`interview.evaluate`)**:
    - The same structured call returns rubric scores, feedback, STAR (when applicable), `strengths`, `improvements`, grounded `improvedAnswer` and optional separate `sampleAnswer`; no second rewrite/sample call is made. The versioned prompt/schema pair is `interview-eval-v10` / `interview-eval-v6` for this additive nullable field.
    - `strengths` contains 1–3 nonblank grounded items when the answer demonstrates positive evidence, or an empty collection when no grounded positive evidence is demonstrated and all rubric scores are below 60. `improvements` contains 1–3 unique, nonblank actionable items (maximum 500 characters each); whitespace and case-insensitive duplicates are normalized before cardinality validation. `improvedAnswer` is nonblank and capped at 4,000 characters.
    - The three answer concepts remain distinct: `candidateAnswer` is the factual source for candidate-specific claims; `improvedAnswer` is a rewrite grounded only in that source; `sampleAnswer` is an educational illustration that may use hypothetical role/project/technology/result details. Sample details never become rubric evidence, scores, strengths, candidate facts, transcript, report findings, Skill Profile, progress or recommendations. Persist/return the sample only as its separately named field and keep any presentation explicitly labeled illustrative.
    - `sampleAnswer.framework` is one of `star`, `self_intro`, `technical` or `direct`. Choose STAR for past-experience/behavioral, conflict, leadership, teamwork, problem-solving or achievement questions; use `self_intro` for introductions/motivation/fit; use `technical` for technical-knowledge questions; use `direct` otherwise. Do not force STAR on every question. A STAR sample requires nonblank Situation, Task, Action and Result sections and a coherent concise full answer. Non-STAR samples do not include STAR sections. Write natural, concise, professional Vietnamese; use modest plausible hypothetical details, avoid gratuitously precise/impressive metrics, and use impersonal technical examples (e.g. “Ví dụ, trong một hệ thống…”) rather than attributing imagined experience to the candidate.
    - Sample validity is independent of core evaluation validity. Missing, malformed, unsupported-framework, blank or otherwise unusable sample data normalizes to null/absent while an otherwise-valid core evaluation remains usable. Do not add a third provider call, increase or otherwise change the operation's existing `MaxOutputTokens` (currently 6,000), or change provider/reasoning settings for this feature. Historical evaluation JSON with no sample field remains valid and reads as null/absent.
    - Strength factual grounding uses only the submitted candidate answer. The question and context may inform relevance and rubric scoring, but neither they nor raw model-generated rubric evidence can establish that the candidate stated or performed a fact. Generic evaluative prose (for example, analytical, structured, or thể hiện khả năng) may paraphrase grounded evidence, while concrete technologies, metrics, responsibilities, achievements, and experience claims remain answer-grounded. Each improvement must state a substantive direct action; directive prefixes such as nên or có thể do not count by themselves.
    - When the original answer is available to Business validation, strengths and the improved answer must retain meaningful evidence from it. New numeric values, technologies, achievements or experience are rejected; missing evidence is described as a suggestion to add it, never fabricated. Repair instructions identify whether the failure is the improvements shape/actionability or improved-answer grounding and explicitly exclude question, rubric, JD, resume and Career Goal context as candidate facts.
    - If the terminal semantic result has valid rubric, evidence, feedback, STAR, strengths and improvements but only `improvedAnswer` remains ungrounded or fabricated, the server may replace that presentation field with a deterministic value containing only the submitted candidate answer (bounded to the existing 4,000-character contract). If the semantic repair call instead ends with provider `InvalidResponse`, the executor may apply the same recovery only to the prior already-typed result and only when the prior semantic failure was one of those improved-answer failures. In both paths it reruns full normalization/validation; this adds no provider call and never recovers invalid core evaluation fields. Malformed provider content is never parsed or used. The normalized coaching is persisted with the answer evaluation and remains provider-neutral.
    - A terminal semantic repair may itself regress a previously-valid field. The executor retains only the current and immediately previous typed raw/validation pairs (a pair is shifted only after a provider response deserializes and reaches semantic validation). It tries operation-owned recovery on the current pair first, then on the previous pair only when its prior validation failed; only an `IsValid` recovery is accepted. For interview improved-answer grounding, this preserves the previous evaluation as a whole and replaces only `improvedAnswer`; outputs are never merged across attempts. Other failure reasons remain fail-closed unless that operation explicitly implements recovery. Safe structured recovery logs include purpose, previous/current failure reasons, attempt and correlation ID, never answer text or raw output.
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

DeepSeek structured-response failures log metadata only: purpose, finish reason,
content length, structural failure stage, and correlation ID. The diagnostics
distinguish malformed envelope, missing/invalid choices, message/content shape,
blank content, and content deserialization failures; raw model content, prompts,
candidate answers, credentials and secrets are never logged. Public/business
failure mapping remains generic.
