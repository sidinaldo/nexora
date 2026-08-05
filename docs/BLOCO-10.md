# Bloco 10 — Configurações

Estado: **fechado**. Os 4 critérios verificados por execução.

`dotnet build` e `ng build` limpos, sem warning. **270 testes verdes** (248 antes → 22 novos).

---

## 1. A premissa da seção "ATENÇÃO" estava errada — e isso reduziu o escopo

O prompt manda verificar se as faixas do semáforo e os dias de inatividade têm coluna, e prevê
migrá-las de Options POCO caso estejam em configuração global.

**Elas já são colunas.** O bloco 6 as criou na migration `CamadaDeTempo`. Conferido no banco:

```
dias_sem_resposta_followup | smallint | default 2
semaforo_amarelo_minutos   | smallint | default 60
semaforo_vermelho_minutos  | smallint | default 240
```

Os defaults são exatamente os que o prompt manda preservar (verde até 1h, amarelo de 1h a 4h,
vermelho acima de 4h; follow-up após 2 dias). **Nenhuma migração de Options foi necessária**, e o
bloco 6 já lê do banco desde que foi escrito.

O `schema_nexora_fase1.sql` citado no prompt não existe no repositório — o arquivo é
`docs/SCHEMA-NEXORA.sql`, e ele de fato não prevê essas colunas, porque descreve a fase 1 antes
do bloco 6. O **banco** é que é a verdade aqui, e ele as tem.

---

## 2. Houve migration nova — por outro motivo

`20260804224642_Configuracoes` cria **uma tabela**: `feriados_ignorados (empresa_id, feriado_id)`.

**Por quê.** O prompt exige que feriado nacional "não possa ser apagado, só desativado". Apagar
já era impedido; **desativar não existia, e não tinha onde existir**:

- feriado nacional é linha **global** (`feriados.empresa_id IS NULL`), compartilhada por todos os
  tenants;
- uma coluna `ativo` na própria linha desativaria o feriado para **todas** as empresas;
- a dispensa é, por natureza, por empresa: o comércio de rua fecha no Corpus Christi e o
  e-commerce não.

Uma linha por (empresa, feriado) é o modelo honesto disso — e é a mesma forma que `feriados` já
usa para o caminho inverso (feriado manual, que só o tenant enxerga). Chave **composta**, sem id
próprio: a PK já impede a mesma empresa dispensar o mesmo feriado duas vezes, e reativar é apagar
a linha.

**O efeito colateral que exigiu cuidado:** feriado é lido em quatro lugares, e os quatro
precisaram excluir os dispensados. Se um ficasse para trás, a empresa que "trabalha no Corpus
Christi" teria o follow-up deslizando no motor mas o semáforo descontando o dia — divergência
silenciosa entre duas telas.

| Onde | Arquivo |
|---|---|
| Motor de follow-up (job, sem tenant) | `DadosFollowUp.FeriadosAsync` |
| Semáforo do cliente | `ServicoPainel.StatusAsync` |
| Minutos úteis do Meu Dia | `ServicoMeuDia.MeuDiaAsync` |
| Lista da tela | `ServicoFeriados.ProximosAsync` |

Na lista, o dispensado vem **marcado**, não filtrado: sumir com ele esconderia do dono a decisão
que ele mesmo tomou. A tela o mostra riscado, com "Voltar a fechar".

---

## 3. O que foi criado

### Backend

| Arquivo | O que é |
|---|---|
| `Core/Entidades/FeriadoIgnorado.cs` | A dispensa por empresa |
| `Core/Servicos/IServicoConfiguracao.cs` + `Infra/Servicos/ServicoConfiguracao.cs` | Ler e gravar a configuração, com as validações |
| `Api/Controllers/ConfiguracaoController.cs` | `GET /api/configuracao`, `PUT .../empresa`, `PUT .../atendimento` |
| `IServicoFeriados` + `ServicoFeriados` | Ganharam `IgnorarAsync` / `ReativarAsync`; `ProximosAsync` passou a marcar o dispensado |
| `FeriadosController` | `POST /{id}/trabalha` e `DELETE /{id}/trabalha` |
| `IServicoEquipe` + `ServicoEquipe` | Ganharam `MinhaContaAsync` e `AtualizarMinhaContaAsync` |
| `ContaController` | `GET /api/conta` e `PUT /api/conta` |
| Migration `Configuracoes` | A tabela acima |

