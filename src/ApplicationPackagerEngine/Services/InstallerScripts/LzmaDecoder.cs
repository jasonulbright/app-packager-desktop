namespace ApplicationPackagerEngine.Services.InstallerScripts;

/// <summary>
/// LZMA1 decoder over a byte array. The whole output buffer doubles as the
/// dictionary because decoding always starts at the stream beginning and
/// stops after the requested number of bytes. Covers the raw streams NSIS
/// writes (properties byte plus dictionary size, no size field) and the
/// Inno Setup header block (5-byte properties prefix, no size field).
/// </summary>
public sealed class LzmaDecoder
{
    private byte[] _in = [];
    private int _pos;
    private int _end;
    private uint _range;
    private uint _code;
    private bool _corrupted;

    private byte[] _out = [];
    private int _outPos;

    private const int NumBitModelTotalBits = 11;
    private const uint BitModelTotal = 1u << NumBitModelTotalBits;
    private const int NumMoveBits = 5;
    private const uint TopValue = 1u << 24;
    private const int NumStates = 12;
    private const int NumPosBitsMax = 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    private const int NumAlignBits = 4;
    private const int MatchMinLen = 2;

    private sealed class LenDecoder
    {
        public readonly ushort[] Choice = InitProbs(2);
        public readonly ushort[] Low = InitProbs(1 << (NumPosBitsMax + 3));
        public readonly ushort[] Mid = InitProbs(1 << (NumPosBitsMax + 3));
        public readonly ushort[] High = InitProbs(1 << 8);
    }

    private static ushort[] InitProbs(int count)
    {
        var p = new ushort[count];
        for (var i = 0; i < count; i++) p[i] = (ushort)(BitModelTotal >> 1);
        return p;
    }

    private byte ReadByte()
    {
        if (_pos < _end) return _in[_pos++];
        _corrupted = true;
        return 0;
    }

    private void RangeInit()
    {
        _code = 0;
        _range = 0xFFFFFFFF;
        if (ReadByte() != 0) _corrupted = true;
        for (var i = 0; i < 4; i++) _code = (_code << 8) | ReadByte();
        if (_code == _range) _corrupted = true;
    }

    private void Normalize()
    {
        if (_range < TopValue)
        {
            _range <<= 8;
            _code = (_code << 8) | ReadByte();
        }
    }

    private uint DecodeDirectBits(int numBits)
    {
        uint res = 0;
        do
        {
            _range >>= 1;
            _code -= _range;
            var t = 0u - (_code >> 31);
            _code += _range & t;
            if (_code == _range) _corrupted = true;
            Normalize();
            res <<= 1;
            res += t + 1;
        }
        while (--numBits != 0);
        return res;
    }

    private uint DecodeBit(ushort[] probs, int index)
    {
        uint v = probs[index];
        var bound = (_range >> NumBitModelTotalBits) * v;
        uint symbol;
        if (_code < bound)
        {
            v += (BitModelTotal - v) >> NumMoveBits;
            _range = bound;
            symbol = 0;
        }
        else
        {
            v -= v >> NumMoveBits;
            _code -= bound;
            _range -= bound;
            symbol = 1;
        }
        probs[index] = (ushort)v;
        Normalize();
        return symbol;
    }

    private uint BitTreeDecode(ushort[] probs, int offset, int numBits)
    {
        uint m = 1;
        for (var i = 0; i < numBits; i++) m = (m << 1) + DecodeBit(probs, offset + (int)m);
        return m - (1u << numBits);
    }

    private uint BitTreeReverseDecode(ushort[] probs, int offset, int numBits)
    {
        uint m = 1;
        uint symbol = 0;
        for (var i = 0; i < numBits; i++)
        {
            var bit = DecodeBit(probs, offset + (int)m);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }
        return symbol;
    }

    private uint DecodeLen(LenDecoder ld, int posState)
    {
        if (DecodeBit(ld.Choice, 0) == 0) return BitTreeDecode(ld.Low, posState << 3, 3);
        if (DecodeBit(ld.Choice, 1) == 0) return 8 + BitTreeDecode(ld.Mid, posState << 3, 3);
        return 16 + BitTreeDecode(ld.High, 0, 8);
    }

    /// <summary>The largest output one <see cref="Decode"/> call produces; a larger request is refused before any allocation.</summary>
    public const int MaxOutputBytes = 256 * 1024 * 1024;

