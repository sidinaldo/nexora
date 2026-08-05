using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Seguranca;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Gera o cenário de desenvolvimento. Ver IServicoSemente para o porquê.
///
/// DETERMINÍSTICO: `Random` com semente fixa. Rodar duas vezes produz os mesmos contatos, com
/// os mesmos valores — é o que permite comparar uma tela antes e depois de uma mudança de
/// layout sem que o dado tenha mudado embaixo.
///
/// As DATAS, ao contrário, são relativas a agora. Um cenário com datas fixas envelhece e passa
/// a mostrar "última mensagem há 4 meses" em toda conversa.</summary>
public class ServicoSemente(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    TimeProvider relogio) : IServicoSemente
{
    /// <summary>A marca que identifica tudo que veio da semeadura. É o que torna a limpeza
    /// cirúrgica: dado digitado à mão não tem esta marca e sobrevive.</summary>
    public const string Marca = "semente-dev";

    private const string DominioSemente = "semente.dev";
    private const int Semente = 20260805;

    public async Task<ResumoSemente> SemearAsync(CancellationToken ct)
    {
        // Limpa antes: sem isso, a segunda execução colide em uq_contatos_telefone.
        await LimparAsync(ct);

        var rnd = new Random(Semente);
        var empresaId = contexto.EmpresaId;
        var agoraUtc = relogio.GetUtcNow().UtcDateTime;

        var etapas = await db.EtapasFunil.AsNoTracking().OrderBy(e => e.Ordem).ToListAsync(ct);
        if (etapas.Count == 0)
            throw new RegraDeNegocioException(
                "Esta empresa não tem funil configurado — não há onde colocar os contatos.");

        var conexao = await db.Conexoes.AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new RegraDeNegocioException(
                "Esta empresa não tem conexão cadastrada — a conversa precisa de uma.");

        var donoId = await db.Usuarios.AsNoTracking()
            .Where(u => u.Papel == PapelUsuario.Dono && u.Status == StatusUsuario.Ativo)
            .Select(u => (long?)u.Id).FirstOrDefaultAsync(ct);

        var usuarios = await CriarEquipeAsync(empresaId, ct);
        var contatos = await CriarContatosAsync(empresaId, etapas, usuarios, donoId, agoraUtc, rnd, ct);
        var (conversas, mensagens) = await CriarConversasAsync(
            empresaId, conexao, contatos, usuarios, donoId, agoraUtc, rnd, ct);
        var lembretes = await CriarLembretesAsync(empresaId, contatos, conversas, donoId, agoraUtc, ct);
        var feriados = await CriarFeriadoAsync(empresaId, agoraUtc, ct);

        return new ResumoSemente(
            contatos.Count, conversas.Count, mensagens, lembretes, usuarios.Count, feriados);
    }

    // ==================================================================== limpeza
    /// <summary>Apaga na ordem das chaves estrangeiras: mensagens referenciam lembrete e
    /// conversa, lembretes referenciam conversa e contato. Inverter a ordem viola FK.</summary>
    public async Task<ResumoSemente> LimparAsync(CancellationToken ct)
    {
        var empresaId = contexto.EmpresaId;

        var idsContatos = await db.Contatos.AsNoTracking()
            .Where(c => c.OrigemDetalhe == Marca)
            .Select(c => c.Id).ToListAsync(ct);

        var idsConversas = await db.Conversas.AsNoTracking()
            .Where(c => idsContatos.Contains(c.ContatoId))
            .Select(c => c.Id).ToListAsync(ct);

        var mensagens = await db.Mensagens
            .Where(m => idsContatos.Contains(m.ContatoId)).ExecuteDeleteAsync(ct);

        var lembretes = await db.Lembretes
            .Where(l => idsContatos.Contains(l.ContatoId)).ExecuteDeleteAsync(ct);

        var conversas = await db.Conversas
            .Where(c => idsContatos.Contains(c.ContatoId)).ExecuteDeleteAsync(ct);

        var contatos = await db.Contatos
            .Where(c => c.OrigemDetalhe == Marca).ExecuteDeleteAsync(ct);

        var usuarios = await db.Usuarios
            .Where(u => u.Email.EndsWith("@" + DominioSemente)).ExecuteDeleteAsync(ct);

        var feriados = await db.Feriados
            .Where(f => f.EmpresaId == empresaId && f.Nome.EndsWith("(semente)"))
            .ExecuteDeleteAsync(ct);

        db.ChangeTracker.Clear();
        return new ResumoSemente(contatos, conversas, mensagens, lembretes, usuarios, feriados);
    }