### Frontend

| Arquivo | O que é |
|---|---|
| `nucleo/servicos/configuracao.servico.ts` | As 10 chamadas |
| `paginas/configuracoes/` | Dados da empresa, atendimento, semáforo, follow-up, feriados |
| `paginas/conta/` | Minha conta completa — substitui `conta-senha` |
| `app.routes.ts` | `/configuracoes` (com `guardaDono`), `/conta`, e `/conta/senha` redirecionando |
| `layout/shell/shell.html` | Link "Configurações" e o rodapé apontando para `/conta` |
| `auth.servico.ts` | `atualizarNome`, para a barra lateral não mostrar o nome antigo |

A página `conta-senha` foi **removida**; a rota antiga redireciona, porque havia link para ela na
sidebar e um 404 depois de reorganizar a tela seria gratuito.

---

## 4. Onde vive o enforcement de papel

**No controller, e só lá.** `[Authorize(Roles = "dono")]` nos PUT de configuração e nas três
rotas de feriado que alteram estado. A leitura é `[Authorize]` simples: o vendedor precisa saber
que horas a empresa atende, e esconder isso dele não protege nada.

Não repeti a checagem dentro do serviço. Duas regras para a mesma coisa divergem no dia em que
uma muda — e a que fica para trás é sempre a que ninguém lembra que existe.

Como a regra mora num atributo, o teste dela **lê o atributo**: se alguém o remover, o teste
quebra; se alguém mexer no serviço, não deve quebrar, porque a regra não está lá. Isso está
verificado também sobre HTTP (§6), que é a prova real.

A tela esconde os controles para quem não é dono — oferecer botão que sempre dá 403 é pior que
não oferecer —, mas quem decide é o servidor.

---

## 5. As validações, e por que cada uma existe

| Regra | Se passasse |
|---|---|
| Abertura antes do fechamento | `ck_empresas_janela` barraria, mas com mensagem de constraint |
| **Pelo menos um dia marcado** | A empresa nunca atenderia: **nenhum** follow-up dispararia e o semáforo **nunca** acenderia. Sem erro, sem log, sem nada para investigar — o dono só descobriria pelo faturamento |
| Vermelho maior que amarelo | A conversa pularia o amarelo e nasceria vermelha |
| **Faixa zero é ACEITA** | Zero **desliga** a cor, e isso é escolha legítima. Recusar seria impor preferência |
| **Mínimo 1 dia de inatividade** | Zero geraria follow-up para conversa respondida **hoje** — o robô escrevendo para quem acabou de ser atendido. É o caminho mais rápido para o número ser denunciado |
| Documento com 11 ou 14 dígitos | Guardado só com dígitos; máscara é da tela |
| Feriado duplicado na data | Dois feriados no mesmo dia, e o motor consultando os dois à toa. Vale também contra os **nacionais** |

Uma recusa não grava nada pela metade — há teste que confere as seis colunas após cada rejeição.

---

## 6. Como cada critério foi verificado

| # | Critério | Resultado |
|---|---|---|
| 1 | `dotnet build`, `ng build` limpos, `dotnet test` verde | 270 testes, zero warning |
| 2 | As 7 validações de integração | Todas, mais 15 outras |
| 3 | Alterar a janela muda o comportamento da rodada do bloco 6 | **Teste que roda o motor duas vezes** |
| 4 | Alterar faixa muda a cor sem redeploy | Verificado no servidor e sobre HTTP |

