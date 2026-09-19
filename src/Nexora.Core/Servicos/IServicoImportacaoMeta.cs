using System.Text.Json.Serialization;
using Nexora.Core.Entidades;
using Nexora.Core.LeadAds;

namespace Nexora.Core.Servicos;

/// <summary>Uma coluna do arquivo e para onde ela vai. `Coluna` é o nome COMO O CLIENTE ESCREVEU.</summary>
public record ColunaMapeada(
    string Coluna,
    // `"meta_lead_id"`, `"origem_detalhe"` — o mesmo rótulo nos dois sentidos. Ver `EnumMinusculo`.
    [property: JsonConverter(typeof(EnumMinusculo<CampoImportacao>))] CampoImportacao Campo);

/// <summary>O que o upload devolve: o arquivo foi aceito e guardado, e isto é o que foi achado nele.
/// NADA virou contato ainda.</summary>
public record ImportacaoRecebida(
    long Id,
    string NomeArquivo,
    int TotalLinhas,
    /// <summary>Uma entrada por coluna, na ordem do arquivo — as reconhecidas já ligadas, o resto
    /// em `ignorar` para o dono decidir.</summary>
    IReadOnlyList<ColunaMapeada> Mapeamento);

/// <summary>Uma linha já transformada: o que ela VAI virar se o mapeamento for confirmado.
///
/// `Telefone` é o NORMALIZADO — é para o dono ver "5584988887777" ao lado do "(84) 98888-7777" da
/// planilha e confirmar que a leitura está certa antes de gravar 10.000 linhas.</summary>
public record LinhaPrevia(
    int Linha,
    string Nome,
    string? Telefone,
    string? Email,
    string? MetaLeadId,
    DateTime? CriadoEm,
    /// <summary>O que VAI acontecer: `importado`, `duplicado` ou `invalido`.</summary>
    [property: JsonConverter(typeof(EnumMinusculo<ResultadoLinha>))] ResultadoLinha Resultado,
    string? Motivo);

public record PreviaImportacao(
    int Total, int Novos, int Duplicados, int Invalidos,
    /// <summary>⚠️ AS PRIMEIRAS 10, e não todas. O spec pede "as 10 primeiras já transformadas":
    /// é o bastante para o dono ver que a leitura está certa, e os totais acima contam o arquivo
    /// INTEIRO — que é o número que decide o clique.</summary>
    IReadOnlyList<LinhaPrevia> Primeiras);

/// <summary>===================== IMPORTAR O CSV DO META LEAD ADS (INT-XX) =====================
///
/// O cliente que ANUNCIA tem os leads do Formulário Instantâneo do Facebook/Instagram presos no
/// Gerenciador de Leads da Meta até alguém exportar. Esta é a porta manual, sem API — de propósito:
/// mede a demanda antes de pagar o preço de OAuth, webhook `leadgen` e revisão de app.
///
/// ===================== OS PASSOS =====================
///   1. `ReceberAsync` — lê o arquivo, guarda a importação e as linhas cruas. Nada vira contato.
///   2. `PreverAsync`  — aplica um mapeamento e mostra o que vai acontecer. Nada vira contato.
///   3. (commit 3)     — grava.
///
/// ⚠️ OS PASSOS 2 E 3 USAM O MESMO JULGAMENTO. É a lição da issue #8: se a prévia e a gravação
/// tivessem regras próprias, o dono aprovaria uma coisa na tela e o banco receberia outra — a forma
/// mais cara de errar, porque ele VIU e confirmou.
/// ======================================================================================</summary>
public interface IServicoImportacaoMeta
{
    /// <summary>O spec: 10.000 linhas. Acima de 500 o processamento vai para segundo plano (commit 4).</summary>
    const int MaximoLinhas = 10_000;

    /// <summary>10 MB, do spec.</summary>
    const int MaximoBytes = 10 * 1024 * 1024;

    Task<ImportacaoRecebida> ReceberAsync(string nomeArquivo, byte[] arquivo, CancellationToken ct);

    Task<PreviaImportacao> PreverAsync(
        long importacaoId, IReadOnlyList<ColunaMapeada> mapeamento, CancellationToken ct);
}
