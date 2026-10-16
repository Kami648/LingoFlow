using System.Text;

namespace LingoFlow.Services;

public record AudioChunk(short[] Samples, double Start, double End)
{
    public double OverlapSeconds { get; init; }
    // A transport slice is not necessarily a complete sentence.
    public bool IsUtteranceEnd { get; init; } = true;
    public double Duration => Samples.Length / 16000d;
    public byte[] ToWave() => Wave.Encode(Samples);
}
public static class Wave
{
    public static short[] ReadPcm16(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new IOException("音频须为 16 kHz 单声道 PCM WAV。");
        reader.ReadInt32(); reader.ReadInt32();
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            string name = new(reader.ReadChars(4)); int count = reader.ReadInt32();
            if (name == "fmt ")
            {
                if (count < 16) throw new IOException("无效 PCM 格式。");
                short format = reader.ReadInt16(), channels = reader.ReadInt16(); int rate = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadInt16(); short bits = reader.ReadInt16();
                if (format != 1 || channels != 1 || rate != 16000 || bits != 16) throw new IOException("音频须为 16 kHz 单声道 16 位 PCM。");
                reader.BaseStream.Seek(count - 16 + (count & 1), SeekOrigin.Current);
            }
            else if (name == "data")
            { var bytes = reader.ReadBytes(count); var samples = new short[bytes.Length / 2]; Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2); return samples; }
            else reader.BaseStream.Seek(count + (count & 1), SeekOrigin.Current);
        }
        throw new IOException("音频缺少数据。");
    }
    public static byte[] Encode(short[] samples)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.UTF8, true);
        writer.Write("RIFF"u8); writer.Write(36 + samples.Length * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples.Length * 2);
        foreach (var sample in samples) writer.Write(sample);
        return memory.ToArray();
    }
}
public sealed class AudioSegmenter
{
    private readonly List<short> pending = [], speech = [];
    private readonly Queue<(short[] Samples, double Level)> preroll = new();
    private readonly List<double> levels = [];
    private readonly int maxSamples, silenceSamples, softSamples, phraseSilence;
    private readonly double threshold;
    private int quiet, leadingOverlap;
    private bool continuation;
    private long cursor, start;
    public event Action<AudioChunk>? Ready;
    public event Action<double>? UtteranceEnded;
    public event Action<double>? Level;
    public AudioSegmenter(bool accurate, double threshold, double? maxDurationSeconds = null)
    {
        maxSamples = (int)((maxDurationSeconds ?? (accurate ? 4.0 : 2.4)) * 16000);
        silenceSamples = (int)((accurate ? .9 : .65) * 16000);
        softSamples = (int)((accurate ? 1.6 : 1.0) * 16000);
        phraseSilence = silenceSamples;
        this.threshold = threshold;
    }
    public void Push(short[] samples)
    {
        pending.AddRange(samples);
        int offset = 0;
        while (pending.Count - offset >= 320)
        {
            var frame = pending.GetRange(offset, 320).ToArray(); offset += 320;
            double rms = Math.Sqrt(frame.Sum(v => (double)v * v) / frame.Length) / 32768;
            Level?.Invoke(rms);
            bool active = rms >= threshold;
            if (speech.Count == 0)
            {
                if (!active) { preroll.Enqueue((frame, rms)); while (preroll.Count > 10) preroll.Dequeue(); cursor += 320; continue; }
                start = cursor - preroll.Count * 320;
                while (preroll.TryDequeue(out var before)) { speech.AddRange(before.Samples); levels.Add(before.Level); }
            }
            speech.AddRange(frame); levels.Add(rms); cursor += 320;
            if (active) quiet = 0; else quiet += 320;
            if (quiet >= silenceSamples || (speech.Count >= softSamples && quiet >= phraseSilence)) EmitAt(speech.Count, false);
            else if (speech.Count >= maxSamples) EmitAt(FindBoundary(), true);
        }
        if (offset > 0) pending.RemoveRange(0, offset);
    }
    private int FindBoundary()
    {
        // Prefer the end of a low-energy 60 ms interval in the last 600 ms.
        // The suffix after this boundary remains in the next slice.
        int best = levels.Count; double score = double.MaxValue;
        for (int end = Math.Max(softSamples / 320, levels.Count - 30); end <= levels.Count; end++)
        {
            if (end < 3) continue;
            double mean = (levels[end - 1] + levels[end - 2] + levels[end - 3]) / 3;
            if (mean < threshold * 1.15 && mean <= score) { best = end; score = mean; }
        }
        return best * 320;
    }
    private void EmitAt(int count, bool carryContext)
    {
        int freshVoice = levels.Skip(leadingOverlap / 320).Take(Math.Max(0, (count - leadingOverlap) / 320)).Count(v => v >= threshold) * 320;
        bool valid = continuation ? freshVoice > 0 : freshVoice >= 1600 && count >= 3200;
        if (valid)
            Ready?.Invoke(new(speech.Take(count).ToArray(), start / 16000d, (start + count) / 16000d) { OverlapSeconds = leadingOverlap / 16000d, IsUtteranceEnd = !carryContext });
        else if (!carryContext && continuation)
            UtteranceEnded?.Invoke((start + count) / 16000d);
        int overlap = carryContext && valid ? Math.Min(count, 5760) : 0;
        int remove = count - overlap;
        speech.RemoveRange(0, remove); levels.RemoveRange(0, remove / 320); start += remove;
        leadingOverlap = overlap; continuation = overlap > 0;
        quiet = 0; for (int i = levels.Count - 1; i >= 0 && levels[i] < threshold; i--) quiet += 320;
    }
    public void Flush()
    {
        if (pending.Count > 0) Push(new short[320 - pending.Count]);
        if (speech.Count > 0) EmitAt(speech.Count, false);
        else UtteranceEnded?.Invoke(cursor / 16000d);
    }
}
