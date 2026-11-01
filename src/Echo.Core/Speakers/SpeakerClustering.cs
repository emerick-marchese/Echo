namespace Echo.Core.Speakers;

/// <summary>
/// Groups the voice prints of a whole call into speakers (agglomerative clustering, average
/// linkage on cosine similarity), then reuses the names given during the call.
/// Measured on real voices through call codecs: 3 s of the same person score 0.61 or more
/// (median 0.74), different people at most 0.29; sentences of 1–2 s vary more.
/// </summary>
public static class SpeakerClustering
{
    /// <summary>A group with less speech than this is too little to be trusted as a person.</summary>
    public const double MinGroupSeconds = 2.5;

    /// <summary>Cluster index (0-based) for each embedding.</summary>
    /// <param name="durations">Seconds of speech behind each embedding: groups with very little
    /// speech join the group they sound closest to (a cough, a laugh, a short "oui").</param>
    public static int[] Cluster(IReadOnlyList<float[]> embeddings, double threshold = 0.5, IReadOnlyList<double>? durations = null, double absorbThreshold = 0.3)
    {
        int n = embeddings.Count;
        var labels = Enumerable.Range(0, n).ToArray();
        if (n <= 1) return labels;

        var sim = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                sim[i, j] = sim[j, i] = SpeakerMath.Cosine(embeddings[i], embeddings[j]);

        var size = Enumerable.Repeat(1, n).ToArray();
        var alive = Enumerable.Repeat(true, n).ToArray();
        while (true)
        {
            int a = -1, b = -1;
            double best = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (!alive[i]) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (alive[j] && sim[i, j] > best)
                    {
                        best = sim[i, j];
                        a = i;
                        b = j;
                    }
                }
            }
            if (a < 0 || best < threshold) break;
            Merge(a, b);
        }

        // Tiny groups join the closest group, if it sounds at least a little alike.
        if (durations is not null)
        {
            var seconds = new double[n];
            for (int i = 0; i < n; i++) seconds[labels[i]] += durations[i];
            while (true)
            {
                int small = -1;
                for (int i = 0; i < n; i++)
                {
                    if (alive[i] && seconds[i] < MinGroupSeconds && (small < 0 || seconds[i] < seconds[small])) small = i;
                }
                if (small < 0) break;

                int target = -1;
                double best = absorbThreshold;
                for (int k = 0; k < n; k++)
                {
                    if (alive[k] && k != small && seconds[k] >= MinGroupSeconds && sim[small, k] >= best)
                    {
                        best = sim[small, k];
                        target = k;
                    }
                }
                if (target < 0)
                {
                    seconds[small] = double.MaxValue; // unlike anybody: keep it
                    continue;
                }
                seconds[target] += seconds[small];
                Merge(target, small);
            }
        }

        void Merge(int a, int b)
        {
            // Merge b into a; average linkage (Lance–Williams update).
            for (int k = 0; k < n; k++)
            {
                if (!alive[k] || k == a || k == b) continue;
                double merged = (size[a] * sim[a, k] + size[b] * sim[b, k]) / (size[a] + size[b]);
                sim[a, k] = sim[k, a] = merged;
            }
            size[a] += size[b];
            alive[b] = false;
            for (int k = 0; k < n; k++)
            {
                if (labels[k] == b) labels[k] = a;
            }
        }

        // Renumber 0..K-1 in order of first appearance.
        var map = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            if (!map.ContainsKey(labels[i])) map[labels[i]] = map.Count;
            labels[i] = map[labels[i]];
        }
        return labels;
    }

    /// <summary>
    /// Final voice numbers and names: a cluster that sounds like a voice seen during the
    /// call keeps that voice's number and name (e.g. renamed "Sarah"); others get new numbers.
    /// </summary>
    public static (int[] Voices, Dictionary<int, string> Names) Name(
        IReadOnlyList<float[]> embeddings,
        int[] clusters,
        IReadOnlyList<SpeakerProfile> liveProfiles,
        double matchThreshold = 0.55)
    {
        int count = clusters.Length == 0 ? 0 : clusters.Max() + 1;
        var centroids = new float[count][];
        for (int c = 0; c < count; c++)
        {
            var members = Enumerable.Range(0, clusters.Length).Where(i => clusters[i] == c).ToList();
            var sum = new float[embeddings[members[0]].Length];
            foreach (int i in members)
                for (int d = 0; d < sum.Length; d++) sum[d] += embeddings[i][d];
            centroids[c] = SpeakerMath.Normalize(sum);
        }

        // Greedy matching, most similar pairs first.
        var pairs = new List<(int Cluster, SpeakerProfile Profile, double Similarity)>();
        for (int c = 0; c < count; c++)
            foreach (var profile in liveProfiles)
            {
                double similarity = SpeakerMath.Cosine(centroids[c], profile.Centroid);
                if (similarity >= matchThreshold) pairs.Add((c, profile, similarity));
            }

        var voiceOfCluster = new Dictionary<int, int>();
        var names = new Dictionary<int, string>();
        var usedProfiles = new HashSet<int>();
        foreach (var (cluster, profile, _) in pairs.OrderByDescending(p => p.Similarity))
        {
            if (voiceOfCluster.ContainsKey(cluster) || !usedProfiles.Add(profile.Id)) continue;
            voiceOfCluster[cluster] = profile.Id;
            names[profile.Id] = profile.Name;
        }

        int next = Math.Max(liveProfiles.Count == 0 ? 0 : liveProfiles.Max(p => p.Id), 0) + 1;
        for (int c = 0; c < count; c++)
        {
            if (voiceOfCluster.ContainsKey(c)) continue;
            voiceOfCluster[c] = next;
            names[next] = Transcription.SpeakerNaming.DefaultVoiceName(next);
            next++;
        }

        return (clusters.Select(c => voiceOfCluster[c]).ToArray(), names);
    }
}
