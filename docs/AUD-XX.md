# AUD-XX — Números calculados só no servidor

**A regra:** todo número mostrado ao usuário vem pronto da API. O painel desenha e formata; não
soma, não conta, não divide.

Isso vale para total, contagem, média, percentual, conversão, ranking e contador. **Fica fora:**
estado da tela ("N selecionados", "Mais filtros (N)", contador de caracteres), o desenho dos
gráficos (escala, coordenadas), a formatação de um valor que já veio pronto, o "há 3h" da Caixa
(tempo de relógio) e o progresso de upload e gravação.

---

## Como se escreve um número novo

- **Agregado no SQL:** `CountAsync`, `SumAsync`, `GroupBy` traduzido, ou SQL cru com `empresa_id`
  explícito. Nunca `ToListAsync()` antes de contar.
- **Percentual:** `decimal` de 0 a 100, duas casas, `MidpointRounding.AwayFromZero` — sempre por
  `Nexora.Core.Servicos.Percentual`. **Sem denominador é `null`**, e a tela mostra "—". Nunca 0.
  Fatias que precisam somar 100 (a rosca de origens) usam `Percentual.Fatias`, que distribui o
  centésimo que sobra pelo maior resto.
- **"Hoje"** é o dia no fuso da empresa: `IHojeDaEmpresa` no controller, `FusoDeNegocio` no
  serviço. Nunca `DateTime.UtcNow.Date` — das 21h à meia-noite, o "hoje" de UTC já é amanhã.
- **Paginação:** `{ itens, totalCount, pagina, tamanhoPagina, totalPaginas }`, com o `totalCount`
  de um `CountAsync` com os mesmos filtros.
- **Depois de uma ação** (mover card, concluir item), o endpoint devolve os números novos ou a
  tela relê. Ela não "já desce o número" por conta própria.
- **Permissão:** quem não tem `ver_numeros_da_equipe` vê só os próprios números, em todo
  endpoint que soma o trabalho de outras pessoas.

---

## Fase 1 — o que a auditoria encontrou

Nenhum endpoint carregava linhas para contar depois — o servidor já agregava no banco. Os
problemas do servidor eram de fuso (UTC), de total de paginação, de números que não batiam entre
telas e de dois vazamentos de permissão. No painel, 28 pontos calculavam número; seis deles sobre
uma lista já cortada pelo servidor, então o número saía **errado**, não só no lugar errado.

### Painel

Caminhos relativos a `frontend/nexora-painel/src/app/`.

