using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace MyGameBuilder.Local.Api.Updates;

public static class UpdatePageAssets
{
    private static readonly IReadOnlyDictionary<string, Asset> Assets = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase)
    {
        ["abscissa.ttf"] = new("abscissa.ttf", "font/ttf"),
        ["abscissa-bold.ttf"] = new("abscissa-bold.ttf", "font/ttf"),
        ["titlefont.ttf"] = new("titlefont.ttf", "font/ttf"),
        ["logo-mgb-tiny.png"] = new("logo-mgb-tiny.png", "image/png"),
        ["load.png"] = new("load.png", "image/png"),
        ["play.png"] = new("play.png", "image/png"),
        ["save.png"] = new("save.png", "image/png"),
    };

    public static IResult Serve(string fileName, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!Assets.TryGetValue(fileName, out var asset))
        {
            return Results.NotFound();
        }

        var path = ResolveAssetPath(environment, asset.FileName);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        return Results.File(
            path,
            asset.ContentType,
            lastModified: File.GetLastWriteTimeUtc(path),
            enableRangeProcessing: false);
    }

    private static string ResolveAssetPath(IHostEnvironment environment, string fileName)
    {
        var relativePath = Path.Combine("Updates", "FlashAssets", fileName);
        var outputPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        return File.Exists(outputPath)
            ? outputPath
            : Path.Combine(environment.ContentRootPath, relativePath);
    }

    private sealed record Asset(string FileName, string ContentType);
}
