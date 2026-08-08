# Bloco 14 — Módulo de relatórios

Sete relatórios, uma barra de filtros, exportação CSV gerada no servidor. Tela em
`/relatorios`, sem `guardaDono`: vendedor entra e vê os números dele.

---

## O que mudou em relação ao prompt

### Devolução não existe — a prova trocou de par

O critério "venda devolvida não altera o relatório do mês em que foi fechada" foi escrito antes do
corte de devolução para a V2 (ver `docs/NEG-2.md`). **Foi provado o par equivalente que o modelo
suporta**, com a mesma armadilha:

```
duas vendas em 06/08: 1000 e 700 ......... relatório = R$ 1.700
  concluir a de 700 ...................... relatório = R$ 1.700   ← NÃO muda
  cancelar a de 1000 ..................... relatório = R$   700   ← muda, retroativamente
```

`CONCLUIDA_NAO_MUDA_O_MES_MAS_CANCELADA_MUDA`, escrito antes de `IServicoRelatorios` existir. As
duas ações no mesmo teste é o que o torna difícil de passar por acidente: quem tratar concluir
como cancelar zera o faturamento; quem tratar cancelar como concluir mantém os 1.700.

Consequências: o filtro "Situação da venda" tem **três** opções, e o relatório 1 separa
**vendido / concluído / cancelado**.

### Relatório 4 — opção B, e ela custou uma linha

O `InterceptorTrilha` grava `etapaId: {antes, depois}` no `jsonb` de **qualquer** evento que mude
a etapa. Confirmado no banco:

```json
{"etapa": {"antes": "Novo Lead", "depois": "Primeiro Atendimento"},
 "etapaId": {"antes": 28, "depois": 29}}
```

Então `Moveu` (arrasto), `Ganhou` (registrar venda), `Reabriu` e a criação do contato entram
todos. **O predicado é sobre a chave, não sobre o verbo:**

```sql
WHERE jsonb_exists(a.alteracoes, 'etapaId')
```

Filtrar por `acao = 'Moveu'` — o caminho óbvio — faria "entraram em Venda" vir **sempre zero**,
porque aquela porta declara `Ganhou`. Há um teste que passa pelas duas portas e uma mutação que
troca o predicado pelo verbo; ela derruba o teste.

**Três limites, e os três estão na tela:**

1. a trilha só existe desde o deploy do AUD-1 — a tela mostra "movimentação registrada desde
   07/08/2026", e explica que zero antes disso é ausência de histórico, não ausência de evento;
2. `ExpurgoTrilha` apaga além de 12 meses — o relatório não vai mais fundo, por construção;
3. escrita em lote por SQL cru (semeadores) não passa pelo interceptor.

Por isso a seção mostra **as duas coisas, com títulos separados**: "Entradas no período" (a série
da trilha, com a ressalva) e "Situação agora" (a foto, sempre verdadeira). Foto atual **não** é
rotulada como período — há teste que lê os títulos e os cabeçalhos das colunas.

---

## Os relatórios

| # | Relatório | Fonte | O que responde |
|---|---|---|---|
| 1 | Vendas por período | `vendas` | quanto entrou, quanto já foi concluído, quanto foi cancelado |
| 2 | Desempenho por vendedor | `vendas` + `contatos` | leads, vendas, valor, ticket médio, conversão |
| 3 | Origem dos leads | `contatos.origem` × `vendas` | qual canal traz **dinheiro**, não só volume |
| 4 | Funil | `auditoria` + `contatos` | quantos entraram na etapa, e quantos estão nela |
| 5 | Tempo de resposta | `mensagens` + `nexora_minutos_uteis` | média **e mediana**, em minutos úteis |
| 6 | Motivos de perda | `contatos.motivo_perda` | por que estamos perdendo, ordenado por valor |
| 7 | Clientes recorrentes | `vendas` agrupada por contato | quem voltou, quantas vezes, quanto somou |

Fora do escopo, como pedido: **relatório por campanha** não foi criado nem deixou placeholder.

### Decisões que valem registro

**A conversão é calculada diferente nos relatórios 2 e 3, de propósito.** No 2 é
`vendas ÷ (vendas + perdidos)` — quem está em negociação não entra, senão a taxa despencaria toda
vez que entrasse lead novo (mesma conta do dashboard). No 3 é `vendas ÷ leads do canal` — a
pergunta ali é "de cada 100 que este canal trouxe, quantos compraram", e ela precisa contar quem
ainda está negociando. As duas notas estão na tela, abaixo de cada tabela.

**"Sem dono" e "Automático" aparecem como linhas.** Contato sem responsável vende, e o follow-up
automático responde cliente. Descartá-los faria a soma das linhas não bater com o total, e
ninguém saberia por quê. Entram por `UNION ALL` com `id NULL`.

**O relatório 7 recorta pela ÚLTIMA compra**, não exigindo que todas caiam no período: a pergunta
é "quem voltou recentemente", e exigir as duas compras dentro do intervalo esconderia justamente o
cliente antigo que acabou de voltar.

