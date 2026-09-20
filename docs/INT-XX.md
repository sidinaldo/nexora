# INT-XX — Importar leads do CSV do Meta Lead Ads

O cliente que **anuncia** tem os leads do Formulário Instantâneo do Facebook/Instagram presos no
Gerenciador de Leads da Meta até alguém exportar. Esta é a porta manual, **sem API**, e é de
propósito: ela mede a demanda antes de pagar o preço de OAuth, webhook `leadgen` e revisão de app.

Se ninguém importar, a integração automática não se justifica. Se importarem toda semana, ela se
justifica sozinha.

---

## 1. Os quatro passos

| passo | rota | o que acontece |
|---|---|---|
| subir | `POST /api/importacoes` | lê o arquivo, guarda a importação e **as linhas cruas**. Nada vira contato |
| mapear e conferir | `POST /api/importacoes/{id}/previa` | aplica um mapeamento e diz o que VAI acontecer. Nada vira contato |
| gravar | `POST /api/importacoes/{id}/gravar` | cria os novos, enriquece os repetidos, carimba cada linha |
| acompanhar | `GET /api/importacoes/{id}` | onde ela está — para o arquivo grande, que um job termina |

**O arquivo sobe UMA vez.** É a diferença para a importação da issue #8, onde ele subia na prévia e
de novo na gravação. Guardar as linhas paga três coisas que aquele desenho não permitia: o
mapeamento (uma conversa, e conversa precisa de memória), o processamento em segundo plano (que
começa depois que a requisição acabou) e reprocessar sem pedir o arquivo de novo.

**Limites:** 10 MB e 10.000 linhas por arquivo. Acima de **500 linhas** quem grava é o job.

---

## 2. O mapeamento

O servidor **sugere** (`id`/`lead_id` → id do lead, `created_time` → data de entrada,
`full_name`/`nome`, `phone_number`/`telefone`/`celular`/`whatsapp`, `email`, `campaign_name` →
campanha) e o dono ajusta o resto. As colunas que ele criou no formulário — "Qual seu orçamento?" —
nascem em `ignorar`: ninguém além dele sabe se aquilo é observação.

**Telefone é o único obrigatório.** É por ele que o sistema sabe quem já está na base; sem ele não
há deduplicação possível, e a tela bloqueia o avanço dizendo isso.

### De onde vieram: a pergunta que faltava

