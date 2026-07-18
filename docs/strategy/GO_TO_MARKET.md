# Go-to-Market — SysMaintenanceHub

**Data:** julho/2026
**Autor:** DataSec — Fernando Silva
**Status:** rascunho v0.1

---

## 1. Posicionamento

**Categoria:** Utilitário Windows • Manutenção & Segurança
**Mercado-alvo primário:** Brasil (público falante de português)
**Persona-alvo:** usuário Windows técnico o suficiente para se preocupar com performance/segurança, mas cansado de:
- Ter que abrir 4 telas diferentes (Settings, PowerShell, DiskCleanup, Task Manager)
- Consumir tutoriais no Reddit / YouTube para descobrir se está atualizado
- Concorrentes gratuitos que empurram bloatware (CCleaner, Advanced SystemCare)

**Frase-âncora:**
> "O painel único que roda Windows Update, Winget, limpeza e diagnóstico de segurança sem te pedir permissão dez vezes."

**Diferenciais competitivos:**
| Feature | SysMaintenanceHub | CCleaner Free | Glary Utilities | Windows Settings |
|---|---|---|---|---|
| Sem adware/bloatware | ✅ | ❌ | ❌ | ✅ |
| CVEs pendentes (MSRC) | ✅ | ❌ | ❌ | ❌ |
| Comparação vs. release-health oficial | ✅ | ❌ | ❌ | ❌ |
| Winget integrado | ✅ | ❌ | ❌ | Parcial |
| Sem "premium unlock" via popup | ✅ | ❌ | ❌ | N/A |
| Interface PT-BR nativa | ✅ | Parcial | ❌ | ✅ |
| Preço one-time | R$ 47 | Grátis+ | Grátis+ | Grátis |

---

## 2. Modelo de Precificação (Low-Ticket)

### Tier 1 — Free (isca de leads)
- Todas as funções de **leitura** (dashboard, KPIs, listagem de vulnerabilidades)
- Máx. 3 execuções de "Executar tudo" por mês
- Sem exportação de relatório
- Watermark discreto no rodapé

### Tier 2 — **Pessoal** — R$ 47 (pagamento único, vitalício, 1 dispositivo)
- Execuções ilimitadas
- Aplicar patch por CVE
- Exportação PDF/HTML dos relatórios
- Atualizações v1.x incluídas
- Suporte por e-mail

### Tier 3 — **Profissional** — R$ 97 (pagamento único, 3 dispositivos)
- Tudo do Pessoal
- CLI headless para agendamento (`SysMaintenanceHub.exe --schedule`)
- Notificações no tray quando aparecerem KBs críticas
- Prioridade no suporte

### Tier 4 — **Empresarial** — R$ 197/ano por até 25 dispositivos
- Console web centralizado (roadmap)
- Instalação silenciosa em massa via MSI/MDM
- Relatórios gerenciais
- SLA de suporte

**Racional do preço-âncora:**
- R$ 47 é abaixo da barreira psicológica de R$ 50, no ticket "impulso"
- CCleaner Pro cobra R$ 89/ano (recorrente); SystemCare Pro R$ 149/ano
- Foco: **converter em volume**, não em margem por venda
- Meta ano 1: 500 licenças Pessoais + 100 Profissionais = R$ 33.200 (bruto)

---

## 3. Funil de Aquisição

```
Topo (awareness)
├── LinkedIn (posts semanais: bug/vulnerabilidade recente + como o SMH resolve)
├── YouTube Shorts (60s por feature: "Winget update em 1 clique")
├── Reddit r/brdev e r/Windows_BR (posts pontuais e não-spam)
├── Blog DataSec (SEO: "como atualizar Windows 11 sem CCleaner")
└── Comparativos "SysMaintenanceHub vs CCleaner"

Meio (consideração)
├── Landing page (Framer ou Astro)
│   ├── Vídeo demo 90s
│   ├── Screenshots grandes do dashboard
│   ├── Depoimentos (após primeiros 20 clientes)
│   └── FAQ (Segurança? Bloatware? Reembolso?)
└── Free trial de 14 dias sem cartão

Fundo (conversão)
├── Botão "Comprar por R$ 47" na LP → checkout
├── Hotmart / Kiwify (plataforma nacional, split de PIX)
├── Alternativa: Gumroad (internacional, PayPal + cartão)
└── Ativação por chave e-mailada + servidor de validação simples

Pós-venda
├── E-mail de boas-vindas com 3 use-cases
├── Newsletter mensal (nova KB crítica + tips)
└── Upgrade Pessoal → Profissional em 3 meses (desconto R$ 30 no upgrade)
```

---

## 4. Canais de Venda (Brasil)

| Plataforma | Comissão | Prós | Contras | Recomendação |
|---|---|---|---|---|
| **Kiwify** | 9,9% + R$ 1 | PIX nativo, checkout PT-BR, whitelabel | Menos maduro que Hotmart | ✅ **Escolher** — 1ª tentativa |
| Hotmart | 9,9% | Marketplace com tráfego próprio, afiliados | UX de comprador saturada | 2ª opção |
| Gumroad | 10% | Global, cartão internacional | Sem PIX, taxação em USD | Se quiser vender fora do BR |
| Stripe direto | 3,99% + R$ 0,39 | Melhor margem | Precisa desenvolver checkout | Só depois de PMF |

**Escolha inicial:** Kiwify (produto digital PT-BR, PIX, split automático, envio de chave por e-mail).

---

## 5. Roadmap de Lançamento (12 semanas)

| Sem. | Entrega |
|---|---|
| 1 | Sistema de licença (chave + validação online opcional) — MVP |
| 2 | Landing page + copy + vídeo demo 90s |
| 3 | Setup Kiwify + integração com envio automático de chave |
| 4 | Beta fechado com 10 amigos técnicos (feedback + depoimentos) |
| 5 | Ajustes com base no beta |
| 6 | **Lançamento oficial** — post LinkedIn + Reddit + newsletter DataSec |
| 7-8 | Reels/Shorts semanais + AMA no Reddit |
| 9 | Primeira newsletter para base (nova KB crítica) |
| 10 | Otimização do funil (A/B na LP, botão CTA) |
| 11 | Programa de afiliados (10% para influenciador tech BR) |
| 12 | Análise: se >100 vendas, escalar; se <30, pivotar copy/canal |

---

## 6. KPIs

- **Conversão LP → checkout:** meta 3% (baseline SaaS BR)
- **Conversão checkout → paga:** meta 55% (baixa no ticket = alta conversão)
- **CAC**: ≤ R$ 15 (LTV/CAC 3:1 no tier Pessoal)
- **Refund rate:** ≤ 5%
- **NPS:** ≥ 40

---

## 7. Sinais Vermelhos (quando pivotar)

- Refund rate > 10% após 90 dias
- CAC > R$ 30 sustentado
- 3 tickets críticos de "quebrou meu Windows" em 30 dias
- Concorrente PT-BR direto surge com free tier equivalente

---

## 8. Próximas 3 Ações Concretas

1. **Registrar o domínio** `sysmaintenancehub.com.br` ou `datasec.app.br`
2. Escrever a **copy da LP** (headline, subheadline, 3 seções de features, FAQ, CTA)
3. Implementar o **sistema de chaves** (ver `docs/strategy/LICENSE_SYSTEM.md` — a criar)

---

© 2026 DataSec — documento interno / não distribuir