---

## Agregação

Tudo no SQL. Oito consultas em SQL cru parametrizado, no molde do `ServicoSerie`.

**Nenhuma função sobre coluna em filtro.** Os cortes são sempre `coluna >= $inicio AND coluna <
$fim`, com os limites calculados no C# a partir do fuso de negócio. `date_trunc` aparece só no
`SELECT` e no `GROUP BY`, sobre o conjunto já recortado.

Isso não é uma promessa em comentário: `ServicoRelatorios.ConsultasParaAuditoria` expõe o SQL, e
dois testes o leem —

- `NENHUMA_FUNCAO_SOBRE_COLUNA_EM_FILTRO` recorta as cláusulas `WHERE` e falha se achar
  `date_trunc(`, `lower(`, `cast(` ou `::date` dentro delas;
- `TODA_CONSULTA_QUE_AGREGA_AGREGA_NO_SQL` exige `COUNT`/`SUM`/`AVG` no texto da consulta.

Expor SQL para teste é feio; a alternativa é uma regra que vale enquanto alguém lembra dela na
revisão.

**Períodos sem dado voltam com zero**, via `generate_series` + `LEFT JOIN` — gráfico com buraco
mente sobre a tendência, e mente para melhor.

---

## Permissão

O corte vive em `ServicoRelatorios.ResponsavelEfetivo`, mesma linha do `ServicoAtividades`: para
papel Vendedor o `responsavelId` que veio do cliente é **descartado** e o próprio usuário é
imposto, em todos os sete.

**Não há `[Authorize(Roles=)]` no controller**, e a ausência é deliberada: a regra não é "pode
chamar a rota" — vendedor pode ver relatório, o dele. O recorte é por linha.

`VENDEDOR_NAO_VE_NUMERO_DE_OUTRO_VENDEDOR_nem_pela_API_direta` chama o serviço passando o id de
outro vendedor — o que uma requisição forjada faria — e confere que voltam só os números dele. E
confere que o gestor, com o mesmo filtro, vê os dois: sem isso o teste passaria com uma regra que
não devolve nada para ninguém.

Uma consequência: `/equipe` e `/etapas` são `[Authorize(Roles="dono")]`, então o **gestor** — que
vê o relatório inteiro — levaria 403 montando o próprio filtro. Foi criada
`GET /api/relatorios/opcoes`, que devolve responsáveis, etapas e os motivos de perda realmente
usados, já recortados pelo mesmo papel. É por isso que o seletor do vendedor nasce travado: a API
manda uma opção só.

---

## Exportação

`GET /api/relatorios/{nome}/csv` monta no **servidor**. `CsvBrasileiro` concentra as três
decisões que o Excel brasileiro exige, cada uma com um sintoma quando falta:

| | Sem isso |
|---|---|
| BOM UTF-8 (`EF BB BF`) | "Preço" abre como "PreÃ§o" |
| `;` separador | tudo cai na primeira coluna |
| `,` decimal, sem milhar | a coluna de dinheiro é texto e não soma |

Oito testes conferem isso **em bytes** — nenhum dos três aparece para quem só olha o arquivo
aberto na máquina certa. Inclui o caso do nome com `;` ("Silva; Filho"), que sem escape empurra
todas as colunas seguintes uma casa e desalinha o arquivo a partir daquela linha.

O CSV de recorrentes **pagina o banco em blocos de 200 e leva a lista inteira**, não a página da
tela: exportar 20 de 3.000 é o tipo de erro que ninguém percebe até fechar o mês com o número
errado.

---

## Gráficos

`grafico-barras` novo, SVG puro, irmão do `grafico-linha` que já existia — mesmas medidas, mesmo
`viewBox`, mesma paleta. Nenhuma biblioteca instalada.

Série temporal pede linha (a tendência importa); comparação entre categorias pede barra — "qual
origem vende mais" não tem tendência nenhuma, e ligar os pontos sugeriria uma que não existe.

A barra tem duas partes: o tom claro é o faturamento, o escuro é a parcela já concluída. Duas
barras lado a lado pediriam ao leitor que somasse mentalmente para saber o total do dia. Só
tokens do design system (`--verde`, `--verde-3`).

---

## Medição

Carga marcada em `nexora_dev`, medida e **removida** — a base voltou ao estado exato de antes
(1007 contatos / 204 vendas / 2971 mensagens / 9 eventos), com conferência de que nada marcado
sobrou.

**Volume medido:** 21.007 contatos · 21.633 vendas · **54.971 mensagens** · 20.009 eventos de
trilha · 2.675 conversas.

O `.sql` de medição foi **gerado a partir do próprio `ServicoRelatorios.cs`**, extraindo as
constantes e substituindo os `$n` por literais — copiar o SQL na mão mediria uma versão parecida,
não a que roda.

`EXPLAIN (ANALYZE, BUFFERS)`, PostgreSQL 17.2, JIT desligado:

