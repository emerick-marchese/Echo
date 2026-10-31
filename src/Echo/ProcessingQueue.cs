using System.Threading.Channels;
using Echo.Core;
using Echo.Core.Output;
using Echo.Core.Processing;
using Echo.Core.Settings;
using Echo.Core.Summary;
using Echo.Core.Transcription;
using Echo.Platform;

namespace Echo;

/// <summary>Processes finished calls one after the other, in the background.</summary>
internal sealed class ProcessingQueue
{
    private readonly SettingsStore _settings;
    private readonly Channel<CallFolder> _channel = Channel.CreateUnbounded<CallFolder>();
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

    public void Enqueue(CallFolder folder)
    {
        Interlocked.Increment(ref _pending);
        _channel.Writer.TryWrite(folder);
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var folder in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                CurrentFolderPath = folder.Path;
                try
                {
                    var result = await ProcessAsync(folder, cancellationToken).ConfigureAwait(false);
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

    private async Task<ProcessingResult> ProcessAsync(CallFolder folder, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var progress = new Progress<string>(Report);
        string modelPath = await EnsureModelAsync(settings, cancellationToken).ConfigureAwait(false);

        ISummarizer? summarizer = CreateSummarizer(settings, out string? unavailableReason);
        var processor = new CallProcessor(
            () => new WhisperTranscriber(modelPath, settings.Language, settings.Vocabulary, settings.UseGpu),
            () => summarizer);

        var options = new ProcessingOptions(settings.MyName, settings.OthersName, Path.GetFileName(modelPath), unavailableReason);
        Report("Préparation de la transcription…");
        return await processor.ProcessAsync(folder, options, progress, cancellationToken).ConfigureAwait(false);
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
