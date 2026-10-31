# Password recovery

Taslim password recovery uses ASP.NET Core Identity's password-reset token provider and the existing cookie/session model. The feature is **disabled by default**. It fails closed until an SMTP sender, TLS, a trusted public base URL, and the explicit enable flag are configured; no email provider is bundled or purchased by this change.

## Required API-service configuration

Configure these values only on the ASP.NET API service. Do not place SMTP credentials in `apps/web`, `NEXT_PUBLIC_*`, source control, or browser-visible settings.

```dotenv
PasswordRecovery__Enabled=true
PasswordRecovery__PublicBaseUrl=https://taslim.ai
PasswordRecovery__RequestCooldownMinutes=5
Email__Provider=Smtp
Email__Host=<your SMTP host>
Email__Port=587
Email__EnableSsl=true
Email__Username=<SMTP username>
Email__Password=<SMTP password>
Email__FromAddress=<verified sender address>
Email__FromName=Taslim.ai
```

Apply the EF migration `AddPasswordResetEmailOutbox` to the API database through the existing migration release process before enabling recovery. It creates durable delivery/cooldown state only; it does not store reset tokens.

Use an SMTP relay that supports STARTTLS (the built-in .NET SMTP client does not support implicit TLS on port 465). TLS and both SMTP username/password are required by the adapter. The sender address must be authorized by the relay. Store secrets in the API host's secret manager, not in this repository. The production origin is deliberately restricted to `https://taslim.ai`; development may use `http://localhost:3000` or `https://localhost:3000`.

The anonymous `GET /api/auth/password-recovery/availability` endpoint returns only `{ "available": true }` or `{ "available": false }` with no-store headers. `available` is true only when the explicit feature flag, configured sender, and allowed public base URL pass server readiness checks; it does not probe the SMTP relay or prove end-to-end delivery. The login link and recovery forms remain hidden until this server-owned response is true, and the API rejects both reset requests and submissions while readiness is false. A failed or unavailable status request keeps the UI closed.

Until all settings are valid and `PasswordRecovery__Enabled=true`, both known and unknown addresses receive the same generic `503` recovery-unavailable response and no reset request is queued. This prevents a decorative/fake success response when delivery is not configured. Once configured, the API responds `202 Accepted` with the same message for existing and unknown accounts after recording eligible requests in the outbox.

## Delivery and token behavior

- The request endpoint is CSRF protected, anonymous, and rate limited. A database-backed per-user cooldown (5 minutes by default) prevents repeated delivery across API replicas; the API also applies its recovery request limit.
- A durable outbox row stores only the user ID, timestamps, delivery state, attempt count, and a sanitized failure code. It never stores an email address, reset token, reset URL, or SMTP payload.
- The worker generates an Identity reset token immediately before sending, uses the saved account language (`en`, `ar`, or `ku`), and retries delivery with bounded backoff. Logs contain no recipient, token, URL, SMTP response, or exception message.
- Reset tokens expire after one hour. The reset page receives email/token values only in the URL fragment, removes the fragment from browser history before rendering, and posts the values over the existing CSRF-protected API route. The token is not sent in page requests or referrer headers.
- Successful reset uses `UserManager.ResetPasswordAsync`, rotates the Identity security stamp, invalidates other sessions, does not create a new login session, and invalidates token reuse.
- Reset and request pages are marked `noindex` and `no-referrer`; API recovery responses are `no-store`.

A `202` means the API accepted a configured delivery request for processing, not that a mailbox has confirmed receipt. The outbox retries transient failures up to five attempts and logs a sanitized failure classification if it cannot deliver. Monitor those logs and ensure the configured SMTP account can send to the app's user population. Apply any additional client-IP edge rate limiting at the public ingress using its verified client-IP signal; the API does not trust arbitrary forwarded-IP headers.

## Verification

The API integration tests exercise configured/unconfigured behavior, readiness changes on configuration reload, non-enumerating responses, CSRF rejection, per-account cooldown, actual test outbox delivery, valid/invalid/expired/replayed Identity tokens, sanitized mail-sender failure storage, session invalidation, and no automatic sign-in. Frontend e2e tests cover enabled and disabled availability UI states, the generic request result, and fragment-token removal before the reset form can be shown.
