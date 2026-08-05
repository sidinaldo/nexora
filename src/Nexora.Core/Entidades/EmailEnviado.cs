namespace Nexora.Core.Entidades;

/// <summary>Registro de TODA tentativa de envio — com sucesso ou com erro.
///
/// ===================== POR QUE ISTO EXISTE =====================
/// Sem esta tabela, "o cliente diz que não recebeu o convite" é indepurável: não dá para saber se
/// o sistema tentou, se o provedor recusou, se o endereço estava errado ou se a mensagem caiu no
/// spam do destinatário. Com ela, as três primeiras hipóteses se respondem em uma consulta.
///
/// NÃO é fila. Não há coluna de tentativas nem de próxima execução, e nada relê esta tabela para
/// reenviar — é log, e o reenvio é o dono clicando de novo. Fila com retry e backoff é fase 2, se
/// o volume justificar.
/// ==============================================================</summary>
public class EmailEnviado
{
    public long Id { get; set; }

    /// <summary>NULL no "esqueci minha senha": o fluxo é público e roda sem tenant no contexto.
    /// É a segunda entidade do sistema com empresa_id anulável, pelo mesmo tipo de razão que
    /// `feriados` — existe caso legítimo sem dono.</summary>
    public long? EmpresaId { get; set; }

    /// <summary>Endereço COMPLETO. Aqui é dado operacional, não log: sem ele não dá para
    /// responder "para onde foi". Quem é mascarado é o LOG da aplicação (PoliticaLogin).</summary>
    public string Destinatario { get; set; } = null!;

    /// <summary>convite · reset · senha_alterada</summary>
    public string Tipo { get; set; } = null!;

    public string Assunto { get; set; } = null!;

    public DateTime EnviadoEm { get; set; }
    public bool Sucesso { get; set; }

    /// <summary>A mensagem do provedor quando falhou. NULL quando deu certo.</summary>
    public string? Erro { get; set; }

    public Empresa? Empresa { get; set; }
}
