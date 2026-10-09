using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Entidades;
using Nexora.Core.Nps;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>===================== NPS-1 ETAPA 2c — A RODADA DA PESQUISA =====================
///
/// O relogio destes testes esta numa QUINTA as 10h local — dentro da janela padrao (8h-20h, seg a
/// sab). Quem precisa de janela fechada move o relogio, e esta dito no teste.
///
/// ⚠️ A RODADA CORRE SEM TENANT NO CONTEXTO, de proposito: e um job, nao ha requisicao, e e isso
/// que prova que o `empresa_id = $1` escrito a mao no `DadosNps` esta no lugar.
/// =====================================================================================</summary>
[Collection("banco")]
public class MotorNpsDbTests(BancoTeste banco)
{
    /// <summary>Quinta, 06/08/2026, 10h de Brasilia — dentro da janela.</summary>
    private static readonly DateTimeOffset QuintaDeManha = new(2026, 8, 6, 13, 0, 0, TimeSpan.Zero);

    /// <summary>Quinta, 23h de Brasilia — fora da janela.</summary>
    private static readonly DateTimeOffset QuintaDeNoite = new(2026, 8, 7, 2, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Hoje = new(2026, 8, 6);

    // ==================================================================== o agendamento

    /// <summary>===================== AGENDAR E PROCURAR, NAO RECEBER AVISO =====================
    ///
    /// ⚠️ NAO HA GANCHO NA CONCLUSAO, E NAO PODE HAVER: os dois caminhos que concluem venda
    /// (`ServicoVendas.ConcluirAsync` e `ConclusaoAutomatica`) sao baseados em CONJUNTO —
    /// `ExecuteUpdate` e SQL cru — e nao carregam entidade onde pendurar nada. A rodada PROCURA
    /// venda concluida sem pesquisa.
    ///
    /// E isso tem um ganho que o gancho nao teria: pesquisa que falhou de nascer numa rodada nasce
    /// na seguinte, de graca.
    /// =================================================================================</summary>
    [Fact]
    public async Task VENDA_CONCLUIDA_GANHA_PESQUISA_AGENDADA_PARA_TRES_DIAS_DEPOIS()
    {
        var (db, tx, amb) = await PrepararAsync("agenda");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-1));

