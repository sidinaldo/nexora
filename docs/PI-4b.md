# PI-4b — Dados de demonstração no produto real

Estado: **fechado**. Os 7 critérios verificados por execução.

`dotnet build -warnaserror` limpo, `ng build` limpo, **385 testes de backend** (367 → 385, +18) e
**89 de frontend**.

A demonstração deixou de ser um endpoint que devolve números inventados e passou a ser **um tenant
com dados de verdade no banco**. Mesmos serviços, mesmas consultas, mesmas telas — se a
demonstração está bonita, o produto está funcionando.

---

## 1. A faixa de números: DDI 55 + DDD **00**

```
5500 9 XXXXXXXX     →  (00) 90000-0001, (00) 90000-0002, …
```

**Por que DDD 00.** Os DDDs brasileiros vão de 11 a 99. O `00` não existe e não pode passar a
existir sem uma renumeração do plano nacional inteiro, então `5500900000001@s.whatsapp.net` não
corresponde a conta nenhuma — hoje nem depois.

**Por que não um prefixo "não alocado" dentro de um DDD real** (o clássico `(11) 90000-0000`): é
mais bonito na tela e muito pior aqui. "Não alocado hoje" não é "impossível", blocos de numeração
são alocados o tempo todo, e ninguém iria reavaliar esta escolha antes de a faixa virar o celular
de alguém. Impossibilidade estrutural é garantia; política de alocação é promessa.

O efeito colateral é deliberado: `(00) 90000-0001` na tela é obviamente falso. Numa captura de tela
de demonstração isso é honestidade, não defeito.

O número tem 13 dígitos e passa por `CanonicalizadorTelefone.EhValido` — o seed grava pelo mesmo
caminho de qualquer cadastro, sem exceção para si mesmo.

---

## 2. As três barreiras

Nenhuma depende das outras estarem certas. É o ponto.

### Barreira 1 — a faixa (acima)

Protege contra o motor disparar para número de gente real. Não protege contra alguém trocar o
telefone de um contato semeado à mão.

### Barreira 2 — `empresas.demonstracao` tira o tenant da rodada

[DadosFollowUp.cs](src/Nexora.Infra/Persistencia/DadosFollowUp.cs): `Where(e => e.Ativo && !e.Demonstracao)`.

O corte é **na fonte**, não dentro do laço do motor: aqui ele vale para tudo que a rodada faz com a
empresa — gerar lembrete, drenar pendente, expirar reserva. Uma checagem lá dentro teria que ser
repetida em cada ramo, e bastaria um esquecer.

Tenant de demonstração tem contato com telefone e conversa parada — exatamente o que a regra de
elegibilidade procura. Sem esta barreira, um tenant pareado a uma Evolution real mandaria follow-up
automático para os números semeados.

### Barreira 3 — o `EnviadorMensagem` recusa o disparo

[EnviadorMensagem.cs](src/Nexora.Core/Whatsapp/EnviadorMensagem.cs), dentro do `DispararAsync` —
**o ponto onde todo envio passa** (lembrete automático, resposta manual e reenvio). É o único lugar
onde uma checagem não pode ser esquecida por um caminho novo.

Duas condições independentes, cobrindo falhas opostas:

| Condição | Cobre |
|---|---|
| a **empresa** está marcada como demonstração | alguém trocou o telefone de um contato semeado por um número real |
| o **número** está na faixa reservada | contato de demonstração acabou num tenant comum (importação, cópia de base, engano) |

Recusa **registra a falha** em vez de apagar a linha: é o mesmo protocolo de qualquer falha de
entrega, a tela já sabe mostrar "não entregue" com o motivo, e apagar liberaria a invariante de
dedupe.

### Os testes das três

