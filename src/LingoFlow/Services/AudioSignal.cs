namespace LingoFlow.Services;

public static class AudioSignal
{
    public static bool IsStationaryTone(short[] samples)
    {
        // Reject only a long, nearly identical periodic waveform with a stable
        // envelope. Ordinary voiced speech has pitch and amplitude variation.
        const int frame = 1600;
        int frames = samples.Length / frame;
        if (frames < 8) return false;
        var energy = new double[frames];
        for (int n = 0; n < frames; n++)
        {
            double sum = 0;
            for (int i = n * frame; i < (n + 1) * frame; i++) sum += (double)samples[i] * samples[i];
            energy[n] = Math.Sqrt(sum / frame);
        }
        double mean = energy.Average();
        if (mean < 3) return false;
        if (Math.Sqrt(energy.Sum(e => (e - mean) * (e - mean)) / frames) / mean > .015) return false;
        double Difference(int lag, int end)
        {
            double difference = 0, total = 0;
            for (int i = 400; i < end; i += 8)
            {
                double a = samples[i], b = samples[i - lag];
                difference += (a - b) * (a - b); total += a * a + b * b;
            }
            return difference / Math.Max(1, total);
        }
        int bestLag = 32; double best = double.MaxValue;
        for (int lag = 32; lag <= 400; lag++)
        {
            double score = Difference(lag, frame);
            if (score < best) { best = score; bestLag = lag; }
        }
        return best < .0005 && Difference(bestLag, samples.Length) < .0005;
    }
}
