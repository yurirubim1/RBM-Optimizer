namespace Rbm.Core;

/// <summary>Gravidade de um achado de diagnóstico.</summary>
public enum Severity { Info = 0, Low = 1, Medium = 2, High = 3, Critical = 4 }

/// <summary>Grau de confiança de uma conclusão ou medição.</summary>
public enum Confidence { Low = 0, Medium = 1, High = 2 }

/// <summary>Risco de uma alteração.</summary>
public enum RiskLevel { Minimal = 0, Low = 1, Medium = 2, High = 3 }

/// <summary>Impacto esperado. "Unknown" é o padrão honesto quando não há dados.</summary>
public enum ImpactLevel { Unknown = 0, Negligible = 1, Low = 2, Medium = 3, High = 4 }

/// <summary>
/// Classificação de evidência (seção 15 do escopo):
/// A = benefício demonstrado; B = plausível/dependente do sistema; C = experimental;
/// D = sem benefício demonstrado / placebo; E = obsoleto/inseguro/contraindicado.
/// </summary>
public enum EvidenceGrade { A, B, C, D, E }

/// <summary>O que é necessário para a alteração entrar em vigor.</summary>
public enum RestartRequirement { None = 0, AppRestart = 1, ExplorerRestart = 2, SignOut = 3, Reboot = 4 }

/// <summary>Esforço do usuário para seguir uma recomendação.</summary>
public enum EffortLevel { Trivial = 0, Low = 1, Medium = 2, High = 3 }

public enum FormFactor { Unknown, Desktop, Laptop }

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Microsoft, Other }

public enum CpuVendor { Unknown, Intel, Amd, Other }

public enum StorageMediaType { Unknown, Hdd, Ssd, Scm }

public enum StorageBusType { Unknown, Sata, Nvme, Usb, Sas, Raid, Virtual, Other }
