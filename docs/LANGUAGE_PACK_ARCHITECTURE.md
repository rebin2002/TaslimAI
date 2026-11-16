# Language Pack Architecture

This foundation adds an installable, versioned language-pack boundary without claiming that every Taslim module is translated.

## Registry and manifest

`apps/web/src/lib/languagePackRegistry.ts` owns a small registry of resources. Each resource has a schema version, pack version, stable ID, canonical BCP 47 language tag, document direction, fallback pack, completeness state, review state, and review flags. A future pack can be installed through `LanguagePackRegistry.install()` without editing the core translation catalog.

The built-in registry adapts the existing English, Arabic, and Sorani catalogs and adds the first `zh-CN` resource. The legacy catalog remains untouched in this stage because open PRs currently own that file.

## Fallback and safety

A missing key in an incomplete pack resolves through its declared fallback, normally English. If the fallback chain cannot resolve a key, the key is returned as an explicit last-resort diagnostic; the UI never receives an empty string or fabricated translation. The `zh-CN` manifest is explicitly `partial` and `needs-human-review`, with module-level flags for UI, auth, account, and Movie Studio coverage.

The first Chinese resource covers high-priority navigation, authentication, account, and core Movie Studio copy. It intentionally does not claim full coverage of Movie Studio's deeper room-specific catalogs or other modules; those remain English until translated and reviewed.

## Metadata and persistence

The manifest supplies the browser language tag and text direction. Anonymous and account-scoped preferences use separate local-storage keys through `languagePackPersistence.ts`; invalid or unavailable storage fails closed without breaking the in-memory UI.

`LocaleProvider` keeps the existing legacy `locale` compatibility value for number/date and module-specific helpers while exposing the selected `languagePackId` and routing shared `t()` calls through the registry. For `zh-CN`, legacy-only specialized catalogs remain English until their module-owned integration paths are available.

## Backend boundary

Auth registration and profile validation accept the canonical interface language `zh-CN` (including case-insensitive input normalization) through an auth-owned catalog. Generation-language validation remains limited to the currently supported generation languages; selecting Chinese for the interface does not claim that any provider supports Chinese generation.

## Review and next stage

`zh-CN` translations are a launch foundation, not a completed localization. A follow-up stage should expand module-owned catalogs, update onboarding's generation-language UX, and add native-speaker review evidence before describing the Chinese experience as complete.