| Teste | O que prova |
|---|---|
| `BARREIRA_1_A_FAIXA_USA_UM_DDD_QUE_NAO_EXISTE` | prefixo, tamanho, DDD `00`, e que passa na validação do cadastro |
| `A_faixa_e_reconhecida_e_nao_pega_numero_de_verdade` | `5584988887777` e `5511999998888` **não** caem na faixa |
| `BARREIRA_2_EMPRESA_DE_DEMONSTRACAO_E_IGNORADA_PELO_MOTOR` | a empresa marcada some da lista da rodada |
| `Empresa_de_demonstracao_INATIVA_tambem_fica_de_fora` | impede alguém "simplificar" o `&&` para um `\|\|` |
| `BARREIRA_3_O_ENVIADOR_RECUSA_DISPARO_DE_TENANT_DE_DEMONSTRACAO` | com telefone **real**: quem barra é a marca da empresa. A Evolution não é chamada e o motivo fica gravado |
| `O_enviador_recusa_pelo_NUMERO_mesmo_em_tenant_comum` | o caso inverso |
| `Tenant_comum_com_numero_comum_continua_enviando` | **as barreiras não pegam quem não devem** — sem isto, uma checagem ampla demais bloquearia cliente pagante |

---

## 3. O seed

`ServicoSeedDemonstracao`, na Infra. Semente fixa `20260805`.

**Idempotente por limpar-e-recriar**, não por detectar-e-sair. A alternativa deixaria o tenant
envelhecer: as datas são relativas a hoje, e um tenant semeado no mês passado mostraria "última
mensagem há 30 dias" na caixa — exatamente o que uma demonstração não pode mostrar. A **empresa** é
reaproveitada (o id não muda, links salvos continuam valendo); o conteúdo é apagado na ordem das
chaves estrangeiras: mensagens → lembretes → conversas → contatos.

**Determinístico**: mesma execução, mesmos dados. Captura de tela reproduzível e teste estável.

**Datas sempre relativas a `agora`**, nunca absolutas.

### O que ele produz

```
{"empresaId":6,"usuarios":3,"contatos":60,"conversas":40,"mensagens":384,
 "lembretes":15,"ganhos":12,"perdidos":8}
```

Empresa "Oficina Central (demonstração)", instância `demo-nexora-nao-parear` — o nome denuncia na
lista de instâncias se alguém apontá-la para uma Evolution real por engano. Conexão marcada como
`conectado` para o painel não abrir com a faixa vermelha de "WhatsApp desconectado" por cima de
tudo; não há instância real por trás, e o envio é barrado pela barreira 3.

Acesso: `ana.demo@nexora.exemplo` / `demonstracao-2026`.

### A coerência mantida à mão

O seed escreve direto no banco, então as invariantes que os serviços respeitam não vêm de graça:

| Invariante | Como |
|---|---|
| `uq_msg_wa_id` | `DEMO-{conversaId}-{i}` — único por construção dentro da instância |
| `aguardando_desde` | instante da **primeira entrada da rajada final**; NULL se a última mensagem foi de saída |
| `ultima_mensagem_em/direcao/previa` | recalculados da última mensagem de verdade, depois de inseri-las |
| `nao_lidas` | entradas **depois da última saída** |
| `ck_contatos_terminal` | ganho e perdido são ramos exclusivos por construção, não por sorte |
| `ordem_kanban` | passo de 1000, sem repetição dentro da etapa |
| telefones | pelo `CanonicalizadorTelefone`, como qualquer cadastro |

Verificado no banco depois de semear: **0 conversas incoerentes**, **0 telefones fora da faixa**.

---

## 4. Como disparar

```
POST /api/demonstracao/semear
X-Chave-Admin: <a mesma chave do cadastro de empresa, do PI-2>
```

**Duas travas independentes, as duas precisam passar:**

1. `Demonstracao:Habilitado` — **falso por padrão**, vem de user-secrets. Desligado, a rota devolve
   **404** (não 403: 403 confirmaria que a rota existe para quem sondasse).
