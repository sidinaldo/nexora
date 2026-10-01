# OPE-1 — A área do operador

> Os números de cada cliente, um catálogo de planos, e os limites por empresa. Em `/operacao`, fora
> do painel do cliente.

## 1. O pedido, e as duas premissas que ele trazia

> *"Preciso criar um plano para adicionar um usuário super para visualizar apenas números e
> configurar empresas; ex: aumentar o plano para valor x tem direito a xy conexões e usuários."*

Antes dele veio outro, que delimitou o escopo:

> *"Para entrar no sistema preciso de um usuário gestor, certo? Esse usuário deve permitir criar
> empresas."*

As duas premissas estavam erradas, e registrá-las importa porque é o que explica o desenho:

- **`gestor` é papel DENTRO de uma empresa**, criado por convite de quem já está lá. O primeiro
  usuário de um cliente é o **dono**, e ele nasce junto com a empresa, na mesma transação.
- **Nenhum papel cria empresa — nem o dono.** São três papéis e sete permissões
  (`src/Nexora.Core/Seguranca/Permissoes.cs`), e nenhuma delas é "criar empresa".

E uma terceira coisa que o pedido supunha existir e não existia: **teto de usuários**. Não havia
nenhum, em lugar nenhum.

## 2. Por que NÃO há um "usuário super"

O pedido pedia um usuário. A entrega não tem um, e a razão é estrutural.

Todo login deste sistema carrega **exatamente um `empresa_id`**, e **26 filtros globais de consulta**
dependem disso. Um usuário que atravessa empresas não é uma permissão a mais — é um furo no alicerce.

E não é hipótese: o sistema anterior (Recupera) **tinha** super-admin — `ClaimSuper`, `ClaimEscopo`,
`ClaimAdminId`, policy `SuperAdmin` — e tudo foi removido de propósito na portabilidade
(`docs/BLOCO-1.md:19-20,32,34`).

**A credencial aqui é a chave de administração que já criava empresa**, no cabeçalho `X-Chave-Admin`.
Nenhum papel novo, nenhum claim novo, nenhuma linha em `usuarios`, nenhum dos 26 filtros tocado.

O que a chave não dá — identidade e revogação por pessoa — vem do **Cloudflare Access**, na frente das
rotas. Ver §8.

### A alternativa recusada

Um quarto papel (`plataforma`) num usuário comum seria o pior dos dois mundos: poria um usuário que
atravessa empresas **dentro do próprio modelo de tenant**, que é exatamente o que os filtros existem
para impedir, e exigiria `IgnoreQueryFilters` espalhado por toda consulta que ele alcançasse.

## 3. A decisão central: o plano é um molde, não um ponteiro vivo

**Atribuir um plano a uma empresa COPIA os limites para a linha dela. Editar o plano depois NÃO muda
ninguém.**

Limite é **contrato**, e contrato de quem assinou em março não muda porque a tabela de preços mudou em
agosto.

Três consequências, todas desejadas:

| | |
|---|---|
| `empresas.plano_id` é **rótulo** | Quem manda são `limite_conexoes` e `limite_usuarios` na linha da empresa |
| O ponto de validação **não mudou uma linha** | `ServicoConexoes.CriarAsync` continua lendo da empresa — exatamente o que o comentário de `Empresa.LimiteConexoes` previu que aconteceria quando planos existissem |
| A exceção por cliente sai de graça | *"Esse cliente negociou uma conexão a mais"* é ajuste na empresa, sem tirá-la do plano e sem criar um plano de uma linha só |

E resolve a pergunta difícil que um catálogo faz na primeira semana — *"quando eu mudar o Pro de 3
para 5 conexões, quem já está no Pro ganha as 5?"* — sem precisar respondê-la: ninguém é
surpreendido, nunca.

### O que `preco` não é

**Não cobra nada.** Não existe cobrança neste sistema: nem assinatura, nem gateway, nem fatura. É o
registro do que foi combinado, lido por uma pessoa — e a tela diz isso em palavras, porque um campo
de preço ao lado de um botão "atribuir plano" se lê como se mudasse o que o cliente paga.

