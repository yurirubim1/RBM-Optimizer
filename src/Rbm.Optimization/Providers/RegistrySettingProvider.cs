using System.Globalization;
using Rbm.Core;
using Rbm.Core.Tweaks;
using Microsoft.Win32;
using RegistryHive = Rbm.Core.Tweaks.RegistryHive;

namespace Rbm.Optimization.Providers;

public sealed class RegistrySettingProvider : ISettingProvider
{
    public bool CanHandle(SettingTarget target) => target is RegistryTarget;

    private static RegistryKey Base(RegistryHive hive) => hive switch
    {
        RegistryHive.HKLM => RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64),
        RegistryHive.HKCU => RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.CurrentUser, RegistryView.Registry64),
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    public SettingValue Read(SettingTarget target)
    {
        var t = (RegistryTarget)target;
        using var root = Base(t.Hive);
        using var key = root.OpenSubKey(t.Path, writable: false);
        if (key is null) return SettingValue.Absent;
        var raw = key.GetValue(t.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null) return SettingValue.Absent;
        var kind = key.GetValueKind(t.Name);
        return new SettingValue(true, MapKind(kind).ToString(), Serialize(raw, kind));
    }

    public void Write(SettingTarget target, SettingValue value)
    {
        var t = (RegistryTarget)target;
        Validate(t);
        using var root = Base(t.Hive);
        if (!value.Exists)
        {
            using var k = root.OpenSubKey(t.Path, writable: true);
            k?.DeleteValue(t.Name, throwOnMissingValue: false);
            return;
        }
        var kind = Enum.TryParse<RegistryValueKind2>(value.Kind, true, out var k2) ? k2 : t.Kind;
        using var key = root.CreateSubKey(t.Path, writable: true) ?? throw new InvalidOperationException($"Não foi possível abrir/criar {t.Display}");
        var (obj, rk) = Deserialize(value.Data ?? "", kind);
        key.SetValue(t.Name, obj, rk);
    }

    public static void Validate(RegistryTarget t)
    {
        if (string.IsNullOrWhiteSpace(t.Path) || t.Path.Length > 512 || t.Path.Contains("..") || t.Path.StartsWith('\\'))
            throw new ArgumentException($"Caminho de Registro inválido: {t.Path}");
        if (t.Name.Length > 255) throw new ArgumentException("Nome de valor muito longo.");
    }

    private static RegistryValueKind2 MapKind(RegistryValueKind k) => k switch
    {
        RegistryValueKind.DWord => RegistryValueKind2.DWord,
        RegistryValueKind.QWord => RegistryValueKind2.QWord,
        RegistryValueKind.ExpandString => RegistryValueKind2.ExpandString,
        RegistryValueKind.MultiString => RegistryValueKind2.MultiString,
        RegistryValueKind.Binary => RegistryValueKind2.Binary,
        _ => RegistryValueKind2.String,
    };

    private static string Serialize(object raw, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((uint)(int)raw).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => unchecked((ulong)(long)raw).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => Convert.ToHexString((byte[])raw),
        RegistryValueKind.MultiString => string.Join('\n', (string[])raw),
        _ => raw.ToString() ?? "",
    };

    private static (object, RegistryValueKind) Deserialize(string data, RegistryValueKind2 kind)
    {
        switch (kind)
        {
            case RegistryValueKind2.DWord:
                return (unchecked((int)ParseU64(data)), RegistryValueKind.DWord);
            case RegistryValueKind2.QWord:
                return (unchecked((long)ParseU64(data)), RegistryValueKind.QWord);
            case RegistryValueKind2.Binary:
                return (Convert.FromHexString(data), RegistryValueKind.Binary);
            case RegistryValueKind2.MultiString:
                return (data.Split('\n'), RegistryValueKind.MultiString);
            case RegistryValueKind2.ExpandString:
                return (data, RegistryValueKind.ExpandString);
            default:
                return (data, RegistryValueKind.String);
        }
    }

    private static ulong ParseU64(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ulong.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (s.StartsWith('-')) return unchecked((ulong)long.Parse(s, CultureInfo.InvariantCulture));
        return ulong.Parse(s, CultureInfo.InvariantCulture);
    }
}