2. A **mesma chave de administração** do cadastro de empresa, em header, comparada em tempo
   constante. Chave vazia na configuração desliga, mesmo com a guarda de ambiente ligada.

Configuração explícita e não `IsDevelopment()`: um ambiente de homologação legítimo pode querer o
tenant de demonstração, e amarrar isso ao nome do ambiente obrigaria a mentir sobre qual ambiente é
qual.

Verificado por HTTP: sem chave → **401**; com a guarda desligada → **404**; com as duas → **200**.

---

## 5. O que foi removido junto com o modo fictício

| Arquivo / trecho | |
|---|---|
| `src/Nexora.Infra/Servicos/ServicoDashboardDemo.cs` | **apagado** |
| `src/Nexora.Core/Servicos/IServicoDashboardDemo.cs` | **apagado** (interface + 7 records) |
| `DashboardController.Demo()` + injeção | removidos — `GET /api/dashboard/demo` responde **404** |
| `ServicosInfra.cs` | `AddSingleton<IServicoDashboardDemo, …>` |
| `modelos.ts` | `IndicadorDemo`, `EtapaFunilDemo`, `OrigemDemo`, `AtividadeDemo`, `TarefaDemo`, `PontoSerieDemo`, `DashboardDemo`, `TipoAtividade` |
| `dashboard.servico.ts` | `demo()` |
| `dashboard.ts` | `modoDemo`, `demo`, `carregandoDemo`, `erroDemo`, `serieEscolhida`, `alternarDemo()`, `carregarDemo()`, `serie`, `formatoSerie`, `fatias`, `totalOrigens`, `larguraFaixa()`, `iconeAtividade()`, `valorIndicador()`, `variacao()`, `etapaValor()`, `FatiaRosca` |
| `dashboard.html` | o bloco `@if (modoDemo())` inteiro (143 linhas: rosca, funil de demonstração, indicadores, tarefas), os dois botões do topo e o convite "Abrir a demonstração" no estado vazio |

**Nenhum uso justificou mantê-los.** O argumento que sustentava o modo fictício no PI-4 era "empresa
nova tem o painel vazio no dia 1, e é quando o cliente decide se fica" — e ele some agora: para
mostrar como o painel fica cheio, a resposta passou a ser *entrar no tenant de demonstração*, que
mostra as **cinco telas** com conteúdo em vez de só o dashboard.

⚠️ Um erro meu no caminho, registrado: ao remover os tipos do `modelos.ts` por corte de intervalo,
levei junto os tipos da série temporal e das atividades do PI-4, que ficavam logo antes do
`DashboardDto`. O `ng build` acusou na hora (`TS2305`), e foram restaurados.

---

## 6. Critérios

| # | Critério | Estado |
|---|---|---|
| 1 | Builds limpos, testes verdes | ✅ 385 backend + 89 frontend |
| 2 | Os sete testes exigidos | ✅ abaixo |
| 3 | Caixa, funil, contatos, Meu Dia e dashboard com conteúdo | ✅ medido |
| 4 | Semáforo com as três cores | ✅ 2 verdes, 4 amarelas, 9 vermelhas |
| 5 | Gráfico com forma, não linha reta | ✅ 28 pontos semanais, 7 com faturamento, pico R$ 14.710 |
| 6 | `/api/dashboard/demo` não existe mais | ✅ HTTP 404 |
| 7 | Seed não roda em produção | ✅ guarda testada |

| Exigido | Teste |
|---|---|
| seed rodado duas vezes não duplica | `SEED_RODADO_DUAS_VEZES_NAO_DUPLICA` |
| empresa de demonstração ignorada pelo motor | `BARREIRA_2_EMPRESA_DE_DEMONSTRACAO_E_IGNORADA_PELO_MOTOR` |
| `EnviadorMensagem` recusa envio | `BARREIRA_3_O_ENVIADOR_RECUSA_DISPARO_DE_TENANT_DE_DEMONSTRACAO` |
| `aguardando_desde` coerente em toda conversa | `AGUARDANDO_DESDE_E_NAO_LIDAS_COERENTES_COM_A_ULTIMA_MENSAGEM` |
| `nao_lidas` coerente | mesmo teste |
| nenhum contato ganho e perdido | `NENHUM_CONTATO_GANHO_E_PERDIDO_AO_MESMO_TEMPO` |
| conversão não é 0% nem 100% | `A_TAXA_DE_CONVERSAO_NAO_E_ZERO_NEM_CEM_POR_CENTO` |

