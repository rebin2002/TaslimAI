# Language Pack Architecture

This foundation adds an installable, versioned language-pack boundary without claiming that every Taslim module is translated.

## Registry and manifest

`apps/web/src/lib/languagePackRegistry.ts` owns a small registry of resources. Each resource has a schema version, pack version, stable ID, canonical BCP 47 language tag, document direction, fallback pack, completeness state, review state, review flags, and an optional measured coverage ledger. A future pack is installed through `LanguagePackRegistry.install()` without editing the core translation catalog.

The built-in registry adapts the existing English, Arabic, and Sorani catalogs and adds the first `zh-CN` resource. The legacy catalog in `apps/web/src/lib/i18n.ts` stays untouched, because it remains the authoritative key universe and other lanes own module catalogs inside it.

Install-time identity rules are deliberately strict, because a pack ID is also a storage-key segment and a fallback-chain target:

- IDs match `^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$`, so path segments and non-ASCII labels are rejected.
- Direction must be exactly `ltr` or `rtl`.
- A pack cannot fall back to itself, and a partial pack must request human review.
- Re-installing a known ID throws unless the caller passes `replaceVersion` naming the installed version. Without that, one late `install()` could silently retarget the `en` root that every partial pack resolves through.
- `missingFallbacks()` reports packs whose fallback target is not installed, which is otherwise invisible: every miss in that pack would silently degrade to a raw key.

## Fallback and safety

Resolution walks the pack's own fallback chain and stops at the first pack that owns the key. Lookups are own-property checks, so an inherited `Object.prototype` member can never be read as a translation. If the chain cannot resolve a key, the key is returned as an explicit last-resort diagnostic; the UI never receives an empty string or a fabricated translation.

Interpolation has the same boundary: `{name}` placeholders substitute only own properties of the supplied variables. A placeholder naming an inherited member (`{toString}`, `{constructor}`) is left literal instead of being coerced into visible text.

## Measured completeness

The English catalog, not a hand-written claim, defines coverage. `coverage(id)` counts a pack's own translated keys per namespace, and `coverageDrift(id)` compares that measurement with the manifest's declared ledger and reports any key the pack ships that the English catalog no longer contains.

Drift is asserted in CI (`languagePackMovieCoverage.test.ts`) instead of throwing during `install()`. The English catalog gains keys during ordinary feature work, so a stale ledger at import time would break every page in the app rather than failing the change that made it stale.

Measured against the catalog on current `main`, `zh-CN` ships 101 of 1918 keys. Movie Studio is the widest surface and the least covered:

| Namespace | Translated | Total | Share |
| --- | --- | --- | --- |
| `movie` | 36 | 151 | 23.8% |
| `movieDeep` | 10 | 92 | 10.9% |
| `movieModule` | 0 | 56 | 0% |
| `movieShell` | 0 | 14 | 0% |
| `movieAudio` | 0 | 10 | 0% |
| `movieTeam` | 0 | 9 | 0% |
| `movieExports` | 0 | 8 | 0% |
| `movieQc` | 0 | 6 | 0% |
| **Movie family total** | **46** | **346** | **13.3%** |

So "core Movie Studio copy" means the brief, workspace, and continuity-guide labels plus ten room-sheet strings. Six Movie namespaces are untranslated and are served entirely by the English fallback, which the manifest records as `module-untranslated` flags rather than leaving implied.

## Metadata and persistence

The manifest supplies the browser language tag and text direction. Anonymous and account-scoped preferences use separate local-storage keys through `languagePackPersistence.ts`; account keys URL-encode the user ID so an ID containing a separator cannot collide with another account. Invalid or unavailable storage fails closed without breaking the in-memory UI.

Within the anonymous scope, `taslim-locale` outranks `taslim-language-pack` on read. That ordering is load-bearing rather than incidental: the legacy key is the cross-feature contract other lanes persist through, and the session effect promotes the anonymous preference into the account-scoped key and then clears both. A pack key that shadows the legacy key therefore pins the interface to English and destroys the preference that was just made, which is a regression that only shows up in the browser suites, not in unit tests.

Persisting keeps the two anonymous keys in step, and removes the legacy value when the selected pack cannot be represented by it, so a stale legacy entry can never mask a newer installable pack on the next load.

`LocaleProvider` keeps the legacy `locale` compatibility value for number/date formatting and module-specific helpers while exposing the selected `languagePackId` and routing shared `t()` calls through the registry. For `zh-CN` the compatibility locale resolves to English, so legacy-only specialized catalogs remain English until their module-owned integration paths exist.

Two selection paths, deliberately different:

- The **header** selector applies a pack immediately, which is the pre-existing behaviour for the legacy locales and what the RTL suites depend on.
- The **account** form edits a draft. The pack is applied by `AuthProvider.updateProfile` after the profile persists, which preserves the "Save changes" accessible name under the language the user is reading, keeps unsaved edits from being discarded by a re-mount, and leaves a failed save in the previous language.

Because the draft is no longer re-mounted on the live pack, its interface-language field must seed from the persisted profile (`accountProfileDraftFromUser`) like every sibling field, never from `languagePackId`: the provider renders English on the first pass and restores the saved pack in an effect, so reading that transient value leaves the control on `en` permanently even when `html lang` is already correct.

## Backend boundary

Auth registration, profile, and onboarding validation accept the canonical interface language `zh-CN` (including case-insensitive input normalization) through `apps/api/Localization/LanguagePackCatalog.cs`, and the request DTOs widen `PreferredLanguage` so a padded value normalizes instead of being rejected by model validation.

Generation-language validation stays on `LanguageCodes.Supported`, which is still English, Arabic, and Sorani. `PreferredLanguage` and `DefaultGenerationLanguage` are validated by separate helpers precisely so that widening the interface catalog can never widen generation support: selecting a Chinese interface does not claim that any provider supports Chinese generation, and no provider is contacted by this change.

## Review and next stage

`zh-CN` is a launch foundation, not a completed localization. Before the Chinese experience can be described as complete:

1. Native-speaker review of the 101 shipped strings.
2. Module-owned expansion of the six untranslated Movie namespaces, raising the floors in `languagePackMovieCoverage.test.ts` as each lands.
3. Onboarding and quick-result label localization, owned by the onboarding lane rather than this one.
4. A decision on whether specialized catalogs such as the Movie Director panel should resolve through the registry instead of the legacy compatibility locale.
