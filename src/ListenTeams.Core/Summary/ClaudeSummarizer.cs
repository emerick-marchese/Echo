using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace ListenTeams.Core.Summary;

/// <summary>Meeting report written by Claude through the Anthropic API.</summary>
public sealed class ClaudeSummarizer : ISummarizer
{
    /// <summary>Models that accept adaptive thinking, effort and server-side refusal fallbacks.</summary>
    private static readonly HashSet<string> CurrentModels =
        ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5"];

    private readonly string _apiKey;
    private readonly string _model;

    public ClaudeSummarizer(string apiKey, string model)
    {
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "claude-opus-5-5" : model.Trim();
    }

    public string DisplayName => $"Claude ({_model})";

    public async Task<string> SummarizeAsync(SummaryRequest request, CancellationToken cancellationToken)
    {
        var client = new AnthropicClient { ApiKey = _apiKey };

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 32000,
            System = SummaryPrompt.System(request),
            Messages = [new BetaUserMessageParam { Content = SummaryPrompt.User(request) }],
        };

        if (CurrentModels.Contains(_model))
        {
            parameters = parameters with
            {
                Thinking = new BetaThinkingConfigAdaptive(),
                OutputConfig = new BetaOutputConfig { Effort = Effort.High },
                // If a safety classifier declines the request, let the API re-run it on the
                // recommended fallback model instead of returning an empty report.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new BetaFallbacksParam(new Default()),
            };
        }

        var text = new StringBuilder();
        string? stopReason = null;
        try
        {
            await foreach (var streamEvent in client.Beta.Messages.CreateStreaming(parameters, cancellationToken).ConfigureAwait(false))
            {
                if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var textDelta))
                {
                    text.Append(textDelta.Text);
                }
                else if (streamEvent.TryPickDelta(out var messageDelta) && messageDelta.Delta.StopReason is { } reason)
                {
                    // Normalise "max_tokens" / "MaxTokens" style values.
                    stopReason = reason.ToString().Replace("_", "").ToLowerInvariant();
                }
            }
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new SummaryException("Clé API Anthropic refusée : vérifiez-la dans les paramètres.", ex);
        }
        catch (AnthropicNotFoundException ex)
        {
            throw new SummaryException($"Modèle « {_model} » introuvable : vérifiez le nom du modèle dans les paramètres.", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new SummaryException("Limite d'utilisation de l'API Anthropic atteinte, réessayez plus tard.", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new SummaryException($"Erreur de l'API Anthropic : {ex.Message}", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new SummaryException("Impossible de joindre l'API Anthropic (connexion Internet ?).", ex);
        }

        if (stopReason is not null && stopReason.Contains("refusal", StringComparison.Ordinal))
        {
            throw new SummaryException("Le modèle a refusé de résumer cette transcription.");
        }

        string result = text.ToString().Trim();
        if (stopReason is not null && stopReason.Contains("maxtokens", StringComparison.Ordinal))
        {
            result += "\n\n> Compte rendu tronqué (longueur maximale atteinte).";
        }
        return result;
    }
}
