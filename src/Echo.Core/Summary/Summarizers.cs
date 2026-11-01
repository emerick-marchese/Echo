using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echo.Core.Summary;

public sealed record SummaryRequest(
    string App,
    string Title,
    DateTime StartedAt,
    TimeSpan Duration,
    string Transcript,
    string MyName,
    string OthersName,
    bool ExactNames = false);

public interface ISummarizer
{
    string DisplayName { get; }

    /// <summary>Returns the meeting report in Markdown.</summary>
    Task<string> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken);
}

public sealed class SummaryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Prompt shared by every summary provider.</summary>
public static class SummaryPrompt
{
    public static string System(SummaryRequest request) =>
        $"""
        Tu rédiges des comptes rendus d'appels {request.App} clairs, fidèles et directement exploitables.
        Tu travailles à partir d'une transcription automatique : elle peut contenir des erreurs de reconnaissance (noms propres, termes techniques, chiffres). Corrige-les quand le contexte rend la bonne forme évidente, sans rien inventer.
        {Speakers(request)}
        N'ajoute aucune information absente de la transcription. Rédige dans la langue majoritaire de la transcription.
        """;

    private static string Speakers(SummaryRequest request) => request.ExactNames
        ? $"Chaque intervention est attribuée à « {request.MyName} » (la personne qui a enregistré l'appel) ou à un autre participant, désigné par son pseudo {request.App} exact ; « {request.OthersName} » désigne les rares passages qui n'ont pas pu être attribués. Utilise ces pseudos tels quels pour nommer les participants."
        : $"Chaque intervention est attribuée à « {request.MyName} » (la personne qui a enregistré l'appel) ou à l'un des autres participants, reconnus à leur voix : « Intervenant 1 », « Intervenant 2 »… (ou le nom que l'utilisateur leur a donné), et « {request.OthersName} » quand la voix n'a pas pu être identifiée. La reconnaissance des voix peut parfois se tromper. Quand la conversation permet de savoir qui est un « Intervenant », utilise son vrai nom.";

    public static string User(SummaryRequest request) =>
        $"""
        Voici la transcription de l'appel « {request.Title} » du {request.StartedAt:dd/MM/yyyy à HH:mm} (durée {FormatDuration(request.Duration)}).
        Chaque ligne commence par l'horodatage [hh:mm:ss] et le nom de la personne qui parle.

        <transcription>
        {request.Transcript.Trim()}
        </transcription>

        Rédige le compte rendu en Markdown avec exactement cette structure :

        # Titre court et explicite, déduit du contenu

        ## Résumé
        Un paragraphe de 4 à 8 phrases : contexte, objet de l'appel, conclusions principales.

        ## Points clés
        Liste à puces des informations importantes échangées.

        ## Décisions
        Liste à puces des décisions prises, ou « Aucune décision explicite. ».

        ## Actions à mener
        Tableau Markdown avec les colonnes | Action | Responsable | Échéance | (« — » quand l'information manque), ou « Aucune action identifiée. ».

        ## Questions ouvertes
        Points restés sans réponse ou à clarifier, ou « Aucune. ».

        ## Déroulé
        Liste chronologique des grands sujets abordés, chacun précédé de l'horodatage [hh:mm:ss] où il commence.

        N'écris rien avant le titre ni après la dernière section.
        """;

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min" : $"{Math.Max(1, (int)Math.Round(d.TotalMinutes))} min";
}

/// <summary>Summary with a local model served by Ollama (https://ollama.com).</summary>
public sealed class OllamaSummarizer : ISummarizer
{
    private readonly string _baseUrl;
    private readonly string _model;

    public OllamaSummarizer(string baseUrl, string model)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
    }

    public string DisplayName => $"Ollama ({_model})";

    public async Task<string> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        var body = new OllamaChatRequest(
            _model,
            [new("system", SummaryPrompt.System(request)), new("user", SummaryPrompt.User(request))],
            Stream: false,
            Options: new OllamaOptions(NumCtx: EstimateContext(request.Transcript)));

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync($"{_baseUrl}/api/chat", body, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SummaryException($"Ollama ne répond pas sur {_baseUrl} : est-il installé et démarré ?", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new SummaryException($"Ollama a renvoyé {(int)response.StatusCode} : {error}");
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken).ConfigureAwait(false);
            string text = result?.Message?.Content ?? string.Empty;
            return StripThinking(text).Trim();
        }
    }

    /// <summary>Context window large enough for the whole transcript (≈ 3 characters per token).</summary>
    private static int EstimateContext(string transcript)
    {
        int needed = transcript.Length / 3 + 4096;
        int size = 8192;
        while (size < needed && size < 131072) size *= 2;
        return size;
    }

    /// <summary>Reasoning models (qwen3, deepseek-r1…) prepend a &lt;think&gt; block.</summary>
    private static string StripThinking(string text)
    {
        int end = text.IndexOf("</think>", StringComparison.Ordinal);
        return end >= 0 ? text[(end + "</think>".Length)..] : text;
    }

    private sealed record OllamaMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OllamaOptions([property: JsonPropertyName("num_ctx")] int NumCtx);

    private sealed record OllamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<OllamaMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] OllamaOptions Options);

    private sealed record OllamaChatResponse([property: JsonPropertyName("message")] OllamaMessage? Message);
}
