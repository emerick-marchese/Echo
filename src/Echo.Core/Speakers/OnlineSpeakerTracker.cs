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
/// Thresholds measured with the CAM++ model on 1.6 s windows of real recorded voices passed
/// through call codecs (Opus 16 then 24 kb/s): two windows of the same person score 0.47 or
/// more 95 % of the time (median 0.60), two different people at most 0.38. Real voices vary
/// much more than synthetic ones, so a high threshold splits one person into several voices.
/// A new speaker is only created when three consecutive windows (1.2 s) agree with each other
/// but with nobody known, so a cough or two people talking at once does not create a voice.
/// </summary>
public sealed class OnlineSpeakerTracker
{
    private readonly double _matchThreshold;
    private readonly double _mergeThreshold;
    private readonly int _confirmWindows;
    private readonly string _namePrefix;
    private readonly List<TrackedSpeaker> _speakers = [];
    private readonly List<float[]> _pending = [];
    private int _nextId = 1;

    public OnlineSpeakerTracker(double matchThreshold = 0.5, double mergeThreshold = 0.65, string namePrefix = "Intervenant", int confirmWindows = 3)
    {
        _matchThreshold = matchThreshold;
        _mergeThreshold = mergeThreshold;
        _confirmWindows = Math.Max(1, confirmWindows);
        _namePrefix = namePrefix;
    }

    public IReadOnlyList<TrackedSpeaker> Speakers => _speakers;

    /// <summary>Assigns a voice print to a speaker; null while a new voice is not confirmed yet.</summary>
    public TrackedSpeaker? Observe(float[] embedding, double seconds)
    {
        var (best, similarity) = BestMatch(embedding);
        if (best is not null && similarity >= _matchThreshold)
        {
            _pending.Clear();
            Update(best, embedding, seconds);
            // Voices drift as they learn: fold together those that turn out to be the same.
            return best.Observations % 10 == 0 ? MergeSimilar(best) : best;
        }

        // An unknown voice: it must persist (consecutive windows that agree) to become a speaker.
        if (_pending.Count > 0 && _pending.Any(p => SpeakerMath.Cosine(p, embedding) < _matchThreshold)) _pending.Clear();
        _pending.Add(embedding);
        if (_pending.Count < _confirmWindows) return null;

        var centroid = _pending[0];
        for (int i = 1; i < _pending.Count; i++) centroid = SpeakerMath.Blend(centroid, _pending[i], 1.0 / (i + 1));
        var speaker = new TrackedSpeaker
        {
            Id = _nextId,
            Name = $"{_namePrefix} {_nextId}",
            Centroid = centroid,
            Observations = _pending.Count,
            TalkSeconds = seconds * _pending.Count,
        };
        _nextId++;
        _speakers.Add(speaker);
        _pending.Clear();
        return MergeSimilar(speaker);
    }

    /// <summary>Names a voice. Giving it the name of another voice merges both (same person).</summary>
    public bool Rename(int id, string name)
    {
        var speaker = _speakers.FirstOrDefault(s => s.Id == id);
        string clean = name.Trim();
        if (speaker is null || clean.Length == 0) return false;
        clean = clean.Length > 40 ? clean[..40] : clean;

        var same = _speakers.FirstOrDefault(s => s != speaker && string.Equals(s.Name, clean, StringComparison.CurrentCultureIgnoreCase));
        if (same is not null)
        {
            // Keep the voice that spoke most, with what both learned.
            var (keep, drop) = same.TalkSeconds >= speaker.TalkSeconds ? (same, speaker) : (speaker, same);
            Absorb(keep, drop);
            keep.Name = clean;
            keep.Renamed = true;
            return true;
        }

        speaker.Name = clean;
        speaker.Renamed = true;
        return true;
    }

    private void Absorb(TrackedSpeaker keep, TrackedSpeaker drop)
    {
        double weight = drop.Observations / (double)Math.Max(1, keep.Observations + drop.Observations);
        keep.Centroid = SpeakerMath.Blend(keep.Centroid, drop.Centroid, weight);
        keep.Observations += drop.Observations;
        keep.TalkSeconds += drop.TalkSeconds;
        if (drop.Renamed && !keep.Renamed)
        {
            keep.Name = drop.Name;
            keep.Renamed = true;
        }
        _speakers.Remove(drop);
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

    /// <summary>Folds into <paramref name="speaker"/> (or into an older voice) the voices that sound the same.</summary>
    private TrackedSpeaker MergeSimilar(TrackedSpeaker speaker)
    {
        foreach (var other in _speakers.ToList())
        {
            if (other == speaker || !_speakers.Contains(other) || SpeakerMath.Cosine(other.Centroid, speaker.Centroid) < _mergeThreshold) continue;
            // The older voice (lower number) stays, so names on screen do not jump.
            var (keep, drop) = other.Id < speaker.Id ? (other, speaker) : (speaker, other);
            Absorb(keep, drop);
            speaker = keep;
        }
        if (_speakers.Count == 0 || _speakers.Max(s => s.Id) < _nextId - 1) _nextId = (_speakers.Count == 0 ? 0 : _speakers.Max(s => s.Id)) + 1;
        return speaker;
    }
}
