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
    IReadOnlyList<LinhaPrevia> Primeiras,
    /// <summary>A caixinha "Avisar minhas integrações", decidida aqui — ver `AvisoIntegracoes`.
    ///
    /// ⚠️ MARCADA POR PADRÃO, ao contrário da planilha comum: o lead do anúncio preencheu o
    /// formulário ontem, e a automação do cliente (boas-vindas, aviso ao vendedor) é exatamente o
    /// que ele quer que rode. A planilha comum é a base ANTIGA, e lá vem desmarcada.</summary>
    AvisoIntegracoes Aviso);

/// <summary>O que o dono escolheu na tela antes de mandar gravar.
///
/// ⚠️ `PipelineId` NULO É O PADRÃO, pela mesma razão da issue #8: 10.000 contatos no funil é um
/// quadro inutilizável, e desfazer é apagar 10.000 cards na mão. Quem quer os cards escolhe, vendo
/// o número na tela antes de confirmar.
///
/// ⚠️ `AvisarIntegracoes` NASCE FALSO no contrato, mesmo com a prévia sugerindo marcado: campo
/// ausente não pode virar 10.000 webhooks para um cliente antigo da API que não conhece o campo.
/// Quem marca é a tela, e ela manda `true` com todas as letras.</summary>
public record GravarImportacao(
    IReadOnlyList<ColunaMapeada> Mapeamento,
    long? PipelineId = null,
    long? ResponsavelId = null,
    bool AvisarIntegracoes = false);

/// <summary>O fim: quantos entraram, e em que estado a importação ficou.</summary>
public record ResultadoImportacao(
    long Id, int Total, int Importados, int Duplicados, int Invalidos,
    [property: JsonConverter(typeof(EnumMinusculo<StatusImportacao>))] StatusImportacao Status);

/// <summary>===================== IMPORTAR O CSV DO META LEAD ADS (INT-XX) =====================
///
/// O cliente que ANUNCIA tem os leads do Formulário Instantâneo do Facebook/Instagram presos no
/// Gerenciador de Leads da Meta até alguém exportar. Esta é a porta manual, sem API — de propósito:
/// mede a demanda antes de pagar o preço de OAuth, webhook `leadgen` e revisão de app.
///
/// ===================== OS PASSOS =====================
///   1. `ReceberAsync` — lê o arquivo, guarda a importação e as linhas cruas. Nada vira contato.
///   2. `PreverAsync`  — aplica um mapeamento e mostra o que vai acontecer. Nada vira contato.
///   3. `GravarAsync`  — grava: cria os novos, enriquece os repetidos e carimba cada linha.
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

    /// <summary>Grava o que a prévia mostrou — MESMO julgamento, do mesmo mapeamento.
    ///
    /// ===================== O QUE ACONTECE COM CADA LINHA =====================
    ///   · nova ....... vira contato, com `origem = meta_ads` e os `meta_*` do anúncio;
    ///   · duplicada .. NÃO vira contato, e o contato que já existia recebe os `meta_*` que
    ///     estiverem NULOS — é o lead que já tinha entrado pelo WhatsApp e agora ganha de onde
    ///     veio. Nome e observações do vendedor ficam intocados;
    ///   · inválida ... nada, e não derruba as outras.
    ///
    /// Cada linha fica com o seu resultado em `importacao_linhas`, com o número DO EXCEL: é o que
    /// responde "e a linha 4.312, o que houve com ela?" depois.
    ///
    /// ⚠️ EM LOTES, e não num `SaveChanges` só. A issue #8 grava tudo-ou-nada de propósito — lá não
    /// há registro do que entrou, então metade dentro seria irrecuperável. Aqui cada linha tem a sua
    /// linha no banco: progresso parcial é auditável, e 10.000 contatos num lote só estouraria
    /// memória e prenderia a transação por minutos.
    /// ======================================================================</summary>
    Task<ResultadoImportacao> GravarAsync(
        long importacaoId, GravarImportacao pedido, CancellationToken ct);
}