| Consulta | 30 dias (padrão da tela) | 12 meses (pior caso) |
|---|---|---|
| 1 · vendas | **4,7 ms** | 19,1 ms |
| 2 · desempenho | **2,9 ms** | 26,3 ms |
| 3 · origem | **6,3 ms** | 32,7 ms |
| 4 · funil (entradas) | **0,9 ms** | 14,1 ms |
| 4 · funil (agora) | **7,7 ms** | 7,1 ms |
| 5 · tempo de resposta | **17,7 ms** | 140,2 ms |
| 6 · motivos de perda | **2,2 ms** | 2,6 ms |
| 7 · recorrentes | **21,1 ms** | 25,3 ms |
| **tela inteira (7 chamadas paralelas)** | **~21 ms** | **~140 ms** |

A tela dispara as sete em paralelo, então o tempo é o da mais lenta, não a soma.

**Índices efetivamente usados em 30 dias** — é o retorno da regra de não usar função sobre coluna
em filtro: `ix_contatos_criado`, `ix_vendas_periodo`, `ix_auditoria_empresa`, `ix_msg_serie`.

**Em 12 meses o planejador troca para `Seq Scan`, e está certo:** a janela seleciona ~70% da
tabela num tenant único, e varrer sai mais barato que percorrer o índice e buscar cada linha.
Índice descartado por escolha do planejador é diferente de índice descartado por função sobre
coluna — o segundo acontece em qualquer recorte, inclusive no de um dia.

A mais cara é o tempo de resposta (140 ms em 12 meses): duas funções de janela sobre 37 mil
mensagens. Usa `SUM` sobre janela padrão, que tem função de transição inversa e roda em uma
passada — a forma óbvia com `MIN(...) OVER (ROWS BETWEEN 1 FOLLOWING ...)` é quadrática e já
estourou timeout no `ServicoSerie`.

---

## Verificação

`dotnet build -warnaserror` limpo · `dotnet test` **691 passando** · `ng build` limpo ·
`ng test` **278 passando**.

### Provas por mutação

| Mutação | Teste que caiu |
|---|---|
| faturamento conta só `status = 'fechada'` (concluir = cancelar) | `CONCLUIDA_NAO_MUDA_O_MES_MAS_CANCELADA_MUDA` |
| `ResponsavelEfetivo` devolve o pedido sem checar papel | `VENDEDOR_NAO_VE_NUMERO_DE_OUTRO_VENDEDOR_nem_pela_API_direta` |
| funil filtra `acao = 'Moveu'` em vez da chave `etapaId` | `FUNIL_NO_PERIODO_conta_entradas_por_ARRASTO_e_por_REGISTRO_DE_VENDA` |
| `alternarSelecao` sem `stopPropagation` (do NEG-2, revalidada) | o teste da caixa de seleção do funil |

### Dois defeitos encontrados por teste, não por leitura

**1. Colisão de parâmetros no tempo de resposta.** `nexora_minutos_uteis` recebe quatro argumentos
(janela e feriados), e eles caíram em `$8..$11` — **em cima de origem, etapa, status e valor
mínimo**. O tempo de resposta saía calculado contra a origem do lead, sem erro nenhum para
denunciar. Foram para `$17..$20`, com o comentário explicando por quê.

**2. `jsonb ?? 'etapaId'` chega literal ao Postgres.** O `?` do jsonb colide com o marcador de
parâmetro de vários drivers, e a duplicação (`??`) é a fuga usual — mas o Npgsql com parâmetros
**posicionais** não a reescreve, e o erro sai `operador não existe: jsonb ?? unknown`. Trocado por
`jsonb_exists(...)`, que não tem ambiguidade.

Além desses, três problemas de fixture que o banco pegou: `mensagens.instance_name` é `NOT NULL`,
`ck_msg_data_disparo` exige `data_disparo` em toda saída, e a coluna do autor chama `enviado_por`
(não `usuario_id`) — sendo nula em entrada e em disparo automático, que foi o que motivou a linha
"Automático" do relatório 5.

---

## Pendências

- **`lateral.spec.ts` falha, e é ANTERIOR a este bloco** — confirmado com `git stash` no NEG-2. O
  rodapé da barra lateral está com 78px onde o teste exige no máximo 64 ("voltou a empilhar?").
  Não foi tocada aqui. É o único teste vermelho da suíte do front.
- **O relatório 4 é fino até a trilha acumular.** Hoje são 9 eventos em `nexora_dev`; leva alguns
  meses de uso até virar informação. A tela diz isso em vez de mostrar zero seco.
- **Relatório por campanha não existe** e não ganhou placeholder — depende do módulo de Campanhas.
- **Sem filtro de conexão**, como pedido: `uq_conexoes_empresa` deixaria o seletor com uma opção
  só. Quando multi-número existir, entra.
- **Adicionar "Relatórios" ao menu consumiu folga da barra lateral.** O teste de densidade
  (`A BARRA NÃO ROLA EM 768px`) continua verde com 12 itens, mas foi preciso atualizar a contagem
  esperada — o próximo item vai chegar mais perto do limite.
