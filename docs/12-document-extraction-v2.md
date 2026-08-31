# Document extraction V2

## Scope

Nexora keeps the existing `IDocumentExtractor` boundary and performs normal PDF/DOCX extraction locally in `Nexora.Integrations`:

- PdfPig `ContentOrderTextExtractor.GetText(page)` remains the PDF fast path.
- A bounded position-based fallback groups PdfPig words into visual lines, detects separated left/right clusters and reconstructs likely columns only when the fast result is suspicious or the page looks multi-column.
- Open XML walks `Body.Elements()` in document order and emits both paragraphs and table rows. Cells are flattened into readable `cell 1 | cell 2` lines without copying visual formatting.

## Quality gate and normalization

`IDetailedDocumentExtractor` returns `DocumentExtractionResult` with page/character/word counts, extraction method, quality score/category, printable/replacement/control ratios, average usable characters per page, repeated-line ratio and warning codes. Defaults live in `DocumentExtractionQualityOptions` and can be overridden under `Documents:Extraction:Quality`.

Text normalization is deterministic: line endings and whitespace are normalized; NUL/control waste, adjacent duplicate lines, decorative separators, isolated page numbers and repeated page boundary lines are removed when safe. Canonical extracted text is still persisted verbatim after these mechanical normalizations.

The worker accepts only `Good` extraction results. Suspicious/failed results include `OCR_MAY_BE_REQUIRED` and fail safely instead of being treated as a successful extraction.

## OCR boundary

OCR is intentionally not implemented in this phase. No Tesseract, native OCR, cloud document AI, Gemini vision/file upload or OCR configuration is present. The quality warning is the future hand-off boundary.

## Structured resume context

After local extraction, the worker requests one structured `ResumeProfile` per resume version and stores it as JSON on `resumes` with model/prompt/schema versions. The profile contains summary, skills, experiences, education, projects, certifications and languages; absent information remains empty/null and is not invented.

`IResumeContextBuilder` creates bounded task-specific AI input:

- profile extraction: the normalized raw CV (the one intentional full-text call);
- resume analysis: profile details plus bounded JD;
- interview question generation: role/seniority, bounded JD and compact profile;
- answer evaluation: current question, answer and minimal profile;
- report generation: transcript and compact profile.

The canonical extracted text remains available for persistence/privacy workflows. Later AI operations use the compact profile/context by default instead of resending the raw CV.
