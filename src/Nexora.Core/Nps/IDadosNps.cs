using Nexora.Core.Entidades;

namespace Nexora.Core.Nps;

/// <summary>Uma pesquisa pronta para sair, com tudo que o disparo precisa numa leitura so.</summary>
public record PesquisaADisparar(
    long PesquisaId,
    long NegociacaoId,
    long ContatoId,
    long ConversaId,
    long ConexaoId,
    string InstanceName,
    string Telefone,
    string NomeDoContato);

/// <summary>O que o `MotorNps` precisa do banco. Interface no Core, implementacao no Infra — o
/// mesmo arranjo de `IDadosFollowUp`, e pela mesma razao: o motor e regra, e regra nao conhece
/// Postgres.</summary>
public interface IDadosNps
{
    /// <summary>===================== AGENDAR POR RECONCILIACAO =====================
    ///
    /// Insere pesquisa `Agendada` para toda negociacao CONCLUIDA desta empresa que ainda nao tem
    /// uma. Devolve quantas nasceram.
    ///
    /// ⚠️ NAO E UM GANCHO NA CONCLUSAO, E NAO PODE SER. Os dois caminhos que concluem venda
    /// (`ServicoVendas.ConcluirAsync` e `ConclusaoAutomatica`) sao baseados em CONJUNTO —
    /// `ExecuteUpdate` e SQL cru — e nao carregam entidade onde pendurar nada. Procurar o que
    /// falta e o unico jeito que funciona para os dois.
    ///
    /// ⚠️ E ELE TAMBEM CONSERTA O QUE SE PERDEU: pesquisa que falhou de nascer numa rodada nasce
    /// na seguinte, de graca. Um gancho teria perdido aquela venda para sempre.
    ///
    /// ⚠️ O QUE IMPEDE A ENXURRADA AO LIGAR A PESQUISA: nao se agenda nada cuja `data_limite` ja
    /// tenha passado. Uma empresa com quinhentas vendas antigas liga o NPS e recebe pesquisa so
    /// das concluidas nos ultimos `dias_apos_conclusao + 7` dias — as outras nasceriam ja
    /// canceladas, e mandar pergunta sobre compra de tres meses atras seria pior que nao mandar.
    /// ====================================================================</summary>
    Task<int> AgendarPendentesAsync(Empresa empresa, DateOnly hoje, CancellationToken ct);

    /// <summary>Cancela as `Agendada` cuja venda deixou de estar concluida — cancelada no meio do
    /// caminho. Devolve quantas.
    ///
    /// ⚠️ `devolvida` NAO EXISTE NESTE PROJETO. O prompt pedia "devolvida ou cancelada"; os status
    /// sao `Aberta`, `Ganha`, `Concluida`, `Perdida` e `Cancelada`. A regra ficou "nao esta mais
    /// concluida", que cobre todos eles sem inventar estado.</summary>
    Task<int> CancelarDeVendaDesfeitaAsync(long empresaId, CancellationToken ct);

    /// <summary>Cancela as `Agendada` que passaram da `data_limite` — o adiamento estourou os sete
    /// dias. Devolve quantas.</summary>
    Task<int> CancelarAdiadasDemaisAsync(long empresaId, DateOnly hoje, CancellationToken ct);

    /// <summary>As `Agendada` com `data_agendada &lt;= hoje`, prontas para sair.</summary>
    Task<IReadOnlyList<PesquisaADisparar>> ADispararAsync(
        long empresaId, DateOnly hoje, CancellationToken ct);

    /// <summary>===================== O CONTATO PODE RECEBER HOJE? =====================
    ///
    /// Duas perguntas numa, porque as duas sao sobre o MESMO contato e o mesmo dia:
    ///
    ///   · houve mensagem HUMANA com ele nas ultimas 24h? Entao ha conversa viva, e entrar com
    ///     um robo no meio dela e grosseria — alem de poder apagar o semaforo de outro card;
    ///   · ele ja recebeu alguma AUTOMATICA hoje? ⚠️ ESTE E O "TETO DIARIO" DE VERDADE. O prompt
    ///     falava de um teto da EMPRESA e ele nao existe: `uq_lembrete_teto_diario` e por CONTATO
    ///     por dia. A pesquisa respeita a mesma regra, senao o cliente que recebeu follow-up de
    ///     manha recebe a pesquisa de tarde.
    /// ======================================================================</summary>
    /// <summary>⚠️ POR `conversaId`, e nao por contato: `mensagens` nao tem indice nenhum por
    /// contato. O contato tem UMA conversa, entao as duas perguntas sao a mesma — e esta entra por
    /// indice. Medido: `ix_msg_serie` na de 24h e `ix_msg_timeline` na do dia.</summary>
    Task<bool> PodeReceberHojeAsync(
        long empresaId, long conversaId, DateOnly hoje, CancellationToken ct);

    /// <summary>Marca a pesquisa como enviada, com o instante e a mensagem que fez a pergunta.</summary>
    /// <summary>⚠️ `mensagemId` ANULAVEL: o caminho `Barrada` nao sabe o id, porque a mensagem foi
    /// posta por OUTRA rodada. A pesquisa precisa ficar `enviada` de qualquer jeito.</summary>
    Task MarcarEnviadaAsync(
        long pesquisaId, long? mensagemId, DateTime quando, CancellationToken ct);

    /// <summary>Empurra a `data_agendada` para o dia informado. Usado quando a janela esta fechada,
    /// a conexao caiu, ou o contato nao pode receber hoje.</summary>
    Task AdiarAsync(long pesquisaId, DateOnly novaData, CancellationToken ct);

    /// <summary>Marca como `Expirada` toda `Enviada` sem nota alem do prazo. Devolve quantas.
    ///
    /// ⚠️ SEM REENVIO. Quem nao respondeu em tres dias nao responde ao quarto lembrete, e insistir
    /// num numero de WhatsApp e o jeito classico de ser bloqueado.</summary>
    Task<int> ExpirarAsync(long empresaId, DateTime limite, CancellationToken ct);
}
