using System.Security.Cryptography;
using System.Text;

namespace Athena.Net.MapServer.World;

// Shared mechanical header shape for this project's "versioned, deterministic, content-hashed binary pack generated
// from pinned rAthena data" artifacts (currently ItemPackFormat; a future monster pack would use this identically).
// Layout: magic(4) | version u16(2) | commit[40] (ASCII hex) | SHA-256 of body(32) | body. Purely mechanical -
// magic/version/commit/hash framing is domain-blind by construction, unlike each pack's own body layout (item rows,
// script table, groups, or whatever a different domain's body needs), which stays entirely domain-specific and is
// never touched by this type. AthenaMapPackFormat predates this helper and keeps its own different, index-based
// header shape (multiple named sub-payloads bundled in one file) - deliberately NOT folded into this type, since
// forcing one shared shape over both would either need this header to carry an index concept pure flat-array packs
// (items, and any future mob pack) have no use for, or stay so thin it would not actually remove the real
// duplication this type targets. See ai/ or the Feature 16 execution plan for the "standardize mechanisms, not
// domain behavior" rationale.
public static class GeneratedPackHeader
{
    public const int CommitLength = 40;
    public const int HashLength = 32;

    public static int Length(int magicLength) => magicLength + 2 + CommitLength + HashLength;

    // Writes magic|version|commit|hash(body) to `output`, then `body` itself, matching the exact byte layout every
    // caller of this helper already wrote by hand (little-endian version via BitConverter, ASCII commit, SHA-256).
    public static void Write(Stream output, ReadOnlySpan<byte> magic, ushort version, string commit, byte[] body)
    {
        if (commit.Length != CommitLength) throw new ArgumentException($"The pinned rAthena commit must be {CommitLength} hex characters.", nameof(commit));
        output.Write(magic);
        output.Write(BitConverter.GetBytes(version));
        output.Write(Encoding.ASCII.GetBytes(commit));
        output.Write(SHA256.HashData(body));
        output.Write(body);
    }

    public readonly ref struct Header(string commit, ReadOnlySpan<byte> body)
    {
        public string Commit { get; } = commit;
        public ReadOnlySpan<byte> Body { get; } = body;
        public void Deconstruct(out string commit, out ReadOnlySpan<byte> body) { commit = Commit; body = Body; }
    }

    // Validates magic, version and content hash, then returns the commit string and the body span - exactly what
    // every caller needs next to decode its own domain-specific sections. `what` names the pack in error messages
    // (e.g. "item data pack") so a validation failure reads the same way callers' existing hand-written checks did.
    public static Header ReadAndVerify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> magic, ushort expectedVersion, string what)
    {
        var headerLength = Length(magic.Length);
        if (data.Length < headerLength || !data[..magic.Length].SequenceEqual(magic)) throw new InvalidDataException($"Not a {what}.");
        var afterMagic = magic.Length;
        if (BitConverter.ToUInt16(data[afterMagic..(afterMagic + 2)]) != expectedVersion) throw new InvalidDataException($"Unsupported {what} version.");
        var commitStart = afterMagic + 2;
        var commit = Encoding.ASCII.GetString(data[commitStart..(commitStart + CommitLength)]);
        var hashStart = commitStart + CommitLength;
        var body = data[headerLength..];
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(data[hashStart..(hashStart + HashLength)])) throw new InvalidDataException($"{what} content hash mismatch.");
        return new Header(commit, body);
    }
}
