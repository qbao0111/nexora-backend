# Document extraction V2

## Scope

Nexora keeps the existing `IDocumentExtractor` boundary and performs normal PDF/DOCX extraction locally in `Nexora.Integrations`:

- PdfPig `ContentOrderTextExtractor.GetText(page)` remains the PDF fast path.
- A bounded position-based fallback groups PdfPig words into visual lines, detects separated left/right clusters and reconstructs likely columns only when the fast result is suspicious or the page looks multi-column.
- Open XML walks `Body.Elements()` in document order and emits both paragraphs and table rows. Cells are flattened into readable `cell 1 | cell 2` lines without copying visual formatting.

## Quality gate and normalization

`IDetailedDocumentExtractor` returns `DocumentExtractionResult` with page/character/word counts, extraction method, quality score/category, printable/replacement/control ratios, average usable characters per page, repeated-line ratio and warning codes. Defaults live in `DocumentExtractionQualityOptions` and can be overridden under `Documents:Extraction:Quality`.

Text normalization is deterministic: line endings and whitespace are normalized; NUL/control waste, adjacent duplicate lines, decorative separators, isolated page numbers and repeated page boundary lines are removed when safe. Canonical extracted text is still persisted verbatim after these mechanical normalizations.

The worker records `extracting`, then accepts only `Good` local extraction. Suspicious/failed local results include `TEXT_EXTRACTION_INSUFFICIENT` and become terminal `failed`; the outbox job is marked processed and a final realtime notification is persisted. There is no external document fallback or repeated OCR attempt.

## Unreadable documents and privacy

Gemini document OCR, its interface and registrations have been removed. Original PDF/DOCX bytes remain inside the local extraction/storage pipeline and are never sent to Gemini. Scan/image-only PDFs, insufficient text, corrupt or unsupported documents fail safely with `RESUME_EXTRACTION_FAILED`: `Không thể đọc nội dung CV. Vui lòng tải lên PDF có văn bản có thể chọn hoặc sao chép, hoặc file DOCX. CV dạng ảnh hoặc bản scan hiện chưa được hỗ trợ.`

FE/Mobile must stop polling on `failed`, show the safe message, and allow a new upload. Analysis may start only on `ready`. Historical `GeminiOcr` metadata and the legacy `ocr_fallback` status value remain readable; no existing text, profile, immutable analysis snapshot or uploaded file is rewritten/deleted by this change. A reclaimed legacy extraction job uses the same local-only path.

## Structured resume context

After local extraction, the first AI operation requests one structured `ResumeProfile` per resume version and stores it as JSON on `resumes` with the selected provider/model identity (for example `deepseek:<model>`) plus prompt/schema versions. This lazy step avoids paying for a profile that is uploaded but never used; later operations reuse the cache. Changing the provider/model, profile prompt version or profile schema version invalidates the cache. Historical unversioned profiles regenerate through the selected text provider when needed. The profile contains summary, skills, experiences, education, projects, certifications and languages; absent information remains empty/null and is not invented.

`IResumeContextBuilder` creates bounded task-specific AI input:

- profile extraction: the normalized raw CV (the one intentional full-text call);
- resume analysis: profile details plus bounded JD;
- interview question generation: role/seniority, bounded JD and compact profile;
- answer evaluation: current question, answer and minimal profile;
- report generation: transcript and compact profile.

The canonical extracted text remains available for persistence/privacy workflows. Later AI operations use the compact profile/context by default instead of resending the raw CV.

## Gemini reference audit and Render secret retirement

With `Ai:Provider=deepseek`, API and Worker register only DeepSeek as `IAiProvider`; `GeminiAiProvider` is not registered and Gemini options/credentials are not bound or required. The Gemini text implementation and its contract tests remain for explicit legacy `Ai:Provider=gemini` configurations; no automatic provider switch exists. Historical project-log entries describe previous behavior only. DeepSeek receives extracted text or compact structured context, never PDF/image input; no image-to-text capability is claimed.

After this PR is independently reviewed, merged and deployed, verify both API and Worker run the new commit with `Ai:Provider=deepseek`, process a text PDF/DOCX to `ready`, reject a scanned CV as `failed`, and complete profile/analysis through DeepSeek. Only then remove obsolete `Ai__Gemini__ApiKey` / `Ai__Gemini__Model` (and equivalent env-group secrets) from both Render services. Restart and verify health plus the same flow again. Do not remove any secret from a separate service still explicitly using the legacy Gemini text provider. This task does not modify Render or print secrets.
