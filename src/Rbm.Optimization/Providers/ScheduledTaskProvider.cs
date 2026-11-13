using System.Text.RegularExpressions;
using System.Xml.Linq;
using Rbm.Core;
using Rbm.Core.Tweaks;

namespace Rbm.Optimization.Providers;

/// <summary>Habilita/desabilita tarefas agendadas via schtasks (XML é independente de idioma).</summary>
public sealed partial class ScheduledTaskProvider : ISettingProvider
{
    public bool CanHandle(SettingTarget target) => target is ScheduledTaskTarget;

    public static void Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400 || !path.StartsWith('\\') || !PathRegex().IsMatch(path) || path.Contains(".."))
            throw new ArgumentException($"Caminho de tarefa inválido: {path}");
    }

    public SettingValue Read(SettingTarget target)
    {
        var t = (ScheduledTaskTarget)target;
        Validate(t.TaskPath);
        var r = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"), new[] { "/query", "/tn", t.TaskPath, "/xml" }, TimeSpan.FromSeconds(15));
        if (!r.Success) return SettingValue.Absent; // tarefa inexistente nesta build
        return new SettingValue(true, "task", ParseEnabled(r.StdOut) ? "Enabled" : "Disabled");
    }

    public static bool ParseEnabled(string xml)
    {
        var start = xml.IndexOf('<');
        var doc = XDocument.Parse(start > 0 ? xml[start..] : xml);
        var enabled = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Settings")?.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled");
        return enabled is null || !string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

    public void Write(SettingTarget target, SettingValue value)
    {
        var t = (ScheduledTaskTarget)target;
        Validate(t.TaskPath);
        if (!value.Exists) return;
        bool enable = string.Equals(value.Data, "Enabled", StringComparison.OrdinalIgnoreCase);
        var r = ProcessRunner.Run(ProcessRunner.System32("schtasks.exe"), new[] { "/change", "/tn", t.TaskPath, enable ? "/enable" : "/disable" }, TimeSpan.FromSeconds(15));
        if (!r.Success) throw new InvalidOperationException($"schtasks falhou ({r.ExitCode}): {(r.StdErr + r.StdOut).Trim()}");
    }

    [GeneratedRegex(@"^[\\A-Za-z0-9 _\-\.\{\}\(\)]+$")]
    private static partial Regex PathRegex();
}
