using Nexora.Core.Whatsapp;

namespace Nexora.Core.Servicos;

/// <summary>Resultado de responder. `Enviada=false` NAO e erro do ponto de vista do produto: a
/// mensagem foi registrada e aparece na conversa marcada como "nao chegou". Devolver 502 aqui
/// esconderia o id da mensagem recem-criada, que a tela precisa para renderizar o balao.</summary>
public record RespostaEnviada(long MensagemId, bool Enviada, string? Erro);

/// <summary>Um arquivo a caminho do WhatsApp (MID-1). `Conteudo` ja em memoria: o teto e 16 MB,
/// e streaming aqui obrigaria a decidir o destino antes de saber se o arquivo e valido.</summary>
public record ArquivoParaEnvio(byte[] Conteudo, string? NomeArquivo, string? MimeDeclarado);

public interface IServicoConversas
{
    /// <summary>O vendedor responde na conversa. Protocolo grava->dispara->confirma.
    ///
    /// Efeitos colaterais na MESMA transacao: zera aguardando_desde e nao_lidas (respondemos,
    /// ninguem mais espera) e ATRIBUI a conversa se ela nao tinha dono.</summary>
    Task<RespostaEnviada> ResponderAsync(long conversaId, string texto, CancellationToken ct);

    /// <summary>Envia imagem ou PDF. Mesmo protocolo do texto: grava a linha, depois dispara.
    ///
    /// O `MimeDeclarado` do multipart e IGNORADO na decisao — quem manda e o conteudo. Ver
    /// AssinaturaArquivo.</summary>
    Task<RespostaEnviada> EnviarMidiaAsync(
        long conversaId, ArquivoParaEnvio arquivo, string? legenda, CancellationToken ct);

    /// <summary>Envia NOTA DE VOZ (bloco 13).
    ///
    /// Separado do envio de mídia porque o formato tem regra própria: o WhatsApp só trata como
    /// nota de voz o OGG/Opus, e o navegador quase nunca grava nisso. Ver `AudioOpus`.</summary>
    Task<RespostaEnviada> EnviarAudioAsync(
        long conversaId, ArquivoParaEnvio arquivo, CancellationToken ct);

    /// <summary>Tenta de novo uma mensagem que falhou, REAPROVEITANDO a linha. Serve texto e
    /// midia — a linha ja sabe qual dos dois e.</summary>
    Task<RespostaEnviada> ReenviarAsync(long mensagemId, CancellationToken ct);

    /// <summary>O vendedor assume a conversa. 409 se ja for de OUTRO — assim ninguem "rouba" um
    /// atendimento em andamento sem querer. Reassumir a propria e no-op.</summary>
    Task AssumirAsync(long conversaId, CancellationToken ct);

    /// <summary>Devolve a conversa para "Nao atribuidas". So quem esta atendendo pode.</summary>
    Task LiberarAsync(long conversaId, CancellationToken ct);
}
