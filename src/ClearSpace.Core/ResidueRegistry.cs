using Microsoft.Win32;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ClearSpace.Core;

public sealed record RegistrySnapshot(RegistryAddress Address, string Fingerprint, string ExportText, int ValueCount);

public interface IResidueRegistry
{
    RegistrySnapshot? Read(RegistryAddress address);
    void DeleteVerified(RegistryAddress address, string fingerprint);
}

public sealed class WindowsResidueRegistry : IResidueRegistry
{
    public RegistrySnapshot? Read(RegistryAddress address)
    {
        using var root = RegistryKey.OpenBaseKey(address.Hive, address.View);
        using var key = root.OpenSubKey(address.Key);
        if (key is null) return null;
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
        int count = 0, keys = 0;
        var stamp = new StringBuilder();
        ReadTree(key, address.FullName, text, stamp, ref count, ref keys, 0);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString() + stamp)));
        return new(address, fingerprint, text.ToString(), count);
    }

    private static void ReadTree(RegistryKey key, string fullName, StringBuilder text, StringBuilder stamp, ref int values, ref int keys, int depth)
    {
        if (++keys > 1000 || depth > 20 || text.Length > 4_000_000) throw new IOException("注册表项过大，已跳过。");
        if (RegQueryInfoKey(key.Handle, null, 0, 0, out _, out _, 0, out _, out _, out _, out _, out long lastWrite) != 0)
            throw new IOException("无法核对注册表项修改时间。");
        stamp.Append(fullName).Append(':').Append(lastWrite).Append(';');
        if (fullName.Contains(']') || fullName.Contains('\r') || fullName.Contains('\n')) throw new IOException("注册表项路径无法安全导出。");
        text.Append('[').Append(fullName).Append("]\r\n");
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            if (++values > 10000) throw new IOException("注册表值过多，已跳过。");
            var kind = key.GetValueKind(name);
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string label = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
            text.Append(label).Append('=');
            switch (kind)
            {
                case RegistryValueKind.String when value is string s:
                    // hex(1) handles embedded CR/LF and NUL without changing the value on restore.
                    AppendHex(text, "hex(1):", Encoding.Unicode.GetBytes(s + '\0')); break;
                case RegistryValueKind.ExpandString when value is string s:
                    AppendHex(text, "hex(2):", Encoding.Unicode.GetBytes(s + '\0')); break;
                case RegistryValueKind.MultiString when value is string[] array:
                    AppendHex(text, "hex(7):", Encoding.Unicode.GetBytes(string.Join('\0', array) + "\0\0")); break;
                case RegistryValueKind.DWord when value is int number:
                    text.Append("dword:").Append(unchecked((uint)number).ToString("x8", CultureInfo.InvariantCulture)); break;
                case RegistryValueKind.QWord when value is long number:
                    AppendHex(text, "hex(b):", BitConverter.GetBytes(number)); break;
                case RegistryValueKind.Binary when value is byte[] bytes: AppendHex(text, "hex:", bytes); break;
                case RegistryValueKind.None when value is byte[] bytes: AppendHex(text, "hex(0):", bytes); break;
                default: throw new IOException("无法完整备份该类型注册表值，已跳过。");
            }
            text.Append("\r\n");
        }
        text.Append("\r\n");
        foreach (string child in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            using var sub = key.OpenSubKey(child) ?? throw new IOException("注册表项在读取时发生变化。");
            ReadTree(sub, fullName + "\\" + child, text, stamp, ref values, ref keys, depth + 1);
        }
    }

    private static string Escape(string text)
    {
        if (text.Contains('\r') || text.Contains('\n') || text.Contains('\0')) throw new IOException("注册表值名称无法安全导出。");
        return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static void AppendHex(StringBuilder output, string prefix, byte[] bytes)
    {
        if (bytes.Length > 1_000_000) throw new IOException("注册表值过大，已跳过。");
        output.Append(prefix);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) output.Append(i % 24 == 0 ? ",\\\r\n  " : ",");
            output.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }
    }

    public void DeleteVerified(RegistryAddress address, string fingerprint)
    {
        var current = Read(address);
        if (current is null || current.Fingerprint != fingerprint) throw new IOException("注册表项在扫描后发生变化，请重新扫描。");
        using var root = RegistryKey.OpenBaseKey(address.Hive, address.View);
        root.DeleteSubKeyTree(address.Key, throwOnMissingSubKey: true);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, StringBuilder? keyClass,
        nint classLength, nint reserved, out uint subKeys, out uint maxSubKeyLength, nint maxClassLength,
        out uint values, out uint maxValueName, out uint maxValueLength, out uint securitySize, out long lastWrite);
}
