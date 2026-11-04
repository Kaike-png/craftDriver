using System.Security.Cryptography;

namespace CraftDriver.Core.Graph;

/// <summary>
/// QuickXorHash do OneDrive (porta da implementação de referência da Microsoft).
/// Usado para validar downloads.
/// </summary>
public sealed class QuickXorHash : HashAlgorithm
{
    const int BitsInLastCell = 32;
    const byte Shift = 11;
    const byte WidthInBits = 160;

    ulong[] _data = null!;
    long _lengthSoFar;
    int _shiftSoFar;

    public QuickXorHash() => Initialize();

    public override int HashSize => WidthInBits;

    public override void Initialize()
    {
        _data = new ulong[(WidthInBits - 1) / 64 + 1];
        _shiftSoFar = 0;
        _lengthSoFar = 0;
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        unchecked
        {
            var currentShift = _shiftSoFar;
            var vectorArrayIndex = currentShift / 64;
            var vectorOffset = currentShift % 64;
            var iterations = Math.Min(cbSize, WidthInBits);

            for (var i = 0; i < iterations; i++)
            {
                var isLastCell = vectorArrayIndex == _data.Length - 1;
                var bitsInVectorCell = isLastCell ? BitsInLastCell : 64;

                if (vectorOffset <= bitsInVectorCell - 8)
                {
                    for (var j = ibStart + i; j < cbSize + ibStart; j += WidthInBits)
                        _data[vectorArrayIndex] ^= (ulong)array[j] << vectorOffset;
                }
                else
                {
                    var index1 = vectorArrayIndex;
                    var index2 = isLastCell ? 0 : vectorArrayIndex + 1;
                    var low = (byte)(bitsInVectorCell - vectorOffset);
                    byte xoredByte = 0;
                    for (var j = ibStart + i; j < cbSize + ibStart; j += WidthInBits)
                        xoredByte ^= array[j];
                    _data[index1] ^= (ulong)xoredByte << vectorOffset;
                    _data[index2] ^= (ulong)xoredByte >> low;
                }

                vectorOffset += Shift;
                while (vectorOffset >= bitsInVectorCell)
                {
                    vectorArrayIndex = isLastCell ? 0 : vectorArrayIndex + 1;
                    vectorOffset -= bitsInVectorCell;
                }
            }

            _shiftSoFar = (_shiftSoFar + Shift * (cbSize % WidthInBits)) % WidthInBits;
        }
        _lengthSoFar += cbSize;
    }

    protected override byte[] HashFinal()
    {
        var rgb = new byte[(WidthInBits - 1) / 8 + 1];
        for (var i = 0; i < _data.Length - 1; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(_data[i]), 0, rgb, i * 8, 8);
        Buffer.BlockCopy(BitConverter.GetBytes(_data[^1]), 0, rgb, (_data.Length - 1) * 8, rgb.Length - (_data.Length - 1) * 8);

        var lengthBytes = BitConverter.GetBytes(_lengthSoFar);
        for (var i = 0; i < lengthBytes.Length; i++)
            rgb[WidthInBits / 8 - lengthBytes.Length + i] ^= lengthBytes[i];
        return rgb;
    }

    public static string Compute(byte[] data)
    {
        using var h = new QuickXorHash();
        return Convert.ToBase64String(h.ComputeHash(data));
    }

    public static string ComputeFile(string path)
    {
        using var h = new QuickXorHash();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
        return Convert.ToBase64String(h.ComputeHash(fs));
    }
}
