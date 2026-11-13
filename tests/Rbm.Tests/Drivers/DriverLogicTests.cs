using Rbm.Core;
using Rbm.Drivers;
using Xunit;
using static Rbm.Tests.Drivers.DriverTestData;

namespace Rbm.Tests.Drivers;

public class DriverSnapshotTests
{
    private static DriverSnapshot Snap(params DriverSnapshotEntry[] e) => new(1, DateTimeOffset.Now, "PC", "Win", e);
    private static DriverSnapshotEntry E(string id, string? inf, string? ver, DateTime? date = null, string prov = "P") =>
        new(id, "Dev " + id, "System", inf, prov, ver, date, true, 0);

    [Fact]
    public void Diff_detects_all_change_kinds()
    {
        var older = Snap(E("A", "oem1.inf", "1.0.0.0"), E("B", "oem2.inf", "2.0.0.0"), E("C", "oem3.inf", "3.0.0.0"),
                         E("D", "oem4.inf", "4.0.0.0"), E("E", "oem5.inf", "5.0.0.0"), E("F", null, null));
        var newer = Snap(E("A", "oem9.inf", "1.0.0.10"), E("B", "oem2.inf", "1.9.9.9"), E("C", "oem3.inf", "3.0.0.0"),
                         E("D", "oem7.inf", "4.0.0.0"), E("F", "oem8.inf", "1.0"), E("G", "oem10.inf", "7.0"));
        var diff = DriverSnapshotComparer.Diff(older, newer);
        Assert.Equal(DriverChangeKind.Upgraded, diff.Changes.Single(c => c.InstanceId == "A").Kind);
        Assert.Equal(DriverChangeKind.Downgraded, diff.Changes.Single(c => c.InstanceId == "B").Kind);
        Assert.DoesNotContain(diff.Changes, c => c.InstanceId == "C");
        Assert.Equal(DriverChangeKind.Changed, diff.Changes.Single(c => c.InstanceId == "D").Kind);
        Assert.Equal(DriverChangeKind.Removed, diff.Changes.Single(c => c.InstanceId == "E").Kind);
        Assert.Equal(DriverChangeKind.Added, diff.Changes.Single(c => c.InstanceId == "F").Kind);
        Assert.Equal(DriverChangeKind.Added, diff.Changes.Single(c => c.InstanceId == "G").Kind);
    }

    [Fact]
    public void Same_version_newer_date_is_upgrade()
    {
        var diff = DriverSnapshotComparer.Diff(Snap(E("A", "oem1.inf", "1.0", new DateTime(2020, 1, 1))),
                                               Snap(E("A", "oem1.inf", "1.0", new DateTime(2021, 1, 1))));
        Assert.Equal(DriverChangeKind.Upgraded, diff.Changes.Single().Kind);
    }

    [Fact]
    public void Json_roundtrip_preserves_entries()
    {
        var inv = Inventory(Device(@"PCI\X\1", "GPU", Driver("oem1.inf", "1.2.3.4", new DateTime(2024, 5, 6))), Device(@"PCI\X\2", "Sem driver"));
        var s = DriverSnapshot.FromInventory(inv);
        Assert.Single(s.Entries); // dispositivo sem driver e sem problema não entra
        var back = DriverSnapshot.FromJson(s.ToJson())!;
        Assert.Equal(s.Entries, back.Entries);
        Assert.Empty(DriverSnapshotComparer.Diff(s, back).Changes);
    }
}

public class DriverInputValidatorTests
{
    [Theory]
    [InlineData("oem1.inf", true)]
    [InlineData("OEM123.INF", true)]
    [InlineData("oem.inf", false)]
    [InlineData("oem1.inf ", false)]
    [InlineData(@"..\oem1.inf", false)]
    [InlineData("machine.inf", false)]
    [InlineData("oem1.inf;calc", false)]
    public void Published_name_is_strict(string name, bool ok) =>
        Assert.Equal(ok, DriverInputValidator.ValidatePublishedName(name, allowAll: false).Success);

    [Fact]
    public void Published_name_all_only_when_allowed()
    {
        Assert.True(DriverInputValidator.ValidatePublishedName("*", allowAll: true).Success);
        Assert.Null(DriverInputValidator.ValidatePublishedName(null, allowAll: true).Value);
        Assert.False(DriverInputValidator.ValidatePublishedName("*", allowAll: false).Success);
    }

