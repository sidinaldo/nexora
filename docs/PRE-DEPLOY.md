# PRÉ-DEPLOY — o que mudou antes dos 3 clientes piloto

Escrito em 2026-09-29, antes de alugar a máquina nova. O VPS de teste (Contabo, um mês) expirou e a
API saiu do ar; o painel no Cloudflare Pages continuou servindo a tela de login, então **quem
abrisse o link via o formulário e não entrava, sem erro nenhum que dissesse o motivo.**

Esse detalhe decidiu metade deste bloco.

## Por que tudo junto, antes da máquina

O passo 8 do `deploy/README.md` — aplicar as migrations — é **manual**, e a própria tabela de
limites registra que *"um deploy que esqueça o passo 8 sobe a API contra schema velho"*. Subir duas
vezes dobra a chance de esquecer. Então o que muda o repositório vai todo antes.

---

## 1. `/api/etapas` — o defeito que o piloto encontraria no primeiro dia

A política `ConfigurarEmpresa` estava na **classe**, e o `[HttpGet]` a herdava. Vendedor e gestor
recebiam 403.

⚠️ **E falhava em silêncio.** Quem mais consome esse `GET` não é a tela de configuração: é o
**seletor de etapa da tela de contato**, que qualquer vendedor abre o dia inteiro. O
`catchError(() => of([]))` do painel — escrito com a intenção certa, *"um seletor vazio é ruim, a
tela não abrir é pior"* — transformava o 403 num **seletor vazio e mudo**.

O padrão já existia, e não precisou ser inventado: `EtiquetasController` e `PipelinesController`
fazem exatamente isto. E o primeiro **citava o `EtapasController` pelo nome como contra-exemplo** —
essa frase saiu, porque deixou de ser verdade.

Seis sabotagens, seis pegas: devolver a política para a classe derruba o `RotasPorPermissaoTests`, e
tirar a política de cada uma das cinco escritas derruba a linha correspondente do dicionário.

## 2. `mem_limit` nos cinco serviços

Não existia limite de recurso em **nenhum** compose do projeto.

⚠️ Sem limite, quem estoura a memória não é quem morre: o kernel mata o **maior** processo, que
quase sempre é o Postgres. Um vazamento na Evolution derrubaria o banco e, com ele, os três clientes
de uma vez.

`mem_limit` e não `deploy.resources`: este arquivo é compose standalone, não swarm — a chave
`deploy:` seria **ignorada em silêncio**, e o limite pareceria estar lá sem estar.

Valores por variável com padrão (`${LIMITE_API:-1g}`), que é o idioma que o arquivo já usa, para a
mesma composição servir 4 GB e 8 GB.

## 3. Swap — a outra metade

`vm.swappiness=10` e 4 GB de swapfile. O limite impede o contêiner de crescer sem fim; o swap
transforma o pico que sobra em **lentidão** em vez de morte. Os dois juntos é que fazem um cliente
cair sozinho.

## 4. Backup automático, cifrado, para bucket

Não existia nada: nem cron, nem systemd timer, nem `schedule:` no CI, nem destino externo, nem
cifragem. Havia comandos para digitar à mão e um `scp` que dependia de alguém lembrar. E a
restauração **nunca tinha sido testada**.

`deploy/backup.sh` faz as quatro peças — os dois bancos, a mídia e o volume de sessão da Evolution —
e três decisões merecem estar escritas:

**Cifra no fluxo, sem arquivo em claro no meio.** Escrever o dump e cifrar depois deixa a versão
legível no disco entre os dois comandos — e se o script morrer no meio, ela **fica lá**.

**Recusa rodar sem `BACKUP_SENHA`.** Os arquivos contêm conversa, telefone e foto de **terceiros**:
gente que nunca ouviu falar do Nexora e não escolheu confiar em nós.

**Não apaga nada no bucket.** A retenção remota é regra de ciclo de vida lá, não comando daqui —
uma credencial que pode apagar backup é uma credencial que um invasor usa para apagar backup.

E confere o tamanho antes de expurgar o local: `pg_dump` não considera erro produzir pouca coisa, e
sem essa checagem o cron reportaria sucesso para quatro arquivos de zero byte.

⚠️ **Falta o passo que nenhum script faz por você:** restaurar uma vez, num banco descartável, antes
do primeiro cliente. Backup não testado é um arquivo, não um backup.

## 5. Monitoramento externo — e por que NÃO um healthcheck

O plano original dizia `healthcheck` no serviço `api`. **Estava errado, por duas razões.**

A primeira é que o `Dockerfile` já tinha a decisão registrada: *"Sem HEALTHCHECK aqui de propósito: a
imagem runtime não traz curl nem wget […] Quem observa é o orquestrador."*

A segunda é mais grave, e era um erro meu de fato: **em compose standalone o Docker não reinicia
contêiner `unhealthy`.** Reiniciar por healthcheck é comportamento de Swarm. Um healthcheck interno
mudaria uma coluna do `docker compose ps` que alguém teria que ir olhar — o que é o mesmo silêncio
com passos a mais.

E não teria ajudado **no caso que de fato aconteceu**: a máquina inteira fora do ar, com o Docker
junto. Nenhum verificador que roda dentro dela poderia avisar.

O que resolve é um `GET /health` de fora, a cada cinco minutos, que manda mensagem para uma pessoa.
Ficou como seção própria no `README`, e os dois itens correspondentes saíram da tabela de limites —
`Backup manual` e `Sem monitoramento` deixaram de ser limites aceitos.

---

**1275 testes de backend, 446 no painel**, build limpo com `-warnaserror`, `compose config -q` válido
com os cinco limites resolvendo, e `bash -n` no script.

⚠️ **O que só é verificável com máquina:** o `mem_limit` valendo sob pressão, o cron disparando, o
envio para o bucket e a restauração. São roteiro no `README`, não promessa deste commit.
