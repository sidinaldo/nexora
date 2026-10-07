namespace Nexora.Core.Nps;

/// <summary>===================== O QUE UM HUMANO DECIDE SOBRE A PESQUISA =====================
///
/// Tres gestos, todos a partir da conversa:
///
///   CONFIRMAR   "isto e uma nota X"  — a `PossivelNota` vira resposta, e as acoes da faixa correm
///   NAO E NOTA  "era outra coisa"    — volta a esperar, e a suspeita e apagada
///   CANCELAR    "deixa para la"      — a pesquisa para, sem nota
///
/// ⚠️ SEM GESTO NOVO DE PERMISSAO. Quem ve a conversa decide sobre a pesquisa dela: o aviso "isto e
/// uma nota?" aparece no meio da thread, e quem esta ali atendendo e quem sabe responder. Pedir um
/// gesto proprio faria o vendedor ver a pergunta e nao poder responde-la.
///
/// ⚠️ OS TRES SAO AUDITADOS. "Quem disse que aquele 2 era uma nota" e pergunta que aparece quando a
/// metrica do mes nao fecha — e a confirmacao manual e o unico ponto em que uma pessoa muda um
/// numero de relatorio com um clique.
/// ====================================================================================</summary>
public interface IServicoPesquisaNps
{
    /// <summary>A `PossivelNota` vira `Respondida`, com o usuario registrado em
    /// `ConfirmadaPorUsuarioId` — e e essa coluna que permite medir depois se o `LeitorDeNota` esta
    /// apertado demais: muita confirmacao manual quer dizer regras estreitas.
    ///
    /// ⚠️ AS ACOES DA FAIXA CORREM AQUI TAMBEM. Confirmar nota 2 na mao tem de criar o lembrete do
    /// detrator igual a nota 2 lida sozinha — senao o aviso dependeria de o leitor ter acertado.</summary>
    Task ConfirmarNotaAsync(long pesquisaId, CancellationToken ct);

    /// <summary>"Nao era nota": volta para `Enviada` e APAGA a suspeita. A pesquisa segue esperando
    /// a nota de verdade, e expira no prazo normal se ela nao vier.
    ///
    /// ⚠️ A SUSPEITA TEM DE SAIR, e nao so o status: uma `nota` pendurada numa pesquisa `enviada`
    /// seria lida como resultado pelo proximo que olhasse a linha — e o check do banco nao barra,
    /// porque ele so exige nota em `respondida`.</summary>
    Task NaoEhNotaAsync(long pesquisaId, CancellationToken ct);

    /// <summary>Para a pesquisa sem nota. Para o caso de o cliente pedir para nao ser incomodado, ou
    /// de a venda ter sido um erro de registro que ninguem cancelou.</summary>
    Task CancelarAsync(long pesquisaId, CancellationToken ct);
}
