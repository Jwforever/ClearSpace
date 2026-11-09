using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ClearSpace.Core;

internal readonly record struct NtfsRun(long StartVcn, long Clusters, long StartLcn);
internal readonly record struct NtfsName(ulong Parent, string Name);
internal sealed record NtfsRecord(ulong Reference, bool Directory, FileAttributes Attributes, long Length,
    DateTime LastWriteUtc, IReadOnlyList<NtfsName> Names, IReadOnlyList<NtfsRun> DataRuns, long DataLength, bool AttributeList,
    ulong BaseReference = 0, bool HasData = false);
internal sealed record IndexedFile(string Path, long Length, DateTime LastWriteUtc, FileAttributes Attributes, ulong Reference,
    IReadOnlyList<(string Path, FileAttributes Attributes)> Ancestors);
internal sealed record NtfsIndexResult(IReadOnlyList<IndexedFile> Files, int InspectedFiles, bool Canceled);

// Reads the volume and its unnamed $MFT data stream using read-only Windows handles.
// Unsupported layouts fail closed and the caller uses the ordinary directory scanner.
internal sealed class NtfsIndexReader
{
    private const uint GetVolumeData = 0x00090064, GetFileRecord = 0x00090068;
    private const ulong OrdinalMask = 0x0000FFFFFFFFFFFF;
    internal delegate void ReadAt(Span<byte> destination, long offset);

