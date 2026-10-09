namespace Nexora.Core.Entidades;

/// <summary>===================== UM TEMPLATE DA API OFICIAL (INT-XX) =====================
///
/// Fora da janela de 24h, a Meta so deixa sair mensagem de um template que ela APROVOU antes. Este
/// e o registro dele do nosso lado: o texto com as variaveis por NOME (`{{nome}}`), e o que a Meta
/// decidiu sobre ele.
///
/// ⚠️ DA CONTA (WABA), E NAO DO NUMERO: a Meta aprova o template para a conta inteira, e qualquer
/// numero dela pode envia-lo. `conexao_id` e por onde ele foi criado — e de qual token ele sai.
///
/// So o RASCUNHO e editavel. Depois de enviado, quem decide o texto e a revisao da Meta; mudar aqui
/// faria a thread mostrar uma coisa e o cliente receber outra.
/// ===================================================================================</summary>
public class ModeloMensagem : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }
    public long ConexaoId { get; set; }
    public string WabaId { get; set; } = null!;

    /// <summary>O nome NA META: minusculas, numeros e `_` (ver `PreenchedorModelo.NomeParaMeta`).</summary>
    public string Nome { get; set; } = null!;

    public CategoriaModelo Categoria { get; set; }

    /// <summary>O codigo de idioma da Meta (`pt_BR`).</summary>
    public string Idioma { get; set; } = "pt_BR";

    /// <summary>O texto com as variaveis por NOME. A Meta recebe a versao numerada.</summary>
    public string Corpo { get; set; } = null!;

    /// <summary>As variaveis do corpo, na ordem em que viram `{{1}}`, `{{2}}`…</summary>
    public string[] Variaveis { get; set; } = [];

    public StatusModelo Status { get; set; } = StatusModelo.Rascunho;

    /// <summary>Por que a Meta recusou, ja em portugues. Nulo quando nao recusou.</summary>
    public string? MotivoRejeicao { get; set; }

    /// <summary>O id do template na Meta: e por ele que o status volta, no webhook e na consulta.</summary>
    public string? IdMeta { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Conexao Conexao { get; set; } = null!;
}
