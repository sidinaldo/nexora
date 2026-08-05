namespace Nexora.Core.Entidades;

/// <summary>Entidade que registra QUANDO foi criada. Marca separada porque nem toda tabela
/// tem `atualizado_em`: `mensagens` e um log append-only — uma mensagem enviada nao muda de
/// conteudo depois, so ganha ACK.</summary>
public interface IEntidadeCriada
{
    DateTime CriadoEm { get; set; }
}

/// <summary>Entidade cujos DOIS carimbos de tempo o InterceptorAuditoria preenche sozinho no
/// SaveChanges.
///
/// Existe porque atribuir criado_em/atualizado_em a mao em cada servico e o padrao que falha
/// em silencio: basta um caminho de escrita esquecer e a coluna passa a mentir, sem erro e
/// sem teste que pegue. O interceptor nao tem como esquecer.</summary>
public interface IEntidadeAuditada : IEntidadeCriada
{
    DateTime AtualizadoEm { get; set; }
}