    // ==================================================================== equipe
    private async Task<List<Usuario>> CriarEquipeAsync(long empresaId, CancellationToken ct)
    {
        // Três papéis e um convite pendente: é o que a tela de Equipe precisa mostrar para se
        // avaliar (selo de papel, selo de status, botão de reenviar convite).
        var novos = new List<Usuario>
        {
            Pessoa(empresaId, "Beatriz Andrade", "beatriz", PapelUsuario.Gestor, StatusUsuario.Ativo),
            Pessoa(empresaId, "Rafael Nogueira", "rafael", PapelUsuario.Vendedor, StatusUsuario.Ativo),
            Pessoa(empresaId, "Camila Duarte", "camila", PapelUsuario.Vendedor, StatusUsuario.Convidado)
        };

        db.Usuarios.AddRange(novos);
        await db.SaveChangesAsync(ct);
        return novos;
    }

    private static Usuario Pessoa(
        long empresaId, string nome, string login, PapelUsuario papel, StatusUsuario status) => new()
        {
            EmpresaId = empresaId,
            Nome = nome,
            // ===================== O ID DA EMPRESA ENTRA NO E-MAIL =====================
            // `uq_usuarios_email` é GLOBAL, não por tenant. Com o e-mail fixo (`beatriz@…`), a
            // semente rodava uma vez por BANCO: o segundo tenant colidia com o primeiro e o
            // comando estourava com violação de unicidade.
            //
            // Reexecutar no MESMO tenant sempre funcionou, porque o `LimparAsync` roda antes e o
            // query filter o recorta por empresa — o problema era só entre tenants.
            //
            // `beatriz.7@semente.dev` continua sendo reconhecido pelo sufixo do domínio, que é
            // como a limpeza os encontra.
            // ==========================================================================
            Email = $"{login}.{empresaId}@{DominioSemente}",
            // Convidado NÃO tem senha (ck_usuarios_senha permite); os ativos entram com uma
            // senha conhecida, para dar para logar como eles em desenvolvimento.
            SenhaHash = status == StatusUsuario.Convidado ? null : HashSenha.Gerar("semente-dev-123"),
            Papel = papel,
            Status = status,
            TokenConvite = status == StatusUsuario.Convidado
                ? Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant()
                : null,
            ConviteExpira = status == StatusUsuario.Convidado
                ? DateTime.UtcNow.AddDays(7)
                : null
        };

    // ==================================================================== contatos
    private static readonly string[] Nomes =
    [
        "Clínica Saúde Total", "Oficina Boa Vista", "Loja Mix Presentes", "Padaria Pão Quente",
        "Auto Peças Silva", "Studio Bella Estética", "Mercado Central", "Pet Shop Amigo Fiel",
        "Ótica Visão Clara", "Açaí do Ponto", "Construtora Lima", "Escola Aprender Mais",
        "Farmácia Vida", "Barbearia Nobre", "Floricultura Íris", "Restaurante Sabor Caseiro",
        "Academia Força Total", "Gráfica Rápida", "Doceria Mel & Cia", "Lava-Jato Brilho",
        "João Mendes", "Maria Ferreira", "Carlos Souza", "Ana Beatriz Rocha", "Rafael Nogueira",
        "Patrícia Lima", "Bruno Carvalho", "Juliana Reis", "Marcos Antunes", "Fernanda Dias",
        "Ricardo Peixoto", "Larissa Monteiro", "Gustavo Barros", "Tatiane Freitas",
        "Eduardo Campos", "Simone Vasconcelos", "Thiago Moreira", "Vanessa Aguiar",
        "Leonardo Pacheco", "Cristiane Amorim"
    ];

