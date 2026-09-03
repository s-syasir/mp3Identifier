using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;

// `dotnet run -- scan <folder>` runs the recursive folder organizer instead of the web app
if (args.Length > 0 && args[0] == "scan")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: dotnet run -- scan <folder>");
        return;
    }
    await ScanMode.RunAsync(args[1]);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 100_000_000);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 100_000_000);

builder.Services.AddHttpClient("acoustid", c => c.BaseAddress = new Uri("https://api.acoustid.org/v2/"));
builder.Services.AddHttpClient("musicbrainz", c =>
{
    c.BaseAddress = new Uri("https://musicbrainz.org/ws/2/");
    // MusicBrainz requires a descriptive User-Agent identifying the app, not an API key
    c.DefaultRequestHeaders.UserAgent.ParseAdd("mp3Identifier/0.1 (https://github.com/s-syasir/mp3Identifier)");
});
builder.Services.AddHttpClient("coverart", c => c.BaseAddress = new Uri("https://coverartarchive.org/"));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// uploaded files live here until the user applies or discards the tag suggestion
var uploadDir = Path.Combine(Path.GetTempPath(), "mp3identifier");
var backupDir = Path.Combine(uploadDir, "backup");
Directory.CreateDirectory(uploadDir);
Directory.CreateDirectory(backupDir);
var pending = new ConcurrentDictionary<string, string>(); // token -> temp file path

