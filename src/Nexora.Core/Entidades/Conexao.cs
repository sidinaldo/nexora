namespace Nexora.Core.Entidades;

/// <summary>O numero de WhatsApp da empresa, via Evolution API.
///
/// FASE 1: uma conexao por empresa — a trava e o indice unico uq_conexoes_empresa, que se
/// remove numa linha. A tabela ja nasce 1:N para nao exigir migracao quando multi-numero
/// entrar.</summary>
public class Conexao : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public string Nome { get; set; } = null!;

    /// <summary>Chave de correlacao com a Evolution e UNICA GLOBALMENTE (nao por empresa).
    ///
    /// O webhook chega dizendo so o nome da instancia, e e por aqui que se descobre o tenant
    /// — antes de qualquer consulta com query filter, que fora de requisicao autenticada
    /// retornaria vazio em silencio. Por isso a busca por instance_name no webhook usa
    /// .IgnoreQueryFilters().</summary>
    public string InstanceName { get; set; } = null!;

    /// <summary>Numero real conectado, canonicalizado com DDI e so digitos (5584988887777).
    /// Preenchido pelo webhook connection.update a partir do ownerJid.</summary>
    public string? Numero { get; set; }

    /// <summary>Guarda o numero anterior quando a empresa pareia um chip diferente. O webhook
    /// e assincrono e nao ha usuario no loop para confirmar, entao nao se bloqueia a troca:
    /// grava o novo, guarda o antigo aqui, e a tela avisa depois.</summary>
    public string? NumeroAnterior { get; set; }

    public string? PerfilNome { get; set; }
    public string? PerfilFotoUrl { get; set; }

    public StatusConexao Status { get; set; } = StatusConexao.NaoCriada;
    public DateTime? StatusEm { get; set; }
    public DateTime? ConectadoEm { get; set; }
    public DateTime? DesconectadoEm { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
}
