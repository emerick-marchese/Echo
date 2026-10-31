using System.Globalization;
using System.Net;
using System.Text;
using Echo.Core.Transcription;
using Markdig;

namespace Echo.Core.Output;

/// <summary>
/// Builds "compte-rendu.html": summary, audio player and transcript in one page. Clicking a
/// timestamp plays the call from that moment. Opens in any browser, no Internet needed.
/// </summary>
public static class ReportWriter
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string Build(
        CallMetadata metadata,
        string? summaryMarkdown,
        string? summaryError,
        IReadOnlyList<TranscriptParagraph> paragraphs,
        string myName,
        string othersName)
    {
        var html = new StringBuilder();
        string title = WebUtility.HtmlEncode(metadata.Title);
        html.Append($$"""
            <!doctype html>
            <html lang="fr">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}} — {{metadata.StartedAt.ToString("dd/MM/yyyy HH:mm", French)}}</title>
            <style>
            :root { --bg:#f7f7f9; --card:#fff; --text:#1d1d22; --muted:#6b6b76; --accent:#4f52b2; --me:#0f7b6c; --others:#4f52b2; --border:#e3e3ea; }
            @media (prefers-color-scheme: dark) { :root { --bg:#16161a; --card:#202026; --text:#ececf1; --muted:#9a9aa6; --accent:#9ea2ff; --me:#4fd1b5; --others:#9ea2ff; --border:#33333c; } }
            * { box-sizing: border-box; }
            body { margin:0; background:var(--bg); color:var(--text); font:16px/1.55 "Segoe UI", system-ui, sans-serif; }
            header { position:sticky; top:0; z-index:1; background:var(--card); border-bottom:1px solid var(--border); padding:14px 20px; }
            header h1 { margin:0 0 4px; font-size:20px; }
            header .meta { color:var(--muted); font-size:14px; margin-bottom:8px; }
            audio { width:100%; }
            main { max-width:900px; margin:0 auto; padding:20px; }
            section { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:4px 22px 18px; margin-bottom:20px; }
            section > h2:first-child { margin-top:18px; }
            table { border-collapse:collapse; width:100%; }
            th, td { border:1px solid var(--border); padding:6px 8px; text-align:left; vertical-align:top; }
            .error { color:#b3261e; }
            .p { display:grid; grid-template-columns:86px 1fr; gap:4px 12px; padding:6px 0; border-bottom:1px solid var(--border); }
            .p:last-child { border-bottom:none; }
            .t { font-variant-numeric:tabular-nums; color:var(--accent); cursor:pointer; background:none; border:none; padding:0; font:inherit; text-align:left; }
            .t:hover { text-decoration:underline; }
            .who { font-weight:600; margin-right:6px; }
            .me .who { color:var(--me); }
            .others .who { color:var(--others); }
            .playing { background:color-mix(in srgb, var(--accent) 12%, transparent); }
            </style>
            </head>
            <body>
            <header>
            <h1>{{title}}</h1>
            <div class="meta">{{WebUtility.HtmlEncode(metadata.App)}} · {{metadata.StartedAt.ToString("dddd d MMMM yyyy, HH:mm", French)}} · {{FormatDuration(metadata.Duration)}}</div>
            <audio id="audio" controls preload="metadata" src="{{CallFolder.AudioFileName}}"></audio>
            </header>
            <main>
            """);

        html.Append("<section id=\"synthese\">");
        if (!string.IsNullOrWhiteSpace(summaryMarkdown))
        {
            html.Append(MarkdownToHtml(summaryMarkdown));
        }
        else
        {
            html.Append("<h2>Synthèse</h2>");
            html.Append(summaryError is null
                ? "<p>Pas de synthèse pour cet appel.</p>"
                : $"<p class=\"error\">Synthèse indisponible : {WebUtility.HtmlEncode(summaryError)}</p>");
        }
        html.Append("</section>");

        html.Append("<section id=\"transcription\"><h2>Transcription</h2>");
        if (paragraphs.Count == 0)
        {
            html.Append("<p>Aucune parole détectée.</p>");
        }
        foreach (var p in paragraphs)
        {
            string css = p.Speaker == Speaker.Me ? "me" : "others";
            string who = WebUtility.HtmlEncode(TranscriptFormatter.SpeakerName(p.Speaker, myName, othersName));
            html.Append($"<div class=\"p {css}\" data-start=\"{Seconds(p.Start)}\" data-end=\"{Seconds(p.End)}\">")
                .Append($"<button class=\"t\" data-t=\"{Seconds(p.Start)}\">{TranscriptFormatter.Timestamp(p.Start)}</button>")
                .Append($"<div><span class=\"who\">{who}</span>{WebUtility.HtmlEncode(p.Text)}</div></div>");
        }
        html.Append("</section>");

        html.Append("""
            </main>
            <script>
            const audio = document.getElementById('audio');
            document.addEventListener('click', e => {
              const t = e.target.closest('[data-t]');
              if (!t) return;
              e.preventDefault();
              audio.currentTime = parseFloat(t.dataset.t);
              audio.play();
            });
            const paragraphs = [...document.querySelectorAll('.p')];
            let current = null;
            audio.addEventListener('timeupdate', () => {
              const now = audio.currentTime;
              const p = paragraphs.find(x => now >= +x.dataset.start && now < +x.dataset.end + 1);
              if (p === current) return;
              current?.classList.remove('playing');
              current = p ?? null;
              current?.classList.add('playing');
            });
            </script>
            </body>
            </html>
            """);
        return html.ToString();
    }

    /// <summary>Summary Markdown as safe HTML (raw HTML disabled), with clickable timestamps.</summary>
    public static string MarkdownToHtml(string markdown) => LinkTimestamps(Markdown.ToHtml(markdown, Pipeline));

    /// <summary>Makes "[hh:mm:ss]" timestamps inside the summary clickable.</summary>
    internal static string LinkTimestamps(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, @"\[(\d{1,2}):(\d{2}):(\d{2})\]", m =>
        {
            int seconds = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
            return $"<button class=\"t\" data-t=\"{seconds}\">[{m.Groups[1].Value}:{m.Groups[2].Value}:{m.Groups[3].Value}]</button>";
        });

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan d) => Summary.SummaryPrompt.FormatDuration(d);
}
