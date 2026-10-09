using Nexora.Core.Entidades;

namespace Nexora.Core.Whatsapp;

/// <summary>===================== O QUE A META DECIDIU SOBRE UM TEMPLATE (INT-XX) =====================
///
/// A Meta responde em `APPROVED`, `REJECTED`, `PAUSED`…, e o motivo em `INVALID_FORMAT`. Aqui isso
/// vira o status do Nexora e uma frase que o dono entende.
///
/// ⚠️ UM LUGAR SO. O mesmo resultado chega por tres caminhos — o webhook da Meta, a consulta do
/// verificador de 5 minutos e o "Atualizar" da tela —, e os tres nao podem traduzir diferente.
/// ==========================================================================================</summary>
public static class RevisaoModelo
{
    /// <summary>Aplica o que a Meta disse. Devolve se mudou algo. Evento que nao mexe na
    /// disponibilidade do template (`FLAGGED`, por exemplo) nao muda nada.</summary>
    public static bool Aplicar(ModeloMensagem modelo, string? statusMeta, string? razao)
    {
        var status = StatusDe(statusMeta);
        if (status == null) return false;

        string? motivo = null;
        if (status == StatusModelo.Rejeitado) motivo = Motivo(statusMeta!, razao);

        if (modelo.Status == status && modelo.MotivoRejeicao == motivo) return false;

        modelo.Status = status.Value;
        modelo.MotivoRejeicao = motivo;
        return true;
    }

    /// <summary>O status do Nexora para o da Meta, ou nulo quando ele nao muda nada.</summary>
    public static StatusModelo? StatusDe(string? statusMeta)
    {
        var s = (statusMeta ?? "").Trim().ToUpperInvariant();

        if (s == "APPROVED" || s == "REINSTATED") return StatusModelo.Aprovado;
        if (s == "PENDING" || s == "IN_APPEAL") return StatusModelo.Enviado;
        if (s == "REJECTED" || s == "PAUSED" || s == "DISABLED" || s == "DELETED" || s == "PENDING_DELETION")
            return StatusModelo.Rejeitado;

        return null;
    }

    /// <summary>Por que o template nao pode ser enviado, em portugues e com o que fazer.</summary>
    public static string Motivo(string statusMeta, string? razao)
    {
        var s = statusMeta.Trim().ToUpperInvariant();

        if (s == "PAUSED")
            return "A Meta pausou este template por baixa qualidade (clientes bloquearam ou denunciaram). "
                 + "Ele volta sozinho se a qualidade melhorar.";
        if (s == "DISABLED")
            return "A Meta desativou este template por baixa qualidade. Crie outro, com outro texto.";
        if (s == "DELETED" || s == "PENDING_DELETION")
            return "O template foi apagado na conta da Meta.";

        var r = (razao ?? "").Trim().ToUpperInvariant();

        if (r == "INVALID_FORMAT")
            return "Formato inválido: confira as variáveis, o tamanho e os caracteres do texto.";
        if (r == "TAG_CONTENT_MISMATCH" || r == "INCORRECT_CATEGORY")
            return "A categoria não combina com o texto: oferta é marketing; aviso sobre algo que o "
                 + "cliente pediu é utilidade.";
        if (r == "PROMOTIONAL")
            return "Texto promocional numa categoria que não aceita oferta. Use a categoria marketing.";
        if (r == "ABUSIVE_CONTENT")
            return "A Meta considerou o conteúdo abusivo.";
        if (r == "SCAM")
            return "A Meta considerou o conteúdo enganoso.";
        if (r.Length == 0 || r == "NONE")
            return "A Meta recusou sem dizer o motivo.";

        return "A Meta recusou: " + razao!.Trim();
    }
}
