# OpenAI Image API implementation notes

Research checked on 2026-09-22 before Batch 3.8 implementation.

- Image generation guide: https://platform.openai.com/docs/guides/image-generation
- Image API reference: https://developers.openai.com/api/reference/python/resources/images/methods/generate/
- GPT Image 2.5 Sunburst model page: https://developers.openai.com/api/docs/models/gpt-image-2.5-sunburst
- Prompting guidance: https://developers.openai.com/api/docs/guides/image-prompting

The current official guide identifies `gpt-image-2.5-sunburst` and `gpt-image-2.5-flare` as the direct Images API models. Sunburst is described as the higher-precision option and Flare as the faster everyday option. The Batch 3.8 server-side default uses the undated `gpt-image-2.5-sunburst` identifier so routing remains server-controlled.

The direct generation endpoint is `POST /v1/images/generations`. The request supports `model`, `prompt`, `n`, `size`, `quality`, `output_format`, `output_compression`, `background`, and `moderation`. The MVP sends `n=1`, maps Square to `1024x1024`, Portrait to `1024x1536`, and Landscape to `1536x1024`, maps Standard to `medium`, maps High to `high`, requests PNG, uses an opaque background, and leaves moderation at `auto`.

The response returns base64 image data in `data[0].b64_json`. The current reference also documents a usage object with input and output tokens plus text/image token details. The implementation records actual provider cost when those fields are returned and uses server configuration for bounded fallback estimation.

The current Sunburst model page lists text input at $5.00 per million tokens, cached text input at $1.25 per million, image input at $8.00 per million, cached image input at $2.00 per million, and image output at $30.00 per million. These are configurable under `ImageGeneration:Pricing`; they are not embedded in handler logic. Customer charge remains zero through `SafeUsageChargingService`.

The image-generation guide documents `moderation=auto` as standard filtering and reports `moderation_blocked` as a stable error code for a safety refusal. The provider maps this to a safe Taslim error without exposing raw provider payloads. The provider logs only status, safe error code, request ID, provider/model key, and duration; it never logs prompts, base64 output, credentials, or headers.

Reference images are supported by the current API, but Batch 3.8 only reserves `ReferenceFileId` in the normalized contract and deliberately rejects execution with a safe `IMAGE_REFERENCE_NOT_SUPPORTED` error. This avoids destabilizing the MVP while preserving a future authorized StoredFile/Asset foundation.
