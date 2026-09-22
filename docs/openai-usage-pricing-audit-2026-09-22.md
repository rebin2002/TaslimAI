# OpenAI Usage Pricing Audit — 2026-09-22

The Batch 3.9 audit checked the current official OpenAI pages on 2026-09-22.

The official GPT Image 2.5 Sunburst model page identifies `gpt-image-2.5-sunburst` and the dated snapshot `gpt-image-2.5-sunburst-2026-09-08`. It supports text and image inputs, image output, the Images API endpoint, and quality settings including low, medium, high, xhigh, max, and auto. The documented token rates are $5.00 per 1M text input tokens, $1.25 per 1M cached text input tokens, $8.00 per 1M image input tokens, $2.00 per 1M cached image input tokens, and $30.00 per 1M image output tokens. Text output is not billed because the model produces images.

The official image-generation guide states that the Images API response contains base64 image data and that usage should be measured from the response usage object. It documents image output customization through size, quality, format, compression, and background. It also states that token consumption can differ by model and quality even when token rates are equal, so the completed transaction must prefer measured provider usage and use estimates only for preflight safety or when usage is unavailable.

For Batch 3.9, the server-side image pricing snapshot is versioned as `gpt-image-2.5-sunburst-2026-09-08`, effective at `2026-09-08T00:00:00Z`, with source `https://developers.openai.com/api/docs/models/gpt-image-2.5-sunburst`. The repository configuration stores these rates and source values outside handler logic. Each completed image transaction stores the pricing version and serialized rate snapshot so later rate changes do not rewrite historical cost meaning.

References

[1]: https://developers.openai.com/api/docs/models/gpt-image-2.5-sunburst "GPT-Image-2.5 Sunburst model page"
[2]: https://developers.openai.com/api/docs/guides/image-generation "OpenAI image generation guide"
[3]: https://openai.com/api/pricing/ "OpenAI API pricing"
