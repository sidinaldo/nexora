namespace Nexora.Core.Entidades;

/// <summary>Um funil inteiro, com as etapas dele. "Vendas", "Pos-venda", "Atacado".
///
/// ===================== O QUE MUDA COM ISTO =====================
/// Ate aqui a empresa tinha UM funil, e `etapas_funil` era por empresa. A padaria que vende no
/// balcao e tambem fornece para mercados tinha que espremer os dois processos nas mesmas cinco
/// colunas — ou escolher qual dos dois o sistema ia atender.
///
/// Agora as etapas pertencem a uma pipeline, e a pipeline e que pertence a empresa.
/// ===============================================================
///
/// ===================== ESTA ETAPA E INVISIVEL =====================
/// Toda empresa existente ganha exatamente UMA pipeline, chamada "Vendas", com as etapas que ela
/// ja tinha. Nenhuma tela muda, e nenhum numero muda: o que era "as etapas da empresa" passa a ser
/// "as etapas da pipeline padrao da empresa", e por enquanto sao a mesma coisa.
///
/// O menu com varias pipelines vem no bloco seguinte. Separar assim mantem a migracao de schema
/// longe da mudanca de navegacao — se algo quebrar, da para saber qual das duas foi.
/// ==================================================================</summary>
public class Pipeline : IEntidadeAuditada
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    public string Nome { get; set; } = null!;

    /// <summary>Cor da pipeline, usada no menu para diferenciar uma da outra de relance. NAO e a
    /// cor das etapas — cada etapa tem a sua, e sao coisas diferentes: a da pipeline identifica o
    /// processo, a da etapa identifica a fase dentro dele.</summary>
    public string Cor { get; set; } = "#2F5D3A";

    /// <summary>Posicao no menu. Mesma ideia de `EtapaFunil.Ordem`, e pelo mesmo motivo: quem
    /// trabalha o dia inteiro numa pipeline quer ela em cima, e ordem alfabetica nao sabe disso.
    ///
    /// ⚠️ Diferente de `etapas_funil`, aqui NAO ha indice unico sobre a ordem. Duas pipelines
    /// empatadas so trocam de lugar no menu; duas ETAPAS empatadas quebram a paginacao por cursor
    /// do quadro, que e o que `uq_etapas_ordem` existe para impedir.</summary>
    public short Ordem { get; set; }

    /// <summary>A pipeline onde o lead entra quando nada mais decide.
    ///
    /// ⚠️ Exatamente UMA por empresa, garantido por `uq_pipelines_padrao` — indice unico parcial
    /// `(empresa_id) WHERE padrao`. Mesmo desenho de `uq_etapas_ganho`, e pela mesma razao: sao
    /// perguntas com uma resposta so, e deixar o aplicativo garantir isso significa que um bug de
    /// escrita cria duas e ninguem descobre ate um lead sumir.
    ///
    /// Zero pipelines padrao e um estado possivel e RUIM — o lead nao teria onde entrar. Por isso
    /// a pipeline padrao nao pode ser apagada enquanto for a unica.</summary>
    public bool Padrao { get; set; }

    public DateTime CriadoEm { get; set; }
    public DateTime AtualizadoEm { get; set; }

    public Empresa Empresa { get; set; } = null!;
}
