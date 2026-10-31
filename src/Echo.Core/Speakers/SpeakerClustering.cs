namespace Echo.Core.Speakers;

/// <summary>
/// Groups the voice prints of a whole call into speakers (agglomerative clustering, average
/// linkage on cosine similarity), then reuses the names given during the call.
/// </summary>
public static class SpeakerClustering
{
    /// <summary>Cluster index (0-based) for each embedding.</summary>
    public static int[] Cluster(IReadOnlyList<float[]> embeddings, double threshold = 0.66)
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
        double matchThreshold = 0.68)
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
