using System.Buffers.Binary;
using System.Security.Cryptography;

namespace neat;

/// <summary>
/// Checks the signatures of a Chrome extension package (.crx, format version 3) that was downloaded
/// from the store. Nothing here depends on WinUI or WebView2, so tests/ext.check runs it as it is.
///
/// What a CRX3 file is (components/crx_file/crx3.proto and crx_verifier.cc in Chromium):
///   "Cr24", the format version (3) and the size N of the header, each 4 bytes little-endian; then the
///   N bytes of the header, a protobuf message (CrxFileHeader); then the ZIP archive. The header holds
///   proofs, each a public key (a DER SubjectPublicKeyInfo) and a signature: RSA ones in field 2,
///   ECDSA (P-256) ones in field 3. It also holds signed_header_data (field 10000), a small protobuf
///   message (SignedData) whose crx_id (field 1) is 16 bytes. Every signature covers the same bytes:
///   the text "CRX3 SignedData" and one 0 byte, then the size of signed_header_data (4 bytes
///   little-endian), signed_header_data itself, and then the whole ZIP archive.
///   An extension's id is the first 16 bytes of the SHA-256 of its public key, written with each
///   hex digit 0 to f as a letter a to p.
///
/// What this check does, as Chromium's verifier does:
///   - It verifies the signature of EVERY proof, RSA with PKCS#1 v1.5 and SHA-256, ECDSA with SHA-256
///     (the signature as a DER sequence). The hash is made once, a piece at a time, from the file, so a
///     big package is never held in memory.
///   - It requires at least one proof whose key hashes to crx_id, and that crx_id is the id that was asked for.
///   - It refuses a header that holds a ZIP end-of-archive marker, which could confuse the unzipping
///     (Chromium refuses it too), and every header or field that is too big, cut short or does not add up.
///   - Version 2 is refused: it is no longer used by the store.
///
/// What it does NOT prove, and must not be taken for: the package that comes from the Chrome Web Store also
/// carries a proof made with Google's own publisher key. It is checked for validity like every other proof,
/// but NEAT pins no Google key, so its presence is not required and not recognised. What this check
/// proves is that the package is intact and belongs to the id that was asked for (the developer's key made
/// that id); it does not prove that the store is honest, and anyone who can serve a package with a key
/// of their own can only ever serve it under the id of that key.
/// </summary>
internal static class CrxSig
{
    public const string Old = "This package format is no longer supported by the store.";
    public const string Mismatch = "The download does not match the extension you asked for.";
    public const string Broken = "The download is damaged.";
    public const string Bad = "The download failed its signature check.";
    public const string NotCrx = "That download is not an extension package.";
    public const string Unreadable = "The download could not be read.";

    // A header is a few hundred bytes (a key and a signature or two). Anything past this is not one.
    private const int MaxHeader = 1024 * 1024;

    // A key or a signature (an RSA 4096 key is about 550 bytes); and how many proofs there may be.
    private const int MaxField = 16 * 1024;
    private const int MaxProofs = 16;

    // "CRX3 SignedData" and a 0 byte.
    private static readonly byte[] Context = { (byte)'C', (byte)'R', (byte)'X', (byte)'3', (byte)' ', (byte)'S', (byte)'i', (byte)'g', (byte)'n', (byte)'e', (byte)'d', (byte)'D', (byte)'a', (byte)'t', (byte)'a', 0 };

    // The ZIP markers that must not be in a header: end of archive, its 64-bit locator and its 64-bit record.
    private static readonly byte[][] ZipEnds =
    {
        new byte[] { (byte)'P', (byte)'K', 5, 6 },
        new byte[] { (byte)'P', (byte)'K', 6, 7 },
        new byte[] { (byte)'P', (byte)'K', 6, 6 },
    };

    private sealed class Proof
    {
        public byte[] Key = Array.Empty<byte>();
        public byte[] Sig = Array.Empty<byte>();
        public bool Rsa;
    }

