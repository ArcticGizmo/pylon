using Microsoft.AspNetCore.Http;

namespace Pylon.Broker.Http;

/// <summary>Errors Pylon raises itself: problem+json with a <c>Pylon-Error</c> header, the same shape as Postern's.</summary>
internal static class PylonErrors
{
    public const string Header = "Pylon-Error";

    public static Task Write(HttpContext context, int status, string code, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.Headers[Header] = code;
        return context.Response.WriteAsJsonAsync(new { type = $"https://pylon.local/errors/{code}", title = code, status, detail },
            options: null, contentType: "application/problem+json");
    }
}
