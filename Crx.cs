using System.Security.Cryptography;
using neat;

// The signature check of a package from the Chrome Web Store (src/neat/web/crxsig.cs). The packages are made
// here, in the form the store's are (a proof with the developer's RSA key, and one with a second key), with keys
// made for the check, so that what is wrong with a hostile one can be wrong in exactly one way at a time.

internal static partial class Program
{
    private static void CrxChecks()
    {
        Run("a package signed with an RSA key passes, for the id of that key", CrxRsa);
        Run("an ECDSA proof passes, and so do two proofs together", CrxEcdsa);
        Run("a second proof of another key passes when it is good", CrxTwoKeys);
        Run("a changed byte of the archive, anywhere in it, fails the check", CrxFlipped);
        Run("a byte added to the archive fails the check", CrxAppended);
        Run("a package for another id than the one asked for is refused", CrxWrongId);
        Run("a signature made with another key fails", CrxWrongSignature);
        Run("every proof is checked, not only the one that names the id", CrxEveryProof);
        Run("a proof of the wrong kind, or in the wrong form, fails", CrxWrongKind);
        Run("a key that is not a key fails", CrxNotAKey);
        Run("a package with no proof of the key the id comes from is refused", CrxNoProof);
        Run("a header that is cut short, too big or not a header is refused", CrxHeaderSize);
        Run("a header that lies about what is in it is refused", CrxHeaderLies);
        Run("a header with a zip end marker in it is refused", CrxZipMarker);
        Run("a version 2 package is refused, and a file that is not a package is not one", CrxOldAndNot);
        Run("a signed package goes on to be unpacked and read", CrxThenStage);
    }

    // ---- keys and pieces ----