    private static readonly OrigemLead[] Origens =
    [
        OrigemLead.Instagram, OrigemLead.Whatsapp, OrigemLead.Facebook, OrigemLead.Google,
        OrigemLead.Indicacao, OrigemLead.Site, OrigemLead.Qrcode, OrigemLead.Manual
    ];

    /// <summary>Quantos contatos em cada etapa. Formato de funil de verdade: muitos no topo,
    /// poucos no fim — com distribuição uniforme o kanban não mostra o afunilamento, que é
    /// justamente o que a tela existe para comunicar.</summary>
    private static readonly int[] PorEtapa = [14, 9, 6, 5, 6];

    private async Task<List<Contato>> CriarContatosAsync(
        long empresaId, List<EtapaFunil> etapas, List<Usuario> equipe, long? donoId,
        DateTime agoraUtc, Random rnd, CancellationToken ct)
    {
        var responsaveis = new List<long?> { donoId };
        responsaveis.AddRange(equipe.Where(u => u.Status == StatusUsuario.Ativo).Select(u => (long?)u.Id));
        responsaveis.Add(null);   // sem dono: a caixa precisa da aba "Não atribuídas"

        var contatos = new List<Contato>();
        var indice = 0;

        for (var e = 0; e < etapas.Count && e < PorEtapa.Length; e++)
        {
            for (var i = 0; i < PorEtapa[e]; i++)
            {
                var nome = Nomes[indice % Nomes.Length];
                var etapaGanho = etapas[e].EGanho;

                contatos.Add(new Contato
                {
                    EmpresaId = empresaId,
                    Nome = nome,
                    // Faixa 96000-0000 em diante: não colide com o contato real do ambiente.
                    Telefone = $"5584{96000000 + indice:D8}",
                    Email = indice % 3 == 0 ? $"contato{indice}@{DominioSemente}" : null,
                    Origem = Origens[indice % Origens.Length],
                    OrigemDetalhe = Marca,          // A MARCA — é por ela que a limpeza acha
                    EtapaId = etapas[e].Id,
                    OrdemKanban = (i + 1) * 10m,
                    ResponsavelId = responsaveis[indice % responsaveis.Count],
                    // Etapa de venda sempre com valor; as outras, dois terços com valor.
                    Valor = etapaGanho || indice % 3 != 0
                        ? Math.Round((decimal)(rnd.NextDouble() * 8500 + 400), 2)
                        : null,
                    Observacoes = indice % 5 == 0
                        ? "Cliente pediu orçamento por WhatsApp. Prefere contato à tarde."
                        : null
                });
                indice++;
            }
        }

        db.Contatos.AddRange(contatos);
        await db.SaveChangesAsync(ct);

        await AjustarMarcosAsync(contatos, etapas, agoraUtc, rnd, ct);
        db.ChangeTracker.Clear();
        return contatos;
    }

