using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Rbm.Core.Agent;
using Rbm.Data;
using Rbm.UI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.Pages.SystemPages;

/// <summary>
/// Utilitários compartilhados pelas páginas de Sistema (Processos, Inicialização, Drivers, Rede, Limpeza).
/// Namespace "SystemPages" (e não "System") para não sombrear o namespace System do .NET.
/// </summary>
public static class AgentCall
{
    public static IAgentClient Client => App.Services.GetRequiredService<IAgentClient>();
    public static bool IsConnected => Client.IsConnected;

    /// <summary>
    /// Garante o agente (dispara o UAC no primeiro uso) e invoca a operação da allow-list.
    /// Retorna null (com toast) se o usuário recusar a elevação ou se o agente devolver erro.
    /// </summary>
    public static async Task<AgentResponse?> InvokeAsync(string op, object payload, string actionTitle, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var agent = Client;
        if (!agent.IsConnected)
        {
            using var _ = App.Tasks.Begin("Iniciando agente elevado");
            if (!await agent.EnsureConnectedAsync(ct))
            {
                App.Notify.Warning(actionTitle, "Esta ação exige o RBM Agent (administrador). A elevação foi recusada ou falhou; nada foi alterado.");
                return null;
            }
        }
        AgentResponse r;
        try { r = await agent.InvokeAsync(op, payload, timeout, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            App.Log(ex, $"Agente {op}");
            App.Notify.Error(actionTitle, "Falha na comunicação com o RBM Agent: " + ex.Message);
            return null;
        }
        if (!r.Ok)
        {
            App.Notify.Error(actionTitle, r.Error ?? "O agente recusou a operação.");
            return null;
        }
        return r;
    }

    /// <summary>Lê o payload de resposta tolerando ausência/formato inesperado.</summary>
    public static T? Read<T>(AgentResponse? r)
    {
        if (r is null) return default;
        try { return r.Read<T>(); }
        catch (JsonException ex) { App.Log(ex, "Resposta do agente em formato inesperado"); return default; }
    }
}

public static class SysUi
{
    public static RbmDatabase Db => App.Services.GetRequiredService<RbmDatabase>();

    /// <summary>Abre URL oficial no navegador padrão (somente http/https).</summary>
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        {
            App.Notify.Warning("Link inválido", url ?? "");
            return;
        }
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { App.Notify.Error("Não foi possível abrir o navegador", ex.Message); }
    }

    /// <summary>Abre uma pasta existente no Explorer (somente leitura: apenas mostra).</summary>
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) { App.Notify.Warning("Pasta não encontrada", path ?? ""); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false }); }
        catch (Exception ex) { App.Notify.Error("Não foi possível abrir a pasta", ex.Message); }
    }

    /// <summary>Mostra o arquivo selecionado no Explorer.</summary>
    public static void RevealFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { App.Notify.Warning("Arquivo não encontrado", path ?? ""); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false }); }
        catch (Exception ex) { App.Notify.Error("Não foi possível abrir o Explorer", ex.Message); }
    }

    /// <summary>Seletor de pasta nativo (Microsoft.Win32.OpenFolderDialog, .NET 8+).</summary>
    public static string? PickFolder(string title, string? initial = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog(App.Current.MainWindow) == true ? dlg.FolderName : null;
    }

    /// <summary>Seletor de arquivo nativo.</summary>
    public static string? PickFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return dlg.ShowDialog(App.Current.MainWindow) == true ? dlg.FileName : null;
    }

    /// <summary>Registra na auditoria local sem derrubar a página se o banco falhar.</summary>
    public static async Task AuditAsync(string action, string detail, bool success)
    {
        try { await Db.AuditAsync(action, detail, success); }
        catch (Exception ex) { App.Log(ex, "Auditoria"); }
    }

    public static string Ms(double v) => double.IsFinite(v) ? v.ToString(v < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("pt-BR")) + " ms" : "—";
    public static string Pct(double v) => double.IsFinite(v) ? v.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + "%" : "—";
    public static string When(DateTime? t) => t is { } d ? d.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")) : "—";
    public static string When(DateTimeOffset? t) => t is { } d ? d.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR")) : "—";
}
