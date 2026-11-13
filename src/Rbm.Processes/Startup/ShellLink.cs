using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Rbm.Processes;

/// <summary>Destino de um atalho (.lnk).</summary>
public sealed record ShellLinkTarget(string? TargetPath, string? Arguments, string? WorkingDirectory, string Source);

/// <summary>
/// Resolve atalhos .lnk: IShellLinkW (COM) primeiro; se falhar, interpreta o formato binário MS-SHLLINK
/// (LinkInfo.LocalBasePath + StringData). Atalhos "anunciados" (MSI) podem não ter caminho resolvível.
/// </summary>
public static class ShellLinkResolver
{
    public static ShellLinkTarget? Resolve(string lnkPath)
    {
        var com = TryCom(lnkPath);
        if (com is { TargetPath.Length: > 0 }) return com;
        try
        {
            var parsed = LnkParser.Parse(File.ReadAllBytes(lnkPath));
            if (parsed is { TargetPath.Length: > 0 }) return parsed;
            return com ?? parsed;
        }
        catch { return com; }
    }

    private static ShellLinkTarget? TryCom(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new ShellLinkCoClass();
            ((IPersistFile)obj).Load(lnkPath, 0 /* STGM_READ */);
            var link = (IShellLinkW)obj;
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, 0, 0);
            string target = sb.ToString();
            sb.Clear();
            link.GetArguments(sb, sb.Capacity);
            string args = sb.ToString();
            sb.Clear();
            link.GetWorkingDirectory(sb, sb.Capacity);
            return new ShellLinkTarget(target.Length > 0 ? target : null, args, sb.Length > 0 ? sb.ToString() : null, "IShellLink");
        }
        catch { return null; }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }
}

/// <summary>Leitor mínimo e defensivo do formato MS-SHLLINK (somente leitura, sem COM). Puro/testável.</summary>
public static class LnkParser
{
    private const uint HasLinkTargetIdList = 0x01, HasLinkInfo = 0x02, HasName = 0x04, HasRelativePath = 0x08,
        HasWorkingDir = 0x10, HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80;
    private static readonly Guid LinkClsid = new("00021401-0000-0000-C000-000000000046");

    public static ShellLinkTarget? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 76 || BinaryPrimitives.ReadUInt32LittleEndian(d) != 0x4C) return null;
        if (new Guid(d.Slice(4, 16)) != LinkClsid) return null;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(20));
        int pos = 76;

        if ((flags & HasLinkTargetIdList) != 0)
        {
            if (pos + 2 > d.Length) return null;
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(pos));
        }

        string? target = null;
        if ((flags & HasLinkInfo) != 0)
        {
            if (pos + 28 > d.Length) return null;
            var li = d.Slice(pos);
            int liSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(li);
            int liHeader = (int)BinaryPrimitives.ReadUInt32LittleEndian(li.Slice(4));
            uint liFlags = BinaryPrimitives.ReadUInt32LittleEndian(li.Slice(8));
            if (liSize < 28 || pos + liSize > d.Length) return null;
            li = li.Slice(0, liSize);
            if ((liFlags & 0x01) != 0) // VolumeIDAndLocalBasePath
            {
                string? basePath = null, suffix = null;
                if (liHeader >= 0x24 && li.Length >= 0x24)
                {
                    int uBase = (int)BinaryPrimitives.ReadUInt32LittleEndian(li.Slice(28));
                    int uSuffix = (int)BinaryPrimitives.ReadUInt32LittleEndian(li.Slice(32));
                    basePath = ReadUnicodeZ(li, uBase);
                    suffix = ReadUnicodeZ(li, uSuffix);
                }
                if (string.IsNullOrEmpty(basePath))
                {
                    int aBase = (int)BinaryPrimitives.ReadUInt32LittleEndian(li.Slice(16));
                    int aSuffix = (int)BinaryPrimitives.ReadUInt32LittleEndian(li.Slice(24));
                    basePath = ReadAnsiZ(li, aBase);
                    suffix = ReadAnsiZ(li, aSuffix);
                }
                if (!string.IsNullOrEmpty(basePath)) target = basePath + (suffix ?? "");
            }
            pos += liSize;
        }

        bool unicode = (flags & IsUnicode) != 0;
        string? name = null, rel = null, wd = null, args = null;
        if ((flags & HasName) != 0) name = ReadStringData(d, ref pos, unicode);
        if ((flags & HasRelativePath) != 0) rel = ReadStringData(d, ref pos, unicode);
        if ((flags & HasWorkingDir) != 0) wd = ReadStringData(d, ref pos, unicode);
        if ((flags & HasArguments) != 0) args = ReadStringData(d, ref pos, unicode);
        _ = name; _ = HasIconLocation;
        if (target is null && rel is not null && wd is not null)
        {
            try { target = Path.GetFullPath(Path.Combine(wd, rel)); } catch { }
        }
        return new ShellLinkTarget(target, args, wd, "MS-SHLLINK");
    }

    private static string? ReadStringData(ReadOnlySpan<byte> d, ref int pos, bool unicode)
    {
        if (pos + 2 > d.Length) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(pos));
        pos += 2;
        int bytes = unicode ? count * 2 : count;
        if (pos + bytes > d.Length) { pos = d.Length; return null; }
        string s = unicode ? Encoding.Unicode.GetString(d.Slice(pos, bytes)) : Ansi.GetString(d.Slice(pos, bytes));
        pos += bytes;
        return s;
    }

    private static string? ReadUnicodeZ(ReadOnlySpan<byte> s, int offset)
    {
        if (offset <= 0 || offset >= s.Length) return null;
        var span = s.Slice(offset);
        int end = 0;
        while (end + 1 < span.Length && (span[end] != 0 || span[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(span.Slice(0, end));
    }

    private static string? ReadAnsiZ(ReadOnlySpan<byte> s, int offset)
    {
        if (offset <= 0 || offset >= s.Length) return null;
        var span = s.Slice(offset);
        int end = span.IndexOf((byte)0);
        if (end < 0) end = span.Length;
        return Ansi.GetString(span.Slice(0, end));
    }

    // Caminhos ANSI no .lnk usam a página de código ANSI do sistema que criou o atalho (assumimos a local).
    private static readonly Encoding Ansi = CreateAnsi();
    private static Encoding CreateAnsi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch { return Encoding.Latin1; }
    }
}