    // Made once: making a key takes a moment.
    private static readonly Lazy<RSA> RsaA = new(() => RSA.Create(2048));
    private static readonly Lazy<RSA> RsaB = new(() => RSA.Create(2048));
    private static readonly Lazy<ECDsa> EcA = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP256));
    private static readonly Lazy<ECDsa> EcB = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP256));

    private sealed record Proof(bool Rsa, byte[] Key, byte[] Sig);

    private static byte[] Varint(ulong v)
    {
        var bytes = new List<byte>();
        while (v >= 0x80)
        {
            bytes.Add((byte)(v | 0x80));
            v >>= 7;
        }

        bytes.Add((byte)v);
        return bytes.ToArray();
    }

    // A length-delimited protobuf field.
    private static byte[] Pbf(int field, byte[] data) => Join(Varint((ulong)((field << 3) | 2)), Varint((ulong)data.Length), data);

    private static string KeyId(byte[] spki) => CrxSig.IdOf(SHA256.HashData(spki).AsSpan(0, 16));

    // crx_id as signed_header_data holds it.
    private static byte[] SignedOf(byte[] spki) => Pbf(1, SHA256.HashData(spki)[..16]);

    // What every proof signs.
    private static byte[] ToSign(byte[] signed, byte[] zip) => Join("CRX3 SignedData\0"u8.ToArray(), Le(signed.Length), signed, zip);

    private static byte[] SignRsa(RSA k, byte[] data) => k.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    private static byte[] SignEc(ECDsa k, byte[] data) => k.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private static Proof RsaProof(RSA k, byte[] data) => new(true, k.ExportSubjectPublicKeyInfo(), SignRsa(k, data));

    private static Proof EcProof(ECDsa k, byte[] data) => new(false, k.ExportSubjectPublicKeyInfo(), SignEc(k, data));

    private static byte[] ProofField(Proof p) => Pbf(p.Rsa ? 2 : 3, Join(Pbf(1, p.Key), Pbf(2, p.Sig)));

    // The header: the proofs, then signed_header_data, as the store's packages have them. Extra goes in between.
    private static byte[] HeaderOf(IEnumerable<Proof> proofs, byte[] signed, params byte[][] extra) =>
        Join(proofs.Select(ProofField).Concat(extra).Append(Pbf(10000, signed)).ToArray());

    private static byte[] Crx3Of(byte[] header, byte[] zip) => Join("Cr24"u8.ToArray(), Le(3), Le(header.Length), header, zip);

    private static readonly byte[] TidyZip = Zip(("manifest.json", Manifest()), ("a.js", "one"), ("b.js", "two two two"));

    // A package the way the store makes it: signed by RsaA (the id comes from it), and by EcA as well when asked.
    private static (byte[] File, string Id) Signed(byte[] zip, bool ec = false)
    {
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var data = ToSign(signed, zip);

        var proofs = new List<Proof> { RsaProof(RsaA.Value, data) };
        if (ec)
            proofs.Add(EcProof(EcA.Value, data));

        return (Crx3Of(HeaderOf(proofs, signed), zip), KeyId(key));
    }

    private static string? CheckBytes(byte[] file, string id) => CrxSig.Check(Save("p.crx", file), id);

    private static void Is(string? got, string? want, string what) =>
        Need(got == want, what + ": expected " + (want ?? "a pass") + ", got " + (got ?? "a pass"));

    private static byte[] Flip(byte[] b, int at)
    {
        var c = (byte[])b.Clone();
        c[at] ^= 0x01;
        return c;
    }

    // ---- the checks ----

    private static Task CrxRsa()
    {
        var (file, id) = Signed(TidyZip);
        Is(CheckBytes(file, id), null, "a good package");
        return Task.CompletedTask;
    }

    private static Task CrxEcdsa()
    {
        // Only an ECDSA proof: the id comes from that key.
        var key = EcA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var only = Crx3Of(HeaderOf(new[] { EcProof(EcA.Value, ToSign(signed, TidyZip)) }, signed), TidyZip);
        Is(CheckBytes(only, KeyId(key)), null, "an ECDSA proof alone");

        // The store's own form: the developer's RSA proof and a second, ECDSA one.
        var (both, id) = Signed(TidyZip, ec: true);
        Is(CheckBytes(both, id), null, "an RSA proof and an ECDSA one");
        return Task.CompletedTask;
    }

    private static Task CrxTwoKeys()
    {
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var data = ToSign(signed, TidyZip);
        var file = Crx3Of(HeaderOf(new[] { RsaProof(RsaA.Value, data), RsaProof(RsaB.Value, data) }, signed), TidyZip);

        Is(CheckBytes(file, KeyId(key)), null, "two RSA proofs");
        return Task.CompletedTask;
    }

    private static Task CrxFlipped()
    {
        var (file, id) = Signed(TidyZip, ec: true);
        var zipAt = file.Length - TidyZip.Length;

        foreach (var at in new[] { zipAt, zipAt + TidyZip.Length / 2, file.Length - 1 })
            Is(CheckBytes(Flip(file, at), id), CrxSig.Bad, "a changed byte at " + (at - zipAt) + " of the archive");

        return Task.CompletedTask;
    }

    private static Task CrxAppended()
    {
        var (file, id) = Signed(TidyZip);
        Is(CheckBytes(Join(file, new byte[] { 0 }), id), CrxSig.Bad, "a byte added at the end");
        return Task.CompletedTask;
    }

    private static Task CrxWrongId()
    {
        var (file, _) = Signed(TidyZip);
        var other = KeyId(RsaB.Value.ExportSubjectPublicKeyInfo());

        Is(CheckBytes(file, other), CrxSig.Mismatch, "another id asked for");
        Is(CheckBytes(file, new string('a', 32)), CrxSig.Mismatch, "an id that is nobody's");
        Is(CheckBytes(file, ""), CrxSig.Mismatch, "no id");
        return Task.CompletedTask;
    }

    private static Task CrxWrongSignature()
    {
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var data = ToSign(signed, TidyZip);

        // The key that the id comes from, a signature that another key made.
        var rsa = new Proof(true, key, SignRsa(RsaB.Value, data));
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { rsa }, signed), TidyZip), KeyId(key)), CrxSig.Bad, "an RSA signature of another key");

        var ecKey = EcA.Value.ExportSubjectPublicKeyInfo();
        var ecSigned = SignedOf(ecKey);
        var ec = new Proof(false, ecKey, SignEc(EcB.Value, ToSign(ecSigned, TidyZip)));
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { ec }, ecSigned), TidyZip), KeyId(ecKey)), CrxSig.Bad, "an ECDSA signature of another key");

        // A signature with one bit changed.
        var good = RsaProof(RsaA.Value, data);
        var bent = new Proof(true, key, Flip(good.Sig, 5));
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { bent }, signed), TidyZip), KeyId(key)), CrxSig.Bad, "a signature with a changed bit");

        // A signature over other data: the right one for another archive.
        var other = RsaProof(RsaA.Value, ToSign(signed, Zip(("manifest.json", Manifest()), ("evil.js", "x"))));
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { other }, signed), TidyZip), KeyId(key)), CrxSig.Bad, "the signature of another archive");
        return Task.CompletedTask;
    }

    private static Task CrxEveryProof()
    {
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var data = ToSign(signed, TidyZip);

        var good = RsaProof(RsaA.Value, data);
        var bad = new Proof(false, EcA.Value.ExportSubjectPublicKeyInfo(), SignEc(EcB.Value, data));

        // The proof that names the id is good; the other one is not, and that is enough.
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { good, bad }, signed), TidyZip), KeyId(key)), CrxSig.Bad, "a bad proof after the good one");
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { bad, good }, signed), TidyZip), KeyId(key)), CrxSig.Bad, "a bad proof before the good one");
        return Task.CompletedTask;
    }

    private static Task CrxWrongKind()
    {
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var data = ToSign(signed, TidyZip);
        var rsa = RsaProof(RsaA.Value, data);

        // An RSA key in the list of ECDSA proofs.
        var wrongList = new Proof(false, rsa.Key, rsa.Sig);
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { wrongList }, signed), TidyZip), KeyId(key)), CrxSig.Bad, "an RSA key in the ECDSA list");

        // An ECDSA key in the list of RSA proofs.
        var ecKey = EcA.Value.ExportSubjectPublicKeyInfo();
        var ecSigned = SignedOf(ecKey);
        var ec = EcProof(EcA.Value, ToSign(ecSigned, TidyZip));
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { ec with { Rsa = true } }, ecSigned), TidyZip), KeyId(ecKey)), CrxSig.Bad, "an ECDSA key in the RSA list");

        // The signature in the other form (r and s side by side, what Windows' own APIs give) is not the one the package uses.
        var p1363 = new Proof(false, ecKey, EcA.Value.SignData(ToSign(ecSigned, TidyZip), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { p1363 }, ecSigned), TidyZip), KeyId(ecKey)), CrxSig.Bad, "an ECDSA signature in the wrong form");
        return Task.CompletedTask;
    }

    private static Task CrxNotAKey()
    {
        // The id is the hash of those bytes, so the proof names the id, and then there is nothing to check it with.
        var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var signed = SignedOf(junk);

        foreach (var rsa in new[] { true, false })
        {
            var file = Crx3Of(HeaderOf(new[] { new Proof(rsa, junk, new byte[] { 9, 9, 9 }) }, signed), TidyZip);
            Is(CheckBytes(file, KeyId(junk)), CrxSig.Bad, "bytes that are not a " + (rsa ? "RSA" : "ECDSA") + " key");
        }

        // A key with something after it: the bytes that follow it are not part of any key.
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var padded = Join(key, new byte[] { 0, 0 });
        var signedPadded = SignedOf(padded);
        var sig = SignRsa(RsaA.Value, ToSign(signedPadded, TidyZip));
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { new Proof(true, padded, sig) }, signedPadded), TidyZip), KeyId(padded)), CrxSig.Bad, "a key with bytes after it");
        return Task.CompletedTask;
    }

    private static Task CrxNoProof()
    {
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var data = ToSign(signed, TidyZip);

        // No proof at all.
        Is(CheckBytes(Crx3Of(HeaderOf(Array.Empty<Proof>(), signed), TidyZip), KeyId(key)), CrxSig.Mismatch, "no proof");

        // A proof that is good, of a key that is not the one the id comes from.
        var other = RsaProof(RsaB.Value, data);
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { other }, signed), TidyZip), KeyId(key)), CrxSig.Mismatch, "a good proof of another key");

        // No signed_header_data: nothing says whose package it is.
        var bare = Join(ProofField(RsaProof(RsaA.Value, data)));
        Is(CheckBytes(Crx3Of(bare, TidyZip), KeyId(key)), CrxSig.Broken, "no signed header data");
        return Task.CompletedTask;
    }

    private static Task CrxHeaderSize()
    {
        var (file, id) = Signed(TidyZip);
        var headerSize = BitConverter.ToInt32(file, 8);

        // The size says more than the file has.
        Is(CheckBytes(Join("Cr24"u8.ToArray(), Le(3), Le(headerSize + 10_000), file[12..]), id), CrxSig.Broken, "a size past the end");

        // Nothing, and far too much.
        Is(CheckBytes(Join("Cr24"u8.ToArray(), Le(3), Le(0), file[12..]), id), CrxSig.Broken, "a size of 0");
        Is(CheckBytes(Join("Cr24"u8.ToArray(), Le(3), Le(int.MaxValue), file[12..]), id), CrxSig.Broken, "a size of 2 GB");
        Is(CheckBytes(Join("Cr24"u8.ToArray(), Le(3), Le(-1), file[12..]), id), CrxSig.Broken, "a size that is negative as a signed number");

        // The file ends in the middle of the header, or right after it (no room for an archive).
        Is(CheckBytes(file[..(12 + headerSize / 2)], id), CrxSig.Broken, "cut in the header");
        Is(CheckBytes(file[..(12 + headerSize)], id), CrxSig.Broken, "no archive after the header");
        Is(CheckBytes(file[..(12 + headerSize + 10)], id), CrxSig.Broken, "an archive too short to be one");

        // Bytes that are not a header.
        Is(CheckBytes(Crx3Of(Enumerable.Repeat((byte)0xFF, 40).ToArray(), TidyZip), id), CrxSig.Broken, "a header of 0xFF");
        Is(CheckBytes(Crx3Of(new byte[40], TidyZip), id), CrxSig.Broken, "a header of zeros");
        Is(CheckBytes(Crx3Of("this is not a protobuf message, it is words"u8.ToArray(), TidyZip), id), CrxSig.Broken, "a header of words");
        return Task.CompletedTask;
    }

    private static Task CrxHeaderLies()
    {
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var data = ToSign(signed, TidyZip);
        var id = KeyId(key);
        var proof = RsaProof(RsaA.Value, data);

        // A field that says it is longer than what is left.
        var lie = Join(Varint((2 << 3) | 2), Varint(5000), new byte[] { 1, 2, 3 });
        Is(CheckBytes(Crx3Of(Join(lie, Pbf(10000, signed)), TidyZip), id), CrxSig.Broken, "a proof longer than the header");

        // A proof without its signature, and one with its key said twice.
        Is(CheckBytes(Crx3Of(HeaderOf(Array.Empty<Proof>(), signed, Pbf(2, Pbf(1, proof.Key))), TidyZip), id), CrxSig.Broken, "a proof with no signature");
        Is(CheckBytes(Crx3Of(HeaderOf(Array.Empty<Proof>(), signed, Pbf(2, Join(Pbf(1, proof.Key), Pbf(1, proof.Key), Pbf(2, proof.Sig)))), TidyZip), id), CrxSig.Broken, "a proof with two keys");

        // signed_header_data twice: it would be unclear which one the signatures are about.
        Is(CheckBytes(Crx3Of(Join(ProofField(proof), Pbf(10000, signed), Pbf(10000, signed)), TidyZip), id), CrxSig.Broken, "signed header data twice");

        // A crx_id that is not 16 bytes, or not there, or twice.
        foreach (var odd in new[] { Pbf(1, new byte[15]), Pbf(1, new byte[17]), Array.Empty<byte>(), Join(signed, signed) })
            Is(CheckBytes(Crx3Of(HeaderOf(new[] { proof }, odd), TidyZip), id), CrxSig.Broken, "a crx_id that is not one");

        // More proofs than any package has.
        var many = Enumerable.Repeat(proof, 17).ToArray();
        Is(CheckBytes(Crx3Of(HeaderOf(many, signed), TidyZip), id), CrxSig.Broken, "17 proofs");

        // A field of a kind that these messages do not use (a 32-bit one, wire type 5), inside a proof.
        var odd5 = Join(Varint((3 << 3) | 5), new byte[] { 1, 2, 3, 4 });
        Is(CheckBytes(Crx3Of(HeaderOf(Array.Empty<Proof>(), signed, Pbf(2, Join(Pbf(1, proof.Key), Pbf(2, proof.Sig), odd5)), Pbf(2, Join(Pbf(1, proof.Key), Pbf(2, proof.Sig), Varint((3 << 3) | 7)))), TidyZip), id), CrxSig.Broken, "a field of a kind that is not known");

        // What a header may have besides: fields that nothing here knows, skipped, with the proofs still good.
        var extra = Join(Varint(7 << 3), Varint(300), Pbf(6, new byte[] { 1, 2, 3 }));
        Is(CheckBytes(Crx3Of(HeaderOf(new[] { proof }, signed, extra), TidyZip), id), null, "fields that are skipped");
        return Task.CompletedTask;
    }

    private static Task CrxZipMarker()
    {
        var key = RsaA.Value.ExportSubjectPublicKeyInfo();
        var signed = SignedOf(key);
        var data = ToSign(signed, TidyZip);
        var id = KeyId(key);

        // Properly signed, and the archive is intact: only the marker in the header is wrong.
        foreach (var marker in new[] { new byte[] { (byte)'P', (byte)'K', 5, 6 }, new byte[] { (byte)'P', (byte)'K', 6, 7 }, new byte[] { (byte)'P', (byte)'K', 6, 6 } })
        {
            var file = Crx3Of(HeaderOf(new[] { RsaProof(RsaA.Value, data) }, signed, Pbf(5, Join(new byte[] { 1 }, marker))), TidyZip);
            Is(CheckBytes(file, id), CrxSig.Broken, "a zip marker " + marker[2] + "," + marker[3] + " in the header");
        }

        return Task.CompletedTask;
    }

    private static Task CrxOldAndNot()
    {
        Is(CheckBytes(Crx2(TidyZip), new string('a', 32)), CrxSig.Old, "a version 2 package");

        var (file, id) = Signed(TidyZip);
        Is(CheckBytes(Join("Cr24"u8.ToArray(), Le(4), file[8..]), id), CrxSig.NotCrx, "version 4");
        Is(CheckBytes(Join("Cr24"u8.ToArray(), Le(0), file[8..]), id), CrxSig.NotCrx, "version 0");
        Is(CheckBytes(TidyZip, id), CrxSig.NotCrx, "a plain zip");
        Is(CheckBytes("Cr2"u8.ToArray(), id), CrxSig.NotCrx, "three bytes");
        Is(CheckBytes(Array.Empty<byte>(), id), CrxSig.NotCrx, "an empty file");
        Is(CheckBytes("<html><body>The store is down</body></html>"u8.ToArray(), id), CrxSig.NotCrx, "a web page");

        // A file that is not there.
        Is(CrxSig.Check(Path.Combine(Temp(), "none.crx"), id), CrxSig.Unreadable, "a missing file");
        return Task.CompletedTask;
    }

    private static Task CrxThenStage()
    {
        // The whole way a store package goes: checked, then unpacked by the unpacker the Extensions page uses.
        var (file, id) = Signed(TidyZip, ec: true);
        var path = Save("tidy.crx", file);
        Is(CrxSig.Check(path, id), null, "the check");

        var store = new ExtStore(Temp());
        var (stage, err) = store.Stage(path);
        Need(stage is not null && err is null, "should unpack: " + err);
        Need(ExtMeta.Read(store.StagePath(stage!)).Meta?.Name == "Tidy", "and be read as an extension");
        Need(File.Exists(Path.Combine(store.StagePath(stage!), "b.js")), "with its files");
        return Task.CompletedTask;
    }
}
