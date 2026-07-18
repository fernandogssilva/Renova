# Auditoria de Segurança — SysMaintenanceHub v1.2

**Escopo:** binário WPF .NET 8 rodando como Administrador, com integração PowerShell, consultas HTTP externas e escrita em Registry.

**Data:** 18/07/2026 · **Auditor:** análise interna DataSec

---

## Superfície de ataque

| Vetor | Componente | Risco base |
|---|---|---|
| Elevação (admin) | app.manifest | crítico se houver EoP local |
| Execução de scripts PS | PowerShellRunner | injeção de comando |
| HTTP externo | MicrosoftCatalogService, VulnerabilityService | MITM, envenenamento |
| Registry write | StartupAppsService | modificação indevida |
| File I/O amplo (delete) | CleanupService | symlink attack |
| Instalação de módulo PS | PSWindowsUpdate via Install-Module | supply chain |
| Log em disco | Serilog | vazamento de PII |

---

## Achados

### 🔴 CRÍTICO 1 — PowerShellRunner: script gravado em `%TEMP%` sem restrição de ACL

**Antes:**
```csharp
var scriptFile = Path.Combine(Path.GetTempPath(), $"smh_{Guid.NewGuid():N}.ps1");
await File.WriteAllTextAsync(scriptFile, script, ...);
```
`%TEMP%` do usuário é escrivível por outros processos do mesmo usuário. Um malware **sem admin** poderia observar o arquivo aparecer e substituir o conteúdo antes do `powershell.exe` (elevado) executá-lo → EoP.

**Correção:** gravar em pasta privada do app (`%LOCALAPPDATA%\SysMaintenanceHub\.ps`) com ACL restrita a Administrators + SYSTEM, e usar handle exclusivo durante o WriteAll.

### 🔴 CRÍTICO 2 — Injeção via parâmetro `KBArticleID`

**Antes:**
```csharp
var script = $@"Get-WindowsUpdate ... -KBArticleID {kbNumber} -Install ...";
```
Se `kb` viesse de uma fonte não-confiável (ex: futura importação de arquivo, ou API externa), um valor `123; Remove-Item C:\\ -Recurse` executaria comando arbitrário elevado.

**Correção:** whitelist regex `^\d{5,8}$` antes de interpolar; rejeitar tudo mais.

### 🟡 ALTO 3 — HTTPS sem cert-pinning nem timeout curto

`HttpClient` usa validação padrão do sistema. Se um CA raiz for comprometido (via malware injetando cert no store do usuário), MITM viabiliza envenenamento do release-health/MSRC.

**Mitigação:** adicionar `ServerCertificateCustomValidationCallback` conferindo o thumbprint dos CAs Microsoft esperados; timeout de 15s (já feito) para reduzir janela de ataque.

### 🟡 ALTO 4 — `Install-Module PSWindowsUpdate -Force` sem pinning de versão

**Antes:**
```powershell
Install-Module -Name PSWindowsUpdate -Force -AllowClobber -Confirm:$false
```
Baixa sempre a última versão da PSGallery. Um ataque de supply chain à PSGallery instala código malicioso em milhões de máquinas elevadas.

**Correção:** pinar `-RequiredVersion 2.2.1.5` (última versão auditada) e verificar assinatura Authenticode antes de importar.

### 🟡 ALTO 5 — Delete recursivo sem verificação de symlink/junction

`CleanupService.CleanAsync` faz `Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)`. Um atacante local pode plantar uma junction dentro de `%TEMP%\` apontando para `C:\Users\ferna\Documents` — o app, elevado, deletará documentos legítimos.

**Correção:** enumerar sem seguir reparse points; verificar `(FileAttributes & FileAttributes.ReparsePoint) == 0`.

### 🟢 MÉDIO 6 — Logs podem gravar caminhos com nome do usuário

Nomes de arquivo em `%USERPROFILE%\...` acabam nos logs. Se o usuário compartilhar logs por suporte, expõe identidade + estrutura do disco.

**Correção:** sanitizar caminhos substituindo `C:\Users\<nome>\` por `C:\Users\%USER%\` no formato de log.

### 🟢 MÉDIO 7 — EXE não assinado

Windows SmartScreen bloqueia por padrão. Usuários médios não vão passar pelo aviso "Aplicativo desconhecido".

**Correção:** obter certificado Code Signing (Sectigo ~R$ 800/ano; DigiCert ~R$ 1500/ano) e assinar via `signtool.exe`.

### 🔵 BAIXO 8 — Falta hash SHA-256 público das releases

Usuários que baixam da GitHub Release não têm como validar integridade além do próprio HTTPS.

**Correção:** publicar `.sha256` junto ao `.exe` no release.

---

## Fixes aplicados nesta versão

- [x] **CRÍTICO 1** — PowerShellRunner grava em pasta privada com ACL
- [x] **CRÍTICO 2** — Validação regex do KB antes de shell
- [x] **ALTO 4** — Pinning de versão do PSWindowsUpdate
- [x] **ALTO 5** — Skip reparse points na limpeza
- [x] **MÉDIO 6** — Sanitização de caminhos nos logs
- [x] **BAIXO 8** — Script publish gera `.sha256` automaticamente

## Pendentes (fora do escopo deste PR)

- [ ] **ALTO 3** — Cert pinning: exige lista de thumbprints Microsoft (a ser mapeada)
- [ ] **MÉDIO 7** — Certificado Code Signing (custo + burocracia)

---

**Assinatura da auditoria:** DataSec — 2026-07-18
