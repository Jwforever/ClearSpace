using System.Buffers.Binary;
using System.Text;
using ClearSpace.Core;

static class NtfsIndexChecks
{
    private const int RecordSize = 1024, SectorSize = 512;
    private static readonly DateTime Modified = new(2026, 10, 1, 10, 20, 30, DateTimeKind.Utc);
    private static ulong Ref(ulong ordinal, ushort sequence = 1) => ordinal | ((ulong)sequence << 48);

    public static void Run(Action<string, Action> check, string sandbox)
    {
        void Assert(bool value, string message = "NTFS index assertion failed") { if (!value) throw new Exception(message); }
        void Reject(Action action)
        {
            try { action(); }
            catch (IOException) { return; }
            throw new Exception("Malformed NTFS input was accepted.");
        }
        check("NTFS parser restores sector fixups and reads Unicode names, sequence numbers, size and UTC time", () =>
        {
            var record = NtfsRecordParser.Parse(Record(12, false, [Name(Ref(8, 7), "视频归档.mp4"), Data(9L << 30)]), SectorSize, 12)!;
            Assert(record.Reference == Ref(12) && record.Names.Single() == new NtfsName(Ref(8, 7), "视频归档.mp4"));
            Assert(record.Length == 9L << 30 && record.LastWriteUtc == Modified && record.HasData && !record.Directory);
        });
        check("NTFS parser distinguishes deleted records, extensions, DOS aliases and hard links", () =>
        {
            byte[] deleted = Record(12, false, [Name(Ref(5), "deleted.bin"), Data(4096)]);
            Put16(deleted, 22, 0);
            Assert(NtfsRecordParser.Parse(deleted, SectorSize, 12) is null);
            byte[] extension = Record(13, false, [Name(Ref(5), "linked.bin")], Ref(12));
            Assert(NtfsRecordParser.Parse((byte[])extension.Clone(), SectorSize, 13) is null);
            Assert(NtfsRecordParser.Parse(extension, SectorSize, 13, includeExtensions: true)!.BaseReference == Ref(12));
            var links = NtfsRecordParser.Parse(Record(12, false,
                [Name(Ref(5), "LONGNA~1.BIN", 2), Name(Ref(5), "long name.bin"), Name(Ref(9), "hard link.bin"), Resident(0x80, [1, 2, 3])]), SectorSize, 12)!;
            Assert(links.Names.Count == 2 && links.Length == 3 && links.Names.All(n => !n.Name.Contains('~')));
        });
        check("NTFS data run decoding supports fragmented extents and signed relative offsets", () =>
        {
            var runs = NtfsRecordParser.ParseRuns([0x11, 3, 100, 0x11, 2, 0xFE, 0x21, 1, 0x2C, 1, 0], 4);
            Assert(runs.SequenceEqual([new NtfsRun(4, 3, 100), new(7, 2, 98), new(9, 1, 398)]));
            var mft = NtfsRecordParser.Parse(Record(0, false, [Data(16384, [0x11, 4, 10, 0])]), SectorSize, 0)!;
            Assert(mft.DataRuns.SequenceEqual([new NtfsRun(0, 4, 10)]));
        });
        check("NTFS parser rejects damaged fixups, invalid lengths, missing terminators and numeric overflow", () =>
        {
            byte[] torn = Record(12, false, [Data(2048)]); Put16(torn, 510, 0xDEAD);
            Reject(() => NtfsRecordParser.Parse(torn, SectorSize, 12));
            byte[] used = Record(12, false, [Data(2048)]); Put32(used, 24, uint.MaxValue);
            Reject(() => NtfsRecordParser.Parse(used, SectorSize, 12));
            byte[] size = Record(12, false, [Data(2048)]); Put32(size, 60, uint.MaxValue);
            Reject(() => NtfsRecordParser.Parse(size, SectorSize, 12));
            byte[] end = Record(12, false, [Data(2048)]); Put32(end, (int)Read32(end, 24) - 4, 0);
            Reject(() => NtfsRecordParser.Parse(end, SectorSize, 12));
            Reject(() => NtfsRecordParser.Parse(Record(12, false, [Data(-1)]), SectorSize, 12));
            Reject(() => NtfsRecordParser.ParseRuns([0x11, 1], 0));
            Reject(() => NtfsRecordParser.ParseRuns([0x01, 1, 0], 0)); // sparse $MFT
            Reject(() => NtfsRecordParser.ParseRuns([0x11, 1, 0xFF, 0], 0));
            Reject(() => NtfsRecordParser.ParseRuns([0x11, 2, 1, 0], long.MaxValue));
        });
        check("NTFS parser handles malformed records without leaking span or arithmetic exceptions", () =>
        {
            var random = new Random(824);
            for (int iteration = 0; iteration < 2000; iteration++)
            {
                byte[] bytes = Record(12, false, [Name(Ref(5), "fixture.bin"), Data(2048)]);
                for (int i = 0; i < 4; i++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                try { NtfsRecordParser.Parse(bytes, SectorSize, 12); }
                catch (IOException) { }
            }
        });
        check("NTFS path reconstruction rejects stale parents, directory cycles and ambiguous ancestry", () =>
        {
            var directories = new Dictionary<ulong, NtfsRecord>
            {
                [Ref(5)] = Parse(Record(5, true, []), 5),
                [Ref(8)] = Parse(Record(8, true, [Name(Ref(5), "资料")]), 8),
                [Ref(9)] = Parse(Record(9, true, [Name(Ref(8), "视频")], attributesValue: FileAttributes.ReparsePoint), 9)
            };
            Assert(NtfsIndexReader.TryResolve(@"C:\", new(Ref(9), "movie.mp4"), directories, out string path, out var ancestors));
            Assert(path == @"C:\资料\视频\movie.mp4" && ancestors.Count == 3 && ancestors[^1].Attributes.HasFlag(FileAttributes.ReparsePoint));
            Assert(!NtfsIndexReader.TryResolve(@"C:\", new(Ref(9, 2), "movie.mp4"), directories, out _, out _));
            var policy = new LargeFilePolicy([]);
            Assert(ancestors.Any(a => !policy.DirectoryMetadataAllowed(a.Path, a.Attributes, a.Path == @"C:\")));
            directories[Ref(8)] = directories[Ref(8)] with { Names = [new(Ref(9), "cycle")] };
            Assert(!NtfsIndexReader.TryResolve(@"C:\", new(Ref(9), "movie.mp4"), directories, out _, out _));
            directories[Ref(8)] = directories[Ref(8)] with { Names = [new(Ref(5), "one"), new(Ref(5), "two")] };
            Assert(!NtfsIndexReader.TryResolve(@"C:\", new(Ref(9), "movie.mp4"), directories, out _, out _));
        });
        check("NTFS index reads fragmented volume images and merges file attribute-list extensions", () =>
        {
            var fixture = Image();
            var result = NtfsIndexReader.ReadIndex(@"C:\", 1024, SectorSize, 4096, RecordSize, 16 * RecordSize,
                fixture.Mft, fixture.Read, CancellationToken.None);
            Assert(!result.Canceled && result.InspectedFiles == 3, "Expected the MFT itself plus two active base file records.");
            Assert(result.Files.Count == 3 && result.Files.All(f => f.Length == 8192 && f.Reference == Ref(10)));
            Assert(result.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(
                [@"C:\资料\视频.mp4", @"C:\资料\别名.mp4", @"C:\资料\扩展名.mp4"]));
        });
        check("NTFS index cancellation yields partial state and unsupported MFT layouts request fallback", () =>
        {
            var fixture = Image();
            using var cts = new CancellationTokenSource();
            var canceled = NtfsIndexReader.ReadIndex(@"C:\", 1024, SectorSize, 4096, RecordSize, 16 * RecordSize,
                fixture.Mft, fixture.Read, cts.Token, new ImmediateProgress(_ => cts.Cancel()));
            Assert(canceled.Canceled && canceled.Files.Count == 0);
            try
            {
                NtfsIndexReader.ReadIndex(@"C:\", 1024, SectorSize, 4096, RecordSize, 16 * RecordSize,
                    fixture.Mft with { AttributeList = true }, fixture.Read, CancellationToken.None);
                throw new Exception("Unsupported MFT continuation was accepted.");
            }
            catch (NotSupportedException) { }
            Reject(() => NtfsIndexReader.ReadIndex(@"C:\", 1024, SectorSize, 4096, RecordSize, 16 * RecordSize,
                fixture.Mft with { DataRuns = [new(1, 4, 2)] }, fixture.Read, CancellationToken.None));
        });
        check("Indexed cleanup verifies the NTFS file identity before calling the recycle bin", () =>
        {
            string root = Path.Combine(sandbox, "index-identity"); Directory.CreateDirectory(root);
            string path = Path.Combine(root, "fixture.bin"); File.WriteAllBytes(path, new byte[2048]);
            var file = new FileInfo(path);
            ulong reference;
            using (var stream = File.OpenRead(path)) reference = NtfsIndexReader.FileReference(stream.SafeFileHandle);
            Assert(reference != 0);
            var changed = new LargeFileCandidate { Path = path, Length = file.Length, LastWriteUtc = file.LastWriteTimeUtc, FileReferenceNumber = reference ^ 1 };
            var matching = new LargeFileCandidate { Path = path, Length = file.Length, LastWriteUtc = file.LastWriteTimeUtc, FileReferenceNumber = reference };
            var bin = new RecordingBin(); var service = new LargeFileService(new LargeFilePolicy([]), bin);
            LargeFileScan Scan(LargeFileCandidate candidate) => new(root, 1024, [candidate], 1, 1, 0, false, false, []);
            Assert(service.Execute(Scan(changed), [changed], CancellationToken.None) is { Succeeded: 0, Skipped: 1 } && bin.Paths.Count == 0);
            Assert(service.Execute(Scan(matching), [matching], CancellationToken.None).Succeeded == 1 && bin.Paths.SequenceEqual([path]));
            Assert(File.Exists(path), "Identity checks must use only the fake recycle bin.");
        });
    }

    private static NtfsRecord Parse(byte[] bytes, ulong ordinal) => NtfsRecordParser.Parse(bytes, SectorSize, ordinal)!;
    private static (NtfsRecord Mft, NtfsIndexReader.ReadAt Read) Image()
    {
        // Sixteen records split into two noncontiguous physical extents.
        byte[] image = new byte[32 * 4096];
        byte[] mftBytes = Record(0, false, [Data(16 * RecordSize, [0x11, 2, 2, 0x11, 2, 18, 0])]);
        void Place(int ordinal, byte[] record)
        {
            int logical = ordinal * RecordSize;
            int physical = logical < 8192 ? 8192 + logical : 20 * 4096 + logical - 8192;
            record.CopyTo(image, physical);
        }
        Place(0, mftBytes); Place(5, Record(5, true, [])); Place(8, Record(8, true, [Name(Ref(5), "资料")]));
        Place(10, Record(10, false, [Name(Ref(8), "视频.mp4"), Name(Ref(8), "别名.mp4"), Resident(0x20, new byte[8])]));
        Place(11, Record(11, false, [Name(Ref(8), "扩展名.mp4"), Data(8192)], Ref(10)));
        Place(12, Record(12, false, [Name(Ref(8), "small.bin"), Data(10)]));
        return (Parse(mftBytes, 0), (Span<byte> destination, long offset) => image.AsSpan(checked((int)offset), destination.Length).CopyTo(destination));
    }

    private static byte[] Record(ulong ordinal, bool directory, byte[][] attributes, ulong baseReference = 0, FileAttributes attributesValue = FileAttributes.Normal)
    {
        byte[] bytes = new byte[RecordSize]; "FILE"u8.CopyTo(bytes);
        Put16(bytes, 4, 48); Put16(bytes, 6, 3); Put16(bytes, 16, 1); Put16(bytes, 20, 56);
        Put16(bytes, 22, (ushort)(directory ? 3 : 1)); Put32(bytes, 28, RecordSize); Put64(bytes, 32, baseReference);
        byte[] standard = new byte[72]; Put64(standard, 8, (ulong)Modified.ToFileTimeUtc());
        Put32(standard, 32, (uint)attributesValue);
        int offset = 56;
        foreach (byte[] attribute in new[] { Resident(0x10, standard) }.Concat(attributes))
        { attribute.CopyTo(bytes, offset); offset += attribute.Length; }
        Put32(bytes, offset, uint.MaxValue); Put32(bytes, 24, (uint)(offset + 4));
        Put16(bytes, 48, 0xA55A);
        for (int i = 1; i <= 2; i++)
        { Put16(bytes, 48 + i * 2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * SectorSize - 2))); Put16(bytes, i * SectorSize - 2, 0xA55A); }
        return bytes;
    }
    private static byte[] Name(ulong parent, string name, byte nameSpace = 1)
    {
        byte[] value = new byte[66 + name.Length * 2]; Put64(value, 0, parent);
        value[64] = (byte)name.Length; value[65] = nameSpace; Encoding.Unicode.GetBytes(name).CopyTo(value, 66);
        return Resident(0x30, value);
    }
    private static byte[] Resident(uint type, byte[] value)
    {
        byte[] result = new byte[(24 + value.Length + 7) & ~7]; Put32(result, 0, type); Put32(result, 4, (uint)result.Length);
        Put32(result, 16, (uint)value.Length); Put16(result, 20, 24); value.CopyTo(result, 24); return result;
    }
    private static byte[] Data(long length, byte[]? runs = null)
    {
        runs ??= [0x11, 1, 10, 0];
        byte[] result = new byte[(64 + runs.Length + 7) & ~7]; Put32(result, 0, 0x80); Put32(result, 4, (uint)result.Length);
        result[8] = 1; Put64(result, 24, (ulong)Math.Max(0, NtfsRecordParser.ParseRuns(runs, 0).Sum(r => r.Clusters) - 1));
        Put16(result, 32, 64); Put64(result, 48, unchecked((ulong)length)); runs.CopyTo(result, 64); return result;
    }
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static uint Read32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private sealed class ImmediateProgress(Action<LargeFileProgress> action) : IProgress<LargeFileProgress>
    {
        public void Report(LargeFileProgress value) => action(value);
    }
}
