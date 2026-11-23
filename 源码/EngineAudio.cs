using System.Buffers.Binary;
using System.Numerics;
using NAudio.Wave;

namespace AudioRelay;

/// <summary>Decodes the LPCM/float WASAPI mix format into interleaved stereo float.</summary>
internal sealed class LoopbackDecoder
{
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid Ieee = new("00000003-0000-0010-8000-00AA00389B71");
    private readonly int channels, stride, sampleBytes;
    private readonly bool floating;
    private readonly (float Left, float Right)[] weights;

    public LoopbackDecoder(WaveFormat format)
    {
        channels = format.Channels;
        stride = format.BlockAlign;
        sampleBytes = format.BitsPerSample / 8;
        var ext = format as WaveFormatExtensible;
        floating = format.Encoding == WaveFormatEncoding.IeeeFloat || ext?.SubFormat == Ieee;
        bool pcm = format.Encoding == WaveFormatEncoding.Pcm || ext?.SubFormat == Pcm;
        if (channels < 1 || channels > 32 || format.SampleRate is < 8_000 or > 384_000 ||
            stride < channels * sampleBytes || (format.BitsPerSample % 8) != 0 ||
            !(floating ? sampleBytes is 4 or 8 : pcm && sampleBytes is >= 1 and <= 4))
            throw new NotSupportedException("当前输出使用不支持的音频格式。请在 Windows 声音设置中选用 PCM 格式（如 48 kHz 立体声）。");

        weights = new (float Left, float Right)[channels];
        if (channels == 1)
        {
            weights[0] = (1, 1);
            return;
        }
        if (channels == 2)
        {
            weights[0] = (1, 0);
            weights[1] = (0, 1);
            return;
        }

        var mask = 0U;
        if (ext != null)
        {
            // NAudio 2.x does not expose dwChannelMask as a public property.
            // Its WAVEFORMATEXTENSIBLE serializer does include the original mask:
            // 4 length bytes + 18 WAVEFORMATEX + 2 valid-bits bytes.
            using var memory = new MemoryStream(44);
            using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, leaveOpen: true))
                ext.Serialize(writer);
            if (memory.Length >= 28) mask = BinaryPrimitives.ReadUInt32LittleEndian(memory.GetBuffer().AsSpan(24, 4));
        }
        if (BitOperations.PopCount(mask) != channels)
            mask = channels switch
            {
                3 => 0x7,       // FL FR FC
                4 => 0x33,      // FL FR BL BR
                5 => 0x37,      // FL FR FC BL BR
                6 => 0x3f,      // FL FR FC LFE BL BR
                7 => 0x70f,     // FL FR FC LFE BC SL SR
                8 => 0x63f,     // FL FR FC LFE BL BR SL SR
                _ => channels == 32 ? uint.MaxValue : (1U << channels) - 1
            };

        int index = 0;
        for (var n = 0; n < 32; n++)
            if (((mask >> n) & 1U) != 0) weights[index++] = Speaker(n);
    }

    public void Decode(ReadOnlySpan<byte> source, Span<float> dest)
    {
        var frames = Math.Min(source.Length / stride, dest.Length / 2);
        for (int i = 0; i < frames; ++i)
        {
            float left = 0, right = 0;
            int offset = i * stride;
            for (int channel = 0; channel < channels; channel++)
            {
                var sample = Sample(source.Slice(offset + channel * sampleBytes, sampleBytes));
                if (!float.IsFinite(sample)) continue;
                // Guard against pathological floating-point endpoint samples.
                sample = Math.Clamp(sample, -4f, 4f);
                left += sample * weights[channel].Left;
                right += sample * weights[channel].Right;
            }
            dest[i * 2] = Math.Clamp(left, -1f, 1f);
            dest[i * 2 + 1] = Math.Clamp(right, -1f, 1f);
        }
    }

    private float Sample(ReadOnlySpan<byte> sample)
    {
        if (floating)
            return sampleBytes == 4
                ? BinaryPrimitives.ReadSingleLittleEndian(sample)
                : (float)BinaryPrimitives.ReadDoubleLittleEndian(sample);
        return sampleBytes switch
        {
            1 => (sample[0] - 128) / 128f, // 8-bit PCM is unsigned
            2 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
            3 => (((sample[0] | sample[1] << 8 | sample[2] << 16) << 8) >> 8) / 8388608f,
            4 => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
            _ => 0
        };
    }

    private static (float Left, float Right) Speaker(int speaker) => speaker switch
    {
        0 => (1, 0),            // front left
        1 => (0, 1),            // front right
        2 => (.7071f, .7071f),  // center voice
        3 => (.25f, .25f),      // LFE
        4 or 6 or 9 or 12 or 15 => (.7071f, 0), // left back/side/height
        5 or 7 or 10 or 14 or 17 => (0, .7071f),
        8 or 11 or 13 or 16 => (.5f, .5f),     // back or height center
        _ => (.35f, .35f)
    };
}