    /// <summary>
    /// Checks a downloaded package against the id that was asked for. Returns null when it passes, or a
    /// sentence for the person. Reads the file only; never throws for a bad file. Slow for a big package,
    /// so call it from a pool thread.
    /// </summary>
    public static string? Check(string path, string wanted)
    {
        try
        {
            return Run(path, wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unreadable;
        }
    }

    /// <summary>An id written as letters, from its 16 bytes (the start of the SHA-256 of a key).</summary>
    internal static string IdOf(ReadOnlySpan<byte> sixteen)
    {
        var chars = new char[sixteen.Length * 2];
        for (var i = 0; i < sixteen.Length; i++)
        {
            chars[2 * i] = (char)('a' + (sixteen[i] >> 4));
            chars[2 * i + 1] = (char)('a' + (sixteen[i] & 15));
        }

        return new string(chars);
    }

    private static string? Run(string path, string wanted)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var head = new byte[12];
        if (!Fill(file, head) || head[0] != 'C' || head[1] != 'r' || head[2] != '2' || head[3] != '4')
            return NotCrx;

        var version = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        if (version == 2)
            return Old;

        if (version != 3)
            return NotCrx;

        // The header must fit in the file with room for the smallest possible zip (22 bytes) after it.
        var size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        if (size == 0 || size > MaxHeader || 12L + size > file.Length - 22)
            return Broken;

        var header = new byte[size];
        if (!Fill(file, header))
            return Broken;

        foreach (var mark in ZipEnds)
        {
            if (header.AsSpan().IndexOf(mark) >= 0)
                return Broken;
        }

        var proofs = new List<Proof>();
        var err = Parse(header, proofs, out var signed);
        if (err is not null)
            return err;

        if (signed is null)
            return Broken;

        var crx = ReadId(signed);
        if (crx is null)
            return Broken;

        // Cheap questions first, before the archive is read: is this the extension that was asked for, and
        // is there a proof made with the key that id comes from?
        if (IdOf(crx) != wanted || !proofs.Any(p => SHA256.HashData(p.Key).AsSpan(0, 16).SequenceEqual(crx)))
            return Mismatch;

        // The file is now at the first byte of the archive. One hash serves every proof: they all cover the same bytes.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Context);

        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)signed.Length);
        hash.AppendData(length);
        hash.AppendData(signed);

        var buf = new byte[81920];
        int n;
        while ((n = file.Read(buf, 0, buf.Length)) > 0)
            hash.AppendData(buf, 0, n);

        var digest = hash.GetHashAndReset();

        foreach (var p in proofs)
        {
            if (!Valid(p, digest))
                return Bad;
        }

        return null;
    }

    private static bool Valid(Proof p, byte[] digest)
    {
        try
        {
            if (p.Rsa)
            {
                using var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(p.Key, out var used);
                return used == p.Key.Length && rsa.VerifyHash(digest, p.Sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(p.Key, out var read);
            return read == p.Key.Length && ec.VerifyHash(digest, p.Sig, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or NotSupportedException)
        {
            // A key of the wrong kind for its list, or one that is not a key at all.
            return false;
        }
    }

    private static bool Fill(Stream s, byte[] into)
    {
        var have = 0;
        while (have < into.Length)
        {
            var n = s.Read(into, have, into.Length - have);
            if (n <= 0)
                return false;

            have += n;
        }

        return true;
    }

    // ---- the protobuf messages ----

    // CrxFileHeader: the proofs (fields 2 and 3) and signed_header_data (10000); anything else is skipped.
    private static string? Parse(ReadOnlySpan<byte> header, List<Proof> proofs, out byte[]? signed)
    {
        signed = null;
        var pb = new Pb(header);

        while (!pb.Done)
        {
            if (!pb.Tag(out var field, out var wire))
                return Broken;

            switch (field)
            {
                case 2:
                case 3:
                    if (wire != 2 || !pb.Bytes(out var inner) || proofs.Count >= MaxProofs)
                        return Broken;

                    var proof = ReadProof(inner);
                    if (proof is null)
                        return Broken;

                    proof.Rsa = field == 2;
                    proofs.Add(proof);
                    break;

                case 10000:
                    // Said twice, it would be unclear which one the signatures are about.
                    if (wire != 2 || signed is not null || !pb.Bytes(out var data) || data.Length > MaxField)
                        return Broken;

                    signed = data.ToArray();
                    break;

                default:
                    if (!pb.Skip(wire))
                        return Broken;

                    break;
            }
        }

        return null;
    }

    // AsymmetricKeyProof: public_key (1) and signature (2), each once and neither empty.
    private static Proof? ReadProof(ReadOnlySpan<byte> bytes)
    {
        byte[]? key = null;
        byte[]? sig = null;
        var pb = new Pb(bytes);

        while (!pb.Done)
        {
            if (!pb.Tag(out var field, out var wire))
                return null;

            if (field is 1 or 2)
            {
                if (wire != 2 || !pb.Bytes(out var data) || data.Length == 0 || data.Length > MaxField)
                    return null;

                if (field == 1 && key is null)
                    key = data.ToArray();
                else if (field == 2 && sig is null)
                    sig = data.ToArray();
                else
                    return null;
            }
            else if (!pb.Skip(wire))
            {
                return null;
            }
        }

        return key is null || sig is null ? null : new Proof { Key = key, Sig = sig };
    }

    // SignedData: crx_id (1), exactly 16 bytes, once.
    private static byte[]? ReadId(ReadOnlySpan<byte> bytes)
    {
        byte[]? id = null;
        var pb = new Pb(bytes);

        while (!pb.Done)
        {
            if (!pb.Tag(out var field, out var wire))
                return null;

            if (field == 1)
            {
                if (wire != 2 || id is not null || !pb.Bytes(out var data) || data.Length != 16)
                    return null;

                id = data.ToArray();
            }
            else if (!pb.Skip(wire))
            {
                return null;
            }
        }

        return id;
    }

    /// <summary>
    /// The least of a protobuf reader: varints and length-delimited fields, which is all these messages
    /// use. Every read is checked against the end of the data, and any other wire type is refused.
    /// </summary>
    private ref struct Pb
    {
        private readonly ReadOnlySpan<byte> _b;
        private int _at;

        public Pb(ReadOnlySpan<byte> b)
        {
            _b = b;
            _at = 0;
        }

        public bool Done => _at >= _b.Length;

        // A varint is at most 10 bytes, and the last may only use the one bit left of 64.
        public bool Varint(out ulong v)
        {
            v = 0;

            for (var shift = 0; shift <= 63; shift += 7)
            {
                if (_at >= _b.Length)
                    return false;

                var x = _b[_at++];
                if (shift == 63 && x > 1)
                    return false;

                v |= (ulong)(x & 0x7F) << shift;
                if ((x & 0x80) == 0)
                    return true;
            }

            return false;
        }

        // The number and the wire type of the next field.
        public bool Tag(out int field, out int wire)
        {
            field = 0;
            wire = 0;

            if (!Varint(out var tag))
                return false;

            var number = tag >> 3;
            if (number == 0 || number > 536870911)
                return false;

            field = (int)number;
            wire = (int)(tag & 7);
            return true;
        }

        public bool Bytes(out ReadOnlySpan<byte> data)
        {
            data = default;

            if (!Varint(out var len) || len > (ulong)(_b.Length - _at))
                return false;

            data = _b.Slice(_at, (int)len);
            _at += (int)len;
            return true;
        }

        // 0 is a varint, 2 is length-delimited. Nothing else is read.
        public bool Skip(int wire)
        {
            if (wire == 0)
                return Varint(out _);

            return wire == 2 && Bytes(out _);
        }
    }
}
