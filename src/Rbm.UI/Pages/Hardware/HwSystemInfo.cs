using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Hardware;
using Rbm.Optimization.Providers;
using Rbm.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Rbm.UI.Pages.Hardware;

/// <summary>Leitura (somente) das configurações de processador do plano de energia ativo.</summary>
public static class ProcessorPlanReader
{
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid MinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    public static readonly Guid MaxState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    public static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    public static readonly Guid CpMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");

    public static string BoostName(uint v) => v switch
    {
        0 => "Desativado",
        1 => "Ativado",
        2 => "Agressivo",
        3 => "Ativado (eficiente)",
        4 => "Agressivo (eficiente)",
        5 => "Agressivo na frequência garantida",
        6 => "Agressivo eficiente na frequência garantida",
        _ => $"Valor {v}",
    };

    public static (string scheme, List<InfoRow> rows) Read(bool hasBattery)
    {
        var g = PowerApi.GetActiveScheme();
        string name = PowerApi.FriendlyName(g);
        var rows = new List<InfoRow>
        {
            Row(g, MinState, "Estado mínimo do processador", v => $"{v}%", hasBattery,
                "Piso de desempenho que o Windows permite ao processador. Valores altos impedem a redução de clock em ociosidade (mais consumo e calor)."),
            Row(g, MaxState, "Estado máximo do processador", v => $"{v}%", hasBattery,
                "Teto de desempenho. Abaixo de 100% normalmente desativa o boost e limita o clock."),
            Row(g, BoostMode, "Modo de aumento de desempenho (boost)", BoostName, hasBattery,
                "Política de boost (Turbo Boost / Precision Boost) usada pelo Windows. Configuração normalmente oculta no Painel de Controle."),
            Row(g, CpMinCores, "Estacionamento de núcleos: mínimo de núcleos ativos", v => $"{v}% dos núcleos", hasBattery,
                "Percentual mínimo de núcleos que não podem ser estacionados (100% = estacionamento efetivamente desativado)."),
        };
        return (name, rows);
    }

    private static InfoRow Row(Guid scheme, Guid setting, string label, Func<uint, string> fmt, bool hasBattery, string tip)
    {
        uint? ac = PowerApi.ReadAc(scheme, SubProcessor, setting);
        uint? dc = PowerApi.ReadDc(scheme, SubProcessor, setting);
        if (ac is null && dc is null) return new InfoRow(label, "não definido neste plano", tip);
        string value = ac is { } a ? fmt(a) : "—";
        string hint = hasBattery ? $"Na bateria: {(dc is { } d ? fmt(d) : "—")} · {tip}" : tip;
        return new InfoRow(label, value + (hasBattery ? " (na tomada)" : ""), hint);
    }
}

/// <summary>Estado (somente leitura) de HAGS e Modo de Jogo.</summary>
public static class GraphicsState
{
    public static (string text, string hint, bool? on) Hags()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            var v = k?.GetValue("HwSchMode");
            return v switch
            {
                2 => ("Ativado", "HwSchMode = 2 em HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers.", true),
                1 => ("Desativado", "HwSchMode = 1 em HKLM\\SYSTEM\\CurrentControlSet\\Control\\GraphicsDrivers.", false),
                null => ("Padrão do sistema", "Valor HwSchMode ausente: o Windows decide conforme o suporte da GPU e do driver (WDDM 2.7+).", null),
                _ => ($"Valor não reconhecido ({v})", "HwSchMode com valor fora do esperado (1 = desativado, 2 = ativado).", null),
            };
        }
        catch (Exception ex) { return (Hw.Unavailable, "Falha ao ler o Registro: " + ex.Message, null); }
    }

    public static (string text, string hint, bool? on) GameMode()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            var v = k?.GetValue("AutoGameModeEnabled");
            return v switch
            {
                1 => ("Ativado", "AutoGameModeEnabled = 1 em HKCU\\Software\\Microsoft\\GameBar.", true),
                0 => ("Desativado", "AutoGameModeEnabled = 0 em HKCU\\Software\\Microsoft\\GameBar.", false),
                null => ("Padrão do Windows (ativado)", "Valor AutoGameModeEnabled ausente: vale o padrão do Windows, que mantém o Modo de Jogo ligado.", null),
                _ => ($"Valor não reconhecido ({v})", "AutoGameModeEnabled com valor fora do esperado (0/1).", null),
            };
        }
        catch (Exception ex) { return (Hw.Unavailable, "Falha ao ler o Registro: " + ex.Message, null); }
    }
}

