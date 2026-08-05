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

    /// <summary>Quantos contatos ABERTOS em cada etapa, do topo para o fim.
    ///
    /// ===================== O FUNIL PRECISA AFUNILAR =====================
    /// A primeira versão distribuía por `i % 4` e dava 10, 10, 10, 10 — o desenho saía um
    /// retângulo, e a etapa de Venda (com os 12 ganhos acumulados) ficava MAIS LARGA que o topo.
    /// Um funil que alarga é o oposto do que a figura existe para mostrar.
    ///
    /// 18 → 11 → 7 → 4 é a forma de um funil que perde gente pelo caminho, que é o que qualquer
    /// dono reconhece na hora.
    /// ====================================================================</summary>
    private static readonly int[] AbertosPorEtapa = [18, 11, 7, 4];

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

    public async Task<ResumoSeedDemonstracao> SemearAsync(CancellationToken ct)
    {
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

        var contatos = await CriarContatosAsync(empresaId, etapas, usuarios, agora, rnd, ct);
        var (conversas, mensagens) = await CriarConversasAsync(
            empresaId, contatos, usuarios, agora, rnd, ct);
        var lembretes = await CriarLembretesAsync(empresaId, contatos, usuarios, agora, rnd, ct);

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
            "Tenant de demonstração {Id} semeado: {Contatos} contatos, {Conversas} conversas, " +
            "{Mensagens} mensagens, {Lembretes} lembretes.",
            empresaId, resumo.Contatos, resumo.Conversas, resumo.Mensagens, resumo.Lembretes);

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
        DateTime agora, Random rnd, CancellationToken ct)
    {
        var vendedores = usuarios.Where(u => u.Papel == PapelUsuario.Vendedor).Select(u => u.Id).ToList();
        var etapaGanho = etapas.Single(e => e.EGanho);
        var abertas = etapas.Where(e => !e.EGanho).ToList();

        // As duas listas são expandidas uma vez: `origens[i]` e `etapaAberta[n]` viram consulta
        // direta, e a proporção fica declarada lá em cima em vez de escondida numa conta de
        // módulo no meio do laço.
        var origens = OrigensComPeso
            .SelectMany(o => Enumerable.Repeat(o.Origem, o.Quantos)).ToList();

        var etapaAberta = AbertosPorEtapa
            .SelectMany((quantos, etapa) => Enumerable.Repeat(etapa, quantos)).ToList();

        var contatos = new List<Contato>();
        var abertosAtribuidos = 0;

        for (var i = 0; i < 60; i++)
        {
            // ===== A DISTRIBUIÇÃO QUE FAZ AS TELAS TEREM FORMA =====
            //   0..11  ganhos    — dão faturamento, taxa de conversão e forma ao gráfico
            //  12..19  perdidos  — sem eles a conversão fica em 100%, que ninguém acredita
            //  20..59  abertos   — enchem o funil
            var ganho = i < 12;
            var perdido = i is >= 12 and < 20;

            var nome = $"{Nomes[i % Nomes.Length]}"
                + (i >= Nomes.Length ? $" ({Negocios[i % Negocios.Length]} {Sobrenomes[i % Sobrenomes.Length]})" : "");

            // Criado entre 5 e 180 dias atrás. RELATIVO a hoje, sempre: data absoluta faria a
            // demonstração envelhecer e mostrar "última mensagem há 5 meses".
            var criadoEm = agora.AddDays(-rnd.Next(5, 180)).AddHours(-rnd.Next(0, 10));

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
                EtapaId = ganho ? etapaGanho.Id
                        : perdido ? abertas[i % abertas.Count].Id
                        : abertas[etapaAberta[abertosAtribuidos++]].Id,
                // Ordem com passo de 1000 e sem repetição dentro da etapa: o kanban insere no
                // ponto médio entre vizinhos, e valores colados forçariam renormalização já na
                // primeira arrastada da demonstração.
                OrdemKanban = (i + 1) * 1000m,
                ResponsavelId = i % 5 == 0 ? null : vendedores[i % vendedores.Count],
                Observacoes = i % 7 == 0 ? "Cliente antigo, prefere ser chamado no WhatsApp." : null
            };

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
                contato.GanhoEm = i < 6 ? NoMesCorrente(agora, i, rnd) : agora.AddDays(-rnd.Next(20, 180));
                contato.Valor = Math.Round((decimal)(rnd.Next(45, 900) * 10), 2);
            }
            else if (perdido)
            {
                // Mesma razão: sem perdido NO MÊS, a conversão do mês fica em 100%.
                contato.PerdidoEm = i < 16 ? NoMesCorrente(agora, i, rnd) : agora.AddDays(-rnd.Next(20, 120));
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
        for (var i = 0; i < contatos.Count; i++)
        {
            var id = contatos[i].Id;

            // Os quatro primeiros nasceram HOJE. O cartão "Leads hoje" é o primeiro número do
            // dashboard, e abrir a demonstração com ele zerado é a pior primeira impressão
            // possível — o resto da tela cheia e o topo em branco.
            var quando = i < 4
                ? agora.AddHours(-(i + 1) * 2)
                : agora.AddDays(-rnd.Next(5, 180)).AddHours(-rnd.Next(0, 10));

            contatos[i].CriadoEm = quando;
            await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CriadoEm, quando), ct);
        }

        db.ChangeTracker.Clear();
        return contatos;
    }

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
        DateTime agora, Random rnd, CancellationToken ct)
    {
        var conexao = await db.Conexoes.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(c => c.EmpresaId == empresaId, ct);

        var dono = usuarios.Single(u => u.Papel == PapelUsuario.Dono);
        var totalMensagens = 0;
        var comConversa = contatos.Take(40).ToList();

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
            var terminaEmEntrada = i < 10 || i % 6 == 0;

            var ultima = i switch
            {
                < 3 => agora.AddMinutes(-rnd.Next(3, 40)),
                < 6 => agora.AddMinutes(-rnd.Next(75, 200)),
                < 10 => agora.AddHours(-rnd.Next(5, 30)),
                _ => agora.AddDays(-rnd.Next(1, 60)).AddHours(-rnd.Next(0, 12))
            };

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
            await db.SaveChangesAsync(ct);

            var mensagens = MontarThread(
                empresaId, conexao, conversa, contato, dono.Id, ultima, terminaEmEntrada, rnd);

            db.Mensagens.AddRange(mensagens);
            await db.SaveChangesAsync(ct);

            // `criado_em` explícito, pelo mesmo motivo dos contatos: a série temporal e o feed
            // de atividades leem esta coluna como o instante do evento.
            foreach (var m in mensagens)
            {
                var id = m.Id;
                var quando = m.RecebidaEm ?? m.EnviadaEm ?? m.ReservadoEm;
                await db.Mensagens.IgnoreQueryFilters().Where(x => x.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.CriadoEm, quando), ct);
            }

            await AlinharConversaAsync(conversa.Id, mensagens, ct);
            totalMensagens += mensagens.Count;
        }

        db.ChangeTracker.Clear();
        return (comConversa.Count, totalMensagens);
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
    private async Task AlinharConversaAsync(
        long conversaId, List<Mensagem> mensagens, CancellationToken ct)
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

        DateTime? aguardandoDesde = entradasPendentes.Count > 0
            ? entradasPendentes[0].RecebidaEm
            : null;

        var previa = (ultima.Texto ?? "").Length > 120
            ? ultima.Texto![..120]
            : ultima.Texto;

        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == conversaId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UltimaMensagemEm, ultimaEm)
                .SetProperty(c => c.UltimaMensagemDirecao, (DirecaoMensagem?)ultima.Direcao)
                .SetProperty(c => c.UltimaMensagemPrevia, previa)
                .SetProperty(c => c.AguardandoDesde, aguardandoDesde)
                .SetProperty(c => c.NaoLidas, entradasPendentes.Count), ct);
    }

    // ==================================================================== lembretes
    private async Task<int> CriarLembretesAsync(
        long empresaId, List<Contato> contatos, List<Usuario> usuarios,
        DateTime agora, Random rnd, CancellationToken ct)
    {
        var dono = usuarios.Single(u => u.Papel == PapelUsuario.Dono);
        var hoje = DateOnly.FromDateTime(agora);
        var alvos = contatos.Where(c => c.PerdidoEm is null).Take(15).ToList();

        var titulos = new[]
        {
            "Ligar para confirmar o horário", "Mandar o orçamento revisado",
            "Retomar contato", "Confirmar a entrega", "Perguntar se ficou tudo certo"
        };

        for (var i = 0; i < alvos.Count; i++)
        {
            // 5 vencidos + 4 de hoje = 9 no Meu Dia; os 6 restantes concluídos, que é o que dá
            // conteúdo ao feed de atividades sem entupir a lista de pendências.
            var concluido = i >= 9;
            var dataAlvo = i < 5 ? hoje.AddDays(-rnd.Next(1, 6))
                         : i < 9 ? hoje
                         : hoje.AddDays(-rnd.Next(1, 20));

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