### Critério 3 — o teste que fecha o bloco

`MUDAR_A_JANELA_PELA_CONFIGURACAO_MUDA_O_COMPORTAMENTO_DA_RODADA` roda o motor do bloco 6 **duas
vezes**, com a mesma conversa parada e o mesmo relógio (10h30 de uma quinta), mudando **só a
janela** pela API de configuração:

```
1ª rodada — janela 8h-20h, 10h30 está DENTRO
            gerados=1  enviados=1  Evolution chamada 1 vez

    o dono estreita a janela para 8h-9h

2ª rodada — mesma conversa, mesma hora, janela agora 8h-9h -> FORA
            gerados=1  enviados=0  adiados=1  Evolution NÃO foi chamada
```

Mais `Mudar_dias_de_inatividade_muda_quem_e_elegivel`: com 7 dias, a conversa parada há 3 deixa
de ser elegível; voltando a 2, volta a ser. **Configuração que não muda comportamento não é
configuração** — esses dois testes são o que impede a tela de virar enfeite.

### Critério 4 — o semáforo sem redeploy

A cor é calculada no cliente a partir do timestamp; o que o servidor manda são os **limites**.
Então "mudar sem redeploy" significa o `/api/painel/status` devolver os limites novos na leitura
seguinte — no máximo 45s depois, que é o intervalo do poll do shell.

```
antes  amarelo=60  vermelho=240
PUT /api/configuracao/atendimento  (amarelo=15, vermelho=45, janela 9h-18h, seg-sex)
depois amarelo=15  vermelho=45  janela=9h-18h  dias=62
```

Verificado em teste (`MUDAR_A_FAIXA_DO_SEMAFORO_CHEGA_NO_PAINEL_SEM_REDEPLOY`) e sobre HTTP.

### O fluxo completo sobre HTTP

Com a API de pé contra o `nexora_dev`, com dois tokens (dono e vendedor):

```
LEITURA aberta a qualquer papel
  GET /configuracao (dono)     200
  GET /configuracao (vendedor) 200

ESCRITA só do dono
  PUT /configuracao/atendimento (vendedor)  403
  PUT /configuracao/empresa (vendedor)      403
  PUT /configuracao/atendimento (dono)      204

VALIDAÇÕES
  janela invertida            400  "O horário de abertura precisa ser antes do de fechamento."
  nenhum dia marcado          400  "Marque pelo menos um dia da semana em que a empresa atende."
  amarelo > vermelho          400  "O tempo do alerta vermelho precisa ser maior que o do amarelo."
  zero dia de inatividade     400  "O follow-up precisa de pelo menos 1 dia de conversa parada."
  faixa ZERO                  204  aceita — desliga a cor

FERIADOS  (19 na base)
  apagar NACIONAL             409  "...não pode ser apagado. Se a empresa atende nesse dia,
                                    marque-o como dia de trabalho."
  marcar dia de trabalho      204  e a lista passa a mostrá-lo marcado
  vendedor tentando o mesmo   403
  voltar a observar           204
  feriado duplicado           409  "Já existe um feriado nessa data."

MINHA CONTA
  GET /conta (vendedor)       200
  PUT /conta (nome novo)      204
  e-mail de outro usuário     409  "Este e-mail já está em uso por outra conta."
  e-mail inválido             400
```

O banco de desenvolvimento foi restaurado aos defaults depois (8h-20h, 126, 60/240, 2 dias; zero
feriado manual, zero dispensa).

**O que não foi verificado por renderização:** não tenho navegador nesta sessão. Conferi os
payloads, os códigos HTTP e as mensagens que cada tela consome, não os pixels.

---

## 7. Decisões próprias

1. **`PUT /api/conta` não recebe id.** O alvo é sempre o usuário do token — é o que permite a
   rota ser `[Authorize]` simples sem abrir a porta para um vendedor editar a conta de outro.