⚠️ **Esta tela gravava todo contato como `meta_ads`, fixo no código.** Ela absorveu a importação de
planilha comum (issue #8), cujo caso é a base que o cliente novo sobe no primeiro dia — e com a
origem fixa, a padaria com 800 clientes ficava com 800 "leads de anúncio" no cadastro e no relatório.

Agora a tela pergunta, **já respondida**: se o cabeçalho tem metadados da Meta (`id`,
`created_time`, `campaign_id`…), sugere `meta_ads`; senão, `manual`. Uma coluna mapeada como
**origem** manda por linha, e só quando disser algo que o Nexora conhece — texto não reconhecido
("panfleto da esquina") fica com a escolha do dono, em vez de virar `manual` e atropelá-la em
silêncio (`OrigemLeadTexto.Reconhecer`).

As colunas **observações**, **obs** e **origem** voltaram a ser reconhecidas automaticamente, como
na importação da #8.

Os ids da Meta são guardados **sem o prefixo de tipo** (`l:`, `ag:`, `c:`, `f:`). Com o prefixo, o
mesmo lead teria outro id no dia em que chegasse pela Graph API, e a deduplicação deixaria de casar.

---

## 3. A deduplicação, nesta ordem

1. **telefone ilegível** → inválido. A linha não entra, e não derruba as outras.
2. **`meta_lead_id` já na empresa** → duplicado. É o mesmo lead, exportado duas vezes.
3. **telefone já na empresa** → duplicado, **e o contato existente recebe os `meta_*` nulos**.
4. **repetido dentro do próprio arquivo** → duplicado, apontando a linha que entrou.
5. o resto → importado, com `origem = meta_ads`.

**A ordem 2 → 3 é a regra.** O id da Meta é exato; o telefone é o fallback para o lead que já tinha
entrado pelo WhatsApp com outro nome. Invertida, a linha casaria com a pessoa errada — e o
enriquecimento escreveria os dados do anúncio no contato de outro.

**A 4 não está no spec, e é a que impede o lote inteiro de cair:** a checagem contra o banco não pega
duplicata dentro do arquivo (nenhuma das duas existe ainda), as duas passariam, e o índice único
derrubaria o `SaveChanges` com as outras 9.998 junto.

### Enriquecer é melhor que duplicar

O repetido por telefone **não vira um segundo cadastro**. O contato que já existia recebe
`meta_lead_id`, `meta_ad_id`, `meta_campaign_id`, `meta_form_id` e a campanha — **só onde estiver
nulo**, e só campos que apenas este importador produz. O nome que o vendedor corrigiu e a observação
que ele escreveu ontem ficam intocados.

### `created_time` vira a data de entrada

O lead preencheu o formulário há três dias; entrar como "hoje" poluiria "leads de hoje" no
dashboard. O `InterceptorAuditoria` foi afrouxado para carimbar `criado_em` **só quando ele vem
zerado** — uma linha, com teste próprio.

---

## 4. O arquivo grande

Até 500 linhas grava no próprio request: é a resposta imediata que um export de 60 leads merece.
Acima disso, o clique guarda as escolhas (mapeamento, funil, responsável, aviso) e devolve
`processando`; quem termina é o `MotorImportacoes`, acordado a cada 5 s pelo `AgendadorImportacoes`.

A tela pergunta o progresso a cada 2 s. Os contadores sobem **a cada lote de 500**, então o número
anda — uma barra parada em zero até o fim faria quem está olhando concluir que travou.

### O job ASSUME a empresa da importação

Os outros jobs deste projeto rodam sem tenant e usam `IgnoreQueryFilters` mais um `Where` explícito
— bom para duas ou três consultas. Aqui o job roda **o mesmo caminho de gravação do botão**, que são
dezenas de consultas escritas para um tenant.

Reescrevê-las seria uma segunda cópia da gravação, e a pior delas: a que só roda com arquivo grande,
onde ninguém olha, e onde um `Where` esquecido põe o lead de um cliente na base de outro. Então o
job preenche o `ContextoDeFundo` com a empresa daquela importação, e daí para baixo tudo funciona
como numa requisição dela — inclusive o query filter que protege o isolamento.

### A reserva

`processando_desde` é marcado num `UPDATE ... WHERE processando_desde IS NULL`, num comando só. Duas
instâncias lendo antes de qualquer uma escrever processariam a mesma importação — e o efeito aqui
não é um webhook repetido que o receptor deduplica: é **a mesma pessoa duas vezes na base do
cliente**, com os contadores da primeira rodada sobrescritos pela segunda.

---

## 5. As decisões de produto

**A caixinha "Avisar minhas integrações"** (decidida em 2026-09-19). A tela Integrações promete
`lead.criado` "por qualquer caminho", e a importação era o único caminho mudo. Disparar sempre
mandaria a boas-vindas do sistema do cliente para 3.000 clientes antigos. Então quem decide é ele, na
hora, vendo quantos avisos vão sair — e o padrão vem do servidor: **marcada** no CSV da Meta (o lead
é de ontem), **desmarcada** na planilha comum.

As entregas em massa nascem com `em_massa = true` e a rodada de webhooks as deixa **para o fim da
fila**: a fila é uma só para todas as empresas, e 2.000 avisos de uma importação atrasariam em cinco
minutos o `venda.fechada` de outro cliente.

**A tela absorveu o modal da issue #8.** Duas telas de importar seriam a duplicata que este projeto
passa o tempo desmontando — o CSV do Meta é um CSV com cabeçalho reconhecível, e o mapeamento
generaliza os sinônimos que a #8 fixou no código. O botão "Importar" de `/contatos` leva para
`/importar`.

⚠️ **As rotas da #8 continuam de pé, sem tela** (`POST /api/contatos/importacao{,/previa}`). Elas
ficam até a importação nova ser validada com um export de verdade; depois disso, ou some, ou vira a
API pública de importação — e essa é uma decisão, não um esquecimento.

---

## 6. O que ainda falta

| item | por quê |
|---|---|
| **Critério 1: um CSV real do Gerenciador de Leads** | a fixture dos testes é fiel ao formato de memória, não a um export de verdade |
| decidir o destino das rotas da issue #8 | ver acima |
| importação recorrente, OAuth, webhook `leadgen`, Graph API | fora de escopo deste bloco, por escolha |

---

## 7. Onde está o quê

| peça | arquivo |
|---|---|
| contrato e os passos | `Nexora.Core/Servicos/IServicoImportacaoMeta.cs` |
| mapeamento, ids, datas | `Nexora.Core/LeadAds/MapeamentoMeta.cs` |
| julgamento e gravação | `Nexora.Infra/Servicos/ServicoImportacaoMeta.cs` |
| o job | `Nexora.Infra/Servicos/MotorImportacoes.cs` + `Nexora.Api/Servicos/AgendadorImportacoes.cs` |
| a empresa que o job assume | `Nexora.Core/ContextoDeFundo.cs` |
| as rotas | `Nexora.Api/Controllers/ImportacoesController.cs` |
| a tela | `frontend/.../paginas/importar/` |
| testes | `ImportacaoMetaDbTests`, `ImportacaoEmSegundoPlanoDbTests`, `importar.spec.ts` |
