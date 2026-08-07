# Progresso do Nexora

Varredura de 05/08/2026, do código — não dos relatórios.

## Estado em 5 linhas

1. **Tudo compila e todo teste passa**: 388 no backend (contra Postgres real) e 96 no frontend.
2. **O produto roda ponta a ponta contra dado real**: cadastrar empresa, logar, receber mensagem por
   webhook, responder, mover card, fechar venda e ver o dashboard — verificado por HTTP.
3. **O maior bloqueio é o Bloco B: nenhum número de WhatsApp real foi pareado até hoje.** Todo o
   caminho de entrega foi exercitado contra um cliente Evolution falso. Não se sabe se funciona
   com um celular de verdade.
4. Segundo bloqueio: o pipeline de CI existe e está publicado, mas **nunca se viu uma execução**.
5. Nada das etapas 12–17 começou; o produto está completo na fase 1 e vazio na fase 2.

## Placar

| Bloco | ✅ Pronto | 🟡 Parcial | ⬜ Não começou |
|---|---|---|---|
| A — desbloqueio e higiene | 4 | 1 | 0 |
| B — validação com telefone real | 0 | 0 | 4 |
| C — cadastro e onboarding | 3 | 0 | 0 |
| D — CI e testes de frontend | 4 | 1 | 0 |
| E — dados e dashboard | 8 | 0 | 0 |
| F — ajustes finos | 6 | 0 | 0 |
| G — funcionalidades novas | 0 | 1 | 5 |
| **Total** | **25** | **3** | **9** |

## Checklist

### Bloco A — desbloqueio e higiene
- [x] ✅ `meu-dia` alinhado, build limpo
- [x] ✅ `senhas-dev.sql` fora do disco e fora do histórico (o repositório nasceu depois da remoção)
- [🟡] Fábrica de design-time lança sem `NEXORA_CONN` — **falta teste que prove o throw**
- [x] ✅ Banco `nexora` órfão removido (confirmado no Postgres)
- [x] ✅ Varredura de segredos — refeita antes de publicar; só placeholders e credencial de container

### Bloco B — validação com telefone real
- [ ] ⬜ Número real pareado — nenhuma conexão tem `conectado_em`; a única "conectada" é a de
      demonstração, com número fictício
- [ ] ⬜ Mensagem do celular criando contato e conversa
- [ ] ⬜ Resposta chegando no celular com ACK até lido
- [ ] ⬜ Ciclo repetido com o serviço reiniciado

### Bloco C — cadastro e onboarding
- [x] ✅ Controller de cadastro protegido por chave em header, comparada em tempo constante
- [x] ✅ Onboarding derivado do estado — os 3 passos são perguntas ao banco, não flag
- [x] ✅ Tempo até a primeira mensagem, carimbado pelo webhook

### Bloco D — CI e testes de frontend
- [🟡] Pipeline escrito e publicado (build, Postgres em container, `ng build`, `ng test` headless) —
      **nunca se observou uma execução**. Era impossível antes (não havia repositório); agora é só
      olhar a aba Actions
- [x] ✅ Testes do semáforo, incluindo o caso das 21h em `chaveDia`
- [x] ✅ Paridade — hoje em **três** lados: C#, TypeScript e SQL, com o mesmo arquivo de casos
- [x] ✅ Renderização das 16 telas
- [x] ✅ Interceptor e guards

### Bloco E — dados e dashboard
- [x] ✅ Série temporal agregada no SQL, com períodos zerados presentes
- [x] ✅ Atividades recentes por cursor, recortadas por papel na API
- [x] ✅ Leads por origem (`GROUP BY` no SQL)
- [x] ✅ Seed de demonstração com as três proteções (faixa DDD 00, flag na empresa, recusa no envio)
- [x] ✅ `ServicoDashboardDemo` e `/api/dashboard/demo` removidos (a rota responde 404)
- [x] ✅ Funil e "De onde vêm seus leads" lado a lado, acima da evolução
- [x] ✅ Funil renderiza o que a API devolver — testado com 3, 5 e 8 etapas
- [x] ✅ Rosca só em tons de verde, com teste que reprova cor fora da paleta

