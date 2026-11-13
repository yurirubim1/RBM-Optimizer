<#
.SYNOPSIS
  Valida o catálogo de tweaks do RBM Optimizer (src/Rbm.Optimization/Catalog/*.json).

.DESCRIPTION
  Verificações:
   - todos os JSON fazem parse e têm schemaVersion = 1
   - ids únicos entre arquivos e no formato minúsculo pontuado
   - campos obrigatórios e valores de enum válidos (grade, risk, impact, restart, confidence, category)
   - toda opção lista o MESMO conjunto de identidades de alvo
   - defaultOption existe; recommendedOption é null ou existe; null obrigatório para C/D/E
   - discriminador "type" é a primeira propriedade de cada alvo
   - registry: hive HKLM/HKCU, kind válido, DWORD/QWORD decimais
   - power: GUIDs válidos, ac/dc inteiros >= 0 ou null; opção padrão define ac e dc
   - service: nunca serviços protegidos; startMode válido
   - bcd: somente useplatformclock, useplatformtick, disabledynamictick, tscsyncpolicy
   - task: caminho começando com '\'
   - Advanced Lab <=> experimental; Privacidade <=> privacy
   - profiles.json: perfis obrigatórios, tweakId/option existentes, apenas notas A/B, nada experimental

  Compatível com Windows PowerShell 5.1 e PowerShell 7+.

.PARAMETER GenerateCatalogMd
  Regenera docs/TWEAK_CATALOG.md a partir dos JSON.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build/validate-catalog.ps1
  powershell -ExecutionPolicy Bypass -File build/validate-catalog.ps1 -GenerateCatalogMd
#>
[CmdletBinding()]
param(
    [string]$CatalogDir,
    [switch]$GenerateCatalogMd
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $CatalogDir) { $CatalogDir = Join-Path $root 'src\Rbm.Optimization\Catalog' }

$errors = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
function Err([string]$m) { $errors.Add($m) }
function Warn([string]$m) { $warnings.Add($m) }

function Read-Json([string]$path) {
    # Leitura explícita em UTF-8 (Get-Content no PS 5.1 usa ANSI para arquivos sem BOM).
    $text = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
    return $text | ConvertFrom-Json
}

function Has-Prop($obj, [string]$name) { return $null -ne $obj -and ($obj.PSObject.Properties.Name -contains $name) }

function Get-LocalizedPt($v) {
    if ($null -eq $v) { return $null }
    if ($v -is [string]) { return $v }
    if (Has-Prop $v 'pt') { return [string]$v.pt }
    return $null
}

$enumGrade      = @('A','B','C','D','E')
$enumConfidence = @('Low','Medium','High')
$enumRisk       = @('Minimal','Low','Medium','High')
$enumImpact     = @('Unknown','Negligible','Low','Medium','High')
$enumRestart    = @('None','AppRestart','ExplorerRestart','SignOut','Reboot')
$enumCategory   = @('CPU','GPU','Memória','Armazenamento','Windows','Gaming','Rede','Energia','Privacidade','Advanced Lab')
$enumRegKind    = @('DWord','QWord','String','ExpandString','MultiString','Binary')
$enumStartMode  = @('Boot','System','Automatic','AutomaticDelayed','Manual','Disabled')
$enumCpuVendor  = @('Unknown','Intel','Amd','Other')
$enumGpuVendor  = @('Unknown','Nvidia','Amd','Intel','Microsoft','Other')
$enumFormFactor = @('Unknown','Desktop','Laptop')
$allowedBcd     = @('useplatformclock','useplatformtick','disabledynamictick','tscsyncpolicy')
$protectedServices = @(
    'WinDefend','WdNisSvc','WdNisDrv','WdFilter','WdBoot','Sense','SecurityHealthService','wscsvc','mpssvc','BFE',
    'wuauserv','UsoSvc','WaaSMedicSvc','BITS','DoSvc','TrustedInstaller','msiserver','CryptSvc','KeyIso','VaultSvc',
    'RpcSs','RpcEptMapper','DcomLaunch','EventLog','Winmgmt','Dnscache','AudioSrv','AudioEndpointBuilder',
    'Schedule','PlugPlay','Power','ProfSvc','gpsvc','LSM','SamSs','BrokerInfrastructure','SystemEventsBroker',
    'CoreMessagingRegistrar','StateRepository','AppIDSvc','EFS','Netlogon','NlaSvc','nsi','Dhcp','WlanSvc','mpsdrv'
)
$guidRx = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
$idRx = '^[a-z0-9]+(\.[a-z0-9][a-z0-9-]*)+$'

function Get-IdentityKey($t, [string]$ctx) {
    switch ($t.type) {
        'registry' { return ('reg:{0}\{1}\{2}' -f $t.hive, ([string]$t.path).TrimEnd('\'), $t.name).ToUpperInvariant() }
        'power'    { return ('power:{0}:{1}:{2}' -f $t.scheme, $t.subgroup, $t.setting).ToUpperInvariant() }
        'service'  { return ('svc:{0}' -f $t.serviceName).ToUpperInvariant() }
        'bcd'      { return ('bcd:{{current}}:{0}' -f $t.element).ToUpperInvariant() }
        'task'     { return ('task:{0}' -f $t.taskPath).ToUpperInvariant() }
        default    { Err "$ctx : tipo de alvo desconhecido '$($t.type)'"; return "unknown:$([guid]::NewGuid())" }
    }
}

function Test-Decimal([string]$s, [bool]$isQword) {
    if ($s -notmatch '^\d+$') { return $false }
    $v = [decimal]$s
    if ($isQword) { return $v -le [decimal]::Parse('18446744073709551615') }
    return $v -le 4294967295
}

function Test-Target($t, [string]$ctx, [bool]$isDefaultOption) {
    $first = @($t.PSObject.Properties)[0].Name
    if ($first -ne 'type') { Err "$ctx : o discriminador 'type' deve ser a primeira propriedade (encontrado '$first')" }
    switch ($t.type) {
        'registry' {
            if (@('HKLM','HKCU') -notcontains $t.hive) { Err "$ctx : hive inválido '$($t.hive)'" }
            if ([string]::IsNullOrWhiteSpace($t.path)) { Err "$ctx : path vazio" }
            if (-not (Has-Prop $t 'name')) { Err "$ctx : name ausente" }
            if ($enumRegKind -notcontains $t.kind) { Err "$ctx : kind inválido '$($t.kind)'" }
            if (-not (Has-Prop $t 'value')) { Err "$ctx : propriedade 'value' ausente (use null para ausente)" }
            $vals = @($t.value)
            if (Has-Prop $t 'equivalents') { $vals += @($t.equivalents) }
            if ($t.kind -eq 'DWord' -or $t.kind -eq 'QWord') {
                foreach ($v in $vals) {
                    if ($null -ne $v -and -not (Test-Decimal ([string]$v) ($t.kind -eq 'QWord'))) { Err "$ctx : valor '$v' não é decimal válido para $($t.kind)" }
                }
            }
            foreach ($v in $vals) { if ($null -ne $v -and -not ($v -is [string])) { Err "$ctx : valores de registro devem ser strings (encontrado $($v.GetType().Name))" } }
        }
        'power' {
            if ([string]::IsNullOrWhiteSpace($t.scheme)) { Err "$ctx : scheme vazio" }
            elseif ($t.scheme -ne 'active' -and $t.scheme -notmatch $guidRx) { Err "$ctx : scheme deve ser 'active' ou GUID" }
            if ($t.subgroup -notmatch $guidRx) { Err "$ctx : subgroup não é GUID" }
            if ($t.setting -notmatch $guidRx) { Err "$ctx : setting não é GUID" }
            foreach ($p in 'ac','dc') {
                if (-not (Has-Prop $t $p)) { Err "$ctx : '$p' ausente"; continue }
                $v = $t.$p
                if ($null -ne $v) {
                    if (-not ($v -is [int] -or $v -is [long] -or $v -is [decimal] -or $v -is [double])) { Err "$ctx : '$p' deve ser inteiro" }
                    elseif ($v -lt 0 -or $v -gt 4294967295 -or [math]::Floor([double]$v) -ne [double]$v) { Err "$ctx : '$p' fora de uint32" }
                }
                elseif ($isDefaultOption) { Err "$ctx : a opção padrão deve definir '$p' (null não reproduz o padrão)" }
            }
        }
        'service' {
            if ([string]::IsNullOrWhiteSpace($t.serviceName)) { Err "$ctx : serviceName vazio" }
            elseif ($protectedServices -contains $t.serviceName) { Err "$ctx : serviço protegido '$($t.serviceName)' não pode ser alvo" }
            if ($enumStartMode -notcontains $t.startMode) { Err "$ctx : startMode inválido '$($t.startMode)'" }
            if (@('Boot','System') -contains $t.startMode) { Err "$ctx : startMode '$($t.startMode)' não permitido" }
        }
        'bcd' {
            if ($allowedBcd -notcontains $t.element) { Err "$ctx : elemento BCD não permitido '$($t.element)'" }
            if (-not (Has-Prop $t 'value')) { Err "$ctx : 'value' ausente (use null para removido)" }
        }
        'task' {
            if (-not ([string]$t.taskPath).StartsWith('\')) { Err "$ctx : taskPath deve começar com '\'" }
            if (-not ($t.enabled -is [bool])) { Err "$ctx : 'enabled' deve ser booleano" }
        }
    }
}

# ---------------------------------------------------------------- tweaks
$tweakFiles = Get-ChildItem -Path $CatalogDir -Filter 'tweaks.*.json' | Sort-Object Name
if ($tweakFiles.Count -eq 0) { Err "Nenhum arquivo tweaks.*.json em $CatalogDir" }

$allTweaks = @{}
$order = New-Object System.Collections.Generic.List[object]

foreach ($f in $tweakFiles) {
    try { $doc = Read-Json $f.FullName } catch { Err "$($f.Name): JSON inválido - $($_.Exception.Message)"; continue }
    if ($doc.schemaVersion -ne 1) { Err "$($f.Name): schemaVersion deve ser 1" }
    if (-not (Has-Prop $doc 'tweaks')) { Err "$($f.Name): propriedade 'tweaks' ausente"; continue }

    foreach ($tw in @($doc.tweaks)) {
        $id = [string]$tw.id
        $ctx = "$($f.Name)::$id"
        if ($id -notmatch $idRx) { Err "$ctx : id fora do formato minúsculo pontuado" }
        if ($allTweaks.ContainsKey($id)) { Err "$ctx : id duplicado (também em $($allTweaks[$id].File))" ; continue }
        $allTweaks[$id] = [pscustomobject]@{ File = $f.Name; Tweak = $tw }
        $order.Add($tw)

        foreach ($req in 'name','category','technicalDescription','plainExplanation','evidence','references','grade','confidence','risk','impact','sideEffects','restart','dependsOn','conflictsWith','compatibility','options','defaultOption','recommendedOption','experimental','privacy','tags') {
            if (-not (Has-Prop $tw $req)) { Err "$ctx : campo obrigatório '$req' ausente" }
        }
        foreach ($lt in 'name','technicalDescription','plainExplanation','evidence','sideEffects') {
            if ([string]::IsNullOrWhiteSpace((Get-LocalizedPt $tw.$lt))) { Err "$ctx : '$lt' sem texto pt" }
        }
        if ($enumCategory -notcontains $tw.category) { Err "$ctx : categoria inválida '$($tw.category)'" }
        if ($enumGrade -notcontains $tw.grade) { Err "$ctx : grade inválida '$($tw.grade)'" }
        if ($enumConfidence -notcontains $tw.confidence) { Err "$ctx : confidence inválida '$($tw.confidence)'" }
        if ($enumRisk -notcontains $tw.risk) { Err "$ctx : risk inválido '$($tw.risk)'" }
        if ($enumImpact -notcontains $tw.impact) { Err "$ctx : impact inválido '$($tw.impact)'" }
        if ($enumRestart -notcontains $tw.restart) { Err "$ctx : restart inválido '$($tw.restart)'" }
        foreach ($r in @($tw.references)) { if ($null -ne $r -and ([string]$r) -notmatch '^https://') { Err "$ctx : referência não é URL https '$r'" } }
        if (@('C','D','E') -contains $tw.grade -and $null -ne $tw.recommendedOption) { Err "$ctx : recommendedOption deve ser null para nota $($tw.grade)" }
        if (($tw.category -eq 'Advanced Lab') -ne [bool]$tw.experimental) { Err "$ctx : 'Advanced Lab' e experimental=true devem andar juntos" }
        if (($tw.category -eq 'Privacidade') -ne [bool]$tw.privacy) { Err "$ctx : categoria 'Privacidade' e privacy=true devem andar juntos" }

        $c = $tw.compatibility
        if ($null -ne $c) {
            if ((Has-Prop $c 'cpuVendor') -and $null -ne $c.cpuVendor -and $enumCpuVendor -notcontains $c.cpuVendor) { Err "$ctx : cpuVendor inválido" }
            if ((Has-Prop $c 'gpuVendor') -and $null -ne $c.gpuVendor -and $enumGpuVendor -notcontains $c.gpuVendor) { Err "$ctx : gpuVendor inválido" }
            if ((Has-Prop $c 'formFactor') -and $null -ne $c.formFactor -and $enumFormFactor -notcontains $c.formFactor) { Err "$ctx : formFactor inválido" }
            if ((Has-Prop $c 'maxBuild') -and $c.maxBuild -gt 0 -and (Has-Prop $c 'minBuild') -and $c.maxBuild -lt $c.minBuild) { Err "$ctx : maxBuild < minBuild" }
        }

        $opts = @($tw.options)
        if ($opts.Count -lt 2) { Err "$ctx : pelo menos 2 opções são necessárias" }
        $keys = @($opts | ForEach-Object { $_.key })
        if (($keys | Sort-Object -Unique).Count -ne $keys.Count) { Err "$ctx : chaves de opção duplicadas" }
        if ($keys -notcontains $tw.defaultOption) { Err "$ctx : defaultOption '$($tw.defaultOption)' não existe" }
        if ($null -ne $tw.recommendedOption -and $keys -notcontains $tw.recommendedOption) { Err "$ctx : recommendedOption '$($tw.recommendedOption)' não existe" }

        $refSet = $null; $refKey = $null
        foreach ($o in $opts) {
            $octx = "$ctx[$($o.key)]"
            if ([string]::IsNullOrWhiteSpace((Get-LocalizedPt $o.label))) { Err "$octx : label sem texto" }
            $targets = @($o.targets)
            if ($targets.Count -eq 0) { Err "$octx : sem alvos"; continue }
            $ids = @()
            foreach ($t in $targets) {
                Test-Target $t $octx ($o.key -eq $tw.defaultOption)
                $ids += (Get-IdentityKey $t $octx)
            }
            if (($ids | Sort-Object -Unique).Count -ne $ids.Count) { Err "$octx : alvo repetido dentro da opção" }
            $set = (($ids | Sort-Object) -join '|')
            if ($null -eq $refSet) { $refSet = $set; $refKey = $o.key }
            elseif ($set -ne $refSet) { Err "$octx : conjunto de alvos difere da opção '$refKey'" }
        }
        # Opções devem diferir entre si em algum valor.
        $sigs = @{}
        foreach ($o in $opts) {
            $sig = (@($o.targets) | ForEach-Object { ($_ | ConvertTo-Json -Compress -Depth 5) }) -join '|'
            if ($sigs.ContainsKey($sig)) { Err "$ctx : opções '$($sigs[$sig])' e '$($o.key)' são idênticas" } else { $sigs[$sig] = $o.key }
        }
    }
}

# dependsOn / conflictsWith
foreach ($id in $allTweaks.Keys) {
    $tw = $allTweaks[$id].Tweak
    foreach ($d in @($tw.dependsOn) + @($tw.conflictsWith)) {
        if ($null -ne $d -and -not $allTweaks.ContainsKey([string]$d)) { Err "${id} : referência a tweak inexistente '$d'" }
    }
}

# Mesmo alvo em dois tweaks diferentes = conflito de propriedade (o snapshot de um sobrescreveria o outro).
$targetOwner = @{}
foreach ($id in $allTweaks.Keys) {
    $tw = $allTweaks[$id].Tweak
    $first = @($tw.options)[0]
    foreach ($t in @($first.targets)) {
        $k = Get-IdentityKey $t $id
        if ($targetOwner.ContainsKey($k) -and $targetOwner[$k] -ne $id) { Err "Alvo $k declarado por '$($targetOwner[$k])' e '$id'" }
        else { $targetOwner[$k] = $id }
    }
}

# ---------------------------------------------------------------- profiles
$profilesPath = Join-Path $CatalogDir 'profiles.json'
if (-not (Test-Path $profilesPath)) { Err "profiles.json ausente" }
else {
    try { $pdoc = Read-Json $profilesPath } catch { Err "profiles.json: JSON inválido - $($_.Exception.Message)"; $pdoc = $null }
    if ($null -ne $pdoc) {
        if ($pdoc.schemaVersion -ne 1) { Err "profiles.json: schemaVersion deve ser 1" }
        $required = @('default','safe','balanced','competitive','workstation','laptop','experimental','gaming-competitive','gaming-balanced','gaming-visual','gaming-lowend','gaming-laptop')
        $pids = @($pdoc.profiles | ForEach-Object { $_.id })
        foreach ($r in $required) { if ($pids -notcontains $r) { Err "profiles.json: perfil obrigatório '$r' ausente" } }
        if (($pids | Sort-Object -Unique).Count -ne $pids.Count) { Err "profiles.json: ids de perfil duplicados" }
        foreach ($p in @($pdoc.profiles)) {
            $pctx = "profiles.json::$($p.id)"
            if ([string]::IsNullOrWhiteSpace((Get-LocalizedPt $p.name))) { Err "$pctx : nome sem texto" }
            if (@('system','gaming') -notcontains $p.kind) { Err "$pctx : kind inválido '$($p.kind)'" }
            if ((Has-Prop $p 'conditions') -and $null -ne $p.conditions -and $null -ne $p.conditions.formFactor -and @('Laptop','Desktop') -notcontains $p.conditions.formFactor) { Err "$pctx : formFactor inválido" }
            if (@('default','experimental') -contains $p.id -and @($p.entries).Count -gt 0) { Err "$pctx : deve ser vazio" }
            $seen = @{}
            foreach ($e in @($p.entries)) {
                if ($null -eq $e) { continue }
                $ectx = "$pctx -> $($e.tweakId)"
                if ($seen.ContainsKey($e.tweakId)) { Err "$ectx : tweak repetido no perfil" } else { $seen[$e.tweakId] = 1 }
                if (-not $allTweaks.ContainsKey([string]$e.tweakId)) { Err "$ectx : tweak inexistente"; continue }
                $tw = $allTweaks[[string]$e.tweakId].Tweak
                if (@($tw.options | ForEach-Object { $_.key }) -notcontains $e.option) { Err "$ectx : opção '$($e.option)' inexistente" }
                if (@('A','B') -notcontains $tw.grade) { Err "$ectx : perfis só podem usar notas A/B (nota $($tw.grade))" }
                if ($tw.experimental) { Err "$ectx : tweaks experimentais não podem estar em perfis" }
                if ([string]::IsNullOrWhiteSpace((Get-LocalizedPt $e.rationale))) { Err "$ectx : rationale vazio" }
                $cf = $tw.compatibility.formFactor
                $pf = if ((Has-Prop $p 'conditions') -and $null -ne $p.conditions) { $p.conditions.formFactor } else { $null }
                if ($null -ne $cf -and $cf -ne 'Unknown' -and $null -ne $pf -and $cf -ne $pf) { Err "$ectx : tweak restrito a $cf em perfil $pf" }
            }
        }
    }
}

# ---------------------------------------------------------------- resumo
$byCat = $order | Group-Object category | Sort-Object Name | ForEach-Object { '{0}={1}' -f $_.Name, $_.Count }
$byGrade = $order | Group-Object grade | Sort-Object Name | ForEach-Object { '{0}={1}' -f $_.Name, $_.Count }
Write-Host ("Tweaks: {0} em {1} arquivos" -f $order.Count, $tweakFiles.Count)
Write-Host ("Por categoria: " + ($byCat -join ', '))
Write-Host ("Por nota: " + ($byGrade -join ', '))

if ($GenerateCatalogMd) {
    $md = New-Object System.Text.StringBuilder
    [void]$md.AppendLine('# Catálogo de tweaks do RBM Optimizer')
    [void]$md.AppendLine('')
    [void]$md.AppendLine('> Arquivo gerado por `build/validate-catalog.ps1 -GenerateCatalogMd` a partir de `src/Rbm.Optimization/Catalog/tweaks.*.json`. Não edite manualmente; a justificativa de cada item está em [TWEAK_RESEARCH.md](TWEAK_RESEARCH.md).')
    [void]$md.AppendLine('')
    [void]$md.AppendLine(('Total: **{0}** tweaks. Por nota: {1}. Por categoria: {2}.' -f $order.Count, ($byGrade -join ', '), ($byCat -join ', ')))
    [void]$md.AppendLine('')
    [void]$md.AppendLine('Legenda de notas: **A** benefício demonstrado · **B** plausível/dependente do sistema · **C** experimental/evidência insuficiente · **D** sem benefício demonstrado/placebo · **E** obsoleto/inseguro/contraindicado (o app bloqueia a aplicação). "—" em *Recomendado* = manter o padrão do Windows.')
    [void]$md.AppendLine('')
    $catOrder = @('CPU','GPU','Memória','Armazenamento','Windows','Gaming','Rede','Energia','Privacidade','Advanced Lab')
    foreach ($cat in $catOrder) {
        $items = @($order | Where-Object { $_.category -eq $cat })
        if ($items.Count -eq 0) { continue }
        [void]$md.AppendLine("## $cat")
        [void]$md.AppendLine('')
        [void]$md.AppendLine('| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |')
        [void]$md.AppendLine('|---|---|:-:|---|---|---|---|---|')
        foreach ($tw in $items) {
            $rec = if ($null -eq $tw.recommendedOption) { '—' } else { '`' + $tw.recommendedOption + '`' }
            $name = (Get-LocalizedPt $tw.name) -replace '\|', '\|'
            [void]$md.AppendLine(('| `{0}` | {1} | {2} | {3} | {4} | {5} | `{6}` | {7} |' -f $tw.id, $name, $tw.grade, $tw.risk, $tw.impact, $tw.restart, $tw.defaultOption, $rec))
        }
        [void]$md.AppendLine('')
    }
    if (Test-Path $profilesPath) {
        $pdoc2 = Read-Json $profilesPath
        [void]$md.AppendLine('## Perfis')
        [void]$md.AppendLine('')
        [void]$md.AppendLine('| Id | Nome | Tipo | Condição | Tweaks (opção) |')
        [void]$md.AppendLine('|---|---|---|---|---|')
        foreach ($p in @($pdoc2.profiles)) {
            $ff = if ($null -ne $p.conditions -and $null -ne $p.conditions.formFactor) { $p.conditions.formFactor } else { '—' }
            $ents = @($p.entries | ForEach-Object { '`{0}`={1}' -f $_.tweakId, $_.option }) -join '<br>'
            if ([string]::IsNullOrEmpty($ents)) { $ents = '(vazio)' }
            [void]$md.AppendLine(('| `{0}` | {1} | {2} | {3} | {4} |' -f $p.id, (Get-LocalizedPt $p.name), $p.kind, $ff, $ents))
        }
        [void]$md.AppendLine('')
    }
    $out = Join-Path $root 'docs\TWEAK_CATALOG.md'
    [IO.File]::WriteAllText($out, $md.ToString(), (New-Object System.Text.UTF8Encoding $false))
    Write-Host "Gerado: $out"
}

foreach ($w in $warnings) { Write-Host "AVISO: $w" -ForegroundColor Yellow }
if ($errors.Count -gt 0) {
    foreach ($e in $errors) { Write-Host "ERRO: $e" -ForegroundColor Red }
    Write-Host ("FALHOU: {0} erro(s)." -f $errors.Count) -ForegroundColor Red
    exit 1
}
Write-Host 'OK: catálogo válido.' -ForegroundColor Green
exit 0