Moeda é BRL e **não é coluna**: o produto é Brasil de ponta a ponta, e uma coluna com um único valor
possível é coluna que ninguém lê. A constante existe para a suposição ser achável.

### Plano não se apaga

Só se arquiva. `empresas.plano_id` é o único traço do que foi vendido, e uma empresa cujo plano sumiu
fica com limites sem explicação. Uma regra em vez de duas ("apaga se não usado, arquiva se usado").

## 4. A cota de pessoas

**Contam `ativo` + `convidado`; `inativo` não conta.** A regra não nasceu aqui — está em
`docs/SCHEMA-NEXORA.sql`, escrita quando o status virou enum de três valores e limite nenhum existia.

### Duas portas, e três caminhos que não cobram

Cobrada em `ConvidarAsync` **e** no ramo `inativo → ativo` de `AtualizarAsync`. Só na primeira, o teto
seria burlável em três cliques: desativa três, convida três, reativa três.

Não cobram, cada um por um motivo diferente:

- **`ReenviarConviteAsync`** — o convite já ocupa a vaga; cobrar de novo recusaria reenviar um convite
  que já está pago;
- **`AceitarConviteAsync`** — a vaga foi reservada no convite **exatamente para o aceite nunca poder
  falhar**. Cobrar ali faria alguém convidado na segunda, com o limite baixado na terça, ser barrado
  na quarta — com um link válido na mão e nenhuma ação possível do lado dele;
- **`ServicoCadastroEmpresa`** — a empresa nasce com uma pessoa e o piso do CHECK é 1.

### Estar acima do limite é estado legal

O operador pode baixar o teto abaixo do uso — é assim que se registra um downgrade. Quando isso
acontece, **ninguém é deslogado, ninguém é desativado, nenhuma conexão é apagada**. O único efeito é
o próximo convite falhar.

A alternativa seria o software escolher quais 2 de 5 funcionários perdem acesso. Ninguém desenhou essa
escolha, ninguém escreveu a mensagem para quem fosse sorteado, e ela entraria por efeito colateral de
um operador digitando um número menor.

Mas o operador é avisado em duas camadas: **o uso aparece ao lado do limite** em toda resposta, e
baixar abaixo do uso exige `confirmarExcedente` — sem ele, 409 explicando que ninguém perde acesso.

### O backfill da migration

```sql
UPDATE empresas e SET limite_usuarios = GREATEST(3,
  (SELECT COUNT(*) FROM usuarios u
    WHERE u.empresa_id = e.id AND u.status IN ('ativo','convidado')));
```

Sem ele, um `DEFAULT 3` seco poria qualquer empresa com 4+ pessoas acima da cota no instante da
migration, e o sintoma apareceria dias depois, no convite seguinte do dono.

**Sem `LEAST(50, …)`, de propósito**: uma empresa de 51 pessoas faz a migration falhar alto. Limitar o
time real de um cliente a 50 em silêncio é perda de dado que ninguém encontra por meses.

E a ordem importa: **backfill antes do CHECK**. Invertido, o erro apontaria para uma linha de dados em
vez de para a regra violada.

## 5. A leitura que atravessa empresas

A lista do operador é **a única leitura deste sistema que atravessa todos os tenants a pedido de um
humano**. SQL cru, paginando `empresas` primeiro e juntando os agregados à página por `LATERAL` — o
custo acompanha o tamanho da página, não o da tabela.

Quatro barreiras independentes impedem uma sessão de cliente chegar nela:

1. **a rota ignora o JWT.** `[AllowAnonymous]` é o certo aqui e é contraintuitivo: `[Authorize]`
   tornaria um token de **cliente** necessário para alcançar a área do operador;
2. **o serviço lança alto** se for alcançado com tenant no contexto;
3. **nada no `ServicosInfra`** injeta `IServicoOperador` fora do controller;
4. **testes provam 2 e 3.**