### Bloco F — ajustes finos
- [x] ✅ Fuso editável, com validação contra o host (id inválido é recusado, sem fallback silencioso)
- [x] ✅ `empresas.uf` e seed de feriados estaduais, idempotente
- [x] ✅ Concorrência ao mover card via `xmin` — conflito devolve 409 e a coluna recarrega
- [x] ✅ Timing uniforme no "esqueci minha senha" (piso de 250 ms)
- [x] ✅ Espera acima da janela devolve marcador, não número
- [x] ✅ Veredicto SMTP: a porta configurada é 587 com STARTTLS; nada a trocar

### Bloco G — funcionalidades novas
- [ ] ⬜ 17 — funil configurável: não há endpoint nem tela para criar/editar etapa
- [ ] ⬜ 12 — funil padrão de 6 etapas mais pós-venda: o cadastro ainda cria 5
- [🟡] 13 — áudio: o webhook **já recebe e guarda** áudio (o tipo existe no domínio e a mídia é
      baixada); falta tocar na thread e gravar pela tela — hoje aparece como "📎 audio"
- [ ] ⬜ 18 — **`negocios`: separar a negociação do contato.** Hoje lead e contato são a MESMA
      linha, então cliente que volta não gera oportunidade nova — e dois orçamentos simultâneos
      para a mesma pessoa são impossíveis. Ver a pendência em `docs/NEG-1.md`.
- [ ] ⬜ 14 — relatórios
- [ ] ⬜ 15 — propostas
- [ ] ⬜ 16 — agenda e reuniões

### Fora do plano
Nada apareceu. `campanha`/`anúncio` são só um comentário no campo de texto livre da origem, com a
nota explícita de que atribuição de custo é fase 2. `instagram`/`facebook` são valores do enum de
origem do lead, não captação por DM.

## Os 3 próximos passos

1. **Parear um número real e rodar o Bloco B inteiro** — esforço **médio**, e é o único item que
   não depende de código. Todo o caminho de entrega foi testado contra um cliente falso; o risco
   concentrado aqui é maior que o de tudo que falta somado.
2. **Abrir a aba Actions e confirmar que o pipeline passa** — esforço **baixo**. O repositório
   existe desde hoje; se algo estiver errado no workflow, é melhor descobrir com 484 testes verdes
   do que no primeiro commit apressado.
3. **Corrigir o `<input type="time">`** — esforço **baixo**. Criar lembrete *com hora* pela tela de
   contato nunca funciona (400). Está aberto desde o PI-1 e é o único bug conhecido que atinge o
   usuário final.

## Achados fora do backlog

**Feito além do previsto:** paridade da regra de minutos úteis nos **três** lados (o prompt pedia
dois); concorrência otimista via `xmin` sem coluna nova; cartão "Tarefas pendentes" no dashboard
alimentado pelo Meu Dia; clique na etapa levando ao quadro com a coluna destacada; backfill da
métrica de tempo até o valor para tenants antigos; e o repositório git criado e publicado — que era
o bloqueio declarado do PI-3.

**Dívidas e bugs abertos (nenhum consertado nesta varredura):**

| O quê | Desde |
|---|---|
| `<input type="time">` manda `"14:30"` e `TimeOnly` espera `"14:30:00"` → 400 | PI-1 |
| Funil do dashboard e do kanban contam diferente: um filtra anonimizado, o outro não | bloco 9 |
| `paginas/em-breve/` é código morto — não roteada, não importada | PI-3 |
| A semente de desenvolvimento só roda uma vez por banco (e-mails fixos, únicos globalmente) | PI-4 |
| O envio SMTP acontece dentro da requisição do reset; relay lento reabre a janela de timing | PI-5 |
| `dashboard.css` 460 bytes acima do budget de 4 kB (era 5,86 kB; caiu, não fechou) | bloco 9 |
| 26 UFs sem feriado estadual cadastrado | PI-5 |
| Três tenants de verificação sobraram em `nexora_dev` | PI-2/PI-4 |
| Arrastar card no kanban nunca foi testado em navegador | bloco 8 |
| Sem lock distribuído no agendador e rate limit em memória — valem para uma instância só | bloco 6 |

## Números de saúde

| Medida | Resultado |
|---|---|
| `dotnet build -warnaserror` | ✅ limpo |
| `dotnet test` | ✅ **388/388** |
| `ng build` | ✅ compila; 1 warning de budget (`dashboard.css`, pré-existente) |
| `ng test` (headless) | ✅ **96/96** |
| Total de testes | **484** |
| Arquivos de teste | 22 no backend, 8 no frontend |