    private const int InitialOutputBytes = 64 * 1024;

    private int _outLimit;

    private void PutByte(byte b)
    {
        if (_outPos == _out.Length)
        {
            var grown = (int)Math.Min(_outLimit, Math.Max(InitialOutputBytes, (long)_out.Length * 2));
            Array.Resize(ref _out, grown);
        }
        _out[_outPos++] = b;
    }

    private byte GetByte(uint dist) => _out[_outPos - (int)dist];

    /// <summary>
    /// Decodes outSize bytes from the raw LZMA data (no props header) at
    /// input[offset..]. Returns the bytes actually produced; a truncated or
    /// corrupt stream yields fewer bytes and corrupted = true. The output buffer grows with
    /// the bytes produced, so a short stream never allocates the whole requested size.
    /// </summary>
    public static byte[] Decode(byte[] input, int offset, int length, byte propsByte, uint dictSize, int outSize, out bool corrupted)
    {
        if (outSize < 0 || outSize > MaxOutputBytes)
            throw new InvalidDataException("The LZMA output size is outside the decoder limit.");
        var d = new LzmaDecoder
        {
            _in = input,
            _pos = offset,
            _end = offset + length,
            _out = new byte[Math.Min(outSize, InitialOutputBytes)],
            _outLimit = outSize,
            _outPos = 0
        };

        if (propsByte >= 9 * 5 * 5) throw new ArgumentException("Bad LZMA properties byte.");
        var lc = propsByte % 9;
        var rem = propsByte / 9;
        var lp = rem % 5;
        var pb = rem / 5;

        var literalProbs = InitProbs(0x300 << (lc + lp));
        var posSlotDecoder = InitProbs(4 << 6);
        var alignDecoder = InitProbs(1 << NumAlignBits);
        var posDecoders = InitProbs(1 + NumFullDistances - EndPosModelIndex);
        var isMatch = InitProbs(NumStates << NumPosBitsMax);
        var isRep = InitProbs(NumStates);
        var isRepG0 = InitProbs(NumStates);
        var isRepG1 = InitProbs(NumStates);
        var isRepG2 = InitProbs(NumStates);
        var isRep0Long = InitProbs(NumStates << NumPosBitsMax);
        var lenDecoder = new LenDecoder();
        var repLenDecoder = new LenDecoder();

        uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        uint state = 0;
        var pbMask = (1u << pb) - 1;
        var lpMask = (1u << lp) - 1;

        d.RangeInit();

        while (d._outPos < outSize && !d._corrupted)
        {
            var posState = (uint)d._outPos & pbMask;

            if (d.DecodeBit(isMatch, (int)((state << NumPosBitsMax) + posState)) == 0)
            {
                var prevByte = d._outPos > 0 ? d._out[d._outPos - 1] : 0u;
                uint symbol = 1;
                var litState = (((uint)d._outPos & lpMask) << lc) + (prevByte >> (8 - lc));
                var probsOffset = (int)(0x300 * litState);
                if (state >= 7)
                {
                    uint matchByte = d._out[d._outPos - (int)rep0 - 1];
                    do
                    {
                        var matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        var bit = d.DecodeBit(literalProbs, probsOffset + (int)(((1 + matchBit) << 8) + symbol));
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit) break;
                    }
                    while (symbol < 0x100);
                }
                while (symbol < 0x100) symbol = (symbol << 1) | d.DecodeBit(literalProbs, probsOffset + (int)symbol);
                d.PutByte((byte)(symbol - 0x100));
                state = state < 4 ? 0 : (state < 10 ? state - 3 : state - 6);
                continue;
            }

            uint len;
            if (d.DecodeBit(isRep, (int)state) != 0)
            {
                if (d._outPos == 0) { d._corrupted = true; break; }
                if (d.DecodeBit(isRepG0, (int)state) == 0)
                {
                    if (d.DecodeBit(isRep0Long, (int)((state << NumPosBitsMax) + posState)) == 0)
                    {
                        state = state < 7 ? 9u : 11u;
                        d.PutByte(d.GetByte(rep0 + 1));
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (d.DecodeBit(isRepG1, (int)state) == 0)
                    {
                        dist = rep1;
                    }
                    else
                    {
                        if (d.DecodeBit(isRepG2, (int)state) == 0)
                        {
                            dist = rep2;
                        }
                        else
                        {
                            dist = rep3;
                            rep3 = rep2;
                        }
                        rep2 = rep1;
                    }
                    rep1 = rep0;
                    rep0 = dist;
                }
                len = d.DecodeLen(repLenDecoder, (int)posState);
                state = state < 7 ? 8u : 11u;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = d.DecodeLen(lenDecoder, (int)posState);
                state = state < 7 ? 7u : 10u;

                var lenState = len;
                if (lenState > 3) lenState = 3;
                var posSlot = d.BitTreeDecode(posSlotDecoder, (int)lenState << 6, 6);
                uint dist;
                if (posSlot < 4)
                {
                    dist = posSlot;
                }
                else
                {
                    var numDirectBits = (int)((posSlot >> 1) - 1);
                    dist = (2 | (posSlot & 1)) << numDirectBits;
                    if (posSlot < EndPosModelIndex)
                    {
                        dist += d.BitTreeReverseDecode(posDecoders, (int)(dist - posSlot), numDirectBits);
                    }
                    else
                    {
                        dist += d.DecodeDirectBits(numDirectBits - NumAlignBits) << NumAlignBits;
                        dist += d.BitTreeReverseDecode(alignDecoder, 0, NumAlignBits);
                    }
                }
                rep0 = dist;
                if (rep0 == 0xFFFFFFFF) break; // end marker
                if (rep0 >= dictSize || rep0 >= (uint)d._outPos) { d._corrupted = true; break; }
            }

            len += MatchMinLen;
            while (len > 0 && d._outPos < outSize)
            {
                d.PutByte(d.GetByte(rep0 + 1));
                len--;
            }
        }

        corrupted = d._corrupted || d._outPos < outSize;
        if (d._outPos == d._out.Length) return d._out;
        var partial = new byte[d._outPos];
        Array.Copy(d._out, partial, d._outPos);
        return partial;
    }