/// <summary>Ferramenta oficial do fabricante da GPU (detectada localmente; nunca instalada pelo RBM).</summary>
public sealed class VendorToolItem
{
    public VendorToolItem(string name, string description, string? launch, string downloadUrl)
    {
        Name = name; Description = description; Launch = launch; DownloadUrl = downloadUrl;
        OpenCommand = new RelayCommand(() => { if (Launch is not null) Hw.ShellOpen(Launch, what: Name); }, () => Launch is not null);
        DownloadCommand = new RelayCommand(() => Hw.ShellOpen(DownloadUrl, what: "o site oficial"));
    }

    public string Name { get; }
    public string Description { get; }
    public string? Launch { get; }
    public string DownloadUrl { get; }
    public bool Installed => Launch is not null;
    public string Status => Installed ? "Instalado" : "Não encontrado";
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand DownloadCommand { get; }

    public static List<VendorToolItem> Detect(IEnumerable<GpuVendor> vendors)
    {
        var set = vendors.ToHashSet();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pkgs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        string? File(params string[] candidates) => candidates.FirstOrDefault(System.IO.File.Exists);
        string? Package(string pfn, string appId) => Directory.Exists(Path.Combine(pkgs, pfn)) ? $@"shell:AppsFolder\{pfn}!{appId}" : null;

        var list = new List<VendorToolItem>();
        if (set.Contains(GpuVendor.Amd))
            list.Add(new("AMD Software: Adrenalin Edition", "Driver, perfis por jogo, Radeon Anti-Lag/Chill, monitoramento e atualizações oficiais da AMD.",
                File(Path.Combine(pf, @"AMD\CNext\CNext\RadeonSoftware.exe")),
                "https://www.amd.com/en/support/download/drivers.html"));
        if (set.Contains(GpuVendor.Nvidia))
        {
            list.Add(new("NVIDIA App", "Atualização de driver, otimização por jogo e recursos da NVIDIA (substitui o GeForce Experience).",
                File(Path.Combine(pf, @"NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe")),
                "https://www.nvidia.com/en-us/software/nvidia-app/"));
            list.Add(new("Painel de Controle NVIDIA", "Configurações 3D globais/por programa, resolução, G-SYNC e cores.",
                File(Path.Combine(pf, @"NVIDIA Corporation\Control Panel Client\nvcplui.exe"))
                    ?? Package("NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj", "NVIDIACorp.NVIDIAControlPanel"),
                "https://apps.microsoft.com/detail/9nf8h0h7wmlt"));
        }
        if (set.Contains(GpuVendor.Intel))
            list.Add(new("Intel Graphics Command Center", "Configurações de exibição, vídeo e perfis por jogo para gráficos Intel.",
                Package("AppUp.IntelGraphicsExperience_8j3eq9eme6ctt", "App"),
                "https://apps.microsoft.com/detail/9plfnlnt3g5g"));
        return list;
    }
}

public sealed record PageFileRow(string Name, long AllocatedMb, long CurrentMb, long PeakMb)
{
    public string AllocatedText => $"{AllocatedMb:N0} MB";
    public string CurrentText => $"{CurrentMb:N0} MB";
    public string PeakText => $"{PeakMb:N0} MB";
}

/// <summary>Arquivo de paginação: uso (Win32_PageFileUsage) + configuração (Registro PagingFiles).</summary>
public static class PageFileReader
{
    public static (List<PageFileRow> rows, string config, string detail) Read()
    {
        var rows = new List<PageFileRow>();
        foreach (var o in SystemInventory.Query("SELECT Name, AllocatedBaseSize, CurrentUsage, PeakUsage FROM Win32_PageFileUsage"))
            rows.Add(new PageFileRow(o["Name"]?.ToString() ?? "", ToLong(o["AllocatedBaseSize"]), ToLong(o["CurrentUsage"]), ToLong(o["PeakUsage"])));

        string[] entries;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"))
            entries = k?.GetValue("PagingFiles") switch { string[] a => a, string s => new[] { s }, _ => Array.Empty<string>() };
        entries = entries.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray();

        if (entries.Length == 0)
            return (rows, "Sem arquivo de paginação configurado",
                "Nenhuma entrada em PagingFiles. Sem arquivo de paginação o limite de commit fica igual à RAM: aplicativos podem falhar por falta de memória mesmo com RAM livre, e despejos de memória (dumps) de travamentos não são gravados.");

        var parts = new List<string>();
        foreach (var e in entries)
        {
            var t = e.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string path = t[0];
            if (path.StartsWith("?:", StringComparison.Ordinal)) { parts.Add("Gerenciado automaticamente pelo Windows (todas as unidades)"); continue; }
            if (t.Length >= 3 && long.TryParse(t[1], out var ini) && long.TryParse(t[2], out var max))
                parts.Add(ini == 0 && max == 0 ? $"{path}: tamanho gerenciado pelo sistema" : $"{path}: tamanho personalizado (inicial {ini:N0} MB, máximo {max:N0} MB)");
            else parts.Add($"{path}: tamanho gerenciado pelo sistema");
        }
        return (rows, string.Join(" · ", parts), "Fonte: Registro (Memory Management\\PagingFiles) e WMI Win32_PageFileUsage.");
    }

