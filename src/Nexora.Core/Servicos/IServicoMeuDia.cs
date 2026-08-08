namespace Nexora.Core.Servicos;

public enum TipoAcao
{
    /// <summary>Cliente esperando resposta (vem de conversas.aguardando_desde).</summary>
    Responder,
    /// <summary>Follow-up ou tarefa marcada (vem de lembretes).</summary>
    Lembrete
}

/// <summary>Um item do plano do dia.
///
/// `AguardandoDesde` e `MinutosUteis` vão juntos de propósito: o timestamp para o cliente pintar
/// a cor (que envelhece sozinha) e os minutos ÚTEIS já descontados do que está fora do
/// expediente — esse desconto depende da janela e dos feriados da empresa, e o navegador não
/// tem os feriados.</summary>
public record AcaoDoDia(
    /// <summary>"responder" ou "lembrete", em MINÚSCULAS.
    ///
    /// String, e não o enum `TipoAcao`, para seguir o padrão de todo DTO do sistema — Papel,
    /// Status, Direcao, Origem e TipoMidia saem todos por `.ToString().ToLower()` no serviço.
    ///
    /// Este era o ÚNICO que saía como enum cru, e o `JsonStringEnumConverter` o serializava em
    /// PascalCase ("Responder"). O cliente compara com "responder": a comparação nunca casava, e
    /// o resultado era silencioso — semáforo nunca colorido, e toda ação renderizada como se
    /// fosse lembrete, com botão "Concluir" apontando para o endpoint errado.</summary>
    string Tipo,
    long Id,
    long ContatoId,
    string ContatoNome,
    string Telefone,
    string Titulo,
    long? ConversaId,
    DateTime? AguardandoDesde,

    /// <summary>Minutos ÚTEIS de espera — NULO quando `EsperaAcimaDaJanela` é true.</summary>
    int? MinutosUteis,

    /// <summary>=========== A ESPERA SAIU DA JANELA MEDÍVEL ===========
    /// O desconto de tempo útil depende dos feriados, e o serviço carrega só os dos últimos
    /// `DiasDeFeriadoCarregados` dias — carregar o histórico inteiro a cada abertura da tela
    /// seria pagar caro por um caso raro. `TempoUtil` também trava em 400 iterações de dia.
    ///
    /// Acima disso o número seria calculado com feriados FALTANDO: sairia maior que o real,
    /// com cara de exato. Um número errado que parece certo é pior que a ausência dele — quem
    /// lê "12.480 minutos úteis" acredita.
    ///
    /// Então, acima da janela, `MinutosUteis` vem NULO e esta bandeira sobe. A tela mostra
    /// "mais de 30 dias", que é verdade, em vez de um número que não é.
    /// ======================================================</summary>
    bool EsperaAcimaDaJanela,

    TimeOnly? HoraAlvo,
    DateOnly? DataAlvo,
    bool Atrasado);

/// <summary>O teto de itens por chamada do Meu Dia.
///
/// Vive no CORE e não no serviço porque o controller precisa dele para o valor padrão — e o
/// controller não conhece a Infra. 200 é o que a tela pede: acima disso a lista deixa de ser um
/// plano do dia e vira uma tabela que ninguém percorre.</summary>
public static class LimiteMeuDia
{
    public const int Maximo = 200;
}

/// <summary>O plano do dia.
///
/// ⚠️ `Respondendo` e `Lembretes` são os TOTAIS, não o tamanho de `Acoes`. A lista vem cortada
/// pelo `limite`, e os dois contadores continuam dizendo quantos existem — é o que permite ao
/// cartão do dashboard escrever "6 de 23" sem uma segunda chamada.
///
/// Contar o tamanho da lista aqui daria "6 de 6" e o vendedor nunca saberia que há mais.</summary>
public record MeuDia(IReadOnlyList<AcaoDoDia> Acoes, int Respondendo, int Lembretes);

public static class JanelaDeEspera
{
    /// <summary>Quantos dias de feriado o Meu Dia carrega para descontar do tempo útil, e o
    /// limite acima do qual a espera deixa de ser medida em número.
    ///
    /// NÃO aumentar para "resolver" espera longa: carregar um ano de feriados a cada abertura da
    /// tela custa em todo carregamento para acertar um caso que quase não acontece — e conversa
    /// esperando há mais de 30 dias não precisa de precisão de minuto, precisa de atenção.</summary>
    public const int Dias = 30;
}

public interface IServicoMeuDia
{
    /// <summary>O plano do dia do vendedor logado: conversas esperando resposta + lembretes
    /// vencidos ou de hoje.
    ///
    /// NÃO existe tabela para isto — é uma LEITURA de duas fontes que já existem. Item concluído
    /// sai da lista sozinho: responder a conversa zera `aguardando_desde`; concluir o lembrete
    /// muda o status.</summary>
    /// <summary>===================== O TETO, E POR QUE ELE EXISTE =====================
    ///
    /// Antes não havia nenhum: a consulta trazia TODA conversa aberta esperando resposta e TODO
    /// lembrete pendente. O cartão do dashboard usava `.slice(0, 6)` no cliente — uma empresa com
    /// 300 conversas esperando baixava 300 para desenhar 6.
    ///
    /// O corte acontece no SQL. Os CONTADORES continuam sendo o total, por `COUNT` no banco: sem
    /// isso a tela cortaria em silêncio, que é pior que cortar.
    ///
    /// `limite` é clampado a 1..200. O dashboard pede 6; a tela Meu Dia pede o teto e avisa
    /// quando truncou.</summary>
    Task<MeuDia> MeuDiaAsync(int limite, CancellationToken ct);
}
