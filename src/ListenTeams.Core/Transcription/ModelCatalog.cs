namespace ListenTeams.Core.Transcription;

public sealed record WhisperModelInfo(string FileName, string Label, int SizeMb, int Quality)
{
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}";
}

/// <summary>Whisper models ListenTeams knows how to use, and where to find them.</summary>
public static class ModelCatalog
{
    public static IReadOnlyList<WhisperModelInfo> Models { get; } =
    [
        new("ggml-large-v3-turbo-q5_0.bin", "Précis — large-v3-turbo (recommandé)", 547, 4),
        new("ggml-medium-q5_0.bin", "Très bon — medium", 514, 3),
        new("ggml-small-q5_1.bin", "Équilibré — small (PC modeste)", 181, 2),
        new("ggml-base-q5_1.bin", "Rapide — base (qualité réduite)", 57, 1),
    ];

    public static WhisperModelInfo Default => Models[0];

    /// <summary>Folders searched for models, in order.</summary>
    public static IEnumerable<string> SearchFolders(string appFolder, string dataFolder) =>
        [Path.Combine(appFolder, "models"), Path.Combine(dataFolder, "models")];

    /// <summary>
    /// Returns the path of the requested model, or of the best model present when
    /// <paramref name="preferred"/> is empty or missing. Null if no model is installed.
    /// </summary>
    public static string? Resolve(string? preferred, IEnumerable<string> folders)
    {
        var folderList = folders.Where(Directory.Exists).ToList();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            if (Path.IsPathRooted(preferred) && File.Exists(preferred)) return preferred;
            foreach (var folder in folderList)
            {
                string candidate = Path.Combine(folder, preferred);
                if (File.Exists(candidate)) return candidate;
            }
        }

        foreach (var model in Models.OrderByDescending(m => m.Quality))
        {
            foreach (var folder in folderList)
            {
                string candidate = Path.Combine(folder, model.FileName);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 1_000_000) return candidate;
            }
        }

        return folderList
            .SelectMany(f => Directory.GetFiles(f, "ggml-*.bin"))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    /// <summary>Downloads a model (used when the installer could not, e.g. offline install).</summary>
    public static async Task<string> DownloadAsync(WhisperModelInfo model, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, model.FileName);
        string partial = target + ".part";

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ListenTeams/1.0");
        using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report(read / (double)total.Value);
            }
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }
}
