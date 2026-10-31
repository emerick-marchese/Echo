namespace Echo.Core.Speakers;

/// <summary>A voice recognised during the call.</summary>
public sealed class TrackedSpeaker
{
    public required int Id { get; init; }

    public required string Name { get; set; }

    /// <summary>The user gave this voice a name.</summary>
    public bool Renamed { get; set; }

    public required float[] Centroid { get; set; }

    public int Observations { get; set; }

    public double TalkSeconds { get; set; }
}

/// <summary>
/// Groups successive voice prints into speakers as the call goes ("Intervenant 1", "2"…).
/// Thresholds measured with the CAM++ model on 1.6 s windows of recorded voices: a window
/// scores ≥ 0.78 against its own speaker and ≤ 0.64 against the others.
/// A new speaker is only created when two consecutive windows agree with each other but
/// with nobody known, so a cough or two people talking at once does not create a voice.
/// </summary>
public sealed class OnlineSpeakerTracker
{
    private readonly double _matchThreshold;
    private readonly double _mergeThreshold;
    private readonly string _namePrefix;
    private readonly List<TrackedSpeaker> _speakers = [];
    private float[]? _pending;
    private int _nextId = 1;

    public OnlineSpeakerTracker(double matchThreshold = 0.68, double mergeThreshold = 0.8, string namePrefix = "Intervenant")
    {
        _matchThreshold = matchThreshold;
        _mergeThreshold = mergeThreshold;
        _namePrefix = namePrefix;
    }

    public IReadOnlyList<TrackedSpeaker> Speakers => _speakers;

    /// <summary>Assigns a voice print to a speaker; null while a new voice is not confirmed yet.</summary>
    public TrackedSpeaker? Observe(float[] embedding, double seconds)
    {
        var (best, similarity) = BestMatch(embedding);
        if (best is not null && similarity >= _matchThreshold)
        {
            _pending = null;
            Update(best, embedding, seconds);
            return best;
        }

        if (_pending is not null && SpeakerMath.Cosine(_pending, embedding) >= _matchThreshold)
        {
            var speaker = new TrackedSpeaker
            {
                Id = _nextId,
                Name = $"{_namePrefix} {_nextId}",
                Centroid = SpeakerMath.Blend(_pending, embedding, 0.5),
                Observations = 2,
                TalkSeconds = seconds * 2,
            };
            _nextId++;
            _speakers.Add(speaker);
            _pending = null;
            return MergeSimilar(speaker);
        }

        _pending = embedding;
        return null;
    }

    public bool Rename(int id, string name)
    {
        var speaker = _speakers.FirstOrDefault(s => s.Id == id);
        string clean = name.Trim();
        if (speaker is null || clean.Length == 0) return false;
        speaker.Name = clean.Length > 40 ? clean[..40] : clean;
        speaker.Renamed = true;
        return true;
    }

    private (TrackedSpeaker? Speaker, double Similarity) BestMatch(float[] embedding)
    {
        TrackedSpeaker? best = null;
        double bestSimilarity = double.MinValue;
        foreach (var speaker in _speakers)
        {
            double similarity = SpeakerMath.Cosine(speaker.Centroid, embedding);
            if (similarity > bestSimilarity)
            {
                best = speaker;
                bestSimilarity = similarity;
            }
        }
        return (best, bestSimilarity);
    }

    private static void Update(TrackedSpeaker speaker, float[] embedding, double seconds)
    {
        speaker.Observations++;
        // Running mean at first, then a slowly moving average that follows the voice.
        double weight = Math.Max(1.0 / speaker.Observations, 0.05);
        speaker.Centroid = SpeakerMath.Blend(speaker.Centroid, embedding, weight);
        speaker.TalkSeconds += seconds;
    }

    /// <summary>A voice created by mistake twice is folded back into the existing one.</summary>
    private TrackedSpeaker MergeSimilar(TrackedSpeaker created)
    {
        foreach (var other in _speakers)
        {
            if (other == created || SpeakerMath.Cosine(other.Centroid, created.Centroid) < _mergeThreshold) continue;
            other.TalkSeconds += created.TalkSeconds;
            other.Observations += created.Observations;
            _speakers.Remove(created);
            _nextId--;
            return other;
        }
        return created;
    }
}
