using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Semeia o tenant de DEMONSTRAÇÃO: uma empresa completa, com dados dentro, para a
/// demonstração ser *usar o produto* em vez de olhar uma tela com números inventados.
///
/// ===================== POR QUE UM TENANT, E NÃO UM ENDPOINT FICTÍCIO =====================
/// O modo anterior (`/api/dashboard/demo`) resolvia UMA tela e deixava todas as outras vazias:
/// dashboard bonito, e depois caixa sem conversa, funil sem card, Meu Dia sem ação. Pior, os
/// números do dashboard fictício não passavam por consulta nenhuma — não provavam que o produto
/// funciona, só que o gerador funciona.
///
/// Com dado no banco, são os MESMOS serviços, as MESMAS consultas e as MESMAS telas. Se a
/// demonstração está bonita, o produto está funcionando.
/// ========================================================================================
///
/// ===================== AS TRÊS BARREIRAS DE SEGURANÇA =====================
/// Contato tem telefone, e o motor manda mensagem para telefone. Se este tenant estivesse
/// pareado a uma instância real da Evolution, a rodada dispararia para números de estranhos.
///
///   1. `TelefoneDemonstracao` — DDD 00, que não existe no plano nacional;
///   2. `empresas.demonstracao` — tira o tenant da rodada do `MotorFollowUp`;
///   3. `EnviadorMensagem` recusa o disparo, no ponto onde todo envio passa.
///
/// Este serviço depende das três, e não substitui nenhuma.
/// ==========================================================================</summary>
public class ServicoSeedDemonstracao(
    NexoraDbContext db,
    IServicoCadastroEmpresa cadastro,
    TimeProvider relogio,
    ILogger<ServicoSeedDemonstracao> log) : IServicoSeedDemonstracao
{
    /// <summary>O e-mail do dono é a CHAVE de idempotência: é único globalmente
    /// (`uq_usuarios_email`), então achá-lo é achar o tenant, sem precisar de coluna marcadora
    /// além da própria `demonstracao`.</summary>
    public const string EmailDono = "ana.demo@nexora.exemplo";

    public const string Senha = "demonstracao-2026";
    public const string NomeEmpresa = "Oficina Central (demonstração)";

    /// <summary>Semente FIXA. Mesma execução, mesmos dados — captura de tela reproduzível e
    /// teste estável. Sem isto, um teste que afirma "6 ganhos" quebraria a cada execução.</summary>
    private const int Semente = 20260805;

    // ---- vocabulário: PT-BR plausível, nenhuma pessoa ou empresa real ----
    private static readonly string[] Nomes =
    [
        "Marcos Antunes", "Juliana Prado", "Rafael Bezerra", "Camila Nogueira", "Diego Vasques",
        "Patrícia Sales", "Bruno Carvalho", "Letícia Moura", "Gustavo Peixoto", "Renata Aguiar",
        "Fábio Toledo", "Simone Barreto", "André Quintela", "VanessaRocha", "Leonardo Pires",
        "Tatiane Furtado", "Rodrigo Menezes", "Cristina Sampaio", "Eduardo Bastos", "Larissa Coelho",
        "Márcio Fontes", "Adriana Vilela", "Thiago Rezende", "Beatriz Amaral", "Otávio Cardim",
        "Sandra Loureiro", "Vinícius Paiva", "Carla Bittencourt", "Henrique Dantas", "Mônica Serrano"
    ];

    private static readonly string[] Negocios =
    [
        "Oficina", "Clínica", "Salão", "Autopeças", "Academia", "Pet Shop", "Padaria",
        "Ótica", "Lavanderia", "Estúdio"
    ];

    private static readonly string[] Sobrenomes =
    [
        "Boa Vista", "São Jorge", "do Vale", "Primavera", "Aliança", "Ipiranga", "Bela Vista"
    ];

    /// <summary>Falas curtas, do jeito que gente escreve no WhatsApp — sem pontuação perfeita,
    /// sem frase de catálogo. Texto de manual denuncia a demonstração em dois segundos.</summary>
    private static readonly string[] Entradas =
    [
        "oi, boa tarde! vcs atendem sábado?",
        "quanto fica o orçamento?",
        "consigo agendar pra quinta?",
        "ainda tem vaga essa semana?",
        "bom dia! vi o anúncio de vocês",
        "vcs parcelam no cartão?",
        "qual o endereço?",
        "obrigado! vou pensar e retorno",
        "pode ser de manhã?",
        "e se eu levar duas, tem desconto?",
        "fechado, pode marcar",
        "desculpa a demora pra responder"
    ];

    private static readonly string[] Saidas =
    [
        "Oi! Tudo bem? Atendemos sim, das 8h às 14h no sábado.",
        "Claro, consigo fazer o orçamento hoje ainda. Me manda o modelo?",
        "Quinta às 14h está livre. Reservo pra você?",
        "Temos vaga sim! Prefere manhã ou tarde?",
        "Bom dia! Que bom que chegou até a gente 🙂",
        "Parcelamos em até 6x sem juros.",
        "Ficamos na Av. das Palmeiras, 1200 — do lado do mercado.",
        "Sem problema! Qualquer dúvida é só chamar.",
        "De manhã fica ótimo. Marquei pra 9h.",
        "Levando duas eu consigo 10% de desconto."
    ];

    /// <summary>Quanto cada etapa aberta perde para a seguinte.
    ///
    /// ===================== O FUNIL PRECISA AFUNILAR =====================
    /// A primeira versão distribuía por `i % 4` e dava 10, 10, 10, 10 — o desenho saía um
    /// retângulo, e a etapa de Venda (com os ganhos acumulados) ficava MAIS LARGA que o topo.
    /// Um funil que alarga é o oposto do que a figura existe para mostrar.
    ///
    /// Era uma lista fixa `[18, 11, 7, 4]`, que só funcionava com exatamente 4 etapas abertas e
    /// 60 contatos. Desde o ARQ-1 o dono cria e apaga etapa, e o volume é parâmetro — então a
    /// forma virou uma RAZÃO: cada etapa fica com 62% da anterior. Vale para 3 etapas ou 11,
    /// para 60 contatos ou 20 mil.
    /// ====================================================================</summary>
    private const double RetencaoPorEtapa = 0.62;

    /// <summary>Sobrenomes para compor nome de pessoa em volume.
    ///
    /// Com 30 nomes fixos e 2000 contatos, cada nome apareceria 67 vezes e a lista de contatos
    /// viraria uma parede de repetição — o que denuncia dado falso mais rápido que qualquer
    /// número errado. 30 × 40 dá 1200 combinações, e o índice do contato completa o resto.</summary>
    private static readonly string[] SobrenomesPessoa =
    [
        "Albuquerque", "Bandeira", "Cavalcanti", "Domingues", "Esteves", "Ferraz", "Guimarães",
        "Hollanda", "Ibrahim", "Jucá", "Klein", "Lacerda", "Maranhão", "Novaes", "Oliveira",
        "Pontes", "Queiroz", "Ramalho", "Siqueira", "Tavares", "Uchôa", "Valadares", "Wanderley",
        "Xavier", "Yamada", "Zanetti", "Almeida", "Braga", "Coutinho", "Drummond", "Escobar",
        "Fagundes", "Galvão", "Henriques", "Iglesias", "Jordão", "Lombardi", "Medeiros",
        "Nascimento", "Portela"
    ];

    /// <summary>Origem dos leads, com PESO — não distribuídas em rodízio.
    ///
    /// O rodízio dava nove fatias de 10% cada, e uma rosca de fatias idênticas não informa nada:
    /// parece defeito, não dado. A proporção abaixo é a de uma PME brasileira típica — o social
    /// traz a maior parte, a indicação pesa, e o resto é cauda.</summary>
    private static readonly (OrigemLead Origem, int Quantos)[] OrigensComPeso =
    [
        (OrigemLead.Instagram, 15),
        (OrigemLead.Whatsapp,  13),
        (OrigemLead.Indicacao, 10),
        (OrigemLead.Google,     7),
        (OrigemLead.Site,       5),
        (OrigemLead.Facebook,   4),
        (OrigemLead.Qrcode,     3),
        (OrigemLead.Manual,     2),
        (OrigemLead.Outro,      1)
    ];

    private static readonly string[] MotivosPerda =
    [
        "Achou mais barato em outro lugar",
        "Desistiu do serviço",
        "Sumiu depois do orçamento",
        "Comprou com concorrente",
        "Fora da nossa região de atendimento"
    ];

    public async Task<ResumoSeedDemonstracao> SemearAsync(
        OpcoesSeedDemonstracao? opcoes, CancellationToken ct)
    {
        var op = (opcoes ?? new OpcoesSeedDemonstracao()).Saneada();

        // A transação é do escopo INTEIRO: um seed que falha no meio deixaria um tenant meio
        // montado — com contato sem conversa, ou conversa sem mensagem — e todo teste de
        // coerência acusaria um problema que é do seed, não do produto.
        //
        // Só abre se ainda NÃO houver uma em curso (mesmo padrão do `ServicoCadastroEmpresa`):
        // abrir aninhada lança, e o teste roda tudo dentro de uma transação revertida.
        var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var empresaId = await LimparOuCriarAsync(ct);
        var agora = relogio.GetUtcNow().UtcDateTime;

        var usuarios = await CriarEquipeAsync(empresaId, ct);
        var etapas = await db.EtapasFunil.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.EmpresaId == empresaId).OrderBy(e => e.Ordem).ToListAsync(ct);

        var rnd = new Random(Semente);

        var contatos = await CriarContatosAsync(empresaId, etapas, usuarios, agora, op, rnd, ct);
        var (conversas, mensagens) = await CriarConversasAsync(
            empresaId, contatos, usuarios, agora, op, rnd, ct);
        var lembretes = await CriarLembretesAsync(empresaId, contatos, usuarios, agora, op, rnd, ct);

        // Commita SÓ a transação que este método abriu. Se veio de fora, quem abriu decide.
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
            await tx.DisposeAsync();
        }

        var resumo = new ResumoSeedDemonstracao(
            empresaId, EmailDono, Senha, usuarios.Count, contatos.Count, conversas, mensagens,
            lembretes,
            contatos.Count(c => c.GanhoEm is not null),
            contatos.Count(c => c.PerdidoEm is not null));

        log.LogInformation(
            "Tenant de demonstração {Id} semeado em {Dias} dias: {Contatos} contatos, " +
            "{Conversas} conversas, {Mensagens} mensagens, {Lembretes} lembretes.",
            empresaId, op.Dias, resumo.Contatos, resumo.Conversas, resumo.Mensagens, resumo.Lembretes);

        return resumo;
    }

    /// <summary>=========== A IDEMPOTÊNCIA ===========
    /// Se o tenant já existe, APAGA o conteúdo dele e reaproveita a empresa, os usuários e as
    /// etapas. Não recria a empresa: o id apareceria mudado a cada execução, e qualquer link
    /// salvo (ou token em mãos) deixaria de funcionar.
    ///
    /// A ordem das exclusões segue as chaves estrangeiras, que são RESTRICT e não cascata:
    /// mensagens → lembretes → conversas → contatos. Inverter dá violação de FK.
    /// ======================================</summary>
    private async Task<long> LimparOuCriarAsync(CancellationToken ct)
    {
        var existente = await db.Usuarios.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Email == EmailDono)
            .Select(u => (long?)u.EmpresaId)
            .FirstOrDefaultAsync(ct);

        if (existente is { } empresaId)
        {
            await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == empresaId).ExecuteDeleteAsync(ct);
            await db.Lembretes.IgnoreQueryFilters().Where(l => l.EmpresaId == empresaId).ExecuteDeleteAsync(ct);
            await db.Conversas.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync(ct);
            await db.Contatos.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId).ExecuteDeleteAsync(ct);
            db.ChangeTracker.Clear();

            // Reafirma a marca: se alguém tirou a flag à mão, o seed a devolve antes de gravar o
            // primeiro contato com telefone.
            await MarcarComoDemonstracaoAsync(empresaId, ct);
            return empresaId;
        }

        var novo = await cadastro.CadastrarAsync(new NovaEmpresa(
            NomeEmpresa, "11222333000181", "Ana Demonstração", EmailDono, Senha,
            NomeConexao: "Atendimento",
            // Instância com nome próprio e reconhecível: se alguém apontá-la para uma Evolution
            // real por engano, o nome denuncia na lista de instâncias.
            InstanceName: "demo-nexora-nao-parear"), ct);

        await MarcarComoDemonstracaoAsync(novo, ct);
        return novo;
    }

    /// <summary>Marca o tenant e deixa a conexão como CONECTADA.
    ///
    /// O status conectado é o que faz o painel não abrir com a faixa vermelha de "WhatsApp
    /// desconectado" em cima de toda tela — que seria a primeira coisa que o cliente veria numa
    /// demonstração. Não há instância real por trás: o envio é barrado pelo `EnviadorMensagem`.</summary>
    private async Task MarcarComoDemonstracaoAsync(long empresaId, CancellationToken ct)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Demonstracao, true)
                .SetProperty(e => e.Uf, "RN"), ct);

        await db.Conexoes.IgnoreQueryFilters().Where(c => c.EmpresaId == empresaId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, StatusConexao.Conectado)
                .SetProperty(c => c.Numero, TelefoneDemonstracao.Numero(0)), ct);

        db.ChangeTracker.Clear();
    }

    /// <summary>O dono já veio do cadastro; faltam os dois vendedores. Sem eles, a coluna
    /// "responsável" fica vazia em toda tela e o filtro por vendedor não tem o que mostrar.</summary>
    private async Task<List<Usuario>> CriarEquipeAsync(long empresaId, CancellationToken ct)
    {
        var existentes = await db.Usuarios.IgnoreQueryFilters()
            .Where(u => u.EmpresaId == empresaId).ToListAsync(ct);

        foreach (var (nome, email) in new[]
        {
            ("Rafael Nunes", "rafael.demo@nexora.exemplo"),
            ("Camila Duarte", "camila.demo@nexora.exemplo")
        })
        {
            if (existentes.Any(u => u.Email == email)) continue;

            var novo = new Usuario
            {
                EmpresaId = empresaId,
                Nome = nome,
                Email = email,
                SenhaHash = HashSenha.Gerar(Senha),
                Papel = PapelUsuario.Vendedor,
                Status = StatusUsuario.Ativo
            };
            db.Usuarios.Add(novo);
            existentes.Add(novo);
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return existentes;
    }

    // ==================================================================== contatos
    private async Task<List<Contato>> CriarContatosAsync(
        long empresaId, List<EtapaFunil> etapas, List<Usuario> usuarios,
        DateTime agora, OpcoesSeedDemonstracao op, Random rnd, CancellationToken ct)
    {
        var vendedores = usuarios.Where(u => u.Papel == PapelUsuario.Vendedor).Select(u => u.Id).ToList();
        var etapaGanho = etapas.Single(e => e.EGanho);
        var abertas = etapas.Where(e => !e.EGanho).ToList();

        // A lista é expandida uma vez: `origens[i]` vira consulta direta, e a proporção fica
        // declarada lá em cima em vez de escondida numa conta de módulo no meio do laço.
        var origens = OrigensComPeso
            .SelectMany(o => Enumerable.Repeat(o.Origem, o.Quantos)).ToList();

        // ===== AS PROPORÇÕES, QUE VALEM EM QUALQUER ESCALA =====
        // Eram contagens fixas (12 ganhos, 8 perdidos) escritas contra 60 contatos. Com o volume
        // virando parâmetro, 12 ganhos em 2000 contatos daria conversão de 0,6% — número que
        // nenhuma demonstração pode mostrar. Proporção resolve nas duas pontas.
        var ganhos = Math.Max(2, op.Contatos * 20 / 100);
        var perdidos = Math.Max(1, op.Contatos * 13 / 100);

        // A contagem por etapa vira uma lista EXPANDIDA — `etapaAberta[n]` é a etapa do n-ésimo
        // contato aberto. Consulta direta no laço, sem conta de módulo escondida.
        var etapaAberta = DistribuirAbertos(op.Contatos - ganhos - perdidos, abertas.Count)
            .SelectMany((quantos, etapa) => Enumerable.Repeat(etapa, quantos))
            .ToList();

        var contatos = new List<Contato>(op.Contatos);
        var abertosAtribuidos = 0;

        for (var i = 0; i < op.Contatos; i++)
        {
            // ===== A DISTRIBUIÇÃO QUE FAZ AS TELAS TEREM FORMA =====
            //   os primeiros  ganhos    — dão faturamento, taxa de conversão e forma ao gráfico
            //   os seguintes  perdidos  — sem eles a conversão fica em 100%, que ninguém acredita
            //   o resto       abertos   — enchem o funil
            var ganho = i < ganhos;
            var perdido = i >= ganhos && i < ganhos + perdidos;

            var nome = NomeDoContato(i);

            var contato = new Contato
            {
                EmpresaId = empresaId,
                Nome = nome,
                // Pelo canonicalizador, como qualquer cadastro — o seed não abre exceção para si.
                Telefone = CanonicalizadorTelefone.Canonicalizar(TelefoneDemonstracao.Numero(i + 1)),
                Email = i % 4 == 0 ? $"contato{i + 1}@exemplo.com.br" : null,
                Origem = origens[i % origens.Count],
                // Ganho → etapa de venda. PERDIDO → qualquer etapa aberta, porque ele some do
                // quadro pelo `perdido_em` e não pode consumir a forma do funil. ABERTO → segue
                // `AbertosPorEtapa`, que é o que dá o afunilamento.
                // O arredondamento da distribuição pode deixar a lista expandida um ou dois
                // curta. `Min` com o último índice evita que isso vire uma exceção no meio do
                // seed — a diferença de um contato não muda a forma do funil.
                EtapaId = ganho ? etapaGanho.Id
                        : perdido ? abertas[i % abertas.Count].Id
                        : abertas[etapaAberta[Math.Min(abertosAtribuidos++, etapaAberta.Count - 1)]].Id,
                // Ordem com passo de 1000 e sem repetição dentro da etapa: o kanban insere no
                // ponto médio entre vizinhos, e valores colados forçariam renormalização já na
                // primeira arrastada da demonstração.
                OrdemKanban = (i + 1) * 1000m,
                ResponsavelId = i % 5 == 0 ? null : vendedores[i % vendedores.Count],
                Observacoes = i % 7 == 0 ? "Cliente antigo, prefere ser chamado no WhatsApp." : null
            };

            // A janela agora é parâmetro. `criadoEm` real é carimbado depois, num UPDATE só —
            // ver `CarimbarCriadoEmAsync`, porque o interceptor de auditoria sobrescreve a coluna
            // em todo INSERT.

            // ===== ck_contatos_terminal: ganho E perdido é estado proibido =====
            // Os dois ramos são exclusivos por construção, não por sorte.
            if (ganho)
            {
                // ===== METADE NO MÊS CORRENTE, METADE NOS 6 MESES =====
                // Os 6 meses dão FORMA ao gráfico de faturamento — sem eles a linha é um pico só.
                // Mas o dashboard conta "vendas do mês" e "taxa de conversão" contra o MÊS
                // CORRENTE: espalhar tudo em 180 dias deixava os números do topo da tela perto de
                // zero, que é justamente o que uma demonstração não pode mostrar. Pior, o
                // resultado dependia do dia do mês em que alguém rodasse o seed.
                contato.GanhoEm = i < ganhos / 2
                    ? NoMesCorrente(agora, i, rnd)
                    : agora.AddDays(-rnd.Next(20, op.Dias));
                contato.Valor = Math.Round((decimal)(rnd.Next(45, 900) * 10), 2);
            }
            else if (perdido)
            {
                // Mesma razão: sem perdido NO MÊS, a conversão do mês fica em 100%.
                contato.PerdidoEm = i < ganhos + perdidos / 2
                    ? NoMesCorrente(agora, i, rnd)
                    : agora.AddDays(-rnd.Next(20, op.Dias));
                contato.MotivoPerda = MotivosPerda[i % MotivosPerda.Length];
            }
            else if (i % 3 == 0)
            {
                // Valor sem ganho = negócio em negociação com proposta na mesa. O cabeçalho da
                // coluna do funil soma isso.
                contato.Valor = Math.Round((decimal)(rnd.Next(30, 600) * 10), 2);
            }

            db.Contatos.Add(contato);
            contatos.Add(contato);
        }

        await db.SaveChangesAsync(ct);

        // `criado_em` é carimbado pelo InterceptorAuditoria em todo INSERT — é o que impede um
        // caminho de escrita de esquecer a coluna. Aqui trabalha contra: a série temporal e o
        // feed usam `criado_em` como instante do evento, e todos os contatos cairiam em hoje.
        var quandos = new DateTime[contatos.Count];
        for (var i = 0; i < contatos.Count; i++)
        {
            // Os quatro primeiros nasceram HOJE. O cartão "Leads hoje" é o primeiro número do
            // dashboard, e abrir a demonstração com ele zerado é a pior primeira impressão
            // possível — o resto da tela cheia e o topo em branco.
            //
            // Com janela curta e volume alto o espalhamento já cobriria hoje sozinho; com janela
            // longa e volume baixo, não. O piso de 4 garante os dois casos.
            quandos[i] = i < 4
                ? agora.AddHours(-(i + 1) * 2)
                : agora.AddDays(-rnd.Next(1, op.Dias)).AddHours(-rnd.Next(0, 10));

            contatos[i].CriadoEm = quandos[i];
        }

        await CarimbarCriadoEmAsync(
            SqlCarimboContatos, contatos.Select(c => c.Id).ToArray(), quandos, ct);

        db.ChangeTracker.Clear();
        return contatos;
    }

    /// <summary>Nome de pessoa que não se repete em volume.
    ///
    /// Com os 30 nomes fixos e 2000 contatos, cada um apareceria 67 vezes — e uma lista de
    /// contatos cheia de repetição denuncia dado falso mais rápido que qualquer número errado.
    /// Combinando com 40 sobrenomes dá 1200 pares; acima disso entra o nome do negócio, que é
    /// como uma base real se parece mesmo (metade dos contatos de PME é "Fulano da Oficina X").</summary>
    private static string NomeDoContato(int i)
    {
        var primeiro = Nomes[i % Nomes.Length].Split(' ')[0];
        var sobrenome = SobrenomesPessoa[(i / Nomes.Length) % SobrenomesPessoa.Length];

        var pares = Nomes.Length * SobrenomesPessoa.Length;
        if (i < pares) return $"{primeiro} {sobrenome}";

        // Passou das combinações limpas: vira "Fulano Sobrenome (Negócio Bairro)".
        var ciclo = i / pares;
        return $"{primeiro} {sobrenome} "
             + $"({Negocios[(i + ciclo) % Negocios.Length]} {Sobrenomes[(i + ciclo) % Sobrenomes.Length]})";
    }

    /// <summary>Quantos contatos abertos em cada etapa, para QUALQUER número de etapas.
    ///
    /// Cada etapa fica com 62% da anterior — a forma de um funil que perde gente pelo caminho.
    /// A sobra do arredondamento vai para a PRIMEIRA etapa, nunca para a última: engordar o topo
    /// mantém o afunilamento; engordar o fim poderia inverter a última comparação e desenhar um
    /// funil que alarga no final.</summary>
    private static int[] DistribuirAbertos(int total, int etapas)
    {
        if (etapas <= 0) return [];
        if (etapas == 1) return [total];

        var pesos = new double[etapas];
        var soma = 0.0;
        for (var e = 0; e < etapas; e++)
        {
            pesos[e] = Math.Pow(RetencaoPorEtapa, e);
            soma += pesos[e];
        }

        var porEtapa = new int[etapas];
        var distribuidos = 0;

        // Da ÚLTIMA para a primeira, com piso 1: etapa vazia no meio do funil parece coluna
        // quebrada, e a diferença de uma unidade não muda a forma do desenho.
        for (var e = etapas - 1; e >= 1; e--)
        {
            porEtapa[e] = Math.Max(1, (int)Math.Round(total * pesos[e] / soma));
            distribuidos += porEtapa[e];
        }

        porEtapa[0] = Math.Max(1, total - distribuidos);
        return porEtapa;
    }

    /// <summary>Carimba `criado_em` de muitas linhas num ÚNICO comando.
    ///
    /// ===================== POR QUE NÃO UM UPDATE POR LINHA =====================
    /// Era assim: `ExecuteUpdateAsync` dentro do laço, uma ida ao banco por contato e outra por
    /// mensagem. Com 60 contatos e 400 mensagens ninguém percebia. Com 2000 contatos e 13 mil
    /// mensagens seriam ~15 mil viagens de rede — o seed passaria de segundos a muitos minutos,
    /// segurando uma transação aberta o tempo todo.
    ///
    /// `unnest` de dois arrays resolve em um comando e dois parâmetros, para qualquer tamanho —
    /// diferente de montar um `VALUES` gigante, que cresce o texto do comando e esbarra no teto
    /// de parâmetros do protocolo.
    /// ==========================================================================
    ///
    /// O nome da tabela NÃO é interpolado: são duas constantes com o SQL inteiro escrito à mão.
    /// Montar o comando com interpolação funcionaria — o argumento nunca vem de fora —, mas
    /// deixaria uma concatenação de SQL no código para alguém copiar num lugar onde a entrada é
    /// externa. Duas constantes custam três linhas e não ensinam nada errado.</summary>
    private const string SqlCarimboContatos =
        """
        UPDATE contatos AS alvo
           SET criado_em = fonte.quando
          FROM (SELECT unnest({0}::bigint[]) AS id,
                       unnest({1}::timestamptz[]) AS quando) AS fonte
         WHERE alvo.id = fonte.id
        """;

    private const string SqlCarimboMensagens =
        """
        UPDATE mensagens AS alvo
           SET criado_em = fonte.quando
          FROM (SELECT unnest({0}::bigint[]) AS id,
                       unnest({1}::timestamptz[]) AS quando) AS fonte
         WHERE alvo.id = fonte.id
        """;

    private async Task CarimbarCriadoEmAsync(
        string sql, long[] ids, DateTime[] quandos, CancellationToken ct)
    {
        if (ids.Length == 0) return;
        await db.Database.ExecuteSqlRawAsync(sql, [ids, quandos], ct);
    }

    /// <summary>Tamanho do bloco de INSERT.
    ///
    /// O EF já agrupa INSERTs, mas o ChangeTracker com dezenas de milhares de entidades vivas
    /// torna cada `DetectChanges` caro — o custo cresce mais que linearmente. Salvar e limpar em
    /// blocos mantém o tempo estável. 1000 é grande o bastante para o ganho e pequeno o bastante
    /// para a memória não importar.</summary>
    private const int TamanhoDoLote = 1000;

    /// <summary>Um instante DENTRO do mês corrente, sempre no passado.
    ///
    /// Rodar o seed no dia 1º deixaria a janela com poucas horas; por isso o piso de 2h e o teto
    /// pelo próprio `agora` — nunca uma data futura, que apareceria como venda que ainda não
    /// aconteceu.</summary>
    private static DateTime NoMesCorrente(DateTime agora, int i, Random rnd)
    {
        var inicioDoMes = new DateTime(agora.Year, agora.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var horasNoMes = Math.Max(2, (int)(agora - inicioDoMes).TotalHours);

        // `i` entra na conta para os pontos não se amontoarem todos na mesma hora.
        return agora.AddHours(-((rnd.Next(1, horasNoMes) + i) % horasNoMes) - 1);
    }

    // ==================================================================== conversas e mensagens
    private async Task<(int Conversas, int Mensagens)> CriarConversasAsync(
        long empresaId, List<Contato> contatos, List<Usuario> usuarios,
        DateTime agora, OpcoesSeedDemonstracao op, Random rnd, CancellationToken ct)
    {
        var conexao = await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.EmpresaId == empresaId, ct);

        var dono = usuarios.Single(u => u.Papel == PapelUsuario.Dono);

        // Dois terços dos contatos têm conversa — a proporção que a versão de 60 contatos usava
        // (40 de 60), agora como razão. O terço restante são leads que ainda não escreveram, e
        // eles importam: sem contato sem conversa, a caixa e o funil teriam sempre o mesmo total
        // e um bug de junção passaria despercebido.
        var comConversa = contatos.Take(Math.Max(1, op.Contatos * 2 / 3)).ToList();

        // ===== FASE 1: todas as conversas de uma vez =====
        // Era um `SaveChanges` por conversa, para obter o id antes de montar a thread. Com 1300
        // conversas isso é 1300 viagens; um AddRange só resolve, e o EF devolve todos os ids.
        var conversas = new List<Conversa>(comConversa.Count);
        var ultimas = new DateTime[comConversa.Count];
        var terminaEmEntradaDe = new bool[comConversa.Count];

        for (var i = 0; i < comConversa.Count; i++)
        {
            var contato = comConversa[i];

            // ===== O QUE FAZ O SEMÁFORO MOSTRAR AS TRÊS CORES =====
            // A cor sai dos MINUTOS ÚTEIS desde `aguardando_desde`, contra os limites da empresa
            // (60 min = amarelo, 240 min = vermelho). Então a última mensagem precisa cair em
            // faixas diferentes, e ela tem que ser de ENTRADA — conversa cuja última mensagem é
            // de saída não está esperando ninguém.
            //
            //  i < 3   → minutos: verde
            //  i < 6   → ~2h: amarelo
            //  i < 10  → ~8h e ontem: vermelho
            //  resto   → a maioria terminando em SAÍDA, ou seja, já respondida
            //
            // A proporção importa: a primeira versão deixava metade do resto esperando, e a caixa
            // abria com 25 de 40 conversas vermelhas. Tecnicamente correto e péssimo como
            // demonstração — parece operação em crise, não ferramenta que funciona. Uma operação
            // saudável tem a maioria respondida e um punhado exigindo atenção agora.
            // Os índices absolutos das faixas de cor continuam absolutos, e não proporcionais: o
            // que a caixa precisa é de UM PUNHADO exigindo atenção agora, não de 10% de 1300
            // conversas em vermelho. Operação saudável tem a maioria respondida.
            var terminaEmEntrada = i < 10 || i % 6 == 0;

            var ultima = i switch
            {
                < 3 => agora.AddMinutes(-rnd.Next(3, 40)),
                < 6 => agora.AddMinutes(-rnd.Next(75, 200)),
                < 10 => agora.AddHours(-rnd.Next(5, 30)),
                _ => agora.AddDays(-rnd.Next(1, op.Dias)).AddHours(-rnd.Next(0, 12))
            };

            ultimas[i] = ultima;
            terminaEmEntradaDe[i] = terminaEmEntrada;

            var conversa = new Conversa
            {
                EmpresaId = empresaId,
                ContatoId = contato.Id,
                ConexaoId = conexao.Id,
                Status = StatusConversa.Aberta,
                ResponsavelId = contato.ResponsavelId,
                AtribuidoEm = contato.ResponsavelId is null ? null : ultima.AddDays(-1),
                UltimaMensagemEm = ultima
            };
            db.Conversas.Add(conversa);
            conversas.Add(conversa);
        }

        await db.SaveChangesAsync(ct);

        // ===== FASE 2: as threads, em lotes =====
        var todasMensagens = new List<Mensagem>(comConversa.Count * 10);
        var alinhamentos = new List<AlinhamentoConversa>(conversas.Count);

        for (var i = 0; i < conversas.Count; i++)
        {
            var mensagens = MontarThread(
                empresaId, conexao, conversas[i], comConversa[i], dono.Id,
                ultimas[i], terminaEmEntradaDe[i], rnd);

            todasMensagens.AddRange(mensagens);
            alinhamentos.Add(CalcularAlinhamento(conversas[i].Id, mensagens));
        }

        for (var inicio = 0; inicio < todasMensagens.Count; inicio += TamanhoDoLote)
        {
            db.Mensagens.AddRange(todasMensagens.Skip(inicio).Take(TamanhoDoLote));
            await db.SaveChangesAsync(ct);
        }

        // `criado_em` explícito, pelo mesmo motivo dos contatos: a série temporal e o feed de
        // atividades leem esta coluna como o instante do evento. Um comando só, não um por linha.
        await CarimbarCriadoEmAsync(
            SqlCarimboMensagens,
            todasMensagens.Select(m => m.Id).ToArray(),
            todasMensagens.Select(m => m.RecebidaEm ?? m.EnviadaEm ?? m.ReservadoEm).ToArray(),
            ct);

        await AlinharConversasAsync(alinhamentos, ct);

        db.ChangeTracker.Clear();
        return (conversas.Count, todasMensagens.Count);
    }

    /// <summary>A thread de uma conversa: 6 a 14 mensagens alternando, terminando na direção
    /// pedida.</summary>
    private static List<Mensagem> MontarThread(
        long empresaId, Conexao conexao, Conversa conversa, Contato contato, long donoId,
        DateTime ultima, bool terminaEmEntrada, Random rnd)
    {
        var quantas = rnd.Next(6, 15);
        var mensagens = new List<Mensagem>();

        for (var i = 0; i < quantas; i++)
        {
            var ehUltima = i == quantas - 1;
            var entrada = ehUltima
                ? terminaEmEntrada
                : (quantas - 1 - i) % 2 == (terminaEmEntrada ? 1 : 0);

            // Espaçamento decrescente até a última: a conversa "acontece" ao longo de horas.
            var quando = ultima.AddMinutes(-(quantas - 1 - i) * rnd.Next(6, 50));

            mensagens.Add(new Mensagem
            {
                EmpresaId = empresaId,
                ConversaId = conversa.Id,
                ContatoId = contato.Id,
                ConexaoId = conexao.Id,
                InstanceName = conexao.InstanceName,
                Direcao = entrada ? DirecaoMensagem.Entrada : DirecaoMensagem.Saida,
                // O índice varia por conversa E por posição, para duas threads não saírem
                // idênticas. `% n` sobre valores não negativos — id e i são ambos positivos —,
                // então não há o caso de índice negativo.
                Texto = entrada
                    ? Entradas[(int)((conversa.Id + i) % Entradas.Length)]
                    : Saidas[(int)((conversa.Id + i) % Saidas.Length)],
                // uq_msg_wa_id é (instance_name, wa_message_id): o par conversa+índice é único
                // dentro da instância, então não há como o seed colidir consigo mesmo.
                WaMessageId = $"DEMO-{conversa.Id}-{i}",
                RecebidaEm = entrada ? quando : null,
                EnviadaEm = entrada ? null : quando,
                // ck_msg_data_disparo: saída EXIGE data_disparo.
                DataDisparo = entrada ? null : DateOnly.FromDateTime(quando),
                ReservadoEm = quando,
                EnviadoPor = entrada ? null : donoId,
                // ACK só na saída. 3 = entregue, 4 = lido — dá conteúdo aos dois ticks da thread.
                Ack = entrada ? null : (short)(i % 3 == 0 ? 3 : 4),
                AckEm = entrada ? null : quando.AddMinutes(1),
                Tentativas = entrada ? (short)0 : (short)1
            });
        }

        return mensagens;
    }

    /// <summary>=========== A COERÊNCIA QUE O SEED PRECISA MANTER À MÃO ===========
    /// O seed escreve direto no banco, então as invariantes que os serviços respeitam não vêm de
    /// graça. Se qualquer uma destas ficar torta, a tela mostra estado que o produto real nunca
    /// produziria — e alguém vai depurar um bug que não existe.
    ///
    ///   • `aguardando_desde` = instante da PRIMEIRA entrada da rajada final, e NULL se a última
    ///     mensagem foi de saída (ninguém está esperando resposta);
    ///   • `ultima_mensagem_em/direcao/previa` batendo com a última mensagem de verdade;
    ///   • `nao_lidas` = quantas entradas vieram DEPOIS da última saída.
    /// ================================================================</summary>
    private sealed record AlinhamentoConversa(
        long ConversaId, DateTime UltimaEm, string Direcao, string? Previa,
        DateTime? AguardandoDesde, int NaoLidas);

    private static AlinhamentoConversa CalcularAlinhamento(long conversaId, List<Mensagem> mensagens)
    {
        var ordenadas = mensagens
            .OrderBy(m => m.RecebidaEm ?? m.EnviadaEm ?? m.ReservadoEm)
            .ToList();

        var ultima = ordenadas[^1];
        var ultimaEm = ultima.RecebidaEm ?? ultima.EnviadaEm ?? ultima.ReservadoEm;

        // As entradas depois da última saída: é a rajada que está esperando resposta.
        var indiceUltimaSaida = ordenadas.FindLastIndex(m => m.Direcao == DirecaoMensagem.Saida);
        var entradasPendentes = ordenadas
            .Skip(indiceUltimaSaida + 1)
            .Where(m => m.Direcao == DirecaoMensagem.Entrada)
            .ToList();

        var previa = (ultima.Texto ?? "").Length > 120
            ? ultima.Texto![..120]
            : ultima.Texto;

        return new AlinhamentoConversa(
            conversaId, ultimaEm,
            // O enum vai como TEXTO e volta com cast no SQL. Depender do mapeamento de ARRAY de
            // enum nativo do Npgsql funcionaria, mas é a peça mais frágil deste comando — e o
            // valor em snake_case é exatamente o que o tipo do Postgres espera.
            ultima.Direcao == DirecaoMensagem.Entrada ? "entrada" : "saida",
            previa,
            entradasPendentes.Count > 0 ? entradasPendentes[0].RecebidaEm : null,
            entradasPendentes.Count);
    }

    /// <summary>Aplica o alinhamento de TODAS as conversas num comando só.
    ///
    /// Era um `ExecuteUpdateAsync` por conversa — 1300 viagens de rede na base grande. As cinco
    /// colunas viram cinco arrays paralelos, e o `unnest` os recompõe em linhas do lado do banco.</summary>
    private async Task AlinharConversasAsync(
        List<AlinhamentoConversa> alinhamentos, CancellationToken ct)
    {
        if (alinhamentos.Count == 0) return;

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE conversas AS alvo
               SET ultima_mensagem_em      = fonte.ultima_em,
                   ultima_mensagem_direcao = fonte.direcao::direcao_mensagem_enum,
                   ultima_mensagem_previa  = fonte.previa,
                   aguardando_desde        = fonte.aguardando,
                   nao_lidas               = fonte.nao_lidas
              FROM (SELECT unnest({0}::bigint[])      AS id,
                           unnest({1}::timestamptz[]) AS ultima_em,
                           unnest({2}::text[])        AS direcao,
                           unnest({3}::text[])        AS previa,
                           unnest({4}::timestamptz[]) AS aguardando,
                           unnest({5}::int[])         AS nao_lidas) AS fonte
             WHERE alvo.id = fonte.id
            """,
            [
                alinhamentos.Select(a => a.ConversaId).ToArray(),
                alinhamentos.Select(a => a.UltimaEm).ToArray(),
                alinhamentos.Select(a => a.Direcao).ToArray(),
                alinhamentos.Select(a => a.Previa).ToArray(),
                alinhamentos.Select(a => a.AguardandoDesde).ToArray(),
                alinhamentos.Select(a => a.NaoLidas).ToArray()
            ], ct);
    }

    // ==================================================================== lembretes
    private async Task<int> CriarLembretesAsync(
        long empresaId, List<Contato> contatos, List<Usuario> usuarios,
        DateTime agora, OpcoesSeedDemonstracao op, Random rnd, CancellationToken ct)
    {
        var dono = usuarios.Single(u => u.Papel == PapelUsuario.Dono);
        var hoje = DateOnly.FromDateTime(agora);

        // ===== POR QUE O MEU DIA NÃO CRESCE COM A BASE =====
        // Lembrete vira linha na tela do Meu Dia, e uma tela com 100 pendências não é uma agenda
        // — é uma lista que ninguém abre. O que a demonstração precisa mostrar é um dia de
        // trabalho plausível: um punhado de vencidos, um punhado de hoje.
        //
        // Por isso a fração é pequena e TEM TETO, ao contrário de ganhos e perdidos, que precisam
        // acompanhar o volume para a conversão fazer sentido.
        var quantos = Math.Clamp(op.Contatos / 20, 10, 40);
        var alvos = contatos.Where(c => c.PerdidoEm is null).Take(quantos).ToList();

        var titulos = new[]
        {
            "Ligar para confirmar o horário", "Mandar o orçamento revisado",
            "Retomar contato", "Confirmar a entrega", "Perguntar se ficou tudo certo"
        };

        for (var i = 0; i < alvos.Count; i++)
        {
            // 5 vencidos + 4 de hoje = 9 no Meu Dia; o resto concluído, que é o que dá conteúdo
            // ao feed de atividades sem entupir a lista de pendências. Os índices são absolutos
            // pela mesma razão do semáforo: a agenda do dia não escala com o tamanho da base.
            var concluido = i >= 9;
            var dataAlvo = i < 5 ? hoje.AddDays(-rnd.Next(1, 6))
                         : i < 9 ? hoje
                         : hoje.AddDays(-rnd.Next(1, Math.Min(20, Math.Max(2, op.Dias))));

            db.Lembretes.Add(new Lembrete
            {
                EmpresaId = empresaId,
                ContatoId = alvos[i].Id,
                Titulo = titulos[i % titulos.Length],
                Origem = OrigemLembrete.Manual,
                Status = concluido ? StatusLembrete.Concluido : StatusLembrete.Pendente,
                DataAlvo = dataAlvo,
                HoraAlvo = i % 3 == 0 ? new TimeOnly(9 + (i % 8), 0) : null,
                ResponsavelId = alvos[i].ResponsavelId ?? dono.Id,
                CriadoPor = dono.Id,
                // `DateOnly.ToDateTime` devolve Kind=Unspecified, e o Npgsql recusa gravar isso
                // numa coluna `timestamptz` — só aceita UTC. Marcar explicitamente é o que
                // mantém a coluna com o mesmo significado do resto do sistema.
                ConcluidoEm = concluido
                    ? DateTime.SpecifyKind(dataAlvo.ToDateTime(new TimeOnly(11, 30)), DateTimeKind.Utc)
                    : null,
                ConcluidoPor = concluido ? dono.Id : null
            });
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return alvos.Count;
    }
}