### O que a lista não mostra, e é deliberado

Nenhum nome de contato, telefone, texto de mensagem ou e-mail da equipe. A área responde *"como vai
este cliente"*, não *"o que ele está conversando"* — e a linha é de desenho: os campos nem chegam do
servidor.

O e-mail do **dono** fica fora da lista e aparece só no detalhe de uma empresa. Cinquenta endereços
numa tabela é um diretório de pessoas; um endereço na tela onde se vai mudar o plano é o contexto da
ligação que se vai fazer.

### A métrica que vale mais que as outras

`primeira_mensagem_em − criado_em` — o intervalo entre a empresa assinar e o produto funcionar. O
próprio `Empresa.cs` a chama de *"a métrica que prevê abandono melhor que qualquer outra"*. Reusar a
que já existe vale mais que inventar um índice de saúde ao lado dela.

Vazia significa **nunca pareou o WhatsApp**, e é o sinal de abandono mais antigo desta base.

## 6. A trilha

Mudança de plano ou de limite deixa linha em `auditoria`, com ator **`Operador`**.

Duas coisas tiveram de mudar para isso funcionar, e as duas eram armadilha:

**O ator era decidido por eliminação.** `usuarioId == 0` virava `Sistema`. A área do operador também
roda sem JWT, então toda ação dela apareceria como *"a rodada automática fez isso, sem humano
envolvido"* — a autoria falsa que o comentário do próprio `AtorAuditoria` proíbe.

**E a escrita simplesmente falharia.** `auditoria.empresa_id` vem do contexto, é `NOT NULL` e é FK
`RESTRICT`. Sem sessão ele vale 0, o INSERT da trilha viola a FK, e — como ela entra no mesmo
`SaveChanges` do fato — a alteração volta atrás inteira, com um 23503 cru. Daí o
`ContextoDeFundo.Assumir(empresaId, 0)` antes de escrever: não é organização de código, é o que faz a
escrita acontecer.

O e-mail que o Cloudflare Access informa vai **dentro do `alteracoes`**, nunca no `usuario_id`: é
afirmação da borda, não prova, e gravá-lo como autor seria a mesma autoria falsa por outro caminho.

> ⚠️ A escrita **carrega a entidade e usa `SaveChanges`**, nunca `ExecuteUpdateAsync`. É um update de
> uma linha e duas colunas — o atalho mais tentador que existe —, e há cinco precedentes em `empresas`
> que o fazem parecer idiomático. Ele pula os dois interceptors: `atualizado_em` para de ser escrito e
> a trilha some, sem erro nenhum. Há teste que lê a **tabela**, não o objeto.

## 7. `planos` é a única tabela sem filtro de tenant

Não tem `empresa_id` e não tem `HasQueryFilter`. É catálogo: o mesmo para todo mundo, e só o operador
escreve nele. Uma sessão de cliente que consultasse `db.Planos` veria a lista inteira — aceitável,
porque nome e preço de plano é o que uma página de vendas publica.

> ⚠️ **O que tornaria isto um vazamento é uma coluna POR CLIENTE ali**: desconto negociado, número de
> contrato, data de renovação, observação do comercial. Qualquer um deles transforma uma tabela global
> inofensiva em leitura cruzada de tenant, sem nenhum erro e sem nenhum teste reprovando.
>
> Dado por cliente mora em `empresas`, que tem filtro. Esta tabela é molde, e molde não tem dono.

A exceção está registrada em `PlanosDbTests`, no lugar onde quem ler "toda tabela é filtrada" vai
procurar a que não é.

## 8. Cloudflare Access — o que falta configurar

Sem código. No painel da Cloudflare, Zero Trust → Access → Applications:

1. criar uma aplicação do tipo **Self-hosted**;
2. domínio `nexora.softioconsultoria.com.br`, caminho `/operacao` (e `/criar-empresa`, se quiser o
   mesmo portão lá);
