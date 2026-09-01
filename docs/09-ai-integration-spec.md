# AI Integration Specification — Nexora

**Status:** Approved implementation baseline; production provider/budgets deferred  
**Last updated:** 2026-08-25

## 1. Allowed AI capabilities in MVP

- Parse JD and CV to a defined schema.
- Compare CV/JD with evidence-based gaps and suggestions.
- Generate one mock-interview question from session context, with behavioral questions favoring evidence-rich scenarios.
- Evaluate an answer by explicit rubric, add structured STAR coaching when applicable, and generate a coaching report.
- Transform/evaluate STAR and case answers without inventing achievements.

Nexora is a practice product: AI output is coaching guidance, not hiring truth or real-interview covert assistance.

## 2. Provider contract

```csharp
public interface IAiProvider
{
    string ModelVersion { get; }

    Task<T> GenerateStructuredAsync<T>(
        AiRequest request,
        CancellationToken cancellationToken);
}
```

`AiRequest` contains purpose, approved prompt template version, untrusted user text delimiters, expected JSON schema, max tokens and correlation ID. Adapter normalises provider errors; provider-specific SDK types never escape to controller/business API.

Current internal implementation:

- `GeminiAiProvider` là provider AI của application cho Development/internal testing bằng development API key/quota. Gemini SDK/HTTP types chỉ ở `Nexora.Integrations`; model identifier từ configuration; key từ secret configuration; output map sang Nexora-owned schema.
- Automated tests that need deterministic provider behavior may register a test-project-only provider; no test double is part of the application runtime or normal development configuration.

Gemini không phải production choice mặc định. DEC-01 vẫn quyết định production provider/model và budgets.

Provider failures use Nexora-owned categories (`configuration`, `authentication`, `rate-limited`, `timeout`, `unavailable`, `invalid-response`) and safe messages. The adapter must not copy a provider response body, credential, or SDK exception text into an API response. Retry only transient or invalid structured responses, use a configured overall timeout and cap attempts at three.

Normal internal validation uses the real browser/API/Worker/Gemini path with owner-supplied data. Automated tests remain network-free by replacing the adapter inside the test project where a critical state invariant needs deterministic output. Gemini development traffic is separate from production enablement and does not resolve DEC-01.

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

## 4. Output quality and safety rules

- Structured output must pass JSON schema + server semantic validation (exact rubric criteria `correctness`, `structure`, `completeness`, `clarity`; score 0–100; required grounded evidence; no missing criterion).
- Behavioral answer evaluation also returns `star.applicable`. When true, Situation/Task/Action/Result components use 0–100 scores with grounded evidence and concise coaching. When false, STAR component details remain null/empty; technical explanations must keep the generic rubric only.
- The server computes STAR overall score with Situation 20%, Task 20%, Action 35% and Result 25%, and aggregates final report `starSummary` from persisted answer evaluations rather than asking the model to perform arithmetic.
- Preserve candidate facts: if a metric/result is absent, suggest how to quantify it; never fabricate achievements.
- Keep `evidence` references to answer spans where possible. If no evidence exists, classify feedback as suggestion, not fact.
- Treat CV/JD/answer as untrusted input: delimiter, instruction hierarchy, no tool access, no secrets in prompt, max input size.
- On invalid output/timeout, retry boundedly then mark job failed with user-friendly message; do not expose raw provider error.

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
