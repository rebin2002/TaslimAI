namespace Taslim.Api.Infrastructure;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        headers["Permissions-Policy"] = "camera=(), geolocation=(), microphone=(), payment=(), usb=()";
        if (context.Request.IsHttps) headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

        await next(context);
    }
}
