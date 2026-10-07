using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O plano do dia. Duas consultas, zero tabela nova.</summary>
public class ServicoMeuDia(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    TimeProvider relogio) : IServicoMeuDia
{
    public async Task<MeuDia> MeuDiaAsync(int limite, CancellationToken ct)
    {
        // Clampa aqui e não só no controller: a regra tem que valer também para quem chamar o
        // serviço por dentro.
        limite = Math.Clamp(limite, 1, LimiteMeuDia.Maximo);
        var meuId = contexto.UsuarioId;

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new
            {
                e.FusoHorario, e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana
            })
            .FirstOrDefaultAsync(ct);

        var fuso = FusoDeNegocio.Resolver(empresa?.FusoHorario);
        var agora = FusoDeNegocio.AgoraNo(relogio, fuso);
        var hoje = DateOnly.FromDateTime(agora);
        var janela = empresa is null
            ? JanelaAtendimento.Padrao
            : new JanelaAtendimento(empresa.JanelaHoraInicio, empresa.JanelaHoraFim, empresa.JanelaDiasSemana);

        // Feriados da janela: o desconto do tempo útil precisa saber quais dias no MEIO da
        // espera foram fechados. Espera mais velha que isto não é medida em número — ver
        // `EsperaAcimaDaJanela`.
        var limiteDaJanela = hoje.AddDays(-JanelaDeEspera.Dias);

