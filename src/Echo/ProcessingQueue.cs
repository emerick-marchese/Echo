using System.Threading.Channels;
using Echo.Core;
using Echo.Core.Output;
using Echo.Core.Processing;
using Echo.Core.Settings;
using Echo.Core.Speakers;
using Echo.Core.Summary;
using Echo.Core.Transcription;
using Echo.Platform;

namespace Echo;

/// <summary>Processes finished calls one after the other, in the background.</summary>
internal sealed class ProcessingQueue
{
    private sealed record Job(CallFolder Folder, bool SummaryOnly);

    private readonly SettingsStore _settings;
    private readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>();
    private int _pending;

    public ProcessingQueue(SettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>Current activity ("Transcription 42 %"…), or null when idle.</summary>
    public event Action<string?>? ProgressChanged;

    public event Action<ProcessingResult>? Completed;

    public event Action<CallFolder, Exception>? Failed;

    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Folder of the call being processed right now, if any.</summary>
    public string? CurrentFolderPath { get; private set; }

    /// <summary>Last progress text of the call being processed.</summary>
    public string? CurrentProgress { get; private set; }

    public void Start(CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => WorkAsync(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>Full processing: transcription, voices, summary.</summary>
    public void Enqueue(CallFolder folder) => Add(new Job(folder, SummaryOnly: false));

    /// <summary>Summary only, from the existing transcript (after the voices were named).</summary>
    public void EnqueueSummary(CallFolder folder) => Add(new Job(folder, SummaryOnly: true));

    private void Add(Job job)
    {
        Interlocked.Increment(ref _pending);
        _channel.Writer.TryWrite(job);
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var folder = job.Folder;
                CurrentFolderPath = folder.Path;
                try
                {
                    var result = await ProcessAsync(job, cancellationToken).ConfigureAwait(false);
                    Log.Info($"Traitement terminé : {result.Folder.Path}");
                    Completed?.Invoke(result);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Échec du traitement de {folder.Path}", ex);
                    Failed?.Invoke(folder, ex);
                }
                finally
                {
                    CurrentFolderPath = null;
                    CurrentProgress = null;
                    Interlocked.Decrement(ref _pending);
                    ProgressChanged?.Invoke(null);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<ProcessingResult> ProcessAsync(Job job, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var progress = new Progress<string>(Report);
        ISummarizer? summarizer = CreateSummarizer(settings, out string? unavailableReason);

        if (job.SummaryOnly)
        {
            var summaryProcessor = new CallProcessor(() => throw new InvalidOperationException(), () => summarizer);
            var summaryOptions = new ProcessingOptions(settings.MyName, settings.OthersName, null, unavailableReason);
            return await summaryProcessor.ResummarizeAsync(job.Folder, summaryOptions, progress, cancellationToken).ConfigureAwait(false);
        }

        string modelPath = await EnsureModelAsync(settings, cancellationToken).ConfigureAwait(false);
        string? voiceModel = settings.IdentifyVoices ? await EnsureVoiceModelAsync(cancellationToken).ConfigureAwait(false) : null;
        var processor = new CallProcessor(
            () => new WhisperTranscriber(modelPath, settings.Language, settings.Vocabulary, settings.UseGpu),
            () => summarizer,
            () => voiceModel is null ? null : new SherpaSpeakerEmbedder(voiceModel, threads: 2));

        var options = new ProcessingOptions(settings.MyName, settings.OthersName, Path.GetFileName(modelPath), unavailableReason);
        Report("Préparation de la transcription…");
        return await processor.ProcessAsync(job.Folder, options, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Voice-print model (28 MB), downloaded if the installer could not. Null if unavailable.</summary>
    public static async Task<string?> EnsureVoiceModelAsync(CancellationToken cancellationToken)
    {
        var folders = ModelCatalog.SearchFolders(AppPaths.AppFolder, AppPaths.DataFolder);
        if (SpeakerModel.Resolve(folders) is { } path) return path;
        try
        {
            Log.Info("Téléchargement du modèle de reconnaissance des voix");
            return await SpeakerModel.DownloadAsync(AppPaths.DownloadedModelsFolder, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Warn($"Modèle de reconnaissance des voix indisponible : {ex.Message}");
            return null;
        }
    }

    private void Report(string text)
    {
        CurrentProgress = text;
        ProgressChanged?.Invoke(text);
    }

    private ISummarizer? CreateSummarizer(AppSettings settings, out string? unavailableReason)
    {
        unavailableReason = null;
        switch (settings.SummaryProvider)
        {
            case SummaryProvider.Claude:
                string? key = _settings.ClaudeApiKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    unavailableReason = "clé API Anthropic non configurée (menu Echo → Paramètres → Synthèse).";
                    return null;
                }
                return new ClaudeSummarizer(key, settings.ClaudeModel);

            case SummaryProvider.Ollama:
                return new OllamaSummarizer(settings.OllamaUrl, settings.OllamaModel);

            default:
                return null;
        }
    }

    private async Task<string> EnsureModelAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var folders = ModelCatalog.SearchFolders(AppPaths.AppFolder, AppPaths.DataFolder);
        if (ModelCatalog.Resolve(settings.WhisperModel, folders) is { } path) return path;

        var model = ModelCatalog.Models.FirstOrDefault(m => m.FileName.Equals(settings.WhisperModel, StringComparison.OrdinalIgnoreCase))
                    ?? ModelCatalog.Default;
        Log.Info($"Téléchargement du modèle {model.FileName}");
        var progress = new Progress<double>(p => Report($"Téléchargement du modèle de transcription {p:P0}"));
        return await ModelCatalog.DownloadAsync(model, AppPaths.DownloadedModelsFolder, progress, cancellationToken).ConfigureAwait(false);
    }
}
