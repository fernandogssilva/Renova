# Roadmap de Produto — SysMaintenanceHub

**Framework:** RICE (Reach · Impact · Confidence · Effort)
**Data:** julho/2026

---

## Now (v1.3 — próximos 30 dias)

| # | Feature | R | I | C | E | RICE | Status |
|---|---|---|---|---|---|---|---|
| 1 | **Splash + First-run wizard** (explica manifesto UAC, cria backup) | 100% | 3 | 100% | 3 | 100 | 🔵 planejar |
| 2 | **Botão "Criar ponto de restauração"** antes de "Executar tudo" | 100% | 3 | 100% | 2 | 150 | 🔵 planejar |
| 3 | **Exportar relatório** (HTML/PDF com KPIs, CVEs, ações executadas) | 60% | 2 | 90% | 3 | 36 | 🔵 planejar |
| 4 | **System Tray + notificação** quando aparecer CVE crítica | 40% | 3 | 80% | 5 | 19 | 🟡 avaliar |

## Next (v2.0 — 60–90 dias)

| # | Feature | Justificativa |
|---|---|---|
| 5 | Agendamento nativo via Task Scheduler | evita usuário lembrar de abrir |
| 6 | Modo "auditor" (só leitura) — sem admin | uso em máquinas de terceiros |
| 7 | Perfis salvos ("Gaming", "Trabalho", "Manutenção Total") | fluxo em 2 cliques |
| 8 | Suporte a servidor de licenças + telemetria opt-in | monetização |

## Later (v3.x — 6+ meses)

- Console web multi-máquina (tier Empresarial)
- Integração com Slack / Teams / Discord (webhook de CVE crítica)
- Dashboard de compliance (LGPD/ISO 27001)
- Verificação de driver via Windows Update for Business

---

## Melhorias imediatas de UX (baixo esforço, alto retorno)

### 1. Primeira execução — sem "tela vazia"
Hoje o usuário abre o app e vê tudo em 0 até o refresh terminar. **Fix:** mostrar skeleton loaders nos cards durante o refresh inicial.

### 2. Botões grandes de ação primária
"Executar tudo" precisa ser 2× mais visível. Hoje divide espaço com "Atualizar", "Cancelar", "Tema".

### 3. Confirmação clara em ações destrutivas
Instalar patch de CVE ou "Executar tudo" → modal com resumo do que vai acontecer + botão "Prosseguir sem perguntar novamente por 24h".

### 4. Estado vazio significativo
"Nenhuma vulnerabilidade pendente" hoje é um DataGrid vazio. **Fix:** ilustração + "Sistema em dia — última checagem X min atrás".

### 5. Copy amigável nos logs
"[ERR] Get-WindowsUpdate: The operation is not supported" → traduzir para "Não foi possível consultar o Windows Update. Verifique sua conexão."

### 6. Atalhos de teclado
- `F5` — Refresh
- `Ctrl+E` — Executar tudo
- `Ctrl+L` — Alternar Log
- `Ctrl+Shift+T` — Alternar tema

### 7. Persistir posição/tamanho da janela
Reabrir onde o usuário deixou.

### 8. Modo compacto (mini-widget)
Janela pequena com só os 6 KPIs, encaixável no canto da tela.

---

## Métricas de sucesso do produto

- Time-to-first-value (abrir → ver 1º KPI): **< 3s** (hoje ~15s por causa dos scans síncronos)
- Retenção D7: > 40%
- Ações por sessão: > 2
- Frequência de uso: > 1/semana
