# Document Rendering References

Batch 3.10 uses DocumentFormat.OpenXml for editable DOCX output and QuestPDF for deterministic server-side PDF output. The renderer decision was checked against the following official references on 2026-09-22:

- QuestPDF quick start: https://www.questpdf.com/quick-start.html
- QuestPDF content direction: https://www.questpdf.com/api-reference/content-direction.html
- QuestPDF font management: https://www.questpdf.com/api-reference/text/font-management.html
- Google Fonts CSS distribution for Noto Sans Arabic: https://fonts.googleapis.com/css2?family=Noto+Sans+Arabic:wght@400

QuestPDF documents `ContentFromRightToLeft()` for RTL layout and recommends deploying additional language fonts rather than relying on host-installed fonts. The API therefore publishes `Fonts/NotoSansArabic-Regular.ttf` and registers it before rendering Arabic and Kurdish Sorani documents. DOCX paragraphs set the Open XML bidirectional property for those languages.
