# Batch 3.2 OpenAI source notes

The following official sources were consulted on September 19, 2026.

- OpenAI Streaming Responses guide: https://developers.openai.com/api/docs/guides/streaming-responses
  - Responses streaming uses HTTP Server-Sent Events with `stream=true`.
  - Important semantic events include `response.output_text.delta`, `response.completed`, and `error`.
  - The Taslim adapter translates these into Taslim-owned `message.delta`, `message.completed`, and `message.failed` events.

- OpenAI Responses API create reference: https://developers.openai.com/api/reference/resources/responses/methods/create/
  - The request supports model selection, server-controlled instructions, input items, and streaming.
  - Taslim sends the system instruction through the server-side `instructions` field and sends original Unicode conversation messages as input.

- OpenAI streaming events reference: https://developers.openai.com/api/reference/resources/responses/streaming-events/
  - Responses streaming is an SSE event stream with typed semantic events, including output text delta and completion lifecycle events.

- OpenAI API pricing: https://openai.com/api/pricing/
  - GPT-5.6 Luna: input $0.20 / 1M, cached input $0.02 / 1M, output $1.20 / 1M.
  - GPT-5.6 Terra: input $2.00 / 1M, cached input $0.20 / 1M, output $12.00 / 1M.
  - GPT-5.6 Sol: input $4.00 / 1M, cached input $0.40 / 1M, output $20.00 / 1M.

These values are configuration-backed internal accounting values only. Taslim does not charge users or expose provider/model metadata in Batch 3.2.
