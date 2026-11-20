namespace ProcessTracker.Service.Api;

/// <summary>
/// The read-only dashboard: <c>/</c> redirects to <c>/dashboard/</c>, whose three files are embedded resources
/// (no file system is served). Registered after the Host/Origin guard, which therefore applies to all of it.
/// </summary>
public static class DashboardEndpoints
{
    private const string Csp =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; "
        + "base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    private const string Prefix = "dashboard/";

    private static readonly Dictionary<string, (byte[] Body, string ContentType)> Files = Load();

    private static Dictionary<string, (byte[] Body, string ContentType)> Load()
    {
        var asm = typeof(DashboardEndpoints).Assembly;
        var files = new Dictionary<string, (byte[], string)>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var file = name[Prefix.Length..];
            var type = Path.GetExtension(file) switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                _ => "application/octet-stream",
            };
            files[file] = (ms.ToArray(), type);
        }
        return files;
    }

    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/dashboard/"));
        // Routing ignores a trailing slash, so one endpoint serves both; without the slash the relative links would break.
        app.MapGet("/dashboard", (HttpContext ctx) =>
            ctx.Request.Path.Value!.EndsWith('/') ? Serve(ctx, "index.html") : Results.Redirect("/dashboard/"));
        app.MapGet("/dashboard/{file}", (HttpContext ctx, string file) => Serve(ctx, file));
        return app;
    }

    private static IResult Serve(HttpContext ctx, string file)
    {
        if (!Files.TryGetValue(file, out var f)) return Results.NotFound();
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = Csp;
        h.XContentTypeOptions = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h.CacheControl = "no-store";
        return Results.Bytes(f.Body, f.ContentType);
    }
}