    [Theory]
    [InlineData(@"\\server\share\x", "path.unc")]
    [InlineData(@"relative\dir", "path.relative")]
    [InlineData(@"C:\Users\x\..\..\Windows", "path.traversal")]
    [InlineData(@"C:\Temp\file.txt:ads", "path.chars")]
    [InlineData("", "path.invalid")]
    public void Backup_folder_rejects_unsafe_shapes(string path, string code) =>
        Assert.Equal(code, DriverInputValidator.ValidateBackupFolder(path).ErrorCode);

    [Fact]
    public void Backup_folder_rejects_system_dirs_and_drive_root()
    {
        Assert.Equal("path.protected", DriverInputValidator.ValidateBackupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).ErrorCode);
        Assert.Equal("path.protected", DriverInputValidator.ValidateBackupFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).ErrorCode);
        Assert.Equal("folder.root", DriverInputValidator.ValidateBackupFolder(Path.GetPathRoot(Environment.SystemDirectory)!).ErrorCode);
    }

    [Fact]
    public void Backup_folder_accepts_existing_folder_in_own_profile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rbm-val-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { Assert.True(DriverInputValidator.ValidateBackupFolder(dir).Success); }
        finally { Directory.Delete(dir); }
        Assert.Equal("path.missing", DriverInputValidator.ValidateBackupFolder(dir).ErrorCode);
    }

    [Fact]
    public void Install_inf_requires_inf_extension()
    {
        string f = Path.Combine(Path.GetTempPath(), "rbm-val-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, "x");
        try { Assert.Equal("inf.extension", DriverInputValidator.ValidateInstallInf(f).ErrorCode); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Store_inf_must_be_inside_FileRepository_package_folder()
    {
        Assert.Equal("store.location", DriverInputValidator.ValidateStoreInf(@"C:\Temp\pkg.inf_amd64_0123456789abcdef\x.inf").ErrorCode);
        string fake = Path.Combine(DriverStoreRules.FileRepository, "..", "evil", "x.inf");
        Assert.False(DriverInputValidator.ValidateStoreInf(fake).Success);
    }

    [Theory]
    [InlineData(@"PCI\VEN_1002&DEV_73FF&SUBSYS_52171849&REV_C7\4&1234", true)]
    [InlineData(@"NOENUMERATOR", false)]
    [InlineData("PCI\\X\n", false)]
    [InlineData("PCI\\X\"&calc", false)]
    public void Instance_id_validation(string id, bool ok) => Assert.Equal(ok, DriverInputValidator.ValidateInstanceId(id).Success);

    [Theory]
    [InlineData(@"oem1_x\a.inf", true)]
    [InlineData(@"..\a.inf", false)]
    [InlineData(@"C:\a.inf", false)]
    [InlineData(@"a\..\..\b.cat", false)]
    public void Manifest_paths_stay_inside_backup(string rel, bool ok) =>
        Assert.Equal(ok, DriverBackupService.SafeCombine(@"C:\Backup\RBM-Drivers-1", rel) is not null);

    [Fact]
    public void Package_id_rule_matches_real_ids()
    {
        Assert.True(DriverStoreRules.IsSafePackageId("amdafd.inf_amd64_d6709807244bfcf6"));
        Assert.True(DriverStoreRules.IsSafePackageId("hdx_msiext_rtk.inf_amd64_0123456789abcdef"));
        Assert.False(DriverStoreRules.IsSafePackageId(@"..\x.inf_amd64_d6709807244bfcf6"));
        Assert.False(DriverStoreRules.IsSafePackageId("x.inf_amd64_short"));
    }
}

public class DriverStoreAnalysisTests
{
    [Fact]
    public void Groups_duplicates_newest_first_and_marks_in_use()
    {
        var store = new DriverStoreInventory(DateTimeOffset.Now, PnpUtilParser.ParseXml(PnpUtilXml), "test", []);
        var inv = Inventory(
            Device(@"PCI\VEN_1022&DEV_1637\4&x", "HD Audio Bus (chipset)", Driver("oem9.inf", "22.20.0.0", new DateTime(2022, 4, 20))),
            Device(@"PCI\VEN_1002&DEV_AB28\6&y", "HD Audio Bus (GPU)", Driver("oem47.inf", "25.30.0.1", new DateTime(2025, 8, 21))));
        var a = DriverStoreService.Analyze(inv, store);
        var g = Assert.Single(a.Duplicates);
        Assert.Equal("amdafd.inf", g.OriginalName);
        Assert.Equal(["oem47.inf", "oem9.inf"], g.Packages.Select(p => p.PublishedName));
        Assert.Equal(2, g.InUsePublishedNames.Count);
        Assert.Equal("oem23.inf", Assert.Single(a.Unreferenced).PublishedName);
    }
}

public class DriverDiagnosticsTests
{
    [Fact]
    public void Produces_structured_findings_from_inventory_only()
    {
        var now = new DateTime(2026, 11, 9);
        var inv = Inventory(
            Device(@"PCI\VEN_10DE&DEV_2484\1", "GPU", Driver("oem1.inf", "31.0.15.1", new DateTime(2025, 1, 1)), problem: 43, className: "Display", classGuid: DisplayClass),
            Device(@"PCI\VEN_8086&DEV_0001\2", "Desconhecido", problem: 28, classGuid: null),
            Device(@"USB\VID_1234&PID_0001\3", "Antigo", Driver("oem2.inf", "1.0", new DateTime(2015, 3, 1))),
            Device(@"USB\VID_1234&PID_0002\4", "Sem assinatura", Driver("oem3.inf", "1.0", new DateTime(2024, 1, 1), sig: DriverSignatureState.Unsigned)),
            Device(@"PCI\VEN_1022&DEV_0002\5", "Inbox antigo", Driver("machine.inf", "10.0.26100.1", new DateTime(2006, 6, 21))),
            Device(@"PCI\VEN_1022&DEV_0003\6", "Desativado", Driver("oem4.inf", "1.0", new DateTime(2024, 1, 1)), problem: 22));

        var f = DriverDiagnostics.Analyze(inv, null, now);
        var code43 = Assert.Single(f, x => x.RuleId == "drivers.problem-code");
        Assert.Equal(Severity.High, code43.Severity);
        Assert.Contains(code43.Evidence, e => e.Contains("CM_PROB_FAILED_POST_START"));
        Assert.Equal(Severity.Medium, Assert.Single(f, x => x.RuleId == "drivers.unknown-device").Severity);
        var old = Assert.Single(f, x => x.RuleId == "drivers.old");
        Assert.Equal(Severity.Info, old.Severity);
        Assert.Single(old.Evidence); // inbox (machine.inf) de 2006 não entra
        Assert.Equal(Severity.High, Assert.Single(f, x => x.RuleId == "drivers.signature").Severity);
        Assert.Equal(Severity.Info, Assert.Single(f, x => x.RuleId == "drivers.disabled").Severity);
        Assert.All(f, x => Assert.Equal("Drivers", x.NavigateTo));
        Assert.Equal(Severity.High, f[0].Severity); // ordenado por gravidade
    }

    [Fact]
    public void Ddu_advised_only_with_evidence()
    {
        var healthy = Inventory(Device(@"PCI\VEN_1002&DEV_73FF\1", "RX", Driver("oem1.inf", "1.0", null), className: "Display", classGuid: DisplayClass,
            hwids: [@"PCI\VEN_1002&DEV_73FF"]));
        Assert.False(DduAdvisor.Evaluate(healthy).Advisable);

        var swapped = Inventory(
            Device(@"PCI\VEN_1002&DEV_73FF\1", "RX", Driver("oem1.inf", "1.0", null), className: "Display", classGuid: DisplayClass, hwids: [@"PCI\VEN_1002&DEV_73FF"]),
            Device(@"PCI\VEN_10DE&DEV_2484\2", "RTX antiga", Driver("oem2.inf", "1.0", null), present: false, className: "Display", classGuid: DisplayClass, hwids: [@"PCI\VEN_10DE&DEV_2484"]));
        var g = DduAdvisor.Evaluate(swapped);
        Assert.True(g.Advisable);
        Assert.Contains(g.ReasonsPt, r => r.Contains("NVIDIA"));
        Assert.StartsWith("https://www.wagnardsoft.com", g.OfficialPage);
    }
}

public class OfficialSourcesTests
{
    [Fact]
    public void Catalog_url_is_official_and_escaped()
    {
        var url = OfficialSources.CatalogSearchUrl(@"PCI\VEN_1002&DEV_73FF");
        Assert.Equal("https://www.catalog.update.microsoft.com/Search.aspx?q=PCI%5CVEN_1002%26DEV_73FF", url);
    }

    [Fact]
    public void All_static_urls_are_https()
    {
        Assert.All(OfficialSources.Catalog, s => Assert.StartsWith("https://", s.Url));
    }

    [Theory]
    [InlineData("Advanced Micro Devices, Inc.", "AMD")]
    [InlineData("AMD (ATI/Radeon)", "AMD")]
    [InlineData("NVIDIA", "NVIDIA")]
    [InlineData("Intel Corporation", "Intel")]
    [InlineData("Micro-Star International Co., Ltd.", "MSI")]
    [InlineData("Fabricante Qualquer", null)]
    public void Normalizes_vendor(string name, string? expected) => Assert.Equal(expected, OfficialSources.NormalizeVendor(name));

    [Fact]
    public void Gpu_guidance_shows_installed_version_and_official_link()
    {
        var gpu = Device(@"PCI\VEN_1002&DEV_73FF\1", "AMD Radeon RX 6600", Driver("oem10.inf", "32.0.21045.16010", new DateTime(2026, 10, 16)),
            className: "Display", classGuid: DisplayClass, hwids: [@"PCI\VEN_1002&DEV_73FF&SUBSYS_52171849&REV_C7"]);
        var g = OfficialSources.GuidanceFor(gpu);
        Assert.Equal(DriverCategory.Gpu, g.Category);
        Assert.Equal("32.0.21045.16010", g.InstalledVersion);
        Assert.Contains(g.Sources, s => s.Url.StartsWith("https://www.amd.com/"));
        Assert.Contains(g.NotesPt, n => n.Contains("nem sempre"));
    }

    [Fact]
    public void Unknown_device_report_has_catalog_searches()
    {
        var inv = Inventory(Device(@"PCI\VEN_8086&DEV_A0E8\3", "Dispositivo desconhecido", problem: 28, classGuid: null,
            hwids: [@"PCI\VEN_8086&DEV_A0E8&SUBSYS_00000000&REV_20", @"PCI\VEN_8086&DEV_A0E8"]));
        var r = Assert.Single(OfficialSources.UnknownAndProblemDevices(inv));
        Assert.Equal("Intel", r.ChipVendor);
        Assert.Contains(r.CatalogSearches, c => c.Query == "VEN_8086&DEV_A0E8");
        Assert.All(r.CatalogSearches, c => Assert.StartsWith(OfficialSources.CatalogBase, c.Url));
    }
}

public class NvidiaPackageAssistantTests
{
    [Theory]
    [InlineData("Display.Driver", NvidiaComponentRole.Essential)]
    [InlineData("HDAudio", NvidiaComponentRole.Optional)]
    [InlineData("PhysX", NvidiaComponentRole.Recommended)]
    [InlineData("NvTelemetry", NvidiaComponentRole.Optional)]
    [InlineData("NvContainer.MessageBus", NvidiaComponentRole.Optional)]
    [InlineData("Algo.Novo", NvidiaComponentRole.Unknown)]
    public void Describes_components(string folder, NvidiaComponentRole role) => Assert.Equal(role, NvidiaPackageAssistant.Describe(folder).Role);

    [Fact]
    public void Analyzes_extracted_folder_read_only_and_checks_gpu_support()
    {
        string root = Path.Combine(Path.GetTempPath(), "rbm-nv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Display.Driver"));
        Directory.CreateDirectory(Path.Combine(root, "HDAudio"));
        File.WriteAllText(Path.Combine(root, "setup.cfg"), "<setup/>");
        File.WriteAllText(Path.Combine(root, "Display.Driver", "DisplayDriver.nvi"), """<?xml version="1.0"?><nvi name="Display.Driver" version="581.57"/>""");
        File.WriteAllText(Path.Combine(root, "Display.Driver", "nv_dispig.inf"), "[Dev]\r\n%D% = S, PCI\\VEN_10DE&DEV_2484\r\n");
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToArray();
        try
        {
            var gpu = Device(@"PCI\VEN_10DE&DEV_2484\1", "RTX 3070", className: "Display", classGuid: DisplayClass, hwids: [@"PCI\VEN_10DE&DEV_2484&SUBSYS_146B1462"]);
            var r = NvidiaPackageAssistant.Analyze(root, gpu);
            Assert.True(r.LooksLikeNvidiaPackage);
            Assert.Equal("581.57", r.PackageVersion);
            Assert.True(r.SupportsInstalledGpu);
            Assert.Equal(2, r.Components.Count);

            var other = gpu with { HardwareIds = [@"PCI\VEN_10DE&DEV_9999"] };
            Assert.False(NvidiaPackageAssistant.Analyze(root, other).SupportsInstalledGpu);

            var after = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToArray();
            Assert.Equal(before, after); // nada foi modificado
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Non_package_folder_is_reported()
    {
        var r = NvidiaPackageAssistant.Analyze(Path.GetTempPath());
        Assert.False(r.LooksLikeNvidiaPackage);
        Assert.NotEmpty(r.Warnings);
    }
}
