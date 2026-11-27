# Academic template library — deterministic evidence (issue #1260)

Sample outputs for four representative configurations out of the **22 base templates / 230
distinct previewable designs** in `apps/api/AcademicTemplates/AcademicTemplateRegistry.cs`.
Every artifact in this folder was rendered from the committed registry with the registry's
deterministic synthetic sample content — no real research, no student data, and no fabricated
references (every bibliography row is explicitly marked `format sample — not a real source`
and carries no DOI, ISBN or resolvable URL).

## Representative configurations

| Configuration id | Design hash | Language | Direction | Files |
| --- | --- | --- | --- | --- |
| `classic-thesis.times.150.classic.center` | `a8c670cb4490` | en | ltr | `.docx` (editable), `.pdf` (accessible), `.html` (preview), `-page-1/2.png` |
| `journal-double-column.times.numeric.narrow` | `e1bdf8652d7d` | en | ltr | same set |
| `kurdish-thesis-ckb.naskh.latin.150` | `4a7d94b230ac` | ku (ckb) | rtl | same set |
| `arabic-dissertation-ar.naskh.band.indic.outside` | `4f551438d996` | ar | rtl | same set |

The design hashes are the first 12 hex characters of the SHA-256 canonical design string
(`AcademicDesignSpec.CanonicalDesignHash`); identity fields (template id, configuration id,
display name) are deliberately excluded, so equal hashes would mean duplicate designs — CI
asserts all 230 hashes are pairwise distinct (no alias padding).

## What the four examples demonstrate

- **classic-thesis**: wide 108 pt spine margin, roman front-matter folio restarting decimal in
  the body, dotted-leader TOC, hanging-indent references, disclosure slot.
- **journal-double-column**: two-column grid, numeric citation markers, top-right folio,
  running head.
- **kurdish-thesis-ckb**: full RTL abstract/bidi layout with Latin digits; the DOCX carries
  `<w:bidi>` + right-aligned gutter so Word mirrors the page correctly.
- **arabic-dissertation-ar**: RTL with institutional band cover, arabic-indic digits
  (U+0660 block) in folios, outside-spine page numbering, Arabic reshaping in PDF text.

## Reproduction

From a checkout with the .NET 8 SDK and Liberation/DejaVu fonts installed:

```bash
# 1) Re-render all four formats for the examples above (writes into a target dir):
#    a small console referencing Taslim.Api iterates the four ids and calls
#    AcademicTemplateDocxRenderer / AcademicTemplatePdfRenderer / AcademicPreviewRenderer
#    with AcademicSampleContent.For(spec). See AcademicSampleExportTests for the
#    programmatic assertions (twips margins, BiDi, pgNumType, footnote parts, PDF pages).
# 2) Page screenshots:
pdftoppm -png -r 90 -f 1 -l 2 <configuration-id>.pdf <configuration-id>-page
# 3) Full-library smoke + determinism + CI parity gates:
dotnet test apps/api.Tests --filter "FullyQualifiedName~Academic"
TASLIM_ACADEMIC_REGEN=1 dotnet test apps/api.Tests --filter AcademicTemplateCatalogParity  # only after intentional registry changes
```

The committed web snapshot `apps/web/src/components/academic/templates/academicTemplateCatalog.generated.json`
is byte-guarded by `AcademicTemplateCatalogParityTests`; the gallery renders the identical
design set client-side.

## Privacy & integrity statements

- No provider, model, routing or backend-cost information appears in the catalog snapshot,
  API DTOs, previews, DOCX properties or PDF metadata (word-boundary scan tests enforce this;
  `AcademicCatalogPrivacyGuard` scrub throws on any leak).
- Templates include a *statement of generative-AI use* slot that follows institutional
  disclosure policy. Nothing in this feature hides authorship or evades AI policy — it is a
  typesetting and export product.
- Re-export/reformat of saved research via a versioned template id + snapshot is a layout
  operation (`AcademicReExportPolicy` returns `AllowedWithoutGenerationCharge = true`); it
  never triggers a provider call, so no extra generation charge can accrue.
