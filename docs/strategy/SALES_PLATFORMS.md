# Plataformas de venda — Wintal (produto digital B2C low-ticket)

**Escopo:** produto digital baixável (EXE Windows), ticket R$ 47–197, público majoritariamente Brasil.
**Data:** julho/2026

---

## Resumo executivo — 3 escolhas por perfil

| Perfil | Escolha | Motivo |
|---|---|---|
| **Começar hoje, foco Brasil** | **Kiwify** | PIX nativo, taxas competitivas, checkout PT-BR, envio automático de chave |
| **Marketplace com tráfego** | **Hotmart** | Tem afiliados, mas UX de comprador saturada |
| **Escalar internacional depois** | **LemonSqueezy** ou **Paddle** | Merchant of Record — trata imposto/IOF global |

---

## Comparativo detalhado (produto digital único, sem assinatura)

### 🇧🇷 Nacionais

| Plataforma | Taxa por venda | Métodos | Chave por e-mail | Split | UX check | Suporte PT-BR | Nota |
|---|---|---|---|---|---|---|---|
| **Kiwify** | 4,99% + R$ 1 (Cartão) · 4,99% (Pix) · 5,49% (Boleto) | Cartão, PIX, Boleto, PayPal | ✅ nativo | ✅ | 9/10 | ✅ | **Melhor 1ª escolha** |
| **Hotmart** | 9,9% + R$ 1 (Cartão) · 4,99% + R$ 1 (PIX) | Cartão, PIX, Boleto, PayPal, 2 cartões | ✅ nativo | ✅ | 6/10 (poluída) | ✅ | Bom se quiser afiliados |
| **Eduzz** | 9,9% + R$ 2 | Cartão, PIX, Boleto | ✅ | ✅ | 7/10 | ✅ | Foco em curso, menos p/ software |
| **Monetizze** | 8,9% + R$ 1,90 | Cartão, PIX, Boleto | ✅ | ✅ | 6/10 | ✅ | Nicho info-produto |
| **Perfectpay** | 6,99% + R$ 1 | Cartão, PIX, Boleto | ✅ | ✅ | 7/10 | ✅ | Alternativa a Kiwify |
| **Braip** | 5,9% + R$ 1 | Cartão, PIX, Boleto | ✅ | ✅ | 6/10 | ✅ | Menos maduro |

### 🌎 Internacionais (Merchant of Record — resolvem imposto/IOF)

| Plataforma | Taxa por venda | Métodos | Vantagem única | Nota |
|---|---|---|---|---|
| **LemonSqueezy** | 5% + $0,50 | Cartão, PayPal, Apple/Google Pay | MoR, gerencia VAT global, ótima API, dashboard limpo | **Melhor para escalar fora** |
| **Paddle** | 5% + $0,50 (Classic) · plano em USD | Cartão, PayPal, Apple/Google Pay | MoR maduro, muito usado por SaaS | Vale se >US$ 5k/mês |
| **FastSpring** | 5,9% + $0,95 | Cartão, PayPal | MoR clássico do mundo shareware | Legado, ainda funciona |
| **Gumroad** | 10% + $0,30 | Cartão, PayPal | Simples pra começar | Taxa alta comparada |
| **Stripe** | 3,4% + $0,30 (BR) | Cartão | Melhor margem, mas você é MoR | Só se tiver LLC/PJ dedicada |
| **Payhip** | 5% (Free) · $29/mês (Plus 0%) | Cartão, PayPal, Bitcoin | Barato + license keys nativo | Boa 2ª opção |
| **SendOwl** | $18/mês + 0% ou % por plano | Cartão, PayPal | Serial keys, PDF stamp, hospedagem do binário | Legado, ainda ok |

---

## Decisão: começar com **Kiwify** (BR) + preparar **LemonSqueezy** (global)

**Racional:**
1. Kiwify tem taxa quase 50% menor que Hotmart no Cartão
2. Chave enviada por e-mail via **Webhook** ou **Automação** integrada
3. Suporte a **PIX** direto (55% dos compradores BR preferem)
4. Checkout traduzido, rápido, mobile-friendly
5. LemonSqueezy fica pronto para o dia em que aparecer venda gringa

**Trade-off aceito:** Kiwify não tem marketplace com tráfego próprio como Hotmart. Estratégia é **trazer o próprio tráfego** via LinkedIn/Reddit/YouTube (já mapeado em `GO_TO_MARKET.md`).

---

## Sistema de licença (backend leve)

**MVP em 3 partes:**

1. **Gerador de chave** (offline)
   - Formato: `WTL-XXXX-XXXX-XXXX-XXXX`
   - Payload assinado com HMAC-SHA256 usando secret privado
   - Payload contém: e-mail, tier, timestamp de compra, deviceLimit

2. **Ativação no app**
   - Usuário cola a chave na tela de ativação
   - App valida assinatura localmente (sem servidor no MVP)
   - Grava chave hasheada em `%LOCALAPPDATA%\Wintal\license.dat`

3. **Anti-piracy leve**
   - Hash da máquina (CPU + placa-mãe) + chave = identidade única
   - Se mesma chave em >3 máquinas, app abre em modo "somente leitura"
   - Sem servidor central no MVP; futuramente adicionar validação online opcional

**Integração Kiwify:**
- Webhook `purchase.approved` → função serverless (Cloudflare Worker) gera chave HMAC
- Kiwify envia e-mail automático com a chave no template

---

## Cálculo de custo real por venda (R$ 47 tier Pessoal)

| Plataforma | Taxa | Você recebe | Margem sobre R$ 47 |
|---|---|---|---|
| **Kiwify (PIX)** | R$ 2,35 (4,99%) | R$ 44,65 | 95% |
| **Kiwify (Cartão)** | R$ 3,35 (5%+R$1) | R$ 43,65 | 93% |
| **Hotmart (Cartão)** | R$ 5,65 (9,9%+R$1) | R$ 41,35 | 88% |
| **Gumroad (USD)** | R$ 6,00 (10%+$0,30) | R$ 41,00 | 87% |

Diferença Kiwify vs Hotmart em 500 vendas anuais = **R$ 1.150 líquidos a mais** (só na plataforma).

---

## Setup em 1 dia (roteiro prático)

1. **Kiwify** (2h): criar conta, cadastrar produto "Wintal Pessoal - Licença Vitalícia", subir imagem/copy, configurar checkout, testar compra em modo sandbox
2. **Webhook + gerador de chave** (3h): Cloudflare Worker + secret, testar
3. **Template de e-mail** (30min): "Bem-vindo ao Wintal — sua chave é XXXX. Baixe em: link. Manual: link."
4. **Landing page** (4h): Framer/Astro/Cardpress + copy da GO_TO_MARKET + botão CTA → Kiwify
5. **Lançamento soft**: post LinkedIn + envio para 10 amigos técnicos

**Meta primeira semana:** 10 vendas orgânicas para validar o funil ponta a ponta.