        await amb.Motor.ExecutarAsync();

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).SingleAsync();

        Assert.Equal(StatusPesquisaNps.Agendada, p.Status);
        // Concluida ONTEM + 3 dias de padrao = depois de amanha.
        Assert.Equal(Hoje.AddDays(2), p.DataAgendada);
        Assert.Equal(Hoje.AddDays(2 + 7), p.DataLimite);
    }

    /// <summary>⚠️ PESQUISA DESLIGADA NAO AGENDA NADA, e a checagem vem ANTES do agendamento.
    /// Agendar com ela desligada acumularia fila silenciosa, e ligar o botao um mes depois
    /// dispararia um mes de perguntas de uma vez.</summary>
    [Fact]
    public async Task COM_A_PESQUISA_DESLIGADA_A_RODADA_NAO_AGENDA_NEM_ENVIA()
    {
        var (db, tx, amb) = await PrepararAsync("desligada");
        using var _ = db; using var __ = tx;

        // Sem `LigarNpsAsync`: o padrao e desligado.
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-1));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, r.Agendadas);
        Assert.Empty(await db.PesquisasNps.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    /// <summary>===================== DESLIGAR NAO DEIXA PESQUISA ABERTA =====================
    ///
    /// ⚠️ A CHECAGEM DE "DESLIGADA" PULAVA A RODADA INTEIRA, inclusive a expiracao. A pesquisa que
    /// saiu antes de o dono desligar ficava `enviada` para sempre, e a leitura no webhook seguia
    /// aceitando nota para ela semanas depois.
    ///
    /// Desligada: nao agenda, nao dispara — e o que ja saiu EXPIRA no prazo de sempre.
    /// ===============================================================================</summary>
    [Fact]
    public async Task COM_A_PESQUISA_DESLIGADA_O_QUE_JA_SAIU_AINDA_EXPIRA()
    {
        var (db, tx, amb) = await PrepararAsync("desligada-expira");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));
        await amb.Motor.ExecutarAsync();
        Assert.Single(amb.Cliente.TextosEnviados);

        // Desligada, e quatro dias depois do envio, com prazo de tres.
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.NpsAtivo, false));
        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.NegociacaoId == negociacao)
            .ExecuteUpdateAsync(u => u.SetProperty(
                x => x.DataEnvio, amb.Relogio.GetUtcNow().UtcDateTime.AddDays(-4)));
        // E uma venda NOVA, que nao pode ganhar pesquisa com a funcao desligada.
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-1), outroFunil: true);
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Expiradas);
        Assert.Equal(0, r.Agendadas);
        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Expirada, await db.PesquisasNps.IgnoreQueryFilters()
            .Where(x => x.NegociacaoId == negociacao).Select(x => x.Status).SingleAsync());
        Assert.Single(await db.PesquisasNps.IgnoreQueryFilters()
            .Where(x => x.EmpresaId == amb.Cenario.Id).ToListAsync());
        Assert.Single(amb.Cliente.TextosEnviados);
    }

    /// <summary>===================== O FUSO DE RESERVA NAO DERRUBA O AGENDAMENTO =====================
    ///
    /// ⚠️ O CASO DA REVISAO: o agendamento mandava `fuso.Id` ao Postgres no `AT TIME ZONE`. Quando o
    /// `Resolver` cai no fuso de reserva, o id e `br-fixo` — que o Postgres recusa. A rodada pegava
    /// a excecao por empresa e seguia: nenhuma pesquisa agendada, todo dia, com uma linha de log.
    ///
    /// Fuso EM BRANCO cai na mesma reserva que um servidor sem tzdata, e e o jeito de reproduzir o
    /// caminho aqui. Com o nome IANA indo ao banco, a pesquisa e agendada normalmente.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task COM_O_FUSO_DE_RESERVA_A_PESQUISA_AINDA_E_AGENDADA()
    {
        var (db, tx, amb) = await PrepararAsync("fuso-reserva");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == amb.Cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.FusoHorario, ""));
        db.ChangeTracker.Clear();

        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-1));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Agendadas);
    }

    /// <summary>===================== O QUE IMPEDE A ENXURRADA AO LIGAR =====================
    ///
    /// ⚠️ Uma empresa com vendas antigas liga a pesquisa e NAO recebe uma pergunta por venda do
    /// historico. Sem o corte, quinhentas vendas concluidas renderiam quinhentas mensagens de
    /// WhatsApp no dia do deploy — e perguntar sobre uma compra de tres meses atras e pior que
    /// calar.
    ///
    /// O corte e a propria `data_limite`: venda velha nasceria com o limite no passado, cancelada
    /// na mesma rodada. Entao nem nasce.
    /// ===========================================================================</summary>
    [Fact]
    public async Task VENDA_VELHA_NAO_GANHA_PESQUISA_AO_LIGAR_A_FUNCAO()
    {
        var (db, tx, amb) = await PrepararAsync("enxurrada");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);

        // No limite: concluida ha 10 dias (3 de prazo + 7 de adiamento) ainda entra.
        var naBorda = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-10));
        // Um dia alem: fora.
        var velha = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-11), outroFunil: true);

        await amb.Motor.ExecutarAsync();

        db.ChangeTracker.Clear();
        var agendadas = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Select(x => x.NegociacaoId).ToListAsync();

        Assert.Contains(naBorda, agendadas);
        Assert.DoesNotContain(velha, agendadas);
    }

    /// <summary>Rodar duas vezes nao cria duas pesquisas. ⚠️ E O `NOT EXISTS` QUE EVITA, e
    /// `uq_pesquisas_nps_negociacao` que GARANTE: o agendador nao tem lock distribuido, e duas
    /// instancias rodariam o mesmo `INSERT ... SELECT` ao mesmo tempo.</summary>
    [Fact]
    public async Task A_RODADA_E_IDEMPOTENTE_NO_AGENDAMENTO()
    {
        var (db, tx, amb) = await PrepararAsync("idempotente");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-1));

        var primeira = await amb.Motor.ExecutarAsync();
        var segunda = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, primeira.Agendadas);
        Assert.Equal(0, segunda.Agendadas);

        db.ChangeTracker.Clear();
        Assert.Single(await db.PesquisasNps.IgnoreQueryFilters().ToListAsync());
    }

    /// <summary>Contato anonimizado nao ganha pesquisa: a LGPD zerou o telefone, e nao ha para onde
    /// mandar.</summary>
    [Fact]
    public async Task CONTATO_ANONIMIZADO_NAO_GANHA_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("anon");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-1));

        await db.Contatos.IgnoreQueryFilters().Where(c => c.Id == amb.Contato.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.AnonimizadoEm, DateTime.UtcNow));
        db.ChangeTracker.Clear();

        await amb.Motor.ExecutarAsync();

        Assert.Empty(await db.PesquisasNps.IgnoreQueryFilters().ToListAsync());
    }

    // ==================================================================== os cancelamentos

    /// <summary>===================== VENDA DESFEITA NAO RECEBE PERGUNTA =====================
    ///
    /// ⚠️ E POR ISSO QUE CANCELAR VEM ANTES DE DISPARAR NA RODADA. Na ordem inversa, o cliente que
    /// desistiu da compra ontem recebe hoje "quanto voce nos recomendaria?".
    ///
    /// ⚠️ `devolvida` NAO EXISTE NESTE PROJETO. O prompt pedia "devolvida ou cancelada"; os status
    /// sao `Aberta`, `Ganha`, `Concluida`, `Perdida` e `Cancelada`. A regra ficou "nao esta mais
    /// concluida", que cobre todos sem inventar estado.
    /// ===========================================================================</summary>
    [Fact]
    public async Task VENDA_QUE_DEIXOU_DE_ESTAR_CONCLUIDA_CANCELA_A_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("desfeita");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje);

        await amb.Motor.ExecutarAsync();

        // Agora a venda e desfeita.
        await db.Negociacoes.IgnoreQueryFilters().Where(n => n.Id == negociacao)
            .ExecuteUpdateAsync(u => u
                .SetProperty(n => n.Status, StatusNegociacao.Cancelada)
                .SetProperty(n => n.CanceladaEm, DateTime.UtcNow));
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Canceladas);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Cancelada,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.NegociacaoId == negociacao).Select(x => x.Status).SingleAsync());

        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    /// <summary>O adiamento tem fim. Sete dias de janela fechada e a pesquisa desiste — perguntar
    /// sobre uma compra de duas semanas atras nao e mais pos-venda.</summary>
    [Fact]
    public async Task PESQUISA_ADIADA_ALEM_DO_LIMITE_E_CANCELADA()
    {
        var (db, tx, amb) = await PrepararAsync("estourada");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje);
        await amb.Motor.ExecutarAsync();

        // O limite passou.
        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.NegociacaoId == negociacao)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.DataAgendada, Hoje.AddDays(-1))
                .SetProperty(x => x.DataLimite, Hoje.AddDays(-1)));
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Canceladas);
        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    // ==================================================================== o disparo

    [Fact]
    public async Task A_PERGUNTA_SAI_COM_AS_VARIAVEIS_PREENCHIDAS()
    {
        var (db, tx, amb) = await PrepararAsync("envio");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Enviadas);

        var texto = Assert.Single(amb.Cliente.TextosEnviados).Texto;

        // O semeador chama o contato de "Cliente Teste" — a saudacao pega o primeiro nome.
        Assert.StartsWith("Oi, ", texto);
        Assert.Contains(amb.Cenario.Empresa.Nome, texto);
        Assert.DoesNotContain("{{", texto);

        db.ChangeTracker.Clear();
        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).SingleAsync();

        Assert.Equal(StatusPesquisaNps.Enviada, p.Status);
        Assert.NotNull(p.DataEnvio);
        Assert.NotNull(p.MensagemEnvioId);
    }

    /// <summary>⚠️ A MENSAGEM NASCE MARCADA `automatica`/`nps`, e os dois campos vem CRAVADOS no
    /// INSERT do `DadosNps` — nao da entidade. O comentario do `ReservarLembreteAsync` conta por
    /// que: aquele INSERT lista as colunas uma a uma, e marcar na entidade nao chega ao banco.
    ///
    /// E `tipo_automacao = 'nps'` e tambem o PREDICADO de `uq_msg_nps`: sem ele cravado, o indice
    /// parcial nao pega a linha e o dedupe deixa de existir.</summary>
    [Fact]
    public async Task A_MENSAGEM_DA_PESQUISA_NASCE_MARCADA_E_LIGADA_A_VENDA()
    {
        var (db, tx, amb) = await PrepararAsync("marca");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        await amb.Motor.ExecutarAsync();

        db.ChangeTracker.Clear();
        var m = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).SingleAsync();

        Assert.Equal(OrigemMensagem.Automatica, m.Origem);
        Assert.Equal(TipoAutomacao.Nps, m.TipoAutomacao);
        Assert.Equal(DirecaoMensagem.Saida, m.Direcao);
        Assert.NotNull(m.EnviadaEm);
    }

    /// <summary>===================== FORA DA JANELA, A DATA ANDA — SEM RESERVAR =====================
    ///
    /// ⚠️ E AQUI O NPS SE AFASTA DO LEMBRETE DE PROPOSITO. O lembrete faz reserve-defer: grava a
    /// mensagem sem postar, porque a `data_alvo` dele so vive naquela linha. A pesquisa tem
    /// `pesquisas_nps.data_agendada`, coluna propria e duravel — entao fora da janela ela so move a
    /// data, e NAO grava mensagem nenhuma.
    ///
    /// Reservar aqui seria pior: a linha ficaria pendente, a drenagem a postaria num momento que o
    /// motor nao escolheu, e o `data_envio` — de onde sai o relogio da expiracao — nao teria como
    /// acompanhar.
    /// ====================================================================================</summary>
    [Fact]
    public async Task FORA_DA_JANELA_A_PESQUISA_ADIA_SEM_GRAVAR_MENSAGEM()
    {
        var (db, tx, amb) = await PrepararAsync("janela", QuintaDeNoite);
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        // Concluida em 03/08 + 3 dias = agendada para 06/08, que e HOJE neste teste. Com 04/08 a
        // pesquisa cairia para 07/08 e nao entraria no disparo — foi o que me custou uma rodada.
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: new DateOnly(2026, 8, 3));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Adiadas);
        Assert.Equal(0, r.Enviadas);
        Assert.Empty(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();

        // NENHUMA mensagem gravada — nem pendente.
        Assert.Empty(await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.NegociacaoId == negociacao).ToListAsync());

        var p = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).SingleAsync();

        Assert.Equal(StatusPesquisaNps.Agendada, p.Status);
        // A quinta as 23h: o proximo dia permitido e a sexta.
        Assert.Equal(new DateOnly(2026, 8, 7), p.DataAgendada);
        Assert.Null(p.DataEnvio);
    }

    /// <summary>===================== CONVERSA VIVA NAO LEVA ROBO =====================
    ///
    /// Mensagem HUMANA nas ultimas 24h quer dizer que alguem esta falando com o cliente agora.
    /// Entrar com a pesquisa no meio e grosseria — e pode apagar o semaforo de outro card.
    /// ======================================================================</summary>
    [Fact]
    public async Task CONVERSA_HUMANA_NAS_ULTIMAS_24H_ADIA_A_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("conversa-viva");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        await MensagemAsync(db, amb, OrigemMensagem.Humana, DirecaoMensagem.Entrada,
            quando: amb.Relogio.GetUtcNow().UtcDateTime.AddHours(-2));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Adiadas);
        Assert.Empty(amb.Cliente.TextosEnviados);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Agendada,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.NegociacaoId == negociacao).Select(x => x.Status).SingleAsync());
    }

    /// <summary>E conversa de ANTEONTEM nao impede: 24h e a janela, nao "qualquer histórico".</summary>
    [Fact]
    public async Task CONVERSA_HUMANA_ANTIGA_NAO_IMPEDE_A_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("conversa-velha");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        await MensagemAsync(db, amb, OrigemMensagem.Humana, DirecaoMensagem.Entrada,
            quando: amb.Relogio.GetUtcNow().UtcDateTime.AddDays(-2));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Enviadas);
        Assert.Single(amb.Cliente.TextosEnviados);
    }

    /// <summary>===================== O TETO DIARIO DE VERDADE =====================
    ///
    /// ⚠️ O PROMPT FALAVA DE UM "TETO DIARIO DA EMPRESA" E ELE NAO EXISTE. O que existe e
    /// `uq_lembrete_teto_diario`: UM lembrete automatico com mensagem por CONTATO por DIA. A
    /// pesquisa respeita a mesma regra, senao o cliente que recebeu follow-up de manha recebe a
    /// pesquisa de tarde — duas automaticas no mesmo dia, pelo mesmo numero, que e o jeito
    /// classico de ser bloqueado.
    /// ==================================================================</summary>
    [Fact]
    public async Task QUEM_JA_RECEBEU_AUTOMATICA_HOJE_NAO_RECEBE_A_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("teto");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        // Um follow-up saiu hoje para este contato.
        await MensagemAsync(db, amb, OrigemMensagem.Automatica, DirecaoMensagem.Saida,
            quando: amb.Relogio.GetUtcNow().UtcDateTime, dataDisparo: Hoje);

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Adiadas);
        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    // ==================================================================== a expiracao

    /// <summary>===================== EXPIRA SEM REENVIO =====================
    ///
    /// ⚠️ Quem nao respondeu em tres dias nao responde ao quarto lembrete, e insistir num numero de
    /// WhatsApp e o jeito classico de ser bloqueado. A pesquisa para de esperar e nada mais sai.
    ///
    /// ⚠️ E O PRAZO CONTA DE `data_envio`, nao de `data_agendada`: uma pesquisa adiada tres dias
    /// por janela fechada teria expirado antes de o cliente ler a pergunta.
    /// ==============================================================</summary>
    [Fact]
    public async Task PESQUISA_SEM_RESPOSTA_EXPIRA_E_NAO_REENVIA()
    {
        var (db, tx, amb) = await PrepararAsync("expira");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        await amb.Motor.ExecutarAsync();
        Assert.Single(amb.Cliente.TextosEnviados);

        // Quatro dias depois do envio, com prazo de tres.
        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.NegociacaoId == negociacao)
            .ExecuteUpdateAsync(u => u.SetProperty(
                x => x.DataEnvio, amb.Relogio.GetUtcNow().UtcDateTime.AddDays(-4)));
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Expiradas);

        db.ChangeTracker.Clear();
        Assert.Equal(StatusPesquisaNps.Expirada,
            await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.NegociacaoId == negociacao).Select(x => x.Status).SingleAsync());

        // E NADA foi reenviado.
        Assert.Single(amb.Cliente.TextosEnviados);
    }

    /// <summary>Dentro do prazo, nao expira. Borda do mesmo numero do teste acima.</summary>
    [Fact]
    public async Task PESQUISA_DENTRO_DO_PRAZO_NAO_EXPIRA()
    {
        var (db, tx, amb) = await PrepararAsync("no-prazo");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));
        await amb.Motor.ExecutarAsync();

        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.NegociacaoId == negociacao)
            .ExecuteUpdateAsync(u => u.SetProperty(
                x => x.DataEnvio, amb.Relogio.GetUtcNow().UtcDateTime.AddDays(-2)));
        db.ChangeTracker.Clear();

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, r.Expiradas);
    }

    // ==================================================================== o isolamento

    /// <summary>===================== A RODADA NAO VAZA ENTRE EMPRESAS =====================
    ///
    /// ⚠️ ELA CORRE SEM TENANT NO CONTEXTO — e um job —, entao o filtro global do EF nao tem o que
    /// filtrar. Quem recorta e o `empresa_id = $1` escrito A MAO em cada comando do `DadosNps`, e e
    /// isso que este teste guarda: a vizinha com a pesquisa DESLIGADA nao recebe nada, ainda que a
    /// venda dela esteja concluida e no prazo.
    /// ==========================================================================</summary>
    [Fact]
    public async Task A_RODADA_DE_UMA_EMPRESA_NAO_AGENDA_PELA_VIZINHA()
    {
        var (db, tx, amb) = await PrepararAsync("tenant");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var minha = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-1));

        // A vizinha tem venda concluida e a pesquisa DESLIGADA.
        var vizinha = await Semeador.TenantAsync(db, "nps-motor-vizinha");
        var dela = await VendaConcluidaAsync(
            db, amb with { Cenario = vizinha, Contato = vizinha.Contato }, concluidaEm: Hoje.AddDays(-1));

        await amb.Motor.ExecutarAsync();

        db.ChangeTracker.Clear();
        var agendadas = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Select(x => x.NegociacaoId).ToListAsync();

        Assert.Contains(minha, agendadas);
        Assert.DoesNotContain(dela, agendadas);
    }

    // ==================================================================== uma conversa por número (CONV-XX)

    /// <summary>Contato com dois números: a pesquisa sai UMA vez, pela conversa principal. Sem a
    /// regra, a consulta devolvia a pesquisa uma vez por conversa.</summary>
    [Fact]
    public async Task CONV_COM_DOIS_NUMEROS_A_PESQUISA_SAI_UMA_VEZ_PELA_PRINCIPAL()
    {
        var (db, tx, amb) = await PrepararAsync("conv-nps-uma");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var agora = amb.Relogio.GetUtcNow().UtcDateTime;
        await db.Conversas.IgnoreQueryFilters().Where(c => c.Id == amb.Conversa.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.UltimaMensagemEm, agora.AddDays(-5)));
        var (conexaoB, _) = await Semeador.SegundoNumeroAsync(db, amb.Cenario, agora.AddDays(-2));
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Enviadas);
        Assert.Equal(conexaoB.InstanceName, Assert.Single(amb.Cliente.TextosEnviados).Instancia);
    }

    /// <summary>O teto diário é do CONTATO: a automática que saiu hoje pelo OUTRO número segura a
    /// pesquisa, que tenta de novo amanhã.</summary>
    [Fact]
    public async Task CONV_A_AUTOMATICA_DE_HOJE_PELO_OUTRO_NUMERO_ADIA_A_PESQUISA()
    {
        var (db, tx, amb) = await PrepararAsync("conv-nps-teto");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var agora = amb.Relogio.GetUtcNow().UtcDateTime;
        // A do cenário é a principal (relógio de verdade); a do B é a secundária.
        var (conexaoB, conversaB) = await Semeador.SegundoNumeroAsync(db, amb.Cenario, agora.AddDays(-10));
        db.Mensagens.Add(new Mensagem
        {
            EmpresaId = amb.Cenario.Id,
            ConversaId = conversaB.Id,
            ContatoId = amb.Contato.Id,
            ConexaoId = conexaoB.Id,
            InstanceName = conexaoB.InstanceName,
            Direcao = DirecaoMensagem.Saida,
            Texto = "Passando para saber se você ainda tem interesse.",
            Origem = OrigemMensagem.Automatica,
            DataDisparo = Hoje
        });
        await db.SaveChangesAsync();
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.ConversaId == conversaB.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.CriadoEm, agora.AddHours(-30)));
        db.ChangeTracker.Clear();
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(0, r.Enviadas);
        Assert.Equal(1, r.Adiadas);
        Assert.Empty(amb.Cliente.TextosEnviados);
    }

    // ==================================================================== o andaime

    private sealed record Ambiente(
        Cenario Cenario, Contato Contato, Conversa Conversa, ContextoMutavel Contexto,
        ClienteWhatsAppFalso Cliente, MotorNps Motor, RelogioFalso Relogio);

    // ==================================================================== a dúvida que ninguém decide

    /// <summary>Uma pesquisa ENVIADA e depois posta em duvida, com as horas dadas — relativas a
    /// "agora" do relogio do teste.</summary>
    private static async Task<long> DuvidaAsync(
        NexoraDbContext db, Ambiente amb, TimeSpan envioHa, TimeSpan? respostaHa)
    {
        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));
        await amb.Motor.ExecutarAsync();

        var agora = amb.Relogio.GetUtcNow().UtcDateTime;
        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.NegociacaoId == negociacao)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, StatusPesquisaNps.PossivelNota)
                .SetProperty(x => x.Nota, (short?)2)
                .SetProperty(x => x.DataEnvio, (DateTime?)(agora - envioHa))
                .SetProperty(x => x.DataResposta, respostaHa == null ? null : agora - respostaHa.Value));
        db.ChangeTracker.Clear();

        return negociacao;
    }

    private static Task<StatusPesquisaNps> StatusAsync(NexoraDbContext db, long negociacao)
    {
        db.ChangeTracker.Clear();
        return db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).Select(x => x.Status).SingleAsync();
    }

    /// <summary>===================== A DUVIDA NAO DECIDIDA EXPIRA =====================
    ///
    /// ⚠️ ELA FICAVA ABERTA PARA SEMPRE, e a leitura continuava tratando a pesquisa como viva:
    /// semanas depois, "2, por favor" sobre OUTRO pedido virava a nota dela — aviso de detrator ao
    /// dono, mensagem ao cliente, e um 2 no relatorio de uma pesquisa de meses antes.
    ///
    /// Prazo de tres dias contado da RESPOSTA: o vendedor tem o mesmo tempo para decidir que o
    /// cliente teve para responder.
    /// ==============================================================</summary>
    [Fact]
    public async Task A_DUVIDA_NAO_DECIDIDA_EXPIRA_NO_PRAZO_CONTADO_DA_RESPOSTA()
    {
        var (db, tx, amb) = await PrepararAsync("duvida-expira");
        using var _ = db; using var __ = tx;

        var negociacao = await DuvidaAsync(db, amb, TimeSpan.FromDays(10), TimeSpan.FromDays(4));

        var r = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, r.Expiradas);
        Assert.Equal(StatusPesquisaNps.Expirada, await StatusAsync(db, negociacao));
    }

    /// <summary>⚠️ A BORDA QUE PROVA DE ONDE O PRAZO CONTA: envio de dez dias atras, mas a duvida
    /// chegou ontem. Contando do ENVIO ela expiraria na hora em que nasceu, e o vendedor nunca
    /// chegaria a ver a pergunta.</summary>
    [Fact]
    public async Task A_DUVIDA_RECENTE_NAO_EXPIRA_MESMO_COM_O_ENVIO_ANTIGO()
    {
        var (db, tx, amb) = await PrepararAsync("duvida-recente");
        using var _ = db; using var __ = tx;

        var negociacao = await DuvidaAsync(db, amb, TimeSpan.FromDays(10), TimeSpan.FromDays(1));

        await amb.Motor.ExecutarAsync();

        Assert.Equal(StatusPesquisaNps.PossivelNota, await StatusAsync(db, negociacao));
    }

    /// <summary>A duvida gravada ANTES de a leitura passar a carimbar a hora da resposta nao tem
    /// essa hora. Ela conta do envio — senao seria justamente ela a ficar aberta para sempre.</summary>
    [Fact]
    public async Task A_DUVIDA_ANTIGA_SEM_HORA_DA_RESPOSTA_CONTA_DO_ENVIO()
    {
        var (db, tx, amb) = await PrepararAsync("duvida-legado");
        using var _ = db; using var __ = tx;

        var negociacao = await DuvidaAsync(db, amb, TimeSpan.FromDays(4), respostaHa: null);

        await amb.Motor.ExecutarAsync();

        Assert.Equal(StatusPesquisaNps.Expirada, await StatusAsync(db, negociacao));
    }

    // ==================================================================== a pergunta que falhou

    /// <summary>===================== A PERGUNTA QUE FALHOU SAI NA RODADA SEGUINTE =====================
    ///
    /// ⚠️ O DEFEITO QUE ISTO TRAVA: a reserva barrada por `uq_msg_nps` era tratada como "ja saiu".
    /// A Evolution caiu no envio, a linha ficou com o erro, e NADA a reenviava — a drenagem do
    /// follow-up so pega linha com `lembrete_id`. No dia seguinte a rodada batia no indice, marcava
    /// a pesquisa `enviada` sem a pergunta ter saido, e ela expirava como "nao respondeu".
    ///
    /// Agora: a primeira rodada falha e a pesquisa FICA agendada; a segunda posta A MESMA LINHA —
    /// uma linha so por venda, como o indice exige — e so entao a pesquisa vira `enviada`, ligada
    /// a ela.
    /// ==========================================================================================</summary>
    [Fact]
    public async Task A_PERGUNTA_QUE_FALHOU_SAI_NA_RODADA_SEGUINTE_PELA_MESMA_LINHA()
    {
        var (db, tx, amb) = await PrepararAsync("falhou-reenvia");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        var negociacao = await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));

        amb.Cliente.ErroParaLancar = new HttpRequestException("Evolution fora do ar");
        var primeira = await amb.Motor.ExecutarAsync();

        Assert.Equal(1, primeira.Falhas);
        db.ChangeTracker.Clear();
        var depoisDaFalha = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).SingleAsync();
        Assert.Equal(StatusPesquisaNps.Agendada, depoisDaFalha.Status);
        Assert.Null(depoisDaFalha.DataEnvio);

        // No dia seguinte a Evolution voltou.
        amb.Cliente.ErroParaLancar = null;
        amb.Relogio.Avancar(TimeSpan.FromDays(1));
        await amb.Motor.ExecutarAsync();

        db.ChangeTracker.Clear();
        var linha = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.NegociacaoId == negociacao).SingleAsync();
        var pesquisa = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).SingleAsync();

        Assert.NotNull(linha.EnviadaEm);
        Assert.Equal(StatusPesquisaNps.Enviada, pesquisa.Status);
        Assert.Equal(linha.Id, pesquisa.MensagemEnvioId);
        // Duas TENTATIVAS de POST, e uma linha so.
        Assert.Equal(2, amb.Cliente.TextosEnviados.Count);
    }

    /// <summary>A outra metade: a linha que JA SAIU. Simula a rodada que postou e caiu antes de marcar
    /// a pesquisa. A seguinte nao manda de novo, e marca com o id e a HORA REAIS da linha — o
    /// relogio da expiracao conta de quando a pergunta chegou, e a hora da segunda rodada daria um
    /// dia a mais de prazo.</summary>
    [Fact]
    public async Task A_PERGUNTA_QUE_JA_SAIU_MARCA_A_PESQUISA_COM_O_ID_E_A_HORA_DELA_SEM_REENVIAR()
    {
        var (db, tx, amb) = await PrepararAsync("ja-saiu");
        using var _ = db; using var __ = tx;

        await LigarNpsAsync(db, amb.Cenario.Id);
        // ⚠️ DUAS VENDAS, e a que volta atras e a SEGUNDA: a linha dela tem o id maior. Com uma
        // venda so, achar "a pergunta desta venda" sem filtrar pela venda daria a linha certa por
        // acaso — e o teste passaria com a busca errada.
        //
        // Do mesmo contato, entao uma por dia: a regra "uma automatica por dia por conversa" adia a
        // segunda para o dia seguinte.
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3));
        await VendaConcluidaAsync(db, amb, concluidaEm: Hoje.AddDays(-3), outroFunil: true);

        await amb.Motor.ExecutarAsync();
        amb.Relogio.Avancar(TimeSpan.FromDays(1));
        await amb.Motor.ExecutarAsync();
        Assert.Equal(2, amb.Cliente.TextosEnviados.Count);

        // A venda da linha MAIS NOVA, qualquer que tenha saido primeiro: a ordem do despacho nao e
        // contrato, e a primeira versao deste teste supunha uma — a sabotagem da busca passava.
        db.ChangeTracker.Clear();
        var negociacao = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.EmpresaId == amb.Cenario.Id && m.TipoAutomacao == TipoAutomacao.Nps)
            .OrderByDescending(m => m.Id)
            .Select(m => m.NegociacaoId!.Value)
            .FirstAsync();

        // A rodada "caiu" depois do POST e antes de marcar: a pesquisa volta a agendada.
        await db.PesquisasNps.IgnoreQueryFilters().Where(x => x.NegociacaoId == negociacao)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, StatusPesquisaNps.Agendada)
                .SetProperty(x => x.DataEnvio, (DateTime?)null)
                .SetProperty(x => x.MensagemEnvioId, (long?)null));
        db.ChangeTracker.Clear();

        var linha = await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.NegociacaoId == negociacao).SingleAsync();

        amb.Relogio.Avancar(TimeSpan.FromDays(1));
        await amb.Motor.ExecutarAsync();

        db.ChangeTracker.Clear();
        var pesquisa = await db.PesquisasNps.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.NegociacaoId == negociacao).SingleAsync();

        Assert.Equal(StatusPesquisaNps.Enviada, pesquisa.Status);
        Assert.Equal(linha.Id, pesquisa.MensagemEnvioId);
        Assert.Equal(linha.EnviadaEm, pesquisa.DataEnvio);
        // Nada saiu de novo: continuam as duas da primeira rodada.
        Assert.Equal(2, amb.Cliente.TextosEnviados.Count);
    }

    private async Task<(NexoraDbContext Db, IDbContextTransaction Tx, Ambiente Amb)> PrepararAsync(
        string sufixo, DateTimeOffset? quando = null)
    {
        var ctx = new ContextoMutavel();
        var db = banco.NovoContexto(ctx);
        var tx = await db.Database.BeginTransactionAsync();

        var cenario = await Semeador.TenantAsync(db, sufixo);
        await DesativarOutrasAsync(db, cenario.Id);

        // ===================== A CONVERSA SEMEADA TEM DE SER VELHA =====================
        // ⚠️ O `Semeador` cria uma mensagem HUMANA na conversa com `criado_em = now()`, e a regra
        // "conversa viva nao leva robo" a pega — corretamente. Sem envelhecer, TODO teste de
        // disparo daria `Adiada`, e foi assim que seis deles falharam de uma vez.
        //
        // Nao e contorno de teste: e o cenario normal. Pesquisa pos-venda sai dias depois da
        // conclusao, quando a conversa ja esfriou.
        // =============================================================================
        await db.Mensagens.IgnoreQueryFilters().Where(m => m.EmpresaId == cenario.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(
                m => m.CriadoEm, new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc)));
        db.ChangeTracker.Clear();

        var relogio = new RelogioFalso(quando ?? QuintaDeManha);
        var cliente = new ClienteWhatsAppFalso();

        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, relogio), cliente,
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            relogio, NullLogger<EnviadorMensagem>.Instance);

        var motor = new MotorNps(
            new DadosNps(db, relogio), new DadosFollowUp(db, relogio), enviador, relogio,
            NullLogger<MotorNps>.Instance);

        // ⚠️ O CONTEXTO FICA VAZIO de proposito: a rodada e um job, e e isso que prova que o
        // recorte por empresa escrito a mao esta no lugar.
        return (db, tx, new Ambiente(
            cenario, cenario.Contato, cenario.Conversa, ctx, cliente, motor, relogio));
    }

    /// <summary>As outras empresas do banco de teste saem da rodada: `EmpresasAtivasAsync` varre
    /// todas, e uma vizinha de outro teste poluiria as contagens.</summary>
    private static async Task DesativarOutrasAsync(NexoraDbContext db, long minha)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id != minha)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Ativo, false));
        db.ChangeTracker.Clear();
    }

    private static async Task LigarNpsAsync(NexoraDbContext db, long empresaId)
    {
        await db.Empresas.IgnoreQueryFilters().Where(e => e.Id == empresaId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.NpsAtivo, true));
        db.ChangeTracker.Clear();
    }

    /// <summary>Uma venda CONCLUIDA no dia informado. `outroFunil` para um segundo negocio do mesmo
    /// contato — `uq_negociacoes_card_por_funil` nao conta `concluida`, mas o segundo precisa de
    /// funil proprio para o cenario ficar realista.</summary>
    private static async Task<long> VendaConcluidaAsync(
        NexoraDbContext db, Ambiente amb, DateOnly concluidaEm, bool outroFunil = false)
    {
        var etapa = amb.Cenario.Etapas[0];

        var negocio = new Negociacao
        {
            EmpresaId = amb.Cenario.Id,
            ContatoId = amb.Contato.Id,
            PipelineId = etapa.PipelineId,
            EtapaId = etapa.Id,
            Status = StatusNegociacao.Concluida,
            Valor = 1000m,
            GanhaEm = concluidaEm.ToDateTime(new TimeOnly(12, 0)).ToUniversalTime(),
            ConcluidaEm = concluidaEm.ToDateTime(new TimeOnly(15, 0)).ToUniversalTime()
        };

        db.Negociacoes.Add(negocio);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return negocio.Id;
    }

    private static async Task MensagemAsync(
        NexoraDbContext db, Ambiente amb, OrigemMensagem origem, DirecaoMensagem direcao,
        DateTime quando, DateOnly? dataDisparo = null)
    {
        db.Mensagens.Add(new Mensagem
        {
            EmpresaId = amb.Cenario.Id,
            ConversaId = amb.Conversa.Id,
            ContatoId = amb.Contato.Id,
            ConexaoId = amb.Cenario.Conexao.Id,
            InstanceName = amb.Cenario.Conexao.InstanceName,
            Direcao = direcao,
            Texto = "oi",
            Origem = origem,
            // `ck_msg_data_disparo` exige a data em toda SAIDA.
            DataDisparo = direcao == DirecaoMensagem.Saida
                ? dataDisparo ?? DateOnly.FromDateTime(quando)
                : dataDisparo
        });

        await db.SaveChangesAsync();

        // `criado_em` tem default no banco; o teste precisa dele no passado.
        await db.Mensagens.IgnoreQueryFilters()
            .Where(m => m.ConversaId == amb.Conversa.Id && m.Texto == "oi")
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.CriadoEm, quando));

        db.ChangeTracker.Clear();
    }
}