    /// <summary>
    /// x86 BCJ filter decode (branch-call-jump address conversion), applied
    /// in place to data that was LZMA-decoded from a filtered NSIS stream.
    /// </summary>
    public static void DecodeBcjX86(byte[] data, int size)
    {
        byte[] maskToAllowed = [1, 1, 1, 0, 1, 0, 0, 0];
        byte[] maskToBitNumber = [0, 1, 2, 2, 3, 3, 3, 3];
        if (size < 5) return;
        uint ip = 5;
        var bufferPos = 0;
        var prevPosT = -1;
        uint prevMask = 0;
        for (; ; )
        {
            var p = bufferPos;
            var limit = size - 4;
            while (p < limit && (data[p] & 0xFE) != 0xE8) p++;
            bufferPos = p;
            if (p >= limit) break;
            var gap = bufferPos - prevPosT;
            if (gap > 3)
            {
                prevMask = 0;
            }
            else
            {
                prevMask = (prevMask << (gap - 1)) & 0x7;
                if (prevMask != 0)
                {
                    var b = data[p + 4 - maskToBitNumber[prevMask]];
                    if (maskToAllowed[prevMask] == 0 || b == 0 || b == 0xFF)
                    {
                        prevPosT = bufferPos;
                        prevMask = ((prevMask << 1) & 0x7) | 1;
                        bufferPos++;
                        continue;
                    }
                }
            }
            prevPosT = bufferPos;
            var b4 = data[p + 4];
            if (b4 == 0 || b4 == 0xFF)
            {
                var src = ((uint)data[p + 4] << 24) | ((uint)data[p + 3] << 16) | ((uint)data[p + 2] << 8) | data[p + 1];
                uint dest;
                for (; ; )
                {
                    dest = src - (ip + (uint)bufferPos);
                    if (prevMask == 0) break;
                    var index = maskToBitNumber[prevMask] * 8;
                    var b = (byte)(dest >> (24 - index));
                    if (!(b == 0 || b == 0xFF)) break;
                    src = dest ^ ((1u << (32 - index)) - 1);
                }
                data[p + 4] = (byte)(~(((dest >> 24) & 1) - 1));
                data[p + 3] = (byte)(dest >> 16);
                data[p + 2] = (byte)(dest >> 8);
                data[p + 1] = (byte)dest;
                bufferPos += 5;
            }
            else
            {
                prevMask = ((prevMask << 1) & 0x7) | 1;
                bufferPos++;
            }
        }
    }
}
