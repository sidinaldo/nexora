using System.Text.Json.Serialization;

namespace Nexora.Core.Servicos;

/// <summary>Uma linha da planilha, já lida e julgada.
///
/// `Linha` é o número COMO ESTÁ NO EXCEL — com o cabeçalho sendo a 1. Quem vai corrigir o arquivo
/// precisa achar a linha lá, e "linha 3 do corpo" não existe na tela de ninguém.</summary>
public record LinhaImportada(
    int Linha,
    string Nome,
    string Telefone,
    string? Email,
    string? Origem,
    string? Observacoes,
    // ⚠️ `"nova"`, e não `"Nova"`: ver `EnumMinusculo`. Sem isto a prévia marcava TODA linha
    // como recusada, porque a tela compara com minúscula.
    [property: JsonConverter(typeof(EnumMinusculo<SituacaoLinha>))] SituacaoLinha Situacao,
    string? Motivo);

public enum SituacaoLinha
{
    /// <summary>Entra.</summary>
    Nova,

    /// <summary>O telefone já é de um contato desta empresa. Pula — ver a decisão em
    /// <see cref="IServicoImportacao"/>.</summary>
    Repetida,

    /// <summary>Não dá para importar: sem nome, telefone inválido, ou repetida DENTRO do próprio
    /// arquivo.</summary>
    Invalida
}

/// <summary>O que a tela mostra antes de gravar, e o que ela mostra depois.
///
/// ⚠️ `Amostra` NÃO É A LISTA INTEIRA. Um arquivo de 2.000 linhas com 1.800 repetidas não pode
/// devolver 1.800 objetos para a tela desenhar — e ninguém lê 1.800 linhas de erro. O que resolve
/// é o número + as primeiras, que é como se confere planilha: olha-se algumas e confia-se na
/// contagem.</summary>
public record ResumoImportacao(
    int Total,
    int Novas,
    int Repetidas,
    int Invalidas,
    IReadOnlyList<LinhaImportada> Amostra,
    AvisoIntegracoes Aviso);

/// <summary>A caixinha "Avisar minhas integrações", decidida AQUI e só desenhada pela tela.
///
/// `Disponivel` — a empresa tem webhook ativo que assina `lead.criado`. Sem isso a caixinha não
/// aparece: oferecer um aviso que não vai a lugar nenhum é prometer o que não acontece.
///
/// `MarcadoPorPadrao` — o que ela traz já marcado. Na planilha comum, NÃO: é a base antiga do
/// cliente, e um aviso por contato dispararia a boas-vindas do sistema dele para quem já é cliente
/// há anos. No CSV do Meta Lead Ads (INT-XX), SIM: ali o lead preencheu o anúncio ontem, e a
/// automação dele é exatamente o que o dono quer que rode.</summary>
public record AvisoIntegracoes(bool Disponivel, bool MarcadoPorPadrao);

/// <summary>===================== IMPORTAR LEAD (issue #8) =====================
///
/// O cliente novo chega com a base dele numa planilha. Sem isto, ele começa do zero e espera o
/// WhatsApp encher — que é o bloqueio de adoção inteiro para uma padaria com 800 clientes.
///
/// ===================== AS TRÊS DECISÕES, E POR QUE CADA UMA =====================
///
/// 1. O CONTATO IMPORTADO NÃO ABRE NEGOCIAÇÃO, ao contrário do cadastro manual.
///    `ServicoContatos.CriarAsync` cria a negociação junto, no mesmo `SaveChanges` — e numa
///    planilha de 800 clientes isso vira 800 cards na primeira etapa do funil padrão. O quadro do
///    vendedor fica inutilizável no dia do import, e desfazer é apagar 800 linhas na mão.
///
///    Desde o E6, "contato com zero negociações" é o estado NORMAL: o lead vive na caixa até
///    alguém decidir que há negócio. Importar respeita isso. Quem quiser o contrário passa
///    `pipelineId` e assume o custo, olhando para o número na tela antes de confirmar.
///
/// 2. TELEFONE REPETIDO PULA, e não atualiza. Atualizar sobrescreveria com uma planilha velha o
///    que o vendedor escreveu no atendimento de ontem — silencioso e irreversível. O resumo diz
///    quantos e quais.
///
/// 3. DOIS PASSOS. `PreverAsync` lê e julga sem gravar nada; `ImportarAsync` grava. Import é
///    quase irreversível, e conferir antes custa um clique.
///
/// ⚠️ O ARQUIVO SOBE DUAS VEZES, uma por passo, e é de propósito: guardar o arquivo entre os dois
/// exigiria estado de servidor com dono, prazo e limpeza — para economizar um upload de 1 MB.
///
/// E UMA QUARTA, decidida depois: `lead.criado` SÓ SAI SE O DONO MARCAR. A tela Integrações
/// promete o evento "por qualquer caminho", e a importação era o único caminho mudo. Mas disparar
/// sempre mandaria a boas-vindas do sistema do cliente para 3.000 clientes antigos. Então quem
/// decide é ele, na hora, vendo quantos avisos vão sair — ver `AvisoIntegracoes`.
/// ==============================================================================</summary>
public interface IServicoImportacao
{
    /// <summary>Teto por arquivo. Acima disso a resposta é "divida a planilha".
    ///
    /// ⚠️ SÍNCRONO DE PROPÓSITO. A fila que existe (`IFilaSegundoPlano`) diz por escrito que NÃO é
    /// fila com retry — uma tentativa e acabou. Importação que falha no meio sem ninguém saber é
    /// pior que importação que não começa.</summary>
    const int MaximoLinhas = 2_000;

    /// <summary>1 MB. Acima disso já não é lista de clientes de PME — é export de outro sistema
    /// inteiro, e o teto de linhas pegaria depois de ler o arquivo todo na memória.</summary>
    const int MaximoBytes = 1024 * 1024;

    /// <summary>Lê, julga e NÃO grava. É o que a tela mostra para o dono conferir.</summary>
    Task<ResumoImportacao> PreverAsync(byte[] arquivo, CancellationToken ct);

    /// <summary>Grava as linhas novas. `pipelineId` nulo — o padrão — cria só os contatos; com
    /// funil, abre também uma negociação na primeira etapa dele. `avisarIntegracoes` enfileira um
    /// `lead.criado` por contato CRIADO — nunca pelo repetido, que não nasceu agora.</summary>
    Task<ResumoImportacao> ImportarAsync(
        byte[] arquivo, long? pipelineId, bool avisarIntegracoes, CancellationToken ct);
}
