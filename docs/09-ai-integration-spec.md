# AI Integration Specification — Nexora

**Status:** Approved implementation baseline; production provider/budgets deferred  
**Last updated:** 2026-08-21

## 1. Allowed AI capabilities in MVP

- Parse JD and CV to a defined schema.
- Compare CV/JD with evidence-based gaps and suggestions.
- Generate one mock-interview question from session context.
- Evaluate an answer by explicit rubric and generate a coaching report.
- Transform/evaluate STAR and case answers without inventing achievements.

Nexora is a practice product: AI output is coaching guidance, not hiring truth or real-interview covert assistance.

## 2. Provider contract

```csharp
public interface IAiProvider
{
    Task<T> GenerateStructuredAsync<T>(
        AiRequest request,
        CancellationToken cancellationToken);
}
```

`AiRequest` contains purpose, approved prompt template version, untrusted user text delimiters, expected JSON schema, max tokens and correlation ID. Adapter normalises provider errors; provider-specific SDK types never escape to controller/business API.

Initial implementation được phép:

- `FakeAiProvider` cho deterministic unit/integration tests.
- `GeminiAiProvider` cho development/testing bằng development API key/quota. Gemini SDK classes chỉ ở `Nexora.Integrations`; model identifier từ configuration; key từ secret configuration; output map sang Nexora-owned schema.

Gemini không phải production choice mặc định. DEC-01 vẫn quyết định production provider/model và budgets.

## 3. Job contract

| Job | Input | Output/state | Quota point |
| --- | --- | --- | --- |
| ExtractResume | stored file ID | extracted/failed | none |
| AnalyzeResume | resume/JD versions | analysis completed/failed | Theo entitlement riêng nếu plan định nghĩa; không dùng nhầm interview reservation |
| StartInterview | interview context | session starting → active hoặc failed | API transaction reserves + creates `starting` session/job; worker success transaction persists validated first usable question + consumes + activates; terminal pre-activation failure transaction voids + fails |
| EvaluateAnswer | question/answer snapshot | evaluation + next action | included in session entitlement |
| BuildReport | completing session | one immutable report; completed/terminal job failure | included after interview consume; retry idempotent và không charge thêm |

StartInterview retries must not duplicate session, question, usage event or job effect. Consumption is determined by successful question persistence plus `starting → active`, not browser receipt. After activation, disconnect/refresh/navigation/no answer or later AI/report failure does not automatically void usage; terminal report failure retains the BR-08 adjustment/support rule.

## 4. Output quality and safety rules

- Structured output must pass JSON schema + server semantic validation (score 0–100, required evidence, no missing criterion).
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

Record model, prompt/rubric/schema version, input/output token count, latency, estimated cost, job outcome and correlation ID. **DEC-01 does not block development, fake/Gemini development testing or Phases 0–3.** It blocks real production AI traffic until Product Owner approves (a) production provider/model, (b) per-user daily/monthly budget, (c) global daily budget, (d) alert thresholds and (e) circuit-break action. Initial engineering defaults for development/staging only: alert at 70% configured daily budget, reject new AI jobs at 90%, circuit-break after 10 provider failures in 5 minutes; production values must replace them. Never run three model evaluations per answer in MVP without an explicit product experiment and budget approval.