Medido no tenant semeado, logado como o dono:

```
DASHBOARD  leadsHoje=4  aguardando=15  followups=9
           vendasMes=6  faturamento=R$ 27.340,00  conversao=60%
           funil: Novo Lead:10 | Primeiro Atendimento:10 | Proposta:10 | Negociação:10 | Venda:12
CAIXA      40 conversas | semáforo: 2 verdes, 4 amarelas, 9 vermelhas
CONTATOS   60
MEU DIA    5 ações
GRÁFICO    28 pontos, 7 com faturamento, pico R$ 14.710
```

### Dois ajustes que a verificação na tela exigiu

Ambos passavam nos testes e ficavam ruins como demonstração:

1. **Conversão do mês saía 0%.** O dashboard conta ganhos e perdidos do **mês corrente**, e eu
   espalhava os 12 ganhos em 180 dias — quase nenhum caía nos poucos dias do mês. Pior, o resultado
   dependia do dia do mês em que alguém rodasse o seed. Metade dos ganhos e dos perdidos passou a
   cair dentro do mês corrente; a outra metade continua espalhada para dar forma ao gráfico.
2. **A caixa abria com 25 de 40 conversas vermelhas.** Tecnicamente correto e péssimo: parece
   operação em crise, não ferramenta que funciona. A proporção de conversas terminando em entrada
   caiu, e agora são 15 esperando, com as três cores representadas.

Também acrescentei 4 contatos criados **hoje**: "Leads hoje" é o primeiro número do dashboard, e
abri-lo zerado com o resto da tela cheia é a pior primeira impressão possível.

---

## 7. Adendo — os gráficos que voltaram (com dado real)

Depois de fechar o bloco, a tela mostrou que a remoção do modo fictício tinha levado junto dois
visuais que não eram "fictícios", só estavam ligados ao dado fictício. Os dois voltaram, agora
alimentados pelo banco:

| Visual | Como ficou |
|---|---|
| **Funil desenhado** (trapézios que estreitam) | usa `d.funil`, que o dashboard já recebia — nenhum backend novo |
| **Rosca de origens** | `GROUP BY origem` no SQL, dentro do payload de `/api/dashboard` |

A agregação de origens entrou no payload existente e não num endpoint próprio: a página já pede
esse payload uma vez ao abrir, e é mais uma consulta agregada em vez de mais uma ida à rede.
Contato **anonimizado fica de fora** — ele foi apagado a pedido do titular, e contá-lo como lead de
um canal manteria o rastro que a anonimização existe para remover.

A **cor** das fatias mora no cliente, diferente da cor da etapa do funil: a etapa tem `cor` porque o
dono a escolhe no cadastro; origem é enum fechado, não há nada a escolher, e mandar hex do servidor
obrigaria uma migration para mudar um tom.

### Três defeitos que só a tela revelou

1. **As abas do gráfico estavam sem estilo.** Escrevi `class="abas"` no PI-4 e o CSS define
   `.abas-serie`. Os botões apareciam como `<button>` cru. Teste de renderização não vê CSS, então
   passou por três blocos sem ninguém notar.
2. **O funil não afunilava.** O seed distribuía os abertos por rodízio — 10, 10, 10, 10 — e o
   desenho saía um retângulo, com a etapa de Venda (12 ganhos acumulados) **mais larga que o topo**.
   A distribuição virou 18 → 11 → 7 → 4, e `larguraFaixa` ganhou teto de 100%: a etapa de ganho
   acumula vendas de meses e pode passar do topo legitimamente, mas o desenho não pode alargar.