app.MapPost("/api/identify", async (IFormFile file, IHttpClientFactory httpFactory, IConfiguration config) =>
{
    var apiKey = config["AcoustId:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
        return Results.Problem("AcoustId:ApiKey isn't set. Register a free key at https://acoustid.org/new-application and put it in appsettings.json or user-secrets.");

    var token = Guid.NewGuid().ToString("N");
    var tempPath = Path.Combine(uploadDir, token + Path.GetExtension(file.FileName));
    await using (var stream = File.Create(tempPath))
        await file.CopyToAsync(stream);
    pending[token] = tempPath;

    var result = await TrackIdentifier.IdentifyAsync(
        tempPath, httpFactory.CreateClient("acoustid"), httpFactory.CreateClient("musicbrainz"), apiKey);

    if (result.Error is not null) return Results.Problem(result.Error);

    if (!result.Matched)
        return Results.Ok(new { token, matched = false, currentTags = TrackIdentifier.ReadCurrentTags(tempPath) });

    return Results.Ok(new
    {
        token,
        matched = true,
        result.Score,
        suggested = new
        {
            result.Title, result.Artist, result.Album, result.Year, result.Genre,
            result.TrackNumber, result.TotalTracks, result.CoverArtUrl, result.ReleaseId
        },
        currentTags = TrackIdentifier.ReadCurrentTags(tempPath)
    });
})
.DisableAntiforgery(); // local single-user tool, no forms/session to forge against

app.MapPost("/api/apply", async (ApplyRequest req, IHttpClientFactory httpFactory) =>
{
    if (!pending.TryGetValue(req.Token, out var tempPath) || !File.Exists(tempPath))
        return Results.NotFound("Unknown or expired token, re-upload the file.");

    // preserve the as-uploaded file before any tag edits touch it
    var backupPath = Path.Combine(backupDir, $"{req.Token}{Path.GetExtension(tempPath)}");
    File.Copy(tempPath, backupPath, overwrite: true);

    var meta = new TrackMetadata(req.Title, req.Artist, req.Album, req.Year, req.Genre, req.TrackNumber, req.TotalTracks, req.ReleaseId);
    await TrackIdentifier.ApplyTagsAsync(tempPath, meta, httpFactory.CreateClient("coverart"));

    var finalName = TrackIdentifier.BuildFileName(meta);
    var finalPath = Path.Combine(uploadDir, finalName);
    File.Move(tempPath, finalPath, overwrite: true);
    pending.TryRemove(req.Token, out _);

    return Results.Ok(new { finalPath, fileName = finalName, backupPath });
});

// serves the tagged file back to the browser so it can be saved wherever the user wants;
// GetFileName strips any directory components so this can't be pointed outside uploadDir
app.MapGet("/api/download/{fileName}", (string fileName) =>
{
    var safeName = Path.GetFileName(fileName);
    var path = Path.Combine(uploadDir, safeName);
    return File.Exists(path)
        ? Results.File(path, "audio/mpeg", safeName)
        : Results.NotFound("File not found, it may have already been moved or renamed.");
});

app.Run();

record ApplyRequest(
    string Token, string? Title, string? Artist, string? Album, string? Year,
    string? Genre, int? TrackNumber, int? TotalTracks, string? ReleaseId);

// what we know about a track once fingerprinting + lookups are done, shared between the web
// app and folder-scan mode so both write identical tags and identical filenames
record TrackMetadata(
    string? Title, string? Artist, string? Album, string? Year,
    string? Genre, int? TrackNumber, int? TotalTracks, string? ReleaseId);

record IdentifyResult(
    string? Error, bool Matched, float Score,
    string? Title, string? Artist, string? Album, string? Year,
    string? Genre, int? TrackNumber, int? TotalTracks, string? ReleaseId, string? CoverArtUrl)
{
    public static IdentifyResult Failed(string error) => new(error, false, 0, null, null, null, null, null, null, null, null, null);
    public static IdentifyResult NoMatch() => new(null, false, 0, null, null, null, null, null, null, null, null, null);
}

static class TrackIdentifier
{
    public static async Task<IdentifyResult> IdentifyAsync(string filePath, HttpClient acoustIdClient, HttpClient mbClient, string apiKey)
    {
        // fpcalc ships in libchromaprint-tools; -json gives {"duration":N,"fingerprint":"..."}
        ProcessStartInfo psi = new("fpcalc", $"-json \"{filePath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        string stdout, stderr;
        int exitCode;
        try
        {
            using var proc = Process.Start(psi)!;
            stdout = await proc.StandardOutput.ReadToEndAsync();
            stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            exitCode = proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return IdentifyResult.Failed("fpcalc isn't installed. Run: sudo apt install libchromaprint-tools");
        }

        if (exitCode != 0)
            return IdentifyResult.Failed($"fpcalc failed: {stderr}");

        using var fpJson = JsonDocument.Parse(stdout);
        var duration = (int)fpJson.RootElement.GetProperty("duration").GetDouble();
        var fingerprint = fpJson.RootElement.GetProperty("fingerprint").GetString();

        // AcoustID's server mishandles the `meta` param over POST (percent-encoded '+' delimiters
        // don't come back as recordings), confirmed against their own docs example, so use GET
        // and keep meta's '+' separators literal rather than URL-escaped.
        var url = "lookup"
            + $"?client={Uri.EscapeDataString(apiKey)}"
            + $"&duration={duration}"
            + $"&fingerprint={Uri.EscapeDataString(fingerprint!)}"
            + "&meta=recordings+releasegroups+releases+compress";
        var response = await acoustIdClient.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return IdentifyResult.Failed($"AcoustID lookup failed ({(int)response.StatusCode}): {body}");

        using var lookupJson = JsonDocument.Parse(body);
        var root = lookupJson.RootElement;
        if (root.GetProperty("status").GetString() != "ok")
            return IdentifyResult.Failed($"AcoustID returned an error: {body}");

        if (!root.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return IdentifyResult.NoMatch();

        // results are pre-sorted by score, highest confidence first, but a single result's
        // `recordings` array can bundle several distinct tracks off the same album (a known
        // AcoustID quirk from crowdsourced submissions), so disambiguate by picking whichever
        // recording's own duration is closest to what fpcalc measured for this file
        var best = results[0];
        var score = best.GetProperty("score").GetSingle();

        string? title = null, album = null, year = null, recordingId = null, releaseGroupId = null, releaseId = null;
        var artists = new List<string>();
        JsonElement? chosenRecording = null;
        double bestDurationDelta = double.MaxValue;

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("recordings", out var recs)) continue;
            foreach (var rec in recs.EnumerateArray())
            {
                if (!rec.TryGetProperty("title", out var titleEl)) continue; // no title, not useful even if duration matches

                var delta = rec.TryGetProperty("duration", out var d)
                    ? Math.Abs(d.GetDouble() - duration)
                    : double.MaxValue - 1; // no duration on the recording, least preferred but still a fallback

                // an exact-duration alternate version (instrumental, radio edit, remix...) shouldn't
                // beat the plain title just because its length happens to line up more precisely
                if (titleEl.GetString()?.Contains('(') == true) delta += 3.0;

                if (delta < bestDurationDelta)
                {
                    bestDurationDelta = delta;
                    chosenRecording = rec;
                }
            }
        }

        if (chosenRecording is { } recording)
        {
            if (recording.TryGetProperty("id", out var rid)) recordingId = rid.GetString();
            if (recording.TryGetProperty("title", out var t)) title = t.GetString();

            if (recording.TryGetProperty("artists", out var artistArr))
                foreach (var a in artistArr.EnumerateArray())
                    if (a.TryGetProperty("name", out var n)) artists.Add(n.GetString()!);

            if (recording.TryGetProperty("releasegroups", out var rgArr) && rgArr.GetArrayLength() > 0)
            {
                var rg = rgArr[0];
                if (rg.TryGetProperty("title", out var at)) album = at.GetString();
                if (rg.TryGetProperty("id", out var rgid)) releaseGroupId = rgid.GetString();

                // `releases` isn't sorted chronologically, it's a mix of the original release and
                // every reissue/reprint, so take the earliest date rather than releases[0]; that
                // earliest release's own id also becomes our source for track number / cover art
                if (rg.TryGetProperty("releases", out var relArr))
                {
                    int? earliestYear = null;
                    int earliestKey = int.MaxValue;
                    foreach (var rel in relArr.EnumerateArray())
                    {
                        if (!rel.TryGetProperty("date", out var dateEl) || !dateEl.TryGetProperty("year", out var y)) continue;
                        var relYear = y.GetInt32();
                        var relMonth = dateEl.TryGetProperty("month", out var m) ? m.GetInt32() : 1;
                        var relDay = dateEl.TryGetProperty("day", out var dd) ? dd.GetInt32() : 1;
                        var key = relYear * 10000 + relMonth * 100 + relDay;
                        if (key < earliestKey)
                        {
                            earliestKey = key;
                            earliestYear = relYear;
                            if (rel.TryGetProperty("id", out var relIdEl)) releaseId = relIdEl.GetString();
                        }
                    }
                    if (earliestYear is { } ey) year = ey.ToString();
                }
            }
        }

        // genre and track-position aren't in AcoustID's response at all, they need direct
        // MusicBrainz lookups; best-effort, if either call fails we still return everything else
        string? genre = null;
        int? trackNumber = null, totalTracks = null;

        if (releaseGroupId is not null)
        {
            try
            {
                var rgResponse = await mbClient.GetAsync($"release-group/{releaseGroupId}?inc=genres&fmt=json");
                if (rgResponse.IsSuccessStatusCode)
                {
                    using var rgJson = JsonDocument.Parse(await rgResponse.Content.ReadAsStringAsync());
                    if (rgJson.RootElement.TryGetProperty("genres", out var genres) && genres.GetArrayLength() > 0)
                    {
                        // pick the most-voted genre tag rather than just the first one listed
                        JsonElement? topGenre = null;
                        var topCount = -1;
                        foreach (var g in genres.EnumerateArray())
                        {
                            var count = g.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                            if (count > topCount) { topCount = count; topGenre = g; }
                        }
                        if (topGenre is { } tg && tg.TryGetProperty("name", out var gn)) genre = gn.GetString();
                    }
                }
            }
            catch (HttpRequestException) { /* best-effort */ }
        }

        if (releaseId is not null && recordingId is not null)
        {
            try
            {
                var relResponse = await mbClient.GetAsync($"release/{releaseId}?inc=recordings&fmt=json");
                if (relResponse.IsSuccessStatusCode)
                {
                    using var relJson = JsonDocument.Parse(await relResponse.Content.ReadAsStringAsync());
                    if (relJson.RootElement.TryGetProperty("media", out var media))
                    {
                        foreach (var medium in media.EnumerateArray())
                        {
                            if (!medium.TryGetProperty("tracks", out var tracks)) continue;
                            foreach (var track in tracks.EnumerateArray())
                            {
                                if (!track.TryGetProperty("recording", out var rec) || !rec.TryGetProperty("id", out var recId)) continue;
                                if (recId.GetString() != recordingId) continue;
                                if (track.TryGetProperty("position", out var pos)) trackNumber = pos.GetInt32();
                                if (medium.TryGetProperty("track-count", out var tc)) totalTracks = tc.GetInt32();
                                break;
                            }
                            if (trackNumber is not null) break;
                        }
                    }
                }
            }
            catch (HttpRequestException) { /* best-effort */ }
        }

        // Cover Art Archive is keyed by release id; no listing call needed, the image URLs are
        // predictable, a missing cover just 404s and callers handle that themselves
        var coverArtUrl = releaseId is not null ? $"https://coverartarchive.org/release/{releaseId}/front-500" : null;

        return new IdentifyResult(null, true, score, title, string.Join(", ", artists), album, year,
            genre, trackNumber, totalTracks, releaseId, coverArtUrl);
    }

    public static async Task ApplyTagsAsync(string filePath, TrackMetadata meta, HttpClient coverArtClient)
    {
        var tagFile = TagLib.File.Create(filePath);
        tagFile.Tag.Title = meta.Title;
        tagFile.Tag.Performers = string.IsNullOrWhiteSpace(meta.Artist) ? Array.Empty<string>() : new[] { meta.Artist };
        tagFile.Tag.Album = meta.Album;
        if (uint.TryParse(meta.Year, out var yearNum)) tagFile.Tag.Year = yearNum;
        if (!string.IsNullOrWhiteSpace(meta.Genre)) tagFile.Tag.Genres = new[] { meta.Genre };
        if (meta.TrackNumber is { } tn) tagFile.Tag.Track = (uint)tn;
        if (meta.TotalTracks is { } tt) tagFile.Tag.TrackCount = (uint)tt;

        if (!string.IsNullOrWhiteSpace(meta.ReleaseId))
        {
            try
            {
                var coverBytes = await coverArtClient.GetByteArrayAsync($"release/{meta.ReleaseId}/front");
                tagFile.Tag.Pictures = new TagLib.IPicture[]
                {
                    new TagLib.Picture(coverBytes) { Type = TagLib.PictureType.FrontCover, Description = "Cover" }
                };
            }
            catch (HttpRequestException) { /* no cover art available for this release, skip it */ }
        }

        tagFile.Save();
        tagFile.Dispose();
    }

    public static string BuildFileName(TrackMetadata meta)
    {
        var safeArtist = Sanitize(meta.Artist ?? "Unknown Artist");
        var safeAlbum = Sanitize(meta.Album ?? "Unknown Album");
        var safeTitle = Sanitize(meta.Title ?? "Unknown Title");
        // Artist - Album - TrackNumber - Title.mp3; track number is omitted when we don't have one
        return meta.TrackNumber is { } tn
            ? $"{safeArtist} - {safeAlbum} - {tn} - {safeTitle}.mp3"
            : $"{safeArtist} - {safeAlbum} - {safeTitle}.mp3";
    }

    public static object ReadCurrentTags(string path)
    {
        using var tagFile = TagLib.File.Create(path);
        return new
        {
            title = tagFile.Tag.Title,
            artist = string.Join(", ", tagFile.Tag.Performers),
            album = tagFile.Tag.Album,
            year = tagFile.Tag.Year
        };
    }

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}

static class ScanMode
{
    public static async Task RunAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Console.Error.WriteLine($"Folder not found: {folderPath}");
            return;
        }

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets(System.Reflection.Assembly.GetEntryAssembly()!, optional: true)
            .AddEnvironmentVariables()
            .Build();

        var apiKey = config["AcoustId:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("AcoustId:ApiKey isn't set. Register a free key at https://acoustid.org/new-application and put it in appsettings.json or user-secrets.");
            return;
        }

        using var acoustIdClient = new HttpClient { BaseAddress = new Uri("https://api.acoustid.org/v2/") };
        using var mbClient = new HttpClient { BaseAddress = new Uri("https://musicbrainz.org/ws/2/") };
        mbClient.DefaultRequestHeaders.UserAgent.ParseAdd("mp3Identifier/0.1 (https://github.com/s-syasir/mp3Identifier)");
        using var coverClient = new HttpClient { BaseAddress = new Uri("https://coverartarchive.org/") };

        var musicRoot = Path.Combine(folderPath, "Music");
        var scanBackupDir = Path.Combine(musicRoot, "_backup");
        Directory.CreateDirectory(musicRoot);
        Directory.CreateDirectory(scanBackupDir);

        // skip anything already under Music/, otherwise a second run would try to re-organize
        // its own previous output
        var files = Directory.EnumerateFiles(folderPath, "*.mp3", SearchOption.AllDirectories)
            .Where(f => !Path.GetFullPath(f).StartsWith(Path.GetFullPath(musicRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();

        Console.WriteLine($"Found {files.Count} mp3 file(s) under {folderPath}");

        foreach (var file in files)
        {
            Console.WriteLine($"Processing: {file}");

            IdentifyResult result;
            try
            {
                result = await TrackIdentifier.IdentifyAsync(file, acoustIdClient, mbClient, apiKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  error: {ex.Message}");
                continue;
            }

            if (result.Error is not null) { Console.WriteLine($"  error: {result.Error}"); continue; }
            if (!result.Matched) { Console.WriteLine("  no AcoustID match, skipping"); continue; }

            var backupPath = Path.Combine(scanBackupDir, Path.GetFileName(file));
            File.Copy(file, backupPath, overwrite: true);

            var meta = new TrackMetadata(result.Title, result.Artist, result.Album, result.Year,
                result.Genre, result.TrackNumber, result.TotalTracks, result.ReleaseId);

            await TrackIdentifier.ApplyTagsAsync(file, meta, coverClient);

            var safeArtist = TrackIdentifier.Sanitize(result.Artist ?? "Unknown Artist");
            var safeAlbum = TrackIdentifier.Sanitize(result.Album ?? "Unknown Album");
            var destDir = Path.Combine(musicRoot, safeArtist, safeAlbum);
            Directory.CreateDirectory(destDir);

            var finalName = TrackIdentifier.BuildFileName(meta);
            var destPath = Path.Combine(destDir, finalName);
            File.Move(file, destPath, overwrite: true);

            Console.WriteLine($"  -> {destPath}");
        }

        Console.WriteLine("Done.");
    }
}