        var feriados = (await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= limiteDaJanela && f.Data <= hoje
                     // Global dispensado pela empresa não conta: para ela aquele dia foi útil.
                     && !db.FeriadosIgnorados.Any(i => i.FeriadoId == f.Id))
            .Select(f => f.Data).ToListAsync(ct)).ToHashSet();

        // ---- (a) conversas esperando resposta: minhas ou sem dono ----
        // Filtro e ordenação no SQL; só a conversão de fuso e o cálculo de minutos úteis (que
        // depende dos feriados) ficam em memória, sobre o conjunto JÁ recortado.
        var esperando = db.Conversas.AsNoTracking()
            .Where(c => c.Status == StatusConversa.Aberta
                     && c.AguardandoDesde != null
                     && (c.ResponsavelId == meuId || c.ResponsavelId == null));

        // O TOTAL, antes do corte. `COUNT` no banco: é o número que o cartão do dashboard usa
        // para escrever "6 de 23", e contar a lista cortada diria "6 de 6".
        var totalEsperando = await esperando.CountAsync(ct);

        // ===================== POR QUE `Take` AQUI É EXATO =====================
        // A ordenação que vale é `MinutosUteis` DESC, e ela roda em memória logo abaixo — depois
        // do corte. Cortar antes de ordenar costuma ser bug; aqui não é.
        //
        // Minutos úteis é função MONOTONICAMENTE NÃO-DECRESCENTE da duração da espera: uma
        // conversa que espera desde ontem não pode ter MENOS minutos úteis que uma que espera
        // desde hoje, porque o intervalo dela contém o da outra. Feriado e fim de semana zeram
        // trechos para as duas igualmente.
        //
        // Logo `aguardando_desde ASC` produz exatamente a mesma ordem que `minutos_uteis DESC`, e
        // as N primeiras do SQL são as N que ficariam no topo depois. Há teste para isso.
        // ======================================================================
        var aguardando = await esperando
            .OrderBy(c => c.AguardandoDesde)
            .Take(limite)
            .Select(c => new
            {
                c.Id, c.ContatoId, Nome = c.Contato.Nome, c.Contato.Telefone, c.AguardandoDesde
            })
            .ToListAsync(ct);

        var acoesConversa = aguardando
            .Select(c => AcaoDeConversa(
                c.Id, c.ContatoId, c.Nome, c.Telefone, c.AguardandoDesde!.Value,
                fuso, agora, janela, feriados, limiteDaJanela))
            .ToList();

        // ---- (b) lembretes pendentes vencidos ou de hoje, do responsável ----
        // `data_alvo <= hoje` inclui o atrasado: com igualdade estrita, um dia de folga do
        // vendedor faria a tarefa sumir da lista para sempre.
        // ⚠️ O PREDICADO SAIU DAQUI (MD-1). Ele passou a ser feito em DOIS lugares — esta lista e
        // o contador ao lado do "Meu Dia" no menu — e escrito por extenso nos dois ele divergiria.
        // Ver o comentário longo de `RegrasLembrete`: é a mesma cicatriz do `RegrasNegociacao`.
        var pendentes = db.Lembretes.AsNoTracking().Where(RegrasLembrete.MeusDeHoje(meuId, hoje));

        var totalLembretes = await pendentes.CountAsync(ct);

        // O que sobrou do teto depois das conversas. A conversa esperando é mais urgente que o
        // lembrete — o cliente está do outro lado —, então ela pega o espaço primeiro.
        var lembretes = await pendentes
            .OrderBy(l => l.HoraAlvo == null).ThenBy(l => l.HoraAlvo).ThenBy(l => l.CriadoEm)
            .Take(Math.Max(0, limite - aguardando.Count))
            .Select(l => new AcaoDoDia(
                // Literal, e não `TipoAcao.Lembrete.ToString().ToLower()`: esta projeção é
                // traduzida para SQL, e o EF não traduz ToString() sobre constante de enum.
                "lembrete", l.Id, l.ContatoId, l.Contato.Nome, l.Contato.Telefone,
                l.Titulo, l.ConversaId, null, null, false, l.HoraAlvo, l.DataAlvo,
                l.DataAlvo < hoje))
            .ToListAsync(ct);

        // Quem espera há mais tempo primeiro; depois os lembretes por hora.
        var acoes = acoesConversa
            .OrderByDescending(a => a.MinutosUteis)
            .Concat(lembretes)
            .ToList();

        // Os contadores são os TOTAIS, não o tamanho das listas cortadas. Ver `MeuDia`.
        return new MeuDia(acoes, totalEsperando, totalLembretes);
    }

    // ==================================================================== a página (AUD-XX)
    /// <summary>===================== A ORDEM DO DIA, PAGINADA NO SERVIDOR =====================
    ///
    /// A ordem é o MOMENTO em que a ação deveria acontecer — a mesma que a tela calculava:
    ///   · conversa: quando o cliente começou a esperar;
    ///   · lembrete com hora: a data-alvo naquela hora, no fuso da empresa;
    ///   · lembrete sem hora: o fim da data-alvo ("em algum momento do dia", depois dos marcados).
    /// Lembrete atrasado tem data no passado e sobe sozinho.
    ///
    /// ⚠️ DUAS CONSULTAS, E NÃO UM `UNION` EM SQL CRU. As duas listas já têm as regras delas em UMA
    /// cópia (`RegrasLembrete.MeusDeHoje` e o recorte de quem espera), e reescrevê-las em SQL seria a
    /// terceira — o defeito que `RegrasLembrete` nasceu para impedir. Cada lista vem ORDENADA do
    /// banco e cortada em `pagina * tamanho`: nenhuma ação da página pedida pode estar além disso
    /// em nenhuma das duas. A intercalação e o corte da página acontecem aqui, sobre esse recorte.
    /// =====================================================================================</summary>
    public async Task<PaginaDoDia> PaginaAsync(
        FiltroDoDia filtro, int pagina, int tamanho, CancellationToken ct)
    {
        pagina = Math.Max(pagina, 1);
        tamanho = Math.Clamp(tamanho, 1, LimiteMeuDia.Maximo);
        var meuId = contexto.UsuarioId;

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new
            {
                e.FusoHorario, e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana
            })
            .FirstOrDefaultAsync(ct);

        var fuso = FusoDeNegocio.Resolver(empresa?.FusoHorario);
        var agora = FusoDeNegocio.AgoraNo(relogio, fuso);
        var hoje = DateOnly.FromDateTime(agora);
        var janela = empresa is null
            ? JanelaAtendimento.Padrao
            : new JanelaAtendimento(empresa.JanelaHoraInicio, empresa.JanelaHoraFim, empresa.JanelaDiasSemana);
        var limiteDaJanela = hoje.AddDays(-JanelaDeEspera.Dias);

        var esperando = db.Conversas.AsNoTracking()
            .Where(c => c.Status == StatusConversa.Aberta
                     && c.AguardandoDesde != null
                     && (c.ResponsavelId == meuId || c.ResponsavelId == null));
        var meusLembretes = db.Lembretes.AsNoTracking().Where(RegrasLembrete.MeusDeHoje(meuId, hoje));
        var atrasados = meusLembretes.Where(l => l.DataAlvo < hoje);

        // As contagens das QUATRO abas, sempre — é o número de cada pílula, e não depende da aba
        // nem da página abertas.
        var responder = await esperando.CountAsync(ct);
        var lembrete = await meusLembretes.CountAsync(ct);
        var vencidos = await atrasados.CountAsync(ct);
        var contagens = new ContagemDoDia(responder + lembrete, responder, lembrete, vencidos);

        int total;
        if (filtro == FiltroDoDia.Responder)
        {
            total = contagens.Responder;
        }
        else if (filtro == FiltroDoDia.Lembrete)
        {
            total = contagens.Lembrete;
        }
        else if (filtro == FiltroDoDia.Atrasadas)
        {
            total = contagens.Atrasadas;
        }
        else
        {
            total = contagens.Todas;
        }

        // Até onde cada lista precisa ir para a página pedida estar completa.
        var ate = pagina * tamanho;
        var candidatos = new List<(DateTime Momento, int Ordem, long Id)>();

        if (filtro == FiltroDoDia.Todas || filtro == FiltroDoDia.Responder)
        {
            var conversas = await esperando
                .OrderBy(c => c.AguardandoDesde).ThenBy(c => c.Id)
                .Take(ate)
                .Select(c => new { c.Id, c.AguardandoDesde })
                .ToListAsync(ct);

            foreach (var c in conversas)
            {
                candidatos.Add((DateTime.SpecifyKind(c.AguardandoDesde!.Value, DateTimeKind.Utc), 0, c.Id));
            }
        }

        if (filtro != FiltroDoDia.Responder)
        {
            var fonte = filtro == FiltroDoDia.Atrasadas ? atrasados : meusLembretes;

            // Na mesma ordem do momento: a data, e dentro dela os de hora marcada antes dos sem hora.
            var lembretes = await fonte
                .OrderBy(l => l.DataAlvo).ThenBy(l => l.HoraAlvo == null).ThenBy(l => l.HoraAlvo)
                .ThenBy(l => l.Id)
                .Take(ate)
                .Select(l => new { l.Id, l.DataAlvo, l.HoraAlvo })
                .ToListAsync(ct);

            foreach (var l in lembretes)
            {
                candidatos.Add((MomentoDoLembrete(l.DataAlvo, l.HoraAlvo, fuso), 1, l.Id));
            }
        }

        // Empate de momento: a conversa antes — o cliente está do outro lado esperando.
        var daPagina = candidatos
            .OrderBy(x => x.Momento).ThenBy(x => x.Ordem).ThenBy(x => x.Id)
            .Skip((pagina - 1) * tamanho)
            .Take(tamanho)
            .ToList();

        var idsConversa = daPagina.Where(x => x.Ordem == 0).Select(x => x.Id).ToList();
        var idsLembrete = daPagina.Where(x => x.Ordem == 1).Select(x => x.Id).ToList();

        var feriados = (await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= limiteDaJanela && f.Data <= hoje
                     && !db.FeriadosIgnorados.Any(i => i.FeriadoId == f.Id))
            .Select(f => f.Data).ToListAsync(ct)).ToHashSet();

        var conversasDaPagina = await db.Conversas.AsNoTracking()
            .Where(c => idsConversa.Contains(c.Id))
            .Select(c => new
            {
                c.Id, c.ContatoId, Nome = c.Contato.Nome, c.Contato.Telefone, c.AguardandoDesde
            })
            .ToDictionaryAsync(c => c.Id, ct);

        var lembretesDaPagina = await db.Lembretes.AsNoTracking()
            .Where(l => idsLembrete.Contains(l.Id))
            .Select(l => new AcaoDoDia(
                "lembrete", l.Id, l.ContatoId, l.Contato.Nome, l.Contato.Telefone,
                l.Titulo, l.ConversaId, null, null, false, l.HoraAlvo, l.DataAlvo,
                l.DataAlvo < hoje))
            .ToDictionaryAsync(l => l.Id, ct);

        var itens = new List<AcaoDoDia>(daPagina.Count);
        foreach (var x in daPagina)
        {
            if (x.Ordem == 1)
            {
                itens.Add(lembretesDaPagina[x.Id]);
                continue;
            }

            var c = conversasDaPagina[x.Id];
            itens.Add(AcaoDeConversa(
                c.Id, c.ContatoId, c.Nome, c.Telefone, c.AguardandoDesde!.Value,
                fuso, agora, janela, feriados, limiteDaJanela));
        }

        var totalPaginas = Paginacao.TotalDePaginas(total, tamanho);

        return new PaginaDoDia(itens, contagens, total, pagina, tamanho, totalPaginas);
    }

    /// <summary>A conversa esperando, como item do dia — a MESMA montagem para a lista e para a
    /// página.</summary>
    private static AcaoDoDia AcaoDeConversa(
        long id, long contatoId, string nome, string telefone, DateTime aguardandoDesde,
        TimeZoneInfo fuso, DateTime agora, JanelaAtendimento janela, HashSet<DateOnly> feriados,
        DateOnly limiteDaJanela)
    {
        var desde = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(aguardandoDesde, DateTimeKind.Utc), fuso);

        // ===== ACIMA DA JANELA, NÃO SE INVENTA NÚMERO =====
        // Os feriados carregados cobrem `JanelaDeEspera.Dias`. Para uma espera mais velha,
        // `MinutosUteis` sairia SEM descontar os feriados anteriores ao recorte — maior que o
        // real, e com cara de exato. A comparação é sobre a DATA da espera, não sobre o
        // resultado do cálculo: perguntar depois já seria tarde.
        var acimaDaJanela = DateOnly.FromDateTime(desde) < limiteDaJanela;

        return new AcaoDoDia(
            TipoAcao.Responder.ToString().ToLower(), id, contatoId, nome, telefone,
            $"Responder {nome}", id, aguardandoDesde,
            acimaDaJanela ? null : TempoUtil.MinutosUteis(desde, agora, janela, feriados),
            acimaDaJanela,
            null, null, false);
    }

    /// <summary>O instante, em UTC, em que o lembrete deveria acontecer: a data-alvo na hora
    /// marcada, ou no fim do dia quando não há hora.</summary>
    private static DateTime MomentoDoLembrete(DateOnly dataAlvo, TimeOnly? horaAlvo, TimeZoneInfo fuso)
    {
        var hora = horaAlvo ?? new TimeOnly(23, 59);
        return TimeZoneInfo.ConvertTimeToUtc(dataAlvo.ToDateTime(hora), fuso);
    }
}
