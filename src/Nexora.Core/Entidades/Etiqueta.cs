namespace Nexora.Core.Entidades;

/// <summary>Um rotulo livre que a empresa cola nos cards. "Revendedor", "Urgente", "Inadimplente".
///
/// ===================== VOCABULARIO UNICO, SEM TIPO =====================
/// Nao existe etiqueta "de venda" e etiqueta "de pos-venda": a mesma "Urgente" serve a qualquer
/// card, em qualquer lugar. Tipar a etiqueta obrigaria a cadastrar "Urgente" duas vezes e a
/// escolher o tipo certo na hora de aplicar — duas chances de errar para nenhum ganho.
///
/// ===================== POR QUE SO O DONO CRIA =====================
/// Mesma regra de `EtapaFunil`: e configuracao, e define o vocabulario da empresa. APLICAR a
/// etiqueta e trabalho do dia e vai ser liberado a qualquer papel — deixar o vendedor criar no
/// meio do atendimento faz nascer "Revendedor", "revenda" e "Revendedores" na mesma semana, e o
/// filtro que vem depois passa a achar so um pedaco de cada busca.
///
/// ===================== A ETIQUETA AINDA NAO COLA EM NADA =====================
/// Este bloco cria o VOCABULARIO. A tabela de ligacao vem no proximo, e ela depende de uma
/// resposta que ainda nao chegou: o card e a PESSOA ou a NEGOCIACAO?
///
/// Hoje a pergunta nao tem consequencia — `uq_conversas_contato` garante uma conversa por
/// contato, entao o card da caixa, o card do funil e a tela de contato sao a mesma linha de
/// `contatos`. Passa a ter quando existir uma tabela de oportunidades: ali o card do funil vira a
/// negociacao, e a ligacao aponta para ela.
///
/// O vocabulario serve aos dois desenhos sem mudar uma linha. Por isso ele vem primeiro, sozinho.
/// ======================================================================</summary>
public class Etiqueta : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public string Nome { get; set; } = null!;

    /// <summary>Hexadecimal de 6 digitos, validado no servico. Vai direto para o `style` do chip
    /// na tela — texto livre aqui seria deixar o dono escrever CSS para a empresa inteira.</summary>
    public string Cor { get; set; } = "#2F5D3A";

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    /// <summary>Quem criou. NULO quando nao havia sessao — semente de desenvolvimento, migracao,
    /// script. Mesmo desenho de `Lembrete.CriadoPor`, e pelo mesmo motivo: `IContextoEmpresa`
    /// devolve `UsuarioId == 0` fora de requisicao autenticada, e gravar 0 criaria FK apontando
    /// para usuario que nao existe.
    ///
    /// Nao aparece na tela hoje. Existe porque etiqueta e vocabulario da EMPRESA: quando duas
    /// pessoas com papel de dono discordarem sobre um rotulo, a pergunta vai ser "quem criou?", e
    /// ela nao tem resposta se ninguem anotou na hora.</summary>
    public long? CriadoPor { get; set; }

    public Empresa Empresa { get; set; } = null!;
    public Usuario? UsuarioCriou { get; set; }
}