3. política permitindo apenas os e-mails que operam;
4. conferir que o Access está injetando `Cf-Access-Authenticated-User-Email` — a trilha passa a
   registrar quem agiu.

**A chave continua sendo a credencial da API.** O Access protege a página; ele não substitui o
portão do servidor.

> ⚠️ **A aplicação NÃO exige o cabeçalho**, e isso é decisão. Exigi-lo trocaria um controle que
> funciona (a chave, comparada em tempo constante) por outro que não está nas nossas mãos: no dia em
> que o Access tiver problema, a área do operador ficaria indisponível — e é justamente o dia em que
> se quer entrar para desativar alguém.

## 9. Riscos que ficaram

**A janela de 45 segundos.** Desativar uma empresa derruba todo mundo no próximo poll do painel, não
na hora. A correção completa seria um carimbo conferido na validação do token, ao custo de uma ida ao
banco por requisição ou de um cache que precisa ser invalidado. O caminho escolhido pega ~99% do valor
por ~0% do custo, e o que ele não pega é uma requisição solta dentro da janela.

**A chave num navegador.** Ela passou a viver numa aba e a atravessar a área de transferência. Extensão
com permissão no domínio, ou qualquer XSS no painel, passa a poder roubá-la — antes era só roubo de
sessão. Registrado também em `docs/INF-1.md` §8.

**A corrida da cota.** Dois convites simultâneos passam os dois pela contagem. Sem trava, pelo mesmo
motivo do limite de conexões: quem clica é o dono, numa tela de configuração, um clique por vez, e o
estrago é uma linha a mais, não dado corrompido. Se um dia importar, o lugar é um advisory lock por
empresa.

**O `Down()` da migration perde estado comercial.** É o único deste sistema que apaga dado de negócio
em vez de estrutura: o catálogo inteiro, qual plano cada empresa tinha, e o teto de pessoas de cada
uma. Reverter e reaplicar não devolve.

## 10. Dois buracos antigos que este bloco achou

Nenhum dos dois é do OPE-1; os dois apareceram porque uma regra nova passou a varrer o que já existia.

**`GET /api/convite/{token}` e `GET /api/redefinir/{token}` não tinham rate limit nenhum.** O
limitador global particiona por usuário e devolve `NoLimiter` para anônimo — então rota anônima sem
política nomeada **não tem teto**. Ganharam `PolConsultaToken`, 30/min **por IP**.

Por IP e não por IP+token como a política vizinha, e a diferença importa: quem sonda token *varia* o
token, então uma partição que o inclui dá um balde novo a cada tentativa e não limita nada.

**`docs/SCHEMA-NEXORA.sql` estava defasado em nove colunas** de `empresas`, de blocos anteriores.
Acrescentar só as duas novas o deixaria igualmente mentiroso; a tabela foi corrigida inteira, e agora
spec e modelo batem em 23 colunas.

## 11. Verificação

```
dotnet test nexora.sln        # 1332
npm run test:ci               # 478
npm run test:celular:ci       # 105
```

**31 sabotagens**, cada uma derrubando exatamente o teste certo. Três delas desmascararam testes
**meus** que passavam pelo motivo errado — vale registrar, porque é o argumento inteiro a favor da
prática:

| Sabotagem | O que ela revelou |
|---|---|
| o 401 limpa o formulário | o teste lia o DOM **velho**: o `[ngModel]` propaga por microtask, e sem esperar a asserção passava com o formulário sendo limpo |
| o 409 do excedente vira erro comum | o teste afirmava a frase, que é a mesma nos dois casos. O que distingue é o **botão de confirmar** |
| a conferência de `ativo` ganha consulta própria | a primeira versão da sabotagem **não compilava**, e o aferidor leu falha de build como "nenhum teste caiu" |

> ⚠️ **Antes de mesclar:** rodar a migration contra uma cópia restaurada do backup de produção e
> conferir que nenhuma empresa ficou acima da cota. E mesclar exige rodar `deploy/deploy.sh` na VPS —
> sem isso a API velha sobe contra schema novo.