2. **Trocar o e-mail não reemite o JWT.** Reemitir token por causa de um dado de cadastro
   trocaria a sessão sem necessidade. Como o nome aparece na barra lateral e vem do token, o
   `AuthServico.atualizarNome` atualiza a cópia local — senão o usuário salvaria e continuaria
   vendo o nome antigo, achando que não gravou.
3. **Feriado manual não pode cair na data de um nacional.** Deixar passar criaria dois feriados
   no mesmo dia para o motor consultar à toa.
4. **A mensagem de recusa do nacional ensina o caminho:** "…marque-o como dia de trabalho". Só
   recusar deixaria o dono achando que a tela está quebrada.
5. **`ServicoConfiguracao` não injeta `IContextoEmpresa`.** O query filter de `empresas` já é
   `x.Id == contexto.EmpresaId`, então `db.Empresas.First()` só pode devolver a empresa da
   requisição. Injetar o contexto para reafirmar o tenant seria uma segunda regra de isolamento
   sobre a mesma linha.
6. **A tela responde "quando cada mudança passa a valer"** num bloco fixo, porque o vendedor vai
   perguntar: faixas do semáforo valem na hora, janela e follow-up da próxima rodada em diante.
7. **Aviso em tom de alerta só no "nenhum dia marcado".** É a única configuração desta tela cuja
   consequência é silenciosa, e por isso a única que merece o vermelho.
8. **`conta-senha` virou `conta`**, com a rota antiga redirecionando.

---

## 8. Pendências

### Deste bloco

| Limite | Consequência |
|---|---|
| **Fuso horário não é editável** | `empresas.fuso_horario` existe e é lido, mas a tela só exibe. Cliente em Manaus ou Rio Branco precisa de `UPDATE` |
| **Sem UF da empresa** | Feriados **estaduais** continuam sem ser semeados desde o bloco 6 — falta a coluna `uf` para saber a qual estado a empresa pertence |
| **Feriado só do ano corrente e do próximo** | É o alcance do seed. Cadastrar manual para 2029 funciona; nacional de 2029 só aparece em 2028 |
| **Sem histórico de quem mudou o quê** | `atualizado_em` diz quando, não quem |
| **Não renderizado em navegador** | Payloads e códigos conferidos; pixels não |

### Carregadas dos blocos anteriores

- **Nenhum telefone pareado** (desde o bloco 3) — o único item com risco em vez de volume.
- **Arrasto do kanban não testado em navegador** (bloco 8).
- **Nenhum envio de e-mail** (desde o bloco 1).
- **`ServicoCadastroEmpresa` sem controller** — não há como criar empresa pela API. Este bloco
  configura uma empresa que existe; criar a primeira ainda é SQL.
- **Sem endpoint de série temporal** — o gráfico do dashboard só roda no modo demonstração.
- **Sem lock distribuído** no agendador de follow-up.
- **Nenhum teste de frontend.**
- `senhas-dev.sql` na raiz, com senha em texto puro.

---

## 9. Estado das 13 telas

| Tela | Estado |
|---|---|
| Login · Aceitar convite · Redefinir senha | prontas |
| Caixa de entrada · Meu Dia · Equipe · Conexão | prontas |
| Funil · Contatos · Contato (detalhe) | prontas |
| Dashboard | pronta |
| **Minha conta** | **pronta** — era parcial (só senha) |
| **Configurações** | **pronta** |

**13 de 13.**

---

## 10. O que falta para a fase 1 estar vendável

O produto deixou de precisar de `UPDATE` no banco para operar. O que resta:

1. **Parear um telefone e mandar mensagem de verdade** — o único item com risco em vez de volume,
   e pendente desde o bloco 3.
2. **Onboarding**: expor o `ServicoCadastroEmpresa` para a primeira empresa nascer pela tela.
3. **Envio de e-mail** — convite e reset de senha ainda são links copiados à mão.
4. **Testar o arrasto do kanban num navegador.**