3. **A rosca era uniforme.** As 9 origens em rodízio davam nove fatias de ~11% — uma rosca de fatias
   idênticas parece defeito, não dado. Viraram pesos de PME brasileira: Instagram 25%, WhatsApp 22%,
   indicação 17%, e cauda.

Os três passavam em todos os testes de coerência. O teste
`O_FUNIL_AFUNILA_E_A_ROSCA_TEM_FATIAS_DESIGUAIS` passou a fixar a **forma** — cada etapa aberta com
menos que a anterior, e a maior fatia valendo pelo menos o dobro da menor.

Resultado, medido no tenant de demonstração:

```
FUNIL    Novo Lead 18 (100%) → Primeiro Atendimento 11 (72%) → Proposta 7 (56%)
         → Negociação 4 (44%) → Venda 12 (76%)
ROSCA    instagram 25,0% · whatsapp 21,7% · indicacao 16,7% · google 11,7%
         · site 8,3% · facebook 6,7% · qrcode 5,0% · manual 3,3% · outro 1,7%
```

Backend passou a **387 testes** (+2); frontend segue em 89.

---

## Pendências

**Deste bloco:**

1. **O feed de atividades mostra quase só `mensagem`.** Ele ordena por tempo, e as mensagens são os
   eventos mais recentes — então venda, contato e lembrete ficam abaixo da primeira página. É o
   comportamento correto do produto, mas numa demonstração seria melhor ver os quatro tipos de
   cara. Resolver exigiria intercalar por tipo, o que mudaria o feed real; não fiz.
2. **Meu Dia do dono mostra 5 ações** (dos 9 follow-ups pendentes). Os outros estão com os
   vendedores, e o dono só vê os dele e os sem responsável — regra correta. Quem demonstrar o Meu
   Dia cheio deve entrar como vendedor.
3. **O tenant de demonstração fica no mesmo banco dos demais.** Isolado por `empresa_id` como
   qualquer tenant, mas aparece em contagens globais (o seed de feriados, por exemplo, agora vê a
   UF `RN` dele). Sem consequência hoje; registro porque é o tipo de coisa que surpreende depois.
4. **A senha da demonstração está no código** (`demonstracao-2026`), de propósito: é uma conta com
   dados fictícios cujo acesso é para ser compartilhado. Se o tenant algum dia subir num ambiente
   acessível pela internet, isso precisa ser reavaliado.

**Carregadas, ainda abertas:**

5. **Pipeline nunca executou** — o Nexora não é repositório git (PI-3, critério 1).
6. **`<input type="time">` manda `"14:30"` e `TimeOnly` espera `"14:30:00"` → 400.**
   [contato.ts:281](frontend/nexora-painel/src/app/paginas/contato/contato.ts#L281). Desde o PI-1.
7. **Funil do dashboard e do kanban contam diferente.**
   [ServicoDashboard.cs:57-59](src/Nexora.Infra/Servicos/ServicoDashboard.cs#L57-L59) não filtra
   `anonimizado_em IS NULL`. Aberta desde o bloco 9 — é uma linha, e segue aguardando sua decisão.
8. **`paginas/em-breve/` é código morto** (PI-3).
9. **A semente de desenvolvimento (`ServicoSemente`) só roda uma vez por banco** — e-mails fixos
   `@semente.dev` contra `uq_usuarios_email` global (PI-4). Ela é independente deste seed de
   demonstração e continua servindo ao desenvolvimento do dia a dia.
10. **O envio SMTP continua dentro da requisição do reset de senha** (PI-5); 26 UFs sem feriado
    estadual cadastrado; sem lock distribuído no agendador; nenhum celular pareado de verdade;
    arrastar card do kanban nunca testado em navegador.