    /// <summary>Carimba as datas depois do INSERT.
    ///
    /// O InterceptorAuditoria sobrescreve `criado_em` em todo Added — então datas passadas
    /// precisam ser aplicadas por UPDATE. É também onde nascem os ganhos e as perdas, que são
    /// o que faz o dashboard sair do zero.</summary>
    private async Task AjustarMarcosAsync(
        List<Contato> contatos, List<EtapaFunil> etapas, DateTime agoraUtc, Random rnd,
        CancellationToken ct)
    {
        var inicioDoMes = new DateTime(agoraUtc.Year, agoraUtc.Month, 1, 12, 0, 0, DateTimeKind.Utc);
        var etapaGanho = etapas.FirstOrDefault(e => e.EGanho);

        // Espalha a criação nos últimos 60 dias — o "leads hoje" do dashboard depende disso.
        for (var i = 0; i < contatos.Count; i++)
        {
            // Os cinco primeiros entraram HOJE.
            var criado = i < 5 ? agoraUtc.AddHours(-rnd.Next(1, 8)) : agoraUtc.AddDays(-rnd.Next(1, 60));
            var id = contatos[i].Id;
            await db.Contatos.Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.CriadoEm, criado), ct);
        }

        if (etapaGanho is null) return;

        // GANHOS deste mês: os que estão na etapa de venda. É a única forma coerente — contato
        // na coluna Venda sem `ganho_em` é o estado divergente que a porta única impede.
        var naVenda = contatos.Where(c => c.EtapaId == etapaGanho.Id).ToList();
        for (var i = 0; i < naVenda.Count; i++)
        {
            var quando = inicioDoMes.AddDays(rnd.Next(0, Math.Max(1, agoraUtc.Day - 1)))
                                    .AddHours(rnd.Next(9, 18));
            var id = naVenda[i].Id;
            await db.Contatos.Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.GanhoEm, quando), ct);
        }

        // PERDIDOS: três, tirados das etapas do meio. Entram na taxa de conversão sem entrar no
        // faturamento — e somem do kanban pelo índice parcial, o que exercita esse filtro.
        var candidatos = contatos
            .Where(c => c.EtapaId != etapaGanho.Id && c.EtapaId != etapas[0].Id)
            .Take(3).ToList();

        string[] motivos = ["Achou caro", "Comprou do concorrente", "Sumiu depois da proposta"];
        for (var i = 0; i < candidatos.Count; i++)
        {
            var id = candidatos[i].Id;
            var quando = agoraUtc.AddDays(-rnd.Next(1, 12));
            var motivo = motivos[i % motivos.Length];
            await db.Contatos.Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.PerdidoEm, quando)
                    .SetProperty(c => c.MotivoPerda, motivo), ct);
        }

        // Um ANONIMIZADO: a lista e o kanban têm que escondê-lo, e o dashboard tem que continuar
        // contando. Sem um exemplo, esse caminho nunca é visto em desenvolvimento.
        var anonimo = contatos.Last();
        await db.Contatos.Where(c => c.Id == anonimo.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Nome, "Contato anonimizado")
                .SetProperty(c => c.Telefone, $"ANON-{anonimo.Id}")
                .SetProperty(c => c.Email, (string?)null)
                .SetProperty(c => c.Observacoes, (string?)null)
                .SetProperty(c => c.AnonimizadoEm, agoraUtc.AddDays(-2)), ct);
    }

    // ==================================================================== conversas
    private static readonly string[] Entradas =
    [
        "Oi, boa tarde! Vocês ainda têm aquele modelo?",
        "Bom dia! Queria saber o preço, por favor",
        "Consegue me mandar o orçamento hoje?",
        "Vocês atendem no sábado?",
        "Obrigado! Vou conversar aqui e te retorno",
        "Ficou um pouco acima do que eu esperava…",
        "Perfeito, pode fechar então!",
        "Ainda estou avaliando, te aviso amanhã"
    ];

    private static readonly string[] Saidas =
    [
        "Oi! Tudo bem? Temos sim, posso te mandar as fotos",
        "Bom dia! Claro, já te passo os valores",
        "Mandei o orçamento no seu e-mail também 👍",
        "Atendemos das 8h às 13h no sábado",
        "Combinado! Fico à disposição",
        "Consigo fazer uma condição melhor no pagamento à vista",
        "Que ótimo! Já vou separar aqui pra você",
        "Sem problema, qualquer dúvida é só chamar"
    ];

    /// <summary>Idades de espera, em minutos, escolhidas para cobrir TODAS as faixas do
    /// semáforo com os limites padrão (amarelo 60, vermelho 240) — e o caso do desconto de
    /// expediente, com uma espera que atravessa a noite.</summary>
    private static readonly int[] EsperasEmMinutos = [8, 35, 95, 180, 320, 600, 1500];

    private async Task<(List<Conversa> Conversas, int Mensagens)> CriarConversasAsync(
        long empresaId, Conexao conexao, List<Contato> contatos, List<Usuario> equipe,
        long? donoId, DateTime agoraUtc, Random rnd, CancellationToken ct)
    {
        // Só os contatos VIVOS e não terminais ganham conversa aberta — conversa de contato
        // ganho ou anonimizado polui a caixa sem exercitar nada.
        var elegiveis = await db.Contatos.AsNoTracking()
            .Where(c => c.OrigemDetalhe == Marca && c.AnonimizadoEm == null && c.PerdidoEm == null)
            .OrderBy(c => c.Id).Take(24).ToListAsync(ct);

        var responsaveis = new List<long?> { donoId, null };
        responsaveis.AddRange(equipe.Where(u => u.Status == StatusUsuario.Ativo).Select(u => (long?)u.Id));

        // Os campos são decididos ANTES do INSERT, e não por UPDATE depois. Além de ser uma ida
        // ao banco em vez de duas por conversa, evita um `ExecuteUpdate` com enum anulável —
        // que o EF não traduz quando o tipo da coluna é enum nativo do Postgres.
        var conversas = new List<Conversa>();
        var plano = new List<(bool Esperando, DateTime Ultima)>();

        for (var i = 0; i < elegiveis.Count; i++)
        {
            // Um em cada oito fica RESOLVIDO, para a aba correspondente ter conteúdo.
            var resolvida = i % 8 == 7;
            // Dos abertos, dois terços estão esperando resposta (última mensagem de ENTRADA).
            var esperando = !resolvida && i % 3 != 2;

            var esperaMin = EsperasEmMinutos[i % EsperasEmMinutos.Length];
            var ultima = esperando
                ? agoraUtc.AddMinutes(-esperaMin)
                : agoraUtc.AddHours(-rnd.Next(2, 72));

            var responsavel = responsaveis[i % responsaveis.Count];

            conversas.Add(new Conversa
            {
                EmpresaId = empresaId,
                ContatoId = elegiveis[i].Id,
                ConexaoId = conexao.Id,
                ResponsavelId = responsavel,
                AtribuidoEm = responsavel != null ? ultima : null,
                Status = resolvida ? StatusConversa.Resolvida : StatusConversa.Aberta,
                ResolvidoEm = resolvida ? ultima : null,
                UltimaMensagemEm = ultima,
                UltimaMensagemDirecao = esperando ? DirecaoMensagem.Entrada : DirecaoMensagem.Saida,
                UltimaMensagemPrevia = esperando
                    ? Entradas[i % Entradas.Length]
                    : Saidas[i % Saidas.Length],
                // aguardando_desde SÓ quando a última é de ENTRADA — é o invariante de que o
                // semáforo inteiro depende. Preenchê-lo com a última de saída faria a conversa
                // ficar vermelha logo depois de ser respondida.
                AguardandoDesde = esperando ? ultima : null,
                NaoLidas = esperando ? rnd.Next(1, 4) : 0
            });
            plano.Add((esperando, ultima));
        }

        db.Conversas.AddRange(conversas);
        await db.SaveChangesAsync(ct);

        var totalMensagens = 0;
        for (var i = 0; i < conversas.Count; i++)
            totalMensagens += await CriarMensagensAsync(
                empresaId, conexao, conversas[i], plano[i].Ultima, plano[i].Esperando, donoId, rnd, ct);

        db.ChangeTracker.Clear();
        return (conversas, totalMensagens);
    }

    private async Task<int> CriarMensagensAsync(
        long empresaId, Conexao conexao, Conversa conversa, DateTime ultima, bool terminaEmEntrada,
        long? donoId, Random rnd, CancellationToken ct)
    {
        var quantas = rnd.Next(4, 11);
        var mensagens = new List<Mensagem>();

        for (var i = 0; i < quantas; i++)
        {
            var ehUltima = i == quantas - 1;
            // Alterna, terminando na direção que a conversa exige.
            var entrada = ehUltima
                ? terminaEmEntrada
                : (quantas - 1 - i) % 2 == (terminaEmEntrada ? 1 : 0);

            var quando = ultima.AddMinutes(-(quantas - 1 - i) * rnd.Next(4, 40));

            var m = new Mensagem
            {
                EmpresaId = empresaId,
                ConversaId = conversa.Id,
                ContatoId = conversa.ContatoId,
                ConexaoId = conexao.Id,
                InstanceName = conexao.InstanceName,
                Direcao = entrada ? DirecaoMensagem.Entrada : DirecaoMensagem.Saida,
                Texto = entrada ? Entradas[i % Entradas.Length] : Saidas[i % Saidas.Length],
                WaMessageId = $"SEM-{conversa.Id}-{i}",
                RecebidaEm = entrada ? quando : null,
                EnviadaEm = entrada ? null : quando,
                // ck_msg_data_disparo: saída EXIGE data_disparo.
                DataDisparo = entrada ? null : DateOnly.FromDateTime(quando),
                ReservadoEm = quando,
                EnviadoPor = entrada ? null : donoId,
                // ACK só nas de saída: 3 = entregue, 4 = lido. Dá conteúdo aos dois ticks.
                Ack = entrada ? null : (short)(i % 2 == 0 ? 4 : 3),
                AckEm = entrada ? null : quando.AddMinutes(1),
                Tentativas = entrada ? (short)0 : (short)1
            };
            mensagens.Add(m);
        }

        // UMA falha e UMA expirada por conversa par: os dois estados terminais do envio
        // precisam aparecer em desenvolvimento, senão o tick de erro nunca é visto.
        if (conversa.Id % 2 == 0 && mensagens.Count > 2)
        {
            var falha = mensagens[1];
            if (falha.Direcao == DirecaoMensagem.Saida)
            {
                falha.EnviadaEm = null;
                falha.Ack = null;
                falha.AckEm = null;
                falha.Erro = "Evolution API inacessível (timeout).";
                falha.WaMessageId = null;   // não confirmou, não tem id
                falha.Tentativas = 2;
            }
        }

        db.Mensagens.AddRange(mensagens);
        await db.SaveChangesAsync(ct);

        // ===================== POR QUE ESTE UPDATE PASSOU A SER NECESSÁRIO =====================
        // O `InterceptorAuditoria` carimba `criado_em` com o relógio em todo INSERT. Enquanto a
        // semente servia só à thread — que ordena por `id` e exibe `enviada_em`/`recebida_em` —
        // isso não incomodava, e havia um comentário aqui dizendo exatamente isso.
        //
        // O comentário ficou FALSO no PI-4: a série temporal e o feed de atividades usam
        // `criado_em` como o instante do evento (é o que o webhook grava, junto com `recebida_em`,
        // e é o que tem índice). Com o carimbo de "agora", toda mensagem semeada caía no dia da
        // geração: o gráfico de tempo de resposta mostrava 55 pares numa única semana, com média
        // ZERO, e parecia defeito da consulta.
        //
        // Um UPDATE por mensagem numa rotina de desenvolvimento é barato; dado de teste que mente
        // sobre a forma do dado real, não.
        // ======================================================================================
        foreach (var m in mensagens)
        {
            var quando = m.RecebidaEm ?? m.EnviadaEm ?? m.ReservadoEm;
            var id = m.Id;
            await db.Mensagens.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CriadoEm, quando), ct);
        }

        db.ChangeTracker.Clear();
        return mensagens.Count;
    }

    // ==================================================================== lembretes
    private async Task<int> CriarLembretesAsync(
        long empresaId, List<Contato> contatos, List<Conversa> conversas, long? donoId,
        DateTime agoraUtc, CancellationToken ct)
    {
        var hoje = DateOnly.FromDateTime(
            FusoDeNegocio.AgoraNo(relogio, FusoDeNegocio.Resolver(FusoDeNegocio.PadraoBrasil)));

        var vivos = await db.Contatos.AsNoTracking()
            .Where(c => c.OrigemDetalhe == Marca && c.AnonimizadoEm == null && c.PerdidoEm == null)
            .OrderBy(c => c.Id).Take(12).ToListAsync(ct);

        if (vivos.Count == 0) return 0;

        var porContato = conversas.ToDictionary(c => c.ContatoId, c => (long?)c.Id);
        var lembretes = new List<Lembrete>();

        // (data, hora, título, é automático)
        var receita = new (int DiasRelativos, string? Hora, string Titulo, bool Automatico)[]
        {
            (-3, null,    "Retomar contato — sumiu depois do orçamento", true),
            (-1, "09:00", "Ligar para confirmar a visita",               false),
            (-1, null,    "Enviar a proposta revisada",                  false),
            ( 0, "09:30", "Ligar de manhã",                              false),
            ( 0, "14:00", "Mandar catálogo atualizado",                  false),
            ( 0, "17:30", "Confirmar entrega de amanhã",                 false),
            ( 0, null,    "Retomar contato",                             true),
            ( 1, "10:00", "Reunião no cliente",                          false),
            ( 2, null,    "Follow-up da proposta",                       false),
            ( 4, "15:00", "Renegociar condição de pagamento",            false)
        };

        for (var i = 0; i < receita.Length && i < vivos.Count; i++)
        {
            var (dias, hora, titulo, automatico) = receita[i];
            var contato = vivos[i];

            lembretes.Add(new Lembrete
            {
                EmpresaId = empresaId,
                ContatoId = contato.Id,
                ConversaId = porContato.GetValueOrDefault(contato.Id),
                Origem = automatico ? OrigemLembrete.Automatico : OrigemLembrete.Manual,
                Status = StatusLembrete.Pendente,
                DataAlvo = hoje.AddDays(dias),
                HoraAlvo = hora is null ? null : TimeOnly.Parse(hora),
                Titulo = titulo,
                Observacao = i % 3 == 0 ? "Cliente pediu para chamar antes de ir." : null,
                // ck_lembretes_texto: envia_mensagem exige texto. Só os automáticos enviam, e
                // cada um está num contato diferente — uq_lembrete_teto_diario continua valendo.
                EnviaMensagem = automatico,
                TextoMensagem = automatico
                    ? $"Oi, {PrimeiroNome(contato.Nome)}! Passando para saber se você ainda tem interesse."
                    : null,
                ResponsavelId = donoId
            });
        }

        // Dois já CONCLUÍDOS: o detalhe do contato separa pendentes de feitos, e sem exemplo
        // essa metade da tela fica sempre vazia.
        for (var i = 0; i < 2 && i + receita.Length < vivos.Count; i++)
            lembretes.Add(new Lembrete
            {
                EmpresaId = empresaId,
                ContatoId = vivos[i + receita.Length].Id,
                Origem = OrigemLembrete.Manual,
                Status = StatusLembrete.Concluido,
                DataAlvo = hoje.AddDays(-(i + 2)),
                Titulo = i == 0 ? "Enviar orçamento" : "Confirmar recebimento",
                ConcluidoEm = agoraUtc.AddDays(-(i + 1)),
                ConcluidoPor = donoId,
                ResponsavelId = donoId
            });

        db.Lembretes.AddRange(lembretes);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return lembretes.Count;
    }

    // ==================================================================== feriado
    private async Task<int> CriarFeriadoAsync(long empresaId, DateTime agoraUtc, CancellationToken ct)
    {
        var data = DateOnly.FromDateTime(agoraUtc).AddDays(21);

        // A tela de configurações precisa de pelo menos um feriado MANUAL para mostrar o botão
        // de apagar — os nacionais só oferecem "trabalhamos neste dia".
        if (await db.Feriados.AnyAsync(f => f.Data == data, ct)) return 0;

        db.Feriados.Add(new Feriado
        {
            EmpresaId = empresaId,
            Data = data,
            Nome = "Aniversário da cidade (semente)",
            Abrangencia = AbrangenciaFeriado.Manual,
            CriadoEm = agoraUtc
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return 1;
    }

    private static string PrimeiroNome(string? nome) =>
        string.IsNullOrWhiteSpace(nome)
            ? "tudo bem"
            : nome.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}
