# Document extraction V2

## Scope

Nexora keeps the existing `IDocumentExtractor` boundary and performs normal PDF/DOCX extraction locally in `Nexora.Integrations`:

- PdfPig `ContentOrderTextExtractor.GetText(page)` remains the PDF fast path.
- A bounded position-based fallback groups PdfPig words into visual lines, detects separated left/right clusters and reconstructs likely columns only when the fast result is suspicious or the page looks multi-column.
- Open XML walks `Body.Elements()` in document order and emits both paragraphs and table rows. Cells are flattened into readable `cell 1 | cell 2` lines without copying visual formatting.

## Quality gate and normalization

`IDetailedDocumentExtractor` returns `DocumentExtractionResult` with page/character/word counts, extraction method, quality score/category, printable/replacement/control ratios, average usable characters per page, repeated-line ratio and warning codes. Defaults live in `DocumentExtractionQualityOptions` and can be overridden under `Documents:Extraction:Quality`.

Text normalization is deterministic: line endings and whitespace are normalized; NUL/control waste, adjacent duplicate lines, decorative separators, isolated page numbers and repeated page boundary lines are removed when safe. Canonical extracted text is still persisted verbatim after these mechanical normalizations.

The worker records `extracting`, then accepts only `Good` local extraction as the fast path. Suspicious/failed local results include `OCR_MAY_BE_REQUIRED`, set the public resume state to `ocr_fallback`, and invoke the configured document fallback. The fallback result is normalized and evaluated by the same quality gate before it can be stored as `ready`.

## Automatic Gemini document fallback

`IDocumentOcrProvider` is the narrow Business boundary; `GeminiDocumentOcrProvider` lives in `Nexora.Integrations`. It is called only for suspicious/failed local extraction and submits the original PDF/DOCX bytes once as Gemini document input. The response contains faithful extracted text and a structured `ResumeProfile`, so the profile is persisted without a second raw-document profile call. If Gemini or the returned text/profile is unusable, the resume becomes `failed` and the API exposes `RESUME_EXTRACTION_FAILED` with a safe Vietnamese message. No Tesseract, native OCR runtime, Python service or paid document SaaS is used.

This is an internal-development Gemini integration only. OCR is not a normal-path dependency and no production provider decision is implied.

## Structured resume context

After local extraction, the first AI operation requests one structured `ResumeProfile` per resume version and stores it as JSON on `resumes` with the configured `gemini:<model>` identity plus prompt/schema versions. This lazy step avoids paying for a profile that is uploaded but never used; later operations reuse the cache. Changing the Gemini model, profile prompt version or profile schema version invalidates the cache. The profile contains summary, skills, experiences, education, projects, certifications and languages; absent information remains empty/null and is not invented.

`IResumeContextBuilder` creates bounded task-specific AI input:

- profile extraction: the normalized raw CV (the one intentional full-text call);
- resume analysis: profile details plus bounded JD;
- interview question generation: role/seniority, bounded JD and compact profile;
- answer evaluation: current question, answer and minimal profile;
- report generation: transcript and compact profile.

The canonical extracted text remains available for persistence/privacy workflows. Later AI operations use the compact profile/context by default instead of resending the raw CV.
