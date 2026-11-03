using System.Runtime.InteropServices;

namespace EqualTune.AudioLab;

internal readonly record struct WasapiAudioFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign, int BytesPerSample, bool IsFloat)
{
    public static WasapiAudioFormat Read(IntPtr pointer)
    {
        var tag = (ushort)Marshal.ReadInt16(pointer, 0);
        var channels = (ushort)Marshal.ReadInt16(pointer, 2);
        var rate = Marshal.ReadInt32(pointer, 4);
        var align = (ushort)Marshal.ReadInt16(pointer, 12);
        var bits = (ushort)Marshal.ReadInt16(pointer, 14);
        var extra = (ushort)Marshal.ReadInt16(pointer, 16);
        var isFloat = tag == 3;
        if (tag == 0xFFFE && extra >= 22)
        {
            var subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(pointer, 24));
            isFloat = subFormat == new Guid("00000003-0000-0010-8000-00AA00389B71");
            var isPcm = subFormat == new Guid("00000001-0000-0010-8000-00AA00389B71");
            if (!isFloat && !isPcm) throw new NotSupportedException("The audio endpoint uses an unsupported sample format.");
        }
        else if (tag != 1 && tag != 3)
        {
            throw new NotSupportedException($"The audio endpoint uses unsupported wave format {tag}.");
        }
        if ((isFloat && bits != 32) || (!isFloat && bits is not (16 or 24 or 32)))
            throw new NotSupportedException($"The audio endpoint uses unsupported {bits}-bit {(isFloat ? "float" : "PCM")} audio.");
        if (rate <= 0 || channels is 0 or > 8 || align < channels * (bits / 8))
            throw new InvalidOperationException("Windows returned an invalid audio mix format.");
        return new WasapiAudioFormat(rate, channels, bits, align, bits / 8, isFloat);
    }

    public float ReadSample(IntPtr data, int offset)
    {
        if (IsFloat) return BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, offset));
        return BitsPerSample switch
        {
            16 => Marshal.ReadInt16(data, offset) / 32768f,
            24 => ReadInt24(data, offset) / 8388608f,
            32 => Marshal.ReadInt32(data, offset) / 2147483648f,
            _ => 0
        };
    }

    public void WriteSample(IntPtr data, int offset, double value)
    {
        value = Math.Clamp(value, -1, 1);
        if (IsFloat)
        {
            Marshal.WriteInt32(data, offset, BitConverter.SingleToInt32Bits((float)value));
            return;
        }
        switch (BitsPerSample)
        {
            case 16:
                Marshal.WriteInt16(data, offset, (short)Math.Round(value * short.MaxValue));
                break;
            case 24:
                var sample24 = (int)Math.Round(value * 8388607);
                Marshal.WriteByte(data, offset, (byte)sample24);
                Marshal.WriteByte(data, offset + 1, (byte)(sample24 >> 8));
                Marshal.WriteByte(data, offset + 2, (byte)(sample24 >> 16));
                break;
            case 32:
                Marshal.WriteInt32(data, offset, (int)Math.Round(value * int.MaxValue));
                break;
        }
    }

    private static int ReadInt24(IntPtr data, int offset)
    {
        var value = Marshal.ReadByte(data, offset) | (Marshal.ReadByte(data, offset + 1) << 8) | (Marshal.ReadByte(data, offset + 2) << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
}
