using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>Contadores do shell. Tudo agregado no SQL — sao dois COUNT, uma leitura de conexao,
/// uma de empresa e uma faixa de feriados, justamente para caber num polling de 45s sem pesar.</summary>
public class ServicoPainel(
    NexoraDbContext db, TimeProvider relogio, IContextoEmpresa contexto) : IServicoPainel
{
    public async Task<StatusPainel> StatusAsync(CancellationToken ct)
    {
        var abertas = db.Conversas.AsNoTracking().Where(c => c.Status == StatusConversa.Aberta);

        // TODAS as conexoes, nao a primeira. O recorte em memoria e sobre uma lista limitada por
        // `ck_empresas_limite_conexoes` (no maximo 20 linhas), e os dois recortes saem da MESMA
        // leitura — duas consultas SQL aqui custariam mais que o filtro.
        var conexoes = await db.Conexoes.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new { c.Nome, c.Status, c.Numero, c.NumeroAnterior })
            .ToListAsync(ct);

        // PAREADA e caida. `Numero != null` e o que separa "caiu" de "ainda nao foi conectada":
        // a segunda nao merece alerta vermelho no topo de todas as telas.
        var caidas = conexoes
            .Where(c => c.Numero != null && c.Status != StatusConexao.Conectado)
            .Select(c => c.Nome)
            .ToList();

        // As faixas do semaforo e a janela saem da EMPRESA, nao de constante. Quem atende das 9h
        // as 18h nao quer o mesmo limite de quem atende 24h.
        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new
            {
                e.Ativo,
                e.FusoHorario,
                e.SemaforoAmareloMinutos, e.SemaforoVermelhoMinutos,
                e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana
            })
            .FirstOrDefaultAsync(ct);

        // ===================== O PORTAO QUE FALTAVA (OPE-1) =====================
        // `empresas.ativo` era lido em DOIS lugares: no login e na rodada de follow-up. Em nenhuma
        // requisicao. Desativar uma empresa, entao, bloqueava login NOVO e mais nada: quem tinha
        // recebido o token dez minutos antes continuava lendo a caixa e mandando WhatsApp por ate
        // DOZE HORAS, que e a validade dele.
        //
        // Para inadimplencia isso e um incomodo. Para abuso, ou para um incidente em que se quer
        // tirar alguem do ar AGORA, e um buraco.
        //
        // A conferencia mora AQUI, e nao num middleware, por uma razao de custo: este metodo JA LIA
        // `empresas` (as faixas do semaforo e a janela vem de la), e o painel JA CHAMA este endpoint
        // a cada 45 segundos. Entao e uma coluna a mais numa projecao existente -- zero consulta
        // nova, zero requisicao nova -- e a exposicao cai de ~12h para ~45s.
        //
        // ⚠️ NAO E UM SUBSTITUTO PARA REVOGACAO DE VERDADE. A correcao completa seria um carimbo em
        // `usuarios`/`empresas` conferido no `OnTokenValidated`, ao custo de uma ida ao banco por
        // requisicao (ou de um cache que precisa ser invalidado). Este caminho pega ~99% do valor
        // por ~0% do custo, e o que ele NAO pega e uma requisicao solta nos 45 segundos de janela.
        //
        // ⚠️ E ENCURTAR O TOKEN NAO SERIA A CORRECAO: trocaria um re-login por hora para TODO MUNDO
        // por um problema que acontece duas vezes por ano.
        //
        // ⚠️ DEPENDE DE `/api/painel/` NAO ESTAR NO `ehPublico` DO INTERCEPTOR. Nao esta, e nao pode
        // entrar: o 401 daqui tem de derrubar a sessao, que e o ponto inteiro.
        // =======================================================================
        if (empresa is not null && !empresa.Ativo)
            throw new RegraDeNegocioException(
                // A MESMA frase do login (`ServicoAutenticacao`), e de proposito: o interceptor
                // manda a pessoa para /entrar, e la ela le exatamente isto de novo. Duas redacoes
                // diferentes para o mesmo fato fariam parecer dois problemas.
                "Empresa inativa. Fale com o suporte.")
            { StatusHttp = 401 };

        // ===================== O MESMO PORTAO, PARA A PESSOA =====================
        // O bloco acima fechou a empresa e deixou o usuario aberto, e o buraco do usuario e MAIS
        // frequente: empresa se desativa duas vezes por ano, pessoa se desativa toda vez que
        // alguem sai da equipe.
        //
        // `usuarios.status` tambem era lido SO no login. Desativar um vendedor as 9h bloqueava
        // login novo e mais nada -- o token que ele ja tinha continuava lendo a caixa de entrada e
        // mandando WhatsApp pelo numero da empresa ate as 21h.
        //
        // ⚠️ ESTA CONSULTA E NOVA, ao contrario da da empresa (que pegou carona numa projecao que
        // ja existia). E uma leitura por chave primaria no endpoint que o painel ja bate a cada
        // 45s -- o preco de uma sonda de indice para fechar uma janela de doze horas.
        //
        // ⚠️ `Convidado` NAO entra aqui. Quem foi convidado e nao aceitou nao tem senha e nao tem
        // token; barrar aqui seria barrar um estado que nao chega a este codigo.
        //
        // Vale o mesmo aviso do bloco de cima: isto nao e revogacao de verdade, e sim ~99% do valor
        // por ~0% do custo. O que escapa e uma requisicao solta dentro dos 45 segundos.
        // ========================================================================
        var souAtivo = await db.Usuarios.AsNoTracking()
            .AnyAsync(u => u.Id == contexto.UsuarioId && u.Status == StatusUsuario.Ativo, ct);

        if (!souAtivo)
            throw new RegraDeNegocioException(
                // A MESMA frase do login, pelo mesmo motivo da de cima.
                "Usuário desativado. Fale com o dono da conta.")
            { StatusHttp = 401 };

        var fuso = FusoDeNegocio.Resolver(empresa?.FusoHorario);
        var hoje = DateOnly.FromDateTime(FusoDeNegocio.AgoraNo(relogio, fuso));

        // Faixa FECHADA de 30 dias: o desconto do tempo util so precisa dos dias no meio da
        // espera, e uma conversa parada ha mais de um mes ja esta vermelha de qualquer jeito.
        // Range scan sobre ix_feriados_data, algumas linhas — nao pesa no poll.
        var feriados = await db.Feriados.AsNoTracking()
            .Where(f => f.Data >= hoje.AddDays(-30) && f.Data <= hoje
                     // Os globais que a empresa dispensou não contam: para ela aquele dia foi
                     // de trabalho, e o desconto do semáforo tem que refletir isso.
                     && !db.FeriadosIgnorados.Any(i => i.FeriadoId == f.Id))
            .OrderBy(f => f.Data)
            .Select(f => f.Data)
            .ToListAsync(ct);

        var padrao = JanelaAtendimento.Padrao;

        return new StatusPainel(
            NaoLidas: await abertas.SumAsync(c => (int?)c.NaoLidas, ct) ?? 0,
            Aguardando: await abertas.CountAsync(c => c.AguardandoDesde != null, ct),

            // ===================== UMA CONSULTA A MAIS, E ELA FOI MEDIDA (MD-1) =====================
            // Este e o endpoint mais chamado do sistema — a cada 45s, por usuario logado. Qualquer
            // ida ao banco a mais aqui custa, e `A_CONFERENCIA_DE_ATIVO_NAO_CUSTA_CONSULTA_NOVA`
            // existe para o numero ficar MEDIDO em vez de esquecido: ele conta as consultas que
            // tocam `empresas`, `usuarios` e agora `lembretes`.
            //
            // O preco e baixo porque o indice ja existia e foi feito para esta pergunta:
            // `ix_lembretes_dia (empresa_id, data_alvo, responsavel_id) WHERE status = 'pendente'`.
            //
            // ⚠️ `hoje` E O MESMO DE CIMA, no fuso da empresa — nao `DateTime.UtcNow`. Com a data em
            // UTC, das 21h a meia-noite de Brasilia o contador ja estaria contando o dia seguinte, e
            // o lembrete de amanha apareceria hoje a noite.
            // =====================================================================================
            LembretesHoje: await db.Lembretes.AsNoTracking()
                .Where(RegrasLembrete.MeusDeHoje(contexto.UsuarioId, hoje))
                .CountAsync(ct),
            // Comeca como conectado quando nao ha conexao pareada ainda: melhor nao acender o
            // banner antes de a empresa ter passado pelo pareamento.
            WhatsappConectado: caidas.Count == 0,
            ConexoesCaidas: caidas,
            // QUALQUER uma que trocou de chip. Perguntar so a primeira esconderia a troca nas
            // outras, e o aviso existe justamente para o dono conferir que o numero certo entrou.
            TrocouDeNumero: conexoes.Any(c => c.NumeroAnterior is not null),
            SemaforoAmareloMinutos: empresa?.SemaforoAmareloMinutos ?? 60,
            SemaforoVermelhoMinutos: empresa?.SemaforoVermelhoMinutos ?? 240,
            JanelaHoraInicio: empresa?.JanelaHoraInicio ?? padrao.HoraInicio,
            JanelaHoraFim: empresa?.JanelaHoraFim ?? padrao.HoraFim,
            JanelaDiasSemana: empresa?.JanelaDiasSemana ?? padrao.DiasSemana,
            FeriadosRecentes: feriados,
            Recuperacao: await RecuperacaoAsync(ct));
    }

    /// <summary>O aviso de mensagens recuperadas das ultimas 24h (REC-1).
    ///
    /// AGREGACAO NO SQL, nao em memoria: sao quatro numeros de um range scan sobre
    /// `ix_msg_recuperada` (parcial — so as recuperadas estao no indice). Trazer as linhas para
    /// contar no C# faria o poll de 45s carregar mensagens que ninguem vai ler.
    ///
    /// 24h fixas, sem flag de "dispensado": o aviso some quando a janela passa. Guardar a
    /// dispensa criaria estado que alguem precisa lembrar de limpar — a mesma escolha do
    /// checklist de primeiros passos.</summary>
    private async Task<AvisoRecuperacao?> RecuperacaoAsync(CancellationToken ct)
    {
        var desde = relogio.GetUtcNow().UtcDateTime.AddDays(-1);

        // O query filter de `mensagens` ja restringe ao tenant da requisicao.
        var r = await db.Mensagens.AsNoTracking()
            .Where(m => m.RecuperadaEm != null && m.RecuperadaEm >= desde)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Mensagens = g.Count(),
                Conversas = g.Select(m => m.ConversaId).Distinct().Count(),
                De = g.Min(m => m.RecebidaEm),
                Ate = g.Max(m => m.RecebidaEm)
            })
            .FirstOrDefaultAsync(ct);

        // `De`/`Ate` sao anulaveis no modelo (recebida_em so existe na ENTRADA). O carimbo so e
        // gravado em entrada, entao na pratica nunca sao nulos aqui — mas devolver um aviso com
        // periodo vazio seria pior que nao avisar.
        return r is null || r.Mensagens == 0 || r.De is null || r.Ate is null
            ? null
            : new AvisoRecuperacao(r.Mensagens, r.Conversas, r.De.Value, r.Ate.Value);
    }
}