    public static bool CanRead(string root)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) &&
            new DriveInfo(root).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    public NtfsIndexResult Read(string root, long minimumBytes, CancellationToken token, IProgress<LargeFileProgress>? progress)
    {
        using var volume = CreateFile(@"\\.\" + root[..2], 0x80000000, 3, 0, 3, 0, 0);
        if (volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        byte[] geometry = Control(volume, GetVolumeData, [], 1024);
        if (geometry.Length < 96) throw new IOException("NTFS 卷信息不完整。");
        int sectorSize = BinaryPrimitives.ReadInt32LittleEndian(geometry.AsSpan(40));
        int clusterSize = BinaryPrimitives.ReadInt32LittleEndian(geometry.AsSpan(44));
        int recordSize = BinaryPrimitives.ReadInt32LittleEndian(geometry.AsSpan(48));
        long validLength = BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(56));
        ValidateGeometry(sectorSize, clusterSize, recordSize, validLength);
        var mft = ReadRecord(volume, 0, recordSize, sectorSize);
        return ReadIndex(root, minimumBytes, sectorSize, clusterSize, recordSize, validLength, mft,
            (Span<byte> destination, long offset) => ReadExactly(volume, destination, offset), token, progress);
    }

    internal static NtfsIndexResult ReadIndex(string root, long minimumBytes, int sectorSize, int clusterSize, int recordSize,
        long validLength, NtfsRecord mft, ReadAt readAt, CancellationToken token, IProgress<LargeFileProgress>? progress = null)
    {
        ValidateGeometry(sectorSize, clusterSize, recordSize, validLength);
        // Attribute-list continuations can move the runlist into extension records. Never assume a contiguous MFT.
        if (mft.AttributeList || mft.DataRuns.Count == 0) throw new NotSupportedException("MFT 扩展属性布局需要目录扫描。");
        long vcn = 0;
        foreach (var run in mft.DataRuns.OrderBy(r => r.StartVcn))
        {
            if (run.StartVcn != vcn || run.StartLcn < 0) throw new IOException("MFT 数据区不连续或包含稀疏片段。");
            vcn = checked(vcn + run.Clusters);
        }
        if (checked(vcn * clusterSize) < validLength) throw new IOException("MFT 数据映射不完整。");
        var directories = new Dictionary<ulong, NtfsRecord>();
        var candidates = new Dictionary<ulong, NtfsRecord>();
        var extensions = new Dictionary<ulong, List<NtfsRecord>>();
        int bufferSize = Math.Max(1024 * 1024, recordSize);
        // Direct volume reads can require sector-aligned buffers even without FILE_FLAG_NO_BUFFERING.
        byte[] buffer = GC.AllocateArray<byte>(bufferSize + sectorSize, pinned: true);
        int alignment = (int)((sectorSize - Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0).ToInt64() % sectorSize) % sectorSize);
        int inspected = 0, extensionCount = 0;
        var clock = Stopwatch.StartNew();
        progress?.Report(new(0, 0, 0, root, "NTFS MFT 索引扫描"));
        foreach (var run in mft.DataRuns.OrderBy(r => r.StartVcn))
        {
            long logical = checked(run.StartVcn * clusterSize), physical = checked(run.StartLcn * clusterSize);
            long remaining = Math.Min(checked(run.Clusters * clusterSize), validLength - logical);
            while (remaining > 0 && !token.IsCancellationRequested)
            {
                int bytes = (int)Math.Min(bufferSize, remaining);
                // MFT extents and valid data must end on record boundaries for this implementation.
                if (bytes % recordSize != 0) throw new IOException("MFT 记录边界不完整。");
                readAt(buffer.AsSpan(alignment, bytes), physical);
                for (int offset = 0; offset < bytes; offset += recordSize)
                {
                    if (token.IsCancellationRequested) break;
                    ulong ordinal = (ulong)((logical + offset) / recordSize);
                    var record = NtfsRecordParser.Parse(buffer.AsSpan(alignment + offset, recordSize), sectorSize, ordinal, includeExtensions: true);
                    if (record is null) continue;
                    if (record.BaseReference != 0)
                    {
                        if (!extensions.TryGetValue(record.BaseReference, out var pieces)) extensions[record.BaseReference] = pieces = [];
                        pieces.Add(record);
                        if (++extensionCount > 250000) throw new IOException("索引扩展记录达到内存上限。");
                    }
                    else if (record.Directory) directories[record.Reference] = record;
                    else
                    {
                        inspected++;
                        if (record.Length >= minimumBytes || record.AttributeList) candidates[record.Reference] = record;
                    }
                    if (directories.Count > 1000000 || candidates.Count > 250000) throw new IOException("索引达到内存上限。");
                }
                physical += bytes; logical += bytes; remaining -= bytes;
                if (clock.ElapsedMilliseconds >= 150)
                { progress?.Report(new(inspected, candidates.Count, 0, root, "NTFS MFT 索引扫描")); clock.Restart(); }
            }
            if (token.IsCancellationRequested || logical >= validLength) break;
        }
        foreach (var reference in directories.Keys.ToArray())
            if (extensions.TryGetValue(reference, out var pieces)) directories[reference] = Merge(directories[reference], pieces);
        foreach (var reference in candidates.Keys.ToArray())
            if (extensions.TryGetValue(reference, out var pieces)) candidates[reference] = Merge(candidates[reference], pieces);
        var resolved = new List<IndexedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in candidates.Values.Where(r => r.HasData && r.Length >= minimumBytes))
        foreach (var name in record.Names)
        {
            if (!TryResolve(root, name, directories, out string path, out var ancestors) || !seen.Add(path)) continue;
            resolved.Add(new(path, record.Length, record.LastWriteUtc, record.Attributes, record.Reference, ancestors));
            if (resolved.Count > 250000) throw new IOException("索引路径达到内存上限。");
        }
        return new(resolved, inspected, token.IsCancellationRequested);
    }

    private static void ValidateGeometry(int sectorSize, int clusterSize, int recordSize, long validLength)
    {
        static bool PowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;
        if (sectorSize is < 512 or > 4096 || !PowerOfTwo(sectorSize) || clusterSize < sectorSize || clusterSize > 2097152 ||
            !PowerOfTwo(clusterSize) || recordSize is < 512 or > 65536 || !PowerOfTwo(recordSize) || recordSize % sectorSize != 0 ||
            validLength < recordSize || validLength > 8L * 1024 * 1024 * 1024 || validLength % recordSize != 0)
            throw new IOException("暂不支持该 NTFS 索引布局。");
    }

    private static NtfsRecord Merge(NtfsRecord record, IEnumerable<NtfsRecord> pieces)
    {
        var names = record.Names.ToList(); long length = record.Length; bool hasData = record.HasData;
        foreach (var piece in pieces)
        {
            if (piece.BaseReference != record.Reference) throw new IOException("NTFS 扩展记录不匹配。");
            foreach (var name in piece.Names) if (!names.Contains(name)) names.Add(name);
            if (!piece.HasData) continue;
            if (hasData) throw new IOException("NTFS 文件包含重复的数据起始记录。");
            length = piece.Length; hasData = true;
        }
        return record with { Names = names, Length = length, HasData = hasData };
    }

    internal static bool TryResolve(string root, NtfsName name, IReadOnlyDictionary<ulong, NtfsRecord> directories,
        out string path, out IReadOnlyList<(string Path, FileAttributes Attributes)> ancestors)
    {
        path = ""; ancestors = [];
        var chain = new List<NtfsRecord>();
        var visited = new HashSet<ulong>();
        ulong parent = name.Parent;
        while ((parent & OrdinalMask) != 5)
        {
            if (chain.Count >= 256 || !visited.Add(parent) || !directories.TryGetValue(parent, out var record) || !record.Directory || record.Names.Count != 1) return false;
            chain.Add(record); parent = record.Names[0].Parent;
        }
        if (!directories.TryGetValue(parent, out var rootRecord) || !rootRecord.Directory) return false;
        string current = root;
        var locations = new List<(string Path, FileAttributes Attributes)> { (root, rootRecord.Attributes) };
        foreach (var record in chain.AsEnumerable().Reverse())
        { current = Path.Combine(current, record.Names[0].Name); locations.Add((current, record.Attributes)); }
        path = Path.Combine(current, name.Name); ancestors = locations;
        return PathSafety.IsWithin(path, root);
    }

    private static NtfsRecord ReadRecord(SafeFileHandle volume, ulong number, int recordSize, int sectorSize)
    {
        byte[] input = BitConverter.GetBytes(number);
        byte[] output = Control(volume, GetFileRecord, input, recordSize + 32);
        if (output.Length < recordSize + 12 || (BinaryPrimitives.ReadUInt64LittleEndian(output) & OrdinalMask) != number ||
            BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8)) != recordSize) throw new IOException("无法读取 MFT 起始记录。");
        return NtfsRecordParser.Parse(output.AsSpan(12, recordSize), sectorSize, number, allowRestoredFixups: true) ??
            throw new IOException("MFT 起始记录无效。");
    }

    private static byte[] Control(SafeFileHandle handle, uint code, byte[] input, int outputSize)
    {
        byte[] output = new byte[outputSize];
        if (!DeviceIoControl(handle, code, input, input.Length, output, output.Length, out int returned, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return output[..returned];
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> bytes, long offset)
    {
        int completed = 0;
        while (completed < bytes.Length)
        {
            int read = RandomAccess.Read(handle, bytes[completed..], offset + completed);
            if (read == 0) throw new IOException("NTFS 索引读取未完成。");
            completed += read;
        }
    }

    internal static ulong FileReference(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return ((ulong)info.IndexHigh << 32) | info.IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}

internal static class NtfsRecordParser
{
    public static NtfsRecord? Parse(Span<byte> bytes, int sectorSize, ulong ordinal, bool allowRestoredFixups = false, bool includeExtensions = false)
    {
        try { return ParseRecord(bytes, sectorSize, ordinal, allowRestoredFixups, includeExtensions); }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        { throw new IOException("NTFS 记录数值或边界无效。", ex); }
    }

    private static NtfsRecord? ParseRecord(Span<byte> bytes, int sectorSize, ulong ordinal, bool allowRestoredFixups, bool includeExtensions)
    {
        if (bytes.Length < 48 || !bytes[..4].SequenceEqual("FILE"u8)) return null;
        ushort flags = U16(bytes, 22);
        if ((flags & 1) == 0) return null;
        ApplyFixups(bytes, sectorSize, allowRestoredFixups);
        ulong baseReference = U64(bytes, 32);
        if (baseReference != 0 && !includeExtensions) return null;
        int used = checked((int)U32(bytes, 24)), start = U16(bytes, 20);
        if (used > bytes.Length || used < 48 || start < 48 || start > used - 4) throw new IOException("NTFS 属性边界无效。");
        ulong reference = (ordinal & 0x0000FFFFFFFFFFFF) | ((ulong)U16(bytes, 16) << 48);
        var names = new List<NtfsName>(); var runs = new List<NtfsRun>();
        FileAttributes attributes = (flags & 2) != 0 ? FileAttributes.Directory : FileAttributes.Normal;
        DateTime modified = DateTime.UnixEpoch;
        long length = 0, dataLength = 0;
        bool attributeList = false, hasData = false, terminated = false;
        int count = 0;
        for (int offset = start; offset <= used - 4;)
        {
            uint type = U32(bytes, offset);
            if (type == uint.MaxValue) { terminated = true; break; }
            if (++count > 128 || offset > used - 24) throw new IOException("NTFS 属性数量或边界无效。");
            int size = checked((int)U32(bytes, offset + 4));
            if (size < 24 || size % 8 != 0 || size > used - offset) throw new IOException("NTFS 属性长度无效。");
            var attribute = bytes.Slice(offset, size);
            bool resident = attribute[8] == 0;
            if (attribute[8] > 1) throw new IOException("NTFS 属性类型无效。");
            if (attribute[9] != 0 && (U16(attribute, 10) < 24 || U16(attribute, 10) > size - attribute[9] * 2))
                throw new IOException("NTFS 属性名称边界无效。");
            if (type == 0x20) attributeList = true;
            if (resident)
            {
                int valueOffset = U16(attribute, 20), valueLength = checked((int)U32(attribute, 16));
                if (valueOffset < 24 || valueLength > size - valueOffset) throw new IOException("NTFS 驻留数据边界无效。");
                var value = attribute.Slice(valueOffset, valueLength);
                if (type == 0x10)
                {
                    if (value.Length < 36) throw new IOException("NTFS 标准属性不完整。");
                    modified = FileTime(I64(value, 8));
                    attributes |= (FileAttributes)U32(value, 32);
                }
                if (type == 0x30)
                {
                    if (value.Length < 66 || value[65] > 3) throw new IOException("NTFS 文件名属性不完整。");
                    int chars = value[64];
                    if (chars == 0 || chars * 2 > value.Length - 66) throw new IOException("NTFS 文件名长度无效。");
                    string name = Encoding.Unicode.GetString(value.Slice(66, chars * 2));
                    if (value[65] == 2 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0) { offset += size; continue; }
                    var entry = new NtfsName(U64(value, 0), name);
                    if (!names.Contains(entry)) names.Add(entry);
                }
                if (type == 0x80 && attribute[9] == 0)
                {
                    if (hasData) throw new IOException("NTFS 文件包含重复的数据起始属性。");
                    length = valueLength; dataLength = valueLength; hasData = true;
                }
            }
            else if (type == 0x80 && attribute[9] == 0)
            {
                if (size < 64) throw new IOException("NTFS 非驻留属性不完整。");
                long startVcn = I64(attribute, 16);
                long endVcn = I64(attribute, 24);
                if (startVcn < 0 || endVcn < startVcn) throw new IOException("NTFS 数据区范围无效。");
                if (startVcn == 0)
                {
                    if (hasData) throw new IOException("NTFS 文件包含重复的数据起始属性。");
                    length = I64(attribute, 48); dataLength = length; hasData = true;
                }
                // Runlists are needed only for $MFT record 0; ordinary files may be compressed or sparse.
                if (ordinal == 0)
                {
                    int mapping = U16(attribute, 32);
                    if (mapping < 64 || mapping >= size || U16(attribute, 12) != 0) throw new IOException("不支持该 MFT 数据属性。");
                    var decoded = ParseRuns(attribute[mapping..], startVcn);
                    if (decoded.Count == 0 || checked(decoded[^1].StartVcn + decoded[^1].Clusters - 1) != endVcn)
                        throw new IOException("MFT 数据映射范围不匹配。");
                    runs.AddRange(decoded);
                }
            }
            offset += size;
        }
        if (!terminated || length < 0) throw new IOException("NTFS 文件大小或属性结束标记无效。");
        return new(reference, (flags & 2) != 0, attributes, length, modified, names, runs, dataLength, attributeList, baseReference, hasData);
    }

    internal static void ApplyFixups(Span<byte> bytes, int sectorSize, bool allowRestored)
    {
        int offset = U16(bytes, 4), count = U16(bytes, 6);
        if (sectorSize < 512 || bytes.Length % sectorSize != 0 || count != bytes.Length / sectorSize + 1 || offset < 8 || offset + count * 2 > bytes.Length)
            throw new IOException("NTFS 扇区校验信息无效。");
        ushort signature = U16(bytes, offset);
        for (int i = 1; i < count; i++)
        {
            int end = i * sectorSize - 2;
            ushort replacement = U16(bytes, offset + i * 2), found = U16(bytes, end);
            if (found != signature && !(allowRestored && found == replacement)) throw new IOException("NTFS 记录在读取时发生变化。");
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[end..], replacement);
        }
    }

    internal static IReadOnlyList<NtfsRun> ParseRuns(ReadOnlySpan<byte> bytes, long startVcn)
    {
        try { return DecodeRuns(bytes, startVcn); }
        catch (OverflowException ex) { throw new IOException("NTFS 数据映射超出范围。", ex); }
    }

    private static IReadOnlyList<NtfsRun> DecodeRuns(ReadOnlySpan<byte> bytes, long startVcn)
    {
        if (startVcn < 0) throw new IOException("NTFS 逻辑簇位置无效。");
        var result = new List<NtfsRun>(); long vcn = startVcn, lcn = 0;
        for (int i = 0; i < bytes.Length;)
        {
            byte header = bytes[i++];
            if (header == 0) return result;
            int lengthBytes = header & 15, offsetBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || offsetBytes is < 1 or > 8 || i + lengthBytes + offsetBytes > bytes.Length) throw new IOException("NTFS 数据映射无效。");
            ulong clusters = 0;
            for (int n = 0; n < lengthBytes; n++) clusters |= (ulong)bytes[i++] << (n * 8);
            long delta = (sbyte)bytes[i + offsetBytes - 1];
            for (int n = offsetBytes - 2; n >= 0; n--) delta = (delta << 8) | bytes[i + n];
            i += offsetBytes;
            if (clusters == 0 || clusters > long.MaxValue) throw new IOException("NTFS 簇数量无效。");
            lcn = checked(lcn + delta);
            if (lcn < 0) throw new IOException("NTFS 簇位置无效。");
            result.Add(new(vcn, (long)clusters, lcn)); vcn = checked(vcn + (long)clusters);
        }
        throw new IOException("NTFS 数据映射缺少结束标记。");
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
    private static long I64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadInt64LittleEndian(data[offset..]);
    private static DateTime FileTime(long value)
    {
        try { return DateTime.FromFileTimeUtc(value); }
        catch (ArgumentOutOfRangeException) { throw new IOException("NTFS 修改时间无效。"); }
    }
}