| # | Tela | O que era calculado | Prioridade | Status |
|---|---|---|---|---|
| 1 | CRM (quadro) | total e valor da coluna refeitos depois de arrastar; a origem nunca era relida | Alta | ✅ Resolvido — `mover` devolve `colunas[{etapaId, total, valorTotal, concluidas}]`; a página da coluna traz os totais |
| 2 | Contatos | filtro de origem só na página aberta: "N de M nesta página" | Alta | ✅ Resolvido — `GET /contatos?origem=`, com abas e total no mesmo recorte |
| 3 | Dashboard | linha "Todos" do funil somada na tela | Alta | ✅ Resolvido — `totalEmNegociacao`, `totalValorEmAberto` |
| 4 | Dashboard | rosca de origens: agrupar, top + "Outros", total e % ajustado | Alta | ✅ Resolvido — `origens[{origem, agrupada, leads, percentual, campanhas}]` e `leadsTotal` |
| 5 | Leads parados | % de aproveitamento = ganhos ÷ marcados | Alta | ✅ Resolvido — `aproveitamentoPercentual` (`null` sem marcados) |
| 6 | Meu Dia | total, abas e paginação sobre a lista cortada em 200 | Alta | ✅ Resolvido — `GET /meu-dia/pagina` com `contagens` e paginação no servidor |
| 7 | Anúncios | "Enviar as N que ainda dão tempo", contado sobre as 50 primeiras | Alta | ✅ Resolvido — `VendasSemEnvio.noPrazo` |
| 8 | Ficha do contato | "N compras somando R$ X · última em" | Média | ✅ Resolvido — `GET /contatos/{id}/vendas` → `{ vendas, resumo }` |
| 9 | Caixa | "N compras · última em" (outra cópia da regra do #8) | Média | ✅ Resolvido — o mesmo `resumo` |
| 10 | Menu (badge) | "não lidas" sobe +1 por mensagem e não desce ao ler | Média | ✅ Resolvido — `naoLidas` no evento e no "marcar como lida" |
| 11 | Ficha do contato | paginação e contagens do histórico e dos lembretes carregados | Média | ✅ Resolvido — histórico e lembretes resolvidos paginados no servidor |
| 12 | Relatórios (funil) | "entradas" × "agora" juntados na tela; etapa ausente vira 0 | Média | ✅ Resolvido — `RelatorioFunil.etapas[]` juntado no servidor |
| 13 | Dashboard | "Mostrando X de Y" com Y somado | Média | ✅ Resolvido — `MeuDia.total` |
| 14 | Meu Dia | espera em "dias" supondo 12h úteis | Média | ✅ Resolvido — `AcaoDoDia.esperaDiasUteis`, pela janela da empresa |
| 15 | Anúncios | "N eventos · M falharam" sobre os últimos 50 | Média | ✅ Resolvido — `PainelConversoes.totais` dos últimos 30 dias |
| 16 | Webhook | "N registradas · M falharam" e paginação sobre os últimos 50 | Média | ✅ Resolvido — registro paginado no servidor, com `falhas` |
| 17 | Captação | total de leads, % canais × formulários, "X de Y ativos" | Média | ✅ Resolvido — `GET /captacao/resumo` |
| 18 | Formulários | total de leads recebidos | Média | ✅ Resolvido — o mesmo resumo do #17 |
| 19 | Pipelines | "N de 5" com o teto escrito no painel | Média | ✅ Resolvido — `GET /limites` — e o teto do servidor era 4, não 5 |
| 20 | Etapas, etiquetas, seletor, conexão | "N de M" com M fixo no painel | Média | ✅ Resolvido — `GET /limites` e `ConexoesDto.emUso` |
| 21 | Paginação (todas) | "Página X de Y" = total ÷ tamanho | Baixa | Lote 3 |
| 22 | Dashboard | "N sem resposta medida" | Baixa | Lote 3 |
| 23 | Gráfico de linha | média móvel de 7 dias | Baixa | Lote 3 |
| 24 | Canais | quantos canais estão sem número | Baixa | Lote 3 |
| 25 | Importar | linhas processadas = importados + duplicados + inválidos | Baixa | Lote 3 |
| 26 | Etiquetas | ranking "Mais usadas" ordenado no painel | Baixa | Lote 3 |
| 27 | Primeiros passos | "Três passos" fixo | Baixa | Lote 3 |
| 28 | Evolução / Leads parados | "há N meses" com 30,44 dias num lugar e 30 no outro | Baixa | Lote 3 |

### Servidor

Caminhos relativos a `src/`.

| # | Onde | Problema | Prioridade | Status |
|---|---|---|---|---|
| B1 | Relatórios e série do dashboard | data final padrão em UTC: das 21h à meia-noite, "hoje" era amanhã | Alta | ✅ Resolvido — `IHojeDaEmpresa` |
| B2 | Tempo de resposta: dashboard × relatório | a série contava mensagem automática e a nota do NPS como resposta | Alta | ✅ Resolvido — o mesmo filtro de origem humana do relatório |
| B3 | Relatório do funil (entradas) | ignorava pessoa (`$7`) e origem (`$8`): o vendedor via as entradas da empresa | Alta | ✅ Resolvido — `EXISTS` por dono do negócio e origem do contato |
| B4 | Dashboard | não aplicava `VerNumerosDaEquipe`: o vendedor via faturamento e conversão da empresa | Alta | ✅ Resolvido — recorte por responsável em todo número do painel e da série |
| B5 | Paginação por `COUNT(*) OVER ()` | página além do fim devolve `total = 0` | Média | ✅ Resolvido — a página vazia relê o total na mesma consulta |
| B6 | Saúde da conexão | "enviadas hoje" zera às 21h (UTC) | Média | ✅ Resolvido — o dia da empresa |
| B7 | Captação | lembrete e checagem de duplicado com a data de UTC | Média | ✅ Resolvido — o dia da empresa |
| B8 | Abas de contatos | contato só com negócio cancelado conta em "Todos" e em nenhuma aba | Média | ✅ Resolvido — cancelada com motivo é perda; sem motivo, como se não existisse |
| B9 | Leads de hoje e série | contam anonimizados; os relatórios não | Média | ✅ Resolvido — anonimizado fora dos dois |
| B10 | Conversão por vendedor | numerador e denominador com donos diferentes | Média | ✅ Resolvido — negócios decididos no período, pelo dono do negócio |
| B11 | Arredondamento | conversão como `double` 0–1 sem arredondar | Média | ✅ Resolvido — vendedor e origem em `Percentual`; a Evolução fica para o lote 3 |
| B12 | Evolução | lista usuário inativo e convidado com zeros | Baixa | Lote 3 |
| B13 | "Hoje" em UTC | feriados e "dias sem compra" do NPS | Baixa | Lote 3 |
| B14 | Coorte da Evolução | reabrir negócio perdido muda meses já fechados | Baixa | Lote 3 |

---

## Lote 1 — Alta

O que ficou de pé, e o teste que trava cada parte. Cada regra nova foi quebrada de propósito
(24 sabotagens no servidor, 10 na tela) e derrubou o teste com o nome abaixo.

| Regra | Teste |
|---|---|
| sem denominador é `null` | `PercentualTests.SEM_DENOMINADOR_E_NULO_E_NAO_ZERO` |
| meio arredonda para cima | `PercentualTests.O_MEIO_ARREDONDA_PARA_CIMA` |
| a rosca soma 100 pelo maior resto | `PercentualTests.O_CENTESIMO_QUE_SOBRA_VAI_PARA_O_MAIOR_RESTO` |
| a rosca agrupa a partir da sétima origem | `RoscaDoPainelTests.PASSANDO_DE_SEIS_O_RESTO_VIRA_UMA_FATIA_AGRUPADA_NO_FIM` |
| aproveitamento = ganhos ÷ marcados | `LeadsParadosDbTests.VENDA_CANCELADA_NAO_CONTA_COMO_REATIVADA` |
| "no prazo" conta além das 50 da lista | `VendasSemConversaoDbTests.O_NO_PRAZO_CONTA_ALEM_DAS_50_DA_LISTA` |
| o vendedor vê só os seus números no painel | `PainelInicialDbTests.QUEM_NAO_VE_A_EQUIPE_VE_SO_OS_PROPRIOS_NUMEROS` |
| a linha "Todos" soma os funis | `PainelInicialDbTests.OS_TOTAIS_DO_FUNIL_VEM_PRONTOS_E_SOMAM_OS_DOIS_FUNIS` |
| "hoje" do gráfico e dos relatórios é o da empresa | `DataPadraoDosControllersTests` |
| mensagem automática e nota do NPS não são resposta | `SerieTemporalDbTests.MENSAGEM_AUTOMATICA_NAO_CONTA_COMO_RESPOSTA_NO_GRAFICO`, `A_NOTA_DO_NPS_NAO_ESPERA_RESPOSTA_NO_GRAFICO` |
| a série do vendedor é só dele | `SerieTemporalDbTests.QUEM_NAO_VE_A_EQUIPE_VE_SO_A_PROPRIA_SERIE` |
| o tempo de resposta do vendedor é o das respostas dele | `SerieTemporalDbTests.O_TEMPO_DE_RESPOSTA_DO_VENDEDOR_E_SO_DO_QUE_ELE_RESPONDEU` |
| entradas do funil respeitam pessoa e origem | `RelatoriosDbTests.AS_ENTRADAS_DO_FUNIL_RESPEITAM_PESSOA_E_ORIGEM` |
| origem filtra lista, abas e total | `ContatosDbTests.O_FILTRO_DE_ORIGEM_VALE_PARA_A_LISTA_AS_ABAS_E_O_TOTAL` |
| origem inventada é 400 | `OrigemNaListaDeContatosTests.ORIGEM_INVENTADA_E_RECUSADA` |
| Meu Dia intercala conversa e lembrete pela hora | `MeuDiaDbTests.A_PAGINA_VEM_NA_ORDEM_DO_DIA_E_INTERCALA_CONVERSA_E_LEMBRETE` |
| as contagens do Meu Dia são do dia inteiro | `MeuDiaDbTests.AS_CONTAGENS_SAO_DO_DIA_INTEIRO_EM_QUALQUER_ABA_E_PAGINA` |
| cada aba do Meu Dia traz só o recorte dela | `MeuDiaDbTests.CADA_ABA_TRAZ_SO_O_RECORTE_DELA` |
| arrastar devolve as duas colunas, contadas no banco | `FunilDbTests.MOVER_DEVOLVE_OS_NUMEROS_DAS_DUAS_COLUNAS_CONTADOS_NO_BANCO` |
| a página da coluna traz os totais da coluna inteira | `FunilDbTests.A_PAGINA_DA_COLUNA_TRAZ_OS_TOTAIS_DA_COLUNA_INTEIRA` |
| coluna de outra empresa volta zerada | `FunilDbTests.A_COLUNA_DE_OUTRA_EMPRESA_DEVOLVE_ZERO_E_NENHUM_CARD` |

Na tela, cada componente do lote tem um teste que devolve números impossíveis para a conta antiga
(33,33% com 10 de 40; "Todos" = 16 com linhas que somam 15; 7 na coluna de onde saiu um card de
2). Voltar a calcular faz o teste mostrar o número da tela.

**Isolamento de empresa.** O painel inicial (`OS_NUMEROS_DE_OUTRA_EMPRESA_NAO_ENTRAM`), o Meu Dia
(`A_PAGINA_NAO_VE_OUTRA_EMPRESA`) e as colunas do CRM são testados com duas empresas. Os dois
primeiros não foram sabotados: o isolamento ali é o filtro global de empresa, que não é regra nova.

**O que ainda tem conta nos componentes do lote**, conferido por busca de `.reduce(`,
`.filter(…).length` e `Math.`: contagem de selecionados (estado da tela), geometria da rosca e
do arrasto, formatação de minutos e o "vence em N dias" (relógio). Os que são número de negócio
estão nos lotes 2 e 3: `anuncios.ts` "M falharam" (#15), `meu-dia.ts` espera em dias (#14),
`dashboard.ts` "sem resposta medida" (#22), `leads-parados.ts` "há N meses" (#28).

---

## Lote 2 — Média

**Decisões do dono do produto.**
- **B8:** o cancelamento segue o motivo, como no CAN-1. Com motivo ("o cliente desistiu") é perda e vai para "Perdidos". Sem motivo ("registrei errado"), o negócio é ignorado e a pessoa volta a "Abertos", como quem não tem negócio.
- **B10:** a conversão do vendedor conta os negócios decididos no período, ganhos ÷ (ganhos + perdidos), pela data e pelo dono do negócio. É a mesma conta do painel inicial.

**Achado no caminho.** A tela de Pipelines tinha o teto copiado como 5, e o servidor aceita 4. Ela mostrava "4 de 5", abria o formulário do quinto e levava 409. É o tipo de cópia que esta auditoria tira da tela.

**O tipo comum de página.** `PaginaComTotal<T>` (`{ itens, totalCount, pagina, tamanhoPagina, totalPaginas }`) entrou no histórico da ficha, nos lembretes resolvidos e no registro do webhook. As outras listas migram no lote 3 (#21).

| Regra | Teste |
|---|---|
| cancelada com motivo é perda | `ContatosDbTests.VENDA_CANCELADA_COM_MOTIVO_CONTA_EM_PERDIDOS` |
| cancelada sem motivo é como se não existisse | `ContatosDbTests.VENDA_CANCELADA_SEM_MOTIVO_E_COMO_SE_NAO_EXISTISSE` |
| conversão do vendedor: decididos no período | `RelatoriosDbTests.A_CONVERSAO_DO_VENDEDOR_E_DOS_NEGOCIOS_DECIDIDOS_NO_PERIODO` |
| sem nada decidido, conversão nula | `RelatoriosDbTests.SEM_NADA_DECIDIDO_A_CONVERSAO_DO_VENDEDOR_E_NULA` |
| página além do fim traz o total (quatro listas) | `RECORRENTES_A_PAGINA_ALEM_DO_FIM_…`, `AS_RESPOSTAS_NA_PAGINA_ALEM_DO_FIM_…`, `LeadsParadosDbTests.A_PAGINA_ALEM_DO_FIM_…`, `A_LISTA_DE_EMPRESAS_ALEM_DO_FIM_…` |
| "enviadas hoje" é o dia da empresa | `ConexoesDbTests.ENVIADAS_HOJE_CONTA_O_DIA_DA_EMPRESA_E_NAO_O_DE_UTC` |
| o lembrete da captação é do dia da empresa | `CapturaDbTests.O_LEMBRETE_DA_CAPTURA_E_PARA_O_DIA_DA_EMPRESA` |
| anonimizado não é lead (painel e gráfico) | `ANONIMIZADO_NAO_CONTA_COMO_LEAD_DE_HOJE`, `ANONIMIZADO_NAO_CONTA_NO_GRAFICO_DE_LEADS` |
| resumo de compras sem as canceladas | `VendasDbTests.O_RESUMO_DE_COMPRAS_SAI_DO_BANCO_SEM_AS_CANCELADAS` |
| não lidas da empresa no evento e ao ler | `O_EVENTO_DE_MENSAGEM_LEVA_AS_NAO_LIDAS_DA_EMPRESA`, `MARCAR_LIDA_DEVOLVE_O_TOTAL_DA_EMPRESA_DEPOIS_DE_LER` |
| histórico paginado no banco | `TrilhaDbTests.O_HISTORICO_PAGINA_NO_BANCO_E_TRAZ_O_TOTAL` |
| lembretes resolvidos paginados; o detalhe só com pendentes | `LembretesResolvidosDbTests.OS_RESOLVIDOS_PAGINAM_NO_BANCO_E_O_DETALHE_TRAZ_SO_OS_PENDENTES` |
| funil do relatório juntado por etapa | `RelatoriosDbTests.FUNIL_NO_PERIODO_conta_entradas_por_ARRASTO_e_por_REGISTRO_DE_VENDA` |
| total do Meu Dia pronto | `MeuDiaDbTests.LIMITE_CORTA_A_LISTA_MAS_O_CONTADOR_DIZ_O_TOTAL` |
| dias de espera pela janela da empresa | `MeuDiaDbTests.OS_DIAS_DE_ESPERA_SAO_PELA_JANELA_DA_EMPRESA` |
| eventos e falhas de Anúncios no período | `ConversoesDbTests.OS_TOTAIS_DO_REGISTRO_SAO_DO_PERIODO_E_NAO_DA_LISTA` |
| registro do webhook paginado, com as falhas | `WebhookSaidaDbTests.O_REGISTRO_DE_ENTREGAS_PAGINA_COM_O_TOTAL_E_AS_FALHAS` |
| resumo da Captação, com fatias que fecham 100 | `CaptacaoResumoDbTests.O_RESUMO_SOMA_NO_BANCO_E_AS_FATIAS_FECHAM_100` |
| tetos do servidor, uso por empresa | `LimitesDbTests.OS_TETOS_SAO_OS_DO_SERVICO_E_O_USO_E_DA_EMPRESA` |
| "cheio" é a mesma conta da recusa | `LimitesDbTests.CHEIO_E_A_MESMA_CONTA_DA_RECUSA` |

Cada regra acima foi quebrada de propósito e derrubou o teste com o nome dela: 26 sabotagens no servidor e 17 na tela. Na tela, cada teste novo devolve números impossíveis para a conta antiga, como 33,33% com 10 de 40, "1 de 23" com contadores que somam 22, ou 230 eventos com 3 na lista.

**Fica para o lote 3:**
- A conversão da Evolução continua como fração de 0 a 1. Há trabalho pausado nessa tela, e ela vai junto com B12 e B14.
- Leads parados volta uma página de cada vez quando a página esvazia; o salto direto para a última vem com `totalPaginas` (#21).