    private static long ToLong(object? o) { try { return o is null ? 0 : Convert.ToInt64(o); } catch { return 0; } }
}

/// <summary>Status do TRIM via "fsutil behavior query DisableDeleteNotify" (parse independente do idioma).</summary>
public static partial class TrimStatusReader
{
    [GeneratedRegex(@"^\s*(NTFS|ReFS)\b[^=\r\n]*DisableDeleteNotify\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FsLine();

    [GeneratedRegex(@"^\s*DisableDeleteNotify\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LegacyLine();

    [GeneratedRegex(@"^\s*(NTFS|ReFS)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AnyFs();

    public sealed record Result(bool Ok, string Ntfs, string Refs, bool? NtfsEnabled, string Raw);

    public static string Describe(string? digit) => digit switch
    {
        "0" => "TRIM ativado",
        "1" => "TRIM desativado",
        null => "não configurado",
        _ => $"valor {digit}",
    };

    public static Result Parse(string output)
    {
        string? ntfs = null, refs = null;
        bool ntfsSeen = false, refsSeen = false;
        foreach (Match m in AnyFs().Matches(output))
        {
            if (m.Groups[1].Value.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) ntfsSeen = true; else refsSeen = true;
        }
        foreach (Match m in FsLine().Matches(output))
        {
            if (m.Groups[1].Value.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) ntfs = m.Groups[2].Value; else refs = m.Groups[2].Value;
        }
        if (ntfs is null && !ntfsSeen && LegacyLine().Match(output) is { Success: true } lm) { ntfs = lm.Groups[1].Value; ntfsSeen = true; }
        bool ok = ntfsSeen || refsSeen;
        return new Result(ok,
            ntfsSeen ? Describe(ntfs) : "não informado",
            refsSeen ? Describe(refs) : "não informado",
            ntfs switch { "0" => true, "1" => false, _ => null },
            output.Trim());
    }

    public static async Task<Result> ReadAsync()
    {
        var r = await ProcessRunner.RunAsync(ProcessRunner.System32("fsutil.exe"), new[] { "behavior", "query", "DisableDeleteNotify" },
            TimeSpan.FromSeconds(10), CancellationToken.None, ProcessRunner.OemEncoding).ConfigureAwait(false);
        var res = Parse(r.StdOut);
        if (!res.Ok)
        {
            string why = r.TimedOut ? "tempo esgotado" : $"código {r.ExitCode}";
            string msg = (r.StdErr + " " + r.StdOut).Trim();
            return new Result(false, Hw.Unavailable, Hw.Unavailable, null, $"fsutil não retornou o estado ({why}). {msg}".Trim());
        }
        return res;
    }
}

/// <summary>Estado de energia (tomada/bateria) via GetSystemPowerStatus.</summary>
public static class PowerStatus
{
    public sealed record Status(bool Known, bool? OnAc, bool HasBattery, int? Percent, TimeSpan? Remaining, bool Charging, bool Saver);

    public static Status Read()
    {
        if (!GetSystemPowerStatus(out var s)) return new Status(false, null, false, null, null, false, false);
        bool? onAc = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        bool hasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
        int? pct = hasBattery && s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        TimeSpan? rem = hasBattery && s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new Status(true, onAc, hasBattery, pct, rem, hasBattery && (s.BatteryFlag & 8) != 0, (s.SystemStatusFlag & 1) != 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}
