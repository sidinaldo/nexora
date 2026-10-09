using Nexora.Core.Entidades;

namespace Nexora.Core.Whatsapp;

/// <summary>Como uma mensagem AUTOMATICA sai agora pela conexao da conversa (INT-XX): texto livre
/// (Evolution, ou API oficial com a janela de 24h aberta), o template aprovado da automacao, ou nao
/// sai — e entao `Motivo` diz por que.</summary>
public sealed class SaidaAutomatica
{
    private SaidaAutomatica(ModeloParaEnvio? modelo, long? modeloId, string? texto, string? motivo)
    {
        Modelo = modelo;
        ModeloId = modeloId;
        Texto = texto;
        Motivo = motivo;
    }

    /// <summary>O template, com os valores na ordem. Nulo = nao e template.</summary>
    public ModeloParaEnvio? Modelo { get; }
    public long? ModeloId { get; }

    /// <summary>O template JA PREENCHIDO — o que fica na thread e o cliente le.</summary>
    public string? Texto { get; }

    /// <summary>Por que nao sai. Preenchido = descartada.</summary>
    public string? Motivo { get; }

    public bool Descartada => Motivo != null;

    public static readonly SaidaAutomatica TextoLivre = new(null, null, null, null);

    public static SaidaAutomatica PorModelo(ModeloParaEnvio modelo, long modeloId, string texto) =>
        new(modelo, modeloId, texto, null);

    public static SaidaAutomatica NaoSai(string motivo) => new(null, null, null, motivo);
}

/// <summary>===================== A DECISAO DE TODA AUTOMATICA (INT-XX) =====================
///
/// Follow-up, lembrete e NPS saem depois de dias sem o cliente escrever. Na API oficial isso quase
/// sempre e janela fechada — e texto livre ali a Meta recusa (131047). Esta decisao fica num lugar
/// so, e o `EnviadorMensagem` a consulta antes de cada disparo automatico: os motores continuam sem
/// saber que existe canal.
///
/// `tipo` e o da reserva; o lembrete que nasceu automatico (follow-up) usa o template do
/// follow-up, e quem distingue e a implementacao, pela origem do lembrete.
/// ====================================================================================</summary>
public interface ISaidaDaAutomatica
{
    Task<SaidaAutomatica> DecidirAsync(Mensagem mensagem, TipoAutomacao tipo, CancellationToken ct);
}
